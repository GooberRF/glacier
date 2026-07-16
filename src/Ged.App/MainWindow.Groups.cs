using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.App.Dialogs;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using CoreVec3 = Ged.Core.Model.Vec3;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// Group-mode UI: the group tree + operations, mover / keyframe
/// authoring with a time-scrubbed path preview, cutscene path editing, .rfg
/// save/load, and the viewport overlays (mover path + ghost, cutscene camera
/// cone, nav-point disc, decal box) that layer over the selection line set.
/// </summary>
public sealed partial class MainWindow
{
    private GroupService? _groups;
    private MoverService? _movers;
    private CutsceneService? _cutscenes;
    private float _moverTransportT = 0.5f;
    private bool _showMoverGhost = true;

    private void RefreshGroupPanelIfActive()
    {
        if (BrushEd?.Mode == EditMode.Group)
        {
            _modePanel.SetContent(BuildGroupPanel());
            RefreshSelectionOverlay();
        }
    }

    // ---- Group-mode tool panel ------------------------------------------------

    private Control BuildGroupPanel()
    {
        var root = new StackPanel { Margin = new Avalonia.Thickness(8), Spacing = 6 };
        if (Document is null || _groups is null || _movers is null)
        {
            root.Children.Add(Note("Open or create a level first."));
            return root;
        }

        root.Children.Add(Header("Group / Mover Mode"));
        root.Children.Add(Note("Select brushes (in Brush mode) and objects, then group, mirror, or make a mover."));
        root.Children.Add(Row(Btn("Create Group…", () => _ = CreateGroupAsync()), Btn("Create Mover", CreateMoverFromSelection)));
        root.Children.Add(Row(Btn("Mirror X", () => MirrorGroupSelection(0)), Btn("Mirror Y", () => MirrorGroupSelection(1)), Btn("Mirror Z", () => MirrorGroupSelection(2))));
        root.Children.Add(Row(Btn("Save .rfg…", () => _ = SaveRfgAsync()), Btn("Load .rfg…", () => _ = LoadRfgAsync())));

        root.Children.Add(Header("Groups"));
        root.Children.Add(BuildGroupTree());

        // Mover / keyframe inspector for a single selected mover.
        if (SelectedMoverGroup() is { } moverGroup)
        {
            root.Children.Add(BuildMoverInspector(moverGroup));
        }

        // Cutscene path editor.
        root.Children.Add(Header("Cutscene Paths"));
        root.Children.Add(BuildCutsceneEditor());

        return root;
    }

    private Control BuildGroupTree()
    {
        var tree = new TreeView { MaxHeight = 220 };
        var roots = new List<TreeViewItem>();

        // Master groups: auto per object type.
        var master = new TreeViewItem { Header = "Master Groups (by type)", IsExpanded = false };
        foreach (IGrouping<LevelObjectKind, LevelObject> g in Document!.Objects.GroupBy(o => o.Kind).OrderBy(g => g.Key.ToString()))
        {
            var item = new TreeViewItem { Header = $"{g.Key} ({g.Count()})" };
            IGrouping<LevelObjectKind, LevelObject> captured = g;
            item.DoubleTapped += (_, _) => { _session.Selection.SelectObjects(captured); AfterMutation(); };
            master.Items.Add(item);
        }

        roots.Add(master);

        // User-defined groups.
        var user = new TreeViewItem { Header = $"User-Defined Groups ({_groups!.Groups.Count})", IsExpanded = true };
        foreach (Group grp in _groups.Groups)
        {
            Group captured = grp;
            string lockMark = _groups.IsLocked(grp) ? " 🔒" : string.Empty;
            var item = new TreeViewItem { Header = $"{grp.Name}  [{grp.Brushes.Count}b {grp.Objects.Count}o]{lockMark}", Tag = grp };
            item.DoubleTapped += (_, _) => { _groups.SelectGroup(captured); SelectGroupBrushes(captured); AfterMutation(); };
            item.ContextMenu = GroupContextMenu(captured);
            user.Items.Add(item);
        }

        roots.Add(user);

        // Moving groups.
        var moving = new TreeViewItem { Header = $"Moving Groups ({_movers!.Movers.Count})", IsExpanded = true };
        foreach (Group grp in _movers.Movers)
        {
            Group captured = grp;
            int kf = grp.MovingData?.Keyframes.Count ?? 0;
            var item = new TreeViewItem { Header = $"{grp.Name}  [{kf} keyframes]", Tag = grp };
            item.DoubleTapped += (_, _) => SelectMoverGroup(captured);
            moving.Items.Add(item);
        }

        roots.Add(moving);
        tree.ItemsSource = roots;
        return tree;
    }

