using System.Text;
using AiSlop.LocalLlm;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>What the gatherer needs to know about the program; all of it already lives in the open project.</summary>
/// <param name="Procedures">Every function by address (imports included), exactly as REA listed them.</param>
/// <param name="Graph">Direct calls between functions.</param>
/// <param name="Summaries">AI one-liners written earlier; the gatherer adds the ones it writes now.</param>
/// <param name="Descriptions">Full descriptions the user already has; their purpose line is reused instead of summarizing again.</param>
/// <param name="GetCode">The decompiled code of a function (from the cache, or decompiled now); null when it is not available.</param>
/// <param name="ProgramCard">The whole-program card, full and compact.</param>
public sealed record ContextSource(
    IReadOnlyDictionary<string, ProcedureInfo> Procedures,
    CallGraphIndex Graph,
    IDictionary<string, NeighbourSummary> Summaries,
    IReadOnlyDictionary<string, FunctionDescription> Descriptions,
    Func<string, CancellationToken, Task<string?>> GetCode,
    (string Full, string Compact) ProgramCard);

/// <summary>
/// Collects what the model should know about a function besides its code, doing the AI work only when it is needed: the
/// callees that have no summary yet are summarized now, in as few requests as possible, and kept in the project, so each
/// neighbour is paid for once and the cost follows what the user actually looks at. Callers are shown as the code around their
/// calls (observed) and are not summarized here.
/// </summary>
public static class ContextGatherer
{
    /// <summary>Callees looked at for their code (library, tiny, or needs a summary); a hub with more is cut and the facts say how many.</summary>
    private const int MaxCalleeLookups = 16;

    /// <summary>
    /// Builds the context for <paramref name="target"/>. <paramref name="progress"/> gets plain-language steps for the UI.
    /// </summary>
    /// <exception cref="LlmUnavailableException">The engine failed while summarizing neighbours.</exception>
    public static async Task<(FunctionContext Context, int Generated)> GatherAsync(
        ILlmProvider llm, ProcedureInfo target, string code, ContextSource src, int contextSize,
        IProgress<string>? progress, CancellationToken ct)
    {
        var graph = src.Graph;
        var graphKnows = graph.Knows(target.Address);

        // ---- who calls it, what it calls
        var callers = graph.CallersOf(target.Address).Where(a => a != target.Address)
            .Select(a => src.Procedures.TryGetValue(a, out var p) ? p : null).OfType<ProcedureInfo>().ToList();
        var callees = graph.CalleesOf(target.Address);
        var internalCallees = callees.Where(a => a != target.Address && !IsExternal(a))
            .Select(a => src.Procedures.TryGetValue(a, out var p) ? p : null).OfType<ProcedureInfo>().Where(p => !p.IsImport).ToList();
        var imported = callees.Where(IsExternal).Select(a => src.Procedures.TryGetValue(a, out var p) ? p.Name : a)
            .Distinct(StringComparer.Ordinal).ToList();

        // ---- the callees' code decides what each one needs: nothing (library), its own code (tiny), or a summary
        progress?.Report("Reading the functions it calls…");
        var notes = new List<(ProcedureInfo Callee, string Line)>();
        var library = new HashSet<string>(StringComparer.Ordinal);
        var toSummarize = new List<NeighbourCandidate>();
        var lookups = 0;
        foreach (var callee in internalCallees)
        {
            ct.ThrowIfCancellationRequested();
            if (TryKnownSummary(callee.Address, src) is { } known)
            {
                notes.Add((callee, $"{callee.Name} ({callee.Address}): {known}"));
                continue;
            }
            if (lookups++ >= MaxCalleeLookups) break;

            var calleeCode = await src.GetCode(callee.Address, ct);
            if (calleeCode is null) continue;
            if (FunctionContextBuilder.IsLibraryCode(calleeCode))
            {
                library.Add(callee.Address);
                notes.Add((callee, $"{callee.Name} ({callee.Address}): library or runtime function (matched by Ghidra's signature database)."));
            }
            else if (FunctionPrompts.Normalize(calleeCode).Length <= NeighbourSummaries.InlineCodeChars)
                notes.Add((callee, $"{callee.Name} ({callee.Address}) is tiny, so here is its code:\n{FunctionPrompts.Normalize(calleeCode)}"));
            else if (toSummarize.Count < NeighbourSummaries.MaxNewPerRequest)
                toSummarize.Add(new NeighbourCandidate(callee.Address, callee.Name, calleeCode));
        }

        // ---- the AI part, only for what has no summary yet
        var generated = 0;
        if (toSummarize.Count > 0)
        {
            var batches = NeighbourSummaries.BuildRequests(toSummarize, contextSize);
            var done = 0;
            foreach (var (request, items) in batches)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Summarizing {toSummarize.Count - done} neighbouring function{(toSummarize.Count - done == 1 ? "" : "s")}…");
                var raw = await LlmCompletion.CollectAsync(llm.StreamCompletionAsync(request, ct), ct);
                var parsed = NeighbourSummaries.ParseReply(raw, items.Select(i => i.Address));
                foreach (var (address, text) in parsed)
                {
                    if (FunctionPrompts.LooksRunaway(text)) continue;
                    src.Summaries[address] = new NeighbourSummary(text, AiProfile.ModelLabel, DateTime.UtcNow);
                    generated++;
                }
                done += items.Count;
            }
            foreach (var item in toSummarize)
                if (src.Summaries.TryGetValue(item.Address, out var summary) && src.Procedures.TryGetValue(item.Address, out var proc))
                    notes.Add((proc, $"{item.Name} ({item.Address}): {summary.Text}"));
        }

