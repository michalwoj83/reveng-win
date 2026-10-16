using System.Diagnostics;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>The neighbourhood shown in the Graph tab and its layout: what is included, what is left out and counted, and that it is stable.</summary>
public class GraphTests
{
    private static string A(int i) => $"0x{0x1000 + i * 0x10:x}";

    private static (CallGraphIndex Graph, Dictionary<string, ProcedureInfo> Procs) Make(Dictionary<int, int[]> calls, int count)
    {
        var graph = new CallGraph();
        for (var i = 0; i < count; i++) graph.Callees[A(i)] = calls.TryGetValue(i, out var c) ? c.Select(x => x < 0 ? $"EXTERNAL:0x{-x:x}" : A(x)).ToArray() : [];
        var procs = Enumerable.Range(0, count).ToDictionary(A, i => new ProcedureInfo(A(i), i == 0 ? "entry" : $"FUN_{i}"));
        for (var i = 1; i < 10; i++) procs[$"EXTERNAL:0x{i:x}"] = new ProcedureInfo($"EXTERNAL:0x{i:x}", $"KERNEL32.DLL::Api{i}");
        return (new CallGraphIndex(graph), procs);
    }

    private static GraphNeighbourhood Build(CallGraphIndex g, Dictionary<string, ProcedureInfo> p, int focus, GraphOptions? o = null,
        Func<string, bool>? lib = null, Func<string, string?>? summary = null) =>
        GraphModel.Build(A(focus), g, p, o ?? new GraphOptions(), lib ?? (_ => false), summary ?? (_ => null));

    // 1 -> 2, 3, import ; 4 -> 1 ; 5 -> 4 ; 2 -> 6
    private static readonly Dictionary<int, int[]> Small = new()
    {
        [1] = [2, 3, -1], [4] = [1], [5] = [4], [2] = [6],
    };

    // ---- what is included -------------------------------------------------------------------------

    [Fact]
    public void Depth_one_shows_direct_callers_and_callees_and_the_focus_sits_in_column_zero()
    {
        var (g, p) = Make(Small, 8);

        var hood = Build(g, p, 1);

        var byName = hood.Nodes.ToDictionary(n => n.Name);
        Assert.Equal(0, byName["FUN_1"].Column);
        Assert.True(byName["FUN_1"].IsFocus);
        Assert.Equal(-1, byName["FUN_4"].Column);               // a caller
        Assert.Equal(1, byName["FUN_2"].Column);                // callees
        Assert.Equal(1, byName["FUN_3"].Column);
        Assert.DoesNotContain(hood.Nodes, n => n.Name == "FUN_5" || n.Name == "FUN_6");   // two hops away
    }

    [Fact]
    public void Depth_two_adds_the_next_ring_on_each_side()
    {
        var (g, p) = Make(Small, 8);

        var hood = Build(g, p, 1, new GraphOptions { Depth = 2 });

        var byName = hood.Nodes.ToDictionary(n => n.Name);
        Assert.Equal(-2, byName["FUN_5"].Column);
        Assert.Equal(2, byName["FUN_6"].Column);
    }

    [Fact]
    public void Direction_limits_the_picture_to_one_side()
    {
        var (g, p) = Make(Small, 8);

        var callers = Build(g, p, 1, new GraphOptions { Direction = GraphDirection.Callers, Depth = 2 });
        var callees = Build(g, p, 1, new GraphOptions { Direction = GraphDirection.Callees, Depth = 2 });

        Assert.All(callers.Nodes, n => Assert.True(n.Column <= 0));
        Assert.All(callees.Nodes, n => Assert.True(n.Column >= 0));
    }

    [Fact]
    public void Edges_connect_only_nodes_that_are_drawn_and_point_from_caller_to_callee()
    {
        var (g, p) = Make(Small, 8);

        var hood = Build(g, p, 1, new GraphOptions { Depth = 2 });

        var ids = hood.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(hood.Edges, e => { Assert.Contains(e.From, ids); Assert.Contains(e.To, ids); });
        Assert.Contains(new GraphEdge(A(4), A(1)), hood.Edges);
        Assert.Contains(new GraphEdge(A(1), A(2)), hood.Edges);
        Assert.DoesNotContain(new GraphEdge(A(1), A(4)), hood.Edges);
    }

    [Fact]
    public void Imports_appear_only_as_leaves_of_the_focus_and_can_be_switched_off()
    {
        var (g, p) = Make(Small, 8);

        var on = Build(g, p, 1);
        var off = Build(g, p, 1, new GraphOptions { ShowImports = false });

        Assert.Contains(on.Nodes, n => n.Kind == GraphNodeKind.Import && n.ShortName == "Api1");
        Assert.DoesNotContain(off.Nodes, n => n.Kind == GraphNodeKind.Import);
    }

