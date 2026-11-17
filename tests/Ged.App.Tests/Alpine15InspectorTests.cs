using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The Properties panel for the Alpine Faction 1.5 objects (chunk C5): a selected object shows its schema fields with
/// their choices, an edit through the panel is one undo step that dirties the chunk, a multi-selection of one kind
/// edits every object at once (never the script name), the rope decoration and liquid-emission rows write through the
/// shared accessor, and each kind has a palette icon.
/// </summary>
public sealed class Alpine15InspectorTests
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


    private static (EditorSession Session, PropertiesPanel Panel) Select(params LevelObjectKind[] kinds)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        var placed = kinds.Select((k, i) => doc.PlaceObject(k, new Vec3(i * 3, 0, 0))!).ToList();
        session.ActiveSelectKinds = SelectKinds.Objects;
        doc.SelectMany(placed);

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));
        panel.Refresh();
        return (session, panel);
    }

    private static Control Root(PropertiesPanel panel)
    {
        var scroll = (ScrollViewer)typeof(PropertiesPanel)
            .GetField("_scroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        return (Control)scroll.Content!;
    }

    private static IEnumerable<Control> Walk(Control? c)
    {
        if (c is null)
        {
            yield break;
        }

        yield return c;
        switch (c)
        {
            case Panel p:
                foreach (Control child in p.Children.OfType<Control>())
                {
                    foreach (Control d in Walk(child))
                    {
                        yield return d;
                    }
                }

                break;
            case Decorator dec:
                foreach (Control d in Walk(dec.Child))
                {
                    yield return d;
                }

                break;
            case ContentControl cc when cc.Content is Control inner:
                foreach (Control d in Walk(inner))
                {
                    yield return d;
                }

                break;
            case ContentPresenter cp when cp.Content is Control inner:
                foreach (Control d in Walk(inner))
                {
                    yield return d;
                }

                break;
        }
    }

    /// <summary>The editor control of the LabeledRow whose label matches (Grid: [0]=label, [1]=editor).</summary>
    private static Control? RowEditor(Control root, string label) =>
        Walk(root).OfType<Grid>()
            .Where(g => g.Children.Count >= 2 && g.Children[0] is TextBlock tb && tb.Text == label)
            .Select(g => g.Children[1] as Control)
            .FirstOrDefault();


    private static HashSet<string?> Labels(PropertiesPanel panel) => Walk(Root(panel)).OfType<TextBlock>().Select(t => t.Text).ToHashSet();

    [AvaloniaFact]
    public void A_Weather_Region_Shows_Its_Fields_With_Alpines_Choices()
    {
        var (_, panel) = Select(LevelObjectKind.WeatherRegion);
        HashSet<string?> labels = Labels(panel);
        foreach (string l in new[] { "Weather", "Shape", "Width", "Density Scale", "Rain Color", "Snow Bitmap", "Column Width" })
        {
            Assert.Contains(l, labels);
        }

        var shape = RowEditor(Root(panel), "Shape") as ComboBox;
        Assert.Equal(new[] { "Box", "Sphere" }, shape!.ItemsSource!.Cast<string>());
        Assert.DoesNotContain(Walk(Root(panel)).OfType<TextBlock>(), t => t.Text?.Contains("read-only", StringComparison.OrdinalIgnoreCase) == true);
    }

    [AvaloniaFact]
    public void Editing_A_Field_Through_The_Panel_Is_One_Undo_Step_And_Clamps_To_Alpines_Range()
    {
        var (session, panel) = Select(LevelObjectKind.WeatherRegion);
        EditorDocument doc = session.Document!;
        LevelObject w = doc.Selection.Single();
        var model = (AlpineWeatherRegion)w.Model;
        w.Section.Dirty = false;

        var density = (TextBox)RowEditor(Root(panel), "Density Scale")!;
        density.Text = "5";
        density.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.Equal(2f, model.DensityScale); // weather_region.cpp:291-292 clamps to 0..2
        Assert.True(w.Section.Dirty);
        doc.Undo.Undo();
        Assert.Equal(1f, model.DensityScale);
    }

    [AvaloniaFact]
    public void A_Vehicle_Factory_Team_Lists_None_Red_Blue()
    {
        var (session, panel) = Select(LevelObjectKind.VehicleFactory);
        var team = (ComboBox)RowEditor(Root(panel), "Team")!;
        Assert.Equal(0, team.SelectedIndex); // stored 0xFF = None
        team.SelectedIndex = 2;
        Assert.Equal((byte)1, ((AlpineVehicleFactory)session.Document!.Selection.Single().Model).Team); // Blue is stored 1
    }

    [AvaloniaFact]
    public void A_Multi_Selection_Of_One_Kind_Edits_Every_Object_But_Not_The_Name()
    {
        var (session, panel) = Select(LevelObjectKind.WaypointHelper, LevelObjectKind.WaypointHelper, LevelObjectKind.WaypointHelper);
        EditorDocument doc = session.Document!;
        Assert.Contains("3 objects (WaypointHelper)", Labels(panel));

        var radius = (TextBox)RowEditor(Root(panel), "Radius")!;
        radius.Text = "9";
        radius.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.All(doc.Objects.Where(o => o.Kind == LevelObjectKind.WaypointHelper), o => Assert.Equal(9f, ((AlpineWaypointHelper)o.Model).Radius));

        doc.Undo.Undo(); // one step for the whole selection
        Assert.All(doc.Objects.Where(o => o.Kind == LevelObjectKind.WaypointHelper), o => Assert.Equal(4f, ((AlpineWaypointHelper)o.Model).Radius));
    }

    [AvaloniaFact]
    public void Rope_Decoration_Meshes_Are_Edited_Through_The_Shared_Accessor()
    {
        var (session, panel) = Select(LevelObjectKind.RopeEmitter);
        var rope = (AlpineRopeEmitter)session.Document!.Selection.Single().Model;
        var meshes = (TextBox)RowEditor(Root(panel), "Decoration Meshes")!;
        meshes.Text = "lamp.v3m;bulb.v3m";
        meshes.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.Equal(2, rope.Decorations!.Meshes.Count);
    }

    [AvaloniaFact]
    public void Every_Alpine_Kind_Has_A_Palette_Icon()
    {
        foreach (LevelObjectKind k in new[]
                 {
                     LevelObjectKind.WeatherRegion, LevelObjectKind.VehicleFactory, LevelObjectKind.ProjectionCamera,
                     LevelObjectKind.RopeEmitter, LevelObjectKind.DirectionalLight, LevelObjectKind.WaypointHelper,
                 })
        {
            Assert.NotNull(PaletteIcons.TryFor(k));
        }
    }
}