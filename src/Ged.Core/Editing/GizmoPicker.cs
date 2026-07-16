using System;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The world-space pose of the manipulator at an instant: its pivot, the three
/// (unit) handle axes — world axes in World mode, the selection's basis in Local
/// mode — and the screen-constant handle length in world units.
/// </summary>
public readonly record struct GizmoPose(Vec3 Pivot, Vec3 AxisX, Vec3 AxisY, Vec3 AxisZ, float Length)
{
    public Vec3 Axis(int i) => i == 0 ? AxisX : (i == 1 ? AxisY : AxisZ);
}

/// <summary>
/// Deterministic CPU ray-picking of gizmo handles. Each handle carries its own
/// <see cref="GizmoHandle"/> id (rendered as a <c>PickKind.Gizmo</c> pick id by the
/// App), and picking a world ray unprojected from the cursor returns the nearest
/// handle of the active tool — used both for hover highlighting and to start a drag
/// on exactly that handle. Pure and unit-tested (no GPU readback).
/// </summary>
public static class GizmoPicker
{
    /// <summary>The nearest handle of <paramref name="tool"/> under the ray, or None.</summary>
    public static GizmoHandle Pick(GizmoPose pose, GizmoTool tool, Vec3 rayOrigin, Vec3 rayDir, float worldTol)
    {
        GizmoHandle best = GizmoHandle.None;
        float bestT = float.MaxValue;

        void Consider(GizmoHandle h, bool hit, float t)
        {
            if (hit && t < bestT)
            {
                bestT = t;
                best = h;
            }
        }

        switch (tool)
        {
            case GizmoTool.Move:
                // Plane quads sit near the pivot and win ties over the arrows behind them.
                Consider(GizmoHandle.PlaneYZ, PlaneQuadHit(pose, 0, rayOrigin, rayDir, out float tpyz), tpyz);
                Consider(GizmoHandle.PlaneZX, PlaneQuadHit(pose, 1, rayOrigin, rayDir, out float tpzx), tpzx);
                Consider(GizmoHandle.PlaneXY, PlaneQuadHit(pose, 2, rayOrigin, rayDir, out float tpxy), tpxy);
                for (int a = 0; a < 3; a++)
                {
                    Vec3 tip = pose.Pivot.Add(pose.Axis(a).Scale(pose.Length));
                    Consider(MoveHandle(a), SegmentHit(pose.Pivot, tip, rayOrigin, rayDir, worldTol, out float ta), ta);
                }

                break;

            case GizmoTool.Rotate:
                for (int a = 0; a < 3; a++)
                {
                    Consider(RotateHandle(a), RingHit(pose.Pivot, pose.Axis(a), pose.Length, rayOrigin, rayDir, worldTol, out float tr), tr);
                }

                break;

            case GizmoTool.Scale:
                Consider(GizmoHandle.ScaleUniform, PointHit(pose.Pivot, rayOrigin, rayDir, worldTol * 1.5f, out float tu), tu);
                for (int a = 0; a < 3; a++)
                {
                    Vec3 tip = pose.Pivot.Add(pose.Axis(a).Scale(pose.Length));
                    Consider(ScaleHandle(a), PointHit(tip, rayOrigin, rayDir, worldTol * 1.3f, out float ts), ts);
                }

                break;
        }

        return best;
    }

    // ---- Handle-id maps -------------------------------------------------------

    private static GizmoHandle MoveHandle(int axis) =>
        axis == 0 ? GizmoHandle.MoveX : (axis == 1 ? GizmoHandle.MoveY : GizmoHandle.MoveZ);

    private static GizmoHandle RotateHandle(int axis) =>
        axis == 0 ? GizmoHandle.RotateX : (axis == 1 ? GizmoHandle.RotateY : GizmoHandle.RotateZ);

    private static GizmoHandle ScaleHandle(int axis) =>
        axis == 0 ? GizmoHandle.ScaleX : (axis == 1 ? GizmoHandle.ScaleY : GizmoHandle.ScaleZ);

    // ---- Primitive ray tests --------------------------------------------------

    /// <summary>Closest approach of the ray to a point; hit within <paramref name="tol"/>. <paramref name="t"/> = ray param.</summary>
    internal static bool PointHit(Vec3 p, Vec3 ro, Vec3 rd, float tol, out float t)
    {
        float dd = rd.Dot(rd);
        t = dd < 1e-12f ? 0f : p.Sub(ro).Dot(rd) / dd;
        Vec3 closest = ro.Add(rd.Scale(t));
        return p.Sub(closest).Length() <= tol;
    }

    /// <summary>Closest approach of the ray to a segment [a,b]; hit within <paramref name="tol"/>.</summary>
    internal static bool SegmentHit(Vec3 a, Vec3 b, Vec3 ro, Vec3 rd, float tol, out float t)
    {
        Vec3 u = b.Sub(a);
        float uu = u.Dot(u);
        if (uu < 1e-12f)
        {
            return PointHit(a, ro, rd, tol, out t);
        }

        // Closest points between the infinite ray (ro + t·rd) and the segment line.
        // With w0 = a − ro: segment param s = (b1·e − rr·d) / denom.
        float rr = rd.Dot(rd);
        Vec3 w0 = a.Sub(ro);
        float b1 = u.Dot(rd);
        float d = u.Dot(w0);
        float e = rd.Dot(w0);
        float denom = (uu * rr) - (b1 * b1);
        float sc = MathF.Abs(denom) < 1e-9f ? 0f : ((b1 * e) - (rr * d)) / denom;
        sc = Math.Clamp(sc, 0f, 1f);

        Vec3 ps = a.Add(u.Scale(sc));
        t = ps.Sub(ro).Dot(rd) / rr;
        Vec3 pr = ro.Add(rd.Scale(t));
        return ps.Sub(pr).Length() <= tol;
    }

    /// <summary>Ray vs a rotate ring (circle of <paramref name="radius"/> in the plane normal to <paramref name="axis"/>).</summary>
    internal static bool RingHit(Vec3 center, Vec3 axis, float radius, Vec3 ro, Vec3 rd, float tol, out float t)
    {
        t = float.MaxValue;
        if (!GizmoMath.RayPlane(center, axis, ro, rd, out Vec3 hit))
        {
            return false;
        }

        float dist = hit.Sub(center).Length();
        t = hit.Sub(ro).Dot(rd) / rd.Dot(rd);
        return MathF.Abs(dist - radius) <= tol && t > 0f;
    }

    /// <summary>Ray vs a plane-translate quad (spans the two axes other than <paramref name="normalAxis"/>).</summary>
    internal static bool PlaneQuadHit(GizmoPose pose, int normalAxis, Vec3 ro, Vec3 rd, out float t)
    {
        t = float.MaxValue;
        Vec3 n = pose.Axis(normalAxis);
        if (!GizmoMath.RayPlane(pose.Pivot, n, ro, rd, out Vec3 hit))
        {
            return false;
        }

        int a1 = (normalAxis + 1) % 3;
        int a2 = (normalAxis + 2) % 3;
        Vec3 rel = hit.Sub(pose.Pivot);
        float c1 = rel.Dot(pose.Axis(a1));
        float c2 = rel.Dot(pose.Axis(a2));
        float inner = pose.Length * 0.18f;
        float outer = pose.Length * 0.45f;
        t = hit.Sub(ro).Dot(rd) / rd.Dot(rd);
        return c1 >= inner && c1 <= outer && c2 >= inner && c2 <= outer && t > 0f;
    }
}
