using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Edge-mode operators. Each mutates a brush <see cref="Geometry"/> given the
/// selected edge(s) and returns an <see cref="OpResult"/>, guarding against degenerate output
/// so the model is never corrupted. Pure; the App wraps them in undo commands. Edges are the
/// canonical vertex-pool pairs from <see cref="EdgeTopology"/>.
/// </summary>
public static class EdgeOps
{
    private const float MinDistance = 1e-4f;

    /// <summary>Translates both endpoint vertices of every selected edge by <paramref name="delta"/>.</summary>
    public static OpResult Move(Geometry g, IReadOnlyCollection<BrushEdge> edges, Vec3 delta)
    {
        if (edges.Count == 0)
        {
            return OpResult.Fail("Select an edge to move.");
        }

        if (Length(delta) < MinDistance)
        {
            return OpResult.Ok("Move edges");
        }

        var verts = new HashSet<int>();
        foreach (BrushEdge e in edges)
        {
            verts.Add(e.V0);
            verts.Add(e.V1);
        }

        foreach (int v in verts)
        {
            if (v >= 0 && v < g.Vertices.Count)
            {
                g.Vertices[v] = g.Vertices[v].Add(delta);
            }
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Move edges") : OpResult.Fail("Move produced degenerate geometry.");
    }

    /// <summary>
    /// Rotates both endpoints of every selected edge about <paramref name="pivot"/> by
    /// <paramref name="rot"/>. Pivot, rotation and vertices are all in the geometry's local
    /// space (the App converts the world gizmo pose into each brush's frame before calling).
    /// </summary>
    public static OpResult Rotate(Geometry g, IReadOnlyCollection<BrushEdge> edges, Mat3 rot, Vec3 pivot)
    {
        if (edges.Count == 0)
        {
            return OpResult.Fail("Select an edge to rotate.");
        }

        foreach (int v in UniqueEndpoints(g, edges))
        {
            g.Vertices[v] = pivot.Add(rot.Transform(g.Vertices[v].Sub(pivot)));
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Rotate edges") : OpResult.Fail("Rotate produced degenerate geometry.");
    }

    /// <summary>Uniformly scales both endpoints of every selected edge about <paramref name="pivot"/>.</summary>
    public static OpResult Scale(Geometry g, IReadOnlyCollection<BrushEdge> edges, Vec3 pivot, float factor)
    {
        if (edges.Count == 0)
        {
            return OpResult.Fail("Select an edge to scale.");
        }

        foreach (int v in UniqueEndpoints(g, edges))
        {
            g.Vertices[v] = pivot.Add(g.Vertices[v].Sub(pivot).Scale(factor));
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Scale edges") : OpResult.Fail("Scale produced degenerate geometry.");
    }

    /// <summary>Scales both endpoints of every selected edge along <paramref name="axis"/> about
    /// <paramref name="pivot"/> (non-uniform, faces stay planar).</summary>
    public static OpResult ScaleAxis(Geometry g, IReadOnlyCollection<BrushEdge> edges, Vec3 pivot, Vec3 axis, float factor)
    {
        if (edges.Count == 0)
        {
            return OpResult.Fail("Select an edge to scale.");
        }

        Vec3 a = Normalize(axis);
        if (Length(a) < 1e-6f)
        {
            return OpResult.Ok("Scale edges");
        }

        foreach (int v in UniqueEndpoints(g, edges))
        {
            float c = g.Vertices[v].Sub(pivot).Dot(a);
            g.Vertices[v] = g.Vertices[v].Add(a.Scale(c * (factor - 1f)));
        }

        GeometryUtil.RecomputeAllPlanes(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Scale edges") : OpResult.Fail("Scale produced degenerate geometry.");
    }

    private static IEnumerable<int> UniqueEndpoints(Geometry g, IReadOnlyCollection<BrushEdge> edges)
    {
        var verts = new HashSet<int>();
        foreach (BrushEdge e in edges)
        {
            verts.Add(e.V0);
            verts.Add(e.V1);
        }

        return verts.Where(v => v >= 0 && v < g.Vertices.Count);
    }

    /// <summary>Collapses an edge, merging its two endpoints to their midpoint.</summary>
    public static OpResult Collapse(Geometry g, BrushEdge edge)
    {
        if (!InRange(g, edge))
        {
            return OpResult.Fail("Select an edge to collapse.");
        }

        Vec3 mid = g.Vertices[edge.V0].Add(g.Vertices[edge.V1]).Scale(0.5f);
        g.Vertices[edge.V0] = mid;
        g.Vertices[edge.V1] = mid;

        GeometryUtil.WeldVertices(g);       // merges the now-coincident endpoints
        GeometryUtil.CleanupFaces(g);       // drops the faces that degenerated to < 3 corners
        GeometryUtil.CompactUnusedVertices(g);
        GeometryUtil.RecomputeAllPlanes(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Collapse edge") : OpResult.Fail("Collapse produced degenerate geometry.");
    }

    /// <summary>
    /// Bevels an interior (2-face) edge into two parallel edges joined by a chamfer face: the
    /// shared endpoints are offset inward along each adjacent face by <paramref name="distance"/>,
    /// and a new quad connects the two resulting parallel edges.
    /// </summary>
    public static OpResult Bevel(Geometry g, BrushEdge edge, float distance)
    {
        if (!InRange(g, edge))
        {
            return OpResult.Fail("Select an edge to bevel.");
        }

        if (MathF.Abs(distance) < MinDistance)
        {
            return OpResult.Fail("Bevel distance is zero.");
        }

        Dictionary<BrushEdge, List<(int Face, int Corner)>> adj = EdgeTopology.Adjacency(g);
        if (!adj.TryGetValue(edge, out List<(int Face, int Corner)>? faces) || faces.Count != 2)
        {
            return OpResult.Fail("Edge Bevel needs an edge shared by exactly two faces.");
        }

        int f0 = faces[0].Face;
        int f1 = faces[1].Face;
        int a = edge.V0, b = edge.V1;

        int a0 = GeometryUtil.AddVertex(g, g.Vertices[a].Add(InwardDir(g, f0, edge, a).Scale(distance)));
        int b0 = GeometryUtil.AddVertex(g, g.Vertices[b].Add(InwardDir(g, f0, edge, b).Scale(distance)));
        int a1 = GeometryUtil.AddVertex(g, g.Vertices[a].Add(InwardDir(g, f1, edge, a).Scale(distance)));
        int b1 = GeometryUtil.AddVertex(g, g.Vertices[b].Add(InwardDir(g, f1, edge, b).Scale(distance)));

        // Non-degenerate only if the two faces pull the corners in genuinely different directions.
        if (a0 == a1 || b0 == b1)
        {
            return OpResult.Fail("Edge Bevel needs two non-coplanar faces.");
        }

        ReplaceCorner(g.Faces[f0], a, a0);
        ReplaceCorner(g.Faces[f0], b, b0);
        ReplaceCorner(g.Faces[f1], a, a1);
        ReplaceCorner(g.Faces[f1], b, b1);

        var chamfer = NewFaceLike(g.Faces[f0]);
        chamfer.FaceId = GeometryUtil.NextFaceId(g);
        AddCorners(chamfer, a0, b0, b1, a1);
        g.Faces.Add(chamfer);

        GeometryUtil.CompactUnusedVertices(g);
        GeometryUtil.RecomputeAllPlanes(g);
        GeometryUtil.AssignAllPlanarUv(g);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Bevel edge") : OpResult.Fail("Bevel produced degenerate geometry.");
    }

    /// <summary>
    /// Extrudes an open (boundary, single-face) edge outward in its face plane, creating a new
    /// quad — for Face-shaped brushes whose every edge is a boundary.
    /// </summary>
    public static OpResult Extrude(Geometry g, BrushEdge edge, float distance)
    {
        if (!InRange(g, edge))
        {
            return OpResult.Fail("Select an edge to extrude.");
        }

        if (MathF.Abs(distance) < MinDistance)
        {
            return OpResult.Fail("Extrude distance is zero.");
        }

        Dictionary<BrushEdge, List<(int Face, int Corner)>> adj = EdgeTopology.Adjacency(g);
        if (!adj.TryGetValue(edge, out List<(int Face, int Corner)>? faces) || faces.Count != 1)
        {
            return OpResult.Fail("Edge Extrude needs an open (boundary) edge.");
        }

        Face f = g.Faces[faces[0].Face];
        Vec3 na = g.Vertices[edge.V0];
        Vec3 nb = g.Vertices[edge.V1];
        Vec3 edgeDir = Normalize(nb.Sub(na));
        Vec3 outward = Normalize(Cross(f.Plane.Normal, edgeDir));

        // Point the extrusion away from the face interior.
        Vec3 mid = na.Add(nb).Scale(0.5f);
        Vec3 toCentroid = GeometryUtil.Centroid(GeometryUtil.Corners(g, f)).Sub(mid);
        if (outward.Dot(toCentroid) > 0f)
        {
            outward = outward.Scale(-1f);
        }

        int a2 = GeometryUtil.AddVertex(g, na.Add(outward.Scale(distance)));
        int b2 = GeometryUtil.AddVertex(g, nb.Add(outward.Scale(distance)));

        var quad = NewFaceLike(f);
        quad.FaceId = GeometryUtil.NextFaceId(g);
        AddCorners(quad, edge.V0, edge.V1, b2, a2);
        g.Faces.Add(quad);

        GeometryUtil.RecomputeAllPlanes(g);
        GeometryUtil.AssignPlanarUv(g, quad);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Extrude edge") : OpResult.Fail("Extrude produced degenerate geometry.");
    }

    // ---- helpers --------------------------------------------------------------

    private static bool InRange(Geometry g, BrushEdge e) =>
        !e.Degenerate && e.V0 >= 0 && e.V1 >= 0 && e.V0 < g.Vertices.Count && e.V1 < g.Vertices.Count;

    /// <summary>Unit direction from <paramref name="v"/> toward its non-edge neighbor within a face.</summary>
    private static Vec3 InwardDir(Geometry g, int faceIndex, BrushEdge edge, int v)
    {
        List<FaceVertex> vs = g.Faces[faceIndex].Vertices;
        int n = vs.Count;
        for (int i = 0; i < n; i++)
        {
            if (vs[i].Index != v)
            {
                continue;
            }

            int prev = vs[(i - 1 + n) % n].Index;
            int next = vs[(i + 1) % n].Index;
            int neighbor = prev == edge.Other(v) ? next : prev;
            return Normalize(g.Vertices[neighbor].Sub(g.Vertices[v]));
        }

        return Vec3.Zero;
    }

    private static void ReplaceCorner(Face f, int oldIndex, int newIndex)
    {
        foreach (FaceVertex fv in f.Vertices)
        {
            if (fv.Index == oldIndex)
            {
                fv.Index = newIndex;
            }
        }
    }

    private static void AddCorners(Face face, params int[] indices)
    {
        foreach (int i in indices)
        {
            face.Vertices.Add(new FaceVertex { Index = i });
        }
    }

    private static Face NewFaceLike(Face f) =>
        new() { Texture = f.Texture, SurfaceIndex = -1, Flags = f.Flags, SmoothingGroups = f.SmoothingGroups, RoomIndex = -1, FaceId = -1 };

    private static float Length(Vec3 v) => MathF.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));

    private static Vec3 Normalize(Vec3 v)
    {
        float len = Length(v);
        return len < 1e-9f ? Vec3.Zero : v.Scale(1f / len);
    }

    private static Vec3 Cross(Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));
}
