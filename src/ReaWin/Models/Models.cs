namespace ReaWin.Models;

/// <summary>Facts read directly from a PE header; nothing here is executed or loaded.</summary>
public sealed record PeFileInfo(
    string FullPath,
    string RelativePath,
    long SizeBytes,
    bool IsDll,
    bool IsManaged,
    string Machine,
    string Subsystem,
    uint EntryPointRva,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> Exports,
    string? ProductName,
    string? FileDescription,
    string? CompanyName,
    string? OriginalFilename,
    string? Error)
{
    public string FileName => Path.GetFileName(FullPath);
    public string BaseName => Path.GetFileNameWithoutExtension(FullPath);
    public int Depth => RelativePath.Count(c => c == '\\' || c == '/');
}

/// <summary>One row in the binaries grid, with the ranking and the reasons behind it.</summary>
public sealed class BinaryItem
{
    public required PeFileInfo Pe { get; init; }
    public int Score { get; init; }
    public bool Recommended { get; init; }
    public bool LikelyHelper { get; init; }
    public string Kind => Pe.IsDll ? "DLL" : "EXE";
    public string Name => Pe.FileName;
    public string RelativePath => Pe.RelativePath;
    public string Subsystem => Pe.Subsystem;
    public string Machine => Pe.Machine;
    public string Managed => Pe.IsManaged ? ".NET" : "";
    public string Role { get; init; } = "";
    public string Size => Pe.SizeBytes >= 1_048_576
        ? $"{Pe.SizeBytes / 1_048_576.0:0.0} MB"
        : $"{Math.Max(1, Pe.SizeBytes / 1024)} KB";
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

public sealed record SegmentInfo(string Name, string Start, string End, long Length);

public sealed record ProcedureInfo(string Address, string Name)
{
    public bool IsImport => Address.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase);
    public bool IsEntry => Name == "entry";
    public bool IsAutoNamed => Name.StartsWith("FUN_", StringComparison.Ordinal);
    public string Library => IsImport && Name.Contains("::") ? Name[..Name.IndexOf("::", StringComparison.Ordinal)] : "";
    public string Kind => IsEntry ? "entry point" : IsImport ? "import" : IsAutoNamed ? "unnamed" : "named";
}

public sealed record StringInfo(string Address, string Value);

public sealed record OverviewResult(
    string EvidenceId,
    string Sha256,
    string Format,
    string Architecture,
    string ImageBase,
    int ProcedureCount,
    int StringCount,
    IReadOnlyList<SegmentInfo> Segments,
    IReadOnlyList<string> Limitations);

public sealed record ReaSettings(string GhidraInstallDir, string JavaHome, string ReaScript, string NodeExe);
