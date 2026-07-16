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
/// The Alpine mesh-object properties inspector (alpine-gap-inventory items 6/7/10): selecting a
/// mesh object builds a dedicated inspector exposing the filename (with Browse), a named-enum
/// Material, the per-slot texture-override list editor, and — when Is Clutter is set — the full
/// clutter-behaviour group (life/debris/explosion/damage factors/corpse). Every edit routes
/// through the document's undo system and dirties the alpine_mesh_objects section.
/// </summary>
public sealed class MeshInspectorTests
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

    private static (EditorSession Session, PropertiesPanel Panel, LevelObject Mo, AlpineMeshObject Mesh) SetUp(
        Action<AlpineMeshObject>? configure = null)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        RflSection sec = doc.Rfl.GetOrCreateSection(SectionType.AlpineMeshObjects, () => new AlpineMeshObjectsSection());
        var mesh = new AlpineMeshObject
        {
            Uid = 9001, Position = new Vec3(1, 2, 3), Orientation = Mat3.Identity,
            ScriptName = "widget", MeshFilename = "widget.v3m", StateAnim = string.Empty,
            CollisionMode = 2, Material = 2,
        };
        configure?.Invoke(mesh);
        ((AlpineMeshObjectsSection)sec.Content!).Meshes.Add(mesh);
        sec.Dirty = true;
        doc.RefreshObjects();

        LevelObject mo = doc.FindByUid(9001)!;
        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(mo);

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));
        panel.Refresh();
        return (session, panel, mo, mesh);
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

    private static Control? RowEditor(Control root, string label) =>
        Walk(root).OfType<Grid>()
            .Where(g => g.Children.Count >= 2 && g.Children[0] is TextBlock tb && tb.Text == label)
            .Select(g => g.Children[1] as Control)
            .FirstOrDefault();

    private static HashSet<string?> Labels(PropertiesPanel panel) =>
        Walk(Root(panel)).OfType<TextBlock>().Select(t => t.Text).ToHashSet();

    [AvaloniaFact]
    public void Selecting_A_Mesh_Object_Builds_The_Inspector_With_Base_Fields()
    {
        var (_, panel, _, _) = SetUp();
        var labels = Labels(panel);

        Assert.Contains("UID", labels);
        Assert.Contains("Script Name", labels);
        Assert.Contains("Position", labels);
        Assert.Contains("Mesh Filename", labels);
        Assert.Contains("State Anim", labels);
        Assert.Contains("Collision Mode", labels);
        Assert.Contains("Material", labels);
        Assert.Contains("Is Clutter", labels);

        // A non-clutter mesh does NOT show the clutter group.
        Assert.DoesNotContain("Clutter Behaviour", labels);
    }

    [AvaloniaFact]
    public void Material_Combo_Shows_The_Named_Value()
    {
        var (_, panel, _, _) = SetUp();
        var combo = RowEditor(Root(panel), "Material") as ComboBox;
        Assert.NotNull(combo);
        Assert.Equal(2, combo!.SelectedIndex);          // Material 2
        Assert.Equal("Metal", combo.SelectedItem);      // = "Metal"
    }

    [AvaloniaFact]
    public void Mesh_Filename_Row_Has_A_Browse_Button()
    {
        var (_, panel, _, _) = SetUp();
        var editor = RowEditor(Root(panel), "Mesh Filename");
        Assert.NotNull(editor);
        bool hasBrowse = Walk(editor!).OfType<Button>().Any(b => (b.Content as string) == "Browse…");
        Assert.True(hasBrowse);
    }

    [AvaloniaFact]
    public void Filename_Edit_Fixes_Legacy_Extension_And_Is_Undoable()
    {
        var (session, panel, _, mesh) = SetUp();
        EditorDocument doc = session.Document!;
        var box = RowEditor(Root(panel), "Mesh Filename") is DockPanel dp
            ? dp.Children.OfType<TextBox>().First()
            : null;
        Assert.NotNull(box);

        box!.Text = "legacy.v3d";
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal("legacy.v3m", mesh.MeshFilename); // v3d -> v3m fixup

        doc.Undo.Undo();
        Assert.Equal("widget.v3m", mesh.MeshFilename);
    }

    [AvaloniaFact]
    public void Toggling_Is_Clutter_Reveals_The_Behaviour_Group_With_Alpine_Defaults()
    {
        var (session, panel, _, mesh) = SetUp();
        EditorDocument doc = session.Document!;

        var check = RowEditor(Root(panel), "Is Clutter") as CheckBox;
        Assert.NotNull(check);
        check!.IsChecked = true; // fires the toggle + Refresh()

        Assert.Equal(1, mesh.IsClutter);
        Assert.NotNull(mesh.Clutter);
        // Alpine MeshClutterProps defaults.
        Assert.Equal(-1f, mesh.Clutter!.Life);
        Assert.Equal(1f, mesh.Clutter.ExplosionRadius);
        Assert.Equal(10f, mesh.Clutter.DebrisVelocity);
        Assert.Equal((sbyte)-1, mesh.Clutter.CorpseMaterial);

        var labels = Labels(panel);
        Assert.Contains("Clutter Behaviour", labels);
        Assert.Contains("Life", labels);
        Assert.Contains("Debris Filename", labels);
        Assert.Contains("Explosion Radius", labels);
        Assert.Contains("Damage Type Factors", labels);
        Assert.Contains("Corpse", labels);
        Assert.Contains("Corpse Material", labels);
        Assert.Contains("Bash", labels);
        Assert.Contains("Crush", labels);

        doc.Undo.Undo();
        Assert.Equal(0, mesh.IsClutter);
    }

    [AvaloniaFact]
    public void Editing_Clutter_Life_Through_The_Panel_Is_Undoable()
    {
        var (session, panel, mo, mesh) = SetUp(m => { m.IsClutter = 1; m.Clutter!.Life = 50f; });
        EditorDocument doc = session.Document!;
        mo.Section.Dirty = false;

        var life = RowEditor(Root(panel), "Life") as TextBox;
        Assert.NotNull(life);
        life!.Text = "123";
        life.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal(123f, mesh.Clutter!.Life);
        Assert.True(mo.Section.Dirty);

        doc.Undo.Undo();
        Assert.Equal(50f, mesh.Clutter!.Life);
    }

    [AvaloniaFact]
    public void Corpse_Material_Combo_Maps_Automatic_To_Minus_One()
    {
        var (session, panel, _, mesh) = SetUp(m => { m.IsClutter = 1; m.Clutter!.CorpseMaterial = 3; });
        var combo = RowEditor(Root(panel), "Corpse Material") as ComboBox;
        Assert.NotNull(combo);
        Assert.Equal(4, combo!.SelectedIndex); // material 3 -> index 4 (Automatic is index 0)

        combo.SelectedIndex = 0; // Automatic
        Assert.Equal((sbyte)-1, mesh.Clutter!.CorpseMaterial);

        combo.SelectedIndex = 1; // Default (material 0)
        Assert.Equal((sbyte)0, mesh.Clutter!.CorpseMaterial);
    }

    [AvaloniaFact]
    public void Texture_Overrides_Render_And_Add_Remove_Are_Undoable()
    {
        var (session, panel, mo, mesh) = SetUp(m =>
            m.TextureOverrides.Add(new AlpineMeshTextureOverride { SlotId = 0, Filename = "base.tga" }));
        EditorDocument doc = session.Document!;

        // The existing override renders with its filename.
        var boxes = Walk(Root(panel)).OfType<TextBox>().Select(b => b.Text).ToList();
        Assert.Contains("base.tga", boxes);
        Assert.Contains("Texture Overrides (1)", Labels(panel).Select(s => s ?? string.Empty));

        // Add appends a new override.
        var add = Walk(Root(panel)).OfType<Button>().First(b => (b.Content as string) == "+ Add Override");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, mesh.TextureOverrides.Count);
        Assert.Equal((byte)1, mesh.TextureOverrides[1].SlotId); // next free slot

        doc.Undo.Undo();
        Assert.Single(mesh.TextureOverrides);
    }
}
