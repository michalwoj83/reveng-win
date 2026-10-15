using Microsoft.Win32;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Finds installed JDKs without running them: reads the registry, well-known install folders,
/// JAVA_HOME and PATH, then parses each JDK's `release` file for its version.
/// </summary>
public static class JavaLocator
{
    private static readonly string[] VendorFolders =
    {
        "Eclipse Adoptium", "Eclipse Foundation", "Java", "Microsoft", "Zulu", "Amazon Corretto",
        "BellSoft", "Semeru", "IBM", "Temurin", "OpenJDK", "GraalVM",
    };

    private static readonly string[] JavaSoftKeys =
    {
        @"SOFTWARE\JavaSoft\JDK",
        @"SOFTWARE\JavaSoft\Java Development Kit",
    };

    /// <summary>All JDKs found, newest first. <paramref name="extraHomes"/> are inspected too (for example the saved setting).</summary>
    public static List<JavaInstall> Detect(params string?[] extraHomes)
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            var home = NormalizeHome(path);
            if (home is not null) homes.Add(home);
        }

        foreach (var h in extraHomes) Add(h);
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));
        foreach (var h in FromRegistry()) Add(h);
        foreach (var h in FromFolders()) Add(h);
        foreach (var h in FromPath()) Add(h);

        return homes
            .Select(Inspect)
            .Where(j => j is not null)
            .Select(j => j!)
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Home, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads one JDK folder. Returns null when it is not a JDK (no java.exe or javac.exe).</summary>
    public static JavaInstall? Inspect(string home)
    {
        var normalized = NormalizeHome(home);
        if (normalized is null) return null;

        var version = "";
        var vendor = "";
        var arch = "";
        var release = Path.Combine(normalized, "release");
        try
        {
            if (File.Exists(release))
            {
                foreach (var line in File.ReadLines(release))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var value = line[(eq + 1)..].Trim().Trim('"');
                    switch (line[..eq].Trim())
                    {
                        case "JAVA_VERSION": version = value; break;
                        case "IMPLEMENTOR": vendor = value; break;
                        case "OS_ARCH": arch = value; break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var is64 = arch.Length == 0 || !(arch is "x86" or "i586" or "i386" or "i686");
        return new JavaInstall(normalized, ParseMajor(version), version, vendor, is64);
    }

    /// <summary>"21.0.4" -> 21, "1.8.0_402" -> 8, anything unparsable -> 0.</summary>
    internal static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '+', '-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major)) return 0;
        if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var minor)) return minor;
        return major;
    }

    private static string? NormalizeHome(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
            return File.Exists(Path.Combine(full, "bin", "java.exe")) && File.Exists(Path.Combine(full, "bin", "javac.exe"))
                ? full
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static IEnumerable<string> FromRegistry()
    {
        foreach (var key in JavaSoftKeys)
        {
            using var root = Registry.LocalMachine.OpenSubKey(key);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub?.GetValue("JavaHome") is string home) yield return home;
            }
        }
    }

    private static IEnumerable<string> FromFolders()
    {
        var roots = new List<string>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            foreach (var vendor in VendorFolders)
                roots.Add(Path.Combine(pf, vendor));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(Path.Combine(profile, ".jdks"));          // IntelliJ-managed JDKs
        roots.Add(Path.Combine(profile, ".gradle", "jdks")); // Gradle toolchains

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> children;
            try { children = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children) yield return child;
        }
    }

    private static IEnumerable<string> FromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string? parent = null;
            try
            {
                if (File.Exists(Path.Combine(dir.Trim('"'), "java.exe")))
                    parent = Path.GetDirectoryName(dir.Trim('"').TrimEnd('\\', '/'));
            }
            catch (ArgumentException) { }
            if (parent is not null) yield return parent;
        }
    }
}
