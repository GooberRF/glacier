using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// Sightlines and exposure; the spawn and item reports.
public sealed partial class SpatialAnalysis
{
    /// <summary>The sightline spots (1,200), rays per spot (16), visible pairs (8,000) and exposure spots (300) of E5.</summary>
    public const int SightSpots = 1200, SightPairs = 8000, ExposureSpots = 300;

    private JsonObject? _sightReport;

    /// <summary>The longest sightlines found (from, to, length), longest first (at most 10).</summary>
    internal List<(Vec3d From, Vec3d To, double Length)> LongestSightlines { get; } = new();

    /// <summary>The exposure spots measured: (floor point, share of the floor within 50 m it sees).</summary>
    internal List<(Vec3d P, double Exposure)> ExposureSamples { get; } = new();

    private JsonObject SightReport()
    {
        if (_sightReport is not null)
        {
            return _sightReport;
        }

        RoutesReport(); // fixed order of the sampled sections
        if (_o.StableSampling)
        {
            return _sightReport = StableSightReport();
        }

        List<int> play = EyeIdx;
        int m = Math.Min(SightSpots, play.Count);
        var spots = new int[m];
        for (int t = 0; t < m; t++)
        {
            spots[t] = play[_rng.Next(play.Count)];
        }

        var rays = new double[m * 16];
        For(m, t =>
        {
            Vec3d e = EyeOf(spots[t]);
            for (int k = 0; k < 16; k++)
            {
                rays[(t * 16) + k] = _sp.Cast(e, SpaceSampler.Directions[k], 200, SpaceSampler.HitFilter.Any) ?? 200;
            }
        });
        var maxRay = new List<double>(m);
        var allRay = new List<double>(m * 16);
        var longest = new List<(Vec3d, Vec3d, double)>();
        for (int t = 0; t < m; t++)
        {
            double mx = 0;
            int mk = 0;
            for (int k = 0; k < 16; k++)
            {
                double d = rays[(t * 16) + k];
                allRay.Add(d);
                if (d > mx)
                {
                    mx = d;
                    mk = k;
                }
            }

            maxRay.Add(mx);
            Vec3d e = EyeOf(spots[t]);
            longest.Add((e, e.Add(SpaceSampler.Directions[mk].Scale(mx)), mx));
        }

        LongestSightlines.AddRange(longest.OrderByDescending(x => x.Item3).Take(10));

        var pairs = new List<(Vec3d A, Vec3d B, double Dist)>(SightPairs);
        for (int t = 0; t < SightPairs; t++)
        {
            int a = play[_rng.Next(play.Count)], b = play[_rng.Next(play.Count)];
            if (a == b)
            {
                continue;
            }

            Vec3d ea = EyeOf(a), eb = EyeOf(b);
            pairs.Add((ea, eb, ea.Sub(eb).Length));
        }

        var los = new bool[pairs.Count];
        For(pairs.Count, k => los[k] = Los(pairs[k].A, pairs[k].B));
        var vis = new List<double>();
        int seen = 0, seenNear = 0, pairsNear = 0;
        for (int k = 0; k < pairs.Count; k++)
        {
            if (pairs[k].Dist <= 40)
            {
                pairsNear++;
                if (los[k])
                {
                    seenNear++;
                }
            }

            if (los[k])
            {
                seen++;
                vis.Add(pairs[k].Dist);
            }
        }

        var expoSpots = new List<Vec3d>(ExposureSpots);
        var expoDraws = new List<List<(Vec3d, Vec3d)>>(ExposureSpots);
        for (int t = 0; t < ExposureSpots; t++)
        {
            // Each spot's index is drawn, then its targets: the same stream as measuring one spot at a time.
            int i = play[_rng.Next(play.Count)];
            expoSpots.Add(_s[i].P);
            expoDraws.Add(DrawExposure(_s[i].P, 50, 60));
        }

        double[] expoValues = EvaluateExposure(expoDraws);
        for (int t = 0; t < expoSpots.Count; t++)
        {
            ExposureSamples.Add((expoSpots[t], expoValues[t]));
        }

        _sightReport = new JsonObject
        {
            ["longest_ray_per_spot"] = Dist(maxRay),
            ["ray_length"] = Dist(allRay),
            ["pair_visible_fraction"] = R(seen / (double)Math.Max(1, pairs.Count)),
            ["pair_visible_fraction_within40"] = R(seenNear / (double)Math.Max(1, pairsNear)),
            ["engagement_distance"] = Dist(vis),
            ["exposure50"] = Dist(expoValues),
            ["exposed_area_fraction"] = R(expoValues.Count(x => x > 0.35) / (double)Math.Max(1, expoValues.Length)),
        };
        return _sightReport;
    }

