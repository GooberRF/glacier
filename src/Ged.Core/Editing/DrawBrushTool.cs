using System;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>The draw-brush tool's interaction stages.</summary>
public enum DrawBrushStage
{
    /// <summary>Tool inactive; hovers and clicks are ignored.</summary>
    Idle,

    /// <summary>Stage 1: pick the work plane (face under the cursor, else the grid plane) and corner A.</summary>
    BasePoint,

    /// <summary>Stage 2: rubber-band the base rectangle on the work plane to corner B.</summary>
    Rectangle,

    /// <summary>Stage 3: extrude the base rectangle along the work-plane normal.</summary>
    Height,
}

/// <summary>Why the last <see cref="DrawBrushTool.Click"/> did (or did not) advance the stage.</summary>
public enum DrawBrushClickOutcome
{
    /// <summary>No click processed yet (or the tool was idle).</summary>
    None,

    /// <summary>The click advanced to the next stage.</summary>
    Advanced,

    /// <summary>The click committed the box; the tool reset to <see cref="DrawBrushStage.BasePoint"/>.</summary>
    Committed,

    /// <summary>The pixel ray is parallel to the work plane (e.g. an ortho Top view vs the Y-up grid plane).</summary>
    PlaneUnreachable,

    /// <summary>The base rectangle collapsed to zero width or depth after snapping; stage not advanced.</summary>
    DegenerateRectangle,

    /// <summary>The extrusion height is zero after snapping; the commit click was rejected.</summary>
    ZeroHeight,
}

/// <summary>The committed box: world-space center and full X/Y/Z extents (metres).</summary>
public readonly record struct DrawBrushResult(Vec3 Center, float Width, float Height, float Depth);

/// <summary>
/// The SketchUp/Blender-style three-stage interactive box creation (Item 8), pure of
/// UI: callers feed world-space pixel rays and receive a snapped preview point, a
/// live ghost box and finally a <see cref="DrawBrushResult"/> to hand to
/// <see cref="BrushEditor.CreateBrush"/>.
///
/// Stage 1 picks the work plane: the face under the cursor when the optional
/// <see cref="PlaneProvider"/> reports a hit, else the world grid plane
/// (Y = <see cref="GridLevel"/>, normal +Y). A face plane's normal is snapped to its
/// dominant world axis and the box is built axis-aligned on that snapped plane —
/// full oriented (rotated) boxes are out of scope for this tool. Stage 2 rubber-bands
/// the base rectangle by intersecting each ray with the fixed work plane. Stage 3
/// maps the ray to an extrusion height via the closest point between the pixel ray
/// and the axis through the base-rectangle center along the plane normal
/// (<see cref="GizmoMath.ClosestAxisParam"/>); the height is clamped to &gt;= 0, so
/// the box always extrudes along the picked normal.
///
/// Snapping (magnet-aware — the caller passes the live toggle through
/// <see cref="SnapEnabled"/>) quantizes the two in-plane coordinates and the height
/// to <see cref="GridSize"/> via <see cref="TransformMath.Snap"/>; the coordinate
/// along the plane normal always stays on the plane, so drawing on a face at a
/// non-grid offset never pulls the base off that face.
///
/// A committing click returns the result and resets to
/// <see cref="DrawBrushStage.BasePoint"/> so the tool stays armed for repeat draws;
/// <see cref="Cancel"/> (ESC) returns to <see cref="DrawBrushStage.Idle"/> with no
/// side effects — the tool never touches a document.
/// </summary>
public sealed class DrawBrushTool
{
    /// <summary>Below this extent (after snapping) a rectangle or height counts as degenerate.</summary>
    private const float DegenerateEps = 1e-4f;

    /// <summary>Thin slab thickness used for the stage-2 ghost (visibility only).</summary>
    private const float GhostSlabThickness = 0.05f;

    // The fixed work plane (valid in Rectangle/Height): a point on the plane, the
    // dominant world axis of its normal (0=X, 1=Y, 2=Z) and the extrusion sign.
    private Vec3 _planePoint;
    private int _axis = 1;
    private float _sign = 1f;

