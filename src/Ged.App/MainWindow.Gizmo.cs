using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Avalonia.Controls;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Input;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Brush = Ged.Core.Model.Brush;
using CoreVec3 = Ged.Core.Model.Vec3;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// The first-class transform manipulator. It shows
/// automatically whenever the current mode has a transformable selection (brushes:
/// move/rotate/scale; objects: move/rotate; faces/vertices: move), with a View ▸
/// Show Gizmo opt-out. Each handle is CPU ray-picked for hover highlight and
/// press-to-drag; drags use correct world-ray math (<see cref="GizmoMath"/>) through
/// the shared <see cref="SnapPolicy"/> (magnet + Alt-invert). ESC reverts the drag
/// via a transaction rollback; release commits one undo entry. Empty-space LMB-drag
/// runs a marquee box-select. The keyboard M/R+arrow and M/N+LMB paths are unchanged.
/// The two-point Clip dialog lives here too.
/// </summary>
public sealed partial class MainWindow
{
    private enum GizmoSelKind { None, Brush, SubGeometry, Object, PrefabUnit }

    private bool _showGizmo = true;
    private bool _gizmoLocal;
    private GizmoTool _gizmoTool = GizmoTool.Move;
    private GizmoHandle _hoverHandle = GizmoHandle.None;
    private GizmoHandle _dragHandle = GizmoHandle.None;
    private bool _gizmoDragging;
    private UndoStack.Transaction? _gizmoTx;
    private IViewportSurface? _dragSurface;

    // Drag capture (world-space, from the pose at drag start).
    private GizmoPose _dragPose;
    private CoreVec3 _dragPivot;
    private CoreVec3 _dragAxis;
    private CoreVec3 _dragPlaneNormal;
    private CoreVec3 _dragPlaneStart;
    private float _dragStartParam;
    private CoreVec3 _dragRingPrevDir;
    private float _dragAngleAccum;
    private float _dragAppliedAngle;
    private CoreVec3 _dragAppliedDelta;
    private float _dragAppliedScale = 1f;
    private Vector2 _dragPivotScreen;
    private float _dragStartRadius;

    // Marquee box-select.
    private bool _marqueeDragging;
    private int _marqX0, _marqY0, _marqX1, _marqY1;
    private IViewportSurface? _marqueeSurface;

    private const float GizmoPixels = 90f;   // screen-constant handle length
    private const float GizmoPickPixels = 9f; // pick tolerance

    private Action<Vector3>? _worldPointHandler;
    private List<LineSegment> _clipPreview = new();
    private List<LineSegment> _holeLines = new();
    private ClipDialog? _clipDialog;

    private void InitGizmoAndClip()
    {
        _dispatcher.Bind(CommandIds.GizmoMove, () => SetGizmoTool(GizmoTool.Move));
        _dispatcher.Bind(CommandIds.GizmoRotate, () => SetGizmoTool(GizmoTool.Rotate));
        _dispatcher.Bind(CommandIds.GizmoScale, () => SetGizmoTool(GizmoTool.Scale));
        _dispatcher.Bind(CommandIds.GizmoNone, ToggleGizmoVisible);
        _dispatcher.Bind(CommandIds.GizmoLocalWorld, ToggleGizmoLocal);
        // "Move/Rotate Tool" (RED M/R, Modern G/R) select the manipulator tool; the
        // M/R keys also arm the in-viewport keyboard nudge, intercepted before dispatch.
        _dispatcher.Bind(CommandIds.TransformMove, () => SetGizmoTool(GizmoTool.Move));
        _dispatcher.Bind(CommandIds.TransformRotate, () => SetGizmoTool(GizmoTool.Rotate));
        _dispatcher.Bind(CommandIds.EditClipDialog, ShowClipDialog);

        _showGizmo = _settings.ShowGizmo;
        _gizmoLocal = _settings.GizmoLocal;

        _viewportGrid.ForEachSurface(s =>
        {
            s.GizmoHitTestAt = GizmoHitTest;
            s.GizmoDragStarted += (x, y) => OnGizmoDragStarted(x, y);
            s.GizmoDragMovedTo += (x, y) => OnGizmoDragMovedTo(x, y);
            s.GizmoDragEnded += OnGizmoDragEnded;
            s.GizmoDragCancelled += OnGizmoDragCancelled;
            s.GizmoHover += (x, y) => OnGizmoHover(s, x, y);
            s.MarqueeStarted += (x, y) => OnMarqueeStarted(s, x, y);
            s.MarqueeMovedTo += (x, y) => OnMarqueeMovedTo(x, y);
            s.MarqueeEnded += (x, y, add) => OnMarqueeEnded(x, y, add);
            s.WorldPointPicked += p => _worldPointHandler?.Invoke(p);
        });

        UpdateGizmoState();
    }

    // ---- Tool + visibility + local/world -------------------------------------

    /// <summary>Selects the active manipulator tool (clamped to what the selection allows).</summary>
    internal void SetGizmoTool(GizmoTool tool)
    {
        _gizmoTool = ClampTool(tool);
        if (!_showGizmo)
        {
            _showGizmo = true;
            _settings.ShowGizmo = true;
            Persist();
        }

        UpdateGizmoState();
        UpdateGizmoToolButtons();
        _dispatcher.ShowMessage(GizmoAvailable()
            ? $"{_gizmoTool} tool ({(_gizmoLocal ? "Local" : "World")})"
            : $"{_gizmoTool} tool — select something to transform");
    }

    private void ToggleGizmoVisible()
    {
        _showGizmo = !_showGizmo;
        _settings.ShowGizmo = _showGizmo;
        Persist();
        UpdateGizmoState();
        UpdateGizmoToolButtons();
        _dispatcher.ShowMessage(_showGizmo ? "Gizmo shown" : "Gizmo hidden");
    }

    private void ToggleGizmoLocal()
    {
        _gizmoLocal = !_gizmoLocal;
        _settings.GizmoLocal = _gizmoLocal;
        Persist();
        UpdateGizmoToolButtons();
        RefreshSelectionOverlay();
        _dispatcher.ShowMessage(_gizmoLocal ? "Gizmo: Local axes" : "Gizmo: World axes");
    }

