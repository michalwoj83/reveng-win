using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin;

/// <summary>
/// The Graph tab: who calls a function and what it calls, one neighbourhood at a time. The picture is never the whole program
/// (see <see cref="GraphModel"/>): it follows a focus function, and the user moves the focus by clicking, searching or going
/// back. Library and runtime functions are hidden by default, hubs are not expanded, and the status line says what is left out.
/// Everything here is REA's observation; the AI's one-liners appear on the nodes that have one, marked as inference.
/// </summary>
public partial class MainWindow
{
    private string? _graphFocus;
    private readonly List<string> _graphHistory = new();
    private int _graphHistoryIndex = -1;
    private int _graphMaxPerColumn = GraphOptions.DefaultMaxPerColumn;
    private GraphNode? _graphSelected;
    private GraphNeighbourhood _graphHood = GraphNeighbourhood.Empty;
    private int _graphVersion;                  // a layout that finishes after a newer request started is dropped
    private string? _graphFollowedSelection;    // the function selected on the Functions tab when the graph last followed it
    private readonly List<GraphAction> _graphActions = new();

    /// <summary>
    /// Something the user can do with a node. The panel beside the graph and the node's pop-up menu are both built from the list
    /// of these, so a new action is one entry in <see cref="RegisterGraphActions"/> and no other change: neither the canvas nor
    /// the panel knows what the actions are. An action offers itself for a node through <paramref name="IsAvailable"/>.
    /// </summary>
    private sealed record GraphAction(string Id, string Label, string Tooltip, Func<GraphNode, bool> IsAvailable, Func<GraphNode, Task> RunAsync);

    private static bool IsFunction(GraphNode n) => n.Kind is GraphNodeKind.Program or GraphNodeKind.Library;

    /// <summary>Called once from the constructor.</summary>
    private void InitializeGraph()
    {
        RegisterGraphActions();
        GraphView.NodeSelected += node => SelectGraphNode(node, fromCanvas: true);
        GraphView.NodeActivated += node => { if (IsFunction(node)) FocusGraph(node.Address); };
        GraphView.MoreRequested += _ => { _graphMaxPerColumn += GraphOptions.MoreStep; RebuildGraph(resetView: false); };
        GraphView.NodeContextRequested += ShowGraphMenu;
        ResetGraph();
    }

    /// <summary>
    /// The things that can be done with a node. Only what works is listed: an action that is not implemented does not appear
    /// (the app does not show controls that look wired when they are not).
    /// <para>
    /// Planned, not implemented yet, each to be added here as one more <see cref="GraphAction"/>: explain the function with the
    /// local AI (select it, then run what the Explain button runs), explain the selected node together with its neighbours,
    /// copy the node's name or address, and add it to a list of functions to look at.
    /// </para>
    /// </summary>
    private void RegisterGraphActions()
    {
        _graphActions.Add(new GraphAction("focus", "Make focus",
            "Show this function's callers and callees instead",
            n => IsFunction(n) && !n.IsFocus,
            n => { FocusGraph(n.Address); return Task.CompletedTask; }));

        _graphActions.Add(new GraphAction("open-code", "Open code",
            "Show this function's decompiled code on the Functions tab",
            n => IsFunction(n),
            n =>
            {
                var proc = _procedures.FirstOrDefault(p => p.Address == n.Address);
                if (proc is null) return Task.CompletedTask;
                FunctionsGrid.SelectedItem = proc;
                FunctionsGrid.ScrollIntoView(proc);
                Tabs.SelectedItem = FunctionsTab;
                return Task.CompletedTask;
            }));
    }

    // ---- entering and leaving the tab --------------------------------------------------------------

