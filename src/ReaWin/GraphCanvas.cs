using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReaWin.Services;

namespace ReaWin;

/// <summary>
/// Draws one call-graph neighbourhood and lets the user pan, zoom, hover and select. It is a single element that draws
/// everything itself, not one control per node: the view is capped at a few dozen nodes, but a control tree would still cost
/// layout and memory for each, and drawing a few hundred shapes in one pass is cheap. Only what is inside the viewport is drawn,
/// and labels are dropped as the user zooms out (a node is a plain box below 35%). Colors come from the app's tokens and are
/// looked up when drawing, so a theme change repaints it (<see cref="ThemeManager.Changed"/>).
/// </summary>
/// <remarks>
/// The canvas knows nothing about what a node can do. It only reports what happened to a node (selected, activated, context
/// menu requested, "+N more" clicked); the host decides what is offered, so new actions (explain, rename…) need no change here.
/// </remarks>
public sealed class GraphCanvas : FrameworkElement
{
    private const double MinScale = 0.2, MaxScale = 2.5;

    private GraphLayoutResult _layout = GraphLayoutResult.Empty;
    private readonly Dictionary<string, PositionedNode> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PositionedEdge>> _incident = new(StringComparer.Ordinal);
    private List<(PositionedEdge Edge, StreamGeometry Curve, StreamGeometry Arrow)> _edgeShapes = new();
    private readonly Dictionary<string, Pen> _pens = new();
    private readonly Dictionary<(string, double, string, bool, double), FormattedText> _texts = new();

    private string? _selectedId;
    private string? _hoverId;
    private double _scale = 1, _offsetX, _offsetY;
    private bool _userMovedView;
    private bool _panning;
    private Point _panStart, _panOrigin;
    private bool _panMoved;

    /// <summary>A node was clicked or reached with the keyboard.</summary>
    public event Action<GraphNode>? NodeSelected;
    /// <summary>A node was double-clicked or Enter was pressed on it.</summary>
    public event Action<GraphNode>? NodeActivated;
    /// <summary>The context menu was asked for on a node (right click or the menu key), at this point of the canvas.</summary>
    public event Action<GraphNode, Point>? NodeContextRequested;
    /// <summary>A "+N more" node was clicked or activated.</summary>
    public event Action<GraphNode>? MoreRequested;

    /// <summary>
    /// Supplies the tooltip for the node under the pointer. The host provides it so the canvas stays free of what a node means
    /// (callers, AI notes, kinds); null or empty text shows no tooltip.
    /// </summary>
    public Func<GraphNode, string?>? DescribeNode { get; set; }

    public GraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;   // drawn in OnRender
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(() => { _pens.Clear(); _texts.Clear(); InvalidateVisual(); });

    // ---- data ----------------------------------------------------------------------------------

    /// <summary>Shows a new layout. <paramref name="resetView"/> fits it to the window (a new focus); otherwise the user's pan and zoom stay.</summary>
    public void SetLayout(GraphLayoutResult layout, bool resetView)
    {
        _layout = layout;
        _byId.Clear();
        _incident.Clear();
        _texts.Clear();
        foreach (var n in layout.Nodes) _byId[n.Node.Id] = n;
        foreach (var e in layout.Edges)
        {
            if (!_incident.TryGetValue(e.From, out var a)) _incident[e.From] = a = new List<PositionedEdge>();
            a.Add(e);
            if (!_incident.TryGetValue(e.To, out var b)) _incident[e.To] = b = new List<PositionedEdge>();
            b.Add(e);
        }
        _edgeShapes = layout.Edges.Select(e => (e, Curve(e), Arrow(e))).ToList();
        if (_selectedId is not null && !_byId.ContainsKey(_selectedId)) _selectedId = null;
        _hoverId = null;
        if (resetView || !_userMovedView) Fit();
        InvalidateVisual();
    }

