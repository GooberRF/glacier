using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Tables;

namespace Ged.App.Panels;

/// <summary>
/// The object-mode palette, split into five top-level tabs (item 1): <b>Objects</b>, a FLAT
/// alphabetically-sorted list of the placeable object types (each row preceded by its viewport
/// icon), plus the "Player Start (Move)" action (item 2); <b>Entities</b>, <b>Clutter</b> and
/// <b>Items</b>, each its own class palette with a small cached mesh-render preview box per class;
/// and <b>Events</b>, the category→class tree. Entities and Clutter reflect their table's
/// <c>$RFE Level1/Level2</c> subcategory hierarchy (Entities single-level: Ultor / Robots /
/// Vehicles / Creatures / Miners; Clutter multi-level); Items are a single flat level (the items
/// table carries no subcategories). Every level is alphabetically sorted. Single-click selects;
/// double-click / Enter / the Place button drops the selection at the camera.
/// </summary>
internal sealed class PalettePanel : UserControl
{
    private IEditorHost? _host;
    private readonly TabControl _tabs = new();
    private readonly MeshHoverPreview _hoverPreview = new();

    private readonly TextBox _objectSearch = new() { Watermark = "Search objects…", Margin = new Avalonia.Thickness(4) };
    private readonly TreeView _objectTree = new();
    private readonly Button _placeButton = new() { Content = "Place", IsEnabled = false, MinWidth = 80, Margin = new Avalonia.Thickness(4) };

    private readonly TextBox _entitySearch = new() { Watermark = "Search entities…", Margin = new Avalonia.Thickness(4) };
    private readonly TreeView _entityTree = new();
    private readonly Button _entityPlace = new() { Content = "Place", IsEnabled = false, MinWidth = 80, Margin = new Avalonia.Thickness(4) };

    private readonly TextBox _clutterSearch = new() { Watermark = "Search clutter…", Margin = new Avalonia.Thickness(4) };
    private readonly TreeView _clutterTree = new();
    private readonly Button _clutterPlace = new() { Content = "Place", IsEnabled = false, MinWidth = 80, Margin = new Avalonia.Thickness(4) };

    private readonly TextBox _itemSearch = new() { Watermark = "Search items…", Margin = new Avalonia.Thickness(4) };
    private readonly TreeView _itemTree = new();
    private readonly Button _itemPlace = new() { Content = "Place", IsEnabled = false, MinWidth = 80, Margin = new Avalonia.Thickness(4) };

    private readonly TextBox _eventSearch = new() { Watermark = "Search events…", Margin = new Avalonia.Thickness(4) };
    private readonly TreeView _eventTree = new();

    private const string PlaceHint = "Single-click selects · double-click / Enter / Place drops at the camera.";

    public PalettePanel()
    {
        var objectsTab = new TabItem { Header = "Objects", Content = BuildObjectsTab() };
        var entitiesTab = new TabItem { Header = "Entities", Content = BuildClassTab(_entitySearch, _entityTree, _entityPlace, LevelObjectKind.Entity) };
        var clutterTab = new TabItem { Header = "Clutter", Content = BuildClassTab(_clutterSearch, _clutterTree, _clutterPlace, LevelObjectKind.Clutter) };
        var itemsTab = new TabItem { Header = "Items", Content = BuildClassTab(_itemSearch, _itemTree, _itemPlace, LevelObjectKind.Item) };

        var eventsRoot = new DockPanel();
        DockPanel.SetDock(_eventSearch, Avalonia.Controls.Dock.Top);
        _eventSearch.TextChanged += (_, _) => BuildEventTree();
        eventsRoot.Children.Add(_eventSearch);
        eventsRoot.Children.Add(_eventTree);
        _eventTree.DoubleTapped += (_, _) => PlaceSelectedEvent();
        var eventsTab = new TabItem { Header = "Events", Content = eventsRoot };

        _tabs.Items.Add(objectsTab);
        _tabs.Items.Add(entitiesTab);
        _tabs.Items.Add(clutterTab);
        _tabs.Items.Add(itemsTab);
        _tabs.Items.Add(eventsTab);

        // The hover-preview popover lives in the panel's tree (it renders nothing inline until
        // opened) so it can anchor beside whichever class row's preview box the pointer is over.
        var root = new Grid();
        root.Children.Add(_tabs);
        root.Children.Add(_hoverPreview.Popup);
        Content = root;

        BuildObjectTree();
        BuildEntityTree();
        BuildClutterTree();
        BuildItemTree();
        BuildEventTree();
    }

    public void Bind(IEditorHost host)
    {
        _host = host;
        // The larger hover render reuses the host's cache-backed class-thumbnail loader; the
        // popover sizes its image to 384px, so the loader renders (and caches) at that size.
        _hoverPreview.RenderInto = host.LoadClassThumbnail;
        RefreshCatalogs();
    }