    [Fact]
    public void A_function_that_is_both_caller_and_callee_is_drawn_once()
    {
        var (g, p) = Make(new Dictionary<int, int[]> { [1] = [2], [2] = [1] }, 4);   // mutual recursion

        var hood = Build(g, p, 1);

        Assert.Single(hood.Nodes, n => n.Name == "FUN_2");
        Assert.Contains(new GraphEdge(A(1), A(2)), hood.Edges);
        Assert.Contains(new GraphEdge(A(2), A(1)), hood.Edges);      // both directions are kept
    }

    [Fact]
    public void A_function_with_no_direct_callers_is_flagged_except_entry()
    {
        var (g, p) = Make(Small, 8);

        var hood = Build(g, p, 5);       // 5 has no callers; it calls 4

        Assert.True(hood.Nodes.Single(n => n.IsFocus).NoCallers);
        Assert.False(Build(g, p, 0).Nodes.Single(n => n.IsFocus).NoCallers);   // entry is the start, not an orphan
    }

    [Fact]
    public void The_ai_one_liner_is_attached_to_the_node_that_has_one()
    {
        var (g, p) = Make(Small, 8);

        var hood = Build(g, p, 1, summary: a => a == A(2) ? "Frees the list." : null);

        Assert.Equal("Frees the list.", hood.Nodes.Single(n => n.Name == "FUN_2").Summary);
        Assert.Null(hood.Nodes.Single(n => n.Name == "FUN_3").Summary);
    }

    // ---- what is left out, and counted ------------------------------------------------------------

    [Fact]
    public void Library_functions_are_hidden_by_default_counted_and_shown_on_request()
    {
        var (g, p) = Make(Small, 8);
        bool Lib(string a) => a == A(3);

        var hidden = Build(g, p, 1, lib: Lib);
        var shown = Build(g, p, 1, new GraphOptions { ShowLibrary = true }, Lib);

        Assert.DoesNotContain(hidden.Nodes, n => n.Name == "FUN_3");
        Assert.Equal(1, hidden.HiddenLibrary);
        Assert.Equal(GraphNodeKind.Library, shown.Nodes.Single(n => n.Name == "FUN_3").Kind);
        Assert.Equal(0, shown.HiddenLibrary);
    }

    [Fact]
    public void A_hidden_library_function_is_not_walked_through()
    {
        // 1 -> 2 (library) -> 6: with the library hidden, 6 must not appear through it
        var (g, p) = Make(Small, 8);

        var hood = Build(g, p, 1, new GraphOptions { Depth = 2 }, lib: a => a == A(2));

        Assert.DoesNotContain(hood.Nodes, n => n.Name == "FUN_6");
    }

    [Fact]
    public void A_hub_that_is_a_direct_neighbour_is_shown_but_not_expanded_and_a_distant_one_is_collapsed()
    {
        // 1 calls 2 and 3; both call hub 9, which has 40 callers (so it is a hub)
        var calls = new Dictionary<int, int[]> { [1] = [2, 3], [2] = [9], [3] = [9] };
        for (var i = 10; i < 50; i++) calls[i] = [9];
        var (g, p) = Make(calls, 60);

        var depth1 = Build(g, p, 9, new GraphOptions { Direction = GraphDirection.Callees });      // focus is the hub: fine
        var viaHub = Build(g, p, 1, new GraphOptions { Depth = 2 });
        var withHubs = Build(g, p, 1, new GraphOptions { Depth = 2, ShowHubs = true });

        Assert.DoesNotContain(viaHub.Nodes, n => n.Name == "FUN_9");
        Assert.Equal(1, viaHub.CollapsedHubs);
        Assert.Contains(withHubs.Nodes, n => n.Name == "FUN_9" && n.Hub);
        Assert.Equal(0, withHubs.CollapsedHubs);
        Assert.True(depth1.Nodes.Single(n => n.IsFocus).Hub);
    }

    [Fact]
    public void A_column_is_capped_and_the_rest_is_one_more_node_with_a_count()
    {
        var calls = new Dictionary<int, int[]> { [1] = Enumerable.Range(2, 40).ToArray() };
        var (g, p) = Make(calls, 60);

        var hood = Build(g, p, 1, new GraphOptions { MaxPerColumn = 10, Direction = GraphDirection.Callees });

        Assert.Equal(10, hood.Nodes.Count(n => n.Kind == GraphNodeKind.Program && !n.IsFocus));
        var more = hood.Nodes.Single(n => n.Kind == GraphNodeKind.More);
        Assert.Equal(30, more.MoreCount);
        Assert.Equal("+30 more", more.Name);
        Assert.Equal(30, hood.Overflow);
        Assert.Equal(10 + 1, hood.Shown);
    }

