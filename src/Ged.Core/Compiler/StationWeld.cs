using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Plane-aware station weld for the leaf-extraction boundary set. RED reaches watertightness because every
/// face is cut by the SAME global partition and coincident cuts come out bit-identical; GED's extraction
/// cuts each portal from its own root→leaf node-plane subset, so an OVER-DETERMINED corner (4+ brush planes
/// meeting at one point — a ridge meeting a wall, a wall meeting two floors) is computed by DIFFERENT
/// three-plane triples on the two faces that meet there and lands a fraction of a millimetre apart — just
/// over the 1e-4 vertex weld, so the shared edge never pairs and the room leaks (measured dominant cohort on
/// dm02/dm06/ctf02/dm04).
/// <para>
/// This pass canonicalises those stations by MERGING vertices that are within <see cref="Tol"/> AND share at
/// least <see cref="MinShared"/> registry planes. Sharing two planes proves the two vertices lie on the same
/// brush LINE (the intersection of those two planes) — so they are the same geometric corner computed two
/// ways, never two distinct authored features: dm01's real 2 cm wall lip is bounded by PARALLEL planes at
/// different offsets (which fold to different registry ids), so its vertices share fewer than two planes and
/// are never merged. That makes the weld safe to run at a millimetre scale where a blunt distance weld would
/// risk bridging real geometry. Each cluster snaps to one deterministic representative position (the member
/// carrying the most planes — the most-constrained, best-conditioned corner — ties broken by the smallest
/// plane-id tuple), so every face meeting at the corner agrees by construction, exactly as RED's shared cut
/// does.
/// </para>
/// </summary>
internal static class StationWeld
{
    /// <summary>Max distance to merge two same-line stations. Well above the observed 0.1–1.5 mm
    /// over-determined-corner divergence, well below any authored feature (and further gated by the shared-plane
    /// test, so it cannot bridge a genuine gap between distinct planes).</summary>
    private const float Tol = 3e-3f;

    /// <summary>Shared registry planes required to treat two near-coincident vertices as the same corner:
    /// two planes ⇒ same brush line ⇒ a real coincidence, not a distinct feature.</summary>
    private const int MinShared = 2;

    private const float Cell = Tol;

    /// <summary>Canonicalises the shared cut-vertex positions of the extracted boundary polygons in place.</summary>
    public static void Canonicalize(List<WorldBsp.BoundaryPolygon> portals)
    {
        var pts = new List<Vec3>();
        var planes = new List<int[]>();
        var backPortal = new List<int>();
        var backVert = new List<int>();
        for (int pi = 0; pi < portals.Count; pi++)
        {
            List<CsgSharedSplit.PsVert> poly = portals[pi].Poly;
            for (int vi = 0; vi < poly.Count; vi++)
            {
                pts.Add(poly[vi].Pos);
                planes.Add(poly[vi].Planes);
                backPortal.Add(pi);
                backVert.Add(vi);
            }
        }

        int n = pts.Count;
        if (n == 0)
        {
            return;
        }

        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            (int, int, int) c = CellOf(pts[i]);
            if (!grid.TryGetValue(c, out List<int>? b))
            {
                grid[c] = b = new List<int>();
            }

            b.Add(i);
        }

        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        float tolSq = Tol * Tol;
        for (int i = 0; i < n; i++)
        {
            Vec3 p = pts[i];
            (int cx, int cy, int cz) = CellOf(p);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                        {
                            continue;
                        }

                        foreach (int j in bucket)
                        {
                            if (j <= i)
                            {
                                continue;
                            }

                            // Never merge two vertices of the SAME portal: that would collapse one of its
                            // edges to zero length (a degenerate sliver). The weld exists to make DIFFERENT
                            // faces meeting at a corner agree, not to simplify a single polygon.
                            if (backPortal[i] == backPortal[j])
                            {
                                continue;
                            }

                            if (pts[j].Sub(p).LengthSquared() <= tolSq && SharedCount(planes[i], planes[j]) >= MinShared)
                            {
                                int ra = Find(i), rb = Find(j);
                                if (ra != rb)
                                {
                                    parent[ra] = rb;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Representative per cluster = the member carrying the most planes (best-conditioned corner), ties
        // broken by the lexicographically smallest plane-id tuple, then the smallest vertex index.
        var repOf = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!repOf.TryGetValue(r, out int cur) || Better(i, cur, planes))
            {
                repOf[r] = i;
            }
        }

        for (int i = 0; i < n; i++)
        {
            int rep = repOf[Find(i)];
            if (rep == i)
            {
                continue;
            }

            Vec3 to = pts[rep];
            if (to.Sub(pts[i]).LengthSquared() < 1e-12f)
            {
                continue;
            }

            List<CsgSharedSplit.PsVert> poly = portals[backPortal[i]].Poly;
            int vi = backVert[i];
            CsgSharedSplit.PsVert v = poly[vi];
            poly[vi] = new CsgSharedSplit.PsVert(to, v.Uv, v.Planes);
        }
    }

    /// <summary>True when candidate <paramref name="a"/> is a better cluster representative than <paramref name="b"/>.</summary>
    private static bool Better(int a, int b, List<int[]> planes)
    {
        int[] pa = planes[a], pb = planes[b];
        if (pa.Length != pb.Length)
        {
            return pa.Length > pb.Length; // more planes ⇒ more constrained
        }

        int m = pa.Length < pb.Length ? pa.Length : pb.Length;
        for (int k = 0; k < m; k++)
        {
            if (pa[k] != pb[k])
            {
                return pa[k] < pb[k];
            }
        }

        return a < b;
    }

    /// <summary>Count of shared ids between two SORTED, distinct plane-id arrays.</summary>
    private static int SharedCount(int[] a, int[] b)
    {
        int i = 0, j = 0, count = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                count++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return count;
    }

    private static (int, int, int) CellOf(Vec3 p) =>
        ((int)System.MathF.Floor(p.X / Cell), (int)System.MathF.Floor(p.Y / Cell), (int)System.MathF.Floor(p.Z / Cell));
}
