using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ged.Core.Editing.Graph;

namespace Ged.App.Controls;

/// <summary>A drawable node on the shared graph canvas (positions are in graph space).</summary>
internal sealed class GraphCanvasNode
{
    /// <summary>Stable key (object UID for the link graph, node id for the dependency graph).</summary>
    public int Key { get; init; }

    /// <summary>The panel's own node object (returned by callbacks).</summary>
    public object? Tag { get; init; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 168;

    public double Height { get; set; } = 46;

    public string Title { get; init; } = string.Empty;

    public string Subtitle { get; init; } = string.Empty;

    public Color Fill { get; init; } = Color.FromRgb(0x3A, 0x3E, 0x46);

    public bool Selected { get; set; }

    /// <summary>Dimmed rendering (e.g. base-game-skipped dependency).</summary>
    public bool Dimmed { get; init; }

    /// <summary>A short status badge drawn top-right (e.g. "MISSING", "3/5"), or null.</summary>
    public string? Badge { get; init; }

    public Color BadgeColor { get; init; } = Colors.Goldenrod;

    /// <summary>null = no checkbox; otherwise the include/exclude state (drawn + hit-tested).</summary>
    public bool? Checkbox { get; set; }

    /// <summary>Draws an output port on the right edge that can be dragged to create an edge.</summary>
    public bool HasPort { get; init; }

    /// <summary>A small square swatch colour drawn on the left (kind indicator), or null.</summary>
    public Color? Swatch { get; init; }

    /// <summary>
    /// Draws a disclosure chevron on the left edge (collapsed = ▸, expanded = ▾) that
    /// can be clicked to toggle. Used by the dependency graph's category nodes.
    /// </summary>
    public bool Collapsible { get; init; }

    /// <summary>The chevron state when <see cref="Collapsible"/> (collapsed hides the subtree).</summary>
    public bool Collapsed { get; set; }

    public string? Tooltip { get; init; }
}

/// <summary>A drawable directed edge between two node keys.</summary>
internal sealed class GraphCanvasEdge
{
    public int FromKey { get; init; }

    public int ToKey { get; init; }

    public object? Tag { get; init; }

    public Color Color { get; init; } = Color.FromRgb(0x8A, 0xC0, 0xFF);

    public bool Selected { get; set; }

    /// <summary>Dashed rendering for a nested/child edge (dependency graph).</summary>
    public bool Dashed { get; init; }

    public string? Tooltip { get; init; }
}

/// <summary>
/// A reusable, custom-drawn node-graph canvas shared by the Link Graph and
/// Dependency Graph panels (dependency-free, in the spirit of the UV-unwrap view).
/// It provides pan (MMB / space-drag), cursor-centred wheel zoom, fit-to-view, a
/// click-to-navigate minimap, draggable single/box-multi-selected nodes, edge
/// selection, checkbox hit-testing, and — where a node exposes an output port —
/// drag-to-create edges with live accept/refuse feedback. The panels supply the
/// node/edge model and react through the exposed callbacks; the canvas owns only
/// view state.
/// </summary>
internal sealed class GraphCanvas : Control
{
    private static readonly Typeface Face = new("Segoe UI");

    private readonly List<GraphCanvasNode> _nodes = new();
    private readonly List<GraphCanvasEdge> _edges = new();
    private readonly Dictionary<int, GraphCanvasNode> _byKey = new();

    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _fitted;

    private DragMode _drag = DragMode.None;
    private Point _dragStartScreen;
    private Point _lastScreen;
    private bool _spaceHeld;
    private readonly Dictionary<int, (double X, double Y)> _dragOrigin = new();
    private Rect _boxRect;

    private GraphCanvasNode? _edgeSource;
    private Point _edgeDragScreen;
    private bool _edgeDragValid;
    private GraphCanvasNode? _edgeDropHover;

