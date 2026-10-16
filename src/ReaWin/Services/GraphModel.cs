using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>What a node in the call graph is. Observed from REA and Ghidra; nothing here is inferred.</summary>
public enum GraphNodeKind
{
    /// <summary>A function of the program.</summary>
    Program,
    /// <summary>A function Ghidra matched to a library signature (C runtime and the like). Hidden unless asked for.</summary>
    Library,
    /// <summary>An imported function: it has no code in this file.</summary>
    Import,
    /// <summary>Not a function: "+N more", standing for neighbours cut by the column limit. Clicking it shows more.</summary>
    More,
}

public enum GraphDirection { Both, Callers, Callees }

/// <summary>
/// What the graph shows around the focus. <see cref="MaxPerColumn"/> keeps every view readable and cheap to draw; the rest of a
/// column is one "+N more" node. A function with more than <see cref="HubDegree"/> neighbours (almost always C runtime helpers
/// that everything calls) is never expanded through, so one hub cannot drag the whole program into the picture.
/// </summary>
public sealed record GraphOptions
{
    public int Depth { get; init; } = 1;
    public GraphDirection Direction { get; init; } = GraphDirection.Both;
    public bool ShowLibrary { get; init; }
    public bool ShowImports { get; init; } = true;
    public bool ShowHubs { get; init; }
    public int MaxPerColumn { get; init; } = DefaultMaxPerColumn;
    public int HubDegree { get; init; } = DefaultHubDegree;

    /// <summary>
    /// Twelve covers nine in ten functions' direct callers and callees in full (measured: median 3, 90th percentile 12) and
    /// keeps a column short enough to read at a normal zoom.
    /// </summary>
    public const int DefaultMaxPerColumn = 12;

    /// <summary>How many more each click on "+N more" shows.</summary>
    public const int MoreStep = 25;
    public const int DefaultHubDegree = 30;
    public const int MaxDepth = 3;
}

/// <summary>One node of the neighbourhood. <paramref name="Column"/> is the signed distance from the focus: callers are negative, callees positive.</summary>
/// <param name="Address">As REA returned it (an import looks like <c>EXTERNAL:0x5a</c>); empty for a "+N more" node.</param>
/// <param name="Summary">The AI's one-line note about this function, when one exists. An inference, shown as such.</param>
/// <param name="MoreCount">For a "+N more" node, how many neighbours it stands for.</param>
public sealed record GraphNode(string Address, string Name, GraphNodeKind Kind, int Column,
    bool IsFocus = false, bool NoCallers = false, bool Hub = false, string? Summary = null, int MoreCount = 0)
{
    /// <summary>Key used in layouts and selections; unique even for several "+N more" nodes.</summary>
    public string Id => Kind == GraphNodeKind.More ? $"more:{Column}" : Address;

    /// <summary>The API name of an import (<c>CreateFileW</c>); the name itself otherwise.</summary>
    public string ShortName => Kind == GraphNodeKind.Import ? ProgramCard.ApiName(Name) : Name;
}

/// <summary>
/// A direct call from <see cref="From"/> to <see cref="To"/> (node ids). A <paramref name="Minor"/> edge is one that does not touch the
/// focus and does not link neighbouring columns in the flow direction (a caller that also calls one of the focus' callees, a call
/// against the flow). There are many of them and they cross the whole picture, so they are drawn only for the node under the pointer
/// or selected.
/// </summary>
public sealed record GraphEdge(string From, string To, bool Minor = false);

/// <summary>The part of the call graph that is drawn, and how much was left out and why (the status line says so; nothing vanishes silently).</summary>
public sealed record GraphNeighbourhood(
    IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges,
    int HiddenLibrary, int CollapsedHubs, int Overflow)
{
    public static GraphNeighbourhood Empty { get; } = new([], [], 0, 0, 0);

    public int Shown => Nodes.Count(n => n.Kind != GraphNodeKind.More);
}