    /// <summary>Pushes the current availability + marquee context onto every surface.</summary>
    internal void UpdateGizmoState()
    {
        _gizmoTool = ClampTool(_gizmoTool);
        if (!GizmoAvailable())
        {
            _hoverHandle = GizmoHandle.None;
        }

        bool avail = GizmoAvailable();
        bool marquee = Document is not null;
        _viewportGrid.ForEachSurface(s => { s.GizmoActive = avail; s.MarqueeEnabled = marquee; });
        UpdateGizmoToolButtons();
        RefreshSelectionOverlay();
    }

    private bool GizmoAvailable() => _showGizmo && TransformableKind() != GizmoSelKind.None;

    private GizmoSelKind TransformableKind()
    {
        // Feature F: a unit-selected prefab instance drives the gizmo as one rigid body, ahead of
        // (and independent of) the per-mode brush/object selection its members also populate.
        if (PrefabUnitActive)
        {
            return GizmoSelKind.PrefabUnit;
        }

        if (BrushEd is { } be)
        {
            if (be.Mode == EditMode.Brush && be.SelectedBrushes.Count > 0)
            {
                return GizmoSelKind.Brush;
            }

            if (be.Mode == EditMode.Vertex && be.SelectedVertices.Count > 0)
            {
                return GizmoSelKind.SubGeometry;
            }

            if (be.Mode == EditMode.Face && be.SelectedFaces.Count > 0)
            {
                return GizmoSelKind.SubGeometry;
            }

            if (be.Mode == EditMode.Edge && be.SelectedEdges.Count > 0)
            {
                return GizmoSelKind.SubGeometry;
            }

            if (be.Mode is EditMode.Brush or EditMode.Face or EditMode.Vertex or EditMode.Edge)
            {
                return GizmoSelKind.None; // brush-geometry modes never transform objects
            }
        }

        return Document is { } doc && doc.Selection.Count > 0 ? GizmoSelKind.Object : GizmoSelKind.None;
    }

    private bool ToolAllowed(GizmoTool t) => TransformableKind() switch
    {
        GizmoSelKind.Brush => true,
        // A prefab unit moves/rotates rigidly (no scale — a rigid body has no meaningful scale here).
        GizmoSelKind.PrefabUnit => t != GizmoTool.Scale,
        GizmoSelKind.Object => t != GizmoTool.Scale,
        // Edge sub-geometry supports full Move/Rotate/Scale (about the selection pivot);
        // Vertex/Face sub-geometry stays Move-only.
        GizmoSelKind.SubGeometry => t == GizmoTool.Move || BrushEd?.Mode == EditMode.Edge,
        _ => false,
    };

    private GizmoTool ClampTool(GizmoTool t) => ToolAllowed(t) ? t : GizmoTool.Move;

    internal GizmoTool ActiveGizmoTool => _gizmoTool;

    internal bool GizmoLocal => _gizmoLocal;

    internal bool GizmoVisible => _showGizmo;

    internal bool GizmoToolEnabled(GizmoTool t) => _showGizmo && ToolAllowed(t);

    // ---- Pose ----------------------------------------------------------------

    private GizmoPose ComputeGizmoPose(IViewportSurface s)
    {
        CoreVec3 pivot = ComputeGizmoPivot();
        (CoreVec3 ax, CoreVec3 ay, CoreVec3 az) = GizmoAxes();
        float wpp = s.Camera?.WorldPerPixel(Vn(pivot), s.SurfaceHeight) ?? 1f;
        return new GizmoPose(pivot, ax, ay, az, GizmoPixels * wpp);
    }

    private float PickTol(IViewportSurface s, CoreVec3 pivot) =>
        GizmoPickPixels * (s.Camera?.WorldPerPixel(Vn(pivot), s.SurfaceHeight) ?? 1f);

    private (CoreVec3 X, CoreVec3 Y, CoreVec3 Z) GizmoAxes()
    {
        if (_gizmoLocal && TrySelectionRotation(out Mat3 rot))
        {
            return (rot.Right, rot.Up, rot.Forward);
        }

        return (new CoreVec3(1, 0, 0), new CoreVec3(0, 1, 0), new CoreVec3(0, 0, 1));
    }

    private bool TrySelectionRotation(out Mat3 rot)
    {
        rot = Mat3.Identity;
        if (PrefabUnitActive && _prefabUnit?.UnitRecord is { } unitRec)
        {
            rot = unitRec.PivotRotation;
            return true;
        }

        if (BrushEd is { Mode: EditMode.Brush } be && be.SelectedBrushes.Count == 1 &&
            be.FindBrush(be.SelectedBrushes.First()) is { } b)
        {
            rot = b.Rotation;
            return true;
        }

        if (Document is { } doc && doc.Selection.Count == 1 && GetModelRotation(doc.Selection.First().Model) is Mat3 m)
        {
            rot = m;
            return true;
        }

        return false;
    }

    private CoreVec3 ComputeGizmoPivot()
    {
        if (PrefabUnitActive && _prefabUnit?.UnitRecord is { } unitRec)
        {
            return unitRec.PivotPosition;
        }

        if (BrushEd is { } be)
        {
            if (be.Mode == EditMode.Brush && be.SelectedBrushes.Count > 0)
            {
                return BrushTransform.SelectionPivot(
                    be.SelectedBrushes.Select(u => be.FindBrush(u)!).Where(b => b is not null).ToList());
            }

            if ((be.Mode == EditMode.Vertex && be.SelectedVertices.Count > 0) ||
                (be.Mode == EditMode.Face && be.SelectedFaces.Count > 0) ||
                (be.Mode == EditMode.Edge && be.SelectedEdges.Count > 0))
            {
                return SubGeometryPivot();
            }
        }

        if (Document is { } doc && doc.Selection.Count > 0)
        {
            CoreVec3 sum = default;
            foreach (LevelObject o in doc.Selection)
            {
                sum = sum.Add(o.Position);
            }

            return sum.Scale(1f / doc.Selection.Count);
        }

        return default;
    }

