using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Dialogs;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Core.Model;
using Ged.App.Viewport;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Brush = Ged.Core.Model.Brush;
using CoreVec3 = Ged.Core.Model.Vec3;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// Editing: the mode framework (Brush/Face/Vertex/Object/Group; texturing lives on
/// Face mode's Texture/UV tab), the mode tool panels, mode-aware
/// picking, keyboard + drag transforms, the brush cookie cutter and every brush/face/vertex
/// operator, plus the deferred camera parity (C axis-orient, END scroll, Ctrl+RMB ortho
/// teleport, numpad bank).
/// </summary>
public sealed partial class MainWindow
{
    private readonly BrushCreateParams _brushParams = new();
    private bool _coordLocal;
    private bool _clipCut;
    private bool _clipFlip;

    // Face mode's tabbed panel (item 0h): Geometry | Texture/UV.
    private TabControl? _faceTabs;
    private bool _textureTabActive;
    private int _dragEpoch;
    private CoreVec3 _cutterPos;
    private bool _showCutterGhost;

    // M/N+LMB drag accumulation (for absolute grid snap of the pivot).
    private bool _brushDragActive;
    private CoreVec3 _brushDragPivot;
    private CoreVec3 _brushDragAccum;
    private CoreVec3 _brushDragApplied;

    private BrushEditor? BrushEd => _session.BrushEditor;

    private void InitEditing()
    {
        // Modes.
        _dispatcher.Bind(CommandIds.ModeBrush, () => SetMode(EditMode.Brush));
        _dispatcher.Bind(CommandIds.ModeFace, () => SetMode(EditMode.Face));
        _dispatcher.Bind(CommandIds.ModeEdge, () => SetMode(EditMode.Edge));
        _dispatcher.Bind(CommandIds.ModeVertex, () => SetMode(EditMode.Vertex));
        _dispatcher.Bind(CommandIds.ModeObject, () => SetMode(EditMode.Object));
        _dispatcher.Bind(CommandIds.ModeGroup, () => SetMode(EditMode.Group));
        _dispatcher.Bind(CommandIds.ModeTexture, FocusTextureTools);

        // Brush operators.
        _dispatcher.Bind(CommandIds.BrushSnapCutter, SnapCutterToCamera);
        _dispatcher.Bind(CommandIds.BrushCreate, CreateBrushFromPanel);
        _dispatcher.Bind(CommandIds.BrushClip, () => ClipSelected(ActivePaneDepthAxis()));
        _dispatcher.Bind(CommandIds.BrushSnapGrid, SnapSelectionToGrid);
        _dispatcher.Bind(CommandIds.BrushMoveCenters, MoveCentersSelected);
        _dispatcher.Bind(CommandIds.BrushReorient, ReorientSelected);
        _dispatcher.Bind(CommandIds.BrushStretch, () => _ = StretchDialogAsync());
        InitDrawBrush(); // Item 8: the interactive three-stage draw-brush tool

        // Face operators (hotkeys).
        _dispatcher.Bind(CommandIds.FaceExtrude, () => _ = ExtrudeDialogAsync());
        _dispatcher.Bind(CommandIds.FaceBevel, () => _ = BevelDialogAsync());

        // Selection memory / grow. Face-mode Shift+S grows the face selection to every
        // face of the owning brushes (item 4) — the same operation as Texture-mode Shift+S.
        _dispatcher.Bind(CommandIds.SelectGrow, () => { BrushEd?.GrowFacesToBrush(); RefreshSelectionOverlay(); });
        _dispatcher.Bind(CommandIds.ViewToggleCoordSpace, () => { _coordLocal = !_coordLocal; UpdateStatusStatics(); });

        // Mode-aware clipboard/delete: route to brush/face/vertex ops while editing brushes.
        _dispatcher.Bind(CommandIds.EditCopy, CopyContext, () => Document is not null);
        _dispatcher.Bind(CommandIds.EditCut, CutContext, () => Document is not null);
        _dispatcher.Bind(CommandIds.EditPaste, PasteContext, () => Document is not null);
        _dispatcher.Bind(CommandIds.EditDelete, DeleteContext, () => Document is not null);

        // Camera parity.
        _dispatcher.Bind(CommandIds.CameraOrientAxis, () => _viewportGrid.ActiveSurface.AxisOrient());
        _dispatcher.Bind(CommandIds.CameraScrollMode, () =>
        {
            _viewportGrid.ActiveSurface.ToggleScrollMode();
            _dispatcher.ShowMessage($"Scroll mode {(_viewportGrid.ActiveSurface.ScrollMode ? "on" : "off")}.");
        });
        _dispatcher.Bind(CommandIds.CameraBankLeft, () => _viewportGrid.ActiveSurface.Bank(-5f));
        _dispatcher.Bind(CommandIds.CameraBankRight, () => _viewportGrid.ActiveSurface.Bank(5f));

        // Viewport transform gestures.
        _viewportGrid.ForEachSurface(s =>
        {
            s.NudgeMove += OnNudgeMove;
            s.NudgeRotate += OnNudgeRotate;
            s.BrushDragStarted += OnBrushDragStarted;
            s.BrushDragPixels += OnBrushDragPixels;
            s.BrushDragEnded += () => { _dragEpoch++; DisarmGeometrySnap(); };
        });

        InitTexture();
        InitEdges(); // item 2: edge-mode operators
        InitGizmoAndClip();
        InitAnnotations(); // B7 ruler + annotations
        InitAssetBrowser();
        InitPackfile();
        InitImportExport();
        SetModePanel(EditMode.Object);
    }

