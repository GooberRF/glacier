using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using AvDock = Avalonia.Controls.Dock;

namespace Ged.App.Panels;

/// <summary>
/// The Layers panel (item 9): a brush build-order / time outliner. One row per brush in
/// build order showing its absolute layer number, UID, property + visibility icons, and
/// per-row up/down nudge. A toolbar reorders (Start/End of time) and toggles Lock/Hide on
/// the selection; two independent filter sets (properties OR'd, visibility OR'd, AND'd
/// together) affect display only. Selection is synced both ways with the viewport; rows
/// drag to reorder (multi-select as a contiguous block).
/// </summary>
internal sealed class LayersPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private readonly System.Collections.Generic.Dictionary<int, Border> _rowByUid = new();
    private readonly ScrollViewer _scroll;
    private IEditorHost? _host;
    private BrushEditor? _bound;

    private LayerSolidity _solidity = LayerSolidity.All;
    private LayerProps _props = LayerProps.All;
    private LayerVis _vis = LayerVis.All;

    private int? _dragFromUid;

    public LayersPanel()
    {
        _scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var root = new DockPanel();
        Control toolbar = BuildToolbar();
        Control filters = BuildFilters();
        DockPanel.SetDock(toolbar, AvDock.Top);
        DockPanel.SetDock(filters, AvDock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(filters);
        root.Children.Add(_scroll);
        Content = root;
    }

    public void Bind(IEditorHost host)
    {
        _host = host;
        Rebind();
        Refresh();
    }

    private void Rebind()
    {
        if (_bound is { } old)
        {
            old.BrushesChanged -= Refresh;
            old.SelectionChanged -= SyncSelectionHighlight;
            old.VisibilityChanged -= Refresh;
        }

        _bound = _host?.BrushEditor;
        if (_bound is { } be)
        {
            be.BrushesChanged += Refresh;
            // A pure selection change updates only the existing rows' highlight — it must NOT run
            // the full Refresh (which clears + rebuilds every row Border). Rebuilding on the first
            // press of a double-click detached the gesture-tracking Border, so the second press
            // landed on a fresh element and DoubleTapped never fired — which is why the camera jump
            // only worked for LOCKED brushes (their lock-refused select never fired SelectionChanged,
            // so their row survived). Structural (BrushesChanged) and visibility changes still Refresh.
            be.SelectionChanged += SyncSelectionHighlight;
            be.VisibilityChanged += Refresh;
        }
    }

    public void Refresh()
    {
        if (_host?.BrushEditor != _bound)
        {
            Rebind();
        }

        _rows.Children.Clear();
        _rowByUid.Clear();
        if (_bound is not { } be)
        {
            return;
        }

        var hidden = new HashSet<int>(be.HiddenBrushes);
        IReadOnlyList<LayerRow> rows = LayersModel.BuildRows(be.Brushes.ToList(), hidden);
        var selected = new HashSet<int>(be.SelectedBrushes);
        Control? firstSelected = null;

        foreach (LayerRow row in rows)
        {
            if (!LayersModel.Passes(row, _solidity, _props, _vis))
            {
                continue;
            }

            var r = (Border)BuildRow(row, selected.Contains(row.Uid));
            _rows.Children.Add(r);
            _rowByUid[row.Uid] = r;
            if (firstSelected is null && selected.Contains(row.Uid))
            {
                firstSelected = r;
            }
        }

        // Scroll the (first) selected row into view (view → rows sync). The rows were just
        // rebuilt, so their layout bounds are still stale (all zero) this frame — calling
        // BringIntoView now would resolve to the top. Defer it one layout pass so it scrolls
        // the actual row instead of resetting the list to the top.
        if (firstSelected is { } target)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => target.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Item 8: a pure selection change updates only the existing rows' highlight background
    /// instead of rebuilding the whole list — clicking a brush in a big level's Layers panel
    /// no longer rebuilds hundreds of rows. Falls back to a full <see cref="Refresh"/> if the
    /// bound editor changed (the row set may be stale).
    /// </summary>
    public void SyncSelectionHighlight()
    {
        if (_bound is not { } be || _host?.BrushEditor != _bound)
        {
            Refresh();
            return;
        }

        var selected = new HashSet<int>(be.SelectedBrushes);
        Border? firstSelected = null;
        foreach ((int uid, Border row) in _rowByUid)
        {
            bool sel = selected.Contains(uid);
            row.Background = sel ? new SolidColorBrush(Color.FromArgb(80, 90, 140, 255)) : Brushes.Transparent;
            if (sel && firstSelected is null)
            {
                firstSelected = row;
            }
        }

        if (firstSelected is { } target)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => target.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Ensures a row shows the panel highlight even when its document selection was lock-refused
    /// (B6): the Layers-panel row highlight must always reflect the row the user clicked,
    /// independent of the lock-gated document selection. For an unlocked brush this is redundant
    /// with the selection-driven <see cref="SyncSelectionHighlight"/> (which re-runs on the
    /// resulting SelectionChanged); for a LOCKED brush — whose select is refused and so fires no
    /// SelectionChanged — it is the only path, and it stays until the next real selection change
    /// (which re-derives every row's highlight from the document selection).
    /// </summary>
    private void HighlightRow(int uid)
    {
        if (_rowByUid.TryGetValue(uid, out Border? row))
        {
            row.Background = new SolidColorBrush(Color.FromArgb(80, 90, 140, 255));
        }
    }

    // ---- Toolbar + filters ----------------------------------------------------

    private Control BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(4, 3) };
        panel.Children.Add(TbBtn("⏮ Start", "Move selected to start of time", () => Apply(be => be.MoveToStartOfTime(Sel(be)), rebuild: true)));
        panel.Children.Add(TbBtn("⏭ End", "Move selected to end of time", () => Apply(be => be.MoveToEndOfTime(Sel(be)), rebuild: true)));
        panel.Children.Add(TbBtn("🔒 Lock", "Lock selected brushes", () => Apply(be => be.SetBrushLocked(Sel(be), true), rebuild: true)));
        // Unlock is "unlock ALL" by design: a locked brush is unselectable, so it can never be in
        // Sel(be) — an "unlock selected" would be a permanent no-op for the very brushes that need
        // unlocking (incl. file-locked ones like ctf06 UID 414). UnlockAll clears every brush's
        // persisted lock state (undoable, dirties the file).
        panel.Children.Add(TbBtn("🔓 Unlock All", "Unlock all locked brushes", () => Apply(be => be.UnlockAll(), rebuild: true)));
        panel.Children.Add(TbBtn("🙈 Hide", "Hide selected brushes", () => Apply(be => be.SetBrushHidden(Sel(be), true), rebuild: true)));
        panel.Children.Add(TbBtn("👁 Show", "Show all brushes", () => Apply(be => be.SetBrushHidden(be.Brushes.Select(b => b.Uid).ToList(), false), rebuild: true)));
        return panel;
    }

    private Control BuildFilters()
    {
        // Three independent filter groups (OR within a group, AND between): Solidity {Air, Solid},
        // Properties {Detail, Portal, Geoable, Breakable}, Visibility {Normal, Locked, Hidden}.
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(4, 0, 4, 3) };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Solid:", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 },
                SolidityCheck("Air", LayerSolidity.Air), SolidityCheck("Solid", LayerSolidity.Solid),
            },
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Props:", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 },
                PropCheck("Detail", LayerProps.Detail), PropCheck("Portal", LayerProps.Portal),
                PropCheck("Geo", LayerProps.Geoable), PropCheck("Break", LayerProps.Breakable),
            },
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Vis:", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 },
                VisCheck("Normal", LayerVis.Normal), VisCheck("Locked", LayerVis.Locked), VisCheck("Hidden", LayerVis.Hidden),
            },
        });
        return panel;
    }

    private CheckBox SolidityCheck(string label, LayerSolidity flag)
    {
        var cb = new CheckBox { Content = label, FontSize = 10, IsChecked = (_solidity & flag) != 0, MinWidth = 0 };
        cb.IsCheckedChanged += (_, _) => { _solidity = cb.IsChecked == true ? _solidity | flag : _solidity & ~flag; Refresh(); };
        return cb;
    }

    private CheckBox PropCheck(string label, LayerProps flag)
    {
        var cb = new CheckBox { Content = label, FontSize = 10, IsChecked = (_props & flag) != 0, MinWidth = 0 };
        cb.IsCheckedChanged += (_, _) => { _props = cb.IsChecked == true ? _props | flag : _props & ~flag; Refresh(); };
        return cb;
    }

    private CheckBox VisCheck(string label, LayerVis flag)
    {
        var cb = new CheckBox { Content = label, FontSize = 10, IsChecked = (_vis & flag) != 0, MinWidth = 0 };
        cb.IsCheckedChanged += (_, _) => { _vis = cb.IsChecked == true ? _vis | flag : _vis & ~flag; Refresh(); };
        return cb;
    }

    // ---- Rows -----------------------------------------------------------------

    private Control BuildRow(LayerRow row, bool selected)
    {
        // No layer-number column: the row's position in the panel conveys build/time order.
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Height = 20 };
        panel.Children.Add(new TextBlock { Text = $"#{row.Uid}", Width = 46, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        panel.Children.Add(Badges(row));

        var up = MiniBtn("▲", "Nudge up (one position earlier)", () => Nudge(row.Uid, -1));
        var down = MiniBtn("▼", "Nudge down (one position later)", () => Nudge(row.Uid, +1));

        // Build-order time index ("t=X"), the number RED shows in its console for a selected
        // brush. Reinstated at the RIGHT (next to the nudge arrows); recomputed live
        // on reorder from the row's build position. Placed immediately left of the arrows.
        var time = new TextBlock
        {
            Text = $"t={row.TimeIndex}",
            FontSize = 10,
            Opacity = 0.6,
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            [ToolTip.TipProperty] = "Build-order time index (RED's t=X)",
        };
        var spacer = new Control { Width = 6 };
        panel.Children.Add(spacer);
        panel.Children.Add(time);
        panel.Children.Add(up);
        panel.Children.Add(down);

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(3, 0),
            Background = selected ? new SolidColorBrush(Color.FromArgb(80, 90, 140, 255)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // Selection sync (rows → view) + block drag.
        border.PointerPressed += (_, e) =>
            HandleRowPressed(row.Uid, (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0);
        border.PointerReleased += (_, _) =>
        {
            if (_dragFromUid is int fromUid && fromUid != row.Uid)
            {
                DropOn(row.Uid);
            }

            _dragFromUid = null;
        };

        // Double-click jumps the perspective camera to (frames) the brush — same mechanism as
        // the Outliner double-click / Jump To.
        border.DoubleTapped += (_, _) => HandleRowDoubleTapped(row.Uid);
        return border;
    }

    /// <summary>
    /// A single row press: document-select the brush (lock-gated, may be refused with a hint) and
    /// ALWAYS highlight the panel row (B6 — the row highlight is independent of the lock-gated
    /// document selection, so a locked row still shows which one you clicked).
    /// </summary>
    private void HandleRowPressed(int uid, bool additive)
    {
        _host?.Selection.SelectBrush(uid, additive);
        _host?.RefreshSelectionOverlay();
        HighlightRow(uid);
        _dragFromUid = uid;
    }

    /// <summary>
    /// A row double-click: jump (frame) the camera to the brush. This ALWAYS works, locked or not
    /// (B6) — jumping to a locked brush is legitimate, and the second press no longer lands on a
    /// rebuilt row (the selection change now only re-highlights, never tears the rows down).
    /// </summary>
    private void HandleRowDoubleTapped(int uid)
    {
        _dragFromUid = null; // the second press must not be read as a drag-start
        _host?.Selection.SelectBrush(uid);
        _host?.RefreshSelectionOverlay();
        HighlightRow(uid);
        _host?.FrameBrush(uid);
    }

    // ---- Test hooks (headless panel-handler coverage; mirrors OutlinerPanel's *ForTest) --------

    internal Border? RowBorderForTest(int uid) => _rowByUid.TryGetValue(uid, out Border? b) ? b : null;

    internal bool RowHighlightedForTest(int uid) =>
        _rowByUid.TryGetValue(uid, out Border? b) && b.Background is SolidColorBrush { Color.A: not 0 };

    internal void PressRowForTest(int uid, bool additive = false) => HandleRowPressed(uid, additive);

    internal void DoubleTapRowForTest(int uid) => HandleRowDoubleTapped(uid);

    private static Control Badges(LayerRow row)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Width = 150 };
        void Badge(bool on, string glyph, string tip, Color color)
        {
            if (on)
            {
                p.Children.Add(new Border
                {
                    Background = new SolidColorBrush(color),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 0),
                    Child = new TextBlock { Text = glyph, FontSize = 9, Foreground = Brushes.Black },
                    [ToolTip.TipProperty] = tip,
                });
            }
        }

        Badge(row.Air, "A", "Air", Color.FromRgb(120, 200, 255));
        Badge(row.Solid, "S", "Solid", Color.FromRgb(200, 200, 200));
        Badge(row.Detail, "D", "Detail", Color.FromRgb(120, 255, 120));
        Badge(row.Portal, "P", "Portal", Color.FromRgb(255, 255, 120));
        Badge(row.Geoable, "G", "Geoable", Color.FromRgb(255, 180, 120));
        Badge(row.Breakable, "B", "Breakable", Color.FromRgb(255, 140, 140));
        Badge(row.Locked, "🔒", "Locked", Color.FromRgb(200, 200, 160));
        Badge(row.Hidden, "🙈", "Hidden", Color.FromRgb(180, 180, 180));
        return p;
    }

    // ---- Operations -----------------------------------------------------------

    private void DropOn(int targetUid)
    {
        if (_bound is not { } be)
        {
            return;
        }

        List<int> order = be.Brushes.Select(b => b.Uid).ToList();
        var selected = be.SelectedBrushes.Count > 0 ? be.SelectedBrushes.ToList() : new List<int> { _dragFromUid ?? targetUid };
        int dropIndex = order.IndexOf(targetUid);
        if (dropIndex < 0)
        {
            return;
        }

        List<int> newOrder = LayersModel.MoveBlock(order, selected, dropIndex);
        be.ReorderTo(newOrder);
        _host?.RequestSceneRebuild();
    }

    private void Nudge(int uid, int delta)
    {
        if (_bound is not { } be)
        {
            return;
        }

        List<int> order = be.Brushes.Select(b => b.Uid).ToList();
        be.ReorderTo(LayersModel.Nudge(order, uid, delta));
        _host?.RequestSceneRebuild();
    }

    private void Apply(Action<BrushEditor> op, bool rebuild)
    {
        if (_bound is not { } be)
        {
            return;
        }

        op(be);
        if (rebuild)
        {
            _host?.RequestSceneRebuild();
        }

        Refresh();
    }

    private static IReadOnlyCollection<int> Sel(BrushEditor be) => be.SelectedBrushes.ToList();

    private static Button TbBtn(string content, string tip, Action click)
    {
        var b = new Button { Content = content, FontSize = 11, Padding = new Thickness(5, 2), [ToolTip.TipProperty] = tip };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button MiniBtn(string content, string tip, Action click)
    {
        var b = new Button
        {
            Content = content, FontSize = 9, Padding = new Thickness(3, 0), MinWidth = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, [ToolTip.TipProperty] = tip,
        };
        b.Click += (_, _) => click();
        return b;
    }
}