    private CoreVec3 SubGeometryPivot()
    {
        if (BrushEd is not { } be)
        {
            return default;
        }

        var pts = new List<CoreVec3>();
        if (be.Mode == EditMode.Vertex)
        {
            foreach ((int bu, int vi) in be.SelectedVertices)
            {
                if (be.FindBrush(bu) is { } b && vi < b.Geometry.Vertices.Count)
                {
                    pts.Add(BrushTransform.WorldVertex(b, b.Geometry.Vertices[vi]));
                }
            }
        }
        else if (be.Mode == EditMode.Edge)
        {
            foreach ((int bu, int v0, int v1) in be.SelectedEdges)
            {
                if (be.FindBrush(bu) is { } b)
                {
                    if (v0 < b.Geometry.Vertices.Count)
                    {
                        pts.Add(BrushTransform.WorldVertex(b, b.Geometry.Vertices[v0]));
                    }

                    if (v1 < b.Geometry.Vertices.Count)
                    {
                        pts.Add(BrushTransform.WorldVertex(b, b.Geometry.Vertices[v1]));
                    }
                }
            }
        }
        else
        {
            foreach ((int bu, int fi) in be.SelectedFaces)
            {
                if (be.FindBrush(bu) is { } b && fi < b.Geometry.Faces.Count)
                {
                    foreach (FaceVertex fv in b.Geometry.Faces[fi].Vertices)
                    {
                        pts.Add(BrushTransform.WorldVertex(b, b.Geometry.Vertices[fv.Index]));
                    }
                }
            }
        }

        if (pts.Count == 0)
        {
            return default;
        }

        CoreVec3 sum = default;
        foreach (CoreVec3 p in pts)
        {
            sum = sum.Add(p);
        }

        return sum.Scale(1f / pts.Count);
    }

    // ---- Hover + press hit-test ----------------------------------------------

    private bool GizmoHitTest(IViewportSurface s, int x, int y)
    {
        if (!GizmoAvailable() || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return false;
        }

        GizmoPose pose = ComputeGizmoPose(s);
        GizmoHandle h = GizmoPicker.Pick(pose, _gizmoTool, V(ro), V(rd), PickTol(s, pose.Pivot));
        _dragHandle = h;
        return h != GizmoHandle.None;
    }

    private void OnGizmoHover(IViewportSurface s, int x, int y)
    {
        if (!GizmoAvailable() || _gizmoDragging)
        {
            return;
        }

        GizmoHandle h = GizmoHandle.None;
        if (s.PixelRay(x, y) is (Vector3 ro, Vector3 rd))
        {
            GizmoPose pose = ComputeGizmoPose(s);
            h = GizmoPicker.Pick(pose, _gizmoTool, V(ro), V(rd), PickTol(s, pose.Pivot));
        }

        if (h != _hoverHandle)
        {
            _hoverHandle = h;
            RefreshSelectionOverlay();
        }
    }

    // ---- Drag lifecycle ------------------------------------------------------

    private void OnGizmoDragStarted(int x, int y)
    {
        IViewportSurface s = _viewportGrid.ActiveSurface;
        if (!GizmoAvailable() || _dragHandle == GizmoHandle.None || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return;
        }

        _dragSurface = s;
        _gizmoDragging = true;
        _dragPose = ComputeGizmoPose(s);
        _dragPivot = _dragPose.Pivot;
        // B1: arm snap-to-geometry for a move drag (vertex/midpoint/face targets).
        if (GizmoMath.ToolOf(_dragHandle) == GizmoTool.Move)
        {
            ArmGeometrySnap();
        }

        _gizmoTx = Document?.Undo.BeginTransaction($"{GizmoMath.ToolOf(_dragHandle)} (gizmo)");
        _dragAppliedDelta = default;
        _dragAngleAccum = 0f;
        _dragAppliedAngle = 0f;
        _dragAppliedScale = 1f;

        CoreVec3 o = V(ro);
        CoreVec3 d = V(rd);
        int axis = GizmoMath.AxisOf(_dragHandle);
        GizmoTool tool = GizmoMath.ToolOf(_dragHandle);

        if (tool == GizmoTool.Move && GizmoMath.IsPlane(_dragHandle))
        {
            _dragPlaneNormal = _dragPose.Axis(GizmoMath.PlaneNormalAxis(_dragHandle));
            GizmoMath.RayPlane(_dragPivot, _dragPlaneNormal, o, d, out _dragPlaneStart);
        }
        else if (axis >= 0)
        {
            _dragAxis = _dragPose.Axis(axis);
            _dragStartParam = GizmoMath.ClosestAxisParam(_dragPivot, _dragAxis, o, d);
            if (tool == GizmoTool.Rotate)
            {
                GizmoMath.RingPickDir(_dragPivot, _dragAxis, o, d, out _dragRingPrevDir);
            }
        }

        if (_dragHandle == GizmoHandle.ScaleUniform)
        {
            s.WorldToScreen(Vn(_dragPivot), out _dragPivotScreen);
            _dragStartRadius = Vector2.Distance(new Vector2(x, y), _dragPivotScreen);
        }

        BeginTransformIndicator(tool); // arm the in-viewport Δ/∠/% indicators
        RefreshSelectionOverlay();
    }

    private void OnGizmoDragMovedTo(int x, int y)
    {
        if (!_gizmoDragging || _dragSurface is not { } s || s.PixelRay(x, y) is not (Vector3 ro, Vector3 rd))
        {
            return;
        }

        bool invert = s.SnapInvertHeld;
        CoreVec3 o = V(ro);
        CoreVec3 d = V(rd);
        switch (GizmoMath.ToolOf(_dragHandle))
        {
            case GizmoTool.Move: GizmoDragMove(o, d, invert); break;
            case GizmoTool.Rotate: GizmoDragRotate(o, d, invert); break;
            case GizmoTool.Scale: GizmoDragScale(s, o, d, x, y, invert); break;
        }
    }