    // ---- Mode framework -------------------------------------------------------

    private void SetMode(EditMode mode) => ApplyMode(mode, announce: true);

    /// <summary>
    /// Switches the editing mode and syncs the selection-filter chips to it (mode→chip,
    /// exclusive). <paramref name="announce"/> is false when re-applying the persisted
    /// filter on level open so the open status message is not clobbered.
    /// </summary>
    private void ApplyMode(EditMode mode, bool announce)
    {
        if (BrushEd is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        ExitIsolationIfActive(); // B6: a mode switch auto-exits isolation (restoring state)
        BrushEd.SetMode(mode);
        _dispatcher.ActiveScope = ScopeFor(mode);
        _showCutterGhost = mode == EditMode.Brush;
        SetModePanel(mode);
        _filter.SyncFromMode(mode);
        ClearInvalidSelection();
        UpdateFilterChips();
        RebuildScene();
        RefreshSelectionOverlay();
        UpdateGizmoState();
        UpdateStatusStatics();
        if (announce)
        {
            _dispatcher.ShowMessage($"{mode} mode");
        }
    }

    /// <summary>
    /// Drops any selection whose kind the current selection-filter no longer allows, so a
    /// selection made in one mode cannot be transformed under an incompatible mode (e.g. a
    /// brush selected in Brush mode is cleared on entering Object mode). Selections of
    /// kinds still enabled — including several at once via Ctrl+chip — survive.
    /// </summary>
    private void ClearInvalidSelection() =>
        Ged.Core.Editing.SelectionScope.ClearInvalid(_filter.Active, BrushEd, Document);

    private bool IsBrushEditMode => BrushEd?.Mode is EditMode.Brush or EditMode.Face or EditMode.Edge or EditMode.Vertex;

    private void CopyContext()
    {
        if (TextureToolsActive && TexCopyUv())
        {
            return;
        }

        if (IsBrushEditMode && BrushEd is { } be && be.SelectedBrushes.Count > 0)
        {
            be.CopySelected();
            _dispatcher.ShowMessage($"Copied {be.SelectedBrushes.Count} brush(es).");
        }
        else
        {
            Document?.CopySelection();
        }
    }

    private void CutContext()
    {
        if (IsBrushEditMode && BrushEd is { } be && be.SelectedBrushes.Count > 0)
        {
            be.CopySelected();
            be.DeleteBrushes(be.SelectedBrushes.ToList());
            AfterBrushEdit();
        }
        else
        {
            Document?.CutSelection();
            AfterMutation();
        }
    }

    private void PasteContext()
    {
        if (TextureToolsActive && TexPasteUv())
        {
            return;
        }

        if (BrushEd is { HasClipboard: true } be && IsBrushEditMode)
        {
            var uids = be.Paste(new CoreVec3(_settings.GridSize, 0, 0));
            be.ClearSelection();
            foreach (int u in uids)
            {
                _session.Selection.SelectBrush(u, additive: true);
            }

            AfterBrushEdit();
        }
        else
        {
            Document?.Paste();
            AfterMutation();
        }
    }

    private void DeleteContext()
    {
        if (BrushEd is not { } be || !IsBrushEditMode)
        {
            Document?.DeleteSelection();
            AfterMutation();
            return;
        }

        switch (be.Mode)
        {
            case EditMode.Brush when be.SelectedBrushes.Count > 0:
                be.DeleteBrushes(be.SelectedBrushes.ToList());
                AfterBrushEdit();
                break;
            case EditMode.Face when be.SelectedFaces.Count > 0:
                FaceOpMulti("Delete faces", FaceOps.Delete);
                break;
            case EditMode.Edge when be.SelectedEdges.Count > 0:
                EdgeSingle("Collapse edge", EdgeOps.Collapse); // "delete" an edge = collapse it
                break;
            case EditMode.Vertex when be.SelectedVertices.Count > 0:
                VertexOpSet("Delete verts", VertexOps.Delete);
                break;
            default:
                Document?.DeleteSelection();
                AfterMutation();
                break;
        }
    }

    private static CommandScope ScopeFor(EditMode mode) => mode switch
    {
        EditMode.Brush => CommandScope.Brush,
        EditMode.Face => CommandScope.Face,
        EditMode.Edge => CommandScope.Edge,
        EditMode.Vertex => CommandScope.Vertex,
        EditMode.Group => CommandScope.Group,
        _ => CommandScope.Object,
    };

    private void SetModePanel(EditMode mode) => _modePanel.SetContent(mode switch
    {
        EditMode.Brush => BuildBrushPanel(),
        EditMode.Face => BuildFaceModePanel(),
        EditMode.Edge => BuildEdgePanel(),
        EditMode.Vertex => BuildVertexPanel(),
        EditMode.Group => BuildGroupPanel(),
        _ => BuildObjectPanel(),
    });

    /// <summary>
    /// True while Face mode's Texture/UV tab is the active tab — the merged-Texture-mode
    /// context. Texture-only gestures (H/V map flip, UV copy/paste, the
    /// tex-panel selection sync) fire only in this context so they never shadow the Face
    /// geometry ops on the Geometry tab.
    /// </summary>
    private bool TextureToolsActive => BrushEd?.Mode == EditMode.Face && _textureTabActive;

    /// <summary>
    /// Shift+T (RED muscle memory): switches to Face mode with the Texture/UV tab focused —
    /// there is no separate Texture mode any more (item 0h).
    /// </summary>
    public void FocusTextureTools()
    {
        if (BrushEd is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        if (BrushEd.Mode != EditMode.Face)
        {
            SetMode(EditMode.Face);
        }

        if (_faceTabs is not null)
        {
            _faceTabs.SelectedIndex = 1;
        }

        _dispatcher.ShowMessage("Texture / UV tools");
    }

    /// <summary>
    /// Face mode's tabbed tool panel (item 0h): a "Geometry" tab (the face ops) and a
    /// "Texture / UV" tab (the merged former Texture-mode panel: picker/apply/mapping/UV/
    /// face-properties). Switching to the Texture/UV tab refreshes the tex-panel selection.
    /// </summary>
    private Control BuildFaceModePanel()
    {
        var tabs = new TabControl { Padding = new Avalonia.Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Geometry", Content = BuildFacePanel() });
        tabs.Items.Add(new TabItem { Header = "Texture / UV", Content = BuildTexturePanel() });
        tabs.SelectedIndex = 0;
        _textureTabActive = false;
        tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, tabs))
            {
                return; // ignore bubbled selection changes from inner combos/lists
            }

            _textureTabActive = tabs.SelectedIndex == 1;
            if (_textureTabActive)
            {
                RefreshTexturePanelSelection();
            }
        };
        _faceTabs = tabs;
        return tabs;
    }

