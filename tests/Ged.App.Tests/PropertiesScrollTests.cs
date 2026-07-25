using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ged.App;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Tests;

/// <summary>
/// Item 2: the Properties panel must scroll all the way to the bottom when the selected
/// object has many fields (a Trigger). The ScrollViewer's vertical scroll visibility is
/// pinned to Auto as a LOCAL value so the Fluent/Dock control-theme ScrollViewer style
/// cannot win and suppress the scrollbar; the layout test proves the tall inspector's
/// extent overflows the viewport and the last field is reachable.
/// </summary>
public sealed class PropertiesScrollTests
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
        public Ged.Core.Model.Vec3 PlacementPoint => default;
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
        public Ged.Core.Editing.PaletteCategoryNode ClutterCategoryTree() => Ged.Core.Editing.PaletteCategoryNode.Empty;
        public Ged.Core.Editing.PaletteCategoryNode EntityCategoryTree() => Ged.Core.Editing.PaletteCategoryNode.Empty;
        public bool PlaySoundPreview(string fileName) => false;
        public void StopSoundPreview() { }
        public IReadOnlyList<string> ClutterSkins(string className) => Array.Empty<string>();
        public void LoadClassThumbnail(LevelObjectKind kind, string? className, Image img) { }
        public string LevelLabel => "test";
        public Task<Ged.Core.Packaging.DependencyScanResult?> ScanDependenciesAsync() => Task.FromResult<Ged.Core.Packaging.DependencyScanResult?>(null);
        public Ged.Core.Packaging.PackfileBuildPlan? CreatePackfilePlan(Ged.Core.Packaging.DependencyScanResult scan) => null;
        public Task OpenPackfileAsync(Ged.Core.Packaging.PackfileBuildPlan plan) => Task.CompletedTask;
    }

    private static ScrollViewer ScrollOf(PropertiesPanel panel) =>
        (ScrollViewer)typeof(PropertiesPanel)
            .GetField("_scroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;

    [AvaloniaFact]
    public void Scroll_Visibility_Is_Pinned_Locally()
    {
        var panel = new PropertiesPanel();
        ScrollViewer sv = ScrollOf(panel);

        // Both must be LOCAL values (never left to the theme default) — that is the fix: a
        // ScrollViewer style in the Fluent/Dock control theme wins over the built-in default,
        // so the value has to be pinned locally for the tall inspector to keep its scrollbar.
        Assert.Equal(ScrollBarVisibility.Auto, sv.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, sv.HorizontalScrollBarVisibility);
        Assert.Equal(BindingPriority.LocalValue,
            sv.GetDiagnostic(ScrollViewer.VerticalScrollBarVisibilityProperty).Priority);
    }

    [AvaloniaFact]
    public void Tall_Trigger_Inspector_Is_Fully_Scrollable_To_The_Bottom()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));

        // Real flow: shown empty, laid out in a bounded window, THEN a selection refreshes it.
        var host = new Window { Content = panel };
        host.Show();
        host.UpdateLayout();

        LevelObject trig = doc.PlaceObject(LevelObjectKind.Trigger, new CoreVec3(0, 0, 0))!;
        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(trig);
        panel.Refresh();
        host.UpdateLayout();

        ScrollViewer sv = ScrollOf(panel);

        // The many-field inspector overflows the viewport ...
        Assert.True(sv.Extent.Height > sv.Viewport.Height,
            $"inspector did not overflow: extent={sv.Extent.Height} viewport={sv.Viewport.Height}");

        // ... and the very bottom (last field) is reachable.
        sv.Offset = new Vector(0, 1_000_000);
        host.UpdateLayout();
        double maxOffset = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(Math.Abs(sv.Offset.Y - maxOffset) < 1.0,
            $"could not scroll to bottom: offset={sv.Offset.Y} expected≈{maxOffset}");
    }

    [AvaloniaFact]
    public void Tall_Inspector_Leaves_Bottom_Clearance_Below_The_Last_Field()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));

        var host = new Window { Content = panel };
        host.Show();
        host.UpdateLayout();

        LevelObject trig = doc.PlaceObject(LevelObjectKind.Trigger, new CoreVec3(0, 0, 0))!;
        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(trig);
        panel.Refresh();
        host.UpdateLayout();

        ScrollViewer sv = ScrollOf(panel);
        var content = (Control)sv.Content!;

        // Precondition: the trigger inspector overflows the viewport.
        Assert.True(sv.Extent.Height > sv.Viewport.Height,
            $"inspector did not overflow: extent={sv.Extent.Height} viewport={sv.Viewport.Height}");

        // The bottom-most interactive editor sits clear of the content extent's bottom edge, so
        // the last field scrolls fully into view instead of being clipped/flush (the report).
        double lastFieldBottom = content.GetVisualDescendants()
            .Where(v => v is TextBox or CheckBox or ComboBox)
            .Select(v => v.TranslatePoint(new Point(0, v.Bounds.Height), content)?.Y ?? 0)
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(lastFieldBottom > 0, "no editor controls were found in the inspector.");

        // The content extent runs comfortably past the last field (~29px here + the scroll
        // padding), so when scrolled to the bottom the final editor is not flush with the edge.
        Assert.True(sv.Extent.Height - lastFieldBottom >= 20,
            $"insufficient bottom clearance: extent={sv.Extent.Height} lastFieldBottom={lastFieldBottom}");
    }
}
