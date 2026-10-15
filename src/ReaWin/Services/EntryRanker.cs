using System.Text.RegularExpressions;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Ranks scanned binaries by how likely they are to be the application's main program.
/// The result is a hint with explicit reasons, not a claim: static imports cannot see
/// LoadLibrary calls, plugins, or processes launched by another program.
/// </summary>
public static partial class EntryRanker
{
    [GeneratedRegex(@"^(unins\w*|uninst\w*|setup\w*|install\w*|update\w*|updater|crash\w*|report\w*|.*helper|elevate\w*|vc_?redist.*|dotnet|createdump|squirrel|.*_proxy|.*launcher_stub|chrome_pwa_launcher)$", RegexOptions.IgnoreCase)]
    private static partial Regex HelperName();

    [GeneratedRegex(@"^(api-ms-win-.*|vcruntime.*|msvcp.*|msvcr.*|concrt.*|ucrtbase|d3dcompiler_.*|libegl|libglesv2|vk_swiftshader|vulkan-1|ffmpeg|libcef|chrome_elf|mfc\d+.*|atl\d+.*|dxcompiler|dxil)$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeLibrary();

    public static List<BinaryItem> Rank(IReadOnlyList<PeFileInfo> files, string root)
    {
        var folder = new DirectoryInfo(root).Name;
        var parsed = files.Where(f => f.Error is null).ToList();

        // Which local DLLs are statically imported, and by whom.
        var localDlls = parsed.Where(f => f.IsDll)
            .GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var importedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in parsed.Where(f => !f.IsDll))
            foreach (var import in exe.Imports.Distinct(StringComparer.OrdinalIgnoreCase))
                if (localDlls.ContainsKey(import))
                {
                    if (!importedBy.TryGetValue(import, out var list)) importedBy[import] = list = new();
                    list.Add(exe.FileName);
                }

        var exeItems = parsed.Where(f => !f.IsDll).Select(f => ScoreExe(f, folder, localDlls)).ToList();
        var topExe = exeItems.Where(i => !i.LikelyHelper).OrderByDescending(i => i.Score).FirstOrDefault();
        var topCompany = topExe?.Pe.CompanyName;

        var dllItems = parsed.Where(f => f.IsDll).Select(f => ScoreDll(f, importedBy, topCompany)).ToList();

        var failed = files.Where(f => f.Error is not null).Select(f => new BinaryItem
        {
            Pe = f,
            Score = 0,
            Role = "Unreadable PE header",
            Reasons = new[] { f.Error! },
        });

        var all = exeItems.Concat(dllItems).Concat(failed)
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Pe.Depth)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Mark exactly one recommendation, and only when the evidence is reasonably strong.
        if (topExe is { Score: >= 50 })
        {
            var index = all.FindIndex(i => ReferenceEquals(i.Pe, topExe.Pe));
            all[index] = new BinaryItem
            {
                Pe = topExe.Pe, Score = topExe.Score, LikelyHelper = false, Recommended = true,
                Role = "Recommended main program", Reasons = topExe.Reasons,
            };
        }
        return all;
    }

    private static BinaryItem ScoreExe(PeFileInfo f, string folder, Dictionary<string, PeFileInfo> localDlls)
    {
        var score = 30;
        var reasons = new List<string> { "Executable image (not a DLL)" };
        var helper = HelperName().IsMatch(f.BaseName);

        switch (f.Subsystem)
        {
            case "WindowsGui": score += 20; reasons.Add("GUI subsystem: a user-facing program"); break;
            case "WindowsCui": score += 5; reasons.Add("Console subsystem: a command-line tool or service host"); break;
        }

        score += f.Depth switch
        {
            0 => 20,
            1 => 8,
            _ => 0,
        };
        reasons.Add(f.Depth == 0 ? "Sits in the application root" : $"Nested {f.Depth} folder(s) deep");

        if (f.BaseName.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            score += 20; reasons.Add("File name matches the application folder name");
        }
        else if (!string.IsNullOrWhiteSpace(f.ProductName) &&
                 (f.ProductName!.Contains(f.BaseName, StringComparison.OrdinalIgnoreCase) ||
                  f.BaseName.Contains(f.ProductName, StringComparison.OrdinalIgnoreCase)))
        {
            score += 12; reasons.Add($"File name resembles product name \"{f.ProductName}\"");
        }

        if (!string.IsNullOrWhiteSpace(f.ProductName) || !string.IsNullOrWhiteSpace(f.FileDescription))
        {
            score += 5; reasons.Add($"Has version info: {f.FileDescription ?? f.ProductName}");
        }

        var localImports = f.Imports.Count(i => localDlls.ContainsKey(i));
        if (localImports > 0)
        {
            score += Math.Min(15, localImports * 5);
            reasons.Add($"Statically imports {localImports} DLL(s) from this folder");
        }

        if (helper)
        {
            score -= 40; reasons.Add("Name looks like an installer, updater, crash reporter or helper");
        }

        var role = helper ? "Likely helper / installer"
            : f.IsManaged ? ".NET application"
            : f.Subsystem == "WindowsGui" ? "GUI application" : "Console program";

        return new BinaryItem
        {
            Pe = f, Score = Math.Clamp(score, 0, 100), LikelyHelper = helper, Role = role, Reasons = reasons,
        };
    }

    private static BinaryItem ScoreDll(PeFileInfo f, Dictionary<string, List<string>> importedBy, string? topCompany)
    {
        var score = 20;
        var reasons = new List<string>();
        var runtime = RuntimeLibrary().IsMatch(f.BaseName);
        string role;

        if (importedBy.TryGetValue(f.FileName, out var users))
        {
            score += 35;
            var list = string.Join(", ", users.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
            reasons.Add($"Statically imported by {list}");
            role = "App library";
        }
        else
        {
            reasons.Add("Not statically imported by any program here (could be a plugin or loaded at runtime)");
            role = "Unreferenced / plugin";
        }

        if (f.Exports.Count > 0)
        {
            score += runtime ? 0 : 15;
            reasons.Add($"Exports {f.Exports.Count} named function(s)");
        }
        else reasons.Add("No named exports");

        if (topCompany is not null && string.Equals(f.CompanyName, topCompany, StringComparison.OrdinalIgnoreCase))
        {
            score += 10; reasons.Add("Same company as the main program");
        }

        if (f.IsManaged) reasons.Add(".NET assembly: use managed inspection, not native decompilation");

        if (runtime)
        {
            score -= 50; role = "Runtime / third-party library";
            reasons.Add("Name matches a common C runtime or platform library");
        }

        return new BinaryItem
        {
            Pe = f, Score = Math.Clamp(score, 0, 100), LikelyHelper = runtime, Role = role, Reasons = reasons,
        };
    }

    /// <summary>True when the folder looks like an Electron app, whose native exe is mostly Chromium.</summary>
    public static bool LooksLikeElectron(string root) =>
        File.Exists(Path.Combine(root, "resources", "app.asar")) ||
        Directory.Exists(Path.Combine(root, "resources", "app"));
}
