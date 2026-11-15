using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Spatial;

namespace Ged.Core.Scripting.Kit;

/// <summary>
/// kit.spawn_points' planner (scripts/lib/kit_items.lua places what it returns). Candidates are clear floor on a
/// 1 m sub-lattice of the spaces, 2.5 m from doorways and 1.2 m from items; each is scored for shelter (beside
/// cover or in an alcove: chest-height rays that hit within 2.5 m), distance from the doorway, exposure and the
/// spawns it sees within 30 m, with a pull toward a nearest neighbour of 8–15 m. Exposure and spawn-to-spawn line
/// of sight are the grader's own (G19, spatial.spawn_report: the share of the playable floor within 40 m that the
/// eye 1.6 m above the spawn's floor sample sees, and eye-to-eye sight for pairs within 30 m), measured on the
/// level's compiled geometry on the grader's floor set (standable floor) with the report's stable targets — so a
/// level populated by the kit passes G19 by construction wherever its spaces allow it.
/// </summary>
internal static class KitSpawnPlanner
{
    private sealed class Cand
    {
        public required KitFloorNode N;
        public int Space;
        public KitFloorNode? Door;
        public double DoorD, Prot, Base;
        public int Sample = -1;
        public double? Expo;
        public bool Used;
        public readonly Dictionary<int, bool> Sight = new();
        public int Key;
    }

    private sealed class Placed
    {
        public double X, Y, Z;
        public int Sample = -1;
        public bool Existing;
    }