    private void OnGizmoDragEnded()
    {
        _gizmoTx?.Commit();
        _gizmoTx = null;
        _gizmoDragging = false;
        _dragSurface = null;
        DisarmGeometrySnap();
        EndTransformIndicator(rebuildIfLabeled: true); // indicators vanish on commit
        _history.Refresh();
        RefreshSelectionOverlay();
    }

    private void OnGizmoDragCancelled()
    {
        _gizmoTx?.Rollback();
        _gizmoTx = null;
        _gizmoDragging = false;
        _dragSurface = null;
        DisarmGeometrySnap();
        EndTransformIndicator(rebuildIfLabeled: false); // the RebuildScene below drops the label
        RebuildScene();
        RefreshSelectionOverlay();
        _properties.Refresh();
        _history.Refresh();
        _dispatcher.ShowMessage("Transform cancelled.");
    }

    // ---- Drag math + application ---------------------------------------------

    private void GizmoDragMove(CoreVec3 o, CoreVec3 d, bool invert)
    {
        CoreVec3 rawDelta;
        if (GizmoMath.IsPlane(_dragHandle))
        {
            if (!GizmoMath.RayPlane(_dragPivot, _dragPlaneNormal, o, d, out CoreVec3 hit))
            {
                return;
            }

            rawDelta = hit.Sub(_dragPlaneStart);
        }
        else
        {
            float param = GizmoMath.ClosestAxisParam(_dragPivot, _dragAxis, o, d);
            rawDelta = _dragAxis.Scale(param - _dragStartParam);
        }

        // B1: geometry snap (vertex > midpoint > face) takes priority over grid; the moved
        // pivot locks onto a target within ~8 px and a highlight marker renders there.
        float snapRadius = SnapWorldRadius(_dragSurface, _dragPivot.Add(rawDelta));
        CoreVec3 targetPivot = _snap.MovedPivotSnapped(_dragPivot, rawDelta, invert, snapRadius);
        CoreVec3 totalDelta = targetPivot.Sub(_dragPivot);
        CoreVec3 delta = totalDelta.Sub(_dragAppliedDelta);
        if (delta.LengthSquared() < 1e-10f)
        {
            RefreshSelectionOverlay(); // keep the snap marker live even when the pivot didn't move
            return;
        }

        _dragAppliedDelta = totalDelta;
        UpdateMoveIndicator(totalDelta); // live dimension-line label (before apply → same rebuild)
        ApplyGizmoTranslation(delta);
        string snapNote = _snap.LastGeometrySnap is { } h ? $"  ⟨snap {h.Kind}⟩" : string.Empty;
        _dispatcher.ShowMessage($"Δ  X {totalDelta.X:+0.###;-0.###;0} · Y {totalDelta.Y:+0.###;-0.###;0} · Z {totalDelta.Z:+0.###;-0.###;0} m{snapNote}");
    }

    private void ApplyGizmoTranslation(CoreVec3 delta)
    {
        switch (TransformableKind())
        {
            case GizmoSelKind.PrefabUnit:
                _prefabUnit?.RigidTransformUnit(Mat3.Identity, delta, default);
                AfterMutation();
                break;
            case GizmoSelKind.Brush when BrushEd is { } be:
                var moveBrushes = TransformableBrushUids(be);
                be.EditBrushesCoalesced(moveBrushes, "Move (gizmo)",
                    b => { BrushTransform.Move(b, delta); return OpResult.Ok(); }, null);
                _prefabInstances?.ApplyRigidTransform(moveBrushes, Mat3.Identity, delta, default);
                AfterBrushEdit();
                break;
            case GizmoSelKind.SubGeometry:
                GizmoMoveSubGeometry(delta);
                AfterBrushEdit();
                break;
            case GizmoSelKind.Object when Document is { } doc:
                var movedObjects = TransformableObjects(doc);
                var movedObjectUids = movedObjects.Select(o => o.Uid).ToList();
                foreach (LevelObject o in movedObjects)
                {
                    doc.EditValue(o.Section, "Move (gizmo)", o.Position, o.Position.Add(delta), v => o.Position = v);
                }

                _prefabInstances?.ApplyRigidTransform(movedObjectUids, Mat3.Identity, delta, default);
                AfterMutation();
                break;
        }
    }

    // ---- G defense-in-depth: transform paths refuse locked members ------------

    /// <summary>Selected brush UIDs excluding locked brushes (untransformable even if stale-selected).</summary>
    private List<int> TransformableBrushUids(BrushEditor be) =>
        be.SelectedBrushes.Where(u => !be.IsBrushLocked(u)).ToList();

    /// <summary>Selected objects excluding locked ones (untransformable even if stale-selected).</summary>
    private List<LevelObject> TransformableObjects(EditorDocument doc) =>
        doc.Selection.Where(o => !doc.IsLocked(o)).ToList();

