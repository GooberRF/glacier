using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

// The space graph report, cover, routes (pair interconnection, CTF, hills).
public sealed partial class SpatialAnalysis
{
    // ---- spaces ------------------------------------------------------------------------------------

    /// <summary>One space's shape summary (for archetypes and the area report).</summary>
    internal sealed record SpaceShape(int Node, double Area, double Length, double Width, double Rect, double Sky, double HeadroomP50, double FloorSpan, double HeadroomIqr, string? Archetype);

    private List<SpaceShape>? _spaceShapes;

    /// <summary>Per-space shape and archetype (spaces of ≥ 30 m² get an archetype), largest first.</summary>
    internal List<SpaceShape> SpaceShapes()
    {
        if (_spaceShapes is not null)
        {
            return _spaceShapes;
        }

        SpaceGraph sg = Spaces;
        var list = new List<SpaceShape>();
        foreach (int sp in Enumerable.Range(0, sg.N).Where(i => sg.IsSpace[i]).OrderByDescending(i => sg.Area[i]))
        {
            List<int> m = sg.Members[sp];
            double a = sg.Area[sp];
            (double len, double wid) = PcaExtent(m.Select(i => _s[i].P));
            double obb = Math.Max(1e-6, (len + _g) * (wid + _g));
            double rect = Math.Min(1, a / obb);
            double[] hr = m.Where(i => !_s[i].OpenSky).Select(i => _s[i].Headroom).OrderBy(x => x).ToArray();
            double sky = m.Count(i => _s[i].OpenSky) / (double)m.Count;
            double hmed = hr.Length > 0 ? Percentiles.Type7(hr, 0.5) : double.NaN;
            double[] ys = m.Select(i => _s[i].P.Y).OrderBy(x => x).ToArray();
            double span = Percentiles.Type7(ys, 0.95) - Percentiles.Type7(ys, 0.05);
            double hriqr = hr.Length > 4 ? Percentiles.Type7(hr, 0.75) - Percentiles.Type7(hr, 0.25) : 0;
            string? key = null;
            if (a >= 30)
            {
                string size = a < 60 ? "S" : a < 200 ? "M" : a < 600 ? "L" : "XL";
                string height = sky > 0.5 ? "sky" : hmed < 4 ? "low" : hmed < 8 ? "mid" : "tall";
                string shape = rect >= 0.85 ? "box" : rect >= 0.65 ? "shaped" : "irregular";
                string lev = span >= 2 ? "multi" : "flat";
                string ceil = hriqr >= 1.0 ? "varceil" : "flatceil";
                key = $"{size}/{height}/{shape}/{lev}/{ceil}";
            }

            list.Add(new SpaceShape(sp, a, len, wid, rect, sky, hmed, span, hriqr, key));
        }

        _spaceShapes = list;
        return list;
    }

