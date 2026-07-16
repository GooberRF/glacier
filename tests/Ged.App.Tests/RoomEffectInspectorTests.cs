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
/// The room-effect properties inspector: selecting a room effect builds a dedicated inspector
/// exposing the effect type, room flags and (for a liquid room) the full liquid block — the
/// answer to "why can't I select and view properties of a room effect object". Editing a field
/// through the panel routes through the document's undo system and dirties the section.
/// </summary>
public sealed class RoomEffectInspectorTests
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

    private static (EditorSession Session, PropertiesPanel Panel, LevelObject Fx) SetUpLiquidRoomEffect()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        RflSection sec = doc.Rfl.GetOrCreateSection(SectionType.RoomEffects, () => new RoomEffectsSection());
        var re = new RoomEffect
        {
            EffectType = RoomEffectsSection.EffectLiquidRoom,
            LiquidProperties = new RoomEffectLiquidProperties
            {
                Waveform = 2, Depth = 4f, SurfaceTexture = "water.tga", LiquidType = 1, Visibility = 8f,
            },
            Header = new ObjectHeader { Uid = 8001, ClassName = "Room Effect", ScriptName = "Room Effect", Position = new Vec3(1, 2, 3) },
        };
        ((RoomEffectsSection)sec.Content!).Effects.Add(re);
        sec.Dirty = true;
        doc.RefreshObjects();

        LevelObject fx = doc.FindByUid(8001)!;
        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(fx);

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));
        panel.Refresh();
        return (session, panel, fx);
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

    [AvaloniaFact]
    public void Selecting_A_Room_Effect_Builds_The_Inspector_With_Type_And_Liquid_Fields()
    {
        var (_, panel, _) = SetUpLiquidRoomEffect();
        Control root = Root(panel);
        var labels = Walk(root).OfType<TextBlock>().Select(t => t.Text).ToHashSet();

        // Header + common fields prove the object is selectable and inspectable.
        Assert.Contains("UID", labels);
        Assert.Contains("Script Name", labels);
        Assert.Contains("Position", labels);
        Assert.Contains("Effect Type", labels);
        Assert.Contains("Room Is Cold", labels);
        Assert.Contains("Room Is Outside", labels);
        Assert.Contains("Room Is Air Lock", labels);

        // The liquid block is present for a liquid room effect.
        Assert.Contains("Depth", labels);
        Assert.Contains("Surface Texture", labels);
        Assert.Contains("Waveform", labels);
        Assert.Contains("Liquid Type", labels);
        Assert.Contains("Visibility", labels);
    }

    [AvaloniaFact]
    public void Effect_Type_Combo_Shows_The_Current_Type()
    {
        var (_, panel, _) = SetUpLiquidRoomEffect();
        var combo = RowEditor(Root(panel), "Effect Type") as ComboBox;
        Assert.NotNull(combo);
        // Liquid Room is enum value 2 -> zero-based index 1.
        Assert.Equal(1, combo!.SelectedIndex);
    }

    [AvaloniaFact]
    public void Placing_A_Room_Effect_Selects_It_And_The_Inspector_Shows()
    {
        // The palette flow: PlaceFromPalette -> Document.PlaceObject -> OnObjectPlaced selects
        // the new object; the properties panel then shows the room-effect inspector with RED's
        // defaults (type None: header fields + type + flags, no nested block).
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        LevelObject placed = doc.PlaceObject(LevelObjectKind.RoomEffect, new Vec3(1, 2, 3))!;
        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(placed);
        Assert.Contains(placed, doc.Selection);

        var panel = new PropertiesPanel();
        panel.Bind(new FakeHost(session));
        panel.Refresh();

        Control root = Root(panel);
        var labels = Walk(root).OfType<TextBlock>().Select(t => t.Text).ToHashSet();
        Assert.Contains("Effect Type", labels);
        Assert.Contains("Room Is Cold", labels);
        Assert.DoesNotContain("Liquid Properties", labels);
        Assert.DoesNotContain("Ambient Light", labels);

        // The type combo reflects RED's default: None (enum 4 -> index 3).
        var combo = RowEditor(root, "Effect Type") as ComboBox;
        Assert.NotNull(combo);
        Assert.Equal(3, combo!.SelectedIndex);
    }

    [AvaloniaFact]
    public void Palette_Room_Effect_Leaf_Has_The_RoomFx_Icon()
    {
        // The palette icon renders a viewport-tinted atlas cell. Every placeable kind now maps
        // to its billboard glyph (item 1c) — including previously-unmapped kinds like Trigger.
        var bmp = PaletteIcons.TryFor(LevelObjectKind.RoomEffect);
        Assert.NotNull(bmp);
        Assert.Equal(32, bmp!.PixelSize.Width);
        Assert.Equal(32, bmp.PixelSize.Height);
        Assert.NotNull(PaletteIcons.TryFor(LevelObjectKind.Trigger));

        // Cached: the same bitmap instance comes back.
        Assert.Same(bmp, PaletteIcons.TryFor(LevelObjectKind.RoomEffect));
    }

    [AvaloniaFact]
    public void Editing_Depth_Through_The_Panel_Is_Undoable_And_Dirties_The_Section()
    {
        var (session, panel, fx) = SetUpLiquidRoomEffect();
        var model = (RoomEffect)fx.Model;
        EditorDocument doc = session.Document!;
        fx.Section.Dirty = false; // start from a clean section to prove the edit dirties it

        var depthBox = RowEditor(Root(panel), "Depth") as TextBox;
        Assert.NotNull(depthBox);

        depthBox!.Text = "12.5";
        depthBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal(12.5f, model.LiquidProperties!.Depth);
        Assert.True(fx.Section.Dirty);

        doc.Undo.Undo();
        Assert.Equal(4f, model.LiquidProperties!.Depth);
    }
}