    private void GizmoMoveSubGeometry(CoreVec3 delta)
    {
        if (BrushEd is not { } be)
        {
            return;
        }

        var byBrush = new Dictionary<int, HashSet<int>>();
        void Add(int bu, int vi)
        {
            if (!byBrush.TryGetValue(bu, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                byBrush[bu] = set;
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
        else
        {
            foreach ((int bu, int fi) in be.SelectedFaces)
            {
                if (be.FindBrush(bu) is { } b && fi < b.Geometry.Faces.Count)
                {
                    foreach (FaceVertex fv in b.Geometry.Faces[fi].Vertices)
                    {
                        Add(bu, fv.Index);
                    }
                }
            }
        }

        if (byBrush.Count == 0)
        {
            return;
        }

        be.EditBrushesCoalesced(byBrush.Keys.ToList(), "Move (gizmo)", b =>
        {
            if (byBrush.TryGetValue(b.Uid, out HashSet<int>? verts))
            {
                CoreVec3 local = b.Rotation.InverseTransform(delta);
                foreach (int vi in verts)
                {
                    if (vi < b.Geometry.Vertices.Count)
                    {
                        b.Geometry.Vertices[vi] = b.Geometry.Vertices[vi].Add(local);
                    }
                }

                GeometryUtil.RecomputeAllPlanes(b.Geometry);
            }

            return OpResult.Ok();
        }, null);
    }

    /// <summary>Applies a per-brush edge transform (rotate/scale) to the selected edges,
    /// grouped by brush, as one coalesced undo entry.</summary>
    private void GizmoTransformEdges(BrushEditor be, string desc, Action<Brush, List<BrushEdge>> apply)
    {
        Dictionary<int, List<BrushEdge>> byBrush = be.SelectedEdges
            .GroupBy(e => e.Brush)
            .ToDictionary(g => g.Key, g => g.Select(e => BrushEdge.Canonical(e.V0, e.V1)).ToList());
        if (byBrush.Count == 0)
        {
            return;
        }

        be.EditBrushesCoalesced(byBrush.Keys.ToList(), desc, b =>
        {
            if (byBrush.TryGetValue(b.Uid, out List<BrushEdge>? edges))
            {
                apply(b, edges);
            }

            return OpResult.Ok();
        }, null);
    }

    private void GizmoDragRotate(CoreVec3 o, CoreVec3 d, bool invert)
    {
        if (!GizmoMath.RingPickDir(_dragPivot, _dragAxis, o, d, out CoreVec3 dir))
        {
            return;
        }

        _dragAngleAccum += GizmoMath.SignedAngle(_dragRingPrevDir, dir, _dragAxis);
        _dragRingPrevDir = dir;
        float deg = _dragAngleAccum * 180f / MathF.PI;
        float snappedDeg = _snap.RotationDegrees(deg, invert);
        float applyDeg = snappedDeg - _dragAppliedAngle;
        if (MathF.Abs(applyDeg) < 1e-4f)
        {
            return;
        }

        _dragAppliedAngle = snappedDeg;
        UpdateRotateIndicator(snappedDeg); // live angle-arc label (before apply → same rebuild)
        Mat3 rot = Mat3Math.FromAxisAngle(_dragAxis, TransformMath.DegToRad(applyDeg));
        ApplyGizmoRotation(rot);
        _dispatcher.ShowMessage($"∠ {snappedDeg:0.#}°");
    }

    private void ApplyGizmoRotation(Mat3 rot)
    {
        CoreVec3 pivot = _dragPivot;
        if (TransformableKind() == GizmoSelKind.PrefabUnit)
        {
            _prefabUnit?.RigidTransformUnit(rot, CoreVec3.Zero, pivot);
            AfterMutation();
            return;
        }

        if (TransformableKind() == GizmoSelKind.SubGeometry && BrushEd is { Mode: EditMode.Edge } beEdge)
        {
            GizmoTransformEdges(beEdge, "Rotate edges (gizmo)", (b, edges) =>
            {
                // Convert the WORLD gizmo rotation about the WORLD pivot into this brush's
                // local frame: localRot = Rᵀ·G·R, localPivot = Rᵀ·(pivot − brushPos).
                Mat3 localRot = Mat3Math.Compose(b.Rotation.Transpose(), Mat3Math.Compose(rot, b.Rotation));
                CoreVec3 localPivot = b.Rotation.InverseTransform(pivot.Sub(b.Position));
                EdgeOps.Rotate(b.Geometry, edges, localRot, localPivot);
            });
            AfterBrushEdit();
        }
        else if (TransformableKind() == GizmoSelKind.Brush && BrushEd is { } be)
        {
            var rotBrushes = TransformableBrushUids(be);
            be.EditBrushesCoalesced(rotBrushes, "Rotate (gizmo)",
                b => { BrushTransform.RotateAboutPivot(b, rot, pivot); return OpResult.Ok(); }, null);
            _prefabInstances?.ApplyRigidTransform(rotBrushes, rot, CoreVec3.Zero, pivot);
            AfterBrushEdit();
        }
        else if (TransformableKind() == GizmoSelKind.Object && Document is { } doc)
        {
            var rotatedObjects = TransformableObjects(doc);
            var rotatedObjectUids = rotatedObjects.Select(o => o.Uid).ToList();
            foreach (LevelObject o in rotatedObjects)
            {
                CoreVec3 np = pivot.Add(rot.Transform(o.Position.Sub(pivot)));
                doc.EditValue(o.Section, "Rotate (gizmo)", o.Position, np, v => o.Position = v);
                if (GetModelRotation(o.Model) is Mat3 cur)
                {
                    Mat3 nr = Mat3Math.Compose(rot, cur).Orthonormalize();
                    doc.EditValue(o.Section, "Rotate (gizmo)", cur, nr, v => SetModelRotation(o.Model, v));
                }
            }

            _prefabInstances?.ApplyRigidTransform(rotatedObjectUids, rot, CoreVec3.Zero, pivot);
            AfterMutation();
        }
    }

    private void GizmoDragScale(IViewportSurface s, CoreVec3 o, CoreVec3 d, int x, int y, bool invert)
    {
        bool uniform = _dragHandle == GizmoHandle.ScaleUniform;
        float factor;
        if (uniform)
        {
            float radius = Vector2.Distance(new Vector2(x, y), _dragPivotScreen);
            factor = GizmoMath.RadialScaleFactor(_dragStartRadius, radius);
        }
        else
        {
            float param = GizmoMath.ClosestAxisParam(_dragPivot, _dragAxis, o, d);
            factor = GizmoMath.AxisScaleFactor(_dragStartParam, param);
        }

        factor = Math.Clamp(factor, 0.01f, 100f);
        factor = _snap.ScaleFactor(factor, invert);
        if (MathF.Abs(factor - _dragAppliedScale) < 1e-4f)
        {
            return;
        }

        float rel = factor / _dragAppliedScale;
        _dragAppliedScale = factor;
        UpdateScaleIndicator(factor); // live percentage label (before apply → same rebuild)
        ApplyGizmoScale(uniform, _dragAxis, rel);
        _dispatcher.ShowMessage($"⤢ {factor * 100f:0.#}%");
    }

    private void ApplyGizmoScale(bool uniform, CoreVec3 axis, float rel)
    {
        if (TransformableKind() == GizmoSelKind.SubGeometry && BrushEd is { Mode: EditMode.Edge } beEdge)
        {
            CoreVec3 wp = _dragPivot;
            GizmoTransformEdges(beEdge, "Scale edges (gizmo)", (b, edges) =>
            {
                CoreVec3 localPivot = b.Rotation.InverseTransform(wp.Sub(b.Position));
                if (uniform)
                {
                    EdgeOps.Scale(b.Geometry, edges, localPivot, rel);
                }
                else
                {
                    EdgeOps.ScaleAxis(b.Geometry, edges, localPivot, b.Rotation.InverseTransform(axis), rel);
                }
            });
            AfterBrushEdit();
            return;
        }

        if (BrushEd is not { } be || be.SelectedBrushes.Count == 0)
        {
            return;
        }

        CoreVec3 pivot = _dragPivot;
        be.EditBrushesCoalesced(be.SelectedBrushes.ToList(), "Scale (gizmo)", b =>
        {
            if (uniform)
            {
                b.Position = pivot.Add(b.Position.Sub(pivot).Scale(rel));
                for (int i = 0; i < b.Geometry.Vertices.Count; i++)
                {
                    b.Geometry.Vertices[i] = b.Geometry.Vertices[i].Scale(rel);
                }

                GeometryUtil.RecomputeAllPlanes(b.Geometry);
            }
            else
            {
                ScaleBrushAxial(b, axis, rel, pivot);
            }

            return OpResult.Ok();
        }, null);
        AfterBrushEdit();
    }

    /// <summary>Non-uniform scale of a brush along a world/local axis about the pivot (affine — faces stay planar).</summary>
    private static void ScaleBrushAxial(Brush b, CoreVec3 axis, float rel, CoreVec3 pivot)
    {
        CoreVec3 a = axis.Normalized();
        CoreVec3 Axial(CoreVec3 wp)
        {
            float c = wp.Sub(pivot).Dot(a);
            return wp.Add(a.Scale(c * (rel - 1f)));
        }

        CoreVec3 newPos = Axial(b.Position);
        for (int i = 0; i < b.Geometry.Vertices.Count; i++)
        {
            CoreVec3 world = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[i]));
            b.Geometry.Vertices[i] = b.Rotation.InverseTransform(Axial(world).Sub(newPos));
        }

        b.Position = newPos;
        GeometryUtil.RecomputeAllPlanes(b.Geometry);
    }

    // ---- Marquee box-select --------------------------------------------------

    private void OnMarqueeStarted(IViewportSurface s, int x, int y)
    {
        _marqueeSurface = s;
        _marqueeDragging = true;
        _marqX0 = _marqX1 = x;
        _marqY0 = _marqY1 = y;
        RefreshSelectionOverlay();
    }

    private void OnMarqueeMovedTo(int x, int y)
    {
        _marqX1 = x;
        _marqY1 = y;
        RefreshSelectionOverlay();
    }

    private void OnMarqueeEnded(int x, int y, bool additive)
    {
        _marqX1 = x;
        _marqY1 = y;
        bool was = _marqueeDragging;
        IViewportSurface? s = _marqueeSurface;
        _marqueeDragging = false;
        _marqueeSurface = null;
        RefreshSelectionOverlay();
        if (was && s is not null && MarqueeSelection.IsMarquee(_marqX0, _marqY0, x, y))
        {
            ApplyMarquee(s, additive);
        }
    }

    private void ApplyMarquee(IViewportSurface s, bool additive)
    {
        if (Document is null)
        {
            return;
        }

        SelectKinds active = _filter.Active;
        MarqueeSelection.Rect rect = MarqueeSelection.FromCorners(_marqX0, _marqY0, _marqX1, _marqY1);
        var cands = new List<MarqueeSelection.Candidate>();

        if ((active & (SelectKinds.Objects | SelectKinds.Groups)) != 0)
        {
            foreach (LevelObject o in Document.Objects)
            {
                if (!o.Hidden && s.WorldToScreen(Vn(o.Position), out Vector2 sc))
                {
                    cands.Add(new MarqueeSelection.Candidate(o.Uid, SelectKinds.Objects, sc.X, sc.Y));
                }
            }
        }

        if ((active & SelectKinds.Brushes) != 0 && BrushEd is { } be)
        {
            foreach (Brush b in be.Brushes)
            {
                if (s.WorldToScreen(Vn(BrushTransform.WorldCentroid(b)), out Vector2 sc))
                {
                    cands.Add(new MarqueeSelection.Candidate(b.Uid, SelectKinds.Brushes, sc.X, sc.Y));
                }
            }
        }

        List<int> hits = MarqueeSelection.Select(rect, cands, active);

        // Feature F: a member hit selects its WHOLE instance (unit semantics), except the instance
        // currently entered for member editing, and except an instance with a locked member (G point
        // 4: unselectable as a unit). Non-member hits stay plain.
        var instanceIds = new List<int>();
        var plainHits = new List<int>();
        foreach (int id in hits)
        {
            if (_prefabUnit?.MemberInstance(id) is { } rec &&
                _prefabUnit.EnteredInstanceId != rec.InstanceId &&
                _prefabUnit.CanSelectAsUnit(rec.InstanceId))
            {
                if (!instanceIds.Contains(rec.InstanceId))
                {
                    instanceIds.Add(rec.InstanceId);
                }
            }
            else
            {
                plainHits.Add(id);
            }
        }

        // A single instance caught alone drives the unit gizmo; any mix falls back to multi-select.
        if (!additive && instanceIds.Count == 1 && plainHits.Count == 0 &&
            _prefabUnit?.SelectUnit(instanceIds[0]) == true)
        {
            UpdateGizmoState();
            RefreshSelectionOverlay();
            _dispatcher.ShowMessage($"Marquee selected prefab instance {instanceIds[0]} as a unit.");
            return;
        }

        _prefabUnit?.Reset();
        if (!additive)
        {
            Document.ClearSelection();
            BrushEd?.ClearSelection();
        }

        int n = 0;
        foreach (int id in plainHits)
        {
            // Route every plain marquee hit through the SAME PickGate the click path uses, so box-
            // select can never drift from click-select (item 2).
            if (BrushEd is { } be2 && be2.FindBrush(id) is not null &&
                Ged.App.Services.PickGate.AllowsBrushEditor(active, Ged.Rendering.Picking.PickKind.Brush))
            {
                if (_session.Selection.SelectBrush(id, additive: true))
                {
                    n++;
                }
            }
            else if (Document.FindByUid(id) is { } o &&
                Ged.App.Services.PickGate.AllowsDocumentSelect(active, Ged.Rendering.Picking.PickKind.Object, o.Kind == LevelObjectKind.Mover))
            {
                if (_session.Selection.SelectObject(o, additive: true))
                {
                    n++;
                }
            }
        }

        // Expanded instance members select as whole units (both kinds, group-like gate, skip locked).
        foreach (int iid in instanceIds)
        {
            if (_prefabInstances?.ById(iid) is { } rec)
            {
                _session.Selection.AddPrefabUnitMembers(rec.MemberUids);
                n += rec.MemberUids.Count;
            }
        }

        UpdateGizmoState();
        RefreshSelectionOverlay();
        _dispatcher.ShowMessage(n > 0 ? $"Marquee selected {n} item(s)." : "Marquee selected nothing.");
    }

    // ---- Gizmo widget drawing ------------------------------------------------

    /// <summary>Builds the manipulator line set at the pivot for the active tool.</summary>
    private IEnumerable<LineSegment> BuildGizmoLines()
    {
        if (!GizmoAvailable())
        {
            return Array.Empty<LineSegment>();
        }

        return BuildGizmoLinesCore();
    }

    private IEnumerable<LineSegment> BuildGizmoLinesCore()
    {
        IViewportSurface s = _viewportGrid.ActiveSurface;
        GizmoPose pose = ComputeGizmoPose(s);
        Vector3 camRight = Vector3.UnitX;
        Vector3 camUp = Vector3.UnitY;
        if (s.Camera is { } cam)
        {
            camRight = SafeNormalize(cam.Right);
            camUp = SafeNormalize(cam.Up);
        }

        return GizmoGeometry.Build(
            pose, _gizmoTool, _hoverHandle, _dragHandle, _gizmoDragging,
            HexColor(_settings.ColorAxisX), HexColor(_settings.ColorAxisY), HexColor(_settings.ColorAxisZ),
            camRight, camUp);
    }

    /// <summary>The marquee rectangle, unprojected onto a camera-facing plane (drawn while dragging).</summary>
    private IEnumerable<LineSegment> BuildMarqueeLines()
    {
        if (!_marqueeDragging || _marqueeSurface is not { } s || s.Camera is not { } cam)
        {
            yield break;
        }

        Vector3 normal = SafeNormalize(cam.Forward);
        Vector3 planePoint = GizmoAvailable() ? Vn(ComputeGizmoPivot()) : cam.Position + (normal * 20f);
        (int cx, int cy)[] corners = { (_marqX0, _marqY0), (_marqX1, _marqY0), (_marqX1, _marqY1), (_marqX0, _marqY1) };
        var w = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            if (s.PixelRay(corners[i].cx, corners[i].cy) is not (Vector3 ro, Vector3 rd) ||
                !RayPlaneN(planePoint, normal, ro, rd, out w[i]))
            {
                yield break;
            }
        }

        uint col = Palette.Rgba(255, 220, 80);
        for (int i = 0; i < 4; i++)
        {
            yield return new LineSegment(w[i], w[(i + 1) % 4], col);
        }
    }

