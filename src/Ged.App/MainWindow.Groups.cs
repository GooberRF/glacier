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
using Ged.Core.Tables;
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

        // Movers are edited in the Properties panel's Keyframe panel (select a keyframe or a mover member).
        if (SelectedMoverGroup() is { } moverGroup)
        {
            root.Children.Add(Note($"Mover \"{moverGroup.Name}\": edit its keyframes, sounds, path and preview in the Properties panel."));
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
            item.ContextMenu = MovingGroupContextMenu(captured);
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

    /// <summary>Moving-group context menu: select, or Dissolve the mover back to editable static geometry.</summary>
    private ContextMenu MovingGroupContextMenu(Group grp)
    {
        var menu = new ContextMenu();
        void Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("Select", () => SelectMoverGroup(grp));
        Item("Dissolve (back to static)", () => DissolveMoverGroup(grp));
        return menu;
    }

    /// <summary>Dissolves a moving group: the mover is torn down and its members become ordinary world brushes.</summary>
    private void DissolveMoverGroup(Group grp)
    {
        _movers!.DissolveMover(grp);
        if (ReferenceEquals(_selectedMover, grp))
        {
            _selectedMover = null;
        }

        AfterMutation();

        // Dissolving returns the member brushes to the static fold, so the compiled static_geometry must
        // re-include them (and drop the now-deleted mover copies). Mark geometry dirty + arm the rebuild,
        // mirroring Create Mover, so the fold is corrected without a manual Build.
        _buildController?.InvalidateBrushGeometry();

        RefreshGroupPanelIfActive();
        _dispatcher.ShowMessage($"Dissolved mover \"{grp.Name}\" — its brushes are static world geometry again.");
    }

    /// <summary>
    /// RED member-click escalation (§A.4): in Object/Group mode a plain click on a group member selects
    /// the WHOLE group as a unit; Alt+click (and Ctrl+click) fall through to individual selection. Same
    /// hybrid feel as prefab-instance unit selection, but groups stay a distinct system. Returns true
    /// when the click was consumed (a group was selected, or the selection was refused because the group
    /// is locked — its hint already shown).
    /// </summary>
    private bool HandleGroupPick(Viewport.IViewportSurface surface, Ged.Rendering.Picking.PickId id, bool additive)
    {
        if (Document is null || BrushEd is null || _groups is null || _movers is null)
        {
            return false;
        }

        // RED escalates only in the whole-object modes; brush/face/vertex/edge modes select individually.
        if (BrushEd.Mode is not (EditMode.Object or EditMode.Group))
        {
            return false;
        }

        // Alt (individual override) or Ctrl (additive) → let the normal per-kind path handle it.
        if (surface.AltHeld || additive)
        {
            return false;
        }

        int memberUid = id.Kind is Ged.Rendering.Picking.PickKind.Object
            or Ged.Rendering.Picking.PickKind.Mesh
            or Ged.Rendering.Picking.PickKind.Brush
            ? id.Index : -1;
        if (memberUid < 0)
        {
            return false;
        }

        Group? group = FindGroupForMember(memberUid);
        if (group is null)
        {
            return false; // not a group member → individual selection
        }

        var memberUids = group.Brushes.Concat(group.Objects).Distinct().ToList();
        if (memberUids.Count == 0)
        {
            return false;
        }

        // A locked group refuses selection as a unit (the router raises the lock hint); consume the
        // click either way so it does not fall through to individual selection.
        if (_session.Selection.SelectGroupUnit(memberUids))
        {
            _selectedMover = _movers.Movers.Contains(group) ? group : null;
            LastPickHighlight = Ged.Rendering.Picking.PickId.None;
            UpdateGizmoState();
            _properties.Refresh();
            RefreshGroupPanelIfActive();
        }

        return true;
    }

    /// <summary>The user-defined or moving group that lists <paramref name="uid"/> as a member, or null.</summary>
    private Group? FindGroupForMember(int uid)
    {
        foreach (Group g in _groups!.Groups)
        {
            if (g.Brushes.Contains(uid) || g.Objects.Contains(uid))
            {
                return g;
            }
        }

        foreach (Group g in _movers!.Movers)
        {
            if (g.Brushes.Contains(uid) || g.Objects.Contains(uid))
            {
                return g;
            }
        }

        return null;
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

        // The gold start keyframe is seeded at the members' rest-pose centre by CreateMover (RED's
        // FUN_00416000 / FUN_004267d0) — lined up with the mover, never at the camera and never lifted.
        Group g = _movers!.CreateMover(brushes, objects, "Mover");
        AfterMutation();

        // Creating a mover changes the STATIC fold (the member brushes must now be excluded from it) and
        // needs its mover faces given globally-unique FaceIds — both happen in the geometry build. Mark
        // geometry dirty and arm the background rebuild so the compiled static_geometry re-folds without
        // the mover and the mover faces are renumbered, promptly and without a manual Build. This also
        // flips the build into the PREVIEW state, so a save re-seals (the existing seal guard) and the
        // fold-exclusion + unique FaceIds reach the written file even if the user never rebuilds by hand.
        _buildController?.InvalidateBrushGeometry();

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

        // The mover preview ghost, its timing ticks and the hovered / dragged curve handle.
        lines.AddRange(BuildMoverOverlays());

        // The Terrain Tools' brush cursor (rings, mirror lines, geoable chunk outlines).
        lines.AddRange(BuildTerrainToolOverlays());

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
}