    private bool _showMinimap = true;
    private string? _lastTip;
    private readonly IBrush _bg = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));

    // Routed edge paths in GRAPH space (zoom/pan never changes a route); rebuilt
    // lazily whenever the graph is replaced or nodes move.
    private readonly Dictionary<GraphCanvasEdge, GraphEdgePath> _edgeRoutes = new();
    private bool _routesDirty = true;

    public GraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private enum DragMode
    {
        None,
        Pan,
        MoveNodes,
        BoxSelect,
        CreateEdge,
    }

    // ---- Callbacks the panel wires up ----

    /// <summary>Node clicked: (key, additive). Additive = Ctrl held.</summary>
    public Action<int, bool>? NodeClicked { get; set; }

    /// <summary>Node double-clicked (jump-to).</summary>
    public Action<int>? NodeDoubleClicked { get; set; }

    /// <summary>A box selection completed with the node keys inside it (+ additive flag).</summary>
    public Action<IReadOnlyList<int>, bool>? BoxSelected { get; set; }

    /// <summary>Node drag finished — the panel persists the new positions from <see cref="Nodes"/>.</summary>
    public Action? NodesMoved { get; set; }

    /// <summary>An edge was clicked/selected (its Tag), or null when the selection cleared.</summary>
    public Action<GraphCanvasEdge?>? EdgeSelected { get; set; }

    /// <summary>A checkbox toggled: (key, newValue).</summary>
    public Action<int, bool>? CheckboxToggled { get; set; }

    /// <summary>A collapsible node's disclosure chevron was clicked: (key). The panel flips its state.</summary>
    public Action<int>? ChevronToggled { get; set; }

    /// <summary>Live edge-drag validation: (fromKey, toKey) → accept. Drives the rubber-band colour.</summary>
    public Func<int, int, bool>? ValidatePort { get; set; }

    /// <summary>A port drag was dropped on a target node: (fromKey, toKey). The panel creates the link.</summary>
    public Action<int, int>? PortDropped { get; set; }

    /// <summary>Empty-space click (clears selection).</summary>
    public Action? BackgroundClicked { get; set; }

    /// <summary>Delete/Backspace pressed with an edge selected (break the link).</summary>
    public Action? EdgeDeleteRequested { get; set; }

    public IReadOnlyList<GraphCanvasNode> Nodes => _nodes;

    public bool ShowMinimap
    {
        get => _showMinimap;
        set
        {
            _showMinimap = value;
            InvalidateVisual();
        }
    }

    /// <summary>Replaces the graph model. Existing view (pan/zoom) is preserved unless <paramref name="refit"/>.</summary>
    public void SetGraph(IEnumerable<GraphCanvasNode> nodes, IEnumerable<GraphCanvasEdge> edges, bool refit = false)
    {
        _nodes.Clear();
        _edges.Clear();
        _byKey.Clear();
        foreach (GraphCanvasNode n in nodes)
        {
            _nodes.Add(n);
            _byKey[n.Key] = n;
        }

        _edges.AddRange(edges);
        _routesDirty = true;
        if (refit || !_fitted)
        {
            _fitted = false;
        }

        InvalidateVisual();
    }

    public void FitToView()
    {
        _fitted = false;
        InvalidateVisual();
    }

    /// <summary>Requests a repaint after the panel mutates a node in place (e.g. a checkbox).</summary>
    public void Redraw() => InvalidateVisual();

    // ---- Coordinate transforms ----

    private double ToScreenX(double gx) => ((gx - _panX) * _zoom) + (Bounds.Width / 2);

    private double ToScreenY(double gy) => ((gy - _panY) * _zoom) + (Bounds.Height / 2);

    private double ToGraphX(double sx) => ((sx - (Bounds.Width / 2)) / _zoom) + _panX;

    private double ToGraphY(double sy) => ((sy - (Bounds.Height / 2)) / _zoom) + _panY;

    private Rect NodeScreenRect(GraphCanvasNode n) =>
        new(ToScreenX(n.X), ToScreenY(n.Y), n.Width * _zoom, n.Height * _zoom);

    // ---- Input ----

    /// <summary>The currently selected edge, or null.</summary>
    public GraphCanvasEdge? SelectedEdge => _edges.FirstOrDefault(ed => ed.Selected);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = true;
        }
        else if (e.Key is Key.Delete or Key.Back && SelectedEdge is not null)
        {
            EdgeDeleteRequested?.Invoke();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
        }

        base.OnKeyUp(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        PointerPoint p = e.GetCurrentPoint(this);
        _dragStartScreen = p.Position;
        _lastScreen = p.Position;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Minimap click navigates.
        if (_showMinimap && MinimapRect() is { } mm && mm.Contains(p.Position))
        {
            NavigateFromMinimap(p.Position, mm);
            e.Handled = true;
            return;
        }

        // Pan: middle button, or space + left.
        if (p.Properties.IsMiddleButtonPressed || (_spaceHeld && p.Properties.IsLeftButtonPressed))
        {
            _drag = DragMode.Pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!p.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Disclosure chevron hit?
        foreach (GraphCanvasNode n in _nodes)
        {
            if (n.Collapsible && ChevronRect(n).Contains(p.Position))
            {
                ChevronToggled?.Invoke(n.Key);
                e.Handled = true;
                return;
            }
        }

        // Checkbox hit?
        foreach (GraphCanvasNode n in _nodes)
        {
            if (n.Checkbox is { } state && CheckboxRect(n).Contains(p.Position))
            {
                CheckboxToggled?.Invoke(n.Key, !state);
                e.Handled = true;
                return;
            }
        }

        // Output-port hit → start an edge-create drag.
        foreach (GraphCanvasNode n in _nodes.Where(n => n.HasPort))
        {
            if (PortRect(n).Contains(p.Position))
            {
                _drag = DragMode.CreateEdge;
                _edgeSource = n;
                _edgeDragScreen = p.Position;
                _edgeDropHover = null;
                _edgeDragValid = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // Node body hit → select + start move (drag the whole selection together).
        GraphCanvasNode? hit = NodeAt(p.Position);
        if (hit is not null)
        {
            if (e.ClickCount == 2)
            {
                NodeDoubleClicked?.Invoke(hit.Key);
                e.Handled = true;
                return;
            }

            if (!hit.Selected)
            {
                NodeClicked?.Invoke(hit.Key, ctrl);
            }
            else if (ctrl)
            {
                NodeClicked?.Invoke(hit.Key, true);
            }

            _drag = DragMode.MoveNodes;
            _dragOrigin.Clear();
            foreach (GraphCanvasNode n in _nodes.Where(n => n.Selected || ReferenceEquals(n, hit)))
            {
                _dragOrigin[n.Key] = (n.X, n.Y);
            }

            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Edge hit → select it.
        GraphCanvasEdge? edge = EdgeAt(p.Position);
        if (edge is not null)
        {
            foreach (GraphCanvasEdge ed in _edges)
            {
                ed.Selected = ReferenceEquals(ed, edge);
            }

            EdgeSelected?.Invoke(edge);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Empty space → box select.
        _drag = DragMode.BoxSelect;
        _boxRect = new Rect(p.Position, p.Position);
        foreach (GraphCanvasEdge ed in _edges)
        {
            ed.Selected = false;
        }

        EdgeSelected?.Invoke(null);
        BackgroundClicked?.Invoke();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Point pos = e.GetCurrentPoint(this).Position;
        double dx = pos.X - _lastScreen.X;
        double dy = pos.Y - _lastScreen.Y;
        _lastScreen = pos;

        switch (_drag)
        {
            case DragMode.Pan:
                _panX -= dx / _zoom;
                _panY -= dy / _zoom;
                InvalidateVisual();
                break;

            case DragMode.MoveNodes:
                double gdx = (pos.X - _dragStartScreen.X) / _zoom;
                double gdy = (pos.Y - _dragStartScreen.Y) / _zoom;
                foreach (KeyValuePair<int, (double X, double Y)> kv in _dragOrigin)
                {
                    if (_byKey.TryGetValue(kv.Key, out GraphCanvasNode? n))
                    {
                        n.X = kv.Value.X + gdx;
                        n.Y = kv.Value.Y + gdy;
                    }
                }

                _routesDirty = true;
                InvalidateVisual();
                break;

            case DragMode.BoxSelect:
                _boxRect = new Rect(_dragStartScreen, pos);
                InvalidateVisual();
                break;

            case DragMode.CreateEdge:
                _edgeDragScreen = pos;
                _edgeDropHover = NodeAt(pos);
                _edgeDragValid = _edgeDropHover is not null && _edgeSource is not null &&
                    !ReferenceEquals(_edgeDropHover, _edgeSource) &&
                    (ValidatePort?.Invoke(_edgeSource.Key, _edgeDropHover.Key) ?? true);
                InvalidateVisual();
                break;

            default:
                UpdateHoverTooltip(pos);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(this).Position;
        switch (_drag)
        {
            case DragMode.MoveNodes:
                if (pos != _dragStartScreen)
                {
                    NodesMoved?.Invoke();
                }

                break;

            case DragMode.BoxSelect:
                var keys = _nodes.Where(n => NodeScreenRect(n).Intersects(_boxRect)).Select(n => n.Key).ToList();
                if (keys.Count > 0)
                {
                    BoxSelected?.Invoke(keys, e.KeyModifiers.HasFlag(KeyModifiers.Control));
                }

                break;

            case DragMode.CreateEdge:
                if (_edgeSource is not null && NodeAt(pos) is { } target && !ReferenceEquals(target, _edgeSource))
                {
                    PortDropped?.Invoke(_edgeSource.Key, target.Key);
                }

                break;
        }

        _drag = DragMode.None;
        _edgeSource = null;
        _edgeDropHover = null;
        _dragOrigin.Clear();
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Point pos = e.GetCurrentPoint(this).Position;
        double beforeX = ToGraphX(pos.X);
        double beforeY = ToGraphY(pos.Y);
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        _zoom = Math.Clamp(_zoom * factor, 0.1, 6.0);
        // Keep the graph point under the cursor fixed.
        _panX = beforeX - ((pos.X - (Bounds.Width / 2)) / _zoom);
        _panY = beforeY - ((pos.Y - (Bounds.Height / 2)) / _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    // ---- Hit testing ----

    private GraphCanvasNode? NodeAt(Point screen)
    {
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            if (NodeScreenRect(_nodes[i]).Contains(screen))
            {
                return _nodes[i];
            }
        }

        return null;
    }

    private GraphCanvasEdge? EdgeAt(Point screen)
    {
        EnsureRouted();
        GraphCanvasEdge? best = null;
        double bestDist = 6;
        foreach (GraphCanvasEdge ed in _edges)
        {
            if (!_byKey.TryGetValue(ed.FromKey, out GraphCanvasNode? a) || !_byKey.TryGetValue(ed.ToKey, out GraphCanvasNode? b))
            {
                continue;
            }

            if (_edgeRoutes.TryGetValue(ed, out GraphEdgePath? path))
            {
                // Min distance over the flattened routed polyline (screen space).
                Point prev = ToScreen(path.Polyline[0]);
                for (int i = 1; i < path.Polyline.Count; i++)
                {
                    Point cur = ToScreen(path.Polyline[i]);
                    double dSeg = DistanceToSegment(screen, prev, cur);
                    if (dSeg < bestDist)
                    {
                        bestDist = dSeg;
                        best = ed;
                    }

                    prev = cur;
                }

                continue;
            }

            double d = DistanceToSegment(screen, EdgeStart(a), EdgeEnd(b));
            if (d < bestDist)
            {
                bestDist = d;
                best = ed;
            }
        }

        return best;
    }

    private Rect PortRect(GraphCanvasNode n)
    {
        Rect r = NodeScreenRect(n);
        double s = 12;
        return new Rect(r.Right - (s / 2), r.Center.Y - (s / 2), s, s);
    }

    private Rect CheckboxRect(GraphCanvasNode n)
    {
        Rect r = NodeScreenRect(n);
        double s = Math.Min(14, r.Height - 6);
        return new Rect(r.X + 5, r.Center.Y - (s / 2), s, s);
    }

    private Rect ChevronRect(GraphCanvasNode n)
    {
        Rect r = NodeScreenRect(n);
        double s = Math.Min(16, r.Height - 6);
        return new Rect(r.X + 4, r.Center.Y - (s / 2), s, s);
    }

    private Point EdgeStart(GraphCanvasNode n)
    {
        Rect r = NodeScreenRect(n);
        return new Point(r.Right, r.Center.Y);
    }

    private Point EdgeEnd(GraphCanvasNode n)
    {
        Rect r = NodeScreenRect(n);
        return new Point(r.X, r.Center.Y);
    }

    // ---- Edge routing ----

    private Point ToScreen(GraphPoint p) => new(ToScreenX(p.X), ToScreenY(p.Y));

    private static GraphRect GraphRectOf(GraphCanvasNode n) => new(n.X, n.Y, n.Width, n.Height);

    /// <summary>
    /// Recomputes every edge's routed path (graph space) when the graph changed or
    /// nodes moved: a smooth S-curve between ports that detours around any other
    /// node's rect it would otherwise pass under.
    /// </summary>
    private void EnsureRouted()
    {
        if (!_routesDirty)
        {
            return;
        }

        _edgeRoutes.Clear();
        var obstacles = new List<GraphRect>(Math.Max(0, _nodes.Count - 2));
        foreach (GraphCanvasEdge ed in _edges)
        {
            if (!_byKey.TryGetValue(ed.FromKey, out GraphCanvasNode? a) || !_byKey.TryGetValue(ed.ToKey, out GraphCanvasNode? b))
            {
                continue;
            }

            obstacles.Clear();
            foreach (GraphCanvasNode n in _nodes)
            {
                if (n.Key != ed.FromKey && n.Key != ed.ToKey)
                {
                    obstacles.Add(GraphRectOf(n));
                }
            }

            _edgeRoutes[ed] = GraphEdgeRouter.Route(GraphRectOf(a), GraphRectOf(b), obstacles);
        }

        _routesDirty = false;
    }

    private void UpdateHoverTooltip(Point pos)
    {
        string? tip = NodeAt(pos)?.Tooltip ?? EdgeAt(pos)?.Tooltip;
        if (!Equals(tip, _lastTip))
        {
            _lastTip = tip;
            ToolTip.SetTip(this, tip);
        }
    }

    // ---- Rendering ----

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(_bg, new Rect(Bounds.Size));
        EnsureFitted();
        EnsureRouted();
        DrawGrid(ctx);

        // Edges behind nodes.
        foreach (GraphCanvasEdge ed in _edges)
        {
            DrawEdge(ctx, ed);
        }

        // In-progress edge creation rubber-band.
        if (_drag == DragMode.CreateEdge && _edgeSource is not null)
        {
            var brush = new SolidColorBrush(_edgeDragValid ? Colors.LimeGreen : Color.FromRgb(0xE0, 0x50, 0x50));
            ctx.DrawLine(new Pen(brush, 2, DashStyle.Dash), EdgeStart(_edgeSource), _edgeDragScreen);
        }

        foreach (GraphCanvasNode n in _nodes)
        {
            DrawNode(ctx, n);
        }

        if (_drag == DragMode.BoxSelect)
        {
            var fill = new SolidColorBrush(Color.FromArgb(40, 0x8A, 0xC0, 0xFF));
            ctx.DrawRectangle(fill, new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0xC0, 0xFF)), 1), _boxRect);
        }

        if (_showMinimap)
        {
            DrawMinimap(ctx);
        }
    }

    private void EnsureFitted()
    {
        if (_fitted || _nodes.Count == 0 || Bounds.Width < 8 || Bounds.Height < 8)
        {
            return;
        }

        double minX = _nodes.Min(n => n.X);
        double minY = _nodes.Min(n => n.Y);
        double maxX = _nodes.Max(n => n.X + n.Width);
        double maxY = _nodes.Max(n => n.Y + n.Height);
        double w = Math.Max(1, maxX - minX);
        double h = Math.Max(1, maxY - minY);
        _zoom = Math.Clamp(Math.Min(Bounds.Width / w, Bounds.Height / h) * 0.9, 0.1, 2.0);
        _panX = (minX + maxX) / 2;
        _panY = (minY + maxY) / 2;
        _fitted = true;
    }

    private void DrawGrid(DrawingContext ctx)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(28, 0xFF, 0xFF, 0xFF)), 1);
        double step = 64 * _zoom;
        if (step < 12)
        {
            return;
        }

        double ox = ToScreenX(0) % step;
        double oy = ToScreenY(0) % step;
        for (double x = ox; x < Bounds.Width; x += step)
        {
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        for (double y = oy; y < Bounds.Height; y += step)
        {
            ctx.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    private void DrawEdge(DrawingContext ctx, GraphCanvasEdge ed)
    {
        if (!_byKey.TryGetValue(ed.FromKey, out GraphCanvasNode? a) || !_byKey.TryGetValue(ed.ToKey, out GraphCanvasNode? b))
        {
            return;
        }

        Color col = ed.Selected ? Colors.White : ed.Color;
        var pen = new Pen(new SolidColorBrush(col), ed.Selected ? 2.5 : 1.6, ed.Dashed ? DashStyle.Dash : null);

        Point tip;
        Point tangentFrom;
        if (_edgeRoutes.TryGetValue(ed, out GraphEdgePath? path))
        {
            // Routed cubic bezier path (control points transformed graph → screen).
            var curve = new StreamGeometry();
            using (StreamGeometryContext g = curve.Open())
            {
                g.BeginFigure(ToScreen(path.Segments[0].P0), false);
                foreach (GraphBezierSegment seg in path.Segments)
                {
                    g.CubicBezierTo(ToScreen(seg.C1), ToScreen(seg.C2), ToScreen(seg.P1));
                }

                g.EndFigure(false);
            }

            ctx.DrawGeometry(null, pen, curve);

            GraphBezierSegment last = path.Segments[^1];
            tip = ToScreen(last.P1);
            tangentFrom = ToScreen(last.C2);
            if (Math.Abs(tip.X - tangentFrom.X) < 1e-3 && Math.Abs(tip.Y - tangentFrom.Y) < 1e-3)
            {
                tangentFrom = ToScreen(last.P0);
            }
        }
        else
        {
            Point p1 = EdgeStart(a);
            Point p2 = EdgeEnd(b);
            ctx.DrawLine(pen, p1, p2);
            tip = p2;
            tangentFrom = p1;
        }

        // Arrowhead at the endpoint, oriented along the incoming tangent.
        var dir = tip - tangentFrom;
        double len = Math.Max(1e-3, Math.Sqrt((dir.X * dir.X) + (dir.Y * dir.Y)));
        var u = new Point(dir.X / len, dir.Y / len);
        var nrm = new Point(-u.Y, u.X);
        Point back = new(tip.X - (u.X * 10), tip.Y - (u.Y * 10));
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(new Point(back.X + (nrm.X * 4.5), back.Y + (nrm.Y * 4.5)));
            g.LineTo(new Point(back.X - (nrm.X * 4.5), back.Y - (nrm.Y * 4.5)));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(new SolidColorBrush(col), null, geo);
    }

    private void DrawNode(DrawingContext ctx, GraphCanvasNode n)
    {
        Rect r = NodeScreenRect(n);
        double opacity = n.Dimmed ? 0.55 : 1.0;
        using (ctx.PushOpacity(opacity))
        {
            var fill = new SolidColorBrush(n.Fill);
            var border = new Pen(new SolidColorBrush(n.Selected ? Colors.White : Color.FromArgb(120, 0, 0, 0)), n.Selected ? 2.5 : 1);
            ctx.DrawRectangle(fill, border, new RoundedRect(r, 5));

            // Left disclosure chevron (collapsible category nodes).
            double textLeft = r.X + 8;
            if (n.Collapsible)
            {
                Rect ch = ChevronRect(n);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xDC)), 1.6);
                double cx = ch.Center.X, cy = ch.Center.Y, h = 3.2;
                if (n.Collapsed)
                {
                    // ▸ pointing right
                    ctx.DrawLine(pen, new Point(cx - 2, cy - h), new Point(cx + 2.5, cy));
                    ctx.DrawLine(pen, new Point(cx + 2.5, cy), new Point(cx - 2, cy + h));
                }
                else
                {
                    // ▾ pointing down
                    ctx.DrawLine(pen, new Point(cx - h, cy - 2), new Point(cx, cy + 2.5));
                    ctx.DrawLine(pen, new Point(cx, cy + 2.5), new Point(cx + h, cy - 2));
                }

                textLeft = ch.Right + 4;
            }

            // Left swatch.
            if (n.Swatch is { } sw)
            {
                var swr = new Rect(r.X + 6, r.Center.Y - 6, 12, 12);
                ctx.DrawRectangle(new SolidColorBrush(sw), null, new RoundedRect(swr, 2));
                textLeft = swr.Right + 6;
            }

            if (n.Checkbox is { } state)
            {
                Rect cb = CheckboxRect(n);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.Gainsboro), 1.2), new RoundedRect(cb, 2));
                if (state)
                {
                    var chk = new Pen(new SolidColorBrush(Colors.LimeGreen), 2);
                    ctx.DrawLine(chk, new Point(cb.X + 3, cb.Center.Y), new Point(cb.Center.X - 1, cb.Bottom - 3));
                    ctx.DrawLine(chk, new Point(cb.Center.X - 1, cb.Bottom - 3), new Point(cb.Right - 2, cb.Y + 2));
                }

                textLeft = cb.Right + 6;
            }

            if (r.Width > 40 && r.Height > 16)
            {
                double avail = Math.Max(20, r.Right - textLeft - 6);
                DrawText(ctx, n.Title, textLeft, r.Y + 5, 12, Colors.White, avail, bold: true);
                if (!string.IsNullOrEmpty(n.Subtitle))
                {
                    DrawText(ctx, n.Subtitle, textLeft, r.Y + 22, 10.5, Color.FromRgb(0xC8, 0xCC, 0xD4), avail);
                }
            }

            if (n.Badge is { } badge)
            {
                DrawBadge(ctx, r, badge, n.BadgeColor);
            }

            if (n.HasPort)
            {
                Rect pr = PortRect(n);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x8A, 0xC0, 0xFF)), new Pen(new SolidColorBrush(Colors.White), 1), pr.Center, pr.Width / 2, pr.Height / 2);
            }
        }
    }

    private void DrawBadge(DrawingContext ctx, Rect node, string text, Color color)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, new SolidColorBrush(Colors.Black));
        double pad = 4;
        double bw = ft.Width + (pad * 2);
        double bh = ft.Height + 2;
        var br = new Rect(node.Right - bw - 4, node.Y + 4, bw, bh);
        if (br.X < node.X)
        {
            return;
        }

        ctx.DrawRectangle(new SolidColorBrush(color), null, new RoundedRect(br, 3));
        ctx.DrawText(ft, new Point(br.X + pad, br.Y + 1));
    }

    private static void DrawText(DrawingContext ctx, string text, double x, double y, double size, Color color, double maxWidth, bool bold = false)
    {
        var typeface = bold ? new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold) : Face;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(color))
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        ctx.DrawText(ft, new Point(x, y));
    }

    // ---- Minimap ----

    private Rect? MinimapRect()
    {
        if (_nodes.Count == 0 || Bounds.Width < 240 || Bounds.Height < 200)
        {
            return null;
        }

        const double w = 176, h = 116, pad = 10;
        return new Rect(Bounds.Width - w - pad, Bounds.Height - h - pad, w, h);
    }

    private void DrawMinimap(DrawingContext ctx)
    {
        if (MinimapRect() is not { } mm)
        {
            return;
        }

        double minX = _nodes.Min(n => n.X);
        double minY = _nodes.Min(n => n.Y);
        double maxX = _nodes.Max(n => n.X + n.Width);
        double maxY = _nodes.Max(n => n.Y + n.Height);
        double gw = Math.Max(1, maxX - minX);
        double gh = Math.Max(1, maxY - minY);
        double s = Math.Min((mm.Width - 8) / gw, (mm.Height - 8) / gh);

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 0x10, 0x12, 0x16)), new Pen(new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xFF, 0xFF)), 1), new RoundedRect(mm, 3));

        Point Map(double gx, double gy) => new(mm.X + 4 + ((gx - minX) * s), mm.Y + 4 + ((gy - minY) * s));

        foreach (GraphCanvasNode n in _nodes)
        {
            Point tl = Map(n.X, n.Y);
            var rr = new Rect(tl.X, tl.Y, Math.Max(2, n.Width * s), Math.Max(2, n.Height * s));
            ctx.DrawRectangle(new SolidColorBrush(n.Selected ? Colors.White : n.Fill), null, rr);
        }

        // Current viewport rectangle.
        double vx0 = ToGraphX(0), vy0 = ToGraphY(0);
        double vx1 = ToGraphX(Bounds.Width), vy1 = ToGraphY(Bounds.Height);
        Point a = Map(Math.Clamp(vx0, minX, maxX), Math.Clamp(vy0, minY, maxY));
        Point b = Map(Math.Clamp(vx1, minX, maxX), Math.Clamp(vy1, minY, maxY));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.Gold), 1), new Rect(a, b));
    }

    private void NavigateFromMinimap(Point click, Rect mm)
    {
        double minX = _nodes.Min(n => n.X);
        double minY = _nodes.Min(n => n.Y);
        double maxX = _nodes.Max(n => n.X + n.Width);
        double maxY = _nodes.Max(n => n.Y + n.Height);
        double gw = Math.Max(1, maxX - minX);
        double gh = Math.Max(1, maxY - minY);
        double s = Math.Min((mm.Width - 8) / gw, (mm.Height - 8) / gh);
        _panX = minX + ((click.X - mm.X - 4) / s);
        _panY = minY + ((click.Y - mm.Y - 4) / s);
        InvalidateVisual();
    }

    // ---- Geometry helper ----

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double wx = p.X - a.X, wy = p.Y - a.Y;
        double c1 = (vx * wx) + (vy * wy);
        if (c1 <= 0)
        {
            return Math.Sqrt((wx * wx) + (wy * wy));
        }

        double c2 = (vx * vx) + (vy * vy);
        if (c2 <= c1)
        {
            double ex = p.X - b.X, ey = p.Y - b.Y;
            return Math.Sqrt((ex * ex) + (ey * ey));
        }

        double t = c1 / c2;
        double px = a.X + (t * vx), py = a.Y + (t * vy);
        double dx = p.X - px, dy = p.Y - py;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
