using System.Text;
using System.Text.RegularExpressions;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// A short, fixed description of the whole program, built without the AI from what REA and the PE header report: what kind of
/// file it is, which libraries it imports and what those imports suggest, and a few distinctive strings. It is the same for
/// every function of a program, so it comes first in the prompt. An import is a hint, not proof that the code uses it.
/// </summary>
public static class ProgramCard
{
    // What an imported API suggests. A name table kept by this app (a heuristic), not something REA reports.
    private static readonly (string Capability, Regex Pattern)[] Capabilities =
    [
        ("file system", new(@"^(CreateFile|ReadFile|WriteFile|DeleteFile|MoveFile|CopyFile|FindFirstFile|FindNextFile|GetFileAttributes|SetFileAttributes|CreateDirectory|RemoveDirectory|GetTempPath|NtCreateFile|NtReadFile|NtWriteFile)", RegexOptions.Compiled)),
        ("registry", new(@"^(Reg[A-Z]|NtOpenKey|NtQueryValueKey|NtSetValueKey|SHGetValue|SHSetValue)", RegexOptions.Compiled)),
        ("network", new(@"^(WSA|socket$|connect$|send$|recv$|bind$|listen$|accept$|gethostby|getaddrinfo|WinHttp|Internet[A-Z]|Http[A-Z]|URLDownload|WNet)", RegexOptions.Compiled)),
        ("cryptography", new(@"^(Crypt|BCrypt|NCrypt|CertOpen|CertFind|WinVerifyTrust)", RegexOptions.Compiled)),
        ("processes and threads", new(@"^(CreateProcess|OpenProcess|TerminateProcess|CreateThread|CreateRemoteThread|ResumeThread|SuspendThread|EnumProcess|Process32|Thread32|CreateToolhelp32Snapshot|WinExec|ShellExecute|NtQuerySystemInformation|NtCreateThread)", RegexOptions.Compiled)),
        ("memory of other processes", new(@"^(VirtualAllocEx|VirtualProtectEx|WriteProcessMemory|ReadProcessMemory|NtMapViewOfSection|NtWriteVirtualMemory|NtReadVirtualMemory)", RegexOptions.Compiled)),
        ("user interface", new(@"^(CreateWindow|DialogBox|CreateDialog|GetMessage|DispatchMessage|MessageBox|DefWindowProc|RegisterClass|SetWindowText|ShowWindow|SendMessage|PostMessage|TrackPopupMenu|LoadIcon|LoadString|DrawText|BeginPaint)", RegexOptions.Compiled)),
        ("services and security", new(@"^(OpenSCManager|CreateService|StartService|ControlService|OpenService|AdjustTokenPrivileges|OpenProcessToken|LookupPrivilegeValue|ImpersonateLoggedOnUser|GetTokenInformation|SetSecurityInfo|LookupAccountSid)", RegexOptions.Compiled)),
        ("COM and OLE", new(@"^(Co[A-Z]|Ole[A-Z]|SysAllocString|VariantInit|SafeArray)", RegexOptions.Compiled)),
        ("debugger checks", new(@"^(IsDebuggerPresent|CheckRemoteDebuggerPresent|OutputDebugString|NtSetInformationThread|DebugActiveProcess)", RegexOptions.Compiled)),
    ];