    [Fact]
    public void Raising_the_cap_shows_more_and_a_function_that_is_not_in_the_graph_gives_an_empty_picture()
    {
        var calls = new Dictionary<int, int[]> { [1] = Enumerable.Range(2, 40).ToArray() };
        var (g, p) = Make(calls, 60);

        Assert.Equal(1 + 35, Build(g, p, 1, new GraphOptions { MaxPerColumn = 35, Direction = GraphDirection.Callees }).Shown);
        Assert.Empty(GraphModel.Build("0xdead", g, p, new GraphOptions(), _ => false, _ => null).Nodes);
    }

    [Fact]
    public void The_most_connected_neighbours_are_the_ones_kept_when_a_column_is_cut()
    {
        // 1 -> 2, 3 ; at depth 2, 20 is called by both, 21 and 22 by one each; room for two in the column
        var calls = new Dictionary<int, int[]> { [1] = [2, 3], [2] = [20, 21], [3] = [20, 22] };
        var (g, p) = Make(calls, 30);

        var hood = Build(g, p, 1, new GraphOptions { Depth = 2, MaxPerColumn = 2, Direction = GraphDirection.Callees });

        Assert.Contains(hood.Nodes, n => n.Name == "FUN_20" && n.Column == 2);     // two connections: first
        Assert.Contains(hood.Nodes, n => n.Name == "FUN_21");                       // then by address
        Assert.DoesNotContain(hood.Nodes, n => n.Name == "FUN_22");
        Assert.Equal(1, hood.Nodes.Single(n => n.Kind == GraphNodeKind.More).MoreCount);
    }

    [Fact]
    public void Edges_that_cross_the_picture_without_touching_the_focus_are_minor_and_the_rest_are_not()
    {
        // 5 -> 4 -> 1 -> 2 -> 6 is the chain; 4 also calls 2 (a caller of the focus calling one of its callees: it spans the focus)
        var calls = new Dictionary<int, int[]> { [1] = [2], [2] = [6], [4] = [1, 2], [5] = [4] };
        var (g, p) = Make(calls, 8);

        var hood = Build(g, p, 1, new GraphOptions { Depth = 2 });

        GraphEdge Edge(int from, int to) => hood.Edges.Single(e => e.From == A(from) && e.To == A(to));
        Assert.False(Edge(4, 1).Minor);     // touches the focus
        Assert.False(Edge(1, 2).Minor);
        Assert.False(Edge(5, 4).Minor);     // neighbouring columns, with the flow
        Assert.False(Edge(2, 6).Minor);
        Assert.True(Edge(4, 2).Minor);      // from column -1 to column +1: it would cut across the focus
    }

    [Fact]
    public void A_minor_edge_stays_in_the_layout_so_the_canvas_can_show_it_on_demand()
    {
        var calls = new Dictionary<int, int[]> { [1] = [2], [4] = [1, 2] };
        var (g, p) = Make(calls, 8);

        var layout = GraphLayout.Compute(Build(g, p, 1));

        Assert.Single(layout.Edges, e => e.Minor);
        Assert.Equal(3, layout.Edges.Count);
    }

    // ---- the layout -------------------------------------------------------------------------------

    [Fact]
    public void Columns_follow_the_distance_from_the_focus_and_nodes_in_a_column_do_not_overlap()
    {
        var (g, p) = Make(Small, 8);
        var layout = GraphLayout.Compute(Build(g, p, 1, new GraphOptions { Depth = 2 }));

        var xs = layout.Nodes.GroupBy(n => n.Node.Column).ToDictionary(c => c.Key, c => c.First().Bounds.X);
        Assert.True(xs[-2] < xs[-1] && xs[-1] < xs[0] && xs[0] < xs[1] && xs[1] < xs[2]);

        foreach (var column in layout.Nodes.GroupBy(n => n.Node.Column))
        {
            var rects = column.Select(n => n.Bounds).OrderBy(r => r.Y).ToList();
            for (var i = 1; i < rects.Count; i++) Assert.True(rects[i].Y >= rects[i - 1].Bottom, "overlap in a column");
        }
    }