    private JsonObject SpacesReport()
    {
        SpaceGraph sg = Spaces;
        double total = sg.Area.Sum();
        CutReport cuts = sg.Cuts;
        List<int> spaces = Enumerable.Range(0, sg.N).Where(i => sg.IsSpace[i]).ToList();
        int v = sg.N, e = sg.Graph.EdgeCount, c = sg.Graph.Components();
        var arr = new JsonArray();
        var arche = new Dictionary<string, double>();
        var rects = new List<(double R, double A)>();
        foreach (SpaceShape sh in SpaceShapes())
        {
            if (sh.Archetype is { } key)
            {
                rects.Add((sh.Rect, sh.Area));
                arche[key] = arche.GetValueOrDefault(key) + sh.Area;
            }

            if (arr.Count < 12)
            {
                // A long view across the space: from one end of its long axis to the other, at eye height.
                List<int> m = sg.Members[sh.Node];
                double mx = m.Average(i => _s[i].P.X), mz = m.Average(i => _s[i].P.Z);
                double sxx = m.Sum(i => (_s[i].P.X - mx) * (_s[i].P.X - mx)), szz = m.Sum(i => (_s[i].P.Z - mz) * (_s[i].P.Z - mz)), sxz = m.Sum(i => (_s[i].P.X - mx) * (_s[i].P.Z - mz));
                double th = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
                var proj = m.Select(i => (I: i, U: ((_s[i].P.X - mx) * Math.Cos(th)) + ((_s[i].P.Z - mz) * Math.Sin(th)))).OrderBy(x => x.U).ToList();
                Vec3d from = _s[proj[(int)(proj.Count * 0.04)].I].P;
                Vec3d to = _s[proj[(int)(proj.Count * 0.96)].I].P;
                arr.Add(new JsonObject
                {
                    ["cam"] = FormattableString.Invariant($"{from.X:0.0},{from.Y + 1.7:0.0},{from.Z:0.0}"),
                    ["look"] = FormattableString.Invariant($"{to.X:0.0},{to.Y + 1.7:0.0},{to.Z:0.0}"),
                    ["area"] = Math.Round(sh.Area, 1), ["len"] = Math.Round(sh.Length, 1), ["wid"] = Math.Round(sh.Width, 1), ["rect"] = Math.Round(sh.Rect, 2),
                    ["headroom_p50"] = double.IsNaN(sh.HeadroomP50) ? null : Math.Round(sh.HeadroomP50, 1), ["sky"] = Math.Round(sh.Sky, 2), ["floor_span"] = Math.Round(sh.FloorSpan, 1),
                    ["degree"] = sg.Graph.Adj[sh.Node].Count,
                    ["c"] = FormattableString.Invariant($"{sg.Cent[sh.Node].X:0},{sg.Cent[sh.Node].Y:0},{sg.Cent[sh.Node].Z:0}"),
                });
            }
        }

        double at = arche.Values.Sum();
        double h = at > 0 ? -arche.Values.Sum(x => x / at * Math.Log(x / at)) : 0;
        double rectW = 0;
        if (rects.Count > 0)
        {
            List<(double R, double A)> srt = rects.OrderBy(r => r.R).ToList();
            double tw = srt.Sum(r => r.A), acc = 0;
            foreach ((double r, double a) in srt)
            {
                acc += a;
                if (acc >= tw / 2)
                {
                    rectW = r;
                    break;
                }
            }
        }

        int leafSpaces = spaces.Count(i => sg.Graph.Adj[i].Count <= 1);
        int leafConn = Enumerable.Range(0, sg.N).Count(i => !sg.IsSpace[i] && sg.Graph.Adj[i].Count == 1 && sg.Area[i] >= 12);
        return new JsonObject
        {
            ["nodes"] = sg.N, ["spaces"] = spaces.Count, ["spaces_ge30"] = rects.Count, ["connectors"] = sg.N - spaces.Count,
            ["loops"] = e - v + c,
            ["loops_per1000m2"] = Math.Round((e - v + c) / (total / 1000), 3),
            ["dead_end_spaces"] = leafSpaces, ["dead_end_connectors"] = leafConn,
            ["choke_nodes_10pct"] = cuts.Chokes.Count,
            ["pocket_area_fraction"] = Math.Round(cuts.PocketArea, 3),
            ["space_area_fraction"] = Math.Round(spaces.Sum(i => sg.Area[i]) / total, 3),
            ["largest_space_fraction"] = spaces.Count > 0 ? Math.Round(spaces.Max(i => sg.Area[i]) / total, 3) : 0,
            ["space_area"] = Distr(spaces.Where(i => sg.Area[i] >= 30).Select(i => sg.Area[i])),
            ["rectangularity_areaweighted_median"] = Math.Round(rectW, 3),
            ["boxy_area_fraction"] = rects.Count > 0 ? Math.Round(rects.Where(r => r.R >= 0.85).Sum(r => r.A) / rects.Sum(r => r.A), 3) : null,
            ["archetypes"] = arche.Count,
            ["archetype_entropy"] = Math.Round(h, 3),
            ["archetype_list"] = new JsonArray(arche.OrderByDescending(k => k.Value).Take(10).Select(k => (JsonNode)FormattableString.Invariant($"{k.Key}:{k.Value:0}")).ToArray()),
            ["top"] = arr,
        };
    }

    private static JsonNode? Distr(IEnumerable<double> v)
    {
        double[] a = v.OrderBy(x => x).ToArray();
        if (a.Length == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["n"] = a.Length, ["p25"] = Math.Round(Percentiles.Type7(a, 0.25), 1), ["p50"] = Math.Round(Percentiles.Type7(a, 0.5), 1),
            ["p75"] = Math.Round(Percentiles.Type7(a, 0.75), 1), ["max"] = Math.Round(a[^1], 1),
        };
    }