/// <summary>Builds the neighbourhood of one function from the stored call graph. Pure and fast: it only follows lists.</summary>
public static class GraphModel
{
    /// <param name="isLibrary">Whether Ghidra matched the function to a library (from its decompiled code, when that is known).</param>
    /// <param name="summaryOf">The AI one-liner for a function, or null.</param>
    public static GraphNeighbourhood Build(
        string focus, CallGraphIndex graph, IReadOnlyDictionary<string, ProcedureInfo> procedures, GraphOptions options,
        Func<string, bool> isLibrary, Func<string, string?> summaryOf)
    {
        if (!procedures.TryGetValue(focus, out var focusProc)) return GraphNeighbourhood.Empty;

        var depth = Math.Clamp(options.Depth, 1, GraphOptions.MaxDepth);
        var cap = Math.Max(1, options.MaxPerColumn);
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);   // by id
        var order = new List<string>();
        var hiddenLibrary = new HashSet<string>(StringComparer.Ordinal);
        var collapsedHubs = new HashSet<string>(StringComparer.Ordinal);
        var overflow = 0;
        var moreNodes = new List<GraphNode>();

        int Degree(string a) => graph.CallersOf(a).Count(x => !IsExternal(x)) + graph.CalleesOf(a).Count(x => !IsExternal(x));

        GraphNode Make(string address, int column, bool isFocus = false)
        {
            procedures.TryGetValue(address, out var p);
            var name = p?.Name ?? address;
            if (IsExternal(address))
                return new GraphNode(address, name, GraphNodeKind.Import, column);
            var library = !isFocus && isLibrary(address);
            return new GraphNode(address, name, library ? GraphNodeKind.Library : GraphNodeKind.Program, column,
                IsFocus: isFocus,
                NoCallers: graph.Knows(address) && graph.CallersOf(address).Count == 0 && name != "entry",
                Hub: Degree(address) > options.HubDegree,
                Summary: summaryOf(address));
        }

        var focusNode = Make(focus, 0, isFocus: true) with { Kind = GraphNodeKind.Program };
        nodes[focus] = focusNode;
        order.Add(focus);

        void Walk(int sign, Func<string, IReadOnlyList<string>> next)
        {
            var frontier = new List<string> { focus };
            for (var level = 1; level <= depth && frontier.Count > 0; level++)
            {
                var column = sign * level;
                // candidates in a stable order: the ones most connected to the previous column first, then by address
                var candidates = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var from in frontier)
                    foreach (var to in next(from))
                    {
                        if (to == focus || nodes.ContainsKey(to)) continue;
                        // an import is only a leaf of the focus' own callees; further out it is noise
                        if (IsExternal(to) && (sign < 0 || level > 1 || !options.ShowImports)) continue;
                        candidates[to] = candidates.GetValueOrDefault(to) + 1;
                    }

                var kept = new List<string>();
                var more = 0;
                foreach (var (address, _) in candidates.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal))
                {
                    var external = IsExternal(address);
                    if (!external)
                    {
                        if (!options.ShowLibrary && isLibrary(address)) { hiddenLibrary.Add(address); continue; }
                        if (!options.ShowHubs && level > 1 && Degree(address) > options.HubDegree) { collapsedHubs.Add(address); continue; }
                    }
                    if (kept.Count >= cap) { more++; continue; }
                    kept.Add(address);
                }

                // programs first, imports after, so the cap cuts the less interesting ones
                foreach (var address in kept.OrderBy(a => IsExternal(a) ? 1 : 0))
                {
                    var node = Make(address, column);
                    nodes[address] = node;
                    order.Add(address);
                }
                if (more > 0)
                {
                    overflow += more;
                    moreNodes.Add(new GraphNode("", $"+{more} more", GraphNodeKind.More, column, MoreCount: more));
                }

                // hubs are shown when they are direct neighbours but never expanded: the next level grows from the others
                frontier = kept.Where(a => !IsExternal(a) && !(Degree(a) > options.HubDegree && !options.ShowHubs)).ToList();
            }
        }

        if (options.Direction != GraphDirection.Callees) Walk(-1, a => graph.CallersOf(a));
        if (options.Direction != GraphDirection.Callers) Walk(+1, a => graph.CalleesOf(a));

        // edges between nodes that are drawn
        var edges = new List<GraphEdge>();
        foreach (var id in order)
        {
            if (nodes[id].Kind == GraphNodeKind.Import) continue;
            foreach (var to in graph.CalleesOf(id))
                if (nodes.TryGetValue(to, out var target) && to != id)
                    edges.Add(new GraphEdge(id, to, Minor: !(nodes[id].IsFocus || target.IsFocus) && target.Column != nodes[id].Column + 1));
        }

        var all = order.Select(id => nodes[id]).Concat(moreNodes).ToList();
        return new GraphNeighbourhood(all, edges, hiddenLibrary.Count, collapsedHubs.Count, overflow);
    }

    private static bool IsExternal(string address) => address.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase);
}
