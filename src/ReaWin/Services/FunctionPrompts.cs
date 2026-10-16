using System.Text;
using System.Text.RegularExpressions;
using AiSlop.LocalLlm;

namespace ReaWin.Services;

/// <summary>What the model is shown: one function's name, address and decompiled code, exactly as REA returned them.</summary>
public sealed record FunctionToDescribe(string Name, string Address, string Code);

/// <summary>A request ready to send, and how much of the function it carries.</summary>
public sealed record BuiltPrompt(LlmRequest Request, int CodeCharsSent, int CodeCharsTotal)
{
    /// <summary>True when the function was too long for the context window and its tail was left out.</summary>
    public bool Truncated => CodeCharsSent < CodeCharsTotal;
}

/// <summary>
/// The prompt for "describe this function", and the clean-up of the model's reply. All of it lives here, in one file,
/// so it can be tuned and tested. Rules that matter for reverse engineering: the code is the only source of truth,
/// placeholder names are not real names, what the code does is kept apart from what it is probably for, and what
/// cannot be told from one function is said so instead of guessed.
/// </summary>
public static class FunctionPrompts
{
    internal const string SystemPrompt =
        "You help a reverse engineer understand one function of a Windows program. The input is C-like pseudocode from " +
        "Ghidra's decompiler. Names such as FUN_140001000, DAT_1401ac170, param_1, local_28, iVar1 and uVar2 are " +
        "placeholders made by the tool, not the author's names; real names appear only for library and imported " +
        "functions (for example atexit or CreateFileW).\n\n" +
        "Describe what this function does, using only what is visible in the code. Rules:\n" +
        "- Say what the code does before saying what it is probably for. State what is visible plainly; use words such as " +
        "\"probably\" or \"looks like\" for anything you infer.\n" +
        "- Never invent anything: no purposes, structure or field names, constants or behavior that the code does not show. " +
        "A callee with a placeholder name is described only by how it is used here.\n" +
        "- If something cannot be told from this function alone, say so under Unclear instead of guessing.\n" +
        "- Keep every name exactly as written in the code.\n" +
        "- Plain text only: no markdown, no # headings, no bold, no code blocks.\n\n" +
        "Reply in exactly this form:\n" +
        "Summary: one or two sentences.\n" +
        "Inputs: what the parameters seem to be used for, or none.\n" +
        "Behavior:\n" +
        "- one step per line, in order\n" +
        "Calls: each called function and what it is used for here, or none.\n" +
        "Unclear: what cannot be told from this function alone, or nothing.";

    internal const string Ask = "Describe this function.\n";

    // One worked example: small and large models alike follow a shown reply more faithfully than a list of rules.
    internal const string ExampleInput =
        "Name: FUN_1400027b0\nAddress: 0x1400027b0\n\nCode:\n" +
        "void FUN_1400027b0(void)\n\n{\n" +
        "  DAT_1401ac170 = FUN_1400e7870(0x18);\n" +
        "  *(longlong *)DAT_1401ac170 = DAT_1401ac170;\n" +
        "  *(longlong *)(DAT_1401ac170 + 8) = DAT_1401ac170;\n" +
        "  atexit(FUN_14011f590);\n" +
        "  return;\n}";

    internal const string ExampleReply =
        "Summary: Allocates a 0x18-byte block, stores it in the global DAT_1401ac170, makes the block point to itself in its " +
        "first two fields, and registers FUN_14011f590 to run at program exit.\n" +
        "Inputs: none.\n" +
        "Behavior:\n" +
        "- Calls FUN_1400e7870 with 0x18 and saves the result in DAT_1401ac170; the callee is unnamed, but it takes a size, so it is probably an allocator.\n" +
        "- Writes the block's own address at offset 0 and at offset 8, which looks like the head of an empty circular doubly linked list.\n" +
        "- Registers FUN_14011f590 with atexit.\n" +
        "Calls: FUN_1400e7870 (probably allocates memory), atexit (C runtime, registers a function to run at exit), FUN_14011f590 (the exit handler; its purpose is not visible here).\n" +
        "Unclear: What the list is used for; nothing in this function reads it.";

