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
/// Runs the installed `rea` CLI as a one-shot child process and parses its JSON evidence. Used for commands that
/// do not need a Ghidra session (doctor, managed and JavaScript inspection); native analysis goes through
/// <see cref="ReaSession"/>, which keeps one Ghidra session alive.
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
        var psi = CreateStartInfo(settings, args);

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

    /// <summary>
    /// Builds the child process for `node rea.mjs &lt;args&gt;`. Shared by one-shot calls and the MCP session so both
    /// get the same environment and argument handling.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(ReaSettings settings, IEnumerable<string> args)
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
            // The MCP session sends file paths on stdin; the default is the OEM code page, which mangles non-ASCII names.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
        return psi;
    }

    /// <summary>Reads the overview evidence envelope (`analyze` and `binary_overview` share this shape).</summary>
    internal static OverviewResult ParseOverview(JsonElement root)
    {
        var subject = root.GetProperty("subject");
        var n = root.GetProperty("normalized_result");
        var segments = new List<SegmentInfo>();
        if (n.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
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

    /// <summary>The `{address, value}` rows of a search/list evidence envelope; empty if the shape is unexpected.</summary>
    internal static List<JsonElement> Items(JsonDocument doc)
    {
        var n = doc.RootElement.GetProperty("normalized_result");
        return n.ValueKind == JsonValueKind.Array ? n.EnumerateArray().ToList() : new List<JsonElement>();
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    internal static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    internal static string Truncate(string s) => s.Length <= 4000 ? s : s[..4000] + "\n… (truncated)";
}