    // ---- Mode-aware picking ---------------------------------------------------

    private bool HandleModePick(Viewport.IViewportSurface surface, PickId id, bool additive)
    {
        if (BrushEd is null)
        {
            return false;
        }

        // The selection filter (chips) governs which pick kinds a click may select —
        // the nearest/most-specific id-buffer hit wins, then the strict mode-scoped
        // gate (item 5, <see cref="Ged.App.Services.PickGate"/>) accepts or ignores it.
        Ged.Core.Editing.SelectKinds allow = _filter.Active;
        bool brushGeom = (allow & (Ged.Core.Editing.SelectKinds.Brushes | Ged.Core.Editing.SelectKinds.Faces | Ged.Core.Editing.SelectKinds.Vertices | Ged.Core.Editing.SelectKinds.Edges)) != 0;

        // Edge mode (item 2): edges aren't in the id buffer, so seed from the brush hit and
        // CPU ray-pick the nearest edge of that brush to the cursor ray.
        if (BrushEd.Mode == EditMode.Edge && (allow & Ged.Core.Editing.SelectKinds.Edges) != 0)
        {
            return HandleEdgePick(surface, id, additive);
        }

        if (id.Kind == PickKind.Brush && Ged.App.Services.PickGate.AllowsBrushEditor(allow, id.Kind))
        {
            Toggle(_session.Selection.SelectBrush, _session.Selection.ToggleBrush, id.Index, additive);
            return true;
        }

        if (id.Kind == PickKind.BrushFace && Ged.App.Services.PickGate.AllowsBrushEditor(allow, id.Kind) &&
            _session.TryResolveBrushFace(id, out int fUid, out int face))
        {
            if (additive)
            {
                _session.Selection.ToggleFace(fUid, face);
            }
            else
            {
                _session.Selection.SelectFace(fUid, face);
            }

            if (TextureToolsActive)
            {
                RefreshTexturePanelSelection();
            }

            return true;
        }

        if (id.Kind == PickKind.BrushVertex && Ged.App.Services.PickGate.AllowsBrushEditor(allow, id.Kind) &&
            _session.TryResolveBrushVertex(id, out int vUid, out int vert))
        {
            if (additive)
            {
                _session.Selection.ToggleVertex(vUid, vert);
            }
            else
            {
                _session.Selection.SelectVertex(vUid, vert);
            }

            return true;
        }

        // Empty click while a brush-geometry kind is active clears the brush sub-selection.
        if (id.IsNone && brushGeom)
        {
            BrushEd.ClearSelection();
            if (TextureToolsActive)
            {
                RefreshTexturePanelSelection();
            }

            return true;
        }

        return false;
    }