    /// <summary>Appended to code that did not fit, so the model knows the end is missing.</summary>
    internal const string TruncationMarker =
        "\n/* ... the rest of this function is not shown: it did not fit the context window ... */";

    /// <summary>Smallest amount of code worth sending; below this the context window is too small for the job.</summary>
    private const int MinCodeChars = 400;

    public static BuiltPrompt Build(FunctionToDescribe function, int contextSize)
    {
        var code = Normalize(function.Code);
        var header = $"Name: {function.Name}\nAddress: {function.Address}\n\nCode:\n";

        // What the window must also hold: instructions, the worked example, the framing of this request.
        var overhead = SystemPrompt.Length + Ask.Length * 2 + ExampleInput.Length + ExampleReply.Length + header.Length + TruncationMarker.Length;
        var budget = LlmContext.MaxInputChars(contextSize, overhead, AiProfile.ReplyTokens);
        if (budget < MinCodeChars)
            throw new LlmContextExceededException(
                "The AI context window is too small to describe a function. Raise the context size in Settings.",
                promptTokens: LlmContext.EstimateTokens(string.Concat(SystemPrompt, ExampleInput, ExampleReply)),
                replyTokens: AiProfile.ReplyTokens, contextSize: contextSize);

        var sent = code;
        if (code.Length > budget)
        {
            // cut at a line break so the model never sees half a statement
            var cut = code.LastIndexOf('\n', budget - 1);
            sent = (cut > MinCodeChars / 2 ? code[..cut] : code[..budget]) + TruncationMarker;
        }

        var request = new LlmRequest(
            new[]
            {
                LlmMessage.System(SystemPrompt),
                LlmMessage.User(Ask + ExampleInput),
                LlmMessage.Assistant(ExampleReply),
                LlmMessage.User(Ask + header + sent),
            },
            Temperature: AiProfile.Temperature,
            MaxTokens: AiProfile.ReplyTokens,
            Extra: AiProfile.RequestExtras);

        var sentChars = sent.EndsWith(TruncationMarker, StringComparison.Ordinal) ? sent.Length - TruncationMarker.Length : sent.Length;
        return new BuiltPrompt(request, sentChars, code.Length);
    }

    /// <summary>Line breaks as <c>\n</c> and no blank lines at the start or end (REA's pseudocode begins with empty lines); indentation is kept.</summary>
    internal static string Normalize(string code) => code.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\n').TrimEnd('\n', ' ', '\t');

    private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Fence = new(@"^\s*```[a-zA-Z]*\s*\n(?<body>.*?)\n\s*```\s*$", RegexOptions.Singleline);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline);
    private static readonly Regex BlankRuns = new(@"\n{3,}");
    // **bold** with text hugging both pairs of asterisks. A C pointer type such as "(char **)" has a space or a bracket
    // next to its asterisks and is left alone, as are identifiers with double underscores (__security_cookie).
    private static readonly Regex Bold = new(@"(?<![*\w])\*\*(?=\S)(?<text>.+?)(?<=\S)\*\*(?![*\w])");

    /// <summary>
    /// The model's reply as text for the AI panel: a reasoning trace, a wrapping code fence, markdown emphasis and
    /// headings removed (the panel shows plain text; identifiers and C pointer types are left alone), one blank line at most, trimmed. The content is not changed.
    /// </summary>
    public static string FormatReply(string reply)
    {
        var text = reply.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Think.Replace(text, "");
        // an unterminated trace (the reply was cut while thinking) is dropped too
        var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0) text = text[..open];
        if (Fence.Match(text.Trim()) is { Success: true } fenced) text = fenced.Groups["body"].Value;
        text = Heading.Replace(text, "");
        text = Bold.Replace(text, "${text}");
        text = BlankRuns.Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>A reply that went off the rails: the same line three times or more. Such a reply is never shown.</summary>
    public static bool LooksRunaway(string reply)
    {
        var lines = reply.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 8);
        return lines.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() >= 3);
    }
}