    // ---- Toolbar sync (defined in MainWindow.cs) -----------------------------

    partial void UpdateGizmoToolButtons();

    // ---- Small helpers -------------------------------------------------------

    private static Vector3 Vn(CoreVec3 v) => new(v.X, v.Y, v.Z);

    private static CoreVec3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;

    private static bool RayPlaneN(Vector3 planePoint, Vector3 normal, Vector3 ro, Vector3 rd, out Vector3 hit)
    {
        float denom = Vector3.Dot(normal, rd);
        if (MathF.Abs(denom) < 1e-9f)
        {
            hit = default;
            return false;
        }

        float t = Vector3.Dot(normal, planePoint - ro) / denom;
        hit = ro + (rd * t);
        return true;
    }

    private static uint HexColor(string hex, byte a = 255)
    {
        string h = hex.TrimStart('#');
        if (h.Length >= 6 &&
            byte.TryParse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return Palette.Rgba(r, g, b, a);
        }

        return Palette.Rgba(200, 200, 200, a);
    }

    // ---- Two-point Clip dialog ------------------------------------------------

    private void ShowClipDialog()
    {
        if (BrushEd is null || BrushEd.SelectedBrushes.Count == 0)
        {
            _dispatcher.ShowMessage("Select a brush to clip.");
            return;
        }

        if (_clipDialog is not null)
        {
            _clipDialog.Activate();
            return;
        }

        _clipDialog = new ClipDialog(this, _settings, ActivePaneDepthAxis());
        _clipDialog.Closed += (_, _) => { _clipDialog = null; SetClipPreview(null, null, 0); ArmClipPick(false, null); };
        _clipDialog.Show(this);
    }

