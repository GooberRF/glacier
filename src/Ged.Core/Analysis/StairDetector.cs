using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Analysis;

/// <summary>
/// Stair detection on the compiled faces (CORPUS-NORMS-SPEC §3.2 "Stairs"): flat floor faces (normal.Y ≥ 0.999)
/// group into treads (coplanar within 0.01 m and touching in XZ); two treads are stepped when their XZ outlines
/// share a boundary segment at least 0.5 m long (gap ≤ 0.03 m) and the rise is 0.05–1.0 m; a stair is a chain of
/// at least 3 stepped treads with risers within 15% and an ascent direction varying at most 25 degrees.
/// Ramps and stairs under an invisible collision ramp still count (the visible treads are what was authored);
/// spiral stairs fail the direction test and are dropped.
/// </summary>
internal static class StairDetector
{
    public sealed record Stair(double Riser, double? Tread, int Steps, Vec3d Bottom, Vec3d Top);

    private sealed class Tread
    {
        public double Y;
        public readonly List<SpaceSampler.SFace> Faces = new();
        public double MinX = double.MaxValue, MaxX = double.MinValue, MinZ = double.MaxValue, MaxZ = double.MinValue;
        public double Cx, Cz;
    }

    private const double MinShared = 0.5;
    private const double Gap = 0.03;
    private const double MinRise = 0.05;
    private const double MaxRise = 1.0;

    /// <summary>
    /// The deepest median tread still read as a stair (a deviation from the spec's definition, which has no cap):
    /// chains of shallow risers on 3 m+ flats are terraced slopes, not stairs.
    /// </summary>
    private const double MaxTread = 2.0;

