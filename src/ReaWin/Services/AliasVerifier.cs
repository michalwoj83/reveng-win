using System.Text.RegularExpressions;

namespace ReaWin.Services;

public enum CodeTokenKind { Identifier, Number, String, Char, Punctuation }

/// <summary>One token of C-like pseudocode and the line it was on (1-based).</summary>
public readonly record struct CodeToken(CodeTokenKind Kind, string Text, int Line);

/// <summary>The tokens of a piece of code without its comments, how many comments there were, and why it could not be read (if it could not).</summary>
public sealed record CodeTokens(IReadOnlyList<CodeToken> Tokens, int Comments, string? Error);

/// <summary>
/// A small tokenizer for the C-like pseudocode Ghidra writes. It is not a C parser: it only splits text into identifiers, numbers,
/// strings, characters and punctuation and drops comments, which is exactly what is needed to tell whether two pieces of code are
/// the same code apart from names and comments.
/// </summary>
public static class CodeTokenizer
{
    private static readonly string[] Punctuators =
    [
        "<<=", ">>=", "...", "->", "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::",
    ];

    public static CodeTokens Tokenize(string code)
    {
        var tokens = new List<CodeToken>();
        var comments = 0;
        var line = 1;
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                comments++;
                while (i < code.Length && code[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return new CodeTokens(tokens, comments, $"line {line}: a comment is never closed");
                comments++;
                line += code.AsSpan(i, end + 2 - i).Count('\n');
                i = end + 2;
                continue;
            }

            // string and character literals, with an optional prefix (L"...", u8"...")
            var prefix = 0;
            if (c is 'L' or 'u' or 'U')
            {
                var p = i + (code.AsSpan(i).StartsWith("u8", StringComparison.Ordinal) ? 2 : 1);
                if (p < code.Length && code[p] is '"' or '\'') prefix = p - i;
            }
            var q = i + prefix;
            if (q < code.Length && code[q] is '"' or '\'')
            {
                var quote = code[q];
                var j = q + 1;
                while (j < code.Length && code[j] != quote)
                {
                    if (code[j] == '\\') j++;
                    if (j < code.Length && code[j] == '\n') line++;
                    j++;
                }
                if (j >= code.Length) return new CodeTokens(tokens, comments, $"line {line}: a literal is never closed");
                tokens.Add(new CodeToken(quote == '"' ? CodeTokenKind.String : CodeTokenKind.Char, code[i..(j + 1)], line));
                i = j + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var j = i + 1;
                while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
                tokens.Add(new CodeToken(CodeTokenKind.Identifier, code[i..j], line));
                i = j;
                continue;
            }

            if (char.IsDigit(c))
            {
                var m = NumberPattern.Match(code, i);
                var text = m.Success && m.Index == i ? m.Value : c.ToString();
                tokens.Add(new CodeToken(CodeTokenKind.Number, text, line));
                i += text.Length;
                continue;
            }

            var punct = Punctuators.FirstOrDefault(p => string.CompareOrdinal(code, i, p, 0, p.Length) == 0);
            tokens.Add(new CodeToken(CodeTokenKind.Punctuation, punct ?? c.ToString(), line));
            i += (punct ?? c.ToString()).Length;
        }
        return new CodeTokens(tokens, comments, null);
    }

    private static readonly Regex NumberPattern = new(
        @"\G(0[xX][0-9A-Fa-f]+|\d+\.\d*([eE][+-]?\d+)?|\d+([eE][+-]?\d+)?)[uUlLfF]*", RegexOptions.Compiled);
}

/// <summary>The outcome of comparing an alias with the original code.</summary>
/// <param name="IsFaithful">True when the alias is the same code: every difference is a consistent renaming of allowed names or a comment.</param>
/// <param name="Reason">When not faithful, what differs, in words fit to show the user and to tell the model.</param>
/// <param name="FunctionName">The function's name in the alias (the original name if it was kept).</param>
/// <param name="Renames">Original name to new name, for every name that was changed.</param>
/// <param name="Comments">How many comments the alias has.</param>
/// <param name="Unrenamed">How many placeholder names (other than callees) were left as they were.</param>
public sealed record AliasCheck(bool IsFaithful, string Reason, string? FunctionName, IReadOnlyDictionary<string, string> Renames, int Comments, int Unrenamed)
{
    public static AliasCheck Failed(string reason, int comments = 0) =>
        new(false, reason, null, new Dictionary<string, string>(), comments, 0);
}