    private static void Toggle(Func<int, bool, bool> select, Func<int, bool> toggle, int uid, bool additive)
    {
        if (additive)
        {
            toggle(uid);
        }
        else
        {
            select(uid, false);
        }
    }

    private string BuildSelectionReadout()
    {
        if (BrushEd is { } be)
        {
            if (be.SelectedBrushes.Count > 0)
            {
                if (be.SelectedBrushes.Count == 1)
                {
                    int uid = be.SelectedBrushes.First();
                    Brush? b = be.FindBrush(uid);
                    CoreVec3 d = b is null ? default : BrushTransform.Dimensions(b);
                    return $"brush {uid}  {d.X:0.##}×{d.Y:0.##}×{d.Z:0.##}m  t={be.TimeIndex(uid)}";
                }

                return $"{be.SelectedBrushes.Count} brushes";
            }

            if (be.SelectedFaces.Count > 0)
            {
                return $"{be.SelectedFaces.Count} face(s)";
            }

            if (be.SelectedVertices.Count > 0)
            {
                return $"{be.SelectedVertices.Count} vertex/vertices";
            }
        }

        if (Document is null || Document.Selection.Count == 0)
        {
            return "—";
        }

        return Document.Selection.Count == 1 ? Document.Selection.First().DisplayName : $"{Document.Selection.Count} objects";
    }