    private Vec3 _cornerA;
    private Vec3 _cornerB;
    private float _height;

    /// <summary>World grid size (metres) the in-plane points and height snap to.</summary>
    public float GridSize { get; set; } = 1f;

    /// <summary>Live magnet toggle: when false, points and height stay continuous.</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>Y level of the fallback grid plane (world grid; 0 matches the editor default).</summary>
    public float GridLevel { get; set; }

    /// <summary>
    /// Stage-1 face pick: maps a (ray origin, ray direction) to a surface point and
    /// outward normal, or null when the ray hits no geometry (grid-plane fallback).
    /// The App supplies a compiled-geometry raycast; tests supply fakes.
    /// </summary>
    public Func<(Vec3 Origin, Vec3 Dir), (Vec3 Point, Vec3 Normal)?>? PlaneProvider { get; set; }

    /// <summary>
    /// Optional snap-to-geometry hook (B1) applied to a resolved stage point AFTER the
    /// grid snap: the App returns a nearby vertex/midpoint/face point, or the input
    /// unchanged when nothing is in range — so geometry targets win over the grid.
    /// </summary>
    public Func<Vec3, Vec3>? PointSnap { get; set; }

    public DrawBrushStage Stage { get; private set; } = DrawBrushStage.Idle;

    /// <summary>The snapped stage-1 preview point under the cursor (null before the first hover).</summary>
    public Vec3? PreviewPoint { get; private set; }

    /// <summary>Why the last <see cref="Click"/> advanced or was rejected.</summary>
    public DrawBrushClickOutcome LastClick { get; private set; }

    /// <summary>Live rectangle extent along the first in-plane axis (X for the Y-up plane).</summary>
    public float WidthReadout => Stage is DrawBrushStage.Rectangle or DrawBrushStage.Height
        ? MathF.Abs(_cornerB.Component(AxisU) - _cornerA.Component(AxisU))
        : 0f;

    /// <summary>Live rectangle extent along the second in-plane axis (Z for the Y-up plane).</summary>
    public float DepthReadout => Stage is DrawBrushStage.Rectangle or DrawBrushStage.Height
        ? MathF.Abs(_cornerB.Component(AxisV) - _cornerA.Component(AxisV))
        : 0f;

    /// <summary>Live extrusion height along the work-plane normal (stage 3).</summary>
    public float HeightReadout => Stage == DrawBrushStage.Height ? _height : 0f;

    /// <summary>
    /// The current ghost box (center + full world X/Y/Z extents), or null when there
    /// is nothing to show. Stage 2 renders a thin slab on the work plane for
    /// visibility; stage 3 renders the real extrusion (minimum-thickness while the
    /// height is still zero).
    /// </summary>
    public (Vec3 Center, float Width, float Height, float Depth)? GhostBox
    {
        get
        {
            switch (Stage)
            {
                case DrawBrushStage.Rectangle:
                    return BoxFor(GhostSlabThickness, centerOnPlane: true);
                case DrawBrushStage.Height:
                    return BoxFor(MathF.Max(_height, GhostSlabThickness), centerOnPlane: false);
                default:
                    return null;
            }
        }
    }

    /// <summary>Arms the tool at stage 1. Also used after a commit or cancel to start over.</summary>
    public void Begin()
    {
        Stage = DrawBrushStage.BasePoint;
        PreviewPoint = null;
        LastClick = DrawBrushClickOutcome.None;
        _height = 0f;
    }

    /// <summary>Disarms the tool with no side effects (ESC at any stage).</summary>
    public void Cancel()
    {
        Stage = DrawBrushStage.Idle;
        PreviewPoint = null;
        LastClick = DrawBrushClickOutcome.None;
        _height = 0f;
    }

