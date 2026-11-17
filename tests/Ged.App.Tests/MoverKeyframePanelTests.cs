using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Tests;

/// <summary>
/// The docked Keyframe panel (RED's Keyframe Properties in two parts), the mover preview and the curve-handle drag:
/// every control shows the document's value, every edit is one undo step through the shared accessors, several
/// keyframes edit together (mixed values blank and untouched), ◀ ▶ step and select, Alpine's greying, RED's propagate
/// buttons, a preview that plays and stops without touching the level, and a handle drag that is one undo step.
/// </summary>
public sealed class MoverKeyframePanelTests
{
    internal static (EditorSession Session, EditorDocument Doc, Group Mover, MoverService Movers) MoverLevel(bool legacy = false)
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        if (legacy)
        {
            LevelPropertyAccess.Set(doc, AlpineLevelPropertiesSchema.Fields.Single(f => f.Field.EffectiveKey == "legacy_movers"), true);
        }

        int box = session.BrushEditor!.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new CoreVec3(0, 1, 0), Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        movers.AddKeyframe(g, new CoreVec3(8, 1, 0), Mat3.Identity);
        movers.AddKeyframe(g, new CoreVec3(8, 1, 8), Mat3.Identity);
        doc.MarkSaved();
        return (session, doc, g, movers);
    }

    private static (Window Window, MoverKeyframePanel Panel) Show(EditorDocument doc, Group g, IReadOnlyList<Keyframe> keys, MoverPanelServices? services = null)
    {
        var panel = new MoverKeyframePanel(doc, g, keys, services);
        var window = new Window { Width = 420, Height = 1400, Content = new ScrollViewer { Content = panel } };
        window.Show();
        window.UpdateLayout();
        return (window, panel);
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Every_Control_Shows_The_Movers_And_The_Keyframes_Values()
    {
        (_, EditorDocument doc, Group g, _) = MoverLevel();
        MovingGroupData d = g.MovingData!;
        d.IsDoor = 1;
        d.MovementType = 3;
        d.StartSound = "lift_start.wav";
        d.LoopingVol = 0.5f;
        Keyframe k = d.Keyframes[1];
        k.ScriptName = "Keyframe";
        k.PauseTime = 2f;
        k.DepartTravelTime = 0.75f;
        k.EventUid = 42;
        k.DegreesAboutAxis = 15f;

        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { k });
        Assert.True(Named<CheckBox>(p, "mover_is_door").IsChecked);
        Assert.False(Named<CheckBox>(p, "mover_rotate_in_place").IsChecked);
        Assert.Equal(2, Named<ComboBox>(p, "mover_movement_type").SelectedIndex); // Ping Pong Infinite
        Assert.Equal(1m, Named<NumericUpDown>(p, "mover_starting_keyframe").Value); // RED shows the first keyframe as 1
        Assert.Equal("lift_start.wav", Named<TextBox>(p, "mover_sound_start").Text);
        Assert.Equal(0.5m, Named<NumericUpDown>(p, "mover_volume_loop").Value);
        Assert.Equal("Keyframe", Named<TextBox>(p, "kf_script_name").Text);
        Assert.Equal(2m, Named<NumericUpDown>(p, "kf_pause_time").Value);
        Assert.Equal(0.75m, Named<NumericUpDown>(p, "kf_depart_travel_time").Value);
        Assert.Equal(42m, Named<NumericUpDown>(p, "kf_event_uid").Value);
        Assert.Null(Named<NumericUpDown>(p, "kf_item_uid1").Value); // -1 shows blank, as RED shows it
        Assert.Equal(15m, Named<NumericUpDown>(p, "kf_degrees_about_axis").Value);
        Assert.Equal(0, Named<ComboBox>(p, "kf_path_segment").SelectedIndex);
        Assert.Equal(0, Named<ComboBox>(p, "kf_mover_facing").SelectedIndex);
        Assert.Contains(p.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "This keyframe (2 of 3)");
        Assert.Contains(p.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Mover (whole chain)", StringComparison.Ordinal) == true);
        w.Close();
    }

    [AvaloniaFact]
    public void Each_Edit_Is_One_Undo_Step_Through_The_Shared_Accessors()
    {
        (_, EditorDocument doc, Group g, _) = MoverLevel();
        Keyframe k = g.MovingData!.Keyframes[1];
        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { k });
        int nodes = doc.Undo.NodeCount;

        Named<NumericUpDown>(p, "kf_pause_time").Value = 3m;
        Assert.Equal(3f, k.PauseTime);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);

        Named<CheckBox>(p, "mover_is_door").IsChecked = true;
        Assert.Equal((byte)1, g.MovingData.IsDoor);
        Named<ComboBox>(p, "mover_movement_type").SelectedIndex = 4;
        Assert.Equal(5, g.MovingData.MovementType);
        Named<NumericUpDown>(p, "mover_starting_keyframe").Value = 2m;
        Assert.Equal(1, g.MovingData.StartingKeyframe); // stored zero-based
        Named<ComboBox>(p, "kf_path_segment").SelectedIndex = 1;
        Assert.Equal(1, (int)AlpineAttachedData.KeyframeData(doc.Rfl, k.Uid).SegmentMode);
        Named<NumericUpDown>(p, "kf_event_uid").Value = null;
        Assert.Equal(-1, k.EventUid);
        Assert.Equal(nodes + 5, doc.Undo.NodeCount); // the UID was already -1: no step

        for (int i = 0; i < 5; i++)
        {
            doc.Undo.Undo();
        }

        Assert.Equal(0f, k.PauseTime);
        Assert.Equal((byte)0, g.MovingData.IsDoor);
        Assert.Equal(1, g.MovingData.MovementType);
        Assert.Equal(0, (int)AlpineAttachedData.KeyframeData(doc.Rfl, k.Uid).SegmentMode);
        w.Close();
    }

    [AvaloniaFact]
    public void Several_Keyframes_Edit_Together_And_Mixed_Values_Stay_Untouched()
    {
        (_, EditorDocument doc, Group g, _) = MoverLevel();
        List<Keyframe> keys = g.MovingData!.Keyframes;
        keys[1].PauseTime = 1f;
        keys[2].PauseTime = 2f;
        keys[1].ScriptName = "a";
        keys[2].ScriptName = "b";
        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { keys[1], keys[2] });
        Assert.Contains(p.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "These keyframes (2 selected of 3)");
        Assert.Null(Named<NumericUpDown>(p, "kf_pause_time").Value);         // mixed: blank
        Assert.Equal(1m, Named<NumericUpDown>(p, "kf_depart_travel_time").Value); // shared: shown
        Assert.Equal(string.Empty, Named<TextBox>(p, "kf_script_name").Text);
        int nodes = doc.Undo.NodeCount;

        Named<NumericUpDown>(p, "kf_accel_time").Value = 0.5m;
        Assert.All(new[] { keys[1], keys[2] }, k => Assert.Equal(0.5f, k.AccelTime));
        Assert.Equal(0f, keys[0].AccelTime);
        Assert.Equal(1f, keys[1].PauseTime); // the mixed field was left as it was
        Assert.Equal(2f, keys[2].PauseTime);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount); // one step for both

        doc.Undo.Undo();
        Assert.All(new[] { keys[1], keys[2] }, k => Assert.Equal(0f, k.AccelTime));
        w.Close();
    }

    [AvaloniaFact]
    public void The_Arrows_Step_Through_The_Chain_And_Select_The_Keyframe()
    {
        (_, EditorDocument doc, Group g, _) = MoverLevel();
        var selected = new List<LevelObject>();
        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { g.MovingData!.Keyframes[0] }, new MoverPanelServices { SelectObject = selected.Add });
        Click(Named<Button>(p, "kf_next"));
        Assert.Equal(1, p.ShownIndex);
        Assert.Equal(g.MovingData.Keyframes[1].Uid, selected.Last().Uid);
        Click(Named<Button>(p, "kf_prev"));
        Click(Named<Button>(p, "kf_prev"));
        Assert.Equal(2, p.ShownIndex); // wraps round the chain
        w.Close();
    }

    [AvaloniaFact]
    public void Path_Fields_Grey_Out_As_Alpine_RED_Greys_Them()
    {
        (_, EditorDocument doc, Group g, _) = MoverLevel(legacy: true);
        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { g.MovingData!.Keyframes[1] });
        Assert.False(Named<ComboBox>(p, "kf_path_segment").IsEnabled);
        Assert.False(Named<ComboBox>(p, "kf_path_ease").IsEnabled);
        Assert.False(Named<CheckBox>(p, "kf_riders_turn").IsEnabled);
        w.Close();

        (_, doc, g, _) = MoverLevel();
        (w, p) = Show(doc, g, new[] { g.MovingData!.Keyframes[1] });
        Assert.True(Named<ComboBox>(p, "kf_path_segment").IsEnabled);
        Assert.False(Named<ComboBox>(p, "kf_segment_facing").IsEnabled); // the mover has no facing of its own
        Named<ComboBox>(p, "kf_mover_facing").SelectedIndex = 1;
        Assert.True(Named<ComboBox>(p, "kf_segment_facing").IsEnabled);
        Named<CheckBox>(p, "mover_rotate_in_place").IsChecked = true;
        Assert.False(Named<ComboBox>(p, "kf_path_segment").IsEnabled);
        Assert.True(Named<ComboBox>(p, "kf_path_ease").IsEnabled); // easing still applies to a spinning mover
        w.Close();
    }

    [AvaloniaFact]
    public void Propagate_Buttons_Copy_By_Script_Name_And_Refuse_The_Default_Name()
    {
        (_, EditorDocument doc, Group g, MoverService movers) = MoverLevel();
        int box2 = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new CoreVec3(20, 1, 0), Mat3.Identity);
        Group other = movers.CreateMover(new[] { box2 }, Array.Empty<int>(), "Lift2");
        g.MovingData!.Keyframes[0].ScriptName = "lift";
        other.MovingData!.Keyframes[0].ScriptName = "LIFT";
        g.MovingData.StopSound = "stop.wav";
        var messages = new List<string>();
        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { g.MovingData.Keyframes[0] }, new MoverPanelServices { ShowMessage = messages.Add });

        Click(Named<Button>(p, "mover_propagate_sounds"));
        Assert.Equal("stop.wav", other.MovingData.StopSound);
        Assert.Contains("2 mover", messages.Last(), StringComparison.Ordinal);

        g.MovingData.Keyframes[0].ScriptName = "Keyframe";
        Click(Named<Button>(p, "mover_propagate_properties"));
        Assert.Contains("default script name", messages.Last(), StringComparison.Ordinal);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Preview_Plays_Scrubs_And_Stops_Without_Touching_The_Level()
    {
        (_, EditorDocument doc, Group g, MoverService movers) = MoverLevel();
        var preview = new MoverPreviewController(() => doc) { UseTimer = false };
        (Window w, MoverKeyframePanel p) = Show(doc, g, new[] { g.MovingData!.Keyframes[0] }, new MoverPanelServices { Preview = preview });
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;
        Brush member = movers.CurrentMemberShapes(g)[0];
        CoreVec3 rest = member.Position;

        Click(Named<Button>(p, "mover_preview"));
        w.UpdateLayout();
        Assert.True(preview.Active);
        Assert.Equal("Play", Named<Button>(p, "preview_play").Content);
        float RestX() => preview.GhostLines(movers).Min(l => MathF.Min(l.A.X, l.B.X));
        Assert.Equal(-1f, RestX(), 3); // the 2 m box around x = 0

        Click(Named<Button>(p, "preview_play"));
        for (int i = 0; i < 5; i++)
        {
            preview.Tick(0.1f); // real time arrives in frames (at most 0.1 s each, as Alpine RED's preview)
        }

        p.SyncTransport();
        Assert.Equal("Pause", Named<Button>(p, "preview_play").Content);
        Assert.Equal(3f, RestX(), 3);
        Assert.Contains("Moving", Named<TextBlock>(p, "preview_status").Text, StringComparison.Ordinal);

        Named<Slider>(p, "preview_scrub").Value = 0.5; // half the run: the end of the first segment
        Assert.False(preview.Playing);
        Assert.Equal(7f, RestX(), 3);

        Click(Named<Button>(p, "preview_stop"));
        Assert.Equal(-1f, RestX(), 3);
        Assert.Equal(rest, member.Position);
        Assert.Equal(nodes, doc.Undo.NodeCount);
        Assert.False(doc.IsDirty);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        Click(Named<Button>(p, "preview_close"));
        Assert.Empty(preview.GhostLines(movers));
        w.Close();
    }

    [AvaloniaFact]
    public void Dragging_A_Curve_Handle_Is_One_Undo_Step_And_Leaves_Auto_For_Aligned()
    {
        (EditorSession session, EditorDocument doc, Group g, _) = MoverLevel();
        List<Keyframe> keys = g.MovingData!.Keyframes;
        InspectorField segment = MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == "path_segment");
        foreach (Keyframe k in keys)
        {
            InspectorAccess.Set(doc, doc.FindByUid(k.Uid)!, segment, 1);
        }

        session.Selection.SelectObject(doc.FindByUid(keys[1].Uid)!);
        var tool = new MoverHandleTool(doc);
        IReadOnlyList<MoverHandle> handles = tool.Visible(doc.Selection);
        MoverHandle grab = handles.Single(h => ReferenceEquals(h.Keyframe, keys[1]) && h.Out);
        float inLength = AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid).InHandle.Length();
        (CoreVec3 autoIn, _) = MoverPathGeometry.ResolvedHandles(doc.Rfl, g, keys[1].Uid)!.Value;
        int nodes = doc.Undo.NodeCount;

        // Looking straight down at the handle; the cursor ray moves 2 m along +Z.
        var down = new Vector3(0, -1, 0);
        Assert.Same(grab, MoverHandleTool.Pick(handles, w => new Vector2(w.X * 10, w.Z * 10), new Vector2(grab.Point.X * 10, grab.Point.Z * 10), grab.Point + new Vector3(0, 10, 0), down));
        tool.Begin(grab, grab.Point + new Vector3(0, 10, 0), down);
        Assert.True(tool.Move(grab.Point + new Vector3(0, 10, 1), down));
        Assert.True(tool.Move(grab.Point + new Vector3(0, 10, 2), down));
        Assert.True(tool.End());
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);

        var data = AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid);
        Assert.Equal(1, (int)data.TangentMode); // Auto became Aligned
        CoreVec3 expectedOut = new CoreVec3(grab.Point.X, grab.Point.Y, grab.Point.Z + 2).Sub(keys[1].Position);
        Assert.Equal(expectedOut.X, data.OutHandle.X, 3);
        Assert.Equal(expectedOut.Z, data.OutHandle.Z, 3);
        Assert.Equal(autoIn.Length(), data.InHandle.Length(), 3); // the other handle keeps its length ...
        Assert.Equal(-1f, data.InHandle.Normalized().Dot(data.OutHandle.Normalized()), 3); // ... turned opposite
        _ = inLength;

        doc.Undo.Undo();
        Assert.Equal(0, (int)AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid).TangentMode);

        tool.Begin(tool.Visible(doc.Selection).First(), Vector3.UnitY * 10, down);
        tool.Move(new Vector3(3, 10, 3), down);
        int position = doc.Undo.Position;
        Assert.True(tool.Cancel());
        Assert.Equal(0, (int)AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid).TangentMode);
        Assert.Equal(position, doc.Undo.Position); // a cancelled drag leaves no step
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
    }

    [AvaloniaFact]
    public void Handles_Show_Only_For_Curved_Segments_Of_A_Selected_Keyframes_Mover()
    {
        (EditorSession session, EditorDocument doc, Group g, _) = MoverLevel();
        var tool = new MoverHandleTool(doc);
        session.Selection.SelectObject(doc.FindByUid(g.MovingData!.Keyframes[0].Uid)!);
        Assert.Empty(tool.Visible(doc.Selection)); // straight segments have no handles
        InspectorAccess.Set(doc, doc.FindByUid(g.MovingData.Keyframes[0].Uid)!, MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == "path_segment"), 1);
        Assert.Equal(2, tool.Visible(doc.Selection).Count); // the out handle of 1 and the in handle of 2
        doc.ClearSelection();
        Assert.Empty(tool.Visible(doc.Selection));
    }

    [AvaloniaFact]
    public void The_Keyframe_Dialog_Hosts_The_Same_Panel()
    {
        (_, EditorDocument doc, Group g, _) = MoverLevel();
        var dlg = new KeyframePropertiesDialog(doc, g, new[] { g.MovingData!.Keyframes[2] }, new MoverPanelServices());
        dlg.Show();
        dlg.UpdateLayout();
        Assert.Equal("Keyframe Properties", dlg.Title);
        Assert.Equal(2, dlg.Panel.ShownIndex);
        Named<NumericUpDown>(dlg, "kf_pause_time").Value = 4m;
        Assert.Equal(4f, g.MovingData.Keyframes[2].PauseTime);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Selecting_A_Keyframe_Or_A_Mover_Member_Shows_The_Keyframe_Panel()
    {
        (EditorSession session, EditorDocument doc, Group g, _) = MoverLevel();
        var panel = new PropertiesPanel();
        panel.Bind(new PanelHost(session));
        session.ActiveSelectKinds = SelectKinds.Objects;
        session.Selection.SelectObject(doc.FindByUid(g.MovingData!.Keyframes[1].Uid)!);
        panel.Refresh();
        Assert.NotNull(panel.MoverPanel);
        Assert.Equal(1, panel.MoverPanel!.ShownIndex);

        doc.ClearSelection();
        session.ActiveSelectKinds = SelectKinds.Brushes;
        session.Selection.SelectBrush(g.Brushes[0]);
        panel.Refresh();
        Assert.NotNull(panel.MoverPanel);
        Assert.Equal(0, panel.MoverPanel!.ShownIndex); // the starting keyframe
    }

    /// <summary>The minimal editor host the Properties panel needs.</summary>
    private sealed class PanelHost : IEditorHost
    {
        private readonly EditorSession _s;

        public PanelHost(EditorSession s) => _s = s;

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
        public CoreVec3 PlacementPoint => default;
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
        public void PlaceEventFromPalette(EventSchema schema) { }
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