    // ---- Cutter ghost ---------------------------------------------------------

    private IEnumerable<LineSegment> BuildCutterGhost()
    {
        // While the interactive draw tool is active its own ghost replaces the
        // cutter ghost, so the two yellow boxes never overlap confusingly.
        if (!_showCutterGhost || BrushEd is null || DrawToolActive)
        {
            return Array.Empty<LineSegment>();
        }

        Brush ghost = BrushFactory.Create(_brushParams, 0, LoadMeshOrNull());
        ghost.Position = _cutterPos;
        return GhostEdgeLines(ghost, Palette.Rgba(200, 200, 90, 200));
    }

    /// <summary>
    /// Deduped world-space edge lines of a ghost brush — the one ghost-drawing path
    /// shared by the cookie-cutter ghost and the draw-brush tool's ghost box.
    /// </summary>
    private static IEnumerable<LineSegment> GhostEdgeLines(Brush ghost, uint color)
    {
        var seen = new HashSet<(int, int)>();
        foreach (Face f in ghost.Geometry.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int b = f.Vertices[(i + 1) % n].Index;
                var key = a < b ? (a, b) : (b, a);
                if (!seen.Add(key))
                {
                    continue;
                }

                CoreVec3 pa = ghost.Position.Add(ghost.Geometry.Vertices[a]);
                CoreVec3 pb = ghost.Position.Add(ghost.Geometry.Vertices[b]);
                yield return new LineSegment(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), color);
            }
        }
    }

    private Ged.Core.IO.Mesh.V3dFile? LoadMeshOrNull()
    {
        if (_brushParams.Shape != BrushShape.Mesh || string.IsNullOrEmpty(_brushParams.MeshFilename))
        {
            return _brushParams.Shape == BrushShape.Mesh ? throw new InvalidOperationException("Pick a mesh.") : null;
        }

        try
        {
            byte[]? bytes = System.IO.File.Exists(_brushParams.MeshFilename)
                ? System.IO.File.ReadAllBytes(_brushParams.MeshFilename)
                : _session.Vfs?.ReadFile(_brushParams.MeshFilename);
            return bytes is null ? null : Ged.Core.IO.Mesh.V3dReader.Read(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Brush creation -------------------------------------------------------

    private void SnapCutterToCamera()
    {
        // Same active-pane pitfall as PlacementPoint: stock B snaps the cutter to the
        // free-look camera, so use the perspective pane's camera, not the raw active one.
        Ged.Rendering.Camera? cam = _viewportGrid.CameraSurface.Camera;
        if (cam is null)
        {
            return;
        }

        Vector3 focus = cam.Position + (cam.Forward * 8f);
        focus = new Vector3(
            TransformMath.Snap(focus.X, _settings.GridSize),
            TransformMath.Snap(focus.Y, _settings.GridSize),
            TransformMath.Snap(focus.Z, _settings.GridSize));
        _cutterPos = new CoreVec3(focus.X, focus.Y, focus.Z);
        _showCutterGhost = BrushEd?.Mode == EditMode.Brush;
        RefreshSelectionOverlay();
    }

    private void CreateBrushFromPanel()
    {
        if (BrushEd is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        try
        {
            // Apply the Texture-preference defaults (per-face by orientation) at creation,
            // guarded against unresolvable names (stale persisted defaults / typos) so a new
            // brush never renders the white fallback while face props show a dead name.
            _brushParams.FloorTexture = _session.ResolveDefaultBrushTexture(_settings.DefaultFloorTexture);
            _brushParams.WallTexture = _session.ResolveDefaultBrushTexture(_settings.DefaultWallTexture);
            _brushParams.CeilingTexture = _session.ResolveDefaultBrushTexture(_settings.DefaultCeilingTexture);
            int uid = BrushEd.CreateBrush(_brushParams, _cutterPos, Mat3.Identity, LoadMeshOrNull());
            _session.Selection.SelectBrush(uid);
            _dispatcher.ShowMessage($"Created {_brushParams.Shape} brush (uid {uid}).");
            AfterMutation();
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Create failed: {ex.Message}");
        }
    }

    // ---- Transforms -----------------------------------------------------------

    private List<int> SelectedBrushUids() => BrushEd?.SelectedBrushes.ToList() ?? new List<int>();

    private void OnNudgeMove(Vector3 dir)
    {
        if (!IsBrushEditMode || BrushEd is null)
        {
            return;
        }

        var delta = new CoreVec3(dir.X, dir.Y, dir.Z).Scale(_settings.GridSize);

        // Edge mode: translate both endpoints of every selected edge (the world delta is
        // converted to each brush's local frame), reusing EdgeOps.Move.
        if (BrushEd.Mode == EditMode.Edge && BrushEd.SelectedEdges.Count > 0)
        {
            Dictionary<int, List<BrushEdge>> byBrush = EdgesByBrush();
            BrushEd.EditBrushes(byBrush.Keys.ToList(), "Move edges",
                b => EdgeOps.Move(b.Geometry, byBrush[b.Uid], b.Rotation.InverseTransform(delta)));
            AfterBrushEdit();
            return;
        }

        if (BrushEd.SelectedBrushes.Count == 0)
        {
            return;
        }

        BrushEd.TransformSelected("Move", b => BrushTransform.Move(b, delta));
        AfterBrushEdit();
    }

    private void OnNudgeRotate(Vector3 axis)
    {
        if (!IsBrushEditMode || BrushEd is null)
        {
            return;
        }

        Mat3 rot = Mat3Math.FromAxisAngle(new CoreVec3(axis.X, axis.Y, axis.Z), TransformMath.DegToRad(_settings.RotationStep));

        // Edge mode: rotate the selected edges about the selection pivot (per-brush local frame).
        if (BrushEd.Mode == EditMode.Edge && BrushEd.SelectedEdges.Count > 0)
        {
            CoreVec3 worldPivot = SubGeometryPivot();
            Dictionary<int, List<BrushEdge>> byBrush = EdgesByBrush();
            BrushEd.EditBrushes(byBrush.Keys.ToList(), "Rotate edges", b =>
            {
                Mat3 localRot = Mat3Math.Compose(b.Rotation.Transpose(), Mat3Math.Compose(rot, b.Rotation));
                CoreVec3 localPivot = b.Rotation.InverseTransform(worldPivot.Sub(b.Position));
                return EdgeOps.Rotate(b.Geometry, byBrush[b.Uid], localRot, localPivot);
            });
            AfterBrushEdit();
            return;
        }

        if (BrushEd.SelectedBrushes.Count == 0)
        {
            return;
        }

        var uids = SelectedBrushUids();
        var brushes = uids.Select(u => BrushEd.FindBrush(u)!).Where(b => b is not null).ToList();
        CoreVec3 pivot = BrushTransform.SelectionPivot(brushes);
        BrushEd.EditBrushes(uids, "Rotate", b => { BrushTransform.RotateAboutPivot(b, rot, pivot); return OpResult.Ok(); });
        AfterBrushEdit();
    }

    /// <summary>Groups the selected edges by brush uid as canonical <see cref="BrushEdge"/> lists.</summary>
    private Dictionary<int, List<BrushEdge>> EdgesByBrush() =>
        BrushEd!.SelectedEdges.GroupBy(e => e.Brush)
            .ToDictionary(g => g.Key, g => g.Select(e => BrushEdge.Canonical(e.V0, e.V1)).ToList());

    private void OnBrushDragStarted()
    {
        // Capture the pivot + reset accumulation so the drag can snap the pivot to
        // absolute grid multiples (magnet on) rather than quantizing each delta.
        _brushDragActive = false;
        _brushDragAccum = default;
        _brushDragApplied = default;
        ArmGeometrySnap(); // B1: snap the M/N drag to geometry too
    }

    private void OnBrushDragPixels(int dx, int dy, bool axisConstrained)
    {
        if (!IsBrushEditMode || BrushEd is null || BrushEd.SelectedBrushes.Count == 0)
        {
            return;
        }

        IViewportSurface s = _viewportGrid.ActiveSurface;
        Ged.Rendering.Camera? cam = s.Camera;
        if (cam is null)
        {
            return;
        }

        var uids = SelectedBrushUids();
        var brushes = uids.Select(u => BrushEd.FindBrush(u)!).ToList();
        if (!_brushDragActive)
        {
            _brushDragPivot = BrushTransform.SelectionPivot(brushes);
            _brushDragActive = true;
        }

        CoreVec3 pivot = _brushDragPivot;
        float h = Math.Max(1, s.SurfaceHeight);
        float worldPerPixel = cam.Projection == Ged.Rendering.CameraProjection.Orthographic
            ? cam.OrthoZoom * 2f / h
            : 2f * Math.Max(2f, Vector3.Distance(cam.Position, new Vector3(pivot.X, pivot.Y, pivot.Z))) * MathF.Tan(cam.FieldOfView * 0.5f) / h;

        Vector3 world = (cam.Right * (dx * worldPerPixel)) - (cam.Up * (dy * worldPerPixel));
        if (axisConstrained)
        {
            world = ConstrainToLargestAxis(world);
        }

        _brushDragAccum = _brushDragAccum.Add(new CoreVec3(world.X, world.Y, world.Z));

        // Geometry snap (vertex/midpoint/face) takes priority over absolute grid; Alt (Ctrl+Alt on Linux) inverts.
        CoreVec3 targetPivot = _snap.MovedPivotSnapped(pivot, _brushDragAccum, s.SnapInvertHeld, SnapWorldRadius(s, pivot.Add(_brushDragAccum)));
        CoreVec3 totalDelta = targetPivot.Sub(pivot);
        CoreVec3 delta = totalDelta.Sub(_brushDragApplied);
        if (delta.LengthSquared() < 1e-10f)
        {
            return;
        }

        _brushDragApplied = totalDelta;
        BrushEd.EditBrushesCoalesced(uids, "Move", b => { BrushTransform.Move(b, delta); return OpResult.Ok(); }, $"brushdrag{_dragEpoch}");
        AfterBrushEdit();
    }

    private static Vector3 ConstrainToLargestAxis(Vector3 v)
    {
        float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
        if (ax >= ay && ax >= az)
        {
            return new Vector3(v.X, 0, 0);
        }

        return ay >= az ? new Vector3(0, v.Y, 0) : new Vector3(0, 0, v.Z);
    }

    private void AfterBrushEdit()
    {
        RebuildScene();
        RefreshSelectionOverlay();
        _history.Refresh();
        UpdateStatusStatics();
    }

    private int ActivePaneDepthAxis()
    {
        // World axis perpendicular to the active pane's plane.
        return _viewportGrid.ActiveSurface.ViewType switch
        {
            Ged.App.Viewport.ViewType.Top or Ged.App.Viewport.ViewType.Bottom => 1,
            Ged.App.Viewport.ViewType.Left or Ged.App.Viewport.ViewType.Right => 0,
            _ => 2,
        };
    }
}
