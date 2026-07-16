using System;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The single snap policy consumed by every mouse-driven transform (the move /
/// rotate / scale gizmos, the M+LMB window-plane drag and the N+LMB axis drag).
/// The magnet toggle drives <see cref="Enabled"/> globally.
///
/// Semantics when active:
/// <list type="bullet">
/// <item><b>Move</b> is an <i>absolute</i> grid snap — the selection pivot lands on
/// world-grid multiples of <see cref="GridSize"/> on every axis it moved along, not
/// a mere quantization of the drag delta (the RED bug this fixes).</item>
/// <item><b>Rotate</b> quantizes to the rotate-by increment (<see cref="RotationStepDegrees"/>).</item>
/// <item><b>Scale</b> steps by <see cref="ScaleStep"/> (5% default).</item>
/// </list>
/// Holding Alt during a drag temporarily inverts the active state (standard modern
/// behaviour). Keyboard M/R+arrow nudges are already increment-based (RED parity)
/// and do NOT consult this policy.
/// </summary>
public sealed class SnapPolicy
{
    /// <summary>Global magnet toggle: when true, mouse drags snap.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which snap targets are active (B1 split-button): grid + geometry kinds.</summary>
    public SnapKinds Kinds { get; set; } = SnapKinds.Default;

    /// <summary>The current level's snap-to-geometry index, or null (grid-only). Set per build/edit.</summary>
    public GeometrySnapIndex? GeometryIndex { get; set; }

    /// <summary>The last geometry target a snapped move locked onto, for the highlight marker (null = grid/none).</summary>
    public SnapResult? LastGeometrySnap { get; private set; }

    /// <summary>World grid size (metres) that move drags snap the pivot onto.</summary>
    public float GridSize { get; set; } = 1f;

    /// <summary>Rotation increment (degrees) that rotate drags quantize to.</summary>
    public float RotationStepDegrees { get; set; } = 15f;

    /// <summary>Scale increment (fraction, e.g. 0.05 = 5%) that scale drags step by.</summary>
    public float ScaleStep { get; set; } = 0.05f;

    /// <summary>Snapping active for this drag given the temporary Alt-invert.</summary>
    public bool IsActive(bool invert) => Enabled ^ invert;

    /// <summary>
    /// Where the pivot should land given its original position and the accumulated
    /// raw drag delta. When active, each axis the drag actually moved along snaps to
    /// a multiple of <see cref="GridSize"/>; axes with no motion are left untouched
    /// (so an axis-constrained drag never grid-jumps the other two). When inactive
    /// the move is free/continuous.
    /// </summary>
    public Vec3 MovedPivot(Vec3 originalPivot, Vec3 accumulatedDelta, bool invert)
    {
        Vec3 target = originalPivot.Add(accumulatedDelta);
        if (!IsActive(invert))
        {
            return target;
        }

        const float eps = 1e-5f;
        float x = MathF.Abs(accumulatedDelta.X) > eps ? TransformMath.Snap(target.X, GridSize) : originalPivot.X;
        float y = MathF.Abs(accumulatedDelta.Y) > eps ? TransformMath.Snap(target.Y, GridSize) : originalPivot.Y;
        float z = MathF.Abs(accumulatedDelta.Z) > eps ? TransformMath.Snap(target.Z, GridSize) : originalPivot.Z;
        return new Vec3(x, y, z);
    }

    /// <summary>
    /// Like <see cref="MovedPivot"/> but with snap-to-geometry taking priority over the
    /// grid (B1): when the magnet is active and a geometry target (vertex &gt; midpoint &gt;
    /// face) is within <paramref name="worldRadius"/> of the moved pivot, the pivot locks
    /// exactly onto it (recorded in <see cref="LastGeometrySnap"/> for the highlight);
    /// otherwise the grid snap applies when the Grid kind is enabled. Falls back to a free
    /// move when nothing snaps.
    /// </summary>
    public Vec3 MovedPivotSnapped(Vec3 originalPivot, Vec3 accumulatedDelta, bool invert, float worldRadius)
    {
        LastGeometrySnap = null;
        Vec3 target = originalPivot.Add(accumulatedDelta);
        if (!IsActive(invert))
        {
            return target;
        }

        if (GeometryIndex is { } idx && (Kinds & SnapKinds.Geometry) != 0 &&
            idx.Query(target, worldRadius, Kinds) is SnapResult hit)
        {
            LastGeometrySnap = hit;
            return hit.Position;
        }

        return (Kinds & SnapKinds.Grid) != 0 ? MovedPivot(originalPivot, accumulatedDelta, invert) : target;
    }

    /// <summary>
    /// Snaps a free world point (Draw Brush stage point, object placement) to the nearest
    /// geometry target within <paramref name="worldRadius"/>, honoring the active kinds and
    /// priority. Returns the snapped point (and sets <see cref="LastGeometrySnap"/>) or the
    /// input point unchanged when nothing is in range / the magnet is off.
    /// </summary>
    public Vec3 SnapWorldPoint(Vec3 point, float worldRadius, bool invert = false)
    {
        LastGeometrySnap = null;
        if (!IsActive(invert) || GeometryIndex is not { } idx || (Kinds & SnapKinds.Geometry) == 0)
        {
            return point;
        }

        if (idx.Query(point, worldRadius, Kinds) is SnapResult hit)
        {
            LastGeometrySnap = hit;
            return hit.Position;
        }

        return point;
    }

    /// <summary>Clears the recorded geometry snap (call when a drag ends / marker should vanish).</summary>
    public void ClearGeometrySnap() => LastGeometrySnap = null;

    /// <summary>Quantizes a rotation angle (degrees) to the rotate-by increment when active.</summary>
    public float RotationDegrees(float degrees, bool invert) =>
        IsActive(invert) ? Step(degrees, RotationStepDegrees) : degrees;

    /// <summary>Quantizes a scale factor to <see cref="ScaleStep"/> when active.</summary>
    public float ScaleFactor(float factor, bool invert) =>
        IsActive(invert) ? Step(factor, ScaleStep) : factor;

    private static float Step(float v, float step) => step > 1e-6f ? MathF.Round(v / step) * step : v;
}
