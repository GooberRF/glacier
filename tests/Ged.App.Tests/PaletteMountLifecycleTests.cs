using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Vpp;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Goober's bug: launch Glacier → File ▸ New Level → the palette Clutter/Items tabs were
/// empty. The class catalogs come from the MOUNTED INSTALL's tables (clutter.tbl/items.tbl),
/// not from the document — so the tabs must be populated whenever an install is mounted,
/// regardless of how (or whether) a document exists: mount-then-new, new-then-mount (the
/// live <see cref="Ged.App.EditorSession.VfsChanged"/> refresh), and mounted-with-no-document.
/// These tests drive a REAL EditorSession mount over a fixture install (a tables.vpp built
/// with real-format .tbl content) through a host that delegates to the session exactly like
/// MainWindow does.
/// </summary>
public sealed class PaletteMountLifecycleTests : IDisposable
{
    private readonly string _installDir;

    public PaletteMountLifecycleTests()
    {
        _installDir = Path.Combine(Path.GetTempPath(), "ged-palette-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_installDir);
        WriteFixtureInstall(_installDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_installDir))
        {
            Directory.Delete(_installDir, recursive: true);
        }
    }

    // ---- Fixture install: one tables.vpp with real-format clutter/items tables --------

    private const string ClutterTbl =
        "#Clutter\n" +
        "$Class Name: \"fix_bookcase\"\n" +
        "$V3D Filename: \"bookcase.v3d\"\n" +
        "$RFE Level1: \"Furniture\"\n" +
        "\n" +
        "$Class Name: \"fix_fern\"\n" +
        "$V3D Filename: \"fern.v3d\"\n" +
        "$RFE Level1: \"Natural\"\n" +
        "$RFE Level2: \"Plants\"\n" +
        "\n" +
        "$Class Name: \"fix_loose\"\n" +
        "$V3D Filename: \"loose.v3d\"\n" +
        "#End\n";

    private const string ItemsTbl =
        "#Items\n" +
        "$Class Name: \"fix_medkit\"\n" +
        "$V3D Filename: \"medkit.v3d\"\n" +
        "\n" +
        "$Class Name: \"fix_ammo\"\n" +
        "$V3D Filename: \"ammo.v3d\"\n" +
        "#End\n";

    private static void WriteFixtureInstall(string dir)
    {
        var vpp = new VppBuilder()
            .Add("clutter.tbl", Encoding.ASCII.GetBytes(ClutterTbl))
            .Add("items.tbl", Encoding.ASCII.GetBytes(ItemsTbl));
        vpp.Write(Path.Combine(dir, "tables.vpp"));
    }

    /// <summary>A host that delegates the catalog surface to a REAL session, like MainWindow.</summary>
    private sealed class SessionHost : IEditorHost
    {
        private readonly EditorSession _s;

        public SessionHost(EditorSession s) => _s = s;

        public EditorDocument? Document => _s.Document;
        public BrushEditor? BrushEditor => _s.BrushEditor;
        public SelectionRouter Selection => _s.Selection;
        public CommandDispatcher Dispatcher => throw new NotImplementedException();
        public void RequestSceneRebuild() { }
        public void RefreshSelectionOverlay() { }
        public void FrameObject(LevelObject o) { }
        public void FrameBrush(int uid) { }
        public void FocusTextureTools() { }
        public void ArmTextureEyedropper(Action<string> onSampled) { }
        public void ViewFromObject(LevelObject o) { }
        public int? SelectedAnnotationId => null;
        public void SelectAnnotation(int? id) { }
        public void DeleteAnnotation(int id) { }
        public Vec3 PlacementPoint => new(0, 0, 0);
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
        public void PlaceFromPalette(LevelObjectKind kind, string? className) { }
        public void MovePlayerStartHere() { }
        public void PlaceEventFromPalette(Ged.Core.Tables.EventSchema schema) { }

        // The two catalog surfaces, EXACTLY as MainWindow.Objects.cs implements them.
        public IReadOnlyList<string> ClassNamesFor(LevelObjectKind kind) => _s.ClassNames(kind);

        public PaletteCategoryNode ClutterCategoryTree() =>
            _s.Clutter?.BuildPaletteTree() ?? PaletteCategoryNode.Empty;

        public PaletteCategoryNode EntityCategoryTree() =>
            _s.Entities?.BuildPaletteTree() ?? PaletteCategoryNode.Empty;