    internal OpResult ClipWithPoints(CoreVec3 a, CoreVec3 b, int viewAxis, ClipMode mode, bool flip)
    {
        if (BrushEd is null)
        {
            return OpResult.Fail("No document.");
        }

        (CoreVec3 point, CoreVec3 normal) = ClipPlanes.FromTwoPoints(a, b, TransformMath.Axis(viewAxis));
        OpResult r = BrushEd.Clip(point, normal, mode, flip);
        Report(r);
        AfterBrushEdit();
        return r;
    }

    internal void ArmClipPick(bool armed, Action<Vector3>? onPoint)
    {
        _worldPointHandler = armed ? onPoint : null;
        _viewportGrid.ForEachSurface(s => s.PointPickArmed = armed);
    }

    internal void SetClipPreview(CoreVec3? a, CoreVec3? b, int viewAxis)
    {
        _clipPreview = new List<LineSegment>();
        if (a is CoreVec3 pa && b is CoreVec3 pb)
        {
            uint color = Palette.Rgba(255, 200, 60);
            var va = new Vector3(pa.X, pa.Y, pa.Z);
            var vb = new Vector3(pb.X, pb.Y, pb.Z);
            _clipPreview.Add(new LineSegment(va, vb, color));

            // Plane hint: extrude the cut line a little along the view axis both ways.
            Vector3 depth = viewAxis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
            float ext = MathF.Max(2f, _settings.GridSize * 4f);
            _clipPreview.Add(new LineSegment(va - (depth * ext), va + (depth * ext), color));
            _clipPreview.Add(new LineSegment(vb - (depth * ext), vb + (depth * ext), color));
        }

        RefreshSelectionOverlay();
    }