    private void ShowInGraph_Click(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is not ProcedureInfo { IsImport: false } proc) return;
        _graphFollowedSelection = proc.Address;   // so that showing the tab does not follow the selection a second time
        Tabs.SelectedItem = GraphTab;
        FocusGraph(proc.Address);
    }

    /// <summary>Called when the Graph tab becomes the visible one.</summary>
    private void OnGraphTabShown()
    {
        var selected = (FunctionsGrid.SelectedItem as ProcedureInfo) is { IsImport: false } p ? p.Address : null;
        // follow the function chosen on the Functions tab since the last visit; otherwise keep exploring where the user was
        if (selected is not null && selected != _graphFollowedSelection)
        {
            _graphFollowedSelection = selected;
            FocusGraph(selected);
            return;
        }
        RebuildGraph(resetView: false);
    }

    /// <summary>Forgets the graph of the analysis that is being replaced.</summary>
    private void ResetGraph()
    {
        _graphVersion++;
        _graphFocus = null;
        _graphHistory.Clear();
        _graphHistoryIndex = -1;
        _graphSelected = null;
        _graphMaxPerColumn = GraphOptions.DefaultMaxPerColumn;
        _graphFollowedSelection = null;
        _graphHood = GraphNeighbourhood.Empty;
        GraphView.SetLayout(GraphLayoutResult.Empty, resetView: true);
        UpdateGraphChrome();
        ShowGraphEmpty();
    }

    /// <summary>The call graph arrived (or was recomputed) while the user may be looking at the tab.</summary>
    private void OnCallGraphChanged()
    {
        if (Tabs.SelectedItem == GraphTab) RebuildGraph(resetView: false);
    }

    // ---- moving the focus ---------------------------------------------------------------------------

    private void FocusGraph(string address, bool addToHistory = true)
    {
        if (addToHistory && _graphFocus != address)
        {
            // going somewhere new from the middle of the history drops what was forward of it
            if (_graphHistoryIndex < _graphHistory.Count - 1) _graphHistory.RemoveRange(_graphHistoryIndex + 1, _graphHistory.Count - _graphHistoryIndex - 1);
            _graphHistory.Add(address);
            if (_graphHistory.Count > 100) _graphHistory.RemoveAt(0);
            _graphHistoryIndex = _graphHistory.Count - 1;
        }
        _graphFocus = address;
        _graphMaxPerColumn = GraphOptions.DefaultMaxPerColumn;
        _graphSelected = null;
        RebuildGraph(resetView: true);
    }

    private void GraphBack_Click(object sender, RoutedEventArgs e)
    {
        if (_graphHistoryIndex <= 0) return;
        _graphHistoryIndex--;
        FocusGraph(_graphHistory[_graphHistoryIndex], addToHistory: false);
    }

    private void GraphForward_Click(object sender, RoutedEventArgs e)
    {
        if (_graphHistoryIndex >= _graphHistory.Count - 1) return;
        _graphHistoryIndex++;
        FocusGraph(_graphHistory[_graphHistoryIndex], addToHistory: false);
    }

    private void GraphFit_Click(object sender, RoutedEventArgs e) => GraphView.Fit();

    private void GraphOptions_Changed(object sender, RoutedEventArgs e)
    {
        _graphMaxPerColumn = GraphOptions.DefaultMaxPerColumn;
        RebuildGraph(resetView: true);
    }

    /// <summary>
    /// Enter focuses the function the text names: an address, or part of a name (a name that starts with the text comes first, then
    /// the shortest). Only program functions are searched; imports have no neighbours of interest.
    /// </summary>
    private void GraphFind_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = GraphFindBox.Text.Trim();
        if (text.Length == 0) return;

        var candidates = _procedures.Where(p => !p.IsImport).ToList();
        var matches = candidates.Where(p => p.Address.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = candidates.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Name.Length).ThenBy(p => p.Address, StringComparer.Ordinal).ToList();

        if (matches.Count == 0)
        {
            GraphStatus.Text = $"No program function matches \"{text}\".";
            return;
        }
        FocusGraph(matches[0].Address);
        if (matches.Count > 1)
            GraphStatus.Text = $"{matches.Count} functions match \"{text}\"; showing {matches[0].Name}. Type more of the name to narrow it.";
    }

    // ---- building and drawing ------------------------------------------------------------------------

    private GraphOptions CurrentGraphOptions() => new()
    {
        Depth = GraphDepth3.IsChecked == true ? 3 : GraphDepth2.IsChecked == true ? 2 : 1,
        Direction = GraphCallersOnly.IsChecked == true ? GraphDirection.Callers : GraphCalleesOnly.IsChecked == true ? GraphDirection.Callees : GraphDirection.Both,
        ShowLibrary = GraphLibraryBox.IsChecked == true,
        ShowImports = GraphImportsBox.IsChecked == true,
        ShowHubs = GraphHubsBox.IsChecked == true,
        MaxPerColumn = _graphMaxPerColumn,
    };

    /// <summary>The AI's one line about a function: a stored neighbour summary, or the purpose line of a description.</summary>
    private string? GraphSummaryOf(string address)
    {
        if (_summaries.TryGetValue(address, out var summary)) return summary.Text;
        return _descriptions.TryGetValue(address, out var d) ? NeighbourSummaries.OneLineFrom(d) : null;
    }

    private bool GraphIsLibrary(string address) =>
        _decompiled.TryGetValue(address, out var code) && FunctionContextBuilder.IsLibraryCode(code);

    /// <summary>
    /// Builds the neighbourhood on the UI thread (it only follows lists, milliseconds, and reads the caches the background
    /// decompile writes) and lays it out on another thread. A layout that finishes after the user clicked again is dropped.
    /// </summary>
    private async void RebuildGraph(bool resetView)
    {
        var version = ++_graphVersion;
        if (_analyzedPath is null || _overview is null) { ShowGraphEmpty(); return; }
        if (_graphIndex is null) { ShowGraphEmpty(); return; }
        if (_graphFocus is null) { ShowGraphEmpty(); return; }

        var procedures = _procedures.GroupBy(p => p.Address, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var hood = GraphModel.Build(_graphFocus, _graphIndex, procedures, CurrentGraphOptions(), GraphIsLibrary, GraphSummaryOf);
        if (hood.Nodes.Count == 0) { ShowGraphEmpty(); return; }

        var layout = await Task.Run(() => GraphLayout.Compute(hood));
        if (version != _graphVersion) return;

        _graphHood = hood;
        GraphEmpty.Visibility = Visibility.Collapsed;
        GraphView.Visibility = Visibility.Visible;
        GraphView.SetLayout(layout, resetView);

        // keep the selection if it is still drawn; otherwise the focus is the selected node
        var keep = _graphSelected is { } s ? hood.Nodes.FirstOrDefault(n => n.Id == s.Id) : null;
        SelectGraphNode(keep ?? hood.Nodes.First(n => n.IsFocus), fromCanvas: false);
        UpdateGraphChrome();
    }

    private void UpdateGraphChrome()
    {
        GraphBackButton.IsEnabled = _graphHistoryIndex > 0;
        GraphForwardButton.IsEnabled = _graphHistoryIndex < _graphHistory.Count - 1;
        var focus = _graphHood.Nodes.FirstOrDefault(n => n.IsFocus);
        GraphFocusText.Text = focus is null ? "No function" : $"{focus.Name}  {focus.Address}";
        GraphFitButton.IsEnabled = focus is not null;

        if (focus is null) { GraphStatus.Text = ""; return; }
        var parts = new List<string> { $"{_graphHood.Shown} of {_graphHood.Shown + _graphHood.HiddenLibrary + _graphHood.CollapsedHubs + _graphHood.Overflow} shown" };
        if (_graphHood.HiddenLibrary > 0) parts.Add($"{_graphHood.HiddenLibrary} library or runtime hidden");
        if (_graphHood.CollapsedHubs > 0) parts.Add($"{_graphHood.CollapsedHubs} hub{(_graphHood.CollapsedHubs == 1 ? "" : "s")} collapsed");
        if (_graphHood.Overflow > 0) parts.Add($"{_graphHood.Overflow} more behind \"+N more\"");
        if (_graphHood.Nodes.Count == 1) parts.Add("no direct callers or callees to draw");
        parts.Add("direct calls only: calls through pointers are not in this graph");
        GraphStatus.Text = string.Join("  ·  ", parts);
    }

    // ---- when there is nothing to draw ---------------------------------------------------------------

    private void ShowGraphEmpty()
    {
        GraphView.Visibility = Visibility.Collapsed;
        GraphEmpty.Visibility = Visibility.Visible;
        GraphEmptyAction.Visibility = GraphStartList.Visibility = GraphStartLabel.Visibility = Visibility.Collapsed;
        ShowGraphNode(null);

        if (_analyzedPath is null || _overview is null)
        {
            GraphEmptyTitle.Text = "No analysis yet";
            GraphEmptyText.Text = "Analyze a program or open a project, then choose a function to see how it is called.";
            return;
        }
        if (_graphIndex is null)
        {
            GraphEmptyTitle.Text = "No call graph yet";
            GraphEmptyText.Text = "The call graph is read from Ghidra once and kept in the project. This project does not have it yet. " +
                                  "Reading it takes seconds, but starts Ghidra if it is not running.";
            GraphEmptyAction.Content = "Read the call graph";
            GraphEmptyAction.Visibility = Visibility.Visible;
            return;
        }

        GraphEmptyTitle.Text = "Choose a function";
        GraphEmptyText.Text = "Select a function on the Functions tab and press Show in graph, search for one above, or start from one of these.";
        FillStartList();
    }

    /// <summary>Starting points: the entry point and what it calls first, then the most connected program functions.</summary>
    private void FillStartList()
    {
        if (_graphIndex is null) return;
        var byAddress = _procedures.GroupBy(p => p.Address, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var items = new List<ProcedureInfo>();
        if (_procedures.FirstOrDefault(p => p.IsEntry) is { } entry)
        {
            items.Add(entry);
            items.AddRange(_graphIndex.CalleesOf(entry.Address).Where(a => !a.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase))
                .Select(a => byAddress.GetValueOrDefault(a)).OfType<ProcedureInfo>().Take(8));
        }
        var degree = (ProcedureInfo p) => _graphIndex.CallersOf(p.Address).Count + _graphIndex.CalleesOf(p.Address).Count;
        items.AddRange(_procedures.Where(p => !p.IsImport && !items.Contains(p) && !GraphIsLibrary(p.Address) && degree(p) <= GraphOptions.DefaultHubDegree)
            .OrderByDescending(degree).ThenBy(p => p.Address, StringComparer.Ordinal).Take(8));

        GraphStartList.Items.Clear();
        foreach (var p in items)
            GraphStartList.Items.Add(new ListBoxItem { Content = $"{p.Name}   {p.Address}", Tag = p.Address });
        GraphStartLabel.Visibility = GraphStartList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void GraphEmptyAction_Click(object sender, RoutedEventArgs e)
    {
        GraphEmptyAction.IsEnabled = false;
        try
        {
            // starting the session also reads the graph when it is missing; if a session is already running, read it now
            var session = _session ?? await EnsureSessionAsync();
            if (session is not null && _graphIndex is null)
            {
                using var cts = BeginWork("Reading the call graph…");
                try { await ComputeCallGraphAsync(session, cts.Token); }
                catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
                finally { EndWork(); }
            }
        }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { GraphEmptyAction.IsEnabled = true; }
        RebuildGraph(resetView: true);
        if (_graphIndex is null) ShowGraphEmpty();
    }

    private void GraphStartList_DoubleClick(object sender, MouseButtonEventArgs e) => FocusFromList(GraphStartList);

    private void GraphStartList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; FocusFromList(GraphStartList); }
    }

    private void FocusFromList(ListBox list)
    {
        if (list.SelectedItem is ListBoxItem { Tag: string address }) FocusGraph(address);
    }

    // ---- the selected node ---------------------------------------------------------------------------

    private void SelectGraphNode(GraphNode node, bool fromCanvas)
    {
        _graphSelected = node;
        if (!fromCanvas) GraphView.SelectedId = node.Id;
        ShowGraphNode(node);
    }

    /// <summary>The panel beside the picture: observed facts, the AI's note when there is one, the actions, and the neighbours as a list.</summary>
    private void ShowGraphNode(GraphNode? node)
    {
        GraphActionsPanel.Children.Clear();
        GraphNeighbours.Items.Clear();
        if (node is null)
        {
            GraphNodeName.Text = "Nothing selected";
            GraphNodeAddress.Text = "";
            GraphNodeDetails.Visibility = GraphNeighboursLabel.Visibility = GraphNeighbours.Visibility = Visibility.Collapsed;
            return;
        }

        GraphNodeName.Text = node.Name;
        GraphNodeAddress.Text = node.Address;
        GraphNodeDetails.Visibility = Visibility.Visible;

        var graph = _graphIndex;
        var callers = graph?.CallersOf(node.Address) ?? [];
        var callees = graph?.CalleesOf(node.Address) ?? [];
        var internalCallees = callees.Where(a => !a.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase)).ToList();
        var facts = new List<string>
        {
            node.Kind switch
            {
                GraphNodeKind.Library => "Library or runtime function (matched by Ghidra's signature database).",
                GraphNodeKind.Import => "Imported function: it has no code in this file.",
                _ => "Program function, not matched to a known library.",
            },
        };
        facts.Add(node.Kind == GraphNodeKind.Import
            ? $"Called by {callers.Count} program function{(callers.Count == 1 ? "" : "s")}."
            : $"Called by {callers.Count} · calls {internalCallees.Count} program function{(internalCallees.Count == 1 ? "" : "s")}" +
              $" and {callees.Count - internalCallees.Count} import{(callees.Count - internalCallees.Count == 1 ? "" : "s")}.");
        if (node.NoCallers) facts.Add("No direct callers: reached through a pointer, vtable or callback, or unused.");
        if (node.Hub) facts.Add("A hub: very many functions call it or it calls, so the graph does not follow calls through it.");
        GraphNodeFacts.Text = string.Join("\n", facts);

        var ai = IsFunction(node) ? GraphSummaryOf(node.Address) : null;
        GraphNodeAi.Visibility = ai is null ? Visibility.Collapsed : Visibility.Visible;
        GraphNodeAiText.Text = ai ?? "";

        foreach (var action in _graphActions.Where(a => a.IsAvailable(node)))
        {
            var button = new Button { Content = action.Label, ToolTip = action.Tooltip, Style = (Style)FindResource("TonalButton"), Margin = new Thickness(0, 0, 6, 6), MinHeight = 28 };
            AutomationProperties.SetName(button, $"{action.Label}: {node.Name}");
            button.Click += async (_, _) => await RunGraphActionAsync(action, node);
            GraphActionsPanel.Children.Add(button);
        }

        // the same neighbours as a list: reachable with the keyboard and by screen readers, and not limited to what is drawn
        var neighbours = new List<(string Glyph, string Address)>();
        neighbours.AddRange(callers.Select(a => ("◀", a)));
        neighbours.AddRange(callees.Select(a => ("▶", a)));
        var byAddress = _procedures.GroupBy(p => p.Address, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        const int cap = 300;
        foreach (var (glyph, address) in neighbours.Take(cap))
        {
            var name = byAddress.TryGetValue(address, out var p) ? p.Name : address;
            var isImport = address.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase);
            GraphNeighbours.Items.Add(new ListBoxItem
            {
                Content = $"{glyph} {name}" + (isImport ? "" : $"   {address}"),
                Tag = isImport ? null : address,
                ToolTip = glyph == "◀" ? "Calls the selected function" : "Called by the selected function",
            });
        }
        if (neighbours.Count > cap)
            GraphNeighbours.Items.Add(new ListBoxItem { Content = $"… and {neighbours.Count - cap} more", IsEnabled = false });
        var any = GraphNeighbours.Items.Count > 0;
        GraphNeighboursLabel.Visibility = GraphNeighbours.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunGraphActionAsync(GraphAction action, GraphNode node)
    {
        try { await action.RunAsync(node); }
        catch (ReaException ex) { StatusText.Text = $"{action.Label} failed: {FirstLine(ex.Message)}"; }
    }

    private void GraphNeighbours_DoubleClick(object sender, MouseButtonEventArgs e) => FocusFromList(GraphNeighbours);

    private void GraphNeighbours_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; FocusFromList(GraphNeighbours); }
    }

    /// <summary>A themed pop-up with the actions available for a node, at the place that was right-clicked.</summary>
    private void ShowGraphMenu(GraphNode node, Point at)
    {
        var actions = _graphActions.Where(a => a.IsAvailable(node)).ToList();
        if (actions.Count == 0) return;

        var stack = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = GraphView,
            Placement = PlacementMode.RelativePoint,
            HorizontalOffset = at.X,
            VerticalOffset = at.Y,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, ToolTip = action.Tooltip, Style = (Style)FindResource("TextButton"), HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 150 };
            button.Click += async (_, _) => { popup.IsOpen = false; await RunGraphActionAsync(action, node); };
            stack.Children.Add(button);
        }
        popup.Child = new Border { Style = (Style)FindResource("Pane"), Padding = new Thickness(6), Child = stack, Margin = new Thickness(0, 0, 8, 8) };
        popup.IsOpen = true;
    }
}
