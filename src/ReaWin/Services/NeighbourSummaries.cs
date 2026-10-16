using System.Text;
using System.Text.RegularExpressions;
using AiSlop.LocalLlm;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>A neighbouring function to summarize: its name and address as REA returned them, and its decompiled code.</summary>
public sealed record NeighbourCandidate(string Address, string Name, string Code);

/// <summary>
/// One-line summaries of a function's callees, written by the model in batches. A batch pays for the instructions once and
/// gets one line per function, which is several times cheaper than describing each function on its own. The summaries are
/// short on purpose: they are context for another function, not descriptions to read, and they are only as good as the
/// code the model saw, so the prompt that uses them labels them as unverified.
/// </summary>
public static class NeighbourSummaries
{
    /// <summary>Most neighbours summarized for one request; the rest are listed by name only and picked up on a later request.</summary>
    public const int MaxNewPerRequest = 8;

    /// <summary>A function this small is shown to the model as code instead of being summarized.</summary>
    public const int InlineCodeChars = 350;

    /// <summary>Longest code sent for one neighbour; a longer one is cut at a line break.</summary>
    public const int MaxCodeChars = 1600;

    private const int MaxBatchTokens = 6000;
    private const int MaxSummaryChars = 300;

    internal const string SystemPrompt =
        "You write one-line summaries of decompiled Windows functions for a reverse engineer. The code is C-like pseudocode " +
        "from Ghidra. Names such as FUN_140001000, DAT_1401ac170, param_1, local_28 and iVar1 are placeholders made by the tool, " +
        "not the author's names; real names appear for library and imported functions.\n\n" +
        "Reply with exactly one line per function, in the order given, in the form: ADDRESS | summary\n" +
        "Each summary is at most 25 words: what the function does, then what it is probably for. Use \"probably\" for anything " +
        "you infer. Never invent behavior the code does not show. For compiler-generated or standard-library code say so " +
        "(for example \"C runtime: ...\"). Keep names exactly as written. Plain text, no markdown, no other lines.";

    private const string ExampleInput =
        "Summarize these functions.\n\n=== 0x140001000 FUN_140001000 ===\nvoid FUN_140001000(void)\n{\n  DAT_140050000 = FUN_140002000(0x18);\n  atexit(FUN_140003000);\n  return;\n}\n\n" +
        "=== 0x140001100 FUN_140001100 ===\nint FUN_140001100(char *param_1)\n{\n  int iVar1;\n  iVar1 = _stricmp(param_1,\"/quiet\");\n  return (uint)(iVar1 == 0);\n}\n";

    private const string ExampleReply =
        "0x140001000 | Allocates a 0x18-byte block into a global and registers an exit handler; probably startup initialization.\n" +
        "0x140001100 | Returns 1 when its string argument equals \"/quiet\" ignoring case; probably a command-line option check.";

    /// <summary>
    /// Requests that summarize <paramref name="candidates"/>, split so that no request holds more than a share of the context
    /// window (and 6,000 tokens: reading speed, not the window, is the limit on this hardware).
    /// </summary>
    public static IReadOnlyList<(LlmRequest Request, IReadOnlyList<NeighbourCandidate> Items)> BuildRequests(
        IReadOnlyList<NeighbourCandidate> candidates, int contextSize)
    {
        var fixedTokens = ContextBudget.TextTokens(SystemPrompt + ExampleInput + ExampleReply) + 40;
        var budget = Math.Max(600, Math.Min(MaxBatchTokens, (int)((contextSize - fixedTokens) * 0.6)));

        var batches = new List<(LlmRequest, IReadOnlyList<NeighbourCandidate>)>();
        var current = new List<NeighbourCandidate>();
        var used = 0;
        foreach (var candidate in candidates)
        {
            var cost = ContextBudget.CodeTokens(Shorten(candidate.Code)) + 20;
            if (current.Count > 0 && used + cost > budget)
            {
                batches.Add((Request(current), current));
                current = new List<NeighbourCandidate>();
                used = 0;
            }
            current.Add(candidate);
            used += cost;
        }
        if (current.Count > 0) batches.Add((Request(current), current));
        return batches;
    }

    private static LlmRequest Request(IReadOnlyList<NeighbourCandidate> items)
    {
        var sb = new StringBuilder("Summarize these functions.\n");
        foreach (var item in items)
            sb.Append("\n=== ").Append(item.Address).Append(' ').Append(item.Name).AppendLine(" ===").AppendLine(Shorten(item.Code));
        return new LlmRequest(
            new[]
            {
                LlmMessage.System(SystemPrompt),
                LlmMessage.User(ExampleInput),
                LlmMessage.Assistant(ExampleReply),
                LlmMessage.User(sb.ToString()),
            },
            Temperature: 0.2,
            MaxTokens: 60 * items.Count + 40,
            Extra: AiProfile.RequestExtras);
    }

    /// <summary>The code with blank edges removed, cut at a line break when longer than <see cref="MaxCodeChars"/>.</summary>
    internal static string Shorten(string code)
    {
        var text = FunctionPrompts.Normalize(code);
        if (text.Length <= MaxCodeChars) return text;
        var cut = text.LastIndexOf('\n', MaxCodeChars - 1);
        return (cut > MaxCodeChars / 2 ? text[..cut] : text[..MaxCodeChars]) + "\n/* ... cut ... */";
    }

    private static readonly Regex Line = new(@"^\s*(?<addr>0[xX][0-9A-Fa-f]+)\s*[|:]\s*(?<text>\S.*?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The summaries in a reply, by address. Only addresses that were asked for are accepted, so a line the model made up is
    /// ignored; a function with no usable line is simply absent and is tried again on a later request.
    /// </summary>
    public static Dictionary<string, string> ParseReply(string reply, IEnumerable<string> expectedAddresses)
    {
        var expected = expectedAddresses.ToDictionary(a => a.ToLowerInvariant(), a => a);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in FunctionPrompts.FormatReply(reply).Split('\n'))
        {
            if (Line.Match(raw) is not { Success: true } m) continue;
            if (!expected.TryGetValue(m.Groups["addr"].Value.ToLowerInvariant(), out var address)) continue;
            var text = m.Groups["text"].Value;
            if (text.Length > MaxSummaryChars) text = text[..MaxSummaryChars].TrimEnd() + "…";
            result.TryAdd(address, text);
        }
        return result;
    }

    /// <summary>
    /// A stored description reduced to one line for use as context: its purpose (or, in older descriptions, its summary).
    /// Null when the text has no such line.
    /// </summary>
    public static string? OneLineFrom(FunctionDescription description)
    {
        foreach (var raw in description.Text.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var key in new[] { "Purpose:", "Summary:" })
                if (line.StartsWith(key, StringComparison.Ordinal))
                {
                    var text = line[key.Length..].Trim();
                    return text.Length == 0 ? null : text.Length <= MaxSummaryChars ? text : text[..MaxSummaryChars].TrimEnd() + "…";
                }
        }
        return null;
    }
}
