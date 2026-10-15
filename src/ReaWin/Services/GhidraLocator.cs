using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>Checks that a folder is an extracted Ghidra release, by file layout only (nothing is run).</summary>
public static class GhidraLocator
{
    /// <summary>
    /// Validates <paramref name="dir"/>. If it is not a Ghidra root but contains exactly one folder that is
    /// (a zip extracted into an extra wrapper folder), that inner folder is returned instead.
    /// </summary>
    public static GhidraInfo Inspect(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return new GhidraInfo("", false, "", "Not set. Select the folder of an extracted Ghidra 12.1.x release.");

        var path = dir.Trim().Trim('"').TrimEnd('\\', '/');
        var direct = Check(path);
        if (direct.IsValid || !Directory.Exists(path)) return direct;

        try
        {
            var inner = Directory.GetDirectories(path).Where(d => Check(d).IsValid).ToList();
            if (inner.Count == 1) return Check(inner[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return direct;
    }

    private static GhidraInfo Check(string path)
    {
        if (!Directory.Exists(path))
            return new GhidraInfo(path, false, "", "Folder does not exist.");

        var headless = Path.Combine(path, "support", "analyzeHeadless.bat");
        var props = Path.Combine(path, "Ghidra", "application.properties");
        if (!File.Exists(headless) || !File.Exists(props))
            return new GhidraInfo(path, false, "",
                "Not a Ghidra release folder (support\\analyzeHeadless.bat or Ghidra\\application.properties is missing). " +
                "Use the folder from the release zip, not a source clone.");

        var version = "";
        try
        {
            foreach (var line in File.ReadLines(props))
                if (line.StartsWith("application.version=", StringComparison.Ordinal))
                    version = line["application.version=".Length..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (version.Length == 0)
            return new GhidraInfo(path, true, "", "Ghidra found; version could not be read.");
        return version.StartsWith("12.1", StringComparison.Ordinal)
            ? new GhidraInfo(path, true, version, $"Ghidra {version} found.")
            : new GhidraInfo(path, true, version, $"Ghidra {version} found, but REA expects 12.1.x; analysis may be rejected.");
    }
}