        public bool PlaySoundPreview(string fileName) => false;
        public void StopSoundPreview() { }
        public IReadOnlyList<string> ClutterSkins(string className) => Array.Empty<string>();
        public void LoadClassThumbnail(LevelObjectKind kind, string? className, Image img) { }
        public string LevelLabel => "test";
        public Task<Ged.Core.Packaging.DependencyScanResult?> ScanDependenciesAsync() => Task.FromResult<Ged.Core.Packaging.DependencyScanResult?>(null);
        public Ged.Core.Packaging.PackfileBuildPlan? CreatePackfilePlan(Ged.Core.Packaging.DependencyScanResult scan) => null;
        public Task OpenPackfileAsync(Ged.Core.Packaging.PackfileBuildPlan plan) => Task.CompletedTask;
    }

    private static (PalettePanel Panel, EditorSession Session) NewBoundPanel(EditorSession session)
    {
        PaletteIcons.Configure(null, false);
        var panel = new PalettePanel();
        panel.Bind(new SessionHost(session));
        // Mirror MainWindow.InitMount/OnVfsChanged: every mount refreshes the palette live.
        session.VfsChanged += panel.RefreshCatalogs;
        return (panel, session);
    }

    private static TreeView Tree(PalettePanel panel, string field) =>
        (TreeView)panel.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;

    private static List<TreeViewItem> Roots(TreeView tree) =>
        ((IEnumerable)tree.ItemsSource!).Cast<TreeViewItem>().ToList();

    private static string HeaderText(object? header) => header switch
    {
        string s => s,
        TextBlock tb => tb.Text ?? string.Empty,
        Avalonia.Controls.Panel p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty,
        _ => string.Empty,
    };

    private static IEnumerable<string> AllLabels(TreeView tree) =>
        Roots(tree).SelectMany(Descend).Select(n => HeaderText(n.Header));

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

    private static void AssertTabsPopulated(PalettePanel panel)
    {
        List<string> clutter = AllLabels(Tree(panel, "_clutterTree")).ToList();
        Assert.Contains("Furniture", clutter);        // RFE Level1 folder
        Assert.Contains("fix_bookcase", clutter);
        Assert.Contains("Natural", clutter);
        Assert.Contains("Plants", clutter);           // RFE Level2 nesting
        Assert.Contains("fix_fern", clutter);
        Assert.Contains("fix_loose", clutter);        // untagged class at the root

        List<string> items = AllLabels(Tree(panel, "_itemTree")).ToList();
        Assert.Equal(new[] { "fix_ammo", "fix_medkit" }, items); // flat, alpha
    }

    // ---- The three lifecycle scenarios -----------------------------------------------

    [AvaloniaFact]
    public void Mount_Then_New_Level_Populates_Clutter_And_Items()
    {
        using var session = new EditorSession();
        session.MountInstall(_installDir);
        session.NewLevel(); // File ▸ New after the startup mount
        var (panel, _) = NewBoundPanel(session);

        AssertTabsPopulated(panel);
    }

    [AvaloniaFact]
    public void Mount_After_New_Level_Refreshes_The_Tabs_Live()
    {
        using var session = new EditorSession();
        var (panel, _) = NewBoundPanel(session);
        session.NewLevel(); // the user made a new level BEFORE any install was mounted

        // Unmounted: the class tabs are empty apart from the mount hint.
        Assert.Contains(AllLabels(Tree(panel, "_clutterTree")), l => l.Contains("mount an RF install"));
        Assert.Contains(AllLabels(Tree(panel, "_itemTree")), l => l.Contains("mount an RF install"));
        Assert.DoesNotContain("fix_bookcase", AllLabels(Tree(panel, "_clutterTree")));

        // Mounting NOW (Settings / wizard / EnsureVfs) refreshes the tabs via VfsChanged.
        session.MountInstall(_installDir);

        AssertTabsPopulated(panel);
    }

    [AvaloniaFact]
    public void Mounted_With_No_Document_Populates_The_Tabs()
    {
        using var session = new EditorSession();
        session.MountInstall(_installDir);
        var (panel, _) = NewBoundPanel(session); // no NewLevel / no document at all

        Assert.Null(session.Document);
        AssertTabsPopulated(panel);
    }

    [AvaloniaFact]
    public void Unmounted_Tabs_Show_A_Mount_Hint_That_Is_Not_Placeable()
    {
        using var session = new EditorSession();
        var (panel, _) = NewBoundPanel(session);

        foreach (string field in new[] { "_clutterTree", "_itemTree" })
        {
            List<TreeViewItem> roots = Roots(Tree(panel, field));
            TreeViewItem hint = Assert.Single(roots);
            Assert.Contains("mount an RF install", HeaderText(hint.Header));
            Assert.Null(hint.Tag); // not placeable — selecting it never arms Place
        }
    }
}