    private ContextMenu GroupContextMenu(Group grp)
    {
        var menu = new ContextMenu();
        void Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("Select", () => { _groups!.SelectGroup(grp); SelectGroupBrushes(grp); AfterMutation(); });
        Item("Duplicate", () => { _groups!.Duplicate(grp); AfterMutation(); });
        Item("Rename…", () => _ = RenameGroupAsync(grp));
        Item(_groups!.IsLocked(grp) ? "Unlock" : "Lock", () => { _groups.SetLocked(grp, !_groups.IsLocked(grp)); RefreshGroupPanelIfActive(); });
        Item("Add Selection", () => { _groups.AddMembers(grp, SelectedBrushUids(), SelectedObjectUids()); AfterMutation(); });
        Item("Remove Selection", () => { _groups.RemoveMembers(grp, SelectedBrushUids().Concat(SelectedObjectUids())); AfterMutation(); });
        Item("Mirror X", () => { _groups.MirrorGroup(grp, 0); AfterMutation(); });
        Item("Dissolve", () => { _groups.Dissolve(grp); AfterMutation(); });
        Item("Dissolve Temporary", () => { _groups.DissolveTemporary(grp); AfterMutation(); });
        return menu;
    }

    // ---- Mover / keyframe inspector -------------------------------------------

    private Group? _selectedMover;

    private Group? SelectedMoverGroup()
    {
        // A selected Mover object resolves to its owning moving group.
        LevelObject? mover = Document?.Selection.FirstOrDefault(o => o.Kind == LevelObjectKind.Mover);
        if (mover is not null)
        {
            _selectedMover = _movers?.FindGroupForMember(mover.Uid);
        }

        return _selectedMover is not null && _movers?.Movers.Contains(_selectedMover) == true ? _selectedMover : null;
    }

    private void SelectMoverGroup(Group grp)
    {
        _selectedMover = grp;
        // Select the first member brush/object so the inspector shows.
        LevelObject? o = grp.Brushes.Concat(grp.Objects).Select(u => Document?.FindByUid(u)).FirstOrDefault(x => x is not null);
        if (o is not null)
        {
            _session.Selection.SelectObject(o);
        }

        RefreshGroupPanelIfActive();
    }

