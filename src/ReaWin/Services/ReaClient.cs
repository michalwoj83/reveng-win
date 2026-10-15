using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>A failed REA invocation, with the exit code and the diagnostic text REA produced.</summary>
public sealed class ReaException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Runs the installed `rea` CLI as a child process and parses its JSON evidence.
/// Each call is an independent Ghidra session: import and auto-analysis are repeated.
/// </summary>
public sealed class ReaClient(ReaSettings settings)
{
    public static ReaSettings DefaultSettings()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new ReaSettings(
            GhidraInstallDir: Environment.GetEnvironmentVariable("GHIDRA_INSTALL_DIR") ?? "",
            JavaHome: Environment.GetEnvironmentVariable("JAVA_HOME") ?? "",
            ReaScript: Path.Combine(appData, "npm", "node_modules", "rea-agents", "scripts", "rea.mjs"),
            NodeExe: "node");
    }

    /// <summary>Raw evidence JSON for one REA command, e.g. ("analyze", path).</summary>
    public async Task<JsonDocument> RunJsonAsync(CancellationToken ct, params string[] args)
    {
        var text = await RunAsync(ct, args.Concat(new[] { "--format", "json" }));
        try { return JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            throw new ReaException($"REA returned output that is not JSON: {ex.Message}\n{Truncate(text)}", 0);
        }
    }

    public async Task<string> RunAsync(CancellationToken ct, IEnumerable<string> args)
    {
        if (!File.Exists(settings.ReaScript))
            throw new ReaException($"REA entry script not found: {settings.ReaScript}\nInstall with: npm install --global rea-agents", -1);

        var psi = new ProcessStartInfo(settings.NodeExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList passes each value as one argument, so paths with spaces or '&' are safe.
        psi.ArgumentList.Add(settings.ReaScript);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Only REA's own configuration is set; the rest of the environment is inherited by the child.
        if (!string.IsNullOrWhiteSpace(settings.GhidraInstallDir))
            psi.Environment["GHIDRA_INSTALL_DIR"] = settings.GhidraInstallDir;
        if (!string.IsNullOrWhiteSpace(settings.JavaHome))
            psi.Environment["JAVA_HOME"] = settings.JavaHome;
        psi.Environment["REA_ANALYSIS_PROVIDER"] = "ghidra";

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ReaException($"Could not start '{settings.NodeExe}': {ex.Message}", -1);
        }
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new ReaException(Truncate(string.IsNullOrWhiteSpace(error) ? output : error), process.ExitCode);
        return output;
    }

    public async Task<OverviewResult> AnalyzeAsync(string path, CancellationToken ct)
    {
        using var doc = await RunJsonAsync(ct, "analyze", path, "--provider", "ghidra");
        var root = doc.RootElement;
        var subject = root.GetProperty("subject");
        var n = root.GetProperty("normalized_result");
        var segments = new List<SegmentInfo>();
        if (n.TryGetProperty("segments", out var segs))
            foreach (var s in segs.EnumerateArray())
                segments.Add(new SegmentInfo(Str(s, "name"), Str(s, "start"), Str(s, "end"),
                    s.TryGetProperty("length", out var l) && l.TryGetInt64(out var len) ? len : 0));
        var limitations = new List<string>();
        if (root.TryGetProperty("limitations", out var lim) && lim.ValueKind == JsonValueKind.Array)
            limitations.AddRange(lim.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        return new OverviewResult(
            Str(root, "evidence_id"),
            subject.TryGetProperty("digest", out var d) ? Str(d, "sha256") : "",
            Str(subject, "format"), Str(subject, "architecture"), Str(n, "image_base"),
            Int(n, "procedure_count"), Int(n, "string_count"), segments, limitations);
    }

    public async Task<List<ProcedureInfo>> ListProceduresAsync(string path, CancellationToken ct)
    {
        using var doc = await RunJsonAsync(ct, "search", path, ".", "--kind", "procedures", "--mode", "regex", "--provider", "ghidra");
        return Items(doc).Select(e => new ProcedureInfo(Str(e, "address"), Str(e, "value"))).ToList();
    }

    public async Task<List<StringInfo>> ListStringsAsync(string path, CancellationToken ct)
    {
        using var doc = await RunJsonAsync(ct, "search", path, ".", "--kind", "strings", "--mode", "regex", "--provider", "ghidra");
        return Items(doc).Select(e => new StringInfo(Str(e, "address"), Str(e, "value"))).ToList();
    }

    public async Task<string> DecompileAsync(string path, string address, CancellationToken ct)
    {
        using var doc = await RunJsonAsync(ct, "decompile", path, address, "--provider", "ghidra");
        var n = doc.RootElement.GetProperty("normalized_result");
        return n.ValueKind == JsonValueKind.String ? n.GetString()! : n.GetRawText();
    }

    private static IEnumerable<JsonElement> Items(JsonDocument doc)
    {
        var n = doc.RootElement.GetProperty("normalized_result");
        return n.ValueKind == JsonValueKind.Array ? n.EnumerateArray().ToList() : new List<JsonElement>();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string Truncate(string s) => s.Length <= 4000 ? s : s[..4000] + "\n… (truncated)";
}