    public static Dictionary<string, object?> Plan(ScriptKitCore core, ScriptArgs a)
    {
        List<KitSpace> spaces = KitArgs.Spaces(a);
        int count = (int)KitArgs.Num(a, "count", 1);
        double margin = KitArgs.Num(a, "margin", 1.5);
        double spacing = KitArgs.Num(a, "spacing", 8);
        double seed = KitArgs.Num(a, "seed", 1);
        double spawnHeight = KitArgs.Num(a, "spawn_height", 1.0);
        KitProbe probe = core.Probe().Model;

        var playable = new KitPlayable(probe, spaces, margin, n => core.ZoneFilter(probe, n));
        double bx0 = spaces.Min(s => s.X0) - 40, bz0 = spaces.Min(s => s.Z0) - 40, bx1 = spaces.Max(s => s.X1) + 40, bz1 = spaces.Max(s => s.Z1) + 40;
        bool InBox(Vec3d p) => p.X >= bx0 && p.X <= bx1 && p.Z >= bz0 && p.Z <= bz1;

        SpatialAnalysis? sight = KitSight.For(core, out string measure);
        int SampleOf(double x, double floorY, double z)
        {
            if (sight is null)
            {
                return -1;
            }

            (int s, bool play) = sight.SpawnSampleOf(new Vec3d(x, floorY + spawnHeight, z));
            return play ? s : -1;
        }

        var chosen = new List<Placed>();
        foreach (Vec3d p in KitArgs.Points(a, "existing"))
        {
            if (InBox(p))
            {
                chosen.Add(new Placed { X = p.X, Y = p.Y - spawnHeight, Z = p.Z, Existing = true, Sample = SampleOf(p.X, p.Y - spawnHeight, p.Z) });
            }
        }

        List<Vec3d> items = KitArgs.Points(a, "items");

        // Candidates.
        var cands = new List<Cand>();
        for (int si = 0; si < playable.PerSpace.Count; si++)
        {
            var ps = playable.PerSpace[si];
            foreach (KitFloorNode n in ps.Nodes)
            {
                if (!n.Clear || ((n.I % 2) + 2) % 2 != 0 || ((n.K % 2) + 2) % 2 != 0)
                {
                    continue;
                }

                double dd = double.MaxValue;
                KitFloorNode? door = null;
                foreach (KitFloorNode dn in ps.Doors)
                {
                    double dist = ((dn.X - n.X) * (dn.X - n.X)) + ((dn.Z - n.Z) * (dn.Z - n.Z));
                    if (dist < dd)
                    {
                        dd = dist;
                        door = dn;
                    }
                }

                if (dd < 2.5 * 2.5 || items.Any(ip => ((ip.X - n.X) * (ip.X - n.X)) + ((ip.Z - n.Z) * (ip.Z - n.Z)) < 1.44 && Math.Abs(ip.Y - n.Y) < 2))
                {
                    continue;
                }

                int sample = sight is null ? -1 : SampleOf(n.X, n.Y, n.Z);
                if (sight is not null && sample < 0)
                {
                    continue; // not on the grader's playable floor
                }

                var c = new Cand { N = n, Space = si, Door = door, DoorD = Math.Sqrt(dd), Prot = probe.Protection(n.X, n.Y, n.Z, 2.5), Sample = sample, Key = cands.Count };
                c.Base = (1.5 * Math.Min(c.Prot, 0.6)) + (0.3 * Math.Min(c.DoorD, 10) / 10);
                cands.Add(c);
            }
        }

        if (cands.Count == 0)
        {
            return Fail($"no clear floor for spawns (margin {margin:0.##} m, 2.5 m from doorways).", "Use a smaller margin or bigger rooms.");
        }

        // Exposure: the grader's measure on the compiled level, with the spawn report's own stable targets (a candidate
        // measures what the report will measure for a spawn standing there); the kit's probe when the level cannot be
        // measured.
        List<KitFloorNode> probeTargets = sight is null ? playable.All.Where((_, i) => i % 16 == 0).ToList() : new();
        void Measure(IEnumerable<Cand> list)
        {
            List<Cand> todo = list.Where(c => c.Expo is null).ToList();
            if (todo.Count == 0)
            {
                return;
            }

            if (sight is not null)
            {
                double[] e = sight.SpawnExposure(todo.Select(c => c.Sample).ToList(), k => new Random(unchecked((int)(seed * 1000003) ^ (todo[k].Key * 7919) ^ 0x5bd1e995)));
                for (int i = 0; i < todo.Count; i++)
                {
                    todo[i].Expo = e[i];
                }

                return;
            }

            foreach (Cand c in todo)
            {
                var st = new KitStream(seed + (c.Key * 31));
                int seen = 0, tried = 0;
                double ex = c.N.X, ey = c.N.Y + KitProbe.Eye, ez = c.N.Z;
                for (int guard = 0; tried < 24 && guard < 24 * 6 && probeTargets.Count > 0; guard++)
                {
                    KitFloorNode t = probeTargets[st.Int(1, probeTargets.Count) - 1];
                    double d2 = ((t.X - ex) * (t.X - ex)) + ((t.Z - ez) * (t.Z - ez));
                    if (d2 <= 1600 && d2 > 1)
                    {
                        tried++;
                        if (probe.Los(ex, ey, ez, t.X, t.Y + KitProbe.Eye, t.Z))
                        {
                            seen++;
                        }
                    }
                }

                c.Expo = tried == 0 ? 0 : seen / (double)tried;
            }
        }

        double Expo(Cand c)
        {
            if (c.Expo is null)
            {
                Measure(new[] { c });
            }

            return c.Expo!.Value;
        }

        bool Sees(Cand c, Placed o, int q)
        {
            if (c.Sight.TryGetValue(q, out bool v))
            {
                return v;
            }

            v = sight is not null && c.Sample >= 0 && o.Sample >= 0
                ? sight.EyeLos(c.Sample, o.Sample)
                : probe.Los(c.N.X, c.N.Y + KitProbe.Eye, c.N.Z, o.X, o.Y + KitProbe.Eye, o.Z);
            c.Sight[q] = v;
            return v;
        }

        int Cmp(Cand x, Cand y)
        {
            if (Math.Abs(x.Base - y.Base) > 1e-9)
            {
                return y.Base.CompareTo(x.Base);
            }

            return x.N.X != y.N.X ? x.N.X.CompareTo(y.N.X) : x.N.Z.CompareTo(y.N.Z);
        }

        // Quotas: at least one per space while count lasts, the rest by floor area weighted by how sheltered the
        // space's best spots are.
        int nsp = playable.PerSpace.Count;
        var quota = new int[nsp];
        var valid = new bool[nsp];
        var weight = new double[nsp];
        var mine = new List<Cand>[nsp];
        double total = 0;
        for (int si = 0; si < nsp; si++)
        {
            mine[si] = cands.Where(c => c.Space == si).ToList();
            StableSort(mine[si], Cmp);
            valid[si] = mine[si].Count > 0 && playable.PerSpace[si].Area >= 20;
            if (valid[si])
            {
                Measure(mine[si].Take(12));
                double best = mine[si].Take(12).Min(Expo);
                weight[si] = playable.PerSpace[si].Area * (1 - best) * (1 - best);
                total += weight[si];
            }
        }

        if (total <= 0)
        {
            total = 1;
        }

        var order = Enumerable.Range(0, nsp).Where(si => valid[si]).ToList();
        StableSort(order, (x, y) => weight[x] != weight[y] ? weight[y].CompareTo(weight[x]) : x.CompareTo(y));
        int left = count;
        foreach (int si in order)
        {
            if (left > 0)
            {
                quota[si] = 1;
                left--;
            }
        }

        while (left > 0 && order.Count > 0)
        {
            int best = -1;
            double bv = double.NegativeInfinity;
            foreach (int si in order)
            {
                double v = (weight[si] / total * count) - quota[si];
                if (v > bv)
                {
                    best = si;
                    bv = v;
                }
            }

            quota[best]++;
            left--;
        }

        if (order.Count == 0)
        {
            order.Add(cands[0].Space);
            quota[cands[0].Space] = count;
        }

        var shortlist = new Dictionary<int, List<Cand>>();
        foreach (int si in order)
        {
            shortlist[si] = mine[si].Take(Math.Max(24, 8 * Math.Max(1, quota[si]))).ToList();
            Measure(shortlist[si]);
        }

        (double Score, int Seen)? Score(Cand c, double minGap)
        {
            KitFloorNode n = c.N;
            double nn = double.MaxValue;
            int seen = 0;
            for (int q = 0; q < chosen.Count; q++)
            {
                Placed o = chosen[q];
                double dist = Math.Sqrt(((o.X - n.X) * (o.X - n.X)) + ((o.Z - n.Z) * (o.Z - n.Z)) + ((o.Y - n.Y) * (o.Y - n.Y)));
                nn = Math.Min(nn, dist);
                if (dist <= 30 && Sees(c, o, q))
                {
                    seen++;
                }
            }

            if (nn < minGap)
            {
                return null;
            }

            double e = Expo(c);
            double s = c.Base - (2.5 * seen) - (3.0 * e) - (e > 0.2 ? 6.0 * (e - 0.2) : 0);
            if (nn >= 8 && nn <= 15)
            {
                s += 0.4;
            }
            else if (nn > 15 && nn < double.MaxValue)
            {
                s += 0.2 - (0.01 * Math.Min(nn - 15, 20));
            }

            return (s, seen);
        }

        var placedIn = new int[nsp];
        var picked = new List<Cand>();
        for (int t = 0; t < count; t++)
        {
            double gap = spacing;
            Cand? best = null;
            double bs = 0;
            while (best is null && gap >= 2)
            {
                for (int pass = 1; pass <= 2 && best is null; pass++)
                {
                    foreach (int si in order)
                    {
                        double pull = 0.8 * Math.Max(-1, Math.Min(1, quota[si] - placedIn[si]));
                        foreach (Cand c in pass == 1 ? shortlist[si] : mine[si])
                        {
                            if (c.Used || Score(c, gap) is not { } sc)
                            {
                                continue;
                            }

                            double v = sc.Score + pull;
                            if (best is null || v > bs + 1e-9)
                            {
                                best = c;
                                bs = v;
                            }
                        }
                    }
                }

                if (best is null)
                {
                    gap -= 1;
                }
            }

            // In sight of another spawn at the full spacing: a spot up to a fifth closer that is hidden from every
            // other spawn (and no more exposed) wins.
            if (best is not null && gap >= spacing && Score(best, 0) is { Seen: > 0 })
            {
                Cand? alt = null;
                double asc = 0;
                foreach (int si in order)
                {
                    double pull = 0.8 * Math.Max(-1, Math.Min(1, quota[si] - placedIn[si]));
                    foreach (Cand c in shortlist[si])
                    {
                        if (!c.Used && Score(c, spacing * 0.8) is { Seen: 0 } sc && (alt is null || sc.Score + pull > asc + 1e-9))
                        {
                            alt = c;
                            asc = sc.Score + pull;
                        }
                    }
                }

                if (alt is not null)
                {
                    best = alt;
                }
            }

            if (best is null)
            {
                break;
            }

            best.Used = true;
            placedIn[best.Space]++;
            chosen.Add(new Placed { X = best.N.X, Y = best.N.Y, Z = best.N.Z, Sample = best.Sample });
            picked.Add(best);
        }

        if (picked.Count < count)
        {
            return Fail($"has room for only {picked.Count} of {count} spawn points with a {margin:0.##} m margin.", "Use a smaller count or margin, or more rooms.");
        }

        // Headings: the nearest doorway when it is in sight, else the longest open heading.
        var spawns = new List<object?>();
        foreach (Cand c in picked)
        {
            KitFloorNode n = c.N;
            double fx, fz;
            if (c.Door is { } d && probe.Los(n.X, n.Y + KitProbe.Eye, n.Z, d.X, d.Y + KitProbe.Eye, d.Z))
            {
                fx = d.X - n.X;
                fz = d.Z - n.Z;
            }
            else
            {
                (fx, fz, _) = probe.LongestRay(n.X, n.Y, n.Z, 40);
            }

            spawns.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = n.X, ["y"] = n.Y, ["z"] = n.Z, ["fx"] = fx, ["fz"] = fz, ["space"] = (double)(c.Space + 1), ["exposure"] = Expo(c),
            });
        }

        // The report, on the same measure.
        var nnList = new List<double>();
        int pairs = 0, seenPairs = 0;
        for (int i = 0; i < picked.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < picked.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                KitFloorNode p = picked[i].N, q = picked[j].N;
                double dist = Math.Sqrt(((p.X - q.X) * (p.X - q.X)) + ((p.Z - q.Z) * (p.Z - q.Z)) + ((p.Y - q.Y) * (p.Y - q.Y)));
                best = Math.Min(best, dist);
                if (i < j && dist <= 30)
                {
                    pairs++;
                    bool v = sight is not null && picked[i].Sample >= 0 && picked[j].Sample >= 0
                        ? sight.EyeLos(picked[i].Sample, picked[j].Sample)
                        : probe.Los(p.X, p.Y + KitProbe.Eye, p.Z, q.X, q.Y + KitProbe.Eye, q.Z);
                    if (v)
                    {
                        seenPairs++;
                    }
                }
            }

            if (best < double.MaxValue)
            {
                nnList.Add(best);
            }
        }

        nnList.Sort();
        List<double> expo = picked.Select(Expo).OrderBy(x => x).ToList();
        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nn_min"] = nnList.Count > 0 ? nnList[0] : null,
            ["nn_median"] = nnList.Count > 0 ? Percentiles.Type7(nnList, 0.5) : null, // the spawn report's p50
            ["los_pairs_within30"] = pairs > 0 ? seenPairs / (double)pairs : 0,
            ["exposure_max"] = expo.Count > 0 ? expo[^1] : null,
            ["exposure_median"] = expo.Count > 0 ? Percentiles.Type7(expo, 0.5) : null,
            ["measure"] = measure,
        };
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["spawns"] = spawns.ToArray(), ["report"] = report };
    }

    private static Dictionary<string, object?> Fail(string message, string hint) =>
        new(StringComparer.Ordinal) { ["error"] = message, ["hint"] = hint };

    /// <summary>A stable merge sort (List.Sort is not stable).</summary>
    internal static void StableSort<T>(List<T> list, Comparison<T> cmp)
    {
        T[] sorted = list.Select((v, i) => (v, i)).OrderBy(x => x, Comparer<(T v, int i)>.Create((p, q) =>
        {
            int c = cmp(p.v, q.v);
            return c != 0 ? c : p.i.CompareTo(q.i);
        })).Select(x => x.v).ToArray();
        list.Clear();
        list.AddRange(sorted);
    }
}
