using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// UX stability of the Outliner on state toggles: hiding/unhiding and locking/unlocking an
/// object must never collapse the tree, drop the selection or scroll away. Toggles refresh the
/// affected row(s) in place (no rebuild); when an external event does force a rebuild, selection
/// (by UID) and group expansion are captured and restored across it.
/// </summary>
public sealed class OutlinerStabilityTests
{
    private sealed class FakeHost : IEditorHost
    {
        private readonly EditorSession _s;

        public FakeHost(EditorSession s) => _s = s;

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
        public Vec3 PlacementPoint => default;
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
        public IReadOnlyList<string> ClassNamesFor(LevelObjectKind kind) => Array.Empty<string>();
        public PaletteCategoryNode ClutterCategoryTree() => PaletteCategoryNode.Empty;
        public PaletteCategoryNode EntityCategoryTree() => PaletteCategoryNode.Empty;
        public bool PlaySoundPreview(string fileName) => false;
        public void StopSoundPreview() { }
        public IReadOnlyList<string> ClutterSkins(string className) => Array.Empty<string>();
        public void LoadClassThumbnail(LevelObjectKind kind, string? className, Image img) { }
        public string LevelLabel => "test";
        public Task<Ged.Core.Packaging.DependencyScanResult?> ScanDependenciesAsync() => Task.FromResult<Ged.Core.Packaging.DependencyScanResult?>(null);
        public Ged.Core.Packaging.PackfileBuildPlan? CreatePackfilePlan(Ged.Core.Packaging.DependencyScanResult scan) => null;
        public Task OpenPackfileAsync(Ged.Core.Packaging.PackfileBuildPlan plan) => Task.CompletedTask;
    }

    private const LevelObjectKind Kind = LevelObjectKind.RoomEffect;

    /// <summary>A level with three same-kind objects (one group of three) and an Outliner bound
    /// to it. Mirrors MainWindow by routing the document's VisibilityChanged into Refresh().</summary>
    private static (EditorSession Session, OutlinerPanel Panel, LevelObject[] Objs) SetUp()
    {
        var session = new EditorSession();
        session.NewLevel();
        session.ActiveSelectKinds = SelectKinds.Objects;
        EditorDocument doc = session.Document!;

        var objs = new[]
        {
            doc.PlaceObject(Kind, new Vec3(0, 0, 0))!,
            doc.PlaceObject(Kind, new Vec3(1, 0, 0))!,
            doc.PlaceObject(Kind, new Vec3(2, 0, 0))!,
        };

        var panel = new OutlinerPanel();
        panel.Bind(new FakeHost(session));

        // Faithful to MainWindow: a visibility change (e.g. ToggleLock) asks the Outliner to
        // refresh. The panel must stay stable across that path.
        doc.VisibilityChanged += panel.Refresh;

        return (session, panel, objs);
    }

    [AvaloniaFact]
    public void Toggling_Hidden_Refreshes_In_Place_And_Preserves_Selection_And_Expansion()
    {
        var (_, panel, objs) = SetUp();
        panel.SetGroupExpandedForTest(Kind, true);
        panel.SelectRowForTest(objs[0].Uid);

        TreeViewItem? rowBefore = panel.RowForTest(objs[0].Uid);
        Assert.True(panel.IsGroupExpandedForTest(Kind));
        Assert.Equal(objs[0].Uid, panel.SelectedRowUidForTest);

        panel.ToggleHiddenForTest(objs[0]);

        // State applied + row updated in place (same container instance — no rebuild).
        Assert.True(objs[0].Hidden);
        Assert.True(panel.RowHiddenGlyphForTest(objs[0].Uid));
        Assert.Same(rowBefore, panel.RowForTest(objs[0].Uid));

        // Selection and expansion untouched.
        Assert.Equal(objs[0].Uid, panel.SelectedRowUidForTest);
        Assert.True(panel.IsGroupExpandedForTest(Kind));

        // Unhide is equally stable.
        panel.ToggleHiddenForTest(objs[0]);
        Assert.False(objs[0].Hidden);
        Assert.False(panel.RowHiddenGlyphForTest(objs[0].Uid));
        Assert.Equal(objs[0].Uid, panel.SelectedRowUidForTest);
        Assert.True(panel.IsGroupExpandedForTest(Kind));
    }

