using System;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>One pickable part of the transform manipulator.</summary>
public enum GizmoHandle
{
    None = 0,

    // Axis translate (arrows).
    MoveX,
    MoveY,
    MoveZ,

    // Plane translate (quads) — named by the two axes they span.
    PlaneYZ,
    PlaneZX,
    PlaneXY,

    // Rotate rings.
    RotateX,
    RotateY,
    RotateZ,

    // Axis scale (boxes) + uniform centre.
    ScaleX,
    ScaleY,
    ScaleZ,
    ScaleUniform,
}

/// <summary>The three manipulator tools.</summary>
public enum GizmoTool
{
    Move,
    Rotate,
    Scale,
}

/// <summary>
/// Pure world-space manipulation math for the transform gizmo — the correct
/// ray/axis/plane/ring/scale computations that replace the old pointer-delta
/// heuristics. Everything here is deterministic and unit-tested; the App feeds it
/// world-space rays unprojected from the cursor and applies the results through the
/// existing <see cref="BrushTransform"/> / <see cref="SnapPolicy"/> pipeline.
/// </summary>
public static class GizmoMath
{
    /// <summary>
    /// The signed parameter along an axis line (through <paramref name="axisPoint"/>,
    /// unit <paramref name="axisDir"/>) of the point closest to the mouse ray. This is
    /// the correct basis for axis translate/scale — the world-space closest-point
    /// between the ray and the axis, NOT a screen-delta projection. Falls back to
    /// projecting the ray origin when the ray is parallel to the axis.
    /// </summary>
    public static float ClosestAxisParam(Vec3 axisPoint, Vec3 axisDir, Vec3 rayOrigin, Vec3 rayDir)
    {
        Vec3 a = axisDir.Normalized();
        Vec3 d = rayDir;
        Vec3 w0 = rayOrigin.Sub(axisPoint);
        float aa = d.Dot(d);
        float b = d.Dot(a);
        float dd = d.Dot(w0);
        float e = a.Dot(w0);
        float denom = aa - (b * b); // (d·d)(a·a) - (d·a)^2 with a·a = 1
        if (MathF.Abs(denom) < 1e-9f)
        {
            return e; // ray parallel to the axis: project the ray origin onto it
        }

        return ((aa * e) - (b * dd)) / denom;
    }

    /// <summary>
    /// Intersects the mouse ray with the plane through <paramref name="planePoint"/>
    /// with normal <paramref name="planeNormal"/>. False when the ray is parallel.
    /// </summary>
    public static bool RayPlane(Vec3 planePoint, Vec3 planeNormal, Vec3 rayOrigin, Vec3 rayDir, out Vec3 hit)
    {
        float denom = planeNormal.Dot(rayDir);
        if (MathF.Abs(denom) < 1e-9f)
        {
            hit = default;
            return false;
        }

        float t = planeNormal.Dot(planePoint.Sub(rayOrigin)) / denom;
        if (float.IsNaN(t) || float.IsInfinity(t))
        {
            hit = default;
            return false;
        }

        hit = rayOrigin.Add(rayDir.Scale(t));
        return true;
    }

    /// <summary>
    /// The in-plane unit direction from <paramref name="pivot"/> to the ray's hit on
    /// the ring plane (normal <paramref name="axis"/>). Used to seed and track a
    /// rotate-ring drag. False when the ray misses or hits the pivot.
    /// </summary>
    public static bool RingPickDir(Vec3 pivot, Vec3 axis, Vec3 rayOrigin, Vec3 rayDir, out Vec3 dir)
    {
        if (!RayPlane(pivot, axis, rayOrigin, rayDir, out Vec3 hit))
        {
            dir = default;
            return false;
        }

        Vec3 n = axis.Normalized();
        Vec3 v = hit.Sub(pivot);
        Vec3 inPlane = v.Sub(n.Scale(v.Dot(n)));
        if (inPlane.LengthSquared() < 1e-10f)
        {
            dir = default;
            return false;
        }

        dir = inPlane.Normalized();
        return true;
    }

    /// <summary>
    /// The signed angle (radians, −π…π) sweeping <paramref name="prevDir"/> to
    /// <paramref name="currDir"/> about <paramref name="axis"/>. Accumulating this
    /// per frame gives a continuous, wrap-safe full-360°-and-beyond rotation.
    /// </summary>
    public static float SignedAngle(Vec3 prevDir, Vec3 currDir, Vec3 axis)
    {
        Vec3 n = axis.Normalized();
        float sin = prevDir.Cross(currDir).Dot(n);
        float cos = prevDir.Dot(currDir);
        return MathF.Atan2(sin, cos);
    }

    /// <summary>Per-axis scale factor from the drag-start and current axis parameters (guarded).</summary>
    public static float AxisScaleFactor(float startParam, float currentParam) =>
        MathF.Abs(startParam) < 1e-6f ? 1f : currentParam / startParam;

    /// <summary>Uniform scale factor from the start/current radial screen distance (guarded).</summary>
    public static float RadialScaleFactor(float startRadius, float currentRadius) =>
        startRadius < 1e-4f ? 1f : currentRadius / startRadius;

    // ---- Handle classification (pure, shared by picking + rendering) ----------

    /// <summary>The tool a handle belongs to (drives which drag math runs on press).</summary>
    public static GizmoTool ToolOf(GizmoHandle handle) => handle switch
    {
        GizmoHandle.RotateX or GizmoHandle.RotateY or GizmoHandle.RotateZ => GizmoTool.Rotate,
        GizmoHandle.ScaleX or GizmoHandle.ScaleY or GizmoHandle.ScaleZ or GizmoHandle.ScaleUniform => GizmoTool.Scale,
        _ => GizmoTool.Move,
    };

    /// <summary>The single axis (0=X,1=Y,2=Z) a handle acts on, or −1 (planes/uniform have none).</summary>
    public static int AxisOf(GizmoHandle handle) => handle switch
    {
        GizmoHandle.MoveX or GizmoHandle.RotateX or GizmoHandle.ScaleX => 0,
        GizmoHandle.MoveY or GizmoHandle.RotateY or GizmoHandle.ScaleY => 1,
        GizmoHandle.MoveZ or GizmoHandle.RotateZ or GizmoHandle.ScaleZ => 2,
        _ => -1,
    };

    /// <summary>The normal axis (0/1/2) of a plane-translate handle, or −1 otherwise.</summary>
    public static int PlaneNormalAxis(GizmoHandle handle) => handle switch
    {
        GizmoHandle.PlaneYZ => 0, // spans Y,Z → normal X
        GizmoHandle.PlaneZX => 1, // spans Z,X → normal Y
        GizmoHandle.PlaneXY => 2, // spans X,Y → normal Z
        _ => -1,
    };

    /// <summary>True for a plane-translate handle.</summary>
    public static bool IsPlane(GizmoHandle handle) =>
        handle is GizmoHandle.PlaneYZ or GizmoHandle.PlaneZX or GizmoHandle.PlaneXY;
}