    // ---- stable sampling ------------------------------------------------------------------------------

    /// <summary>Headroom above a standing eye (m): an eye point needs at least <see cref="Eye"/> plus this under the ceiling.</summary>
    public const double EyeClearance = 0.1;

    /// <summary>Stable sampling: sightline and exposure spots, points paired for the visible-pair shares, pairs over any distance and within 40 m, exposure targets per spot.</summary>
    public const int StableSpots = 1200, StablePairPoints = 4000, StablePairs = 32000, StableNearPairs = 16000, StableExposureTargets = 120;

    private List<int>? _eyeIdx;
    private List<int>? _stableOrder;
    private ulong[]? _keys;

    /// <summary>
    /// The playable samples sightlines and exposure are measured from and to: with <see cref="SpatialOptions.StandableFloor"/>
    /// those where a standing eye is below the ceiling (headroom at least <see cref="Eye"/> + <see cref="EyeClearance"/>),
    /// else every playable sample (the study).
    /// </summary>
    internal List<int> EyeIdx => _eyeIdx ??= _o.StandableFloor
        ? _w.PlayIdx.Where(i => _s[i].Headroom >= Eye + EyeClearance).ToList()
        : _w.PlayIdx;

    /// <summary>
    /// The eye samples in stable order: by a seeded hash of the lattice position (cell and floor height), so a sample keeps
    /// its rank when floor elsewhere changes.
    /// </summary>
    internal List<int> StableOrder
    {
        get
        {
            if (_stableOrder is null)
            {
                List<int> eyes = EyeIdx;
                var keys = new ulong[_s.Count];
                foreach (int i in eyes)
                {
                    SpaceSampler.Sample s = _s[i];
                    keys[i] = Mix(Mix(Mix((ulong)(uint)_o.Seed ^ 0x9E3779B97F4A7C15UL, (uint)s.I), (uint)s.K), (uint)(int)Math.Round(s.P.Y * 4));
                }

                _keys = keys;
                _stableOrder = eyes.OrderBy(i => keys[i]).ThenBy(i => i).ToList();
            }

            return _stableOrder;
        }
    }

