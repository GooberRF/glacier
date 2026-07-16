using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ged.Core.Editor;
using AvDock = Avalonia.Controls.Dock;

namespace Ged.App.Panels;

/// <summary>
/// A type-grouped tree of every level object with live counts, an instant filter,
/// per-type and per-item visibility (eye) and lock (padlock) toggles, double-click
/// to select+frame, and a right-click menu (Jump To / View From / Select All of
/// Type). This generalizes stock RED's Select/Hide dialogs into a modern panel.
///
/// UX stability: hide/unhide and lock/unlock never rebuild the tree — they refresh
/// only the affected row(s) in place (icon glyph + label opacity), so selection,
/// group expansion and scroll position are untouched. When a rebuild is genuinely
/// required (an external <see cref="EditorDocument"/> event: objects added/removed,
/// rename, an out-of-panel hide, etc.) it runs through <see cref="Refresh"/>, which
/// captures and restores selection (by UID), expansion (by node key) and the scroll
/// offset across the rebuild.
/// </summary>
internal sealed class OutlinerPanel : UserControl
{
    private readonly TextBox _filter = new() { Watermark = "Filter…", Margin = new Avalonia.Thickness(4), FontSize = 12 };
    private readonly TreeView _tree = new() { Margin = new Avalonia.Thickness(2) };
    private IEditorHost? _host;

    // Row lookup for in-place refresh and restore-by-UID. A single object can appear
    // in more than one node (its kind group AND a prefab-instance node), so every row
    // for a UID is tracked; _groupOfUid links each object row to its kind group's eye.
    private readonly Dictionary<int, List<TreeViewItem>> _rowsByUid = new();
    private readonly Dictionary<int, GroupRef> _groupOfUid = new();

    // Re-entrancy guard: while WE mutate the document from a row toggle, a document
    // event (e.g. ToggleLock -> VisibilityChanged) would otherwise call back into
    // Refresh() and rebuild the tree — undoing our in-place update. Suppress that so
    // the toggle stays a pure in-place refresh.
    private bool _suppressRefresh;

    private sealed record GroupRef(Button Eye, IReadOnlyList<LevelObject> Items);

