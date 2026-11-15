using System;
using System.Collections.Generic;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Analysis;

/// <summary>
/// Finds where polygons meet edge to edge: every pair of polygon edges that run along the same line and overlap by
/// at least a minimum length, with the overlapping segment. Works across T-junctions (an edge of one face lying along
/// part of a longer edge of its neighbour), unlike vertex-index matching, and on polygons that share no vertex pool
/// (separate brushes). Edges are bucketed by their supporting line, so it stays near linear in the number of edges.
/// </summary>
public static class SeamFinder
{
    /// <summary>How far (metres) an edge end may lie off the other edge's line and still count as collinear.</summary>
    public const double LineTolerance = 2e-3;

    /// <summary>
    /// Every (a, b, p0, p1) with a &lt; b: an edge of polygon a and an edge of polygon b are collinear and overlap along
    /// [p0, p1], at least <paramref name="minLength"/> long. A pair of polygons can appear more than once (several edge
    /// pairs).
    /// </summary>
    public static List<(int A, int B, Vec3d P0, Vec3d P1)> Pairs(IReadOnlyList<Vec3d[]> polys, double minLength)
    {
        ArgumentNullException.ThrowIfNull(polys);

        // Collinear edges share their line: bucket every edge by the point of its line closest to the origin.
        const double cell = 0.02;
        var edges = new List<(int Face, Vec3d A, Vec3d B, Vec3d U, double L)>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int fi = 0; fi < polys.Count; fi++)
        {
            Vec3d[] p = polys[fi];
            for (int k = 0; k < p.Length; k++)
            {
                Vec3d a = p[k], b = p[(k + 1) % p.Length];
                Vec3d d = b.Sub(a);
                double len = d.Length;
                if (len < minLength || !double.IsFinite(len))
                {
                    continue;
                }

                Vec3d u = d.Scale(1 / len);
                Vec3d c = a.Sub(u.Scale(a.Dot(u)));
                int ei = edges.Count;
                edges.Add((fi, a, b, u, len));
                (long, long, long) key = (Cell(c.X), Cell(c.Y), Cell(c.Z));
                if (!grid.TryGetValue(key, out List<int>? l))
                {
                    grid[key] = l = new List<int>();
                }

                l.Add(ei);
            }
        }

        var result = new List<(int, int, Vec3d, Vec3d)>();
        for (int e1 = 0; e1 < edges.Count; e1++)
        {
            (int fa, Vec3d a0, Vec3d a1, Vec3d u, double la) = edges[e1];
            Vec3d c = a0.Sub(u.Scale(a0.Dot(u)));
            long cx = Cell(c.X), cy = Cell(c.Y), cz = Cell(c.Z);
            for (long x = cx - 1; x <= cx + 1; x++)
            {
                for (long y = cy - 1; y <= cy + 1; y++)
                {
                    for (long z = cz - 1; z <= cz + 1; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out List<int>? l))
                        {
                            continue;
                        }

                        foreach (int e2 in l)
                        {
                            if (e2 <= e1)
                            {
                                continue;
                            }

                            (int fb, Vec3d b0, Vec3d b1, Vec3d ub, _) = edges[e2];
                            if (fb == fa || Math.Abs(u.Dot(ub)) < 0.9999 || Off(b0, a0, u) > LineTolerance || Off(b1, a0, u) > LineTolerance)
                            {
                                continue;
                            }

                            double t0 = b0.Sub(a0).Dot(u), t1 = b1.Sub(a0).Dot(u);
                            double lo = Math.Max(0, Math.Min(t0, t1)), hi = Math.Min(la, Math.Max(t0, t1));
                            if (hi - lo < minLength)
                            {
                                continue;
                            }

                            Vec3d p0 = a0.Add(u.Scale(lo)), p1 = a0.Add(u.Scale(hi));
                            result.Add(fa < fb ? (fa, fb, p0, p1) : (fb, fa, p0, p1));
                        }
                    }
                }
            }
        }

        return result;

        static long Cell(double v) => (long)Math.Floor(v / cell);
    }

    private static double Off(Vec3d p, Vec3d o, Vec3d u)
    {
        Vec3d d = p.Sub(o);
        return d.Sub(u.Scale(d.Dot(u))).Length;
    }
}
