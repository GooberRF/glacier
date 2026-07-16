using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// A small CPU ray-vs-geometry intersector for the draw-brush tool's stage-1 work
/// plane pick: fans every face of a <see cref="Geometry"/> into triangles and
/// returns the nearest hit point plus the hit face's plane normal. Pure and
/// deterministic — no GPU, spatial structure or tolerance tuning; the compiled
/// level geometry it runs against is small enough for a linear sweep per click.
/// </summary>
public static class GeometryRaycast
{
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Intersects a world-space ray with the geometry (assumed to be in world space,
    /// as compiled static geometry is). Returns the nearest hit point and the face
    /// normal oriented to oppose the ray (the visible side — a work plane always
    /// faces the viewer), or null when nothing is hit.
    /// </summary>
    public static (Vec3 Point, Vec3 Normal)? Raycast(Geometry g, Vec3 rayOrigin, Vec3 rayDir)
    {
        ArgumentNullException.ThrowIfNull(g);
        float bestT = float.PositiveInfinity;
        Vec3 bestNormal = default;

        foreach (Face f in g.Faces)
        {
            int n = f.Vertices.Count;
            if (n < 3)
            {
                continue;
            }

            // Triangle fan around the first corner.
            Vec3 v0 = VertexAt(g, f.Vertices[0].Index);
            for (int i = 1; i < n - 1; i++)
            {
                Vec3 v1 = VertexAt(g, f.Vertices[i].Index);
                Vec3 v2 = VertexAt(g, f.Vertices[i + 1].Index);
                if (RayTriangle(rayOrigin, rayDir, v0, v1, v2, out float t) && t < bestT)
                {
                    bestT = t;
                    bestNormal = FaceNormal(g, f);
                }
            }
        }

        if (float.IsPositiveInfinity(bestT))
        {
            return null;
        }

        // Draw on the side the viewer sees: flip a back-facing normal toward the ray origin.
        if (bestNormal.Dot(rayDir) > 0f)
        {
            bestNormal = bestNormal.Negate();
        }

        return (rayOrigin.Add(rayDir.Scale(bestT)), bestNormal);
    }

    /// <summary>The face's outward normal — the stored plane when valid, else recomputed from the polygon.</summary>
    private static Vec3 FaceNormal(Geometry g, Face f)
    {
        Vec3 n = f.Plane.Normal;
        if (n.LengthSquared() > Epsilon)
        {
            return n.Normalized();
        }

        List<Vec3> poly = GeometryUtil.Corners(g, f);
        return GeometryUtil.Normal(poly);
    }

    /// <summary>Möller–Trumbore ray/triangle intersection (both winding orders, front hits only: t &gt; 0).</summary>
    private static bool RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c, out float t)
    {
        t = 0f;
        Vec3 e1 = b.Sub(a);
        Vec3 e2 = c.Sub(a);
        Vec3 p = d.Cross(e2);
        float det = e1.Dot(p);
        if (MathF.Abs(det) < Epsilon)
        {
            return false; // ray parallel to the triangle plane
        }

        float inv = 1f / det;
        Vec3 s = o.Sub(a);
        float u = s.Dot(p) * inv;
        if (u < -Epsilon || u > 1f + Epsilon)
        {
            return false;
        }

        Vec3 q = s.Cross(e1);
        float v = d.Dot(q) * inv;
        if (v < -Epsilon || u + v > 1f + Epsilon)
        {
            return false;
        }

        t = e2.Dot(q) * inv;
        return t > Epsilon;
    }

    private static Vec3 VertexAt(Geometry g, int index) =>
        index >= 0 && index < g.Vertices.Count ? g.Vertices[index] : default;
}
