using AiSlop.LocalLlm;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Everything that is specific to the model this app ships (Ternary Bonsai 2 27B, PTQ1_0) and to using it for one job:
/// describing a decompiled function. The shared package stays model-agnostic; the model's quirks live here.
/// </summary>
/// <remarks>
/// The model needs PrismML's llama.cpp build (stock llama.cpp rejects its quantization types), and it is a reasoning
/// model: left alone it thinks for thousands of tokens before it answers. A description does not need that, so each
/// request sends <c>reasoning_effort: "none"</c> (the model card's way of turning it off).
/// </remarks>
public static class AiProfile
{
    /// <summary>Shown in the UI next to AI output, so a description is never mistaken for an observation.</summary>
    public const string ModelLabel = "Ternary Bonsai 2 27B";

    /// <summary>
    /// Context window in tokens. Enough for a large decompiled function plus the instructions and the reply; the model's
    /// attention is mostly linear, so a window this size stays small in memory. Larger functions are cut (and the UI says so).
    /// </summary>
    public const int DefaultContextSize = 8192;

    /// <summary>Room kept for the description (a summary, a few bullets, the calls, what is unclear).</summary>
    public const int ReplyTokens = 700;

    /// <summary>Low temperature: the job is to read code faithfully, not to be creative.</summary>
    public const double Temperature = 0.3;

    /// <summary>Flash attention, as the model card runs it (<c>-fa on</c>).</summary>
    public static IReadOnlyList<string> ServerArguments { get; } = ["-fa", "on"];

    /// <summary>
    /// Request fields for this model: no reasoning trace, and the model card's non-thinking sampling (top_p, top_k).
    /// The card also lists a presence penalty of 1.5 for chat; it is left out because describing code means repeating
    /// the same identifiers on purpose, and a presence penalty works against that.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> RequestExtras { get; } = new Dictionary<string, object?>
    {
        ["reasoning_effort"] = "none",
        ["top_p"] = 0.8,
        ["top_k"] = 20,
    };

    /// <summary>The AI settings a first run starts with: off (AI is opt-in), with the context size this model is used with.</summary>
    public static AiSettings DefaultSettings => new() { ContextSize = DefaultContextSize };

    /// <summary>The user's AI settings with this app's missing pieces filled in.</summary>
    public static AiSettings Resolve(ReaSettings settings) => settings.Ai ?? DefaultSettings;

    /// <summary>The settings the engine runs with: the installed paths and this model's server arguments added.</summary>
    public static AiSettings ForEngine(AiSettings settings, AiInstallation files) =>
        files.Apply(settings) with { ExtraArguments = ServerArguments };

    /// <summary>
    /// The package's messages say "reinstall the app" or "run the app's setup", which fits an installed product. This app is
    /// set up with a script, so those two messages say that. Everything else passes through unchanged.
    /// </summary>
    public static string Translate(string english) => english switch
    {
        "The AI engine is not installed. Reinstall the app to add it." =>
            "The AI engine is not installed. Run scripts\\Install-Ai.ps1 to add it.",
        "The AI model is not installed. Run the app's setup again to download it." =>
            "The AI model is not installed. Run scripts\\Install-Ai.ps1 to download it (about 6 GB).",
        _ => english,
    };
}