    /// <summary>
    /// Rebuilds the class-bearing tabs (Clutter / Items) and the object list from the host
    /// catalogs. The shell calls this whenever an RF install is mounted or the tables are
    /// reloaded — without it the class tabs, filled from catalogs that are empty until an
    /// install mounts, would come up empty and never recover.
    /// </summary>
    public void RefreshCatalogs()
    {
        BuildObjectTree();
        BuildEntityTree();
        BuildClutterTree();
        BuildItemTree();
    }

    /// <summary>Rebuilds every tab's rows so their icons re-resolve (called when the icon atlas / setting changes).</summary>
    public void RefreshIcons()
    {
        BuildObjectTree();
        BuildEntityTree();
        BuildClutterTree();
        BuildItemTree();
    }

    // ---- Objects tab ----------------------------------------------------------

    private Control BuildObjectsTab()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_objectSearch, Avalonia.Controls.Dock.Top);
        _objectSearch.TextChanged += (_, _) => BuildObjectTree();
        root.Children.Add(_objectSearch);
        root.Children.Add(BottomBar(_placeButton));
        WireClassTree(_objectTree, _placeButton);
        root.Children.Add(_objectTree); // fills the remaining space (no dead space)
        return root;
    }

    private void BuildObjectTree()
    {
        string filter = (_objectSearch.Text ?? string.Empty).Trim();
        bool filtering = filter.Length > 0;
        bool Match(string s) => !filtering || s.Contains(filter, StringComparison.OrdinalIgnoreCase);

        // A FLAT, alphabetically-sorted list of object types, each preceded by its viewport
        // icon. Entities, Clutter and Items are NOT here — they are their own top-level, class-
        // browsable tabs with mesh previews (item 1). A lone class-less "Entity" row here would
        // be redundant with (and less useful than) the Entities tab, so it is omitted for parity
        // with how Clutter / Items are excluded.
        var entries = new List<(string Key, TreeViewItem Node)>();
        foreach (PlaceableObjectType type in ObjectFactory.Palette)
        {
            if (type.Kind is LevelObjectKind.Clutter or LevelObjectKind.Item or LevelObjectKind.Entity)
            {
                continue;
            }

            if (Match(type.DisplayName))
            {
                LevelObjectKind kind = type.Kind;
                TreeViewItem node = IconLeaf(type.DisplayName, Services.PaletteIcons.TryFor(kind), () => _host?.PlaceFromPalette(kind, null));
                PlaceableDrag.WireSource(node, () => PlaceableDrag.Class(kind, null), onPress: () => _hoverPreview.Cancel()); // drag out into a viewport (item E)
                entries.Add((type.DisplayName, node));
            }
        }

        // "Player Start (Move)" (item 2 rename): the unique spawn's move action, sorted under P
        // in the flat list. (Mover / keyframe creation lives in the Tools panel in Group mode.)
        const string playerStart = "Player Start (Move)";
        if (Match(playerStart))
        {
            entries.Add((playerStart,
                IconLeaf(playerStart, Services.PaletteIcons.TryFor(LevelObjectKind.PlayerStart), () => _host?.MovePlayerStartHere())));
        }

        entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        _objectTree.ItemsSource = entries.Select(e => e.Node).ToList();
        _placeButton.IsEnabled = false;
    }

    // ---- Clutter / Items tabs -------------------------------------------------

    private Control BuildClassTab(TextBox search, TreeView tree, Button place, LevelObjectKind kind)
    {
        var root = new DockPanel();
        DockPanel.SetDock(search, Avalonia.Controls.Dock.Top);
        search.TextChanged += (_, _) => RebuildClassTab(kind);
        root.Children.Add(search);
        root.Children.Add(BottomBar(place));
        WireClassTree(tree, place);
        root.Children.Add(tree);
        return root;
    }

    /// <summary>Rebuilds the class tab for the given kind (Entity / Clutter / Item) after a search edit.</summary>
    private void RebuildClassTab(LevelObjectKind kind)
    {
        switch (kind)
        {
            case LevelObjectKind.Entity:
                BuildEntityTree();
                break;
            case LevelObjectKind.Clutter:
                BuildClutterTree();
                break;
            default:
                BuildItemTree();
                break;
        }
    }

    /// <summary>Rebuilds the Entities tab from the host's subcategory tree (RFE Level1/Level2 nesting).</summary>
    private void BuildEntityTree()
    {
        PaletteCategoryNode root = _host?.EntityCategoryTree() ?? PaletteCategoryNode.Empty;
        string filter = (_entitySearch.Text ?? string.Empty).Trim();
        List<TreeViewItem> nodes = BuildCategoryLevel(root, LevelObjectKind.Entity, filter);
        AddMountHintIfEmpty(nodes, filter, "No entities available — mount an RF install in Settings ▸ General.");
        _entityTree.ItemsSource = nodes;
        _entityPlace.IsEnabled = false;
    }

    /// <summary>Rebuilds the Clutter tab from the host's subcategory tree (RFE Level1/Level2 nesting).</summary>
    private void BuildClutterTree()
    {
        PaletteCategoryNode root = _host?.ClutterCategoryTree() ?? PaletteCategoryNode.Empty;
        string filter = (_clutterSearch.Text ?? string.Empty).Trim();
        List<TreeViewItem> nodes = BuildCategoryLevel(root, LevelObjectKind.Clutter, filter);
        AddMountHintIfEmpty(nodes, filter, "No clutter available — mount an RF install in Settings ▸ General.");
        _clutterTree.ItemsSource = nodes;
        _clutterPlace.IsEnabled = false;
    }

    /// <summary>Rebuilds the Items tab: a single flat level (the items table carries no subcategories).</summary>
    private void BuildItemTree()
    {
        IReadOnlyList<string> classes = _host?.ClassNamesFor(LevelObjectKind.Item) ?? Array.Empty<string>();
        PaletteCategoryNode root = PaletteCategoryTree.Build(
            classes.Select(c => (c, (IReadOnlyList<string>)Array.Empty<string>())));
        string filter = (_itemSearch.Text ?? string.Empty).Trim();
        List<TreeViewItem> nodes = BuildCategoryLevel(root, LevelObjectKind.Item, filter);
        AddMountHintIfEmpty(nodes, filter, "No items available — mount an RF install in Settings ▸ General.");
        _itemTree.ItemsSource = nodes;
        _itemPlace.IsEnabled = false;
    }

    /// <summary>
    /// When a class tab has NO content (and the user isn't just filtering everything out),
    /// shows a dim, non-placeable hint row instead of a silently blank tree — the class
    /// catalogs come from the mounted install's tables, so an empty tab virtually always
    /// means no RF install is mounted yet.
    /// </summary>
    private static void AddMountHintIfEmpty(List<TreeViewItem> nodes, string filter, string hint)
    {
        if (nodes.Count == 0 && filter.Length == 0)
        {
            nodes.Add(new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = hint,
                    Opacity = 0.6,
                    FontStyle = FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap,
                },
                Focusable = false,
            });
        }
    }

    /// <summary>The top-level tree rows for a category root: subcategory folders (alpha) then leaf classes (alpha).</summary>
    private List<TreeViewItem> BuildCategoryLevel(PaletteCategoryNode root, LevelObjectKind kind, string filter)
    {
        bool filtering = filter.Length > 0;
        bool Match(string s) => !filtering || s.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var nodes = new List<TreeViewItem>();
        foreach (PaletteCategoryNode sub in root.SubCategories)
        {
            if (BuildFolderNode(sub, kind, filtering, Match, ancestorMatched: false) is { } folder)
            {
                nodes.Add(folder);
            }
        }

        // Classes with no category tag sit at the root, alphabetically, alongside the folders.
        foreach (string cls in root.Classes)
        {
            if (Match(cls))
            {
                nodes.Add(ClassLeaf(kind, cls));
            }
        }

        return nodes;
    }

    /// <summary>
    /// One subcategory folder (recursively). Under a search filter a folder is included only when
    /// something inside it matches; a folder whose own name matches shows all its contents.
    /// </summary>
    private TreeViewItem? BuildFolderNode(PaletteCategoryNode node, LevelObjectKind kind, bool filtering, Func<string, bool> match, bool ancestorMatched)
    {
        bool selfMatched = ancestorMatched || match(node.Name);
        var children = new List<TreeViewItem>();

        foreach (PaletteCategoryNode sub in node.SubCategories)
        {
            if (BuildFolderNode(sub, kind, filtering, match, selfMatched) is { } childFolder)
            {
                children.Add(childFolder);
            }
        }

        foreach (string cls in node.Classes)
        {
            if (selfMatched || match(cls))
            {
                children.Add(ClassLeaf(kind, cls));
            }
        }

        if (children.Count == 0)
        {
            return null;
        }

        var folder = new TreeViewItem { Header = node.Name, IsExpanded = filtering };
        foreach (TreeViewItem child in children)
        {
            folder.Items.Add(child);
        }

        return folder;
    }

    /// <summary>A class row: a small cached mesh-render preview box (kind-glyph fallback) then the name.</summary>
    private TreeViewItem ClassLeaf(LevelObjectKind kind, string className)
    {
        var img = new Image
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            // Start from the kind glyph so the box is never blank; the mesh render (when the row
            // is realized and a VFS is mounted) replaces it, falling back here on failure.
            Source = Services.PaletteIcons.TryFor(kind),
        };
        // Hovering the box pops a larger mesh render beside the row (after a short dwell); leaving
        // it closes the popover. The popover never takes focus, so tree navigation is unaffected.
        img.PointerEntered += (_, _) => _hoverPreview.Schedule(img, kind, className);
        img.PointerExited += (_, _) => _hoverPreview.Cancel();
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { img, new TextBlock { Text = className, VerticalAlignment = VerticalAlignment.Center } },
        };
        var leaf = new TreeViewItem { Header = header, Tag = PlaceClass(kind, className) };
        leaf.AttachedToVisualTree += (_, _) => LoadThumbnail(kind, className, img);
        PlaceableDrag.WireSource(header, () => PlaceableDrag.Class(kind, className), onPress: () => _hoverPreview.Cancel()); // drag out into a viewport (item E)
        return leaf;
    }

    private Action PlaceClass(LevelObjectKind kind, string className) => () => _host?.PlaceFromPalette(kind, className);

    // ---- Shared row + wiring helpers ------------------------------------------

    /// <summary>The Place button + hint row docked to the bottom of a class/object tab.</summary>
    private static Control BottomBar(Button place)
    {
        var bottom = new DockPanel { Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(place, Avalonia.Controls.Dock.Right);
        bottom.Children.Add(place);
        bottom.Children.Add(new TextBlock
        {
            Text = PlaceHint,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4),
        });
        return bottom;
    }

    /// <summary>Wires a tree + Place button: selection arms Place; double-click / Enter / Place run the leaf.</summary>
    private void WireClassTree(TreeView tree, Button place)
    {
        tree.DoubleTapped += (_, _) => RunTag(tree);
        tree.SelectionChanged += (_, _) => place.IsEnabled = TagOf(tree) is not null;
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunTag(tree);
                e.Handled = true;
            }
        };
        place.Click += (_, _) => RunTag(tree);
    }

    private static Action? TagOf(TreeView tree) =>
        tree.SelectedItem is TreeViewItem { Tag: Action a } ? a : null;

    private static void RunTag(TreeView tree) => TagOf(tree)?.Invoke();

    /// <summary>A flat object row: the kind's viewport icon (when mapped) then its name.</summary>
    private static TreeViewItem IconLeaf(string label, IImage? icon, Action run) =>
        new() { Header = IconRow(icon, label, 18), Tag = run };

    /// <summary>A small square glyph followed by a label; the shared row for leaves and section titles.</summary>
    private static Control IconRow(IImage? icon, string label, double size)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (icon is null)
        {
            return text;
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Image
                {
                    Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 6, 0),
                },
                text,
            },
        };
    }

    /// <summary>
    /// Loads a leaf's mesh thumbnail, isolating every thumbnail failure from selection.
    /// A thumbnail is a cosmetic extra: it must never throw out of the tree pipeline.
    /// </summary>
    private void LoadThumbnail(LevelObjectKind kind, string? className, Image thumb)
    {
        try
        {
            _host?.LoadClassThumbnail(kind, className, thumb);
        }
        catch (Exception)
        {
            // Non-fatal: the tree works with or without a preview image.
        }
    }

    // ---- Events tab -----------------------------------------------------------

    private void BuildEventTree()
    {
        string filter = (_eventSearch.Text ?? string.Empty).Trim();
        var nodes = new List<TreeViewItem>();
        foreach (IGrouping<string, EventSchema> cat in EventSchemaCatalog.Placeable
                     .Where(e => filter.Length == 0 || e.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(e => e.Category))
        {
            var catNode = new TreeViewItem { Header = $"{cat.Key} ({cat.Count()})", IsExpanded = filter.Length > 0 };
            foreach (EventSchema schema in cat.OrderBy(e => e.ClassName))
            {
                var leaf = new TreeViewItem { Header = LabelFor(schema), Tag = schema };
                catNode.Items.Add(leaf);
            }

            nodes.Add(catNode);
        }

        _eventTree.ItemsSource = nodes;
    }

    private static string LabelFor(EventSchema s)
    {
        // Display label only — the Alpine grouping is already conveyed by the AF_* category
        // header, so no per-item suffix. Never derive the class_name from this (serialization
        // matches on EventSchema.ClassName, which this leaves untouched).
        string orient = s.HasOrientation ? " ↻" : string.Empty;
        return $"{s.ClassName}{orient}";
    }

    private void PlaceSelectedEvent()
    {
        if (SelectedSchema() is { } schema)
        {
            _host?.PlaceEventFromPalette(schema);
        }
    }

    private EventSchema? SelectedSchema() =>
        _eventTree.SelectedItem is TreeViewItem { Tag: EventSchema s } ? s : null;
}