    public OutlinerPanel()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_filter, AvDock.Top);
        root.Children.Add(_filter);
        root.Children.Add(_tree);
        Content = root;

        _filter.GetObservable(TextBox.TextProperty).Subscribe(new AnonymousObserver(_ => Rebuild()));
    }

    public void Bind(IEditorHost host)
    {
        _host = host;
        Rebuild();
    }

    /// <summary>External refresh (document events). Rebuilds the tree but preserves the
    /// user's selection, expansion and scroll; suppressed while a row toggle is applying
    /// its own in-place update.</summary>
    public void Refresh()
    {
        if (_suppressRefresh)
        {
            return;
        }

        OutlinerState state = CaptureState();
        Rebuild();
        RestoreState(state);
    }

    private void Rebuild()
    {
        EditorDocument? doc = _host?.Document;
        _tree.Items.Clear();
        _rowsByUid.Clear();
        _groupOfUid.Clear();
        if (doc is null)
        {
            return;
        }

        string filter = _filter.Text?.Trim() ?? string.Empty;
        AppendPrefabInstances(doc, filter);
        var byKind = doc.Objects
            .Where(o => string.IsNullOrEmpty(filter) ||
                        o.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        o.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(o => o.Kind)
            .OrderBy(g => g.Key.ToString());

        foreach (IGrouping<LevelObjectKind, LevelObject> group in byKind)
        {
            var items = group.ToList();
            var groupItem = new TreeViewItem
            {
                Tag = group.Key,
                IsExpanded = !string.IsNullOrEmpty(filter),
            };
            groupItem.Header = GroupHeader(doc, group.Key, items, out Button groupEye);
            var gref = new GroupRef(groupEye, items);

            foreach (LevelObject o in items.OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                TreeViewItem node = ItemNode(doc, o);
                groupItem.Items.Add(node);
                RegisterRow(o.Uid, node);
                _groupOfUid[o.Uid] = gref;
            }

            _tree.Items.Add(groupItem);
        }

        AppendAnnotations(doc, filter);
    }

    private void RegisterRow(int uid, TreeViewItem node)
    {
        if (!_rowsByUid.TryGetValue(uid, out List<TreeViewItem>? rows))
        {
            rows = new List<TreeViewItem>();
            _rowsByUid[uid] = rows;
        }

        rows.Add(node);
    }

    /// <summary>
    /// Item 1: a top-level "Prefab Instances" section grouping each placed instance's object
    /// members under a prefab node (a "modified" instance is badged). Selecting the node selects
    /// every member; the context menu offers Orphan + Select All Members.
    /// </summary>
    private void AppendPrefabInstances(EditorDocument doc, string filter)
    {
        if (_host?.PrefabInstances is not { HasInstances: true } svc)
        {
            return;
        }

        var instances = svc.Instances
            .Where(r => string.IsNullOrEmpty(filter) || r.PrefabName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (instances.Count == 0)
        {
            return;
        }

        var section = new TreeViewItem
        {
            Header = new TextBlock { Text = $"Prefab Instances ({instances.Count})", FontWeight = FontWeight.SemiBold, FontSize = 12 },
            IsExpanded = true,
            Tag = "sec:prefabs",
        };

        foreach (Ged.Core.Model.PrefabInstanceRecord rec in instances)
        {
            int id = rec.InstanceId;
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            header.Children.Add(new TextBlock { Text = "🧩", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock
            {
                Text = $"{rec.PrefabName}  ({rec.MemberUids.Count})" + (rec.Modified ? "  • modified" : string.Empty),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = rec.Modified ? new SolidColorBrush(Color.FromRgb(255, 190, 90)) : Brushes.Gray,
            });

            var node = new TreeViewItem { Header = header, IsExpanded = false, Tag = "prefab:" + id };
            foreach (int uid in rec.MemberUids)
            {
                if (doc.FindByUid(uid) is { } member)
                {
                    TreeViewItem memberNode = ItemNode(doc, member);
                    node.Items.Add(memberNode);
                    RegisterRow(member.Uid, memberNode);
                }
            }

            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Select All Members", () => _host?.SelectPrefabInstanceMembers(id)));
            menu.Items.Add(MenuItem("Orphan Instance", () => _host?.OrphanPrefabInstance(id)));
            node.ContextMenu = menu;
            node.DoubleTapped += (_, e) => { _host?.SelectPrefabInstanceMembers(id); e.Handled = true; };
            section.Items.Add(node);
        }

        _tree.Items.Add(section);
    }

    /// <summary>The B7 "Annotations" section: each measurement (label + distance), select →
    /// highlight/frame, Delete removes it (undoable).</summary>
    private void AppendAnnotations(EditorDocument doc, string filter)
    {
        var anns = doc.Annotations
            .Where(a => string.IsNullOrEmpty(filter) || a.EffectiveLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (anns.Count == 0)
        {
            return;
        }

        var section = new TreeViewItem
        {
            Header = new TextBlock { Text = $"Annotations ({anns.Count})", FontWeight = FontWeight.SemiBold, FontSize = 12 },
            IsExpanded = true,
            Tag = "sec:annotations",
        };

        foreach (Ged.Core.Editing.Annotation a in anns)
        {
            int id = a.Id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = "📏", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock
            {
                Text = $"{a.EffectiveLabel}  ({a.Distance:0.##} m)",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = _host?.SelectedAnnotationId == id ? 1.0 : 0.85,
                FontWeight = _host?.SelectedAnnotationId == id ? FontWeight.Bold : FontWeight.Normal,
            });

            var item = new TreeViewItem { Header = row, Tag = a };
            item.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    _host?.SelectAnnotation(id);
                }
            };
            item.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete)
                {
                    _host?.DeleteAnnotation(id);
                    e.Handled = true;
                }
            };
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Select", () => _host?.SelectAnnotation(id)));
            menu.Items.Add(MenuItem("Delete", () => _host?.DeleteAnnotation(id)));
            item.ContextMenu = menu;
            section.Items.Add(item);
        }

        _tree.Items.Add(section);
    }

    private Control GroupHeader(EditorDocument doc, LevelObjectKind kind, List<LevelObject> items, out Button eye)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{kind} ({items.Count})",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        });

        bool allHidden = items.All(o => o.Hidden);
        eye = IconToggle(allHidden ? "◌" : "◉", "Show/hide all of type");
        eye.Click += (_, _) => ToggleGroupHidden(doc, items);
        panel.Children.Add(eye);

        var selectAll = IconToggle("⊙", "Select all of type");
        selectAll.Click += (_, _) => { _host?.Selection.SelectAllOfKind(kind); _host?.RefreshSelectionOverlay(); };
        panel.Children.Add(selectAll);
        return panel;
    }

    private TreeViewItem ItemNode(EditorDocument doc, LevelObject o)
    {
        var item = new TreeViewItem { Header = ItemHeader(doc, o), Tag = o };
        item.DoubleTapped += (_, _) =>
        {
            _host?.Selection.SelectObject(o);
            _host?.RefreshSelectionOverlay();
            _host?.FrameObject(o);
        };
        item.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                bool additive = (e.KeyModifiers & KeyModifiers.Control) != 0;
                _host?.Selection.SelectObject(o, additive);
                _host?.RefreshSelectionOverlay();
            }
        };
        item.ContextMenu = BuildContextMenu(doc, o);
        return item;
    }

    private Control ItemHeader(EditorDocument doc, LevelObject o)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var eye = IconToggle(o.Hidden ? "◌" : "◉", "Show/hide");
        eye.Click += (_, _) => ToggleHiddenRow(doc, o);
        panel.Children.Add(eye);

        var padlock = IconToggle(doc.IsLocked(o) ? "🔒" : "🔓", "Lock/unlock");
        padlock.Click += (_, _) => ToggleLockRow(doc, o);
        panel.Children.Add(padlock);

        panel.Children.Add(new TextBlock
        {
            Text = o.DisplayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            [ToolTip.TipProperty] = o.DisplayName,
            Opacity = o.Hidden ? 0.45 : 1.0,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"#{o.Uid}",
            FontSize = 10,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    // ---- in-place state toggles (no tree rebuild) -----------------------------

    /// <summary>Hide/unhide one object: applies the (undoable) flag and refreshes only that
    /// object's row(s) in place — selection/expansion/scroll are never disturbed.</summary>
    private void ToggleHiddenRow(EditorDocument doc, LevelObject o)
    {
        _suppressRefresh = true;
        try
        {
            doc.EditValue(o.Section, "Toggle hidden", o.Hidden, !o.Hidden, v => o.Hidden = v);
        }
        finally
        {
            _suppressRefresh = false;
        }

        RefreshRow(doc, o);
        _host?.RequestSceneRebuild();
    }

    /// <summary>Lock/unlock one object (session state). <see cref="EditorDocument.ToggleLock"/>
    /// fires VisibilityChanged; the guard keeps the resulting Refresh from rebuilding, and we
    /// refresh the row(s) in place instead.</summary>
    private void ToggleLockRow(EditorDocument doc, LevelObject o)
    {
        _suppressRefresh = true;
        try
        {
            doc.ToggleLock(o);
        }
        finally
        {
            _suppressRefresh = false;
        }

        RefreshRow(doc, o);
    }

    /// <summary>Show/hide every object of a type: one undo transaction, then in-place row
    /// refreshes — the group stays open, selection and scroll are preserved.</summary>
    private void ToggleGroupHidden(EditorDocument doc, IReadOnlyList<LevelObject> items)
    {
        bool target = !items.All(o => o.Hidden);
        _suppressRefresh = true;
        try
        {
            using (doc.Undo.BeginTransaction("Toggle type visibility"))
            {
                foreach (LevelObject o in items)
                {
                    doc.EditValue(o.Section, "hide", o.Hidden, target, v => o.Hidden = v);
                }
            }
        }
        finally
        {
            _suppressRefresh = false;
        }

        foreach (LevelObject o in items)
        {
            RefreshRow(doc, o);
        }

        _host?.RequestSceneRebuild();
    }

    /// <summary>Re-renders every row for an object (its group node and any prefab-member node)
    /// and refreshes its group's "all hidden" eye glyph — without touching the tree structure,
    /// so the TreeViewItem containers (and thus selection/expansion) persist.</summary>
    private void RefreshRow(EditorDocument doc, LevelObject o)
    {
        if (_rowsByUid.TryGetValue(o.Uid, out List<TreeViewItem>? rows))
        {
            foreach (TreeViewItem row in rows)
            {
                row.Header = ItemHeader(doc, o);
            }
        }

        if (_groupOfUid.TryGetValue(o.Uid, out GroupRef? g))
        {
            g.Eye.Content = g.Items.All(x => x.Hidden) ? "◌" : "◉";
        }
    }

    // ---- rebuild with state preservation --------------------------------------

    private sealed record OutlinerState(int? SelectedUid, HashSet<string> Expanded, Vector Scroll);

    private OutlinerState CaptureState()
    {
        TreeViewItem? sel = EnumerateItems(_tree).FirstOrDefault(it => it.IsSelected);
        int? selUid = sel?.Tag is LevelObject lo ? lo.Uid : null;

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (TreeViewItem it in EnumerateItems(_tree))
        {
            if (it.IsExpanded && ExpandKey(it) is { } key)
            {
                expanded.Add(key);
            }
        }

        Vector scroll = FindScrollViewer()?.Offset ?? default;
        return new OutlinerState(selUid, expanded, scroll);
    }

    private void RestoreState(OutlinerState s)
    {
        foreach (TreeViewItem it in EnumerateItems(_tree))
        {
            if (ExpandKey(it) is { } key)
            {
                it.IsExpanded = s.Expanded.Contains(key);
            }
        }

        if (s.SelectedUid is { } uid && _rowsByUid.TryGetValue(uid, out List<TreeViewItem>? rows) && rows.Count > 0)
        {
            rows[0].IsSelected = true;
        }

        if (FindScrollViewer() is { } sv)
        {
            sv.Offset = s.Scroll;
        }
    }

    /// <summary>A stable key for an expandable node so expansion survives a rebuild: kind groups
    /// key on their <see cref="LevelObjectKind"/>, section/prefab nodes on their string Tag. Object
    /// and annotation leaf rows return null (not expansion-tracked).</summary>
    private static string? ExpandKey(TreeViewItem it) => it.Tag switch
    {
        LevelObjectKind k => "grp:" + k,
        string s => s,
        _ => null,
    };

    private static IEnumerable<TreeViewItem> EnumerateItems(ItemsControl root)
    {
        foreach (object? obj in root.Items)
        {
            if (obj is TreeViewItem it)
            {
                yield return it;
                foreach (TreeViewItem d in EnumerateItems(it))
                {
                    yield return d;
                }
            }
        }
    }

    private ScrollViewer? FindScrollViewer() => _tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    // ---- headless test hooks --------------------------------------------------

    internal void ToggleHiddenForTest(LevelObject o) => ToggleHiddenRow(_host!.Document!, o);

    internal void ToggleLockForTest(LevelObject o) => ToggleLockRow(_host!.Document!, o);

    internal void ToggleGroupHiddenForTest(LevelObjectKind kind)
    {
        if (_host?.Document is { } doc)
        {
            ToggleGroupHidden(doc, doc.Objects.Where(o => o.Kind == kind).ToList());
        }
    }

    internal void SelectRowForTest(int uid)
    {
        if (_rowsByUid.TryGetValue(uid, out List<TreeViewItem>? rows) && rows.Count > 0)
        {
            rows[0].IsSelected = true;
        }
    }

    /// <summary>The primary row container for a UID (identity changes across a rebuild).</summary>
    internal TreeViewItem? RowForTest(int uid) =>
        _rowsByUid.TryGetValue(uid, out List<TreeViewItem>? rows) && rows.Count > 0 ? rows[0] : null;

    internal int? SelectedRowUidForTest =>
        EnumerateItems(_tree).FirstOrDefault(it => it.IsSelected)?.Tag is LevelObject lo ? lo.Uid : null;

    internal void SetGroupExpandedForTest(LevelObjectKind kind, bool expanded)
    {
        foreach (TreeViewItem it in _tree.Items.OfType<TreeViewItem>())
        {
            if (it.Tag is LevelObjectKind k && k == kind)
            {
                it.IsExpanded = expanded;
            }
        }
    }

    internal bool IsGroupExpandedForTest(LevelObjectKind kind) =>
        _tree.Items.OfType<TreeViewItem>().FirstOrDefault(it => it.Tag is LevelObjectKind k && k == kind)?.IsExpanded ?? false;

    internal bool RowHiddenGlyphForTest(int uid)
    {
        // The row's eye glyph reads "hidden" (◌) when the object is hidden.
        if (_rowsByUid.TryGetValue(uid, out List<TreeViewItem>? rows) && rows.Count > 0 &&
            rows[0].Header is StackPanel p && p.Children.Count > 0 && p.Children[0] is Button eye)
        {
            return (eye.Content as string) == "◌";
        }

        return false;
    }

    private ContextMenu BuildContextMenu(EditorDocument doc, LevelObject o)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Jump To", () => { _host?.Selection.SelectObject(o); _host?.RefreshSelectionOverlay(); _host?.FrameObject(o); }));
        menu.Items.Add(MenuItem("View From", () => { _host?.Selection.SelectObject(o); _host?.ViewFromObject(o); }));
        menu.Items.Add(MenuItem("Select All of Type", () => { _host?.Selection.SelectAllOfKind(o.Kind); _host?.RefreshSelectionOverlay(); }));

        // Alpine "To Mesh Object" (gap item 3): convert a placed clutter/entity into a Mesh object,
        // inheriting destructibility + spawning child coronas/thrusters. Operates on the selection —
        // convert the whole selection if the clicked object is part of it, else just this one.
        if (o.Kind is LevelObjectKind.Clutter or LevelObjectKind.Entity)
        {
            menu.Items.Add(MenuItem("Convert to Mesh Object", () =>
            {
                if (_host is not { } h)
                {
                    return;
                }

                if (h.Document is { } d && !d.IsSelected(o))
                {
                    h.Selection.SelectObject(o);
                }

                h.Dispatcher.Invoke(Ged.Core.Input.CommandIds.ObjConvertToMesh);
            }));
        }

        return menu;
    }

    /// <summary>Reflects the document's selection into the tree highlight.</summary>
    public void SyncSelection()
    {
        // Selection is authoritative in the document; the tree simply re-renders
        // opacity/labels on Refresh, so nothing extra is required here.
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    private static Button IconToggle(string glyph, string tip) => new()
    {
        Content = glyph,
        FontSize = 12,
        Padding = new Avalonia.Thickness(3, 0),
        MinWidth = 0,
        Background = Brushes.Transparent,
        BorderThickness = new Avalonia.Thickness(0),
        [ToolTip.TipProperty] = tip,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>A minimal IObserver&lt;T&gt; adapter so we can subscribe to Avalonia observables inline.</summary>
internal sealed class AnonymousObserver : IObserver<string?>
{
    private readonly Action<string?> _onNext;

    public AnonymousObserver(Action<string?> onNext) => _onNext = onNext;

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(string? value) => _onNext(value);
}
