using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Pure helpers for editing brush <see cref="Geometry"/>: plane recomputation,
/// default planar UV mapping, face area/centroid, vertex welding and validation.
/// All operate on the editable brush representation (vertex pool + faces indexing
/// into it, no rooms/surfaces/portals) and never touch the GPU or IO layers.
/// </summary>
public static class GeometryUtil
{
    /// <summary>Default editor texture-projection scale: one texture tile per this many metres.</summary>
    public const float DefaultTileMetres = 4f;

    /// <summary>Minimum face area (m²) below which a face is treated as degenerate.</summary>
    public const float MinFaceArea = 1e-5f;

    /// <summary>World position of a face vertex via its pool index.</summary>
    public static Vec3 PositionOf(Geometry g, FaceVertex fv) => g.Vertices[fv.Index];

    /// <summary>The ordered world positions of a face's corners.</summary>
    public static List<Vec3> Corners(Geometry g, Face f)
    {
        var list = new List<Vec3>(f.Vertices.Count);
        foreach (FaceVertex fv in f.Vertices)
        {
            list.Add(g.Vertices[fv.Index]);
        }

        return list;
    }

    /// <summary>
    /// Newell's method: the area-weighted normal of a polygon, robust for
    /// non-planar or slightly noisy inputs. Returns a unit normal (or zero for a
    /// degenerate polygon).
    /// </summary>
    public static Vec3 Normal(IReadOnlyList<Vec3> poly)
    {
        var n = new Vec3(0, 0, 0);
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i];
            Vec3 b = poly[(i + 1) % poly.Count];
            n = n.Add(new Vec3(
                (a.Y - b.Y) * (a.Z + b.Z),
                (a.Z - b.Z) * (a.X + b.X),
                (a.X - b.X) * (a.Y + b.Y)));
        }

        return n.Normalized();
    }

    public static Vec3 Centroid(IReadOnlyList<Vec3> poly)
    {
        if (poly.Count == 0)
        {
            return default;
        }

        var sum = new Vec3(0, 0, 0);
        foreach (Vec3 p in poly)
        {
            sum = sum.Add(p);
        }

        return sum.Scale(1f / poly.Count);
    }

    /// <summary>Polygon area from fan triangulation about the centroid.</summary>
    public static float Area(IReadOnlyList<Vec3> poly)
    {
        if (poly.Count < 3)
        {
            return 0f;
        }

        Vec3 c = Centroid(poly);
        float area = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3 a = poly[i].Sub(c);
            Vec3 b = poly[(i + 1) % poly.Count].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    /// <summary>Recomputes and stores the plane (outward normal + offset) of a face from its corners.</summary>
    public static void RecomputePlane(Geometry g, Face f)
    {
        List<Vec3> poly = Corners(g, f);
        Vec3 n = Normal(poly);
        Vec3 c = Centroid(poly);
        f.Plane = new RfPlane(n, n.Dot(c));
    }

    public static void RecomputeAllPlanes(Geometry g)
    {
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count >= 3)
            {
                RecomputePlane(g, f);
            }
        }
    }

    /// <summary>
    /// Assigns a default planar-projected UV to every corner of a face, choosing
    /// the projection plane from the face normal's dominant axis. Texture-mode UV
    /// tools (pixels-per-meter, box/cylinder mapping) refine these.
    /// </summary>
    public static void AssignPlanarUv(Geometry g, Face f, float tileMetres = DefaultTileMetres)
    {
        float scale = tileMetres > 1e-4f ? 1f / tileMetres : 1f;
        Vec3 n = f.Plane.Normal;
        (int uAxis, int vAxis) = DominantProjection(n);
        foreach (FaceVertex fv in f.Vertices)
        {
            Vec3 p = g.Vertices[fv.Index];
            fv.TextureCoords = new Uv(p.Component(uAxis) * scale, -p.Component(vAxis) * scale);
        }
    }

    public static void AssignAllPlanarUv(Geometry g, float tileMetres = DefaultTileMetres)
    {
        foreach (Face f in g.Faces)
        {
            AssignPlanarUv(g, f, tileMetres);
        }
    }

    /// <summary>Chooses (u,v) world axes for planar mapping given a face normal.</summary>
    public static (int UAxis, int VAxis) DominantProjection(Vec3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (az >= ax && az >= ay)
        {
            return (0, 1); // facing ±Z: map X,Y
        }

        if (ax >= ay)
        {
            return (2, 1); // facing ±X: map Z,Y
        }

        return (0, 2); // facing ±Y: map X,Z
    }

    /// <summary>
    /// Merges vertices closer than <paramref name="epsilon"/> into shared pool
    /// entries, rewrites face indices, and drops any vertex no face references.
    /// Keeps the pool tight after edits that split or move vertices.
    /// </summary>
    public static void WeldVertices(Geometry g, float epsilon = 1e-4f)
    {
        var remap = new int[g.Vertices.Count];
        var kept = new List<Vec3>();
        for (int i = 0; i < g.Vertices.Count; i++)
        {
            Vec3 v = g.Vertices[i];
            int found = -1;
            for (int j = 0; j < kept.Count; j++)
            {
                if (kept[j].ApproxEquals(v, epsilon))
                {
                    found = j;
                    break;
                }
            }

            if (found < 0)
            {
                found = kept.Count;
                kept.Add(v);
            }

            remap[i] = found;
        }

        foreach (Face f in g.Faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                fv.Index = remap[fv.Index];
            }
        }

        CompactUnusedVertices(g, kept);
    }

    /// <summary>Removes vertices no face references, renumbering remaining indices.</summary>
    public static void CompactUnusedVertices(Geometry g, List<Vec3>? overridePool = null)
    {
        List<Vec3> pool = overridePool ?? g.Vertices;
        var used = new bool[pool.Count];
        foreach (Face f in g.Faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                if (fv.Index >= 0 && fv.Index < used.Length)
                {
                    used[fv.Index] = true;
                }
            }
        }

        var remap = new int[pool.Count];
        var compact = new List<Vec3>();
        for (int i = 0; i < pool.Count; i++)
        {
            if (used[i])
            {
                remap[i] = compact.Count;
                compact.Add(pool[i]);
            }
            else
            {
                remap[i] = -1;
            }
        }

        foreach (Face f in g.Faces)
        {
            foreach (FaceVertex fv in f.Vertices)
            {
                fv.Index = remap[fv.Index];
            }
        }

        g.Vertices = compact;
    }

    /// <summary>Adds a vertex to the pool (reusing a near-coincident one) and returns its index.</summary>
    public static int AddVertex(Geometry g, Vec3 v, float epsilon = 1e-4f)
    {
        for (int i = 0; i < g.Vertices.Count; i++)
        {
            if (g.Vertices[i].ApproxEquals(v, epsilon))
            {
                return i;
            }
        }

        g.Vertices.Add(v);
        return g.Vertices.Count - 1;
    }

    /// <summary>Ensures the texture name exists in the table and returns its index.</summary>
    public static int EnsureTexture(Geometry g, string texture)
    {
        int idx = g.Textures.IndexOf(texture);
        if (idx < 0)
        {
            idx = g.Textures.Count;
            g.Textures.Add(texture);
        }

        return idx;
    }

    /// <summary>The next unused face id in the geometry (max + 1, or 0).</summary>
    public static int NextFaceId(Geometry g) =>
        g.Faces.Count == 0 ? 0 : g.Faces.Max(f => f.FaceId) + 1;

    /// <summary>
    /// Removes consecutive (and wrap-around) duplicate vertex indices from every
    /// face, drops any face left with fewer than three distinct vertices, and
    /// compacts the pool. Used after welds/collapses that can pinch a face.
    /// </summary>
    public static void CleanupFaces(Geometry g)
    {
        var kept = new List<Face>(g.Faces.Count);
        foreach (Face f in g.Faces)
        {
            var verts = new List<FaceVertex>(f.Vertices.Count);
            foreach (FaceVertex fv in f.Vertices)
            {
                if (verts.Count == 0 || verts[^1].Index != fv.Index)
                {
                    verts.Add(fv);
                }
            }

            while (verts.Count > 1 && verts[0].Index == verts[^1].Index)
            {
                verts.RemoveAt(verts.Count - 1);
            }

            if (verts.Distinct(FaceVertexIndexComparer.Instance).Count() >= 3)
            {
                f.Vertices = verts;
                kept.Add(f);
            }
        }

        g.Faces = kept;
        CompactUnusedVertices(g);
    }

    private sealed class FaceVertexIndexComparer : IEqualityComparer<FaceVertex>
    {
        public static readonly FaceVertexIndexComparer Instance = new();

        public bool Equals(FaceVertex? x, FaceVertex? y) => x?.Index == y?.Index;

        public int GetHashCode(FaceVertex obj) => obj.Index;
    }

    /// <summary>
    /// Validates a brush geometry for authoring: no face with fewer than three
    /// vertices, no zero-area (degenerate) face, and no out-of-range vertex index.
    /// Returns the first problem found, or success.
    /// </summary>
    public static OpResult Validate(Geometry g)
    {
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                return OpResult.Fail("A face would have fewer than three vertices.");
            }

            foreach (FaceVertex fv in f.Vertices)
            {
                if (fv.Index < 0 || fv.Index >= g.Vertices.Count)
                {
                    return OpResult.Fail("A face references a vertex outside the pool.");
                }
            }

            if (Area(Corners(g, f)) < MinFaceArea)
            {
                return OpResult.Fail("A face would have zero area.");
            }
        }

        return OpResult.Ok();
    }

    /// <summary>The axis-aligned bounds of a geometry's local vertex pool.</summary>
    public static Aabb LocalBounds(Geometry g)
    {
        if (g.Vertices.Count == 0)
        {
            return new Aabb(default, default);
        }

        Vec3 min = g.Vertices[0];
        Vec3 max = g.Vertices[0];
        foreach (Vec3 v in g.Vertices)
        {
            min = Vec3Math.Min(min, v);
            max = Vec3Math.Max(max, v);
        }

        return new Aabb(min, max);
    }
}
