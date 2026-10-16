using AiSlop.LocalLlm;
using System.Text.Json.Serialization;

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
    [JsonIgnore] public bool IsImport => Address.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsEntry => Name == "entry";
    [JsonIgnore] public bool IsAutoNamed => Name.StartsWith("FUN_", StringComparison.Ordinal);
    [JsonIgnore] public string Library => IsImport && Name.Contains("::") ? Name[..Name.IndexOf("::", StringComparison.Ordinal)] : "";
    [JsonIgnore] public string Kind => IsEntry ? "entry point" : IsImport ? "import" : IsAutoNamed ? "unnamed" : "named";
}

/// <summary>Catalog entry for a project stored in the app's project folder; small so the list loads instantly.</summary>
public sealed record ProjectMeta
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public DateTime SavedUtc { get; init; }
    public int Functions { get; init; }
    public int Decompiled { get; init; }
    public long SizeBytes { get; init; }

    [JsonIgnore]
    public string Summary =>
        $"{Functions:N0} functions · code for {Decompiled:N0} · {SizeBytes / 1_048_576.0:0.0} MB · " +
        $"saved {SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>One function's result from a batch decompile: pseudocode, or REA's reason it failed.</summary>
public sealed record DecompiledFunction(string Address, string? Code, string? Error);

/// <summary>
/// Everything needed to show a finished analysis again without running Ghidra. Addresses and names are stored
/// exactly as REA returned them so they can still be passed back to REA.
/// </summary>
public sealed record ProjectFile
{
    public int Version { get; init; } = 1;
    public DateTime SavedUtc { get; init; } = DateTime.UtcNow;
    public string TargetPath { get; init; } = "";
    public string Root { get; init; } = "";
    public OverviewResult Overview { get; init; } = null!;
    public List<ProcedureInfo> Procedures { get; init; } = new();
    public List<StringInfo> Strings { get; init; } = new();
    /// <summary>Pseudocode by function address, as returned by REA.</summary>
    public Dictionary<string, string> Decompiled { get; init; } = new();
    /// <summary>REA's own error text for functions it could not decompile, by address.</summary>
    public Dictionary<string, string> DecompileErrors { get; init; } = new();
    /// <summary>
    /// AI descriptions by function address. These are the model's <b>inferences</b>, kept apart from everything above, which
    /// REA observed. Projects saved before this existed simply have none.
    /// </summary>
    public Dictionary<string, FunctionDescription> Descriptions { get; init; } = new();
    /// <summary>
    /// Which functions each function calls directly, as REA reported them. Kept so the AI can be told how a function fits
    /// into the program; callers are derived from it. Null in projects saved before this existed: the app recomputes it
    /// (a few seconds once Ghidra's session is up) and stores it.
    /// </summary>
    public CallGraph? CallGraph { get; init; }
    /// <summary>
    /// One-line AI summaries of functions that were shown to the model as context for another function, by address.
    /// Inferences like <see cref="Descriptions"/>, written on demand and kept so they are not paid for twice.
    /// </summary>
    public Dictionary<string, NeighbourSummary> Summaries { get; init; } = new();
}

/// <summary>
/// Direct calls between functions: the callee addresses of each function that was queried, exactly as REA's
/// <c>procedure_callees</c> returned them (imports look like <c>EXTERNAL:0x5a</c>). Only this direction is stored;
/// <c>procedure_callers</c> returns exactly its inverse, so <c>CallGraphIndex</c> derives it.
/// </summary>
public sealed record CallGraph
{
    public Dictionary<string, string[]> Callees { get; init; } = new();
}

/// <summary>A one-line summary of a function written by the local AI model as context for another function. An inference.</summary>
public sealed record NeighbourSummary(string Text, string Model, DateTime GeneratedUtc);

/// <summary>
/// A description of one function written by the local AI model: an inference from the decompiled code, not an observation.
/// Stored with the model that wrote it and how much of the code it saw, so it is never mistaken for REA output.
/// </summary>
public sealed record FunctionDescription(string Text, int CodeCharsSent, int CodeCharsTotal, string Model, DateTime GeneratedUtc)
{
    /// <summary>True when the function was longer than the model's context window and its tail was not shown to it.</summary>
    [JsonIgnore] public bool Truncated => CodeCharsSent < CodeCharsTotal;

    /// <summary>
    /// What the model was given besides the code, as text: the facts about callers and callees (observed) and the notes on
    /// neighbouring functions (themselves AI-written). Kept so the description can be judged against its context.
    /// Empty for descriptions written before context existed.
    /// </summary>
    public string Context { get; init; } = "";

    /// <summary>Addresses of the neighbouring functions whose summaries were in <see cref="Context"/>.</summary>
    public string[] NeighbourAddresses { get; init; } = [];

    /// <summary>
    /// A short human-readable name the model gave the function ("Initialize global list"), for people to scan lists and graphs.
    /// An inference, shown next to the name REA reported, never instead of it. Null for descriptions written before this existed
    /// or when the model gave none that was usable.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// One sentence for a node of the graph: what the function is for, standing on its own. An inference. When the model gave
    /// none, the first sentence of the purpose stands in; null for older descriptions.
    /// </summary>
    public string? Brief { get; init; }
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

/// <summary>
/// User settings. <paramref name="Theme"/> is "system" (follow Windows), "light" or "dark". <paramref name="Ai"/> is null
/// until the user first opens the AI settings; the app then uses <c>AiProfile.DefaultSettings</c> (AI off).
/// <paramref name="Revision"/> numbers the version of the app's defaults the file was written under, so a changed default
/// (the AI context size) can be carried over once without overriding a choice made afterwards.
/// </summary>
public sealed record ReaSettings(string GhidraInstallDir, string JavaHome, string ReaScript, string NodeExe,
    string Theme = "system", AiSettings? Ai = null, int Revision = 0);

/// <summary>A JDK found on disk. The version comes from its `release` file; java.exe is never run.</summary>
public sealed record JavaInstall(string Home, int Major, string Version, string Vendor, bool Is64Bit)
{
    /// <summary>Ghidra 12.1.x needs a 64-bit JDK 21 or newer.</summary>
    public const int MinimumMajor = 21;

    public bool IsSupported => Major >= MinimumMajor && Is64Bit;

    public string Display
    {
        get
        {
            var name = Major > 0 ? $"JDK {Version}" : "JDK (version unknown)";
            if (Vendor.Length > 0) name += $" — {Vendor}";
            if (Major is > 0 and < MinimumMajor) name += $"  [too old, needs {MinimumMajor}+]";
            else if (!Is64Bit) name += "  [32-bit, needs 64-bit]";
            else if (Major <= 0) name += "  [version unknown]";
            return $"{name}   {Home}";
        }
    }
}

/// <summary>Result of checking a folder for the layout of a Ghidra release.</summary>
public sealed record GhidraInfo(string Dir, bool IsValid, string Version, string Message);
