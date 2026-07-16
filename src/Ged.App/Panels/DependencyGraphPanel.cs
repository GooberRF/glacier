using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Controls;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;
using Ged.Core.Packaging;
using Ged.Core.Packaging.Graph;

namespace Ged.App.Panels;

/// <summary>
/// The Dependency Graph panel: visualises what the level relies on (fed by the
/// <see cref="DependencyScanner"/>) as a level → category → file node graph on the
/// shared <see cref="GraphCanvas"/>. Indirect deps (mesh material textures, ATX
/// frames) nest under their parent as child edges; Included / BaseGameSkipped /
/// Missing files are colour-coded with badges and category counts. Selecting a file
/// lists its referencers ("why is this included") with jump-to buttons. Per-node
/// include checkboxes bind to the same <see cref="PackfileBuildPlan"/> the packfile
/// dialog uses; "Create Packfile…" hands that plan (with the graph's include state)
/// to the dialog. Refresh re-scans.
/// </summary>
internal sealed class DependencyGraphPanel : UserControl
{
    private readonly GraphCanvas _canvas = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 4), Opacity = 0.75, FontSize = 12 };
    private readonly StackPanel _referers = new() { Margin = new Thickness(6) };
    private readonly TextBlock _refHeader = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 6, 6, 2), TextWrapping = TextWrapping.Wrap };
    private readonly Button _createPack = new() { Content = "Create Packfile…", FontSize = 12, Padding = new Thickness(8, 3) };

    // Item 6: live filename filter, mirroring the Link Graph's UID/search box.
    private readonly TextBox _filter = new() { Watermark = "filter filename", Width = 180, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private string? _fileFilter;

    private IEditorHost? _host;
    private EditorDocument? _boundDoc;
    private DependencyScanResult? _scan;
    private DependencyGraph? _graph;
    private PackfileBuildPlan? _plan;
    private readonly Dictionary<int, GraphCanvasNode> _nodeByKey = new();
    private readonly Dictionary<int, DependencyGraphNode> _modelByKey = new();

    // Per-session collapse state (item 2): categories whose file subtree is hidden.
    private readonly HashSet<DependencyCategory> _collapsed = new();
    private bool _busy;

    public DependencyGraphPanel()
    {
        _canvas.NodeClicked = (key, _) => ShowReferers(key);
        _canvas.NodeDoubleClicked = OnNodeDoubleClicked;
        _canvas.CheckboxToggled = OnCheckboxToggled;
        _canvas.ChevronToggled = OnChevronToggled;
        _canvas.BackgroundClicked = () => ShowReferers(-1);

        _createPack.Click += (_, _) => _ = CreatePackfileAsync();
        _filter.TextChanged += (_, _) =>
        {
            _fileFilter = _filter.Text;
            if (_scan is not null)
            {
                Rebuild(refit: false);
            }
        };

        var toolbar = BuildToolbar();
        var right = BuildReferencersPanel();

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_status, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(_status);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right);
        root.Children.Add(right);
        root.Children.Add(_canvas);
        Content = root;
    }

    public void Bind(IEditorHost host) => _host = host;

    /// <summary>Called by the shell on document/structural change: re-scans on a new document, else re-renders.</summary>
    public void Refresh()
    {
        EditorDocument? doc = _host?.Document;
        if (doc is null)
        {
            _boundDoc = null;
            _scan = null;
            _graph = null;
            _plan = null;
            _canvas.SetGraph(Array.Empty<GraphCanvasNode>(), Array.Empty<GraphCanvasEdge>());
            _status.Text = "No level open.";
            return;
        }

        if (!ReferenceEquals(doc, _boundDoc))
        {
            _boundDoc = doc;
            _scan = null;
            _ = RescanAsync(); // auto-scan once for a newly opened document
            return;
        }

        if (_scan is not null)
        {
            Rebuild(refit: false);
        }
    }

    private async System.Threading.Tasks.Task RescanAsync()
    {
        if (_host is null || _busy)
        {
            return;
        }

        _busy = true;
        _status.Text = "Scanning level dependencies…";
        try
        {
            DependencyScanResult? scan = await _host.ScanDependenciesAsync();
            if (scan is null)
            {
                _status.Text = "Mount an RF install to scan dependencies (Settings → install path).";
                _canvas.SetGraph(Array.Empty<GraphCanvasNode>(), Array.Empty<GraphCanvasEdge>());
                return;
            }

            _scan = scan;
            _plan = _host.CreatePackfilePlan(scan);
            Rebuild(refit: true);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- Graph build + layout ----

    private void Rebuild(bool refit)
    {
        if (_scan is null || _host is null)
        {
            return;
        }

        _graph = DependencyGraphModel.Build(_scan, _host.LevelLabel);

        // Item 2: hide the file subtree of any collapsed category. The category node
        // stays (with a count badge); only the layout/render see the reduced graph.
        DependencyGraph visible = _graph.Collapse(_collapsed);

        // Item 6: narrow to files whose name matches the live filter (their categories / level
        // ancestors are kept as anchors; unrelated categories drop out).
        visible = FilterByFileName(visible, _fileFilter);
        var itemByDep = _plan?.AllItems.ToDictionary(i => i.Dependency) ?? new Dictionary<PackDependency, PackfileBuildItem>();

        // Shared layered layout engine (core-testable): Level → Category → File left
        // to right, real node sizes, no overlaps.
        IReadOnlyDictionary<int, GraphNodePos> pos = DependencyGraphLayout.Build(visible);

        _nodeByKey.Clear();
        _modelByKey.Clear();
        var nodes = new List<GraphCanvasNode>();
        foreach (DependencyGraphNode m in visible.Nodes)
        {
            (double x, double y) = pos.TryGetValue(m.Id, out GraphNodePos p) ? (p.X, p.Y) : (0, 0);
            bool isCategory = m.NodeKind == DependencyNodeKind.Category;
            bool collapsed = isCategory && m.Category is { } cat0 && _collapsed.Contains(cat0);
            var cn = new GraphCanvasNode
            {
                Key = m.Id,
                Tag = m,
                X = x,
                Y = y,
                Width = m.NodeKind == DependencyNodeKind.File ? 190 : 168,
                Title = TitleOf(m),
                Subtitle = SubtitleOf(m),
                Fill = FillOf(m),
                Dimmed = m.Status == DependencyStatus.BaseGameSkipped,
                Collapsible = isCategory,
                Collapsed = collapsed,
                Badge = collapsed ? m.Total.ToString(System.Globalization.CultureInfo.InvariantCulture) : BadgeOf(m),
                BadgeColor = (collapsed && m.MissingCount > 0) || m.Status == DependencyStatus.Missing ? Colors.IndianRed : Colors.SlateGray,
                Checkbox = CheckboxOf(m, itemByDep),
                Tooltip = TooltipOf(m),
            };
            nodes.Add(cn);
            _nodeByKey[m.Id] = cn;
            _modelByKey[m.Id] = m;
        }

        var edges = visible.Edges.Select(e => new GraphCanvasEdge
        {
            FromKey = e.FromId,
            ToKey = e.ToId,
            Dashed = e.Nested,
            Color = e.Nested ? Color.FromRgb(0x9A, 0x9A, 0x6A) : Color.FromRgb(0x6A, 0x8A, 0xB0),
            Tooltip = e.Nested ? "indirect dependency" : null,
        }).ToList();

        _canvas.SetGraph(nodes, edges, refit);
        UpdateStatus();
    }

    // ---- Interaction ----

    private void OnCheckboxToggled(int key, bool value)
    {
        if (_plan is null || !_modelByKey.TryGetValue(key, out DependencyGraphNode? m) || m.Dependency is null)
        {
            return;
        }

        PackfileBuildItem? item = _plan.AllItems.FirstOrDefault(i => ReferenceEquals(i.Dependency, m.Dependency));
        if (item is null || !item.CanInclude)
        {
            return;
        }

        item.Include = value;
        if (_nodeByKey.TryGetValue(key, out GraphCanvasNode? cn))
        {
            cn.Checkbox = value;
        }

        _canvas.Redraw();
        UpdateStatus();
    }

    private void OnNodeDoubleClicked(int key)
    {
        // Double-clicking a category header toggles its collapse (item 2).
        if (_modelByKey.TryGetValue(key, out DependencyGraphNode? m) &&
            m.NodeKind == DependencyNodeKind.Category && m.Category is { } cat)
        {
            ToggleCategory(cat);
            return;
        }

        // Otherwise jump to the first referencer with a UID.
        if (m?.Dependency is { } dep &&
            dep.Referers.FirstOrDefault(r => r.Uid is not null) is { Uid: int uid } &&
            _host?.Document?.FindByUid(uid) is { } o)
        {
            _host.FrameObject(o);
        }
    }

    private void OnChevronToggled(int key)
    {
        if (_modelByKey.TryGetValue(key, out DependencyGraphNode? m) &&
            m.NodeKind == DependencyNodeKind.Category && m.Category is { } cat)
        {
            ToggleCategory(cat);
        }
    }

    private void ToggleCategory(DependencyCategory cat)
    {
        if (!_collapsed.Remove(cat))
        {
            _collapsed.Add(cat);
        }

        Rebuild(refit: false);
    }

    private void ShowReferers(int key)
    {
        _referers.Children.Clear();
        if (key < 0 || !_modelByKey.TryGetValue(key, out DependencyGraphNode? m))
        {
            _refHeader.Text = "Select a file to see why it's included.";
            return;
        }

        if (m.NodeKind != DependencyNodeKind.File || m.Dependency is not { } dep)
        {
            _refHeader.Text = m.NodeKind == DependencyNodeKind.Category
                ? $"{m.Label}: {m.Total} file(s) — {m.IncludedCount} included, {m.SkippedCount} base-game, {m.MissingCount} missing."
                : $"{m.Label}";
            return;
        }

        _refHeader.Text = $"Why is “{dep.FileName}” included?\n{StatusText(dep.Status)}" +
            (dep.SourceDescription is { } s ? $"\nfrom {s}" : string.Empty);

        if (dep.Referers.Count == 0)
        {
            _referers.Children.Add(new TextBlock { Text = "(no recorded referencers)", Opacity = 0.6, FontSize = 12 });
            return;
        }

        foreach (DependencyReferer r in dep.Referers)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1) };
            if (r.Uid is int uid && _host?.Document?.FindByUid(uid) is not null)
            {
                var jump = new Button { Content = "Jump", FontSize = 11, Padding = new Thickness(6, 1), Margin = new Thickness(4, 0, 0, 0) };
                jump.Click += (_, _) =>
                {
                    if (_host?.Document?.FindByUid(uid) is { } o)
                    {
                        _host.Selection.SelectObject(o);
                        _host.RefreshSelectionOverlay();
                        _host.FrameObject(o);
                    }
                };
                DockPanel.SetDock(jump, Avalonia.Controls.Dock.Right);
                row.Children.Add(jump);
            }

            row.Children.Add(new TextBlock
            {
                Text = r.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _referers.Children.Add(row);
        }
    }

    private async System.Threading.Tasks.Task CreatePackfileAsync()
    {
        if (_host is null)
        {
            return;
        }

        if (_plan is null)
        {
            _host.Dispatcher.ShowMessage("Scan dependencies first (Refresh).");
            return;
        }

        await _host.OpenPackfileAsync(_plan);
    }

    // ---- Node presentation ----

    private static string TitleOf(DependencyGraphNode m) => m.NodeKind switch
    {
        DependencyNodeKind.Level => m.Label,
        DependencyNodeKind.Category => $"{m.Label}  ({m.Total})",
        _ => m.Label,
    };

    private static string SubtitleOf(DependencyGraphNode m) => m.NodeKind switch
    {
        DependencyNodeKind.Level => "level",
        DependencyNodeKind.Category => $"{m.IncludedCount} inc · {m.SkippedCount} base · {m.MissingCount} miss",
        _ => m.Dependency is { } d ? $"{d.Kind}{(d.Size > 0 ? $"  ·  {FormatSize(d.Size)}" : string.Empty)}" : string.Empty,
    };

    private static string? BadgeOf(DependencyGraphNode m) => m.Status switch
    {
        DependencyStatus.Missing => "MISSING",
        DependencyStatus.BaseGameSkipped => "base",
        _ => null,
    };

    private static bool? CheckboxOf(DependencyGraphNode m, IReadOnlyDictionary<PackDependency, PackfileBuildItem> items)
    {
        if (m.NodeKind != DependencyNodeKind.File || m.Dependency is null)
        {
            return null;
        }

        return items.TryGetValue(m.Dependency, out PackfileBuildItem? item) && item.CanInclude ? item.Include : null;
    }

    private static Color FillOf(DependencyGraphNode m) => m.NodeKind switch
    {
        DependencyNodeKind.Level => Color.FromRgb(0x3A, 0x4A, 0x6A),
        DependencyNodeKind.Category => Color.FromRgb(0x30, 0x34, 0x40),
        _ => m.Status switch
        {
            DependencyStatus.Included => Color.FromRgb(0x2A, 0x4C, 0x38),
            DependencyStatus.BaseGameSkipped => Color.FromRgb(0x36, 0x38, 0x3E),
            DependencyStatus.Missing => Color.FromRgb(0x66, 0x2A, 0x2A),
            _ => Color.FromRgb(0x3A, 0x3E, 0x46),
        },
    };

    private static string TooltipOf(DependencyGraphNode m)
    {
        if (m.Dependency is { } d)
        {
            return $"{d.FileName}\n{d.Kind} · {StatusText(d.Status)}\n{d.Referers.Count} referencer(s)";
        }

        return m.NodeKind == DependencyNodeKind.Category
            ? $"{m.Label}: {m.Total} direct file(s)"
            : m.Label;
    }

    private static string StatusText(DependencyStatus s) => s switch
    {
        DependencyStatus.Included => "included (packed)",
        DependencyStatus.BaseGameSkipped => "base-game (engine ships it)",
        DependencyStatus.Missing => "MISSING",
        _ => s.ToString(),
    };

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";

    private void UpdateStatus()
    {
        if (_scan is null)
        {
            return;
        }

        _status.Text = $"{_scan.All.Count} deps — {_scan.Included.Count} included, " +
            $"{_scan.BaseGameSkipped.Count} base-game, {_scan.Missing.Count} missing" +
            (_plan is { } p ? $"  |  packing {p.SelectedCount} ({FormatSize(p.SelectedSize)})" : string.Empty);
    }

    // ---- Chrome ----

    private Control BuildToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 4) };
        row.Children.Add(Btn("Refresh", () => _ = RescanAsync()));
        row.Children.Add(Btn("Fit", () => _canvas.FitToView()));
        row.Children.Add(Btn("Include All", () => SetAllIncludes(true)));
        row.Children.Add(Btn("Exclude All", () => SetAllIncludes(false)));
        row.Children.Add(_createPack);
        var minimap = new CheckBox { Content = "Minimap", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        minimap.IsCheckedChanged += (_, _) => _canvas.ShowMinimap = minimap.IsChecked == true;
        row.Children.Add(minimap);
        row.Children.Add(_filter);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2A)), Child = row };
    }

    private Control BuildReferencersPanel()
    {
        var content = new StackPanel();
        content.Children.Add(_refHeader);
        content.Children.Add(_referers);
        _refHeader.Text = "Select a file to see why it's included.";
        return new Border
        {
            Width = 250,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x32, 0x38)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = content },
        };
    }

    private void SetAllIncludes(bool include)
    {
        if (_plan is null)
        {
            return;
        }

        foreach (PackfileBuildItem item in _plan.AllItems.Where(i => i.CanInclude))
        {
            item.Include = include;
        }

        Rebuild(refit: false);
    }

    private static Button Btn(string text, Action action)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3) };
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>
    /// Item 6: the subgraph whose file nodes' names contain <paramref name="query"/>
    /// (case-insensitive substring). Matching files keep their whole ancestor chain — parent
    /// file (for nested deps), category, level — so they stay connected and anchored; categories
    /// with no surviving file drop out. A blank query returns the graph unchanged. Framework-free
    /// so it is unit-tested at the model level.
    /// </summary>
    internal static DependencyGraph FilterByFileName(DependencyGraph graph, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return graph;
        }

        string q = query.Trim();

        // Incoming adjacency: node -> parents (category → file, file → nested file).
        var parents = new Dictionary<int, List<int>>();
        foreach (DependencyGraphEdge e in graph.Edges)
        {
            if (!parents.TryGetValue(e.ToId, out List<int>? list))
            {
                parents[e.ToId] = list = new List<int>();
            }

            list.Add(e.FromId);
        }

        // Seed with matching files, then walk up keeping every ancestor.
        var kept = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (DependencyGraphNode n in graph.Nodes)
        {
            if (n.NodeKind == DependencyNodeKind.File &&
                n.Label.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                kept.Add(n.Id))
            {
                queue.Enqueue(n.Id);
            }
        }

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (parents.TryGetValue(cur, out List<int>? ps))
            {
                foreach (int p in ps)
                {
                    if (kept.Add(p))
                    {
                        queue.Enqueue(p);
                    }
                }
            }
        }

        // Always keep the level root as an anchor, even when nothing matches.
        foreach (DependencyGraphNode n in graph.Nodes)
        {
            if (n.NodeKind == DependencyNodeKind.Level)
            {
                kept.Add(n.Id);
            }
        }

        var nodes = graph.Nodes.Where(n => kept.Contains(n.Id)).ToList();
        var edges = graph.Edges.Where(e => kept.Contains(e.FromId) && kept.Contains(e.ToId)).ToList();
        return new DependencyGraph(nodes, edges);
    }
}