    internal static (double Len, double Wid) PcaExtent(IEnumerable<Vec3d> pts)
    {
        List<Vec3d> a = pts.ToList();
        double mx = a.Average(p => p.X), mz = a.Average(p => p.Z);
        double sxx = 0, szz = 0, sxz = 0;
        foreach (Vec3d p in a)
        {
            sxx += (p.X - mx) * (p.X - mx);
            szz += (p.Z - mz) * (p.Z - mz);
            sxz += (p.X - mx) * (p.Z - mz);
        }

        double th = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
        double c = Math.Cos(th), s = Math.Sin(th);
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        foreach (Vec3d p in a)
        {
            double u = ((p.X - mx) * c) + ((p.Z - mz) * s), v = (-(p.X - mx) * s) + ((p.Z - mz) * c);
            u0 = Math.Min(u0, u);
            u1 = Math.Max(u1, u);
            v0 = Math.Min(v0, v);
            v1 = Math.Max(v1, v);
        }

        return (u1 - u0, v1 - v0);
    }

    // ---- cover -------------------------------------------------------------------------------------

    private bool[]? _cover;
    private double[]? _coverDist;
    private JsonObject? _coverReport;

    /// <summary>True for playable samples beside cover (low cover or a free-standing obstacle with floor behind it).</summary>
    internal bool[] CoverSamples
    {
        get
        {
            CoverReport();
            return _cover!;
        }
    }

    /// <summary>Walk distance from every sample to the nearest cover sample or clutter/mesh object (∞ = none reachable).</summary>
    internal double[] CoverDistance
    {
        get
        {
            CoverReport();
            return _coverDist!;
        }
    }