    private Control BuildMoverInspector(Group group)
    {
        MovingGroupData data = group.MovingData ??= new MovingGroupData();
        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(Header($"Mover: {group.Name}"));

        // Keyframe list.
        for (int i = 0; i < data.Keyframes.Count; i++)
        {
            Keyframe k = data.Keyframes[i];
            int idx = i;
            string star = i == data.StartingKeyframe ? "★ " : string.Empty;
            var kfRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var pick = new Button { Content = $"{star}KF{i}", MinWidth = 56 };
            pick.Click += (_, _) => { _editKeyframeIndex = idx; RefreshGroupPanelIfActive(); };
            kfRow.Children.Add(pick);
            kfRow.Children.Add(new TextBlock { Text = $"pos {k.Position.X:0.#},{k.Position.Y:0.#},{k.Position.Z:0.#}", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
            root.Children.Add(kfRow);
        }

        root.Children.Add(Row(Btn("Add Keyframe @ cam", () => AddKeyframeAtCamera(group)), Btn("Set Start", () => SetStartKeyframe(group))));

        // Selected keyframe properties.
        if (_editKeyframeIndex >= 0 && _editKeyframeIndex < data.Keyframes.Count)
        {
            Keyframe k = data.Keyframes[_editKeyframeIndex];
            root.Children.Add(Header($"Keyframe {_editKeyframeIndex} Properties"));
            root.Children.Add(Num("Travel Time to Next (s)", k.DepartTravelTime, v => _movers!.EditKeyframe(k, "Travel", x => x.DepartTravelTime = v, x => x.DepartTravelTime = k.DepartTravelTime)));
            root.Children.Add(Num("Return Travel (s)", k.ReturnTravelTime, v => _movers!.EditKeyframe(k, "Return", x => x.ReturnTravelTime = v, x => x.ReturnTravelTime = k.ReturnTravelTime)));
            root.Children.Add(Num("Pause Time (s)", k.PauseTime, v => _movers!.EditKeyframe(k, "Pause", x => x.PauseTime = v, x => x.PauseTime = k.PauseTime)));
            root.Children.Add(Num("Accel Time (s)", k.AccelTime, v => _movers!.EditKeyframe(k, "Accel", x => x.AccelTime = v, x => x.AccelTime = k.AccelTime)));
            root.Children.Add(Num("Decel Time (s)", k.DecelTime, v => _movers!.EditKeyframe(k, "Decel", x => x.DecelTime = v, x => x.DecelTime = k.DecelTime)));
            root.Children.Add(Num("Degrees About Axis (rotate-in-place)", k.DegreesAboutAxis, v => _movers!.EditKeyframe(k, "Degrees", x => x.DegreesAboutAxis = v, x => x.DegreesAboutAxis = k.DegreesAboutAxis)));
            root.Children.Add(IntNum2("Triggered Event UID", k.EventUid, v => _movers!.EditKeyframe(k, "Event UID", x => x.EventUid = v, x => x.EventUid = k.EventUid)));
        }

        // Motion fields.
        root.Children.Add(Header("Motion"));
        var move = new ComboBox
        {
            ItemsSource = new[] { "One Way", "Ping Pong Once", "Ping Pong Infinite", "Loop Once", "Loop Infinite", "Lift" },
            SelectedIndex = Math.Clamp(data.MovementType - 1, 0, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        move.SelectionChanged += (_, _) => { int t = move.SelectedIndex + 1; _movers!.EditMover(data, "Movement type", d => d.MovementType = t, d => d.MovementType = data.MovementType); };
        root.Children.Add(Labeled("Movement Type", move));
        root.Children.Add(Check("Is Door (blocks visibility)", data.IsDoor != 0, v => _movers!.EditMover(data, "Is door", d => d.IsDoor = (byte)(v ? 1 : 0), d => d.IsDoor = data.IsDoor)));
        root.Children.Add(Check("Rotate In Place", data.RotateInPlace != 0, v => _movers!.EditMover(data, "Rotate in place", d => d.RotateInPlace = (byte)(v ? 1 : 0), d => d.RotateInPlace = data.RotateInPlace)));
        root.Children.Add(Check("Hold Open [Alpine]", _movers!.IsHoldOpen(group), v => { _movers.SetHoldOpen(group, v); AfterMutation(); }));
        root.Children.Add(TextField("Start Sound", data.StartSound, s => _movers!.EditMover(data, "Start sound", d => d.StartSound = s, d => d.StartSound = data.StartSound)));
        root.Children.Add(TextField("Stop Sound", data.StopSound, s => _movers!.EditMover(data, "Stop sound", d => d.StopSound = s, d => d.StopSound = data.StopSound)));

        // Path preview transport.
        root.Children.Add(Header("Path Preview"));
        root.Children.Add(Check("Show Ghost", _showMoverGhost, v => { _showMoverGhost = v; RefreshSelectionOverlay(); }));
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = _moverTransportT, SmallChange = 0.02, LargeChange = 0.1 };
        slider.ValueChanged += (_, _) => { _moverTransportT = (float)slider.Value; RefreshSelectionOverlay(); };
        root.Children.Add(Labeled("Transport (scrub ghost)", slider));
        return root;
    }

    private int _editKeyframeIndex = -1;

    private void AddKeyframeAtCamera(Group group)
    {
        CoreVec3 p = PlacementPoint;
        _movers!.AddKeyframe(group, p, Mat3.Identity);
        AfterMutation();
        _dispatcher.ShowMessage("Keyframe added at camera.");
    }

    private void SetStartKeyframe(Group group)
    {
        if (group.MovingData is { } data && _editKeyframeIndex >= 0)
        {
            int idx = _editKeyframeIndex;
            _movers!.EditMover(data, "Set start keyframe", d => d.StartingKeyframe = idx, d => d.StartingKeyframe = data.StartingKeyframe);
            AfterMutation();
        }
    }

    // ---- Cutscene editor ------------------------------------------------------

    private Control BuildCutsceneEditor()
    {
        var root = new StackPanel { Spacing = 4 };
        if (_cutscenes is null)
        {
            return root;
        }

        root.Children.Add(Row(Btn("New Path…", () => _ = NewCutscenePathAsync()), Btn("Add Node @ cam", AddCutsceneNodeAtCamera)));
        foreach (CutscenePath path in _cutscenes.Paths)
        {
            CutscenePath captured = path;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var sel = new Button { Content = $"{path.Name} ({path.PathNodes.Count})", MinWidth = 90 };
            sel.Click += (_, _) => { _selectedPath = captured; RefreshSelectionOverlay(); RefreshGroupPanelIfActive(); };
            row.Children.Add(sel);
            var append = new Button { Content = "+node" };
            append.Click += (_, _) => AppendSelectedNodesToPath(captured);
            row.Children.Add(append);
            root.Children.Add(row);
        }

        if (_selectedPath is { } p && _cutscenes.Paths.Contains(p))
        {
            root.Children.Add(Note($"Path \"{p.Name}\": " + string.Join(" → ", p.PathNodes)));
        }

        return root;
    }

    private CutscenePath? _selectedPath;

    private void AddCutsceneNodeAtCamera()
    {
        ObjectHeader node = _cutscenes!.AddNode(PlacementPoint, ActiveCameraRotation());
        if (_selectedPath is { } p && _cutscenes.Paths.Contains(p))
        {
            _cutscenes.AppendNode(p, node.Uid);
        }

        AfterMutation();
        _dispatcher.ShowMessage("Cutscene path node added.");
    }

    private void AppendSelectedNodesToPath(CutscenePath path)
    {
        var nodes = Document?.Selection.Where(o => o.Kind == LevelObjectKind.CutscenePathNode).ToList() ?? new();
        foreach (LevelObject n in nodes)
        {
            _cutscenes!.AppendNode(path, n.Uid);
        }

        AfterMutation();
    }

    private async Task NewCutscenePathAsync()
    {
        string? name = await InputDialog.ShowAsync(this, "New Cutscene Path", "Path name:", "Path");
        if (!string.IsNullOrWhiteSpace(name))
        {
            _selectedPath = _cutscenes!.CreatePath(name);
            AfterMutation();
        }
    }

    // ---- Group operations -----------------------------------------------------

    private async Task CreateGroupAsync()
    {
        string? name = await InputDialog.ShowAsync(this, "Create Group", "Group name:", "Group");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var brushes = SelectedBrushUids();
        var objects = SelectedObjectUids();
        if (brushes.Count + objects.Count == 0)
        {
            _dispatcher.ShowMessage("Select brushes/objects first.");
            return;
        }

        _groups!.CreateGroup(name, brushes, objects);
        AfterMutation();
        _dispatcher.ShowMessage($"Created group \"{name}\".");
    }

    private async Task RenameGroupAsync(Group grp)
    {
        string? name = await InputDialog.ShowAsync(this, "Rename Group", "New name:", grp.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _groups!.Rename(grp, name);
            AfterMutation();
        }
    }

    private void CreateMoverFromSelection()
    {
        var brushes = SelectedBrushUids();
        var objects = SelectedObjectUids();
        if (brushes.Count + objects.Count == 0)
        {
            _dispatcher.ShowMessage("Select brushes/objects to turn into a mover.");
            return;
        }

        Group g = _movers!.CreateMover(brushes, objects, "Mover");
        AfterMutation();
        SelectMoverGroup(g);
        _dispatcher.ShowMessage($"Created mover with {g.Brushes.Count} brush(es).");
    }

    private void MirrorGroupSelection(int axis)
    {
        var brushes = SelectedBrushUids();
        var objects = SelectedObjectUids();
        if (brushes.Count + objects.Count == 0)
        {
            _dispatcher.ShowMessage("Select brushes/objects to mirror.");
            return;
        }

        _groups!.MirrorMembers(brushes, objects, axis, $"Mirror {"XYZ"[axis]}");
        AfterMutation();
    }

    private void SelectGroupBrushes(Group grp)
    {
        if (BrushEd is null)
        {
            return;
        }

        BrushEd.ClearSelection();
        foreach (int uid in grp.Brushes)
        {
            _session.Selection.SelectBrush(uid, additive: true);
        }
    }

    private List<int> SelectedObjectUids() => Document?.Selection.Select(o => o.Uid).ToList() ?? new List<int>();

    // ---- .rfg save / load -----------------------------------------------------

    private async Task SaveRfgAsync()
    {
        if (Document is null)
        {
            return;
        }

        var brushes = SelectedBrushUids();
        var objects = SelectedObjectUids();
        if (brushes.Count + objects.Count == 0)
        {
            _dispatcher.ShowMessage("Select brushes/objects to save as a group.");
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Group (.rfg)",
            DefaultExtension = "rfg",
            SuggestedFileName = "group.rfg",
            FileTypeChoices = new[] { new FilePickerFileType("RF Group (.rfg)") { Patterns = new[] { "*.rfg" } } },
        });
        if (file?.TryGetLocalPath() is not string path)
        {
            return;
        }

        bool alpine = Document.Rfl.Header.Version >= 0x12C;
        RfgFile rfg = RfgInterop.Export(Document, brushes, objects, alpine);
        rfg.Save(path);
        _dispatcher.ShowMessage($"Saved {System.IO.Path.GetFileName(path)} ({(alpine ? "Alpine v300" : "stock")}).");
    }

    private async Task LoadRfgAsync()
    {
        if (Document is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Group (.rfg)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("RF Group (.rfg)") { Patterns = new[] { "*.rfg" } } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not string path)
        {
            return;
        }

        try
        {
            RfgFile rfg = RfgFile.Load(path);
            var placed = RfgInterop.Import(Document, rfg, PlacementPoint);
            AfterMutation();
            _dispatcher.ShowMessage($"Imported {placed.Count} object(s) from {System.IO.Path.GetFileName(path)} at camera.");
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Load .rfg failed: {ex.Message}");
        }
    }

    // ---- Viewport overlays ----------------------------------------------------

    /// <summary>Whether the last handled selection contained a decal (drives the decal-face rebuild).</summary>
    private bool _selectionHadDecal;

    /// <summary>True when the current selection contains at least one decal (its facing face is scene-baked).</summary>
    private bool SelectionHasDecal()
    {
        if (Document is null)
        {
            return false;
        }

        foreach (LevelObject o in Document.Selection)
        {
            if (o.Model is Decal)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<LineSegment> BuildEditingOverlays()
    {
        var lines = new List<LineSegment>();
        if (Document is null)
        {
            return lines;
        }

        // Mover path + ghost for the selected mover.
        if (SelectedMoverGroup() is { MovingData: { } data } group && data.Keyframes.Count > 0)
        {
            var points = data.Keyframes.Select(k => k.Position).ToList();
            lines.AddRange(OverlayBuilder.Path(points, Math.Clamp(data.StartingKeyframe, 0, points.Count - 1)));
            if (_showMoverGhost && _movers is not null)
            {
                var moverBrushes = _movers.MoverBrushes.Where(b => group.Brushes.Contains(b.Uid)).ToList();
                if (moverBrushes.Count > 0)
                {
                    CoreVec3 sampled = OverlayBuilder.SamplePath(points, _moverTransportT);
                    lines.AddRange(OverlayBuilder.MoverGhost(moverBrushes, points[Math.Clamp(data.StartingKeyframe, 0, points.Count - 1)], sampled));
                }
            }
        }

        // Cutscene path polyline for the selected path.
        if (_selectedPath is { } path && _cutscenes is not null && _cutscenes.Paths.Contains(path))
        {
            var pts = path.PathNodes.Select(u => _cutscenes.FindNode(u)?.Position).Where(p => p is not null).Select(p => p!.Value).ToList();
            if (pts.Count > 0)
            {
                lines.AddRange(OverlayBuilder.Path(pts, 0, Palette.Rgba(255, 120, 200)));
            }
        }

        // Per-selection shape glyphs: nav disc, decal box, cutscene camera cone.
        foreach (LevelObject o in Document.Selection)
        {
            switch (o.Model)
            {
                case NavPoint np:
                    lines.AddRange(OverlayBuilder.Disc(np.Position, np.Radius > 0 ? np.Radius : 1f));
                    break;
                case Decal d:
                    lines.AddRange(OverlayBuilder.Box(d.Header.Position, d.Header.Rotation, d.Extents.LengthSquared() > 1e-4f ? d.Extents : new CoreVec3(1, 1, 0.2f)));
                    break;
                case ObjectHeader h when o.Kind == LevelObjectKind.CutsceneCamera:
                    lines.AddRange(OverlayBuilder.CameraCone(h.Position, h.Rotation));
                    break;
            }
        }

        return lines;
    }

    private Mat3 ActiveCameraRotation()
    {
        Ged.Rendering.Camera? cam = _viewportGrid.ActiveSurface.Camera;
        if (cam is null)
        {
            return Mat3.Identity;
        }

        Vector3 f = cam.Forward, r = cam.Right, u = cam.Up;
        return new Mat3(new CoreVec3(f.X, f.Y, f.Z), new CoreVec3(r.X, r.Y, r.Z), new CoreVec3(u.X, u.Y, u.Z));
    }

    // ---- small helpers --------------------------------------------------------

    private static Control IntNum2(string label, int value, Action<int> set)
    {
        var box = new NumericUpDown { Value = value, Increment = 1m, Minimum = -1m, Maximum = 1000000m, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.ValueChanged += (_, _) => set((int)(box.Value ?? 0));
        return Labeled(label, box);
    }

    private static Control TextField(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value };
        box.LostFocus += (_, _) => set(box.Text ?? string.Empty);
        return Labeled(label, box);
    }
}