/// <summary>
/// Decides, token by token and without the model, whether an "alias implementation" is the same code as the original. The alias
/// may differ in comments and in names, and only in these names: the function's own name, and the placeholders Ghidra makes
/// (<c>param_1</c>, <c>local_28</c>, <c>iVar1</c>, <c>DAT_…</c>, <c>LAB_…</c>). Everything else must be identical: keywords, types,
/// operators, numbers, strings, real names such as <c>atexit</c>, and the placeholder names of other functions unless a name for
/// them was given. A name must be renamed consistently (one new name per old name, never two old names to one new name, never a
/// name that is already used) and must be a readable snake_case identifier. This is what makes "same code, same semantics" a
/// property that is checked instead of one that is asked for.
/// </summary>
public static class AliasVerifier
{
    /// <summary>What Ghidra names things it could not name: variables, parameters, labels, globals, functions.</summary>
    private static readonly Regex Placeholder = new(
        @"^(local_[0-9A-Za-z_]+|local_res[0-9A-Fa-f]+|param_\d+|[a-z]{1,4}Var\d+|[a-z]*Stack_[0-9A-Fa-f]+|unaff_\w+|extraout_\w+|" +
        @"in_(R[A-Z0-9]+|E[A-Z]+|[A-Z]{2,}\d*|stack_[0-9A-Fa-f]+)|_?DAT_[0-9A-Fa-f]+|PTR_\w+_[0-9A-Fa-f]+|LAB_[0-9A-Fa-f]+|" +
        @"(thunk_)?FUN_[0-9A-Fa-f]+|SUB_[0-9A-Fa-f]+|[su]_\w+_[0-9A-Fa-f]{6,}|switchD_[0-9A-Fa-f]+|caseD_[0-9A-Fa-f]+)$",
        RegexOptions.Compiled);

    private static readonly Regex FunctionPlaceholder = new(@"^(thunk_)?(FUN|SUB)_[0-9A-Fa-f]+$", RegexOptions.Compiled);
    private static readonly Regex Readable = new(@"^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>Words a new name may not be: C keywords and the type names Ghidra uses.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float", "for",
        "goto", "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct",
        "switch", "typedef", "union", "unsigned", "void", "volatile", "while", "bool", "true", "false", "null", "nullptr",
        "byte", "word", "dword", "qword", "uint", "ulong", "ushort", "uchar", "longlong", "ulonglong", "code", "undefined",
        "undefined1", "undefined2", "undefined3", "undefined4", "undefined5", "undefined6", "undefined7", "undefined8",
        "size_t", "wchar_t", "wchar16", "wchar32", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t",
    };

    private const int MaxNameLength = 48;

    /// <summary>True for names Ghidra generates; they are the ones that may be renamed (plus the function's own name).</summary>
    public static bool IsPlaceholder(string name) => Placeholder.IsMatch(name);