    /// <summary>
    /// Per-frame pointer update: refreshes the stage-1 preview point, the stage-2
    /// rubber-band corner or the stage-3 height. Returns false when the ray cannot
    /// reach the work plane (parallel — see the ortho note on <see cref="Click"/>).
    /// </summary>
    public bool Hover(Vec3 rayOrigin, Vec3 rayDir)
    {
        switch (Stage)
        {
            case DrawBrushStage.BasePoint:
                if (!ResolveBasePoint(rayOrigin, rayDir, out Vec3 point, out _, out _))
                {
                    return false;
                }

                PreviewPoint = point;
                return true;

            case DrawBrushStage.Rectangle:
                if (!RayToWorkPlane(rayOrigin, rayDir, out Vec3 hit))
                {
                    return false;
                }

                _cornerB = hit;
                return true;

            case DrawBrushStage.Height:
                _height = HeightFromRay(rayOrigin, rayDir);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Advances the state machine: stage 1 fixes the work plane and corner A, stage 2
    /// fixes corner B (a rectangle that snapped to zero width or depth does NOT
    /// advance), stage 3 commits — returning the box and resetting to
    /// <see cref="DrawBrushStage.BasePoint"/> for the next draw. Returns null for
    /// every non-committing click; <see cref="LastClick"/> carries the reason.
    ///
    /// Ortho note: <see cref="Ged.Rendering"/> cameras produce valid rays for ortho
    /// panes too, and stages 1–2 work there whenever the pane's ray can reach the
    /// work plane (e.g. a Front view onto a Z-facing wall). Where the ray is parallel
    /// to the plane — e.g. the Top view vs the Y-up grid plane — the intersection
    /// does not exist, the click reports <see cref="DrawBrushClickOutcome.PlaneUnreachable"/>
    /// and the tool stays put: effectively perspective-only for that plane.
    /// </summary>
    public DrawBrushResult? Click(Vec3 rayOrigin, Vec3 rayDir)
    {
        switch (Stage)
        {
            case DrawBrushStage.BasePoint:
                if (!ResolveBasePoint(rayOrigin, rayDir, out Vec3 point, out int axis, out float sign))
                {
                    LastClick = DrawBrushClickOutcome.PlaneUnreachable;
                    return null;
                }

                _axis = axis;
                _sign = sign;
                _planePoint = point;
                _cornerA = point;
                _cornerB = point;
                PreviewPoint = point;
                Stage = DrawBrushStage.Rectangle;
                LastClick = DrawBrushClickOutcome.Advanced;
                return null;

            case DrawBrushStage.Rectangle:
                if (!RayToWorkPlane(rayOrigin, rayDir, out Vec3 hit))
                {
                    LastClick = DrawBrushClickOutcome.PlaneUnreachable;
                    return null;
                }

                _cornerB = hit;
                if (WidthReadout <= DegenerateEps || DepthReadout <= DegenerateEps)
                {
                    LastClick = DrawBrushClickOutcome.DegenerateRectangle;
                    return null; // stay in Rectangle until the base has area
                }

                _height = 0f;
                Stage = DrawBrushStage.Height;
                LastClick = DrawBrushClickOutcome.Advanced;
                return null;

            case DrawBrushStage.Height:
                _height = HeightFromRay(rayOrigin, rayDir);
                if (_height <= DegenerateEps)
                {
                    LastClick = DrawBrushClickOutcome.ZeroHeight;
                    return null; // a zero-height box would be degenerate geometry
                }

                (Vec3 center, float w, float h, float d) = BoxFor(_height, centerOnPlane: false);
                Begin(); // stay armed for repeat draws (the App's ESC disarms fully)
                LastClick = DrawBrushClickOutcome.Committed;
                return new DrawBrushResult(center, w, h, d);

            default:
                LastClick = DrawBrushClickOutcome.None;
                return null;
        }
    }

    // ---- Plane / snapping internals --------------------------------------------

    private int AxisU => _axis == 0 ? 1 : 0;

    private int AxisV => _axis == 2 ? 1 : 2;

    /// <summary>
    /// Resolves the stage-1 base point: face plane from the provider when the ray
    /// hits geometry (normal snapped to its dominant axis), else the Y-up grid
    /// plane at <see cref="GridLevel"/>. False when the ray is parallel to the
    /// grid plane and no face was hit.
    /// </summary>
    private bool ResolveBasePoint(Vec3 rayOrigin, Vec3 rayDir, out Vec3 point, out int axis, out float sign)
    {
        if (PlaneProvider?.Invoke((rayOrigin, rayDir)) is (Vec3 facePoint, Vec3 faceNormal) &&
            faceNormal.LengthSquared() > DegenerateEps)
        {
            // Axis-aligned approximation: snap the face normal to its dominant world
            // axis and draw on the plane through the hit point with that normal.
            (axis, sign) = DominantAxis(faceNormal);
            point = GeometrySnap(SnapInPlane(facePoint, axis, facePoint.Component(axis)));
            return true;
        }

        axis = 1;
        sign = 1f;
        var gridPoint = new Vec3(0f, GridLevel, 0f);
        if (!GizmoMath.RayPlane(gridPoint, new Vec3(0f, 1f, 0f), rayOrigin, rayDir, out Vec3 hit))
        {
            point = default;
            return false;
        }

        point = GeometrySnap(SnapInPlane(hit, axis, GridLevel));
        return true;
    }

    /// <summary>Intersects a ray with the fixed work plane and snaps in-plane.</summary>
    private bool RayToWorkPlane(Vec3 rayOrigin, Vec3 rayDir, out Vec3 snapped)
    {
        Vec3 normal = Vec3.Zero.WithComponent(_axis, 1f);
        if (!GizmoMath.RayPlane(_planePoint, normal, rayOrigin, rayDir, out Vec3 hit))
        {
            snapped = default;
            return false;
        }

        snapped = GeometrySnap(SnapInPlane(hit, _axis, _planePoint.Component(_axis)));
        return true;
    }

    /// <summary>Applies the optional geometry-snap hook (B1) to a resolved point.</summary>
    private Vec3 GeometrySnap(Vec3 p) => SnapEnabled && PointSnap is { } snap ? snap(p) : p;

    /// <summary>
    /// Height from a stage-3 ray: the parameter of the closest point between the
    /// pixel ray and the extrusion axis (base-rect center, signed plane normal) —
    /// the standard closest-point-between-two-lines mapping — snapped to the grid
    /// and clamped to &gt;= 0 (the box only extrudes along the picked normal).
    /// </summary>
    private float HeightFromRay(Vec3 rayOrigin, Vec3 rayDir)
    {
        Vec3 axisDir = Vec3.Zero.WithComponent(_axis, _sign);
        float t = GizmoMath.ClosestAxisParam(BaseCenter(), axisDir, rayOrigin, rayDir);
        if (SnapEnabled)
        {
            t = TransformMath.Snap(t, GridSize);
        }

        return MathF.Max(0f, t);
    }

    /// <summary>Snaps the two in-plane coordinates to the grid; the plane coordinate stays on the plane.</summary>
    private Vec3 SnapInPlane(Vec3 p, int axis, float planeLevel)
    {
        if (SnapEnabled)
        {
            p = TransformMath.Snap(p, GridSize);
        }

        return p.WithComponent(axis, planeLevel);
    }

    private Vec3 BaseCenter() => Vec3Math.Lerp(_cornerA, _cornerB, 0.5f);

    /// <summary>The axis-aligned box for the current base rect and a normal-axis extent.</summary>
    private (Vec3 Center, float Width, float Height, float Depth) BoxFor(float extent, bool centerOnPlane)
    {
        Vec3 center = BaseCenter();
        if (!centerOnPlane)
        {
            center = center.WithComponent(_axis, center.Component(_axis) + (_sign * extent * 0.5f));
        }

        Vec3 extents = Vec3.Zero
            .WithComponent(AxisU, WidthReadout)
            .WithComponent(AxisV, DepthReadout)
            .WithComponent(_axis, extent);
        return (center, extents.X, extents.Y, extents.Z);
    }

    /// <summary>The dominant world axis (index + sign) of a normal; ties resolve X → Y → Z.</summary>
    private static (int Axis, float Sign) DominantAxis(Vec3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az)
        {
            return (0, MathF.Sign(n.X) < 0 ? -1f : 1f);
        }

        return ay >= az ? (1, MathF.Sign(n.Y) < 0 ? -1f : 1f) : (2, MathF.Sign(n.Z) < 0 ? -1f : 1f);
    }
}