    private bool FloorBehind(Vec3d p, Vec3d dir, double d)
    {
        for (double t = d + 0.75; t <= d + 4.0; t += 0.5)
        {
            Vec3d q = p.Add(dir.Scale(t));
            int i = (int)Math.Floor(q.X / _g), k = (int)Math.Floor(q.Z / _g);
            if (!_w.Lattice.TryGetValue((i, k), out List<int>? l))
            {
                continue;
            }

            foreach (int j in l)
            {
                if (_w.Play[j] && Math.Abs(_s[j].P.Y - p.Y) <= 2.0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool CoverAt(int i)
    {
        Vec3d p = _s[i].P;
        Vec3d lo = p.Add(new Vec3d(0, 0.7, 0));
        Vec3d hi = p.Add(new Vec3d(0, Eye, 0));
        Span<double> dl = stackalloc double[16];
        Span<double> dh = stackalloc double[16];
        for (int k = 0; k < 16; k++)
        {
            dl[k] = _sp.Cast(lo, SpaceSampler.Directions[k], 10, SpaceSampler.HitFilter.Any) ?? 10;
            dh[k] = _sp.Cast(hi, SpaceSampler.Directions[k], 10, SpaceSampler.HitFilter.Any) ?? 10;
        }

        for (int k = 0; k < 16; k++)
        {
            Vec3d dir = SpaceSampler.Directions[k];
            // Low cover: blocked crouched, open standing, playable floor beyond it.
            if (dl[k] < 2.5 && dh[k] > dl[k] + 2.0 && FloorBehind(p, dir, dl[k]))
            {
                return true;
            }

            // Free-standing tall cover: blocked at eye height within 2.5 m with playable floor directly behind the
            // obstacle (a column, block or thin divider between walkable areas, not the room's outer wall).
            if (dh[k] < 2.5 && FloorBehind(p, dir, dh[k]))
            {
                return true;
            }
        }

        return false;
    }

    private JsonObject CoverReport()
    {
        if (_coverReport is not null)
        {
            return _coverReport;
        }

        _cover = new bool[_s.Count];
        List<int> play = _w.PlayIdx;
        var flags = new bool[play.Count];
        For(play.Count, j => flags[j] = CoverAt(play[j]));
        int coverN = 0;
        for (int j = 0; j < play.Count; j++)
        {
            if (flags[j])
            {
                _cover[play[j]] = true;
                coverN++;
            }
        }

        // Clutter and mesh objects count as cover points too.
        List<int> srcs = play.Where(i => _cover[i]).ToList();
        foreach (Vec3d c in _obj.ClutterPoints)
        {
            int s = _w.SnapPlay(c);
            if (s >= 0)
            {
                srcs.Add(s);
            }
        }

        double[] d = srcs.Count > 0 ? _w.Dijkstra(srcs) : Enumerable.Repeat(double.PositiveInfinity, _s.Count).ToArray();
        _coverDist = d;
        List<double> all = play.Select(i => Math.Min(d[i], 50)).ToList();
        List<double> open = play.Where(i => _s[i].Width >= 8).Select(i => Math.Min(d[i], 50)).ToList();
        _coverReport = new JsonObject
        {
            ["cover_sample_fraction"] = R(coverN / (double)play.Count),
            ["dist_to_cover"] = Dist(all),
            ["dist_to_cover_open"] = Dist(open),
            ["open_area_fraction"] = R(open.Count / (double)play.Count),
            ["far_from_cover_fraction"] = R(all.Count(x => x > 6) / (double)all.Count),
        };
        return _coverReport;
    }

    // ---- routes ------------------------------------------------------------------------------------

    private JsonObject? _routesReport;

    /// <summary>The capture-point hills: (position, radius) from each handler event's trigger.</summary>
    internal List<(Vec3d P, double Radius, int Uid)> HillPositions()
    {
        var res = new List<(Vec3d, double, int)>();
        Dictionary<int, LevelObject> byUid = _level.Objects.GroupBy(o => o.Uid).ToDictionary(g => g.Key, g => g.First());
        foreach (LevelObject o in _level.Objects.Where(o => o.Kind == LevelObjectKind.Event && (o.ClassName ?? string.Empty).Contains("Capture_Point", StringComparison.OrdinalIgnoreCase)))
        {
            Vec3d pos = SpatialObjects.V(o.Position);
            double rad = 5;
            List<int> links = (o.Model as RflEvent)?.Links ?? new List<int>();
            LevelObject? trig = links.Select(u => byUid.GetValueOrDefault(u)).FirstOrDefault(x => x?.Kind == LevelObjectKind.Trigger);
            trig ??= _level.Objects.FirstOrDefault(t => t.Kind == LevelObjectKind.Trigger && ((t.Model as Trigger)?.Links.Contains(o.Uid) ?? false));
            if (trig?.Model is Trigger tr)
            {
                pos = SpatialObjects.V(tr.Position);
                rad = tr.SphereRadius ?? (Math.Max(tr.BoxWidth ?? 5, tr.BoxDepth ?? 5) / 2);
            }

            if ((o.ClassName ?? string.Empty).Contains("Handler", StringComparison.OrdinalIgnoreCase))
            {
                res.Add((pos, rad, o.Uid));
            }
        }

        return res;
    }

    /// <summary>
    /// Exposure of a floor point: the share of up to <paramref name="n"/> random playable eye points within
    /// <paramref name="maxDist"/> that it can see.
    /// </summary>
    private double Exposure(Vec3d p, double maxDist, int n) => EvaluateExposure(new List<List<(Vec3d, Vec3d)>> { DrawExposure(p, maxDist, n) })[0];

    /// <summary>The random targets of one exposure measurement, drawn in order (at most 4n draws, n kept).</summary>
    private List<(Vec3d Eye, Vec3d Target)> DrawExposure(Vec3d p, double maxDist, int n, Random? rng = null)
    {
        rng ??= _rng;
        var pairs = new List<(Vec3d, Vec3d)>(n);
        Vec3d e = p.Add(new Vec3d(0, Eye, 0));
        for (int t = 0; t < n * 4 && pairs.Count < n; t++)
        {
            int j = EyeIdx[rng.Next(EyeIdx.Count)];
            Vec3d q = EyeOf(j);
            if (q.Sub(e).Length > maxDist)
            {
                continue;
            }

            pairs.Add((e, q));
        }

        return pairs;
    }

    /// <summary>The spawn-exposure radius (m) and target count of the spawn report (G19 / E8).</summary>
    internal const double SpawnExposureRadius = 40;

    /// <summary>The spawn report's target count per spawn.</summary>
    internal const int SpawnExposureTargets = 120;

    /// <summary>
    /// Spawn exposure exactly as the spawn report measures it (G19, E8: <c>exposure40</c>): the share of the playable eye
    /// points within 40 m that the eye 1.6 m over each floor sample in <paramref name="samples"/> sees. With
    /// <see cref="SpatialOptions.StableSampling"/> these are the report's own stable targets (the first
    /// <see cref="SpawnExposureTargets"/> of the stable order, so a candidate measures what the report will measure
    /// for a spawn standing there); otherwise random targets drawn from <paramref name="rngFor"/> (one stream per
    /// sample, so the value never depends on evaluation order).
    /// </summary>
    internal double[] SpawnExposure(IReadOnlyList<int> samples, Func<int, Random> rngFor) =>
        EvaluateExposure(_o.StableSampling
            ? samples.Select(s => DrawStable(_s[s].P, SpawnExposureRadius, SpawnExposureTargets, s)).ToList()
            : samples.Select((s, k) => DrawExposure(_s[s].P, SpawnExposureRadius, SpawnExposureTargets, rngFor(k))).ToList());

    /// <summary>The floor sample a spawn standing at <paramref name="p"/> is measured from (−1 = none), and whether it is playable.</summary>
    internal (int Sample, bool Playable) SpawnSampleOf(Vec3d p)
    {
        int s = _w.Snap(p);
        return (s, s >= 0 && _w.Play[s]);
    }

    /// <summary>The playable floor area (m²) whose samples satisfy <paramref name="inside"/>.</summary>
    internal double PlayableAreaWhere(Func<Vec3d, bool> inside) => _w.PlayIdx.Count(i => inside(_s[i].P)) * _g * _g;

    /// <summary>The floor point of a lattice sample.</summary>
    internal Vec3d SampleFloor(int sample) => _s[sample].P;

    /// <summary>Line of sight between the eyes (1.6 m) over two floor samples, as the spawn report tests spawn pairs.</summary>
    internal bool EyeLos(int a, int b) => Los(EyeOf(a), EyeOf(b));

    /// <summary>The visible share of each drawn target set (the rays run in parallel).</summary>
    private double[] EvaluateExposure(List<List<(Vec3d Eye, Vec3d Target)>> draws)
    {
        var flat = draws.SelectMany((d, i) => d.Select(x => (Point: i, x.Eye, x.Target))).ToList();
        var seen = new bool[flat.Count];
        For(flat.Count, k => seen[k] = Los(flat[k].Eye, flat[k].Target));
        var count = new int[draws.Count];
        for (int k = 0; k < flat.Count; k++)
        {
            if (seen[k])
            {
                count[flat[k].Point]++;
            }
        }

        return Enumerable.Range(0, draws.Count).Select(i => draws[i].Count > 0 ? count[i] / (double)draws[i].Count : 0).ToArray();
    }

    private double HillCover(Vec3d c, double r)
    {
        List<int> inside = _w.PlayIdx.Where(i =>
        {
            Vec3d p = _s[i].P;
            return Math.Abs(p.Y - c.Y) < 4 && Math.Sqrt(((p.X - c.X) * (p.X - c.X)) + ((p.Z - c.Z) * (p.Z - c.Z))) <= r;
        }).ToList();
        return inside.Count == 0 ? 0 : inside.Count(i => CoverSamples[i]) / (double)inside.Count;
    }

    /// <summary>The CTF flags (red, blue), when the level has both.</summary>
    internal ((Vec3d P, int Uid) Red, (Vec3d P, int Uid) Blue)? Flags()
    {
        var red = _obj.Items.Where(i => i.Cat == "flag" && i.Cls.Contains("red", StringComparison.OrdinalIgnoreCase)).ToList();
        var blue = _obj.Items.Where(i => i.Cat == "flag" && i.Cls.Contains("blue", StringComparison.OrdinalIgnoreCase)).ToList();
        return red.Count > 0 && blue.Count > 0 ? ((red[0].P, red[0].Uid), (blue[0].P, blue[0].Uid)) : null;
    }

    private JsonObject RoutesReport()
    {
        if (_routesReport is not null)
        {
            return _routesReport;
        }

        CoverReport(); // hill cover reads it; also fixes the sampled sections' order
        SpaceGraph sg = Spaces;
        CellGraph cg = Cell8;
        var o = new JsonObject();

        // Pair interconnection on the space graph: node-disjoint routes between spaces at least 25 m apart.
        List<int> bigSpaces = Enumerable.Range(0, sg.N).Where(i => sg.IsSpace[i] && sg.Area[i] >= 40).ToList();
        var conn = new List<double>();
        if (bigSpaces.Count >= 2)
        {
            for (int t = 0; t < 60; t++)
            {
                int a = bigSpaces[_rng.Next(bigSpaces.Count)], b = bigSpaces[_rng.Next(bigSpaces.Count)];
                if (a == b || sg.Cent[a].Sub(sg.Cent[b]).Length < 25)
                {
                    continue;
                }

                conn.Add(sg.Graph.DisjointCount(new[] { a }, new[] { b }, 6));
            }
        }

        o["space_pair_routes"] = Dist(conn);
        o["space_pair_routes_ge2_fraction"] = conn.Count > 0 ? R(conn.Count(c => c >= 2) / (double)conn.Count) : null;
        o["space_pair_routes_ge3_fraction"] = conn.Count > 0 ? R(conn.Count(c => c >= 3) / (double)conn.Count) : null;

        if (Flags() is { } flags)
        {
            var ctf = new JsonObject();
            int rs = _w.SnapPlay(flags.Red.P), bs = _w.SnapPlay(flags.Blue.P);
            ctf["flag_euclid"] = R(flags.Red.P.Sub(flags.Blue.P).Length);
            if (rs >= 0 && bs >= 0)
            {
                double[] dr = _w.Dijkstra(new[] { rs });
                double[] db = _w.Dijkstra(new[] { bs });
                ctf["flag_walk"] = R(dr[bs]);
                List<int> src = cg.NodesNear(flags.Red.P, 12);
                List<int> dst = cg.NodesNear(flags.Blue.P, 12);
                ctf["cell_routes"] = cg.Graph.DisjointCount(src, dst, 8);
                List<double> lens = cg.Graph.GreedyRoutes(src, dst, (u, v) => cg.Cent[u].Sub(cg.Cent[v]).Length, 8).OrderBy(x => x).ToList();
                ctf["cell_route_lengths"] = new JsonArray(lens.Select(x => (JsonNode)R(x)).ToArray());
                int ra = sg.NodeOf(rs), ba = sg.NodeOf(bs);
                if (ra >= 0 && ba >= 0 && ra != ba)
                {
                    ctf["space_routes"] = sg.Graph.DisjointCount(new[] { ra }, new[] { ba }, 8);
                    ctf["space_route_lengths"] = new JsonArray(sg.Graph.GreedyRoutes(new[] { ra }, new[] { ba }, (u, v) => sg.Cent[u].Sub(sg.Cent[v]).Length, 8).Select(x => (JsonNode)R(x)).ToArray());
                }

                if (lens.Count > 0)
                {
                    ctf["cell_routes_within_1_5x"] = lens.Count(x => x <= 1.5 * lens[0]);
                }

                List<double> TeamTo(int team, double[] dist) => _obj.Spawns.Select((s, i) => (s, i))
                    .Where(x => x.s.Team == team && _w.SpawnSample[x.i] >= 0).Select(x => dist[_w.SpawnSample[x.i]]).Where(double.IsFinite).ToList();
                List<double> rsp = TeamTo(1, db), bsp = TeamTo(2, dr);
                if (rsp.Count > 0 && bsp.Count > 0)
                {
                    double mr = Med(rsp), mb = Med(bsp);
                    ctf["red_spawn_to_blue_flag"] = R(mr);
                    ctf["blue_spawn_to_red_flag"] = R(mb);
                    ctf["spawn_flag_balance"] = R(Math.Min(mr, mb) / Math.Max(mr, mb));
                }

                List<double> ownR = TeamTo(1, dr);
                if (ownR.Count > 0)
                {
                    ctf["red_spawn_to_own_flag_p50"] = R(Med(ownR));
                }
            }

            o["ctf"] = ctf;
        }

        List<(Vec3d P, double Radius, int Uid)> hills = HillPositions();
        if (hills.Count > 0)
        {
            var arr = new JsonArray();
            foreach ((Vec3d hp, double radius, int uid) in hills)
            {
                int hs = _w.SnapPlay(hp);
                var ho = new JsonObject { ["pos"] = FormattableString.Invariant($"{hp.X:0},{hp.Y:0},{hp.Z:0}"), ["radius"] = R(radius), ["uid"] = uid };
                if (hs >= 0)
                {
                    List<int> near = cg.NodesNear(hp, Math.Max(6, radius));
                    List<int> far = Enumerable.Range(0, cg.N).Where(i => cg.Cent[i].Sub(hp).Length > 40).ToList();
                    ho["approaches_cell"] = far.Count > 0 ? cg.Graph.DisjointCount(near, far, 8) : 0;
                    int hn = sg.NodeOf(hs);
                    if (hn >= 0)
                    {
                        ho["space_degree"] = sg.Graph.Adj[hn].Count;
                    }

                    ho["exposure"] = R(Exposure(_s[hs].P, 60, 300));
                    ho["hill_cover_fraction"] = R(HillCover(hp, Math.Max(4, radius)));
                }

                arr.Add(ho);
            }

            o["hills"] = arr;
        }

        _routesReport = o;
        return o;
    }
}