    /// <summary>A 64-bit mix (splitmix64's finaliser over the running value plus the next word).</summary>
    private static ulong Mix(ulong h, ulong v)
    {
        ulong z = h + v + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A pair's stable selection draw in [0, 1), symmetric in its two samples.</summary>
    private double PairDraw(int a, int b)
    {
        ulong ka = _keys![a], kb = _keys[b];
        return (Mix(Math.Min(ka, kb), Math.Max(ka, kb)) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>
    /// Stable exposure targets of a floor point: the eyes over the first <paramref name="n"/> samples of
    /// <see cref="StableOrder"/> within <paramref name="maxDist"/> of its eye (never <paramref name="self"/>).
    /// </summary>
    private List<(Vec3d Eye, Vec3d Target)> DrawStable(Vec3d p, double maxDist, int n, int self = -1)
    {
        var pairs = new List<(Vec3d, Vec3d)>(n);
        Vec3d e = p.Add(new Vec3d(0, Eye, 0));
        double r2 = maxDist * maxDist;
        foreach (int j in StableOrder)
        {
            if (j == self)
            {
                continue;
            }

            Vec3d q = EyeOf(j);
            double dx = q.X - e.X, dy = q.Y - e.Y, dz = q.Z - e.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= r2)
            {
                pairs.Add((e, q));
                if (pairs.Count >= n)
                {
                    break;
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// The sight report with stable sampling (<see cref="SpatialOptions.StableSampling"/>): the study's measures — the
    /// longest of 16 rays per spot, the visible share of point pairs (any distance, and within 40 m) with the distances of
    /// the visible ones, and exposure within 50 m — on samples chosen by <see cref="StableOrder"/>: the first
    /// <see cref="StableSpots"/> spots; pairs among the first <see cref="StablePairPoints"/> points, each kept by its own
    /// hash draw (about <see cref="StablePairs"/> over any distance and <see cref="StableNearPairs"/> within 40 m); and
    /// <see cref="StableExposureTargets"/> targets per exposure spot. The random stream still advances as the study's draws
    /// advance it, so the later sampled sections see the stream they always did.
    /// </summary>
    private JsonObject StableSightReport()
    {
        List<int> play = EyeIdx;
        int mr = Math.Min(SightSpots, play.Count);
        for (int t = 0; t < mr; t++)
        {
            _rng.Next(play.Count);
        }

        for (int t = 0; t < SightPairs; t++)
        {
            _rng.Next(play.Count);
            _rng.Next(play.Count);
        }

        for (int t = 0; t < ExposureSpots; t++)
        {
            _ = DrawExposure(_s[play[_rng.Next(play.Count)]].P, 50, 60);
        }

        List<int> order = StableOrder;
        int m = Math.Min(StableSpots, order.Count);
        var rays = new double[m * 16];
        For(m, t =>
        {
            Vec3d e = EyeOf(order[t]);
            for (int k = 0; k < 16; k++)
            {
                rays[(t * 16) + k] = _sp.Cast(e, SpaceSampler.Directions[k], 200, SpaceSampler.HitFilter.Any) ?? 200;
            }
        });
        var maxRay = new List<double>(m);
        var allRay = new List<double>(m * 16);
        var longest = new List<(Vec3d, Vec3d, double)>();
        for (int t = 0; t < m; t++)
        {
            double mx = 0;
            int mk = 0;
            for (int k = 0; k < 16; k++)
            {
                double d = rays[(t * 16) + k];
                allRay.Add(d);
                if (d > mx)
                {
                    mx = d;
                    mk = k;
                }
            }

            maxRay.Add(mx);
            Vec3d e = EyeOf(order[t]);
            longest.Add((e, e.Add(SpaceSampler.Directions[mk].Scale(mx)), mx));
        }

        LongestSightlines.AddRange(longest.OrderByDescending(x => x.Item3).ThenBy(x => x.Item1.X).ThenBy(x => x.Item1.Z).Take(10));

        // Pairs among the first points, each kept by its own draw.
        int q = Math.Min(StablePairPoints, order.Count);
        var eyes = new Vec3d[q];
        for (int a = 0; a < q; a++)
        {
            eyes[a] = EyeOf(order[a]);
        }

        long all = (long)q * (q - 1) / 2, near = 0;
        for (int a = 0; a < q; a++)
        {
            for (int b = a + 1; b < q; b++)
            {
                if (eyes[a].Sub(eyes[b]).Length <= 40)
                {
                    near++;
                }
            }
        }

        double pAll = all > 0 ? Math.Min(1, StablePairs / (double)all) : 0;
        double pNear = near > 0 ? Math.Min(1, StableNearPairs / (double)near) : 0;
        var pairs = new List<(int A, int B, double Dist, bool All, bool Near)>();
        for (int a = 0; a < q; a++)
        {
            for (int b = a + 1; b < q; b++)
            {
                double d = eyes[a].Sub(eyes[b]).Length;
                double u = PairDraw(order[a], order[b]);
                bool inAll = u < pAll, inNear = d <= 40 && u < pNear;
                if (inAll || inNear)
                {
                    pairs.Add((a, b, d, inAll, inNear));
                }
            }
        }

        var los = new bool[pairs.Count];
        For(pairs.Count, k => los[k] = Los(eyes[pairs[k].A], eyes[pairs[k].B]));
        var vis = new List<double>();
        int allCount = 0, seen = 0, nearCount = 0, seenNear = 0;
        for (int k = 0; k < pairs.Count; k++)
        {
            if (pairs[k].All)
            {
                allCount++;
                if (los[k])
                {
                    seen++;
                    vis.Add(pairs[k].Dist);
                }
            }

            if (pairs[k].Near)
            {
                nearCount++;
                if (los[k])
                {
                    seenNear++;
                }
            }
        }

        // Exposure: the first spots, each against its first targets within 50 m.
        var drawn = new List<(Vec3d Eye, Vec3d Target)>[m];
        For(m, t => drawn[t] = DrawStable(_s[order[t]].P, 50, StableExposureTargets, order[t]));
        double[] expoValues = EvaluateExposure(drawn.ToList());
        for (int t = 0; t < m; t++)
        {
            ExposureSamples.Add((_s[order[t]].P, expoValues[t]));
        }

        return new JsonObject
        {
            ["longest_ray_per_spot"] = Dist(maxRay),
            ["ray_length"] = Dist(allRay),
            ["pair_visible_fraction"] = R(seen / (double)Math.Max(1, allCount)),
            ["pair_visible_fraction_within40"] = R(seenNear / (double)Math.Max(1, nearCount)),
            ["engagement_distance"] = Dist(vis),
            ["exposure50"] = Dist(expoValues),
            ["exposed_area_fraction"] = R(expoValues.Count(x => x > 0.35) / (double)Math.Max(1, expoValues.Length)),
            ["sampling"] = "stable",
        };
    }

    // ---- spawns --------------------------------------------------------------------------------------

    private JsonObject? _spawnReport;

    /// <summary>Per playable multiplayer spawn: uid, position, nearest spawn, exposure, distance to the nearest item, visible spawns within 30 m (on a payload map only those live in the same checkpoint stage).</summary>
    internal List<(int Uid, Vec3d P, double Nearest, double Exposure, double ToItem, List<int> Sees)> SpawnDetails { get; } = new();

    private JsonObject SpawnReport()
    {
        if (_spawnReport is not null)
        {
            return _spawnReport;
        }

        SightReport(); // fixed order of the sampled sections
        var sp = _obj.Spawns.Select((s, i) => (s, i)).Where(x => x.s.Kind == "mp" && _w.SpawnSample[x.i] >= 0 && _w.Play[_w.SpawnSample[x.i]]).ToList();
        var nn = new List<double>();
        var pairList = new List<(int A, int B)>();
        var nearest = new Dictionary<int, double>();
        foreach (var a in sp)
        {
            double best = double.MaxValue;
            foreach (var b in sp)
            {
                if (a.i == b.i)
                {
                    continue;
                }

                double d = a.s.P.Sub(b.s.P).Length;
                best = Math.Min(best, d);
                if (a.i < b.i && d <= 30)
                {
                    pairList.Add((a.i, b.i));
                }
            }

            nearest[a.i] = best;
            if (best < double.MaxValue)
            {
                nn.Add(best);
            }
        }

        var pairLos = new bool[pairList.Count];
        For(pairList.Count, k => pairLos[k] = Los(EyeOf(_w.SpawnSample[pairList[k].A]), EyeOf(_w.SpawnSample[pairList[k].B])));
        int losPairs = pairLos.Count(x => x);

        // Pairs live at the same time: on a payload map a respawn a checkpoint links is live only while that checkpoint
        // is the cart's target, so two groups of different stages never spawn together (every pair elsewhere).
        bool staged = SpawnStages() is not null;
        var live = new bool[pairList.Count];
        for (int k = 0; k < pairList.Count; k++)
        {
            live[k] = !staged || LiveTogether(_obj.Spawns[pairList[k].A].Uid, _obj.Spawns[pairList[k].B].Uid);
        }

        int livePairs = live.Count(x => x), liveLos = Enumerable.Range(0, pairList.Count).Count(k => live[k] && pairLos[k]);
        List<List<(Vec3d Eye, Vec3d Target)>> spawnDraws = sp.Select(x => DrawExposure(_s[_w.SpawnSample[x.i]].P, SpawnExposureRadius, SpawnExposureTargets)).ToList();
        if (_o.StableSampling)
        {
            // The draws above keep the random stream where the later sections expect it; the measure uses stable targets.
            spawnDraws = sp.Select(x => DrawStable(_s[_w.SpawnSample[x.i]].P, SpawnExposureRadius, SpawnExposureTargets, _w.SpawnSample[x.i])).ToList();
        }

        double[] expo = EvaluateExposure(spawnDraws);
        List<double> toItem = sp.Select(x => _obj.Items.Count == 0 ? double.NaN : _obj.Items.Min(it => it.P.Sub(x.s.P).Length)).ToList();
        for (int k = 0; k < sp.Count; k++)
        {
            int si = sp[k].i;
            var sees = new List<int>();
            for (int p = 0; p < pairList.Count; p++)
            {
                if (pairLos[p] && live[p] && (pairList[p].A == si || pairList[p].B == si))
                {
                    sees.Add(_obj.Spawns[pairList[p].A == si ? pairList[p].B : pairList[p].A].Uid);
                }
            }

            SpawnDetails.Add((sp[k].s.Uid, sp[k].s.P, nearest[si], expo[k], toItem[k], sees));
        }

        _spawnReport = new JsonObject
        {
            ["count"] = sp.Count,
            ["nn"] = Dist(nn),
            ["los_pairs_within30_fraction"] = pairList.Count > 0 ? R(losPairs / (double)pairList.Count) : null,
            ["los_pairs_within30_live_fraction"] = livePairs > 0 ? R(liveLos / (double)livePairs) : null,
            ["pairs_within30"] = pairList.Count,
            ["pairs_within30_live"] = livePairs,
            ["staged"] = staged,
            ["exposure40"] = Dist(expo),
            ["to_nearest_item"] = Dist(toItem),
            ["area_per_spawn"] = sp.Count > 0 ? R(_w.PlayArea / sp.Count) : null,
        };
        return _spawnReport;
    }

    // ---- items -----------------------------------------------------------------------------------

    private JsonObject? _itemReport;
    private double[]? _traffic;
    private double[]? _trafficSorted;

    /// <summary>Per item: uid, class, category, position, traffic percentile (−1 = off the floor), in a pocket, nearest weapon/powerup (NaN when not one).</summary>
    internal List<(int Uid, string Cls, string Cat, Vec3d P, double Traffic, bool Pocket, double MajorNearest)> ItemDetails { get; } = new();

    /// <summary>The 8 m cell graph's traffic per node (shortest paths through it, 400 random pairs).</summary>
    internal double[] Traffic
    {
        get
        {
            ItemReport();
            return _traffic!;
        }
    }

    /// <summary>A traffic value's percentile among the 8 m cell nodes.</summary>
    internal double TrafficPercentile(double t) => _trafficSorted is not { Length: > 0 } s ? double.NaN : s.Count(x => x < t) / (double)s.Length;

    private JsonObject ItemReport()
    {
        if (_itemReport is not null)
        {
            return _itemReport;
        }

        SpawnReport(); // fixed order of the sampled sections
        CellGraph cg = Cell8;
        SpaceGraph sg = Spaces;
        _traffic = cg.Traffic(400, _rng);
        _trafficSorted = _traffic.OrderBy(x => x).ToArray();
        var byCat = new Dictionary<string, List<double>>();
        var pocketCat = new Dictionary<string, int>();
        var catN = new Dictionary<string, int>();
        var major = _obj.Items.Where(i => i.Cat is "weapon" or "powerup").Select(i => i.P).ToList();

        // Spread: nearest-neighbour distance among weapons and powerups (an item at the same spot as another counts once each).
        List<double> nn = major.Select((p, i) => major.Where((q, j) => j != i).Select(q => q.Sub(p).Length).DefaultIfEmpty(double.NaN).Min()).ToList();
        int mi = 0;
        foreach (var it in _obj.Items)
        {
            int s = _w.SnapPlay(it.P);
            int n = s >= 0 ? cg.NodeOfSample[s] : -1;
            double pct = n >= 0 ? TrafficPercentile(_traffic[n]) : -1;
            bool pocket = s >= 0 && n >= 0 && sg.InPocket(s);
            double mn = it.Cat is "weapon" or "powerup" ? nn[mi++] : double.NaN;
            ItemDetails.Add((it.Uid, it.Cls, it.Cat, it.P, pct, pocket, mn));
            if (s < 0 || n < 0)
            {
                continue;
            }

            if (!byCat.TryGetValue(it.Cat, out List<double>? l))
            {
                byCat[it.Cat] = l = new List<double>();
            }

            l.Add(pct);
            catN[it.Cat] = catN.GetValueOrDefault(it.Cat) + 1;
            if (pocket)
            {
                pocketCat[it.Cat] = pocketCat.GetValueOrDefault(it.Cat) + 1;
            }
        }

        var o = new JsonObject { ["count"] = _obj.Items.Count, ["per100"] = R(_obj.Items.Count * 100 / _w.PlayArea) };
        var cats = new JsonObject();
        foreach (KeyValuePair<string, List<double>> kv in byCat)
        {
            cats[kv.Key] = new JsonObject
            {
                ["n"] = kv.Value.Count,
                ["traffic_pct_p50"] = R(Med(kv.Value)),
                ["in_pocket_fraction"] = R(pocketCat.GetValueOrDefault(kv.Key) / (double)catN[kv.Key]),
            };
        }

        o["by_category"] = cats;
        o["major_nn"] = Dist(nn);
        _itemReport = o;
        return o;
    }
}
