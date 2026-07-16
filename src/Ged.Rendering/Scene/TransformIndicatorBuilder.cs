using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ged.Rendering.Scene;

/// <summary>
/// Pure geometry for the in-viewport transform progress indicators shown during a gizmo
/// drag (item: transform indicators): the MOVE dimension line (drag start → current with
/// end ticks), the ROTATE angle arc swept at the pivot, and the numeric label texts
/// ("5.0 M", "45°", "150%"). The SCALE ghost outline is a recolored snapshot of the
/// selection wireframe captured at drag start (built by the App layer). No GPU / App
/// dependency — fully unit-testable line math, mirroring <see cref="OverlayBuilder"/>.
/// </summary>
public static class TransformIndicatorBuilder
{
    /// <summary>Arc tessellation step in degrees.</summary>
    private const float ArcStepDegrees = 5f;

    /// <summary>
    /// The MOVE indicator: a dimension line from the drag-start pivot to the current
    /// (snapped) position, with a small perpendicular tick cross at each end.
    /// </summary>
    public static IReadOnlyList<LineSegment> MoveLine(Vector3 start, Vector3 end, uint color)
    {
        var lines = new List<LineSegment>();
        Vector3 dir = end - start;
        float len = dir.Length();
        if (len < 1e-5f)
        {
            return lines;
        }

        dir /= len;
        lines.Add(new LineSegment(start, end, color));

        // End ticks: two short perpendicular strokes per end (a small cross ⊥ the line).
        (Vector3 u, Vector3 v) = PerpendicularBasis(dir);
        float tick = MathF.Max(0.08f, MathF.Min(0.35f, len * 0.06f));
        foreach (Vector3 p in new[] { start, end })
        {
            lines.Add(new LineSegment(p - (u * tick), p + (u * tick), color));
            lines.Add(new LineSegment(p - (v * tick), p + (v * tick), color));
        }

        return lines;
    }

    /// <summary>
    /// The ROTATE indicator: an arc at <paramref name="pivot"/> in the plane ⊥
    /// <paramref name="axis"/>, swept from <paramref name="startDir"/> by
    /// <paramref name="sweepDegrees"/> (signed, right-handed about the axis — the same
    /// convention as the gizmo's SignedAngle accumulation), with spokes at both ends.
    /// </summary>
    public static IReadOnlyList<LineSegment> RotationArc(
        Vector3 pivot, Vector3 axis, Vector3 startDir, float sweepDegrees, float radius, uint color)
    {
        var lines = new List<LineSegment>();
        if (MathF.Abs(sweepDegrees) < 1e-3f || radius < 1e-5f ||
            axis.LengthSquared() < 1e-10f || startDir.LengthSquared() < 1e-10f)
        {
            return lines;
        }

        Vector3 k = Vector3.Normalize(axis);
        // Project the start direction into the rotation plane (defensive; the gizmo's ring
        // pick dir is already in-plane).
        Vector3 s = startDir - (k * Vector3.Dot(startDir, k));
        if (s.LengthSquared() < 1e-10f)
        {
            return lines;
        }

        s = Vector3.Normalize(s);

        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweepDegrees) / ArcStepDegrees));
        Vector3 prev = pivot + (s * radius);
        for (int i = 1; i <= steps; i++)
        {
            float a = sweepDegrees * i / steps;
            Vector3 p = pivot + (RotateAround(s, k, a) * radius);
            lines.Add(new LineSegment(prev, p, color));
            prev = p;
        }

        // Spokes from the pivot to the arc's start and end.
        lines.Add(new LineSegment(pivot, pivot + (s * radius), color));
        lines.Add(new LineSegment(pivot, pivot + (RotateAround(s, k, sweepDegrees) * radius), color));
        return lines;
    }

    /// <summary>
    /// Rotates <paramref name="v"/> by <paramref name="degrees"/> around the (normalized)
    /// <paramref name="axis"/>, right-handed (Rodrigues) — matches the sign convention of
    /// the gizmo's SignedAngle, so a positive gizmo sweep draws a positive arc.
    /// </summary>
    public static Vector3 RotateAround(Vector3 v, Vector3 axis, float degrees)
    {
        float rad = degrees * MathF.PI / 180f;
        float c = MathF.Cos(rad);
        float s = MathF.Sin(rad);
        return (v * c) + (Vector3.Cross(axis, v) * s) + (axis * Vector3.Dot(axis, v) * (1f - c));
    }

    /// <summary>Recolors a captured wireframe snapshot (the SCALE original-bounds ghost).</summary>
    public static IReadOnlyList<LineSegment> Recolor(IEnumerable<LineSegment> lines, uint color)
    {
        var result = new List<LineSegment>();
        foreach (LineSegment l in lines)
        {
            result.Add(new LineSegment(l.A, l.B, color));
        }

        return result;
    }

    /// <summary>The MOVE label ("5.0 M" — the label font renders uppercase only).</summary>
    public static string FormatDistance(float meters) => $"{meters:0.0##} M";

    /// <summary>The ROTATE label ("45°").</summary>
    public static string FormatAngle(float degrees) => $"{degrees:0.#}°";

    /// <summary>The SCALE label ("150%").</summary>
    public static string FormatScale(float factor) => $"{factor * 100f:0.#}%";

    /// <summary>An arbitrary orthonormal basis perpendicular to <paramref name="dir"/> (normalized).</summary>
    private static (Vector3 U, Vector3 V) PerpendicularBasis(Vector3 dir)
    {
        Vector3 seed = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(seed, dir));
        Vector3 v = Vector3.Normalize(Vector3.Cross(dir, u));
        return (u, v);
    }
}
