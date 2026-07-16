using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Vertex-mode operators over a brush <see cref="Geometry"/> and a set of selected
/// pool indices: Weld, Collapse, Delete, Bridge, plus Align/Jitter/Stretch/Bend/
/// Twist/snap (delegated to <see cref="Deformers"/>). Pure, guarded against
/// degenerate results, and wrapped in undo commands by the App.
/// </summary>
public static class VertexOps
{
    /// <summary>Welds the selected vertices onto the last-selected one, cleaning pinched faces.</summary>
    public static OpResult Weld(Geometry g, IReadOnlyList<int> indices)
    {
        if (indices.Count < 2)
        {
            return OpResult.Fail("Select at least two vertices to weld.");
        }

        if (indices.Any(i => i < 0 || i >= g.Vertices.Count))
        {
            return OpResult.Fail("A selected vertex is out of range.");
        }

        Vec3 target = g.Vertices[indices[^1]];
        MergeTo(g, indices, target);
        return Finish(g, "Weld");
    }

    /// <summary>Collapses the selected vertices to their shared centroid.</summary>
    public static OpResult Collapse(Geometry g, IReadOnlyList<int> indices)
    {
        if (indices.Count < 2)
        {
            return OpResult.Fail("Select at least two vertices to collapse.");
        }

        Vec3 centre = GeometryUtil.Centroid(indices.Select(i => g.Vertices[i]).ToList());
        MergeTo(g, indices, centre);
        return Finish(g, "Collapse");
    }

    /// <summary>[ALPINE] Deletes the selected vertices, dropping faces that fall below a triangle.</summary>
    public static OpResult Delete(Geometry g, IReadOnlyCollection<int> indices)
    {
        if (indices.Count == 0)
        {
            return OpResult.Fail("Select vertices to delete.");
        }

        var set = new HashSet<int>(indices);
        var kept = new List<Face>();
        foreach (Face f in g.Faces)
        {
            var verts = f.Vertices.Where(v => !set.Contains(v.Index)).ToList();
            if (verts.Count >= 3)
            {
                f.Vertices = verts;
                kept.Add(f);
            }
        }

        if (kept.Count == 0)
        {
            return OpResult.Fail("Deleting those vertices would remove the whole brush.");
        }

        g.Faces = kept;
        GeometryUtil.CompactUnusedVertices(g);
        GeometryUtil.RecomputeAllPlanes(g);
        return OpResult.Ok("Delete vertices");
    }

    /// <summary>
    /// [ALPINE] Bridges an arbitrary number (3+) of selected vertices into one new face,
    /// wound around their centroid in the best-fit plane and oriented outward to match
    /// the adjacent faces. Mirrors Alpine editor_patch/geometry.cpp:977-1119; the
    /// neighbour-normal orientation vote is <c>:1078-1106</c>. Stock RED capped this at
    /// three or four vertices — the cap is lifted here.
    /// </summary>
    public static OpResult Bridge(Geometry g, IReadOnlyList<int> indices)
    {
        if (indices.Count < 3)
        {
            return OpResult.Fail("Bridge needs at least three vertices.");
        }

        if (indices.Any(i => i < 0 || i >= g.Vertices.Count))
        {
            return OpResult.Fail("A selected vertex is out of range.");
        }

        // Order the ring around its centroid in the best-fit plane.
        var pts = indices.Select(i => (Index: i, Pos: g.Vertices[i])).ToList();
        Vec3 c = GeometryUtil.Centroid(pts.Select(p => p.Pos).ToList());
        Vec3 normal = GeometryUtil.Normal(pts.Select(p => p.Pos).ToList());
        if (normal.LengthSquared() < 1e-6f)
        {
            return OpResult.Fail("The bridge vertices are collinear.");
        }

        // Orient outward: if every existing face touching a bridge vertex faces the
        // opposite way, flip the winding normal so the new face matches its neighbours.
        normal = OrientToNeighbors(g, indices, normal);

        Vec3 u = MathF.Abs(normal.X) < 0.9f ? normal.Cross(new Vec3(1, 0, 0)).Normalized() : normal.Cross(new Vec3(0, 1, 0)).Normalized();
        Vec3 w = normal.Cross(u);
        pts.Sort((p, q) =>
            MathF.Atan2(p.Pos.Sub(c).Dot(w), p.Pos.Sub(c).Dot(u))
                .CompareTo(MathF.Atan2(q.Pos.Sub(c).Dot(w), q.Pos.Sub(c).Dot(u))));

        var face = new Face { Texture = 0, SurfaceIndex = -1, RoomIndex = -1, FaceId = GeometryUtil.NextFaceId(g) };
        foreach (var p in pts)
        {
            face.Vertices.Add(new FaceVertex { Index = p.Index });
        }

        g.Faces.Add(face);
        GeometryUtil.RecomputePlane(g, face);
        GeometryUtil.AssignPlanarUv(g, face);
        return GeometryUtil.Validate(g) ? OpResult.Ok("Bridge") : OpResult.Fail("Bridge produced a degenerate face.");
    }

    /// <summary>Snaps the selected vertices to the grid (Ctrl+G in vertex mode).</summary>
    public static OpResult SnapToGrid(Geometry g, IReadOnlyCollection<int> indices, float grid)
    {
        Deformers.SnapToGrid(g, grid, indices);
        return Finish(g, "Snap to grid");
    }

    /// <summary>Aligns the selected vertices on an axis (0=X, 1=Y, 2=Z).</summary>
    public static OpResult Align(Geometry g, IReadOnlyCollection<int> indices, int axis)
    {
        if (indices.Count < 2)
        {
            return OpResult.Fail("Select at least two vertices to align.");
        }

        Deformers.Align(g, axis, indices);
        return Finish(g, "Align");
    }

    /// <summary>
    /// Alpine's neighbour-normal orientation (geometry.cpp:1078-1106): tallies the sign of
    /// the dot product between the bridge normal and the plane normal of every existing face
    /// that shares one of the bridge vertices. If they all disagree (neg &gt; 0, pos == 0),
    /// the bridge normal is flipped so the new face faces the same way as its neighbours.
    /// A mixed or empty neighbourhood leaves the best-fit normal untouched.
    /// </summary>
    private static Vec3 OrientToNeighbors(Geometry g, IReadOnlyList<int> indices, Vec3 normal)
    {
        var set = new HashSet<int>(indices);
        int pos = 0, neg = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || !f.Vertices.Any(fv => set.Contains(fv.Index)))
            {
                continue;
            }

            float dot = normal.Dot(f.Plane.Normal);
            if (dot > 0f)
            {
                pos++;
            }
            else if (dot < 0f)
            {
                neg++;
            }
        }

        return neg > 0 && pos == 0 ? normal.Negate() : normal;
    }

    private static void MergeTo(Geometry g, IReadOnlyList<int> indices, Vec3 target)
    {
        int keep = indices[^1];
        g.Vertices[keep] = target;
        var others = new HashSet<int>(indices.Take(indices.Count - 1));
        foreach (Face f in g.Faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                if (others.Contains(fv.Index))
                {
                    fv.Index = keep;
                }
            }
        }
    }

    private static OpResult Finish(Geometry g, string label)
    {
        GeometryUtil.CleanupFaces(g);
        GeometryUtil.RecomputeAllPlanes(g);
        return g.Faces.Count == 0 ? OpResult.Fail("Operation removed the whole brush.") : OpResult.Ok(label);
    }
}
