using System;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Pure 2D-gizmo math for the UV Unwrap editor's held-M/R/S manipulator. Converts a pointer
/// drag (expressed in UV space) into the move delta / rotation angle / per-axis scale factors
/// that feed the existing <see cref="UnwrapOps"/> transforms. No UI type is referenced, so the
/// snap rules are unit-testable independently of the window.
/// <para>
/// Snap rule (shared with the toolbar Grid Snap toggle): when grid snap is ON the move gizmo
/// lands the selection's centroid on the nearest grid multiple, and the rotate gizmo snaps the
/// cumulative angle to the nearest Rotate-step multiple; when OFF both are continuous. Scale is
/// always continuous — a scale factor has no grid meaning.
/// </para>
/// </summary>
public static class UvGizmoMath
{
    /// <summary>Which axes a move / scale drag affects.</summary>
    public enum Axis
    {
        /// <summary>Both U and V (free drag / uniform scale).</summary>
        Both,

        /// <summary>U only (the horizontal axis handle).</summary>
        U,

        /// <summary>V only (the vertical axis handle).</summary>
        V,
    }

    /// <summary>
    /// The move delta for a drag from <paramref name="start"/> to <paramref name="current"/>
    /// (both in UV space), constrained to <paramref name="axis"/>. When <paramref name="gridSnap"/>
    /// is on (and <paramref name="step"/> is positive) the delta is chosen so the selection's
    /// <paramref name="centroid"/> lands on the nearest multiple of the grid step on each free
    /// axis — matching the toolbar Grid Snap behaviour applied to the whole island's pivot.
    /// </summary>
    public static (float Du, float Dv) MoveDelta(
        Uv centroid, Uv start, Uv current, Axis axis, bool gridSnap, float step)
    {
        float du = axis == Axis.V ? 0f : current.U - start.U;
        float dv = axis == Axis.U ? 0f : current.V - start.V;
        if (gridSnap && step > 1e-6f)
        {
            if (axis != Axis.V)
            {
                du = SnapToStep(centroid.U + du, step) - centroid.U;
            }

            if (axis != Axis.U)
            {
                dv = SnapToStep(centroid.V + dv, step) - centroid.V;
            }
        }

        return (du, dv);
    }

    /// <summary>
    /// The signed rotation in degrees carrying the ray centroid→<paramref name="from"/> onto the
    /// ray centroid→<paramref name="to"/>, in <see cref="UnwrapOps.Rotate"/>'s sign convention (a
    /// positive value rotates the (U,V) plane so <paramref name="from"/>'s angle increases toward
    /// <paramref name="to"/>). Normalised to (-180, 180].
    /// </summary>
    public static float AngleDegrees(Uv centroid, Uv from, Uv to)
    {
        float a0 = MathF.Atan2(from.V - centroid.V, from.U - centroid.U);
        float a1 = MathF.Atan2(to.V - centroid.V, to.U - centroid.U);
        float deg = (a1 - a0) * 180f / MathF.PI;
        while (deg <= -180f)
        {
            deg += 360f;
        }

        while (deg > 180f)
        {
            deg -= 360f;
        }

        return deg;
    }

    /// <summary>Rounds <paramref name="degrees"/> to the nearest multiple of <paramref name="step"/> (rotate-snap).</summary>
    public static float SnapAngle(float degrees, float step) =>
        step > 1e-6f ? MathF.Round(degrees / step) * step : degrees;

    /// <summary>
    /// The per-axis scale factor for dragging an axis handle: the ratio of the pointer's signed
    /// distance from the centroid now (<paramref name="current"/>) to its distance at drag start
    /// (<paramref name="start"/>) on one component. Guarded to 1 when the start sat on the
    /// centroid (no usable lever arm).
    /// </summary>
    public static float AxisScale(float centroid, float start, float current, float minLever = 1e-4f)
    {
        float lever = start - centroid;
        return MathF.Abs(lever) < minLever ? 1f : (current - centroid) / lever;
    }

    /// <summary>
    /// The uniform scale factor for dragging the centre / corner handle: the ratio of the
    /// pointer's radial distance from the centroid now to its distance at drag start. Guarded to
    /// 1 when the start sat on the centroid.
    /// </summary>
    public static float UniformScale(Uv centroid, Uv start, Uv current, float minLever = 1e-4f)
    {
        float d0 = Distance(centroid, start);
        return d0 < minLever ? 1f : Distance(centroid, current) / d0;
    }

    /// <summary>Rounds a coordinate to the nearest multiple of <paramref name="step"/>.</summary>
    public static float SnapToStep(float value, float step) =>
        step > 1e-6f ? MathF.Round(value / step) * step : value;

    private static float Distance(Uv a, Uv b)
    {
        float du = a.U - b.U;
        float dv = a.V - b.V;
        return MathF.Sqrt((du * du) + (dv * dv));
    }
}
