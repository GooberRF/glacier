using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// B6: Layers-panel row interaction under the lock-enforcement rules. Row highlight and the
/// double-click camera jump must ALWAYS work (locked or not); only the single-click DOCUMENT
/// selection stays lock-gated. The two coupled defects were (a) the panel's own
/// <c>SelectionChanged += Refresh</c> tore down the row Borders on every selection change — so
/// the second press of a double-click landed on a rebuilt element and the jump only fired for
/// LOCKED brushes (whose refused select never fired SelectionChanged); and (b) row highlight was
/// derived solely from the lock-gated document selection, so a locked row could never highlight.
/// </summary>
public sealed class LayersPanelInteractionTests
{
    private sealed class FakeHost : IEditorHost
    {
        private readonly EditorSession _s;

        public FakeHost(EditorSession s) => _s = s;

        public List<int> FramedBrushes { get; } = new();

        public EditorDocument? Document => _s.Document;
        public BrushEditor? BrushEditor => _s.BrushEditor;
        public SelectionRouter Selection => _s.Selection;
        public CommandDispatcher Dispatcher => throw new NotImplementedException();
        public void RequestSceneRebuild() { }

        public void RequestHistoryJump(Ged.Core.Editor.UndoNode target) { }
        public void RefreshSelectionOverlay() { }
        public void FrameObject(LevelObject o) { }
        public void FrameBrush(int uid) => FramedBrushes.Add(uid);
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

    private static (EditorSession Session, FakeHost Host, LayersPanel Panel, int[] Uids) SetUp()
    {
        var session = new EditorSession();
        session.NewLevel();
        session.ActiveSelectKinds = SelectKinds.Brushes; // brush selection permitted
        BrushEditor be = session.BrushEditor!;
        int a = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 0, 0), Mat3.Identity);
        int b = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(4, 0, 0), Mat3.Identity);
        be.SetMode(EditMode.Brush);

        var host = new FakeHost(session);
        var panel = new LayersPanel();
        panel.Bind(host);
        return (session, host, panel, new[] { a, b });
    }

    [AvaloniaFact]
    public void Selection_Change_Highlights_In_Place_Without_Rebuilding_Rows()
    {
        // Fix (a) root cause: the panel must not rebuild rows on a selection change, or the second
        // press of a double-click lands on a fresh Border and DoubleTapped never fires. Proven by
        // Border identity surviving a selection change (pre-fix `SelectionChanged += Refresh` cleared
        // and rebuilt every row).
        var (session, _, panel, uids) = SetUp();
        Border? rowBefore = panel.RowBorderForTest(uids[1]);
        Assert.NotNull(rowBefore);

        session.Selection.SelectBrush(uids[0]); // fires BrushEditor.SelectionChanged

        Assert.Same(rowBefore, panel.RowBorderForTest(uids[1])); // same instance -> gesture survives
        Assert.True(panel.RowHighlightedForTest(uids[0]));        // the selected row IS highlighted
    }

    [AvaloniaFact]
    public void Double_Click_Frames_The_Camera_For_An_Unlocked_Brush()
    {
        // The reported symptom: an UNLOCKED brush's double-click did not jump the camera.
        var (_, host, panel, uids) = SetUp();
        panel.DoubleTapRowForTest(uids[0]);
        Assert.Equal(new[] { uids[0] }, host.FramedBrushes);
    }

    [AvaloniaFact]
    public void Locked_Row_Highlights_In_The_Panel_But_Is_Not_Document_Selected()
    {
        // Fix (b): a locked row must still highlight when clicked (panel row interaction is not
        // gated by the document-selection lock rules), yet the document selection stays lock-gated.
        var (session, _, panel, uids) = SetUp();
        BrushEditor be = session.BrushEditor!;
        be.SetBrushLocked(new[] { uids[0] }, true);
        panel.Refresh();

        panel.PressRowForTest(uids[0]);

        Assert.True(panel.RowHighlightedForTest(uids[0]));       // row highlights despite the lock
        Assert.DoesNotContain(uids[0], be.SelectedBrushes);      // but it is NOT document-selected
    }

    [AvaloniaFact]
    public void Double_Click_Frames_The_Camera_Even_For_A_Locked_Brush()
    {
        // Jumping the camera to a locked brush is legitimate and must always work.
        var (session, host, panel, uids) = SetUp();
        BrushEditor be = session.BrushEditor!;
        be.SetBrushLocked(new[] { uids[0] }, true);
        panel.Refresh();

        panel.DoubleTapRowForTest(uids[0]);

        Assert.Equal(new[] { uids[0] }, host.FramedBrushes);     // jump fired
        Assert.DoesNotContain(uids[0], be.SelectedBrushes);      // still not document-selected
    }
}