    public static List<Stair> Detect(IReadOnlyList<SpaceSampler.SFace> allFaces, CancellationToken ct)
    {
        List<SpaceSampler.SFace> flats = allFaces.Where(f => f.Floor && f.N.Y >= 0.999).ToList();
        if (flats.Count < 3)
        {
            return new List<Stair>();
        }

        // 1. Treads: coplanar touching flats (union-find over AABB-overlapping candidate pairs).
        int n = flats.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        double Y(SpaceSampler.SFace f) => f.P.Average(p => p.Y);
        double[] ys = flats.Select(Y).ToArray();
        int[] order = Enumerable.Range(0, n).OrderBy(i => flats[i].MinX).ThenBy(i => i).ToArray();
        long iter = 0;
        for (int a = 0; a < n; a++)
        {
            SpaceSampler.SFace fa = flats[order[a]];
            for (int b = a + 1; b < n; b++)
            {
                if ((++iter & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                SpaceSampler.SFace fb = flats[order[b]];
                if (fb.MinX > fa.MaxX + Gap)
                {
                    break;
                }

                if (Math.Abs(ys[order[a]] - ys[order[b]]) > 0.01 || fb.MinZ > fa.MaxZ + Gap || fa.MinZ > fb.MaxZ + Gap)
                {
                    continue;
                }

                if (SharedLength(fa, fb) > 0.01)
                {
                    int ra = Find(order[a]), rb = Find(order[b]);
                    if (ra != rb)
                    {
                        parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                    }
                }
            }
        }

        var treads = new List<Tread>();
        foreach (IGrouping<int, int> g in Enumerable.Range(0, n).GroupBy(Find).OrderBy(g => g.Key))
        {
            var t = new Tread();
            double area = 0, cx = 0, cz = 0, ySum = 0;
            foreach (int i in g.OrderBy(i => i))
            {
                SpaceSampler.SFace f = flats[i];
                t.Faces.Add(f);
                double a = Math.Abs(SpaceSampler.Newell(f.P).Y) / 2;
                double fx = f.P.Average(p => p.X), fz = f.P.Average(p => p.Z);
                area += a;
                cx += fx * a;
                cz += fz * a;
                ySum += ys[i] * a;
                t.MinX = Math.Min(t.MinX, f.MinX);
                t.MaxX = Math.Max(t.MaxX, f.MaxX);
                t.MinZ = Math.Min(t.MinZ, f.MinZ);
                t.MaxZ = Math.Max(t.MaxZ, f.MaxZ);
            }

            if (area <= 0)
            {
                continue;
            }

            t.Cx = cx / area;
            t.Cz = cz / area;
            t.Y = ySum / area;
            treads.Add(t);
        }

        // 2. Stepped pairs (lower → upper).
        var pairs = new List<(int Lo, int Hi, double Rise, double Dx, double Dz)>();
        int[] tOrder = Enumerable.Range(0, treads.Count).OrderBy(i => treads[i].MinX).ThenBy(i => i).ToArray();
        for (int a = 0; a < tOrder.Length; a++)
        {
            Tread ta = treads[tOrder[a]];
            for (int b = a + 1; b < tOrder.Length; b++)
            {
                if ((++iter & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                Tread tb = treads[tOrder[b]];
                if (tb.MinX > ta.MaxX + Gap)
                {
                    break;
                }

                if (tb.MinZ > ta.MaxZ + Gap || ta.MinZ > tb.MaxZ + Gap)
                {
                    continue;
                }

                double rise = Math.Abs(ta.Y - tb.Y);
                if (rise < MinRise || rise > MaxRise || !Stepped(ta, tb))
                {
                    continue;
                }

                (int lo, int hi) = ta.Y < tb.Y ? (tOrder[a], tOrder[b]) : (tOrder[b], tOrder[a]);
                double dx = treads[hi].Cx - treads[lo].Cx, dz = treads[hi].Cz - treads[lo].Cz;
                double len = Math.Sqrt((dx * dx) + (dz * dz));
                if (len < 1e-6)
                {
                    continue;
                }

                pairs.Add((lo, hi, rise, dx / len, dz / len));
            }
        }

        pairs.Sort((p, q) =>
        {
            int c = treads[p.Lo].Y.CompareTo(treads[q.Lo].Y);
            return c != 0 ? c : p.Lo != q.Lo ? p.Lo.CompareTo(q.Lo) : p.Hi.CompareTo(q.Hi);
        });

        // 3. Chains, greedily from the lowest unused pair.
        var byLo = new Dictionary<int, List<int>>();
        var byHi = new Dictionary<int, List<int>>();
        for (int q = 0; q < pairs.Count; q++)
        {
            (byLo.TryGetValue(pairs[q].Lo, out List<int>? l1) ? l1 : byLo[pairs[q].Lo] = new List<int>()).Add(q);
            (byHi.TryGetValue(pairs[q].Hi, out List<int>? l2) ? l2 : byHi[pairs[q].Hi] = new List<int>()).Add(q);
        }

        var used = new HashSet<int>();
        var stairs = new List<Stair>();
        double cos25 = Math.Cos(25 * Math.PI / 180);
        for (int s = 0; s < pairs.Count; s++)
        {
            if (used.Contains(s))
            {
                continue;
            }

            var seed = pairs[s];
            var chain = new List<int> { seed.Lo, seed.Hi };
            var risers = new List<double> { seed.Rise };
            var chainPairs = new List<int> { s };
            bool Agrees((int Lo, int Hi, double Rise, double Dx, double Dz) p) =>
                Math.Abs(p.Rise - seed.Rise) <= 0.15 * seed.Rise && ((p.Dx * seed.Dx) + (p.Dz * seed.Dz)) >= cos25;

            // Forward.
            while (true)
            {
                int last = chain[^1];
                int pick = -1;
                double bestCos = -2;
                foreach (int q in byLo.TryGetValue(last, out List<int>? fw) ? fw : new List<int>())
                {
                    var p = pairs[q];
                    if (p.Lo != last || used.Contains(q) || chain.Contains(p.Hi) || !Agrees(p))
                    {
                        continue;
                    }

                    double c = (p.Dx * seed.Dx) + (p.Dz * seed.Dz);
                    if (c > bestCos + 1e-12)
                    {
                        bestCos = c;
                        pick = q;
                    }
                }

                if (pick < 0)
                {
                    break;
                }

                chain.Add(pairs[pick].Hi);
                risers.Add(pairs[pick].Rise);
                chainPairs.Add(pick);
            }

            // Backward.
            while (true)
            {
                int first = chain[0];
                int pick = -1;
                double bestCos = -2;
                foreach (int q in byHi.TryGetValue(first, out List<int>? bw) ? bw : new List<int>())
                {
                    var p = pairs[q];
                    if (p.Hi != first || used.Contains(q) || chain.Contains(p.Lo) || !Agrees(p))
                    {
                        continue;
                    }

                    double c = (p.Dx * seed.Dx) + (p.Dz * seed.Dz);
                    if (c > bestCos + 1e-12)
                    {
                        bestCos = c;
                        pick = q;
                    }
                }

                if (pick < 0)
                {
                    break;
                }

                chain.Insert(0, pairs[pick].Lo);
                risers.Add(pairs[pick].Rise);
                chainPairs.Add(pick);
            }

            if (chain.Count < 3)
            {
                continue;
            }

            foreach (int q in chainPairs)
            {
                used.Add(q);
            }

            // Mean ascent direction over the chain; tread = extent along it of the intermediate treads.
            double mx = treads[chain[^1]].Cx - treads[chain[0]].Cx, mz = treads[chain[^1]].Cz - treads[chain[0]].Cz;
            double ml = Math.Sqrt((mx * mx) + (mz * mz));
            (double ux, double uz) = ml > 1e-6 ? (mx / ml, mz / ml) : (seed.Dx, seed.Dz);
            var depths = new List<double>();
            for (int i = 1; i + 1 < chain.Count; i++)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (SpaceSampler.SFace f in treads[chain[i]].Faces)
                {
                    foreach (Vec3d p in f.P)
                    {
                        double t = (p.X * ux) + (p.Z * uz);
                        lo = Math.Min(lo, t);
                        hi = Math.Max(hi, t);
                    }
                }

                depths.Add(hi - lo);
            }

            // Terrace guard: treads deeper than MaxTread are a stepped slope, not a stair.
            if (depths.Count > 0 && SpaceSampler.Median(depths) > MaxTread)
            {
                continue;
            }

            Tread bottom = treads[chain[0]], top = treads[chain[^1]];
            stairs.Add(new Stair(
                SpaceSampler.Median(risers),
                depths.Count > 0 ? SpaceSampler.Median(depths) : null,
                chain.Count,
                new Vec3d(bottom.Cx, bottom.Y, bottom.Cz),
                new Vec3d(top.Cx, top.Y, top.Cz)));
        }

        return stairs;
    }

    private static bool Stepped(Tread a, Tread b)
    {
        foreach (SpaceSampler.SFace fa in a.Faces)
        {
            foreach (SpaceSampler.SFace fb in b.Faces)
            {
                if (fb.MinX > fa.MaxX + Gap || fa.MinX > fb.MaxX + Gap || fb.MinZ > fa.MaxZ + Gap || fa.MinZ > fb.MaxZ + Gap)
                {
                    continue;
                }

                if (SharedLength(fa, fb) >= MinShared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The longest collinear boundary overlap (in XZ) between two faces' edges, within the 0.03 m gap.</summary>
    internal static double SharedLength(SpaceSampler.SFace a, SpaceSampler.SFace b)
    {
        double best = 0;
        for (int i = 0; i < a.P.Length; i++)
        {
            Vec3d p1 = a.P[i], p2 = a.P[(i + 1) % a.P.Length];
            double ex = p2.X - p1.X, ez = p2.Z - p1.Z;
            double el = Math.Sqrt((ex * ex) + (ez * ez));
            if (el < 1e-6)
            {
                continue;
            }

            double ux = ex / el, uz = ez / el;
            for (int j = 0; j < b.P.Length; j++)
            {
                Vec3d q1 = b.P[j], q2 = b.P[(j + 1) % b.P.Length];
                double fx = q2.X - q1.X, fz = q2.Z - q1.Z;
                double fl = Math.Sqrt((fx * fx) + (fz * fz));
                if (fl < 1e-6 || Math.Abs((ux * fz / fl) - (uz * fx / fl)) > 0.035)
                {
                    continue;
                }

                // Perpendicular distance of q1, q2 from the line through p1 along u.
                double d1 = Math.Abs(((q1.X - p1.X) * uz) - ((q1.Z - p1.Z) * ux));
                double d2 = Math.Abs(((q2.X - p1.X) * uz) - ((q2.Z - p1.Z) * ux));
                if (d1 > Gap || d2 > Gap)
                {
                    continue;
                }

                double t1 = ((q1.X - p1.X) * ux) + ((q1.Z - p1.Z) * uz);
                double t2 = ((q2.X - p1.X) * ux) + ((q2.Z - p1.Z) * uz);
                double overlap = Math.Min(el, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
                best = Math.Max(best, overlap);
            }
        }

        return best;
    }
}