    internal int GizmoDepthAxisForActivePane() => ActivePaneDepthAxis();
}

/// <summary>
/// The stock two-point Clip dialog: pick or type two viewport points that define a
/// cutting plane (the third axis from the view direction), preview it, then
/// Split / Cut / flip the normal. Modeless; its position is persisted.
/// </summary>
internal sealed class ClipDialog : Window
{
    private readonly MainWindow _owner;
    private readonly AppSettings _settings;

    private readonly TextBox _ax = Num(), _ay = Num(), _az = Num();
    private readonly TextBox _bx = Num(), _by = Num(), _bz = Num();
    private readonly CheckBox _gridSnap = new() { Content = "Grid snap", IsChecked = true };
    private readonly CheckBox _flip = new() { Content = "Flip normal" };
    private readonly ComboBox _axis;

    public ClipDialog(MainWindow owner, AppSettings settings, int defaultAxis)
    {
        _owner = owner;
        _settings = settings;

        Title = "Clip (two-point plane)";
        Width = 320;
        Height = 300;
        WindowStartupLocation = settings.ClipDialogX is not null ? WindowStartupLocation.Manual : WindowStartupLocation.CenterOwner;
        if (settings.ClipDialogX is int x && settings.ClipDialogY is int y)
        {
            Position = new Avalonia.PixelPoint(x, y);
        }

        _axis = new ComboBox { ItemsSource = new[] { "View axis X", "View axis Y", "View axis Z" }, SelectedIndex = Math.Clamp(defaultAxis, 0, 2) };
        _axis.SelectionChanged += (_, _) => UpdatePreview();

        var panel = new StackPanel { Margin = new Avalonia.Thickness(10), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Two points define the cut line; the plane extrudes along the chosen view axis.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray });
        panel.Children.Add(PointRow("Point A", _ax, _ay, _az, () => ArmPick(true)));
        panel.Children.Add(PointRow("Point B", _bx, _by, _bz, () => ArmPick(false)));
        panel.Children.Add(_axis);
        panel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { _gridSnap, _flip } });

        var split = new Button { Content = "Split" };
        split.Click += (_, _) => DoClip(ClipMode.Split);
        var cut = new Button { Content = "Cut" };
        cut.Click += (_, _) => DoClip(ClipMode.Cut);
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        panel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { split, cut, close } });

        Content = panel;

        foreach (TextBox t in new[] { _ax, _ay, _az, _bx, _by, _bz })
        {
            t.LostFocus += (_, _) => UpdatePreview();
        }

        PositionChanged += (_, _) => { _settings.ClipDialogX = Position.X; _settings.ClipDialogY = Position.Y; };
    }

    private void ArmPick(bool pointA)
    {
        _owner.ArmClipPick(true, world =>
        {
            float gx = world.X, gy = world.Y, gz = world.Z;
            if (_gridSnap.IsChecked == true)
            {
                float g = Math.Max(0.03125f, _settings.GridSize);
                gx = MathF.Round(gx / g) * g;
                gy = MathF.Round(gy / g) * g;
                gz = MathF.Round(gz / g) * g;
            }

            (pointA ? _ax : _bx).Text = gx.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            (pointA ? _ay : _by).Text = gy.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            (pointA ? _az : _bz).Text = gz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            _owner.ArmClipPick(false, null);
            UpdatePreview();
        });
    }

    private bool TryPoints(out Ged.Core.Model.Vec3 a, out Ged.Core.Model.Vec3 b)
    {
        a = b = default;
        if (!TryVec(_ax, _ay, _az, out a) || !TryVec(_bx, _by, _bz, out b))
        {
            return false;
        }

        return true;
    }

    private void UpdatePreview()
    {
        if (TryPoints(out Ged.Core.Model.Vec3 a, out Ged.Core.Model.Vec3 b))
        {
            _owner.SetClipPreview(a, b, _axis.SelectedIndex);
        }
        else
        {
            _owner.SetClipPreview(null, null, 0);
        }
    }

    private void DoClip(ClipMode mode)
    {
        if (!TryPoints(out Ged.Core.Model.Vec3 a, out Ged.Core.Model.Vec3 b))
        {
            return;
        }

        _owner.ClipWithPoints(a, b, _axis.SelectedIndex, mode, _flip.IsChecked == true);
    }

    private static bool TryVec(TextBox x, TextBox y, TextBox z, out Ged.Core.Model.Vec3 v)
    {
        v = default;
        if (float.TryParse(x.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fx) &&
            float.TryParse(y.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fy) &&
            float.TryParse(z.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fz))
        {
            v = new Ged.Core.Model.Vec3(fx, fy, fz);
            return true;
        }

        return false;
    }

    private static TextBox Num() => new() { Width = 60, FontSize = 11 };

    private Control PointRow(string label, TextBox x, TextBox y, TextBox z, Action pick)
    {
        var pickBtn = new Button { Content = "Pick", FontSize = 11 };
        pickBtn.Click += (_, _) => pick();
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(new TextBlock { Text = label, Width = 54, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 11 });
        row.Children.Add(x);
        row.Children.Add(y);
        row.Children.Add(z);
        row.Children.Add(pickBtn);
        return row;
    }
}