    /// <summary>The id of the selected node, or null.</summary>
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value) return;
            _selectedId = value;
            InvalidateVisual();
        }
    }

    /// <summary>The smallest zoom <see cref="Fit"/> picks: below it the labels are dropped, and a picture nobody can read is not a fit.</summary>
    private const double MinFitScale = 0.7;

    /// <summary>How far a picture with many columns may shrink to fit the width.</summary>
    private const double MinFitWidthScale = 0.45;

    /// <summary>
    /// Shows the whole picture centered when it fits at a readable zoom (never above 100%). When it does not, it keeps the
    /// readable zoom and centers on the focused function instead, so the focus and what is next to it can be read and the rest
    /// is a pan away.
    /// </summary>
    public void Fit()
    {
        _userMovedView = false;
        var b = _layout.Bounds;
        if (ActualWidth <= 0 || ActualHeight <= 0 || b.Width <= 0) { _scale = 1; _offsetX = _offsetY = 0; return; }
        const double pad = 28;
        // a tall column is read by panning, so height may not shrink the picture below the readable zoom; width may, a little further,
        // so that a view with several columns shows them all (names still show at that zoom, the rest appears on zooming in)
        var byWidth = (ActualWidth - 2 * pad) / b.Width;
        var byHeight = (ActualHeight - 2 * pad) / Math.Max(b.Height, 1);
        _scale = Math.Clamp(Math.Min(byWidth, Math.Max(byHeight, MinFitScale)), MinFitWidthScale, 1);

        var focus = _layout.Nodes.FirstOrDefault(n => n.Node.IsFocus)?.Bounds;
        var focusX = focus is { } f ? f.X + f.Width / 2 : b.X + b.Width / 2;
        var focusY = focus is { } g ? g.Y + g.Height / 2 : b.Y + b.Height / 2;
        _offsetX = b.Width * _scale <= ActualWidth - 2 * pad
            ? (ActualWidth - b.Width * _scale) / 2 - b.X * _scale
            : ActualWidth / 2 - focusX * _scale;
        _offsetY = b.Height * _scale <= ActualHeight - 2 * pad
            ? (ActualHeight - b.Height * _scale) / 2 - b.Y * _scale
            : ActualHeight / 2 - focusY * _scale;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_userMovedView) Fit();
    }

    // ---- drawing -------------------------------------------------------------------------------

    private Brush Token(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private Pen PenOf(string token, double thickness, bool dashed = false)
    {
        var key = $"{token}|{thickness}|{dashed}";
        if (_pens.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(Token(token), thickness) { LineJoin = PenLineJoin.Round };
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        return _pens[key] = pen;
    }

    private FormattedText Text(string text, double size, string brushToken, bool bold, double maxWidth, bool mono = false)
    {
        var key = (text + (mono ? "\u0001" : ""), size, brushToken, bold, Math.Round(maxWidth));
        if (_texts.TryGetValue(key, out var cached)) return cached;
        var family = TryFindResource(mono ? "MonoFont" : "UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, Token(brushToken), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (_texts.Count > 600) _texts.Clear();   // zooming never grows this without bound
        return _texts[key] = ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // an invisible fill so the empty parts of the canvas receive the mouse (panning); not a color choice
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, PenOf("FocusBrush", 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 8, 8);
        if (_layout.Nodes.Count == 0) return;

        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _offsetX, _offsetY));
        var view = new LayoutRect(-_offsetX / _scale, -_offsetY / _scale, ActualWidth / _scale, ActualHeight / _scale);
        var active = _selectedId ?? _hoverId;

        foreach (var (edge, curve, arrow) in _edgeShapes)
        {
            var lit = active is not null && (edge.From == active || edge.To == active);
            if (edge.Minor && !lit) continue;   // see GraphEdge.Minor: shown only for the node under the pointer or selected
            if (!EdgeBounds(edge).Intersects(view)) continue;
            var pen = lit ? PenOf("PrimaryBrush", 2) : PenOf("ControlBorderBrush", 1.2, edge.Back);
            dc.DrawGeometry(null, pen, curve);
            dc.DrawGeometry(lit ? Token("PrimaryBrush") : Token("ControlBorderBrush"), null, arrow);
        }

        foreach (var pn in _layout.Nodes)
            if (pn.Bounds.Intersects(view)) DrawNode(dc, pn);

        dc.Pop();
    }

    private void DrawNode(DrawingContext dc, PositionedNode pn)
    {
        var n = pn.Node;
        var r = new Rect(pn.Bounds.X, pn.Bounds.Y, pn.Bounds.Width, pn.Bounds.Height);
        var selected = n.Id == _selectedId;
        var hover = n.Id == _hoverId;
        var inner = r.Width - 20;

        switch (n.Kind)
        {
            case GraphNodeKind.More:
                dc.DrawRoundedRectangle(null, PenOf(hover ? "PrimaryBrush" : "ControlBorderBrush", 1, dashed: true), r, 14, 14);
                if (_scale >= 0.35)
                    dc.DrawText(Text(n.Name + "  (click to show)", 12, "AccentTextBrush", true, inner), new Point(r.X + 12, r.Y + (r.Height - 15) / 2));
                return;

            case GraphNodeKind.Import:
                dc.DrawRoundedRectangle(Token("SurfaceBrush"), PenOf(selected ? "PrimaryBrush" : hover ? "ControlBorderBrush" : "LineBrush", selected ? 2 : 1), r, 14, 14);
                if (_scale >= 0.35)
                {
                    var label = n.ShortName;
                    var lib = n.Name.Contains("::", StringComparison.Ordinal) ? n.Name[..n.Name.IndexOf("::", StringComparison.Ordinal)] : "";
                    dc.DrawText(Text("⌁ " + label + (lib.Length > 0 ? "   " + lib : ""), 11.5, "MutedBrush", false, inner, mono: false), new Point(r.X + 12, r.Y + (r.Height - 14) / 2));
                }
                return;
        }

        // a function: focus and selection use the violet outline (violet means selection and action)
        var library = n.Kind == GraphNodeKind.Library;
        var pen = n.IsFocus ? PenOf("PrimaryBrush", 2.5)
            : selected ? PenOf("PrimaryBrush", 2)
            : library ? PenOf("ControlBorderBrush", 1, dashed: true)
            : hover ? PenOf("ControlBorderBrush", 1.4)
            : PenOf("LineBrush", 1);
        dc.DrawRoundedRectangle(Token("SurfaceBrush"), pen, r, 9, 9);
        if (selected && !n.IsFocus) dc.DrawRoundedRectangle(Token("TintBrush"), null, r, 9, 9);
        if (_scale < 0.35) return;                       // a plain box when zoomed far out

        // First line: the name the AI gave the function (the alias name, else the human-readable title) when it has been explained,
        // tagged "AI" at the right edge because it is an inference; otherwise the name REA reported. The REA name and address always
        // stay on the second line, exactly as reported.
        var nameBrush = library ? "MutedBrush" : "TextBrush";
        var aiLabel = !string.IsNullOrEmpty(n.Alias) ? n.Alias : n.Title;
        var titled = !string.IsNullOrEmpty(aiLabel);
        if (titled && _scale >= 0.5)
        {
            var tag = Text("AI", 10, "AccentTextBrush", true, 24);
            dc.DrawText(tag, new Point(r.Right - 10 - tag.WidthIncludingTrailingWhitespace, r.Y + 10));
            dc.DrawText(Text(aiLabel!, 12.5, nameBrush, true, inner - 28), new Point(r.X + 10, r.Y + 8));
        }
        else dc.DrawText(Text(n.Name, 12.5, nameBrush, true, inner), new Point(r.X + 10, r.Y + 8));
        if (_scale < 0.5) return;

        var badges = new List<string>();
        if (library) badges.Add("library");
        if (n.NoCallers) badges.Add("no callers");
        if (n.Hub) badges.Add("hub");
        var sub = (titled ? n.Name + "  " : "") + n.Address + (badges.Count > 0 ? "   ·   " + string.Join(" · ", badges) : "");
        dc.DrawText(Text(sub, 10.5, "MutedBrush", false, inner, mono: true), new Point(r.X + 10, r.Y + 27));

        // Third line: the brief, cut with an ellipsis to the width of the node; the tooltip shows all of it.
        if (_scale >= 0.7 && !string.IsNullOrEmpty(n.Summary))
            dc.DrawText(Text("AI  " + n.Summary, 10.5, "AccentTextBrush", false, inner), new Point(r.X + 10, r.Y + 44));
    }

    private static StreamGeometry Curve(PositionedEdge e)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(e.P0.X, e.P0.Y), false, false);
            c.BezierTo(new Point(e.P1.X, e.P1.Y), new Point(e.P2.X, e.P2.Y), new Point(e.P3.X, e.P3.Y), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>A small filled triangle at the end of the curve, pointing the way the curve arrives.</summary>
    private static StreamGeometry Arrow(PositionedEdge e)
    {
        var dx = e.P3.X - e.P2.X;
        var dy = e.P3.Y - e.P2.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        const double size = 9;
        var tip = new Point(e.P3.X, e.P3.Y);
        var back = new Point(tip.X - dx * size, tip.Y - dy * size);
        var left = new Point(back.X - dy * size * 0.45, back.Y + dx * size * 0.45);
        var right = new Point(back.X + dy * size * 0.45, back.Y - dx * size * 0.45);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(left, true, false);
            c.LineTo(right, true, false);
        }
        g.Freeze();
        return g;
    }

    private static LayoutRect EdgeBounds(PositionedEdge e)
    {
        var minX = Math.Min(Math.Min(e.P0.X, e.P3.X), Math.Min(e.P1.X, e.P2.X));
        var maxX = Math.Max(Math.Max(e.P0.X, e.P3.X), Math.Max(e.P1.X, e.P2.X));
        var minY = Math.Min(Math.Min(e.P0.Y, e.P3.Y), Math.Min(e.P1.Y, e.P2.Y));
        var maxY = Math.Max(Math.Max(e.P0.Y, e.P3.Y), Math.Max(e.P1.Y, e.P2.Y));
        return new LayoutRect(minX - 10, minY - 10, maxX - minX + 20, maxY - minY + 20);
    }

    // ---- the mouse -----------------------------------------------------------------------------

    private Point ToWorld(Point p) => new((p.X - _offsetX) / _scale, (p.Y - _offsetY) / _scale);

    private PositionedNode? NodeAt(Point screen)
    {
        var w = ToWorld(screen);
        // the last drawn is on top
        for (var i = _layout.Nodes.Count - 1; i >= 0; i--)
            if (_layout.Nodes[i].Bounds.Contains(w.X, w.Y)) return _layout.Nodes[i];
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (NodeAt(p) is { } hit)
        {
            Choose(hit.Node, activate: e.ClickCount >= 2);
            e.Handled = true;
            return;
        }
        _panning = true;
        _panMoved = false;
        _panStart = p;
        _panOrigin = new Point(_offsetX, _offsetY);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panning)
        {
            if (!_panMoved && (Math.Abs(p.X - _panStart.X) > 3 || Math.Abs(p.Y - _panStart.Y) > 3)) _panMoved = true;
            if (_panMoved)
            {
                _offsetX = _panOrigin.X + (p.X - _panStart.X);
                _offsetY = _panOrigin.Y + (p.Y - _panStart.Y);
                _userMovedView = true;
                InvalidateVisual();
            }
            return;
        }
        var hit = NodeAt(p);
        var id = hit?.Node.Id;
        Cursor = id is null ? Cursors.Arrow : Cursors.Hand;
        if (id != _hoverId)
        {
            _hoverId = id;
            ToolTip = hit is null ? null : DescribeNode?.Invoke(hit.Node);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ReleaseMouseCapture();
        if (!_panMoved && _selectedId is not null) { _selectedId = null; InvalidateVisual(); }   // a click on empty space clears the selection
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverId is null) return;
        _hoverId = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (NodeAt(p) is not { } hit || hit.Node.Kind == GraphNodeKind.More) return;
        Select(hit.Node);
        NodeContextRequested?.Invoke(hit.Node, p);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        ZoomAt(p, Math.Pow(1.0015, e.Delta));
        e.Handled = true;
    }

    private void ZoomAt(Point p, double factor)
    {
        var next = Math.Clamp(_scale * factor, MinScale, MaxScale);
        factor = next / _scale;
        _offsetX = p.X - (p.X - _offsetX) * factor;
        _offsetY = p.Y - (p.Y - _offsetY) * factor;
        _scale = next;
        _userMovedView = true;
        InvalidateVisual();
    }

    // ---- the keyboard --------------------------------------------------------------------------

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Move(-1, 0); break;
            case Key.Right: Move(1, 0); break;
            case Key.Up: Move(0, -1); break;
            case Key.Down: Move(0, 1); break;
            case Key.Enter or Key.Space:
                if (_selectedId is not null && _byId.TryGetValue(_selectedId, out var current)) Choose(current.Node, activate: true);
                break;
            case Key.Apps:
                if (_selectedId is not null && _byId.TryGetValue(_selectedId, out var menuNode) && menuNode.Node.Kind != GraphNodeKind.More)
                {
                    var b = menuNode.Bounds;
                    NodeContextRequested?.Invoke(menuNode.Node, new Point(b.X * _scale + _offsetX + 20, b.Y * _scale + _offsetY + 20));
                }
                break;
            case Key.Home: Fit(); break;
            case Key.OemPlus or Key.Add: ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1.2); break;
            case Key.OemMinus or Key.Subtract: ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1 / 1.2); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Arrow keys: left and right go to a neighbour in the next column (preferring one that is connected, then the nearest
    /// level), up and down to the previous or next node in the same column.
    /// </summary>
    private void Move(int dx, int dy)
    {
        if (_layout.Nodes.Count == 0) return;
        if (_selectedId is null || !_byId.TryGetValue(_selectedId, out var from))
        {
            var start = _layout.Nodes.FirstOrDefault(n => n.Node.IsFocus) ?? _layout.Nodes[0];
            Select(start.Node);
            EnsureVisible(start);
            return;
        }

        PositionedNode? target = null;
        if (dy != 0)
        {
            var column = _layout.Nodes.Where(n => n.Node.Column == from.Node.Column).OrderBy(n => n.Bounds.Y).ToList();
            var i = column.FindIndex(n => n.Node.Id == from.Node.Id);
            var j = i + dy;
            if (j >= 0 && j < column.Count) target = column[j];
        }
        else
        {
            var wanted = from.Node.Column + dx;
            var inColumn = _layout.Nodes.Where(n => n.Node.Column == wanted).ToList();
            if (inColumn.Count == 0) return;
            var connected = _incident.TryGetValue(from.Node.Id, out var edges)
                ? edges.Select(e => e.From == from.Node.Id ? e.To : e.From).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var pool = inColumn.Where(n => connected.Contains(n.Node.Id)).ToList();
            if (pool.Count == 0) pool = inColumn;
            var mid = from.Bounds.Y + from.Bounds.Height / 2;
            target = pool.OrderBy(n => Math.Abs(n.Bounds.Y + n.Bounds.Height / 2 - mid)).First();
        }
        if (target is null) return;
        Select(target.Node);
        EnsureVisible(target);
    }

    private void EnsureVisible(PositionedNode pn)
    {
        var x = pn.Bounds.X * _scale + _offsetX;
        var y = pn.Bounds.Y * _scale + _offsetY;
        var w = pn.Bounds.Width * _scale;
        var h = pn.Bounds.Height * _scale;
        const double margin = 24;
        if (x < margin) _offsetX += margin - x;
        else if (x + w > ActualWidth - margin) _offsetX -= x + w - (ActualWidth - margin);
        if (y < margin) _offsetY += margin - y;
        else if (y + h > ActualHeight - margin) _offsetY -= y + h - (ActualHeight - margin);
        _userMovedView = true;
        InvalidateVisual();
    }

    // ---- reporting -----------------------------------------------------------------------------

    private void Select(GraphNode node)
    {
        if (node.Kind == GraphNodeKind.More) return;
        _selectedId = node.Id;
        InvalidateVisual();
        NodeSelected?.Invoke(node);
    }

    private void Choose(GraphNode node, bool activate)
    {
        if (node.Kind == GraphNodeKind.More)
        {
            MoreRequested?.Invoke(node);   // one click is enough: there is nothing to select on it
            return;
        }
        Select(node);
        if (activate) NodeActivated?.Invoke(node);
    }
}
