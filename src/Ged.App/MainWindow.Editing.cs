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
    // The shared brush-type template the Brush panel's "Air (else Solid)" checkbox and the
    // Draw Brush tool both create from — seeded from the launch default below.
    private readonly BrushCreateParams _brushParams = NewDefaultBrushParams();
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

    /// <summary>
    /// The launch-default brush-type template (owner decision: Air on every launch). The
    /// single shared <see cref="_brushParams"/> — used by both the Brush panel's cookie
    /// cutter and the Draw Brush tool — is seeded from this. It is never persisted to
    /// settings.cfg, so the type always resets to Air at startup while the panel's
    /// "Air (else Solid)" checkbox toggles it freely in-session.
    /// </summary>
    internal static BrushCreateParams NewDefaultBrushParams() => new() { Air = true };

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
            s.BrushDragEnded += () => { _dragEpoch++; DisarmGeometrySnap(); CommitInteractiveTransform(); };
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
        UpdateFilterChips();
        // Single mode-transition chokepoint (P3): prune the selection to the kinds the new mode
        // allows AND drop the transient pick highlight, so nothing unselectable in this mode stays
        // visually selected (an EAX region selected in Object mode must not linger into Brush mode).
        _session.SyncSelectionToKinds(_filter.Active);
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

        // Try the most-specific in-mode selection for the hit kind (chip-gated). `selected` tracks
        // whether an in-mode selection was actually produced — false means empty space, a wrong-kind
        // hit, or a locked-only hit (its lock hint is raised inside the SelectionRouter call).
        bool selected = false;

        // Vertex mode (B2): the CPU screen-space nearest-vertex search is AUTHORITATIVE, not a
        // fallback. The GPU id buffer is a single-pixel read of tiny dots and mis-resolves them
        // under occlusion — it can hand back a vertex BEHIND the one under the cursor, or the
        // whole-brush face id, so a click that is visibly on a dot could select the wrong vertex or
        // (even after the 8 px radius fix) fail outright. Resolving the vertex by a true screen-space
        // nearest search FIRST makes the pick reliable; when a vertex is within the radius this click
        // IS a vertex interaction (a select, or a locked-brush refusal that already hinted) and must
        // never fall through to the whole-brush id path, which would grab the brush the dot sits on.
        bool vertexMode = BrushEd.Mode == EditMode.Vertex && (allow & Ged.Core.Editing.SelectKinds.Vertices) != 0;
        if (vertexMode && NearestBrushVertexOnRay(surface, out int nUid, out int nVert))
        {
            selected = additive ? _session.Selection.ToggleVertex(nUid, nVert) : _session.Selection.SelectVertex(nUid, nVert);
        }
        else if (id.Kind == PickKind.Brush && Ged.App.Services.PickGate.AllowsBrushEditor(allow, id.Kind))
        {
            selected = additive ? _session.Selection.ToggleBrush(id.Index) : _session.Selection.SelectBrush(id.Index, false);
            if (selected)
            {
                LastPickHighlight = id; // only the accepted direct hit lights the pick (item (a))
            }
        }
        else if (id.Kind == PickKind.BrushFace && Ged.App.Services.PickGate.AllowsBrushEditor(allow, id.Kind) &&
            _session.TryResolveBrushFace(id, out int fUid, out int face))
        {
            selected = additive ? _session.Selection.ToggleFace(fUid, face) : _session.Selection.SelectFace(fUid, face);
            if (selected)
            {
                LastPickHighlight = id;
            }
        }
        else if (id.Kind == PickKind.BrushVertex && Ged.App.Services.PickGate.AllowsBrushEditor(allow, id.Kind) &&
            _session.TryResolveBrushVertex(id, out int vUid, out int vert))
        {
            // Reached only when the CPU search found no vertex within the radius but the id buffer
            // still reports a vertex pixel — kept as a defensive fallback.
            selected = additive ? _session.Selection.ToggleVertex(vUid, vert) : _session.Selection.SelectVertex(vUid, vert);
            if (selected)
            {
                LastPickHighlight = id;
            }
        }

        if (selected)
        {
            if (TextureToolsActive)
            {
                RefreshTexturePanelSelection();
            }

            return true;
        }

        // No in-mode selection was produced. In a brush-geometry mode a non-additive click clears
        // the sub-selection — whether it was empty space, a wrong-kind hit (e.g. a face pixel in
        // vertex mode), or a locked-only hit (its lock hint already shown). This is the universal
        // "clicking where no valid selection exists clears" rule (item 3). Additive (Ctrl) clicks
        // keep the selection. Object/Group modes fall through (return false) to OnPicked's
        // document-selection path, which applies the same rule there.
        if (brushGeom)
        {
            if (!additive)
            {
                BrushEd.ClearSelection();
                if (TextureToolsActive)
                {
                    RefreshTexturePanelSelection();
                }
            }

            return true;
        }

        return false;
    }

    private const float VertexPickPixels = 8f;

    /// <summary>
    /// The nearest registered brush vertex to the surface's last pick ray within
    /// <see cref="VertexPickPixels"/> screen pixels, or false. A ray/point CPU search over the
    /// scene's vertex registry (world positions), so it recovers a near-miss on a tiny vertex dot
    /// regardless of whether a face occluded the dot's id in the pick buffer.
    /// </summary>
    private bool NearestBrushVertexOnRay(Viewport.IViewportSurface surface, out int brushUid, out int vertexIndex)
    {
        brushUid = vertexIndex = -1;
        if (_session.BrushPickVertices is not { Count: > 0 } verts || surface.LastPickRay is not (Vector3 ro, Vector3 rd))
        {
            return false;
        }

        return Ged.App.Services.VertexPickSearch.TryNearest(
            verts, ro, rd,
            w => surface.Camera?.WorldPerPixel(w, surface.SurfaceHeight) ?? 1f,
            VertexPickPixels, out brushUid, out vertexIndex);
    }

    private string BuildSelectionReadout()
    {
        // Feature F: while inside a prefab instance, the status bar shows the persistent
        // editing indicator (distinct from the Q-lock wording).
        if (_prefabUnit?.EnteredInstanceId is int enteredId)
        {
            return $"Editing prefab instance {enteredId} — ESC to exit";
        }

        if (_prefabUnit?.UnitInstanceId is int unitId)
        {
            return $"prefab instance {unitId} (unit)";
        }

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
        // Feature F: keyboard-nudge the whole prefab unit rigidly (drives it in any mode; one undo
        // step — RigidTransformUnit wraps its own transaction when no gizmo drag is open).
        if (PrefabUnitActive)
        {
            var unitDelta = new CoreVec3(dir.X, dir.Y, dir.Z).Scale(_settings.GridSize);
            _prefabUnit?.RigidTransformUnit(Mat3.Identity, unitDelta, default);
            AfterMutation();
            return;
        }

        if (!IsBrushEditMode || BrushEd is null)
        {
            return;
        }

        var delta = new CoreVec3(dir.X, dir.Y, dir.Z).Scale(_settings.GridSize);

        // Sub-geometry modes (vertex/face/edge): translate the selected sub-geometry, then triangulate
        // any face the move bent off-plane — one undo entry, scoped to the moved vertices. The same
        // bending the gizmo drag guards against (PlanarizeSubGeometryOnCommit) happens on a keyboard
        // nudge, which never runs through the gizmo lifecycle (RED parity; see FacePlanarizer).
        if (BrushEd.Mode is EditMode.Vertex or EditMode.Face or EditMode.Edge)
        {
            NudgeSubGeometryMove(delta);
            return;
        }

        // G defense-in-depth: never nudge a locked brush even if a stale selection slips through.
        var moveUids = BrushEd.SelectedBrushes.Where(u => !BrushEd.IsBrushLocked(u)).ToList();
        if (moveUids.Count == 0)
        {
            return;
        }

        BrushEd.EditBrushesCoalesced(moveUids, "Move", b => { BrushTransform.Move(b, delta); return OpResult.Ok(); }, null);
        _prefabInstances?.ApplyRigidTransform(moveUids, Mat3.Identity, delta, default);
        AfterBrushEdit();
        _buildController?.ArmPostTransformBuild(); // keyboard-nudge move: refresh the merged stash on any size level (Q3)
    }

    private void OnNudgeRotate(Vector3 axis)
    {
        Mat3 rot = Mat3Math.FromAxisAngle(new CoreVec3(axis.X, axis.Y, axis.Z), TransformMath.DegToRad(_settings.RotationStep));

        // Feature F: keyboard-rotate the whole prefab unit about its pose pivot, rigidly.
        if (PrefabUnitActive && _prefabUnit?.UnitRecord is { } unitRec)
        {
            _prefabUnit.RigidTransformUnit(rot, CoreVec3.Zero, unitRec.PivotPosition);
            AfterMutation();
            return;
        }

        if (!IsBrushEditMode || BrushEd is null)
        {
            return;
        }

        // Sub-geometry modes (vertex/face/edge): rotate the selected sub-geometry about its pivot,
        // then triangulate any face the rotation bent off-plane — one undo entry (RED parity; see
        // FacePlanarizer). Matches the gizmo edge rotate, extended to vertex/face selections.
        if (BrushEd.Mode is EditMode.Vertex or EditMode.Face or EditMode.Edge)
        {
            NudgeSubGeometryRotate(rot);
            return;
        }

        // G defense-in-depth: exclude locked brushes from the rotate set.
        var uids = BrushEd.SelectedBrushes.Where(u => !BrushEd.IsBrushLocked(u)).ToList();
        if (uids.Count == 0)
        {
            return;
        }

        var brushes = uids.Select(u => BrushEd.FindBrush(u)!).Where(b => b is not null).ToList();
        CoreVec3 pivot = BrushTransform.SelectionPivot(brushes);
        BrushEd.EditBrushes(uids, "Rotate", b => { BrushTransform.RotateAboutPivot(b, rot, pivot); return OpResult.Ok(); });
        _prefabInstances?.ApplyRigidTransform(uids, rot, CoreVec3.Zero, pivot);
        AfterBrushEdit();
        _buildController?.ArmPostTransformBuild(); // keyboard-nudge rotate: refresh the merged stash on any size level (Q3)
    }

    /// <summary>
    /// The pool vertices the current sub-geometry selection covers, grouped by brush uid — the exact
    /// set the gizmo sub-geometry move and its planarize pass drive. Shared by the gizmo commit
    /// (<see cref="PlanarizeSubGeometryOnCommit"/>, <c>GizmoMoveSubGeometry</c>) and the keyboard-nudge
    /// sub-geometry ops so all three carry identical moved-vertex scoping.
    /// </summary>
    private Dictionary<int, HashSet<int>> SelectedSubGeometryVertsByBrush()
    {
        var byBrush = new Dictionary<int, HashSet<int>>();
        if (BrushEd is not { } be)
        {
            return byBrush;
        }

        void Add(int bu, int vi)
        {
            if (!byBrush.TryGetValue(bu, out HashSet<int>? set))
            {
                byBrush[bu] = set = new HashSet<int>();
            }

            set.Add(vi);
        }

        if (be.Mode == EditMode.Vertex)
        {
            foreach ((int bu, int vi) in be.SelectedVertices)
            {
                Add(bu, vi);
            }
        }
        else if (be.Mode == EditMode.Edge)
        {
            foreach ((int bu, int v0, int v1) in be.SelectedEdges)
            {
                Add(bu, v0);
                Add(bu, v1);
            }
        }
        else if (be.Mode == EditMode.Face)
        {
            foreach ((int bu, int fi) in be.SelectedFaces)
            {
                if (be.FindBrush(bu) is { } b && fi >= 0 && fi < b.Geometry.Faces.Count)
                {
                    foreach (FaceVertex fv in b.Geometry.Faces[fi].Vertices)
                    {
                        Add(bu, fv.Index);
                    }
                }
            }
        }

        return byBrush;
    }

    /// <summary>
    /// Keyboard-nudge translate of the selected sub-geometry (vertex/face/edge) by a world delta, as
    /// one undo entry, triangulating any face the move bent off-plane scoped to the moved vertices
    /// (RED-parity guard; see <see cref="FacePlanarizer"/>). No-op when nothing is sub-selected.
    /// </summary>
    private void NudgeSubGeometryMove(CoreVec3 worldDelta)
    {
        if (BrushEd is not { } be)
        {
            return;
        }

        Dictionary<int, HashSet<int>> byBrush = SelectedSubGeometryVertsByBrush();
        if (byBrush.Count == 0)
        {
            return;
        }

        int triangulated = 0;
        be.EditBrushes(byBrush.Keys.ToList(), "Move", b =>
        {
            if (byBrush.TryGetValue(b.Uid, out HashSet<int>? verts))
            {
                CoreVec3 local = b.Rotation.InverseTransform(worldDelta);
                foreach (int vi in verts)
                {
                    if (vi >= 0 && vi < b.Geometry.Vertices.Count)
                    {
                        b.Geometry.Vertices[vi] = b.Geometry.Vertices[vi].Add(local);
                    }
                }

                GeometryUtil.RecomputeAllPlanes(b.Geometry);
                triangulated += FacePlanarizer.Planarize(b.Geometry, verts);
            }

            return OpResult.Ok();
        });
        NotePlanarized(triangulated);
        AfterBrushEdit();
    }

    /// <summary>
    /// Keyboard-nudge rotate of the selected sub-geometry about its selection pivot, as one undo
    /// entry, triangulating any face the rotation bent off-plane (RED-parity guard). No-op when
    /// nothing is sub-selected.
    /// </summary>
    private void NudgeSubGeometryRotate(Mat3 worldRot)
    {
        if (BrushEd is not { } be)
        {
            return;
        }

        Dictionary<int, HashSet<int>> byBrush = SelectedSubGeometryVertsByBrush();
        if (byBrush.Count == 0)
        {
            return;
        }

        CoreVec3 worldPivot = SubGeometryPivot();
        int triangulated = 0;
        be.EditBrushes(byBrush.Keys.ToList(), "Rotate", b =>
        {
            if (byBrush.TryGetValue(b.Uid, out HashSet<int>? verts))
            {
                // Convert the world rotation about the world pivot into this brush's local frame.
                Mat3 localRot = Mat3Math.Compose(b.Rotation.Transpose(), Mat3Math.Compose(worldRot, b.Rotation));
                CoreVec3 localPivot = b.Rotation.InverseTransform(worldPivot.Sub(b.Position));
                foreach (int vi in verts)
                {
                    if (vi >= 0 && vi < b.Geometry.Vertices.Count)
                    {
                        b.Geometry.Vertices[vi] = localPivot.Add(localRot.Transform(b.Geometry.Vertices[vi].Sub(localPivot)));
                    }
                }

                GeometryUtil.RecomputeAllPlanes(b.Geometry);
                triangulated += FacePlanarizer.Planarize(b.Geometry, verts);
            }

            return OpResult.Ok();
        });
        NotePlanarized(triangulated);
        AfterBrushEdit();
    }

    private void OnBrushDragStarted()
    {
        // Capture the pivot + reset accumulation so the drag can snap the pivot to
        // absolute grid multiples (magnet on) rather than quantizing each delta.
        _brushDragActive = false;
        _brushDragAccum = default;
        _brushDragApplied = default;
        BeginInteractiveTransform(); // defer the O(level) rebuild to drag end; live ghost only per frame
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
        if (_session.InteractiveTransformActive)
        {
            _interactiveEditApplied = true;
            RefreshSelectionOverlay(); // cheap per-frame drag ghost; full rebuild deferred to commit
            return;
        }

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