    /// <summary>
    /// The function's own name in the original: the identifier right before the first parenthesis of the signature (before the
    /// first brace). Null when the code does not look like a function definition.
    /// </summary>
    public static string? FunctionNameOf(IReadOnlyList<CodeToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "{") return null;
            if (tokens[i].Text == "(") return i > 0 && tokens[i - 1].Kind == CodeTokenKind.Identifier ? tokens[i - 1].Text : null;
        }
        return null;
    }

    /// <param name="calleeAliases">Names already chosen for functions this one calls (original name to new name): the only new names callees may get.</param>
    public static AliasCheck Verify(string original, string alias, IReadOnlyDictionary<string, string>? calleeAliases = null)
    {
        calleeAliases ??= new Dictionary<string, string>();
        var o = CodeTokenizer.Tokenize(original);
        var a = CodeTokenizer.Tokenize(alias);
        if (o.Error is not null) return AliasCheck.Failed("the original code could not be read: " + o.Error);
        if (a.Error is not null) return AliasCheck.Failed("the alias could not be read: " + a.Error, a.Comments);
        if (o.Tokens.Count == 0) return AliasCheck.Failed("there is no code");

        var ownName = FunctionNameOf(o.Tokens);
        if (ownName is null) return AliasCheck.Failed("the original does not start with a function signature, so its name cannot be found");

        // the first place the two differ in more than a name, to say where
        var forward = new Dictionary<string, string>(StringComparer.Ordinal);
        var backward = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = Math.Min(o.Tokens.Count, a.Tokens.Count);
        for (var i = 0; i < count; i++)
        {
            var t = o.Tokens[i];
            var u = a.Tokens[i];
            var where = $"near line {u.Line} of the alias";

            if (t.Kind != u.Kind || (t.Kind != CodeTokenKind.Identifier && t.Text != u.Text))
                return AliasCheck.Failed($"{where}: the original has `{t.Text}` here and the alias has `{u.Text}`; only names and comments may differ", a.Comments);

            if (t.Kind != CodeTokenKind.Identifier) continue;

            var isOwn = t.Text == ownName;
            var calleePlaceholder = !isOwn && FunctionPlaceholder.IsMatch(t.Text);
            var renamable = isOwn || (Placeholder.IsMatch(t.Text) && !calleePlaceholder);

            if (calleePlaceholder)
            {
                // another function: only the name already chosen for it may replace its placeholder
                var allowed = calleeAliases.TryGetValue(t.Text, out var chosen) ? chosen : null;
                if (u.Text != t.Text && u.Text != allowed)
                    return AliasCheck.Failed($"{where}: `{t.Text}` is another function and may not be renamed" +
                                             (allowed is null ? "" : $" (except to `{allowed}`)"), a.Comments);
            }
            else if (!renamable && u.Text != t.Text)
                return AliasCheck.Failed($"{where}: `{t.Text}` is a real name or a type and must stay as it is, but the alias has `{u.Text}`", a.Comments);

            // one new name per old name, in every place it occurs
            if (forward.TryGetValue(t.Text, out var seen))
            {
                if (seen != u.Text)
                    return AliasCheck.Failed($"{where}: `{t.Text}` is named `{seen}` elsewhere but `{u.Text}` here; a name must be changed the same way everywhere", a.Comments);
            }
            else
            {
                forward[t.Text] = u.Text;
                if (backward.TryGetValue(u.Text, out var other) && other != t.Text)
                    return AliasCheck.Failed($"{where}: `{u.Text}` is used for both `{other}` and `{t.Text}`; two variables cannot share a name", a.Comments);
                backward[u.Text] = t.Text;
            }

            // a changed name must be a readable snake_case identifier
            if (u.Text != t.Text && !calleePlaceholder)
            {
                var bad = BadName(u.Text);
                if (bad is not null) return AliasCheck.Failed($"{where}: the new name `{u.Text}` {bad}", a.Comments);
            }
        }

        if (o.Tokens.Count != a.Tokens.Count)
        {
            var extra = o.Tokens.Count > a.Tokens.Count ? o.Tokens[count] : a.Tokens[count];
            return AliasCheck.Failed(o.Tokens.Count > a.Tokens.Count
                ? $"the alias ends early: the original continues with `{extra.Text}` (line {extra.Line} of the original)"
                : $"the alias has more code than the original, starting with `{extra.Text}` (line {extra.Line} of the alias)", a.Comments);
        }

        // a new name must not be a name the code already uses for something else (a real name, or a kept placeholder)
        var kept = forward.Where(kv => kv.Key == kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (from, to) in forward.Where(kv => kv.Key != kv.Value))
            if (kept.Contains(to))
                return AliasCheck.Failed($"the new name `{to}` for `{from}` is already used for something else in the code", a.Comments);

        var renames = forward.Where(kv => kv.Key != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var functionName = forward[ownName];
        if (functionName == ownName && Placeholder.IsMatch(ownName))
            return AliasCheck.Failed($"the function is still called `{ownName}`; it needs a readable name", a.Comments);
        if (a.Comments == 0)
            return AliasCheck.Failed("the alias has no comments; it needs comments that explain what the code does", 0);

        var unrenamed = forward.Count(kv => kv.Key == kv.Value && Placeholder.IsMatch(kv.Key) && !FunctionPlaceholder.IsMatch(kv.Key));
        return new AliasCheck(true, "", functionName, renames, a.Comments, unrenamed);
    }

    /// <summary>Why a new name is not acceptable, or null when it is.</summary>
    internal static string? BadName(string name)
    {
        if (name.Length > MaxNameLength) return $"is longer than {MaxNameLength} characters";
        if (!Readable.IsMatch(name)) return "is not a snake_case identifier (lowercase letters, digits and single underscores, starting with a letter)";
        if (Reserved.Contains(name)) return "is a C keyword or type name";
        if (Placeholder.IsMatch(name)) return "looks like a placeholder, not a readable name";
        return null;
    }
}
