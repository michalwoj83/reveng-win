using System.Text;
using System.Text.RegularExpressions;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Everything the model is told about a function besides its code. The sections differ in how far they can be trusted and are
/// labeled that way in the prompt: the program card and the facts are observed (REA, Ghidra, the PE header), the call-site
/// excerpts are code as decompiled, and the notes on neighbours are themselves AI-written inferences.
/// </summary>
/// <param name="ProgramCard">The whole-program card (see <see cref="ProgramCard"/>); the same for every function.</param>
/// <param name="ProgramCardCompact">A shorter card for small context windows.</param>
/// <param name="Facts">Kind of function, callers and callees by name and address.</param>
/// <param name="CallSites">Decompiled code around the places where callers call this function; empty if none could be found.</param>
/// <param name="Notes">One line per neighbouring function: an AI summary, a library tag, or the code itself when it is tiny.</param>
/// <param name="NeighbourAddresses">Functions whose AI summaries appear in <paramref name="Notes"/>.</param>
public sealed record FunctionContext(string ProgramCard, string ProgramCardCompact, string Facts, string CallSites, string Notes,
    IReadOnlyList<string> NeighbourAddresses)
{
    /// <summary>What is stored with a description: everything but the program card, which is the same for the whole program.</summary>
    public string ForStorage => string.Join("\n\n", new[]
    {
        Facts.Length > 0 ? "Facts (observed):\n" + Facts : "",
        CallSites.Length > 0 ? "Call sites (code as decompiled):\n" + CallSites : "",
        Notes.Length > 0 ? "Notes on neighbouring functions (AI-written, unverified):\n" + Notes : "",
    }.Where(s => s.Length > 0));
}

/// <summary>Builds the text sections of a <see cref="FunctionContext"/> from what is known about a function's neighbours.</summary>
public static class FunctionContextBuilder
{
    public const int MaxCallers = 12;
    public const int MaxInternalCallees = 16;
    public const int MaxImports = 20;
    public const int MaxCallSiteCallers = 4;
    private const int ExcerptRadius = 3;
    private const int MaxSitesPerCaller = 3;
    private const int MaxExcerptChars = 700;

    /// <summary>
    /// Ghidra marks a function it matched against its library signatures with a "Library Function" comment at the top of the
    /// decompiled code. That is the one cheap, observed way to tell runtime and library code from the program's own.
    /// </summary>
    public static bool IsLibraryCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        var head = code.Length <= 600 ? code : code[..600];
        return head.Contains("Library Function", StringComparison.Ordinal);
    }

    /// <summary>A neighbour as the facts and notes show it: name and address exactly as REA returned them.</summary>
    public sealed record Neighbour(string Address, string Name, bool Library)
    {
        public string Label => $"{Name} ({Address})";
    }

    /// <summary>The observed facts: what kind of function it is, who calls it, what it calls (long lists are cut and say so).</summary>
    public static string Facts(ProcedureInfo target, bool library, bool graphKnows, IReadOnlyList<Neighbour> callers,
        IReadOnlyList<Neighbour> internalCallees, IReadOnlyList<string> importedNames)
    {
        var sb = new StringBuilder();
        sb.Append("Function: ").Append(target.Name).Append(" (").Append(target.Address).AppendLine(")");
        sb.Append("Kind: ").AppendLine(library
            ? "library or runtime function (matched by Ghidra's signature database)"
            : "not matched to a known library by Ghidra (may still be compiler-generated)");

        if (!graphKnows) sb.AppendLine("Call information: not available.");
        else
        {
            sb.AppendLine(callers.Count == 0
                ? "Called by: no direct callers: reached through a pointer, vtable or callback, or unused."
                : $"Called by {callers.Count}: {List(callers.Select(c => c.Label), MaxCallers)}");
            sb.AppendLine(internalCallees.Count == 0 && importedNames.Count == 0
                ? "Calls: nothing."
                : $"Calls {internalCallees.Count} program functions: " +
                  (internalCallees.Count == 0 ? "none" : List(internalCallees.Select(c => c.Library ? c.Label + " [library]" : c.Label), MaxInternalCallees)));
            if (importedNames.Count > 0)
                sb.AppendLine($"Calls {importedNames.Count} imported functions: {List(importedNames, MaxImports)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string List(IEnumerable<string> items, int max)
    {
        var all = items.ToList();
        return string.Join(", ", all.Take(max)) + (all.Count > max ? $", and {all.Count - max} more" : "");
    }

    /// <summary>
    /// The decompiled lines around the places where <paramref name="callerCode"/> calls <paramref name="targetName"/>: how the
    /// function is used, shown as code and not as a summary. Empty when the name does not appear (indirect or thunked calls).
    /// </summary>
    public static string CallSiteExcerpt(string callerCode, string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return "";
        var pattern = new Regex(@"(?<![\w])" + Regex.Escape(targetName) + @"(?![\w])");
        var lines = callerCode.Replace("\r\n", "\n").Split('\n');
        var hits = Enumerable.Range(0, lines.Length).Where(i => pattern.IsMatch(lines[i])).Take(MaxSitesPerCaller).ToList();
        if (hits.Count == 0) return "";

        var sb = new StringBuilder();
        var next = 0;   // first line not yet written, so overlapping excerpts merge
        foreach (var hit in hits)
        {
            var from = Math.Max(next, hit - ExcerptRadius);
            var to = Math.Min(lines.Length - 1, hit + ExcerptRadius);
            if (from > to) continue;
            if (sb.Length > 0 && from > next) sb.AppendLine("  ...");
            for (var i = from; i <= to; i++) sb.AppendLine(lines[i].TrimEnd());
            next = to + 1;
            if (sb.Length >= MaxExcerptChars) break;
        }
        var text = sb.ToString().TrimEnd();
        return text.Length <= MaxExcerptChars ? text : text[..MaxExcerptChars] + "\n  ...";
    }

    /// <summary>The call-site section: one block per caller that shows the call.</summary>
    public static string CallSites(IEnumerable<(Neighbour Caller, string Excerpt)> excerpts)
    {
        var sb = new StringBuilder();
        foreach (var (caller, excerpt) in excerpts.Where(e => e.Excerpt.Length > 0))
            sb.Append("-- in ").Append(caller.Label).AppendLine(":").AppendLine(excerpt);
        return sb.ToString().TrimEnd();
    }
}
