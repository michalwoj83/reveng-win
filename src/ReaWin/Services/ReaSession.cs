using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// One long-lived `rea --mcp` server bound to a single binary. Ghidra imports and analyzes the file once, on the
/// first query; every later query (function list, strings, decompile) is answered from that session in milliseconds.
/// Requests may overlap; responses are matched by JSON-RPC id.
/// </summary>
/// <remarks>
/// Cancelling a wait never sends a cancel to REA. REA documents that cancelling an active Ghidra request discards
/// the session's database and re-imports the file on the next query, which would cost minutes. A cancelled wait
/// simply abandons the answer.
/// </remarks>
public sealed class ReaSession : IDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private long _nextId;
    private bool _disposed;

    private ReaSession(Process process, string targetPath)
    {
        _process = process;
        TargetPath = targetPath;
    }

    /// <summary>The absolute path of the binary this session analyzes.</summary>
    public string TargetPath { get; }

    /// <summary>Starts the server and opens <paramref name="targetPath"/>. Opening is cheap; analysis happens on the first query.</summary>
    public static async Task<ReaSession> OpenAsync(ReaSettings settings, string targetPath, CancellationToken ct)
    {
        var psi = ReaClient.CreateStartInfo(settings, new[] { "--mcp" });
        var process = new Process { StartInfo = psi };
        var session = new ReaSession(process, targetPath);
        process.ErrorDataReceived += session.OnStderr;
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ReaException($"Could not start '{settings.NodeExe}': {ex.Message}", -1);
        }
        process.BeginErrorReadLine();
        _ = session.ReadLoopAsync();

        try
        {
            using (await session.RequestAsync("initialize", new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new Dictionary<string, object?> { ["name"] = "rea-win", ["version"] = "1" },
            }, ct)) { }
            await session.SendAsync(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            });
            using var opened = await session.CallAsync("open_binary",
                new Dictionary<string, object?> { ["path"] = targetPath, ["provider_id"] = "ghidra" }, ct);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    // ---- typed queries ------------------------------------------------------------------------

    /// <summary>Overview evidence. The first call on a session triggers Ghidra's import and auto-analysis.</summary>
    public async Task<OverviewResult> OverviewAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("binary_overview", null, ct);
        return ReaClient.ParseOverview(doc.RootElement);
    }

    public async Task<List<ProcedureInfo>> ListProceduresAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("list_procedures", null, ct);
        return ReaClient.Items(doc)
            .Select(e => new ProcedureInfo(ReaClient.Str(e, "address"), ReaClient.Str(e, "value")))
            .ToList();
    }

    public async Task<List<StringInfo>> ListStringsAsync(CancellationToken ct)
    {
        using var doc = await CallAsync("list_strings", null, ct);
        return ReaClient.Items(doc)
            .Select(e => new StringInfo(ReaClient.Str(e, "address"), ReaClient.Str(e, "value")))
            .ToList();
    }

    /// <summary>Decompiles one function. <paramref name="address"/> is passed to REA exactly as REA returned it.</summary>
    public async Task<string> DecompileAsync(string address, CancellationToken ct)
    {
        using var doc = await CallAsync("procedure_pseudo_code",
            new Dictionary<string, object?> { ["procedure"] = address }, ct);
        var n = doc.RootElement.GetProperty("normalized_result");
        return n.ValueKind == JsonValueKind.String ? n.GetString()! : n.GetRawText();
    }

    /// <summary>
    /// Decompiles several functions in one round trip. A function that fails is reported in its own result and
    /// does not discard the others.
    /// </summary>
    public async Task<List<DecompiledFunction>> DecompileBatchAsync(IReadOnlyList<string> addresses, CancellationToken ct)
    {
        using var doc = await CallAsync("batch_decompile",
            new Dictionary<string, object?> { ["addresses"] = addresses }, ct);
        var results = new List<DecompiledFunction>(addresses.Count);
        var n = doc.RootElement.GetProperty("normalized_result");
        if (!n.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in items.EnumerateArray())
        {
            var address = ReaClient.Str(item, "address");
            if (item.TryGetProperty("pseudocode", out var code) && code.ValueKind == JsonValueKind.String)
            {
                results.Add(new DecompiledFunction(address, code.GetString(), null));
                continue;
            }
            var error = item.TryGetProperty("error", out var err) ? err.GetRawText() : item.GetRawText();
            results.Add(new DecompiledFunction(address, null, ReaClient.Truncate(error)));
        }
        return results;
    }

    /// <summary>
    /// Reads the direct callees of every address in <paramref name="addresses"/> with REA's <c>procedure_callees</c>. After
    /// Ghidra's analysis each call is a few milliseconds, so a few thousand functions take seconds when several requests are
    /// in flight (overlapping requests inside one session are fine). A function REA cannot answer for is left out of the
    /// result, so the graph says "unknown" for it instead of "calls nothing". Cancellation stops between requests.
    /// </summary>
    public async Task<CallGraph> CallGraphAsync(IReadOnlyList<string> addresses, IProgress<int>? progress, CancellationToken ct)
    {
        var callees = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        var next = -1;
        var done = 0;

        async Task WorkerAsync()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var i = Interlocked.Increment(ref next);
                if (i >= addresses.Count) return;
                var address = addresses[i];
                try
                {
                    using var doc = await CallAsync("procedure_callees",
                        new Dictionary<string, object?> { ["procedure"] = address }, ct).ConfigureAwait(false);
                    if (doc.RootElement.TryGetProperty("normalized_result", out var n) && n.ValueKind == JsonValueKind.Array)
                        callees[address] = n.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
                }
                catch (ReaException) { /* unknown for this function; the others are still useful */ }
                var count = Interlocked.Increment(ref done);
                if (count % 250 == 0) progress?.Report(count);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => WorkerAsync())).ConfigureAwait(false);
        progress?.Report(addresses.Count);
        // in the order of the addresses, so a saved graph does not depend on which request finished first
        return new CallGraph
        {
            Callees = addresses.Where(callees.ContainsKey).ToDictionary(a => a, a => callees[a], StringComparer.Ordinal),
        };
    }

    // ---- JSON-RPC plumbing --------------------------------------------------------------------

    /// <summary>Calls one REA tool and returns its evidence JSON. Failures become <see cref="ReaException"/> with REA's own text.</summary>
    private async Task<JsonDocument> CallAsync(string tool, IDictionary<string, object?>? args, CancellationToken ct)
    {
        using var response = await RequestAsync("tools/call", new Dictionary<string, object?>
        {
            ["name"] = tool,
            ["arguments"] = args ?? new Dictionary<string, object?>(),
        }, ct);

        var text = "";
        var result = response.RootElement.GetProperty("result");
        if (result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
            text = ReaClient.Str(content[0], "text");

        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new ReaException(ReaClient.Truncate(text.Length > 0 ? text : $"REA tool '{tool}' failed."), 1);

        JsonDocument inner;
        try { inner = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            throw new ReaException($"REA returned output that is not JSON: {ex.Message}\n{ReaClient.Truncate(text)}", 0);
        }

        // Errors from the analysis provider arrive as a normal result holding {"error": {...}}.
        var root = inner.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object && !root.TryGetProperty("normalized_result", out _))
        {
            inner.Dispose();
            throw new ReaException(ReaClient.Truncate(text), 1);
        }
        return inner;
    }

    /// <summary>Sends one JSON-RPC request and returns the response message, which is known to hold a `result`.</summary>
    private async Task<JsonDocument> RequestAsync(string method, IDictionary<string, object?> parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed) throw new ReaException("The REA session has been closed.", -1);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(ct);
        });

        await SendAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });

        var message = await tcs.Task.ConfigureAwait(false);
        var root = message.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var text = ReaClient.Str(error, "message") is { Length: > 0 } m ? m : error.GetRawText();
            message.Dispose();
            throw new ReaException(ReaClient.Truncate(text), 1);
        }
        if (!root.TryGetProperty("result", out _))
        {
            message.Dispose();
            throw new ReaException("REA sent a response without a result.", 1);
        }
        return message;
    }

    private async Task SendAsync(IDictionary<string, object?> message)
    {
        var json = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ReaException($"The REA server is not running. {StderrSummary()}", -1);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                // Responses have an id and no method; anything else is a server-initiated message we do not use.
                if (!doc.RootElement.TryGetProperty("method", out _) &&
                    doc.RootElement.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id) &&
                    _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(doc);
                else
                    doc.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }

        var failure = new ReaException($"The REA server stopped unexpectedly. {StderrSummary()}", -1);
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(failure);
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(e.Data);
            while (_stderrTail.Count > 8) _stderrTail.Dequeue();
        }
    }

    private string StderrSummary()
    {
        lock (_stderrTail)
            return _stderrTail.Count == 0 ? "" : "Last server output:\n" + string.Join("\n", _stderrTail);
    }

    // ---- shutdown -----------------------------------------------------------------------------

    /// <summary>
    /// Asks REA to close the target (so it removes its temporary Ghidra project), waits briefly, then ends the
    /// process tree. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                Task.Run(async () =>
                {
                    using var doc = await CallAsync("close_binary", null, cts.Token).ConfigureAwait(false);
                }).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or ReaException or OperationCanceledException) { }

        _disposed = true;
        var closed = new ReaException("The REA session has been closed.", -1);
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(closed);
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }
}
