using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The Terrain Tools in the shell (ALPINE-COMPAT-SPEC C9): the docked panel shows and writes the shared brush settings,
/// its own edits (Stitch Edges, decoration Add / Apply / Remove) are one undo step each, a viewport stroke through
/// <see cref="TerrainToolController"/> is one undo step that marks the geometry out of date only when it changed heights or
/// holes, the level scene leaves a terrain being stroked to the live overlay, and Generate Terrain previews and applies
/// as one undo step. Screenshot evidence of the panel and the dialog runs with GED_APP_TESTS_RENDER=1 and
/// GED_DIALOG_EVIDENCE set to an output folder.
/// </summary>
public sealed class TerrainToolsPanelTests
{
    private static (EditorSession Session, EditorDocument Doc, LevelObject Obj, AlpineTerrain T) Level(int n = 33, float cell = 2f, bool geoable = false)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        LevelObject obj = TerrainOps.Create(doc, new Vec3(0, 0, 0), n, n, cell, "hills");
        var t = (AlpineTerrain)obj.Model;
        TerrainOps.Apply(doc, obj, "layers", x =>
        {
            TerrainOps.SetLayers(x, new[] { "grass1.tga", "rock02.tga", "sand01.tga" });
            x.HeightMin = 0f;
            x.HeightRange = 16f;
            x.Geoable = geoable;
        });
        doc.MarkSaved();
        return (session, doc, obj, t);
    }

    private static (float X, float Z) Centre(AlpineTerrain t) =>
        (t.Origin.X + (AlpineTerrainRules.Extent(t.Nx, t.CellSize) * 0.5f), t.Origin.Z + (AlpineTerrainRules.Extent(t.Nz, t.CellSize) * 0.5f));

    private static (Vector3 O, Vector3 D) Down(float x, float z) => (new Vector3(x, 200f, z), new Vector3(0, -1, 0));

    private static (Window Window, TerrainToolsPanel Panel) Show(EditorDocument doc, LevelObject obj, TerrainBrushSettings s, TerrainToolServices? services = null, bool armed = false)
    {
        var panel = new TerrainToolsPanel(doc, obj, s, armed, services);
        var window = new Window { Width = 380, Height = 1500, Content = new ScrollViewer { Content = panel } };
        window.Show();
        window.UpdateLayout();
        return (window, panel);
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void The_Panel_Shows_And_Writes_The_Shared_Brush_Settings()
    {
        (_, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level();
        var s = new TerrainBrushSettings { Tool = TerrainTool.Raise, Radius = 12f, Strength = 0.3f, Falloff = TerrainFalloff.Linear, Layer = 1 };
        bool? armed = null;
        int settingsChanged = 0;
        var services = new TerrainToolServices { SetArmed = a => armed = a, SettingsChanged = () => settingsChanged++ };
        (Window w, TerrainToolsPanel p) = Show(doc, obj, s, services);

        // RED's 14 tools in two columns, the selected one checked with its hint.
        Assert.Equal(14, p.GetVisualDescendants().OfType<RadioButton>().Count(r => r.GroupName == "ttools_tool"));
        Assert.True(Named<RadioButton>(p, "ttools_tool_raise").IsChecked);
        Assert.StartsWith("Raises by up to strength x radius / 20", Named<TextBlock>(p, "ttools_hint").Text);
        Assert.False(Named<RadioButton>(p, "ttools_tool_geoable_chunks").IsEnabled); // not geoable
        Assert.Equal(12m, Named<NumericUpDown>(p, "ttools_radius").Value);
        Assert.Equal(30m, Named<NumericUpDown>(p, "ttools_strength").Value);
        Assert.Equal(1, Named<ComboBox>(p, "ttools_falloff").SelectedIndex);
        ListBox layers = Named<ListBox>(p, "ttools_layers");
        Assert.Equal(t.Layers.Count, layers.ItemCount);
        Assert.Equal(1, layers.SelectedIndex);

        // Writes go to the shared settings (the viewport reads the same object) and redraw the cursor.
        Named<NumericUpDown>(p, "ttools_radius").Value = 20m;
        Named<NumericUpDown>(p, "ttools_strength").Value = 75m;
        Named<ComboBox>(p, "ttools_falloff").SelectedIndex = 2;
        Named<RadioButton>(p, "ttools_tool_flatten").IsChecked = true;
        layers.SelectedIndex = 2;
        Named<CheckBox>(p, "ttools_mirror_x").IsChecked = true;
        Named<NumericUpDown>(p, "ttools_mirror_x_offset").Value = 1.5m;
        Named<NumericUpDown>(p, "ttools_height").Value = 7.25m;
        Named<NumericUpDown>(p, "ttools_angle").Value = -20m;
        Assert.Equal(20f, s.Radius);
        Assert.Equal(0.75f, s.Strength, 4);
        Assert.Equal(TerrainFalloff.Constant, s.Falloff);
        Assert.Equal(TerrainTool.Flatten, s.Tool);
        Assert.Equal(2, s.Layer);
        Assert.True(s.MirrorX);
        Assert.Equal(3, s.MirrorXOffset2);
        Assert.Equal(7.25f, s.SetHeight);
        Assert.Equal(-20f, s.RampAngle);
        Assert.True(settingsChanged >= 8);
        Assert.Contains("Mirror X", Named<TextBlock>(p, "ttools_status").Text);

        // The arm toggle tells the shell; a sampled height shows after RefreshSettings.
        Named<ToggleButton>(p, "ttools_active").IsChecked = true;
        Assert.True(armed);
        s.SetHeight = 3.5f;
        p.RefreshSettings();
        Assert.Equal(3.5m, Named<NumericUpDown>(p, "ttools_height").Value);
        Assert.False(doc.IsDirty);
        w.Close();
    }

    [AvaloniaFact]
    public void Geoable_Chunks_Is_Offered_Only_On_A_Geoable_Terrain()
    {
        (_, EditorDocument doc, LevelObject obj, _) = Level(geoable: true);
        var s = new TerrainBrushSettings { Tool = TerrainTool.GeoableChunks };
        (Window w, TerrainToolsPanel p) = Show(doc, obj, s);
        Assert.True(Named<RadioButton>(p, "ttools_tool_geoable_chunks").IsEnabled);
        Assert.True(Named<RadioButton>(p, "ttools_tool_geoable_chunks").IsChecked);
        Assert.Contains("Geoable chunks", Named<TextBlock>(p, "ttools_status").Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Stitch_And_Each_Decoration_Edit_Are_One_Undo_Step()
    {
        (_, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level();
        float extent = AlpineTerrainRules.Extent(t.Nx, t.CellSize);
        LevelObject other = TerrainOps.Create(doc, new Vec3(extent, 0, 0), t.Nx, t.Nz, t.CellSize, "east");
        TerrainOps.Apply(doc, other, "lift", x => TerrainOps.SetNormalizedHeights(x, (_, _) => 0.5));
        int edits = 0;
        (Window w, TerrainToolsPanel p) = Show(doc, obj, new TerrainBrushSettings(), new TerrainToolServices { AfterEdit = () => edits++ });

        int steps = doc.Undo.UndoEntries.Count;
        Click(Named<Button>(p, "ttools_stitch"));
        Assert.Equal(steps + 1, doc.Undo.UndoEntries.Count);
        Assert.Contains("Stitched", Named<TextBlock>(p, "ttools_status").Text);

        Named<TextBox>(p, "ttools_deco_mesh").Text = "bush01.v3m";
        Named<NumericUpDown>(p, "ttools_deco_density").Value = 0.2m;
        Named<CheckBox>(p, "ttools_deco_casts_shadows").IsChecked = true;
        Click(Named<Button>(p, "ttools_deco_add"));
        Assert.Equal(steps + 2, doc.Undo.UndoEntries.Count);
        AlpineTerrainDecoration d = Assert.Single(t.Decorations);
        Assert.Equal("bush01.v3m", d.Mesh);
        Assert.Equal(0.2f, d.Density, 4);
        Assert.NotEqual(0, d.Flags & AlpineTerrainSurface.DecorationFlagCastsShadows);
        Assert.Equal(t.Layers.Count + 1, Named<ListBox>(p, "ttools_layers").ItemCount); // the decoration is paintable

        Named<NumericUpDown>(p, "ttools_deco_density").Value = 1.5m;
        Click(Named<Button>(p, "ttools_deco_apply"));
        Assert.Equal(steps + 3, doc.Undo.UndoEntries.Count);
        Assert.Equal(1.5f, t.Decorations[0].Density, 4);

        Click(Named<Button>(p, "ttools_deco_remove"));
        Assert.Equal(steps + 4, doc.Undo.UndoEntries.Count);
        Assert.Empty(t.Decorations);
        Assert.Equal(4, edits);

        // A bad mesh name is refused without a step.
        Named<TextBox>(p, "ttools_deco_mesh").Text = "bush01.tga";
        Click(Named<Button>(p, "ttools_deco_add"));
        Assert.Equal(steps + 4, doc.Undo.UndoEntries.Count);

        doc.Undo.Undo();
        Assert.Equal(1.5f, Assert.Single(t.Decorations).Density, 4);
        doc.Undo.Undo();
        Assert.Equal(0.2f, t.Decorations[0].Density, 4);
        doc.Undo.Undo();
        Assert.Empty(t.Decorations);
        w.Close();
    }

    [AvaloniaFact]
    public void A_Viewport_Stroke_Is_One_Undo_Step_And_Only_A_Height_Stroke_Needs_A_Rebuild()
    {
        (EditorSession session, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level();
        var build = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { });
        build.Attach();
        var s = new TerrainBrushSettings { Tool = TerrainTool.PaintLayer, Layer = 1, Radius = 6f, Strength = 1f };
        var tools = new TerrainToolController(() => doc, () => obj, s);

        // MainWindow.TerrainTools.cs wiring, replicated: a stroke that changed heights or holes marks the build stale.
        var committed = new List<bool>();
        tools.Committed += geometry =>
        {
            committed.Add(geometry);
            if (geometry)
            {
                build.InvalidateGeometry();
            }
        };

        (float cx, float cz) = Centre(t);
        (Vector3 o, Vector3 d) = Down(cx, cz);
        Assert.False(tools.HitTest(o, d)); // not armed: clicks select as usual
        tools.Armed = true;
        Assert.True(tools.HitTest(o, d));
        Assert.False(tools.HitTest(new Vector3(cx + 500, 200, cz), d)); // off the terrain

        // Paint: one step, no rebuild.
        int steps = doc.Undo.UndoEntries.Count;
        byte[] weights = (byte[])t.Grid.Weights.Clone();
        tools.Begin(o, d);
        Assert.NotNull(tools.Stroke);
        for (int i = 1; i <= 8; i++)
        {
            (Vector3 mo, Vector3 md) = Down(cx + i, cz);
            tools.MoveTo(mo, md);
        }

        Assert.Equal(steps, doc.Undo.UndoEntries.Count); // nothing recorded mid-stroke
        Assert.NotEqual(weights, t.Grid.Weights);        // but the terrain shows it live
        Assert.NotNull(tools.LiveScene());
        Assert.True(tools.End());
        Assert.Null(tools.Stroke);
        Assert.Equal(steps + 1, doc.Undo.UndoEntries.Count);
        Assert.Equal(new[] { false }, committed);
        Assert.False(build.GeometryDirty);

        // Raise, held still (the 40 ms repeat): one step, rebuild needed.
        s.Tool = TerrainTool.Raise;
        ushort[] heights = (ushort[])t.Grid.Heights.Clone();
        tools.Begin(o, d);
        tools.Tick();
        tools.Tick();
        Assert.True(tools.End());
        Assert.Equal(steps + 2, doc.Undo.UndoEntries.Count);
        Assert.Equal(new[] { false, true }, committed);
        Assert.True(build.GeometryDirty);
        Assert.NotEqual(heights, t.Grid.Heights);

        doc.Undo.Undo();
        Assert.Equal(heights, t.Grid.Heights);

        // Esc mid-stroke restores the terrain and records nothing.
        heights = (ushort[])t.Grid.Heights.Clone();
        int before = doc.Undo.UndoEntries.Count;
        tools.Begin(o, d);
        tools.Tick();
        tools.Cancel();
        Assert.Equal(heights, t.Grid.Heights);
        Assert.Equal(before, doc.Undo.UndoEntries.Count);
        Assert.Equal(2, committed.Count);
    }

    [AvaloniaFact]
    public void Ctrl_Samples_Set_Height_And_Shift_Places_The_Mirror_Lines()
    {
        (_, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level();
        TerrainOps.Apply(doc, obj, "lift", x => TerrainOps.SetNormalizedHeights(x, (_, _) => 0.25));
        var s = new TerrainBrushSettings { Tool = TerrainTool.SetHeight, MirrorX = true };
        int changed = 0;
        var tools = new TerrainToolController(() => doc, () => obj, s) { Armed = true };
        tools.SettingsChanged += () => changed++;
        int steps = doc.Undo.UndoEntries.Count;
        (float cx, float cz) = Centre(t);

        (Vector3 o, Vector3 d) = Down(cx, cz);
        tools.Begin(o, d, ctrl: true);
        Assert.Null(tools.Stroke);
        Assert.Equal(t.Origin.Y + 4f, s.SetHeight, 2); // a quarter of the 16 m range
        Assert.StartsWith("Height", tools.Note);

        (o, d) = Down(cx + (3 * t.CellSize), cz);
        tools.Begin(o, d, shift: true);
        Assert.Null(tools.Stroke);
        Assert.Equal(6, s.MirrorXOffset2); // three cells right of the centre, in half cells
        Assert.Equal(2, changed);
        Assert.Equal(steps, doc.Undo.UndoEntries.Count);

        // The cursor rides the hover point and shows the mirror line; disarmed it vanishes.
        tools.HoverAt(o, d);
        IReadOnlyList<LineSegment> lines = tools.CursorLines();
        Assert.True(lines.Count > 32);
        tools.Armed = false;
        Assert.Empty(tools.CursorLines());
    }

    [AvaloniaFact]
    public void The_Level_Scene_Leaves_A_Stroked_Or_Hidden_Terrain_Out_And_Follows_Show_Decorations()
    {
        (EditorSession session, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level();
        TerrainOps.Apply(doc, obj, "deco", x =>
        {
            TerrainOps.SetDecoration(x, 0, new AlpineTerrainDecoration("bush01.v3m", 0.5f, 1f, 1f, 60f, 80f, 0f, AlpineTerrainRules.DecorationLinkNone, AlpineTerrainSurface.DecorationFlagRandomYaw));
            TerrainOps.FillDecorationCoverage(x, 0, 255);
        });

        RenderScene scene = session.BuildScene();
        Assert.Contains(scene.Terrains, d => d.Uid == obj.Uid);
        Assert.NotEmpty(scene.Decorations);

        session.ShowTerrainDecorations = false;
        Assert.Empty(session.BuildScene().Decorations);
        session.ShowTerrainDecorations = true;

        session.LiveTerrainUid = obj.Uid;
        scene = session.BuildScene();
        Assert.DoesNotContain(scene.Terrains, d => d.Uid == obj.Uid);
        Assert.DoesNotContain(scene.Batches, b => b.OwnerUid == obj.Uid);
        Assert.Empty(scene.Decorations);
        session.LiveTerrainUid = 0;

        doc.SelectMany(new[] { obj });
        doc.HideSelected();
        scene = session.BuildScene();
        Assert.DoesNotContain(scene.Terrains, d => d.Uid == obj.Uid);
        Assert.Empty(scene.Decorations);
    }

    [AvaloniaFact]
    public void A_Selected_Terrain_Shows_The_Tools_Above_Its_Fields()
    {
        (EditorSession session, EditorDocument doc, LevelObject obj, _) = Level();
        session.ActiveSelectKinds = SelectKinds.Objects;
        doc.SelectMany(new[] { obj });
        var settings = new TerrainBrushSettings { Radius = 9f };
        var panel = new PropertiesPanel { TerrainSettings = settings, TerrainArmed = () => true };
        panel.Bind(new TerrainFakeHost(session));
        panel.Refresh();
        Assert.NotNull(panel.TerrainPanel);
        var window = new Window { Width = 400, Height = 1600, Content = panel };
        window.Show();
        window.UpdateLayout();
        Assert.True(Named<ToggleButton>(panel.TerrainPanel!, "ttools_active").IsChecked);
        Assert.Equal(9m, Named<NumericUpDown>(panel.TerrainPanel!, "ttools_radius").Value);
        window.Close();

        doc.SelectMany(Array.Empty<LevelObject>());
        panel.Refresh();
        Assert.Null(panel.TerrainPanel);
    }

    [AvaloniaFact]
    public void Generate_Terrain_Previews_And_Applies_As_One_Undo_Step()
    {
        (_, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level();
        var dlg = new TerrainGenerateDialog(doc, obj);
        dlg.Show();
        Assert.Equal(t.Nx * t.Nz, dlg.PreviewHeights.Length);
        double[] first = dlg.PreviewHeights;
        dlg.Seed.Value = 77;
        Assert.NotEqual(first, dlg.PreviewHeights); // the preview follows the fields
        Assert.Equal(4, dlg.NoiseType.ItemCount);
        dlg.Nx.Value = 49;
        dlg.Nz.Value = 41;
        Assert.Equal(49 * 41, dlg.PreviewHeights.Length);
        Assert.Contains("49 x 41 vertices", dlg.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "tgen_stats").Text);

        int steps = doc.Undo.UndoEntries.Count;
        ushort[] heights = (ushort[])t.Grid.Heights.Clone();
        Click(dlg.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "tgen_apply"));
        Assert.True(dlg.Applied);
        Assert.Equal(steps + 1, doc.Undo.UndoEntries.Count);
        Assert.Equal(49, t.Nx);
        Assert.Equal(41, t.Nz);
        Assert.True(t.Grid.Heights.Distinct().Count() > 100);

        doc.Undo.Undo();
        Assert.Equal(33, t.Nx);
        Assert.Equal(heights, t.Grid.Heights);
    }

    // ---- screenshot evidence --------------------------------------------------------------------------------------

    private static void Save(Window w, string path)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(path);
    }

    [AvaloniaFact]
    public void Evidence_Terrain_Tools_Panel_And_Generate_Dialog()
    {
        string? output = Environment.GetEnvironmentVariable("GED_DIALOG_EVIDENCE");
        if (Environment.GetEnvironmentVariable(TestAppBuilder.RenderVariable) != "1" || output is null)
        {
            return;
        }

        Directory.CreateDirectory(output);
        (_, EditorDocument doc, LevelObject obj, AlpineTerrain t) = Level(65, 4f, geoable: true);
        TerrainOps.Apply(doc, obj, "deco", x =>
            TerrainOps.SetDecoration(x, 0, new AlpineTerrainDecoration("bush01.v3m", 0.25f, 0.8f, 1.2f, 35f, 80f, 0f, AlpineTerrainRules.DecorationLinkNone, AlpineTerrainSurface.DecorationFlagRandomYaw)));
        var s = new TerrainBrushSettings { Tool = TerrainTool.Raise, Radius = 12f, MirrorX = true };
        var panel = new TerrainToolsPanel(doc, obj, s, armed: true);
        var dock = new Window { Width = 380, Height = 1480, Content = new Border { Padding = new Avalonia.Thickness(6), Child = new ScrollViewer { Content = panel } } };
        dock.Show();
        Save(dock, Path.Combine(output, "terrain_tools_panel.png"));
        dock.Close();

        var dlg = new TerrainGenerateDialog(doc, obj);
        dlg.Show();
        Save(dlg, Path.Combine(output, "terrain_generate_height.png"));
        dlg.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "tgen_preview_shaded").IsChecked = true;
        Save(dlg, Path.Combine(output, "terrain_generate_shaded.png"));
        dlg.Close();
        Assert.True(t.Decorations.Count == 1);
    }

    private sealed class TerrainFakeHost : IEditorHost
    {
        private readonly EditorSession _s;

        public TerrainFakeHost(EditorSession s) => _s = s;

        public EditorDocument? Document => _s.Document;
        public BrushEditor? BrushEditor => _s.BrushEditor;
        public SelectionRouter Selection => _s.Selection;
        public CommandDispatcher Dispatcher => throw new NotImplementedException();
        public void RequestSceneRebuild() { }
        public void RequestHistoryJump(UndoNode target) { }
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
}
