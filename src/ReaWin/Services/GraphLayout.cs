namespace ReaWin.Services;

/// <summary>A rectangle in layout units (device-independent pixels at zoom 1). Services stay free of WPF types.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public bool Intersects(LayoutRect o) => X < o.Right && Right > o.X && Y < o.Bottom && Bottom > o.Y;
}

public readonly record struct LayoutPoint(double X, double Y);

/// <summary>A node with its place in the picture.</summary>
public sealed record PositionedNode(GraphNode Node, LayoutRect Bounds);

/// <summary>
/// An edge as a cubic curve from <see cref="P0"/> to <see cref="P3"/>. A <see cref="Back"/> edge goes against the left-to-right
/// flow (recursion, or a caller that is also a callee); it is bent above the nodes so it cannot be mistaken for a forward call.
/// </summary>
public sealed record PositionedEdge(string From, string To, LayoutPoint P0, LayoutPoint P1, LayoutPoint P2, LayoutPoint P3, bool Back, bool Minor = false);

public sealed record GraphLayoutResult(IReadOnlyList<PositionedNode> Nodes, IReadOnlyList<PositionedEdge> Edges, LayoutRect Bounds)
{
    public static GraphLayoutResult Empty { get; } = new([], [], new LayoutRect(0, 0, 0, 0));
}

/// <summary>
/// A layered left-to-right layout: callers on the left, the focus in the middle, callees on the right, one column per distance
/// from the focus. Inside a column nodes are ordered by the average position of their neighbours in the column nearer the focus,
/// a few sweeps, which removes most crossings; ties are broken by the model's order, so the same input always gives the same
/// picture. It is not a force-directed layout: that would be slow, would shuffle on every click and would turn a hub into a hairball.
/// </summary>
public static class GraphLayout
{
    public const double NodeWidth = 220;
    public const double NodeHeight = 64;
    public const double ImportHeight = 30;
    public const double ColumnGap = 110;
    public const double RowGap = 14;
    private const int Sweeps = 3;

    public static GraphLayoutResult Compute(GraphNeighbourhood hood)
    {
        if (hood.Nodes.Count == 0) return GraphLayoutResult.Empty;

        var columns = hood.Nodes.GroupBy(n => n.Column).ToDictionary(g => g.Key, g => g.ToList());
        var minColumn = columns.Keys.Min();
        var maxColumn = columns.Keys.Max();
        var index = hood.Nodes.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var columnOf = hood.Nodes.ToDictionary(n => n.Id, n => n.Column, StringComparer.Ordinal);

        // neighbours of each node in the column nearer the focus (toward 0): where it should sit relative to them
        var inward = hood.Nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in hood.Edges)
        {
            if (!columnOf.TryGetValue(e.From, out var cf) || !columnOf.TryGetValue(e.To, out var ct) || cf == ct) continue;
            if (Math.Abs(cf) < Math.Abs(ct)) inward[e.To].Add(e.From);
            else if (Math.Abs(ct) < Math.Abs(cf)) inward[e.From].Add(e.To);
        }

        // vertical position = slot number within the column; the model's order is the starting order
        var slot = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var list in columns.Values)
            for (var i = 0; i < list.Count; i++) slot[list[i].Id] = i;

        for (var sweep = 0; sweep < Sweeps; sweep++)
        {
            foreach (var column in columns.Keys.Where(c => c != 0).OrderBy(Math.Abs))
            {
                var list = columns[column];
                list.Sort((a, b) =>
                {
                    var ka = Barycenter(a, inward, slot);
                    var kb = Barycenter(b, inward, slot);
                    var c = ka.CompareTo(kb);
                    return c != 0 ? c : index[a.Id].CompareTo(index[b.Id]);
                });
                for (var i = 0; i < list.Count; i++) slot[list[i].Id] = i;
            }
        }

        // columns are centered on the focus' row, so the picture reads outward from the middle
        var rects = new Dictionary<string, LayoutRect>(StringComparer.Ordinal);
        foreach (var (column, list) in columns)
        {
            var total = list.Sum(HeightOf) + RowGap * (list.Count - 1);
            var y = -total / 2;
            foreach (var node in list)
            {
                var h = HeightOf(node);
                rects[node.Id] = new LayoutRect((column - minColumn) * (NodeWidth + ColumnGap), y, NodeWidth, h);
                y += h + RowGap;
            }
        }

        var positioned = hood.Nodes.Select(n => new PositionedNode(n, rects[n.Id])).ToList();
        var edges = new List<PositionedEdge>();
        foreach (var e in hood.Edges)
        {
            if (!rects.TryGetValue(e.From, out var a) || !rects.TryGetValue(e.To, out var b)) continue;
            edges.Add(Curve(e, a, b, columnOf[e.From], columnOf[e.To]));
        }

        var left = rects.Values.Min(r => r.X);
        var top = Math.Min(rects.Values.Min(r => r.Y), edges.Count == 0 ? 0 : edges.Min(e => Math.Min(e.P1.Y, e.P2.Y)));
        var right = rects.Values.Max(r => r.Right);
        var bottom = rects.Values.Max(r => r.Bottom);
        return new GraphLayoutResult(positioned, edges, new LayoutRect(left, top, right - left, bottom - top));
    }

    private static double HeightOf(GraphNode n) => n.Kind == GraphNodeKind.Import || n.Kind == GraphNodeKind.More ? ImportHeight : NodeHeight;

    private static double Barycenter(GraphNode node, Dictionary<string, List<string>> inward, Dictionary<string, double> slot)
    {
        var list = inward[node.Id];
        if (list.Count == 0) return double.MaxValue / 4;   // unconnected inward (imports, "+N more"): after the connected ones
        return list.Average(id => slot[id]);
    }

    private static PositionedEdge Curve(GraphEdge e, LayoutRect a, LayoutRect b, int fromColumn, int toColumn)
    {
        if (fromColumn < toColumn)
        {
            // forward: out of the right side, into the left side
            var p0 = new LayoutPoint(a.Right, a.Y + a.Height / 2);
            var p3 = new LayoutPoint(b.X, b.Y + b.Height / 2);
            var dx = Math.Max(30, (p3.X - p0.X) / 2);
            return new PositionedEdge(e.From, e.To, p0, new LayoutPoint(p0.X + dx, p0.Y), new LayoutPoint(p3.X - dx, p3.Y), p3, false, e.Minor);
        }
        else
        {
            // backward or within a column: from the top of one node to the top of the other, bowed upward
            var p0 = new LayoutPoint(a.X + a.Width / 2, a.Y);
            var p3 = new LayoutPoint(b.X + b.Width / 2, b.Y);
            var lift = 46 + Math.Abs(p3.X - p0.X) * 0.12;
            var top = Math.Min(p0.Y, p3.Y) - lift;
            return new PositionedEdge(e.From, e.To, p0, new LayoutPoint(p0.X, top), new LayoutPoint(p3.X, top), p3, true, e.Minor);
        }
    }
}
