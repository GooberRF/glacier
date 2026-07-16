using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Controls;
using Ged.Core.Editing;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;

namespace Ged.App.Panels;

/// <summary>
/// Link Graph 2.0 — an interactive node-graph editor over the level's links. Nodes
/// (triggers / events / movers / targets / others) are pan/zoom-navigable on a
/// shared <see cref="GraphCanvas"/>, draggable (positions persist per-level in a
/// <c>&lt;level&gt;.gedlayout.json</c> sidecar), and edge-editable: drag a node's
/// output port to another node to create a validated link, click an edge and press
/// Delete to break it. A Show-All toggle, a kind-filter row, and a UID/script/class
/// search box filter the view; double-click jumps the camera. All edits route
/// through <see cref="LinkGraphEditor"/> (undo-safe, validated).
/// </summary>
internal sealed class LinkGraphPanel : UserControl
{
    private readonly GraphCanvas _canvas = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 4), Opacity = 0.75, FontSize = 12 };
    private readonly CheckBox _showAll = new() { Content = "Show All", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Watermark = "search uid / script / class", Width = 190, FontSize = 12 };
    private readonly Dictionary<GraphNodeCategory, CheckBox> _kindBoxes = new();
    private readonly LinkGraphFilter _filter = new();

    private IEditorHost? _host;
    private LinkGraphEditor? _editor;
    private GraphLayout _layout = new();
    private string? _boundPath;
    private EditorDocument? _boundDoc;
    private bool _suppress;
    private int _nodeCount;
    private int _edgeCount;

    public LinkGraphPanel()
    {
        _showAll.IsCheckedChanged += (_, _) => { _filter.ShowAll = _showAll.IsChecked == true; Refresh(); };
        _search.TextChanged += (_, _) => { _filter.Search = _search.Text; Refresh(); };

        _canvas.NodeClicked = OnNodeClicked;
        _canvas.NodeDoubleClicked = OnNodeDoubleClicked;
        _canvas.BoxSelected = OnBoxSelected;
        _canvas.NodesMoved = PersistLayout;
        _canvas.EdgeSelected = _ => UpdateStatus();
        _canvas.ValidatePort = (from, to) => _editor?.ValidateDrop(from, to).Ok ?? false;
        _canvas.PortDropped = OnPortDropped;
        _canvas.BackgroundClicked = () => { };
        _canvas.EdgeDeleteRequested = BreakSelectedEdge;

        var root = new DockPanel();
        Control toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_status, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_canvas);
        Content = root;
    }

    public void Bind(IEditorHost host) => _host = host;

    public void Refresh()
    {
        if (_suppress)
        {
            return;
        }

        EditorDocument? doc = _host?.Document;
        if (doc is null)
        {
            _editor = null;
            _canvas.SetGraph(Array.Empty<GraphCanvasNode>(), Array.Empty<GraphCanvasEdge>());
            _status.Text = "No level open.";
            return;
        }

        // Rebind (and reload the layout sidecar) when the document or its path changes.
        if (!ReferenceEquals(doc, _boundDoc) || !string.Equals(doc.Path, _boundPath, StringComparison.OrdinalIgnoreCase))
        {
            _boundDoc = doc;
            _boundPath = doc.Path;
            _editor = new LinkGraphEditor(doc);
            _layout = _boundPath is { } p ? GraphLayoutStore.Load(GraphLayoutStore.SidecarPathFor(p)) : new GraphLayout();
        }

        _editor ??= new LinkGraphEditor(doc);

        _filter.SelectionUids = doc.Selection.Select(s => s.Uid).ToList();
        LinkGraph graph = _editor.Build(_filter);

        // Auto-place any node that has no saved position (absent sidecar → full layout).
        GraphAutoLayout.Apply(graph, _layout, relayoutAll: false);

        var selectedUids = new HashSet<int>(doc.Selection.Select(s => s.Uid));
        var nodes = new List<GraphCanvasNode>();
        foreach (LinkGraphNode n in graph.Nodes)
        {
            _layout.TryGet(n.Uid, out double x, out double y);
            nodes.Add(new GraphCanvasNode
            {
                Key = n.Uid,
                Tag = n,
                X = x,
                Y = y,
                Title = n.DisplayName,
                Subtitle = n.Missing ? "missing link target" : $"{n.Kind}  ·  uid {n.Uid}",
                Fill = ColorFor(n.Kind, n.Missing),
                Selected = selectedUids.Contains(n.Uid),
                HasPort = n.CanOriginate,
                Badge = n.Missing ? "MISSING" : null,
                BadgeColor = Colors.IndianRed,
                Tooltip = n.Missing ? $"Broken link: no object with UID {n.Uid}" : $"{n.DisplayName}\n{n.Kind} · uid {n.Uid}\n{n.ClassName}",
            });
        }

        var edges = graph.Edges.Select(e => new GraphCanvasEdge
        {
            FromKey = e.From,
            ToKey = e.To,
            Tag = e,
            Tooltip = EdgeTooltip(graph, e),
        }).ToList();

        _canvas.SetGraph(nodes, edges);
        _nodeCount = graph.Nodes.Count;
        _edgeCount = graph.Edges.Count;
        UpdateStatus();
    }

    // ---- Canvas callbacks ----

    private void OnNodeClicked(int uid, bool additive)
    {
        if (_host?.Document is not { } doc || doc.FindByUid(uid) is not { } o)
        {
            return;
        }

        _suppress = true;
        _host.Selection.SelectObject(o, additive);
        _suppress = false;
        _host.RefreshSelectionOverlay();
        Refresh();
    }

    private void OnNodeDoubleClicked(int uid)
    {
        if (_host?.Document?.FindByUid(uid) is { } o)
        {
            _host.FrameObject(o);
        }
    }

    private void OnBoxSelected(IReadOnlyList<int> uids, bool additive)
    {
        if (_host?.Document is not { } doc)
        {
            return;
        }

        var objs = uids.Select(doc.FindByUid).OfType<LevelObject>().ToList();
        if (objs.Count == 0)
        {
            return;
        }

        _suppress = true;
        _host.Selection.SelectObjects(objs, additive);
        _suppress = false;
        _host.RefreshSelectionOverlay();
        Refresh();
    }

    private void OnPortDropped(int from, int to)
    {
        if (_editor is null)
        {
            return;
        }

        LinkResult r = _editor.CreateLink(from, to);
        if (!r.Ok)
        {
            _host?.Dispatcher.ShowMessage(r.Message);
        }

        // A successful create fires LinksChanged → Refresh; refresh anyway for the refusal path.
        Refresh();
    }

    private void BreakSelectedEdge()
    {
        if (_editor is null || _canvas.SelectedEdge?.Tag is not LinkGraphEdge e)
        {
            return;
        }

        if (_editor.BreakLink(e.From, e.To))
        {
            _host?.Dispatcher.ShowMessage($"Broke link {e.From} → {e.To}.");
        }

        Refresh();
    }

    private void PersistLayout()
    {
        foreach (GraphCanvasNode n in _canvas.Nodes)
        {
            _layout.Set(n.Key, n.X, n.Y);
        }

        SaveSidecar();
    }

    private void SaveSidecar()
    {
        if (_boundPath is { } path)
        {
            try
            {
                GraphLayoutStore.Save(_layout, GraphLayoutStore.SidecarPathFor(path));
            }
            catch (Exception)
            {
                // Non-fatal: an unwritable sidecar just means positions aren't remembered.
            }
        }
    }

    // ---- Toolbar ----

    private Control BuildToolbar()
    {
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 4, 6, 2) };
        row1.Children.Add(_showAll);
        row1.Children.Add(Btn("Fit", () => _canvas.FitToView()));
        row1.Children.Add(Btn("Auto-Layout", () => RunLayout(relayoutAll: false)));
        row1.Children.Add(Btn("Re-layout All", () => RunLayout(relayoutAll: true)));
        row1.Children.Add(Btn("Break Link", BreakSelectedEdge));
        var minimap = new CheckBox { Content = "Minimap", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        minimap.IsCheckedChanged += (_, _) => _canvas.ShowMinimap = minimap.IsChecked == true;
        row1.Children.Add(minimap);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0, 6, 4) };
        row2.Children.Add(new TextBlock { Text = "Kinds:", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, FontSize = 12 });
        foreach ((GraphNodeCategory cat, string label) in new[]
        {
            (GraphNodeCategory.Trigger, "Triggers"),
            (GraphNodeCategory.Event, "Events"),
            (GraphNodeCategory.Mover, "Movers"),
            (GraphNodeCategory.Target, "Targets"),
            (GraphNodeCategory.Other, "Others"),
        })
        {
            var cb = new CheckBox { Content = label, IsChecked = true, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            GraphNodeCategory captured = cat;
            cb.IsCheckedChanged += (_, _) => OnKindToggled(captured, cb.IsChecked == true);
            _kindBoxes[cat] = cb;
            row2.Children.Add(cb);
        }

        row2.Children.Add(_search);

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2A)), Child = panel };
    }

    private void OnKindToggled(GraphNodeCategory cat, bool on)
    {
        // Empty Categories set = all shown; populate it from the boxes and drop when full.
        _filter.Categories.Clear();
        foreach (KeyValuePair<GraphNodeCategory, CheckBox> kv in _kindBoxes)
        {
            if (kv.Value.IsChecked == true)
            {
                _filter.Categories.Add(kv.Key);
            }
        }

        if (_filter.Categories.Count == _kindBoxes.Count)
        {
            _filter.Categories.Clear(); // all on = no filter
        }

        Refresh();
    }

    private void RunLayout(bool relayoutAll)
    {
        if (_editor is null)
        {
            return;
        }

        _filter.SelectionUids = _host?.Document?.Selection.Select(s => s.Uid).ToList() ?? new List<int>();
        LinkGraph graph = _editor.Build(_filter);
        GraphAutoLayout.Apply(graph, _layout, relayoutAll);
        SaveSidecar();
        Refresh();
    }

    private void UpdateStatus()
    {
        string sel = _canvas.SelectedEdge?.Tag is LinkGraphEdge e ? $"  |  edge {e.From} → {e.To} selected (Del to break)" : string.Empty;
        _status.Text = $"{_nodeCount} nodes, {_edgeCount} links" +
            (_filter.ShowAll ? " (all)" : " (selection component)") +
            "  |  drag a port to link, click an edge + Del to break" + sel;
    }

    private static string EdgeTooltip(LinkGraph graph, LinkGraphEdge e)
    {
        LinkGraphNode? a = graph.Node(e.From);
        LinkGraphNode? b = graph.Node(e.To);
        return $"{a?.DisplayName ?? e.From.ToString()} ({a?.Kind}) → {b?.DisplayName ?? e.To.ToString()} ({b?.Kind})";
    }

    private static Color ColorFor(LevelObjectKind? kind, bool missing) => missing ? Color.FromRgb(0x60, 0x30, 0x30) : kind switch
    {
        LevelObjectKind.Trigger => Color.FromRgb(0x3A, 0x5A, 0x8A),
        LevelObjectKind.Event => Color.FromRgb(0x6A, 0x3A, 0x7A),
        LevelObjectKind.Mover => Color.FromRgb(0x2A, 0x6A, 0x4A),
        LevelObjectKind.Target => Color.FromRgb(0x7A, 0x5A, 0x2A),
        LevelObjectKind.Clutter => Color.FromRgb(0x5A, 0x4A, 0x3A),
        LevelObjectKind.NavPoint => Color.FromRgb(0x4A, 0x5A, 0x2A),
        _ => Color.FromRgb(0x3A, 0x3E, 0x46),
    };

    private static Button Btn(string text, Action action)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3) };
        b.Click += (_, _) => action();
        return b;
    }
}
