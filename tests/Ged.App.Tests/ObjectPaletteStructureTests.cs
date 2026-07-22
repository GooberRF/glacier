using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The five-tab object palette (item 1): an Objects tab (flat, alpha-sorted list of the placeable
/// kinds, each with its viewport icon, plus the renamed "Player Start (Move)" action — item 2);
/// dedicated Entities, Clutter and Items tabs whose class rows each carry a mesh-preview box.
/// Entities and Clutter reflect their table's RFE Level1/Level2 subcategory nesting (alpha at every
/// level; Entities single-level, Clutter multi-level) while Items are a single flat level. Place /
/// double-click both drop the selection with undo.
/// </summary>
public sealed class ObjectPaletteStructureTests
{
    private sealed class RecordingHost : IEditorHost
    {
        private readonly EditorSession _s;

        public RecordingHost(EditorSession s) => _s = s;

        public List<(LevelObjectKind Kind, string? Class)> Placed { get; } = new();

        public int MovePlayerStartCalls { get; private set; }

        public EditorDocument? Document => _s.Document;
        public BrushEditor? BrushEditor => _s.BrushEditor;
        public SelectionRouter Selection => _s.Selection;
        public CommandDispatcher Dispatcher => throw new NotImplementedException();
        public void RequestSceneRebuild() { }

        public void RequestHistoryJump(Ged.Core.Editor.UndoNode target) { }
        public void RefreshSelectionOverlay() { }
        public void FrameObject(LevelObject o) { }
        public void FrameBrush(int uid) { }
        public void FocusTextureTools() { }
        public void ArmTextureEyedropper(Action<string> onSampled) { }
        public void ViewFromObject(LevelObject o) { }
        public int? SelectedAnnotationId => null;
        public void SelectAnnotation(int? id) { }
        public void DeleteAnnotation(int id) { }
        public Vec3 PlacementPoint => new(1, 2, 3);
        public LinkService? Links => null;
        public PrefabInstanceService? PrefabInstances => null;
        public string? GetLightCookie(int lightUid) => null;
        public float GetLightCookieSharpness(int lightUid) => 1f;
        public void SetLightCookie(int lightUid, string? cookieFile) { }
        public void SetLightCookieSharpness(int lightUid, float sharpness) { }
        public Task<string?> PickCookieImageAsync() => Task.FromResult<string?>(null);
        public void OrphanPrefabInstance(int instanceId) { }
        public void SelectPrefabInstanceMembers(int instanceId) { }
        public void OnObjectPlaced(LevelObject? placed) { }

        public void PlaceFromPalette(LevelObjectKind kind, string? className)
        {
            Placed.Add((kind, className));
            _s.Document?.PlaceObject(kind, new Vec3(1, 2, 3), className);
        }

        public void MovePlayerStartHere() => MovePlayerStartCalls++;
        public void PlaceEventFromPalette(Ged.Core.Tables.EventSchema schema) { }

        public IReadOnlyList<string> ClassNamesFor(LevelObjectKind kind) => kind switch
        {
            // Deliberately unsorted, to prove the Items tab sorts (item 1e).
            LevelObjectKind.Item => new[] { "Medical_Kit", "First_Aid" },
            LevelObjectKind.Entity => new[] { "Miner", "Guard" },
            _ => Array.Empty<string>(),
        };

        // A representative clutter subcategory tree — multi-level nesting (Natural ▸ Plants/Rocks),
        // deliberately unsorted input so the builder's alpha ordering is exercised. "looseItem" has
        // no category, so it sits at the root alongside the folders.
        public PaletteCategoryNode ClutterCategoryTree() =>
            PaletteCategoryTree.Build(new (string, IReadOnlyList<string>)[]
            {
                ("officebookcase", new[] { "Furniture" }),
                ("crate", new[] { "Storage" }),
                ("barrel", new[] { "Storage" }),
                ("fern", new[] { "Natural", "Plants" }),
                ("cactus", new[] { "Natural", "Plants" }),
                ("boulder", new[] { "Natural", "Rocks" }),
                ("looseItem", Array.Empty<string>()),
            });

        // A representative entity subcategory tree — the entity table's single-level $RFE Level1
        // folders (Ultor / Miners / Creatures …), deliberately unsorted so the builder's alpha
        // ordering is exercised. "looseBot" has no category, so it sits at the root alongside
        // the folders. (The real catalog excludes $RFE Level1 "Ignore" entities before this point.)
        public PaletteCategoryNode EntityCategoryTree() =>
            PaletteCategoryTree.Build(new (string, IReadOnlyList<string>)[]
            {
                ("Guard", new[] { "Ultor" }),
                ("Elite", new[] { "Ultor" }),
                ("Rider", new[] { "Creatures" }),
                ("Baby_Rider", new[] { "Creatures" }),
                ("Driller", new[] { "Miners" }),
                ("looseBot", Array.Empty<string>()),
            });

        public bool PlaySoundPreview(string fileName) => false;
        public void StopSoundPreview() { }
        public IReadOnlyList<string> ClutterSkins(string className) => Array.Empty<string>();
        public void LoadClassThumbnail(LevelObjectKind kind, string? className, Image img) { }
        public string LevelLabel => "test";
        public Task<Ged.Core.Packaging.DependencyScanResult?> ScanDependenciesAsync() => Task.FromResult<Ged.Core.Packaging.DependencyScanResult?>(null);
        public Ged.Core.Packaging.PackfileBuildPlan? CreatePackfilePlan(Ged.Core.Packaging.DependencyScanResult scan) => null;
        public Task OpenPackfileAsync(Ged.Core.Packaging.PackfileBuildPlan plan) => Task.CompletedTask;
    }

    private static (PalettePanel Panel, RecordingHost Host, EditorSession Session) NewBoundPanel()
    {
        // GED's own drawn atlas, per-kind tinted (the default palette-icon state).
        PaletteIcons.Configure(null, false);
        var session = new EditorSession();
        session.NewLevel();
        var host = new RecordingHost(session);
        var panel = new PalettePanel();
        panel.Bind(host); // RefreshCatalogs pulls the class data into the tabs
        return (panel, host, session);
    }

    private static T Field<T>(PalettePanel panel, string name) =>
        (T)panel.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;

    private static TreeView Tree(PalettePanel panel, string field) => Field<TreeView>(panel, field);

    private static List<TreeViewItem> Roots(TreeView tree) =>
        ((IEnumerable)tree.ItemsSource!).Cast<TreeViewItem>().ToList();

    private static List<string> TabHeaders(PalettePanel panel) =>
        Field<TabControl>(panel, "_tabs").Items.Cast<TabItem>().Select(t => (string)t.Header!).ToList();

    private static string HeaderText(object? header) => header switch
    {
        string s => s,
        TextBlock tb => tb.Text ?? string.Empty,
        Panel p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty,
        _ => string.Empty,
    };

    private static Image? HeaderImage(object? header) =>
        header is Panel p ? p.Children.OfType<Image>().FirstOrDefault() : null;

    private static IEnumerable<TreeViewItem> Descend(TreeViewItem node)
    {
        yield return node;
        foreach (TreeViewItem child in node.Items.Cast<TreeViewItem>())
        {
            foreach (TreeViewItem d in Descend(child))
            {
                yield return d;
            }
        }
    }

    private static TreeViewItem Find(TreeView tree, string label) =>
        Roots(tree).SelectMany(Descend).First(n => HeaderText(n.Header) == label);

    // ---- Tab structure --------------------------------------------------------

    [AvaloniaFact]
    public void Palette_Has_Five_Top_Level_Tabs_Including_Entities_Clutter_And_Items()
    {
        var (panel, _, _) = NewBoundPanel();
        Assert.Equal(new[] { "Objects", "Entities", "Clutter", "Items", "Events" }, TabHeaders(panel));
    }

    [AvaloniaFact]
    public void Objects_List_Is_Flat_Alpha_Sorted_Without_Entities_Clutter_Or_Items()
    {
        var (panel, _, _) = NewBoundPanel();
        List<TreeViewItem> nodes = Roots(Tree(panel, "_objectTree"));

        var labels = nodes.Select(n => HeaderText(n.Header)).ToList();
        Assert.Equal(labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(), labels);

        // Entities, Clutter and Items are their own tabs now — no section (or leaf) for them here.
        Assert.DoesNotContain(labels, l => l is "Entity" or "Clutter" or "Items");
        // And nothing in the Objects list is expandable (all flat leaves).
        Assert.All(nodes, n => Assert.Empty(n.Items));
        Assert.Contains("Light", labels);
    }

    [AvaloniaFact]
    public void Player_Start_Move_Row_Is_Present_And_Sorts_Under_P()
    {
        var (panel, _, _) = NewBoundPanel();
        var labels = Roots(Tree(panel, "_objectTree")).Select(n => HeaderText(n.Header)).ToList();

        Assert.Contains("Player Start (Move)", labels);
        Assert.DoesNotContain("Move Player Start here", labels); // the old label is gone

        // Sorted under P: it falls between the last "P…"-or-earlier and the first later entry.
        int idx = labels.IndexOf("Player Start (Move)");
        if (idx > 0)
        {
            Assert.True(string.Compare(labels[idx - 1], "Player Start (Move)", StringComparison.OrdinalIgnoreCase) <= 0);
        }
    }

    [AvaloniaFact]
    public void No_Mover_Or_Keyframe_Entries_Remain()
    {
        var (panel, _, _) = NewBoundPanel();
        IEnumerable<string> allLabels = new[] { "_objectTree", "_entityTree", "_clutterTree", "_itemTree" }
            .SelectMany(f => Roots(Tree(panel, f)).SelectMany(Descend))
            .Select(n => HeaderText(n.Header));

        Assert.DoesNotContain(allLabels, l => l.Contains("Mover", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allLabels, l => l.Contains("Keyframe", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Every_Objects_Row_Has_A_Viewport_Icon()
    {
        var (panel, _, _) = NewBoundPanel();
        foreach (TreeViewItem leaf in Roots(Tree(panel, "_objectTree")))
        {
            Image? img = HeaderImage(leaf.Header);
            Assert.NotNull(img);
            Assert.NotNull(img!.Source);
        }
    }

    // ---- Clutter tab: subcategory nesting + previews --------------------------

    [AvaloniaFact]
    public void Clutter_Tab_Reflects_Subcategory_Nesting_Alpha_At_Every_Level()
    {
        var (panel, _, _) = NewBoundPanel();
        List<TreeViewItem> roots = Roots(Tree(panel, "_clutterTree"));

        // Top level: subcategory folders (alpha), then the uncategorized root class.
        var folders = roots.Where(n => n.Items.Count > 0).Select(n => HeaderText(n.Header)).ToList();
        Assert.Equal(new[] { "Furniture", "Natural", "Storage" }, folders);
        Assert.Contains(roots, n => HeaderText(n.Header) == "looseItem"); // no-category class at root

        // Natural nests two second-level folders (alpha): Plants, Rocks.
        TreeViewItem natural = roots.First(n => HeaderText(n.Header) == "Natural");
        Assert.Equal(new[] { "Plants", "Rocks" },
            natural.Items.Cast<TreeViewItem>().Select(n => HeaderText(n.Header)).ToList());

        // Natural ▸ Plants holds its classes, alpha-sorted.
        TreeViewItem plants = natural.Items.Cast<TreeViewItem>().First(n => HeaderText(n.Header) == "Plants");
        Assert.Equal(new[] { "cactus", "fern" },
            plants.Items.Cast<TreeViewItem>().Select(n => HeaderText(n.Header)).ToList());

        // Storage classes alpha-sorted too.
        TreeViewItem storage = roots.First(n => HeaderText(n.Header) == "Storage");
        Assert.Equal(new[] { "barrel", "crate" },
            storage.Items.Cast<TreeViewItem>().Select(n => HeaderText(n.Header)).ToList());
    }

    [AvaloniaFact]
    public void Clutter_Class_Rows_Have_A_Mesh_Preview_Box()
    {
        var (panel, _, _) = NewBoundPanel();
        // A leaf class (fern) sits under Natural ▸ Plants; it must carry a preview box now.
        TreeViewItem fern = Find(Tree(panel, "_clutterTree"), "fern");
        Image? box = HeaderImage(fern.Header);
        Assert.NotNull(box);
        Assert.NotNull(box!.Source);
    }

    // ---- Entities tab: subcategory nesting + previews -------------------------

    [AvaloniaFact]
    public void Entities_Tab_Reflects_Subcategory_Nesting_Alpha_At_Every_Level()
    {
        var (panel, _, _) = NewBoundPanel();
        List<TreeViewItem> roots = Roots(Tree(panel, "_entityTree"));

        // Top level: the single-level $RFE Level1 folders (alpha), then the uncategorized root class.
        var folders = roots.Where(n => n.Items.Count > 0).Select(n => HeaderText(n.Header)).ToList();
        Assert.Equal(new[] { "Creatures", "Miners", "Ultor" }, folders);
        Assert.Contains(roots, n => HeaderText(n.Header) == "looseBot"); // no-category entity at root

        // Creatures holds its classes, alpha-sorted.
        TreeViewItem creatures = roots.First(n => HeaderText(n.Header) == "Creatures");
        Assert.Equal(new[] { "Baby_Rider", "Rider" },
            creatures.Items.Cast<TreeViewItem>().Select(n => HeaderText(n.Header)).ToList());

        // Ultor classes alpha-sorted too.
        TreeViewItem ultor = roots.First(n => HeaderText(n.Header) == "Ultor");
        Assert.Equal(new[] { "Elite", "Guard" },
            ultor.Items.Cast<TreeViewItem>().Select(n => HeaderText(n.Header)).ToList());
    }

    [AvaloniaFact]
    public void Entity_Class_Rows_Have_A_Mesh_Preview_Box()
    {
        var (panel, _, _) = NewBoundPanel();
        // A leaf class (Guard) sits under Ultor; it must carry a preview box.
        TreeViewItem guard = Find(Tree(panel, "_entityTree"), "Guard");
        Image? box = HeaderImage(guard.Header);
        Assert.NotNull(box);
        Assert.NotNull(box!.Source);
    }

    [AvaloniaFact]
    public void Entity_Row_Places_The_Class_With_Undo()
    {
        var (panel, host, session) = NewBoundPanel();
        EditorDocument doc = session.Document!;
        TreeView tree = Tree(panel, "_entityTree");

        tree.SelectedItem = Find(tree, "Driller");
        Button place = Field<Button>(panel, "_entityPlace");
        Assert.True(place.IsEnabled);
        place.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Contains((LevelObjectKind.Entity, (string?)"Driller"), host.Placed);
        Assert.Equal(1, doc.Objects.Count(o => o.Kind == LevelObjectKind.Entity));
        doc.Undo.Undo();
        Assert.Equal(0, doc.Objects.Count(o => o.Kind == LevelObjectKind.Entity));
    }

    // ---- Items tab: flat + previews -------------------------------------------

    [AvaloniaFact]
    public void Items_Tab_Is_Flat_Alpha_With_Preview_Boxes()
    {
        var (panel, _, _) = NewBoundPanel();
        List<TreeViewItem> roots = Roots(Tree(panel, "_itemTree"));

        Assert.Equal(new[] { "First_Aid", "Medical_Kit" },
            roots.Select(n => HeaderText(n.Header)).ToList()); // alpha, flat
        Assert.All(roots, n => Assert.Empty(n.Items)); // no nesting
        Assert.All(roots, n =>
        {
            Image? box = HeaderImage(n.Header);
            Assert.NotNull(box);
            Assert.NotNull(box!.Source);
        });
    }

    // ---- Placement ------------------------------------------------------------

    [AvaloniaFact]
    public void Place_Button_Arms_On_A_Class_Row_And_Disarms_On_A_Folder()
    {
        var (panel, _, _) = NewBoundPanel();
        TreeView tree = Tree(panel, "_clutterTree");
        Button place = Field<Button>(panel, "_clutterPlace");

        tree.SelectedItem = Find(tree, "fern"); // a class leaf
        Assert.True(place.IsEnabled);

        tree.SelectedItem = Find(tree, "Natural"); // a folder is not placeable
        Assert.False(place.IsEnabled);
    }

    [AvaloniaFact]
    public void Place_And_Double_Click_Both_Place_With_Undo()
    {
        var (panel, host, session) = NewBoundPanel();
        EditorDocument doc = session.Document!;

        // Objects tab: select a flat Light row, click Place.
        TreeView objTree = Tree(panel, "_objectTree");
        objTree.SelectedItem = Find(objTree, "Light");
        Button objPlace = Field<Button>(panel, "_placeButton");
        Assert.True(objPlace.IsEnabled);
        objPlace.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Contains((LevelObjectKind.Light, (string?)null), host.Placed);
        Assert.Equal(1, doc.Objects.Count(o => o.Kind == LevelObjectKind.Light));
        doc.Undo.Undo();
        Assert.Equal(0, doc.Objects.Count(o => o.Kind == LevelObjectKind.Light));

        // Items tab: select an item class row and run (double-click / Enter / Place share RunTag).
        TreeView itemTree = Tree(panel, "_itemTree");
        itemTree.SelectedItem = Find(itemTree, "First_Aid");
        Field<Button>(panel, "_itemPlace").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Contains((LevelObjectKind.Item, (string?)"First_Aid"), host.Placed);
        Assert.Equal(1, doc.Objects.Count(o => o.Kind == LevelObjectKind.Item));
        doc.Undo.Undo();
        Assert.Equal(0, doc.Objects.Count(o => o.Kind == LevelObjectKind.Item));
    }

    [AvaloniaFact]
    public void Clutter_Row_Places_The_Class_With_Undo()
    {
        var (panel, host, session) = NewBoundPanel();
        EditorDocument doc = session.Document!;
        TreeView tree = Tree(panel, "_clutterTree");

        tree.SelectedItem = Find(tree, "barrel");
        Field<Button>(panel, "_clutterPlace").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Contains((LevelObjectKind.Clutter, (string?)"barrel"), host.Placed);
        Assert.Equal(1, doc.Objects.Count(o => o.Kind == LevelObjectKind.Clutter));
        doc.Undo.Undo();
        Assert.Equal(0, doc.Objects.Count(o => o.Kind == LevelObjectKind.Clutter));
    }

    [AvaloniaFact]
    public void Player_Start_Move_Row_Invokes_The_Host_Action()
    {
        var (panel, host, _) = NewBoundPanel();
        TreeView tree = Tree(panel, "_objectTree");
        MethodInfo run = typeof(PalettePanel).GetMethod("RunTag", BindingFlags.NonPublic | BindingFlags.Static)!;

        tree.SelectedItem = Find(tree, "Player Start (Move)");
        run.Invoke(null, new object[] { tree });

        Assert.Equal(1, host.MovePlayerStartCalls);
    }

    // ---- Mesh hover preview (owner ask) ---------------------------------------

    private static MeshHoverPreview Hover(PalettePanel panel) => Field<MeshHoverPreview>(panel, "_hoverPreview");

    private static void RaisePointer(Control c, RoutedEvent<PointerEventArgs> ev)
    {
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        c.RaiseEvent(new PointerEventArgs(ev, c, pointer, c, default,
            0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
    }

    [AvaloniaTheory]
    [InlineData("_clutterTree", "fern")]
    [InlineData("_entityTree", "Guard")]
    [InlineData("_itemTree", "First_Aid")]
    public void Hovering_A_Class_Row_Box_Schedules_Then_Opens_A_384_Preview_In_Every_Tab(string treeField, string cls)
    {
        var (panel, _, _) = NewBoundPanel();
        MeshHoverPreview hover = Hover(panel);
        Image box = HeaderImage(Find(Tree(panel, treeField), cls).Header)!;

        RaisePointer(box, InputElement.PointerEnteredEvent);

        // Dwell-delayed: scheduled for THIS row, not yet shown (so a quick scroll never flashes).
        Assert.True(hover.HasPendingShow);
        Assert.False(hover.IsShowing);
        Assert.Equal(cls, hover.PendingClass);

        // The dwell elapses → the popover opens with a 384px render.
        hover.ShowNow();
        Assert.True(hover.IsShowing);
        Assert.NotNull(hover.CurrentImage);
        Assert.Equal(MeshHoverPreview.PreviewSize, hover.CurrentImage!.Width);
        Assert.Equal(MeshHoverPreview.PreviewSize, hover.CurrentImage!.Height);

        // Leaving the box closes it.
        RaisePointer(box, InputElement.PointerExitedEvent);
        Assert.False(hover.IsShowing);
        Assert.False(hover.HasPendingShow);
    }

    [AvaloniaFact]
    public void Leaving_The_Box_Before_The_Dwell_Elapses_Never_Flashes_A_Popup()
    {
        var (panel, _, _) = NewBoundPanel();
        MeshHoverPreview hover = Hover(panel);
        Image box = HeaderImage(Find(Tree(panel, "_clutterTree"), "barrel").Header)!;

        RaisePointer(box, InputElement.PointerEnteredEvent);
        RaisePointer(box, InputElement.PointerExitedEvent); // scrolled past before the dwell

        Assert.False(hover.HasPendingShow);
        Assert.False(hover.IsShowing);
    }
}