    [AvaloniaFact]
    public void Toggling_Lock_Preserves_Selection_And_Expansion_Despite_VisibilityChanged()
    {
        var (session, panel, objs) = SetUp();
        EditorDocument doc = session.Document!;
        panel.SetGroupExpandedForTest(Kind, true);
        panel.SelectRowForTest(objs[1].Uid);

        TreeViewItem? rowBefore = panel.RowForTest(objs[1].Uid);

        panel.ToggleLockForTest(objs[1]);

        // Lock applied; the VisibilityChanged->Refresh callback did NOT rebuild (guard), so the
        // container is the same instance and selection/expansion are intact.
        Assert.True(doc.IsLocked(objs[1]));
        Assert.Same(rowBefore, panel.RowForTest(objs[1].Uid));
        Assert.Equal(objs[1].Uid, panel.SelectedRowUidForTest);
        Assert.True(panel.IsGroupExpandedForTest(Kind));

        // Unlock is equally stable.
        panel.ToggleLockForTest(objs[1]);
        Assert.False(doc.IsLocked(objs[1]));
        Assert.Equal(objs[1].Uid, panel.SelectedRowUidForTest);
        Assert.True(panel.IsGroupExpandedForTest(Kind));
    }

    [AvaloniaFact]
    public void Multi_Selection_Survives_Toggling_One_Member()
    {
        var (session, panel, objs) = SetUp();
        EditorDocument doc = session.Document!;

        // Two objects selected in the document (multi-select); the tree highlights the first.
        Assert.True(session.Selection.SelectObject(objs[0]));
        Assert.True(session.Selection.SelectObject(objs[1], additive: true));
        panel.SetGroupExpandedForTest(Kind, true);
        panel.SelectRowForTest(objs[0].Uid);

        var before = doc.Selection.Select(o => o.Uid).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { objs[0].Uid, objs[1].Uid }.OrderBy(x => x), before);

        // Toggle hidden on one selected member.
        panel.ToggleHiddenForTest(objs[1]);

        var after = doc.Selection.Select(o => o.Uid).OrderBy(x => x).ToArray();
        Assert.Equal(before, after);
        Assert.True(objs[1].Hidden);
        Assert.Equal(objs[0].Uid, panel.SelectedRowUidForTest);
        Assert.True(panel.IsGroupExpandedForTest(Kind));

        // And toggling lock on the other member leaves the multi-selection intact too.
        panel.ToggleLockForTest(objs[0]);
        Assert.Equal(before, doc.Selection.Select(o => o.Uid).OrderBy(x => x).ToArray());
        Assert.True(panel.IsGroupExpandedForTest(Kind));
    }

    [AvaloniaFact]
    public void External_Rebuild_Restores_Selection_And_Expansion_By_Uid()
    {
        var (_, panel, objs) = SetUp();
        panel.SetGroupExpandedForTest(Kind, true);
        panel.SelectRowForTest(objs[2].Uid);

        TreeViewItem? rowBefore = panel.RowForTest(objs[2].Uid);

        // A genuine external rebuild (as fired by ObjectsChanged/rename/out-of-panel hide).
        panel.Refresh();

        // The rebuild replaced the containers (proving it really rebuilt) ...
        TreeViewItem? rowAfter = panel.RowForTest(objs[2].Uid);
        Assert.NotNull(rowAfter);
        Assert.NotSame(rowBefore, rowAfter);

        // ... yet selection (restored by UID) and expansion (restored by node key) survived.
        Assert.Equal(objs[2].Uid, panel.SelectedRowUidForTest);
        Assert.True(panel.IsGroupExpandedForTest(Kind));
    }

    [AvaloniaFact]
    public void Group_Hide_All_Preserves_Expansion_And_Selection()
    {
        var (_, panel, objs) = SetUp();
        panel.SetGroupExpandedForTest(Kind, true);
        panel.SelectRowForTest(objs[0].Uid);

        panel.ToggleGroupHiddenForTest(Kind);

        Assert.All(objs, o => Assert.True(o.Hidden));
        Assert.True(panel.IsGroupExpandedForTest(Kind));
        Assert.Equal(objs[0].Uid, panel.SelectedRowUidForTest);

        // Show-all restores visibility and stays stable.
        panel.ToggleGroupHiddenForTest(Kind);
        Assert.All(objs, o => Assert.False(o.Hidden));
        Assert.True(panel.IsGroupExpandedForTest(Kind));
        Assert.Equal(objs[0].Uid, panel.SelectedRowUidForTest);
    }
}