    private static readonly Regex Interesting = new(
        @"(://|[A-Za-z]:\\|\\\\|HKEY_|Software\\|\.dll\b|\.exe\b|\.sys\b|%[sdxu]|error|failed|cannot|unable|password|token|license|usage:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The card as plain text. <paramref name="pe"/> adds the file's subsystem and version resources when the file could be read
    /// (header only, never run). <paramref name="compact"/> leaves out the strings and shortens the lists, for small windows.
    /// </summary>
    public static string Build(OverviewResult overview, string targetPath, IReadOnlyList<ProcedureInfo> procedures,
        IReadOnlyList<StringInfo> strings, PeFileInfo? pe, bool compact = false)
    {
        var sb = new StringBuilder();
        sb.Append("File: ").Append(Path.GetFileName(targetPath)).Append(" (").Append(overview.Format).Append(", ")
          .Append(overview.Architecture).Append(", image base ").Append(overview.ImageBase).Append("). ")
          .Append(overview.ProcedureCount.ToString("N0")).Append(" functions, ").Append(overview.StringCount.ToString("N0")).AppendLine(" strings.");

        if (pe is { Error: null })
        {
            sb.Append("Type: ").Append(Describe(pe)).AppendLine();
            var info = new List<string>();
            if (!string.IsNullOrWhiteSpace(pe.ProductName)) info.Add($"product \"{pe.ProductName}\"");
            if (!string.IsNullOrWhiteSpace(pe.FileDescription)) info.Add($"description \"{pe.FileDescription}\"");
            if (!string.IsNullOrWhiteSpace(pe.CompanyName)) info.Add($"company \"{pe.CompanyName}\"");
            if (info.Count > 0) sb.Append("Version resource: ").Append(string.Join(", ", info)).AppendLine();
        }

        var imports = procedures.Where(p => p.IsImport).ToList();
        if (imports.Count > 0)
        {
            var libraries = imports.GroupBy(p => p.Library, StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(compact ? 5 : 8)
                .Select(g => $"{g.Key} ({g.Count()})");
            sb.Append("Imports: ").Append(imports.Count).Append(" functions; most from ").AppendLine(string.Join(", ", libraries));

            var hints = new List<string>();
            foreach (var (capability, pattern) in Capabilities)
            {
                var names = imports.Select(p => ApiName(p.Name)).Where(n => pattern.IsMatch(n)).Distinct(StringComparer.Ordinal).ToList();
                if (names.Count > 0)
                    hints.Add($"{capability} ({string.Join(", ", names.Take(compact ? 3 : 5))}{(names.Count > (compact ? 3 : 5) ? ", …" : "")})");
            }
            if (hints.Count > 0)
                sb.Append("What the imports suggest (a hint from API names, not proof of use): ").AppendLine(string.Join("; ", hints));
        }

        if (!compact)
        {
            var notable = NotableStrings(strings, 12);
            if (notable.Count > 0)
            {
                sb.AppendLine("Some strings in the file:");
                foreach (var s in notable) sb.Append("- ").AppendLine(s);
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The API name of an import: <c>KERNEL32.DLL::CreateFileW</c> gives <c>CreateFileW</c>.</summary>
    internal static string ApiName(string importName)
    {
        var i = importName.LastIndexOf("::", StringComparison.Ordinal);
        return i >= 0 ? importName[(i + 2)..] : importName;
    }

    private static string Describe(PeFileInfo pe)
    {
        var kind = pe.IsDll ? "DLL" : pe.Subsystem switch
        {
            "WindowsGui" => "Windows GUI program",
            "WindowsCui" => "Windows console program",
            _ => $"program (subsystem {pe.Subsystem})",
        };
        return $"{kind}, {pe.Machine}";
    }

    /// <summary>
    /// A few strings likely to say what the program does: paths, URLs, registry keys, messages, format strings. Deterministic
    /// (best score first, then address), each cut to 90 characters; very long strings (embedded licenses) are skipped.
    /// </summary>
    internal static List<string> NotableStrings(IReadOnlyList<StringInfo> strings, int count)
    {
        return strings
            .Where(s => s.Value.Length is >= 10 and <= 200 && LetterShare(s.Value) >= 0.5)
            .Select(s => (s, score: (Interesting.IsMatch(s.Value) ? 2 : 0) + (s.Value.Contains(' ') ? 1 : 0)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.s.Address, StringComparer.Ordinal)
            .Select(x => x.s.Value.Replace("\r", " ").Replace("\n", " ").Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(count)
            .Select(v => v.Length <= 90 ? $"\"{v}\"" : $"\"{v[..90]}…\"")
            .ToList();
    }

    private static double LetterShare(string s) => s.Count(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\\' || c == '/' || c == '.' || c == ':') / (double)s.Length;
}