        // ---- how callers use it: the code around the calls, observed
        progress?.Report("Looking at how it is called…");
        var siteList = new List<(FunctionContextBuilder.Neighbour, string)>();
        var neighbourAddresses = notes.Where(n => src.Summaries.ContainsKey(n.Callee.Address)).Select(n => n.Callee.Address).ToList();
        foreach (var caller in callers)
        {
            ct.ThrowIfCancellationRequested();
            if (siteList.Count >= FunctionContextBuilder.MaxCallSiteCallers) break;
            var callerCode = await src.GetCode(caller.Address, ct);
            if (callerCode is null) continue;
            var excerpt = FunctionContextBuilder.CallSiteExcerpt(callerCode, target.Name);
            if (excerpt.Length > 0)
                siteList.Add((new FunctionContextBuilder.Neighbour(caller.Address, caller.Name, FunctionContextBuilder.IsLibraryCode(callerCode)), excerpt));
        }

        var facts = FunctionContextBuilder.Facts(
            target, FunctionContextBuilder.IsLibraryCode(code), graphKnows,
            callers.Select(c => new FunctionContextBuilder.Neighbour(c.Address, c.Name, false)).ToList(),
            internalCallees.Select(c => new FunctionContextBuilder.Neighbour(c.Address, c.Name, library.Contains(c.Address))).ToList(),
            imported);

        var context = new FunctionContext(
            src.ProgramCard.Full, src.ProgramCard.Compact, facts,
            FunctionContextBuilder.CallSites(siteList),
            string.Join("\n", notes.Select(n => n.Line)),
            neighbourAddresses);
        return (context, generated);
    }

    private static bool IsExternal(string address) => address.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase);

    /// <summary>A neighbour's one line: its stored summary, or the purpose line of a full description the user already has.</summary>
    private static string? TryKnownSummary(string address, ContextSource src)
    {
        // a description the user asked for is richer than a summary written as context, so it comes first
        if (src.Descriptions.TryGetValue(address, out var description) && NeighbourSummaries.OneLineFrom(description) is { } line) return line;
        return src.Summaries.TryGetValue(address, out var summary) ? summary.Text : null;
    }
}
