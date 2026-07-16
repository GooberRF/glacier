using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// T-joint fixing: wherever a vertex of one face lies on the open edge of
/// another face (a T-junction left by CSG splitting), the vertex is inserted
/// into that edge so the two faces share it. This both eliminates the runtime
/// seam sparkle RED fixes and makes the mesh edge-manifold, which lets the room
/// flood fill connect faces reliably. Runs to a fixed point.
/// </summary>
public static class TJointFixer
{
    private const float LineEps = 1e-3f;   // max perpendicular distance to count as "on the edge"
    private const float EndEps = 1e-3f;    // keep away from the edge endpoints
    private const float CellSize = 4f;
    private const int MaxPasses = 12;

    /// <summary>Inserts on-edge vertices from <paramref name="pool"/> into every face's edges.</summary>
    public static void Fix(List<CsgFace> faces, IReadOnlyList<Vec3> pool)
    {
        // Spatial hash of the pool positions for fast on-edge candidate lookup.
        var grid = new Dictionary<(int, int, int), List<Vec3>>();
        foreach (Vec3 p in pool)
        {
            (int, int, int) cell = Cell(p);
            if (!grid.TryGetValue(cell, out List<Vec3>? bucket))
            {
                bucket = new List<Vec3>();
                grid[cell] = bucket;
            }

            bucket.Add(p);
        }

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool changed = false;
            foreach (CsgFace f in faces)
            {
                // Skip detail/geoable/breakable brush faces: RED never CSG-splits or t-joint-fixes
                // a detail brush, so its compiled room carries the pristine authored faces. Inserting
                // world T-junction stations here (near-coincident + collinear mid-edge vertices) makes
                // the game's RUNTIME material-debris shatter — which re-detects the open boundary by
                // shared vertex identity after each bisection cut — chain those stations into malformed
                // non-planar loops its ear clip cannot cap ("[CapFace] Ear clip stuck"). SeamSealer
                // already excludes detail faces for the same reason; this keeps the two passes aligned.
                if ((f.Flags & (ushort)FaceFlags.IsDetail) != 0)
                {
                    continue;
                }

                if (InsertOnEdges(f, grid))
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private static bool InsertOnEdges(CsgFace f, Dictionary<(int, int, int), List<Vec3>> grid)
    {
        List<CsgVertex> verts = f.Vertices;
        int n = verts.Count;
        var result = new List<CsgVertex>(n + 4);
        bool changed = false;

        for (int i = 0; i < n; i++)
        {
            CsgVertex a = verts[i];
            CsgVertex b = verts[(i + 1) % n];
            result.Add(a);

            Vec3 pa = a.Position, pb = b.Position;
            Vec3 dir = pb.Sub(pa);
            float lenSq = dir.LengthSquared();
            if (lenSq < 1e-9f)
            {
                continue;
            }

            // Collect on-edge candidates with their parameter t.
            var hits = new List<(float T, Vec3 P)>();
            CollectOnEdge(grid, pa, pb, dir, lenSq, hits);
            if (hits.Count == 0)
            {
                continue;
            }

            hits.Sort((x, y) => x.T.CompareTo(y.T));
            float lastT = 0f;
            foreach ((float t, Vec3 p) in hits)
            {
                if (t - lastT < EndEps)
                {
                    continue; // coincident with the previous insertion
                }

                result.Add(new CsgVertex(p, LerpUv(a, b, t)));
                lastT = t;
                changed = true;
            }
        }

        if (changed)
        {
            f.Vertices = result;
        }

        return changed;
    }

    private static void CollectOnEdge(
        Dictionary<(int, int, int), List<Vec3>> grid, Vec3 pa, Vec3 pb, Vec3 dir, float lenSq,
        List<(float, Vec3)> hits)
    {
        Vec3 min = Vec3Math.Min(pa, pb);
        Vec3 max = Vec3Math.Max(pa, pb);
        (int x0, int y0, int z0) = Cell(min);
        (int x1, int y1, int z1) = Cell(max);
        var seen = new HashSet<(int, int, int)>();

        for (int cx = x0 - 1; cx <= x1 + 1; cx++)
        {
            for (int cy = y0 - 1; cy <= y1 + 1; cy++)
            {
                for (int cz = z0 - 1; cz <= z1 + 1; cz++)
                {
                    if (!grid.TryGetValue((cx, cy, cz), out List<Vec3>? bucket))
                    {
                        continue;
                    }

                    foreach (Vec3 p in bucket)
                    {
                        float t = p.Sub(pa).Dot(dir) / lenSq;
                        float len = MathF.Sqrt(lenSq);
                        if (t * len <= EndEps || (1f - t) * len <= EndEps)
                        {
                            continue; // at or beyond an endpoint
                        }

                        Vec3 proj = pa.Add(dir.Scale(t));
                        if (proj.Distance(p) <= LineEps && seen.Add(Quantize(p)))
                        {
                            hits.Add((t, p));
                        }
                    }
                }
            }
        }
    }

    private static Uv LerpUv(CsgVertex a, CsgVertex b, float t) =>
        new(a.Uv.U + ((b.Uv.U - a.Uv.U) * t), a.Uv.V + ((b.Uv.V - a.Uv.V) * t));

    private static (int, int, int) Cell(Vec3 p) =>
        ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize), (int)MathF.Floor(p.Z / CellSize));

    private static (int, int, int) Quantize(Vec3 p) =>
        ((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f));
}