    [Fact]
    public void The_focus_is_vertically_centered_and_the_bounds_hold_every_node()
    {
        var (g, p) = Make(Small, 8);
        var layout = GraphLayout.Compute(Build(g, p, 1, new GraphOptions { Depth = 2 }));

        var focus = layout.Nodes.Single(n => n.Node.IsFocus).Bounds;
        Assert.Equal(0, focus.Y + focus.Height / 2, 6);
        Assert.All(layout.Nodes, n =>
        {
            Assert.True(n.Bounds.X >= layout.Bounds.X && n.Bounds.Right <= layout.Bounds.Right);
            Assert.True(n.Bounds.Y >= layout.Bounds.Y && n.Bounds.Bottom <= layout.Bounds.Bottom);
        });
    }

    [Fact]
    public void The_same_input_gives_exactly_the_same_picture()
    {
        var (g, p) = Make(Small, 8);
        var hood = Build(g, p, 1, new GraphOptions { Depth = 2 });

        var a = GraphLayout.Compute(hood);
        var b = GraphLayout.Compute(hood);

        Assert.Equal(a.Nodes.Select(n => (n.Node.Id, n.Bounds)), b.Nodes.Select(n => (n.Node.Id, n.Bounds)));
        Assert.Equal(a.Edges.Select(e => (e.From, e.To, e.P0, e.P3)), b.Edges.Select(e => (e.From, e.To, e.P0, e.P3)));
    }

    [Fact]
    public void Nodes_are_ordered_to_sit_level_with_their_neighbours_so_edges_do_not_cross_needlessly()
    {
        // two callers of 1; each is called by its own parent: parents must be in the same order as their children
        var calls = new Dictionary<int, int[]> { [1] = [], [2] = [1], [3] = [1], [4] = [2], [5] = [3] };
        var (g, p) = Make(calls, 8);

        var layout = GraphLayout.Compute(Build(g, p, 1, new GraphOptions { Depth = 2, Direction = GraphDirection.Callers }));

        double Y(string name) => layout.Nodes.Single(n => n.Node.Name == name).Bounds.Y;
        Assert.Equal(Y("FUN_2") < Y("FUN_3"), Y("FUN_4") < Y("FUN_5"));
    }

    [Fact]
    public void A_call_against_the_flow_is_a_back_edge_bent_above_the_nodes()
    {
        var (g, p) = Make(new Dictionary<int, int[]> { [1] = [2], [2] = [1] }, 4);
        var layout = GraphLayout.Compute(Build(g, p, 1));

        var back = layout.Edges.Single(e => e.Back);
        var forward = layout.Edges.Single(e => !e.Back);

        // callers are placed first, so 2 sits left of the focus (column -1) and the focus calling it runs against the flow
        Assert.Equal(A(1), back.From);
        Assert.Equal(A(2), back.To);
        Assert.True(back.P1.Y < Math.Min(back.P0.Y, back.P3.Y));            // bowed upward
        Assert.True(forward.P0.X < forward.P3.X);
    }

    [Fact]
    public void An_empty_neighbourhood_has_an_empty_layout()
    {
        Assert.Empty(GraphLayout.Compute(GraphNeighbourhood.Empty).Nodes);
    }

    // ---- cost -------------------------------------------------------------------------------------

    [Fact]
    public void A_program_sized_graph_with_hubs_builds_and_lays_out_a_worst_case_view_quickly()
    {
        // roughly the shape of a real 3,000-function program: median 2 callees, a few runtime hubs called by hundreds
        var rng = new Random(42);
        const int n = 3000;
        var calls = new Dictionary<int, int[]>();
        for (var i = 1; i < n; i++)
        {
            var targets = new HashSet<int>();
            for (var k = rng.Next(1, 5); k > 0; k--) targets.Add(rng.Next(1, n));
            if (rng.NextDouble() < 0.4) targets.Add(1 + rng.Next(0, 6));        // calls one of six hubs
            targets.Remove(i);
            calls[i] = targets.ToArray();
        }
        var (g, p) = Make(calls, n);

        var watch = Stopwatch.StartNew();
        var widest = 0;
        for (var focus = 1; focus < 200; focus++)
        {
            var hood = Build(g, p, focus, new GraphOptions { Depth = 3 });
            var layout = GraphLayout.Compute(hood);
            widest = Math.Max(widest, layout.Nodes.Count);
            Assert.True(layout.Nodes.Count(x => x.Node.Kind != GraphNodeKind.More) <= 1 + 7 * (GraphOptions.DefaultMaxPerColumn + 1) * 2,
                "the column cap bounds the view");
        }
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 3000, $"200 views took {watch.ElapsedMilliseconds} ms");
        Assert.True(widest > 10);       // the test did exercise real neighbourhoods
    }
}
