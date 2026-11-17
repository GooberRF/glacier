using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// The heights reading behind the heights overlay of plan and top renders (glacier-cli render --overlays heights,
// --mode heights): how the walkable floor changes height. It reads the same lattice, walk graph and flight readings as
// the function checks (SpatialAnalysis.Flights):
//   * flat floor: playable samples on level faces (slope under 1:20), neighbours within 0.1 m;
//   * ramps and slopes: playable samples on sloped faces (1:20 to 45 degrees) joined by walking, rising 0.75 m or more over 4 m² or more;
//   * stairs: the flights FindFlights reads (a run between landings that climbs at least 1 m on a plane) whose floor is
//     mostly level treads;
//   * drops: an edge where a floor region of 16 m² or more meets floor more than a step (0.6 m, FootStep) lower in the next
//     lattice cell, with open air between (no wall at head height, nothing between the two floors), in polylines of 1.5 m or more. Walkable steps,
//     ramps and stairs never make one: they rise at most a step from one lattice cell to the next.
public sealed partial class SpatialAnalysis
{
    /// <summary>A face flatter than this slope (rise per run, 1:20) is level floor.</summary>
    public const double LevelSlope = 0.05;

    /// <summary>A ramp or slope rises at least this much (m).</summary>
    public const double RampMinRise = 0.75;

    /// <summary>A ramp or slope covers at least this much floor (m²).</summary>
    public const double RampMinArea = 4;

    /// <summary>A flat floor region is reported from this area (m²).</summary>
    public const double FlatMinArea = 2;

    /// <summary>Drop polylines shorter than this (m) are left out.</summary>
    public const double DropMinLength = 1.5;

    /// <summary>A drop is marked only off a floor region (flat, ramp or stairs) of at least this area (m²).</summary>
    public const double DropMinHighArea = 16;

    /// <summary>The colour range covers the floor within this (m) of the lowest and highest stand point (see <see cref="HeightsReading.RangeMin"/>).</summary>
    public const double StandWindow = 8.0;

    /// <summary>Reads the heights (see the file header).</summary>
    internal HeightsReading ReadHeights()
    {
        int n = _s.Count;
        double g = _g;

        // The floor the reading uses: playable samples under a roof or the sky (a sample with nothing at all above it
        // stands on the outside of the level: the top of a trim flush with a ceiling).
        HUse = new bool[n];
        HIdx = new List<int>();
        foreach (int i in _w.PlayIdx)
        {
            if (!double.IsPositiveInfinity(_s[i].Headroom))
            {
                HUse[i] = true;
                HIdx.Add(i);
            }
        }

        // Per playable sample: the slope of the face it stands on and its uphill direction.
        var slope = new double[n];
        var upX = new double[n];
        var upZ = new double[n];
        For(n, i =>
        {
            if (!HUse[i])
            {
                return;
            }

            Vec3d p = _s[i].P;
            if (_sp.Cast(new Vec3d(p.X, p.Y + 0.05, p.Z), new Vec3d(0, -1, 0), 0.3, SpaceSampler.HitFilter.UpFacing, out int f) is not null && f >= 0)
            {
                Vec3d nn = _sp.Faces[f].N;
                double h = Math.Sqrt((nn.X * nn.X) + (nn.Z * nn.Z));
                slope[i] = nn.Y > 1e-6 ? h / nn.Y : 0;
                if (h > 1e-9)
                {
                    upX[i] = -nn.X / h;
                    upZ[i] = -nn.Z / h;
                }
            }
        });

        bool Level(int i) => slope[i] < LevelSlope;

        // Feature of every sample: kind (HeightsReading.Kind*) and index.
        var kind = new int[n];
        var feature = new int[n];
        Array.Fill(feature, -1);

        // ---- stairs: the flights read for the function checks whose floor is mostly level treads ----
        var stairs = new List<HeightsFlight>();
        foreach (FlightShape f in FindFlights())
        {
            int level = f.T.Count(Level);
            if (level < 0.6 * f.T.Count)
            {
                continue; // a ramp: read below from its sloped faces
            }

            int id = stairs.Count;
            foreach (int i in f.T)
            {
                kind[i] = HeightsReading.KindStairs;
                feature[i] = id;
            }

            double low = _flats![f.Bot].Y, high = f.Top >= 0 ? _flats[f.Top].Y : f.Ymax;
            (double lx, double lz) = Middle(f.T);
            var marks = new List<HeightsMark>
            {
                new(f.FootEdge.X, low, f.FootEdge.Z, f.Ux, f.Uz),
                new(f.TopEdge.X, high, f.TopEdge.Z, f.Ux, f.Uz),
            };
            stairs.Add(new HeightsFlight(id, "stairs", low, high, new Vec3d(f.FootEdge.X, low, f.FootEdge.Z), new Vec3d(f.TopEdge.X, high, f.TopEdge.Z),
                f.Ux, f.Uz, f.Run, f.Width, f.T.Count * g * g, lx, lz, marks));
        }

        // ---- flat floor: level playable samples within FlatTolerance of their neighbours ----
        var fu = new UnionFind(n);
        foreach (int i in HIdx)
        {
            if (!Level(i) || kind[i] != HeightsReading.KindNone)
            {
                continue;
            }

            foreach (WalkEdge e in _w.Adj[i])
            {
                int j = e.To;
                if (!e.Link && !e.Drop && HUse[j] && Level(j) && kind[j] == HeightsReading.KindNone && Math.Abs(_s[j].P.Y - _s[i].P.Y) <= FlatTolerance)
                {
                    fu.Union(i, j);
                }
            }
        }

        var flats = new List<HeightsFlat>();
        foreach (List<int> t in HIdx.Where(i => Level(i) && kind[i] == HeightsReading.KindNone).GroupBy(i => fu.Find(i)).Select(gr => gr.ToList())
                     .OrderByDescending(t => t.Count).ThenBy(t => t[0]))
        {
            double area = t.Count * g * g;
            if (area < FlatMinArea)
            {
                continue;
            }

            int id = flats.Count;
            foreach (int i in t)
            {
                kind[i] = HeightsReading.KindFlat;
                feature[i] = id;
            }

            (double lx, double lz) = Middle(t);
            flats.Add(new HeightsFlat(id, Med(t.Select(i => _s[i].P.Y)), area, lx, lz));
        }

        // ---- ramps and slopes: sloped playable floor joined by walking ----
        var uf = new UnionFind(n);
        foreach (int i in HIdx)
        {
            if (Level(i) || kind[i] == HeightsReading.KindStairs || slope[i] > 1.3)
            {
                continue;
            }

            foreach (WalkEdge e in _w.Adj[i])
            {
                int j = e.To;
                if (!e.Link && !e.Drop && HUse[j] && !Level(j) && kind[j] != HeightsReading.KindStairs && slope[j] <= 1.3
                    && Math.Abs(_s[j].P.Y - _s[i].P.Y) <= FootStep)
                {
                    uf.Union(i, j);
                }
            }
        }

        var ramps = new List<HeightsFlight>();
        foreach (List<int> t in HIdx.Where(i => !Level(i) && kind[i] != HeightsReading.KindStairs && slope[i] <= 1.3)
                     .GroupBy(i => uf.Find(i)).Select(gr => gr.ToList()))
        {
            _ct.ThrowIfCancellationRequested();
            double area = t.Count * g * g;
            double ylo = t.Min(i => _s[i].P.Y), yhi = t.Max(i => _s[i].P.Y);
            if (area < RampMinArea || yhi - ylo < RampMinRise)
            {
                continue;
            }

            // The climb: the mean uphill direction of its faces.
            double sx = t.Sum(i => upX[i]), sz = t.Sum(i => upZ[i]);
            double sl = Math.Sqrt((sx * sx) + (sz * sz));
            if (sl < 1e-6)
            {
                continue;
            }

            double ux = sx / sl, uz = sz / sl;
            double s0 = double.MaxValue, s1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (int i in t)
            {
                double s = (_s[i].P.X * ux) + (_s[i].P.Z * uz), v = (-_s[i].P.X * uz) + (_s[i].P.Z * ux);
                s0 = Math.Min(s0, s);
                s1 = Math.Max(s1, s);
                v0 = Math.Min(v0, v);
                v1 = Math.Max(v1, v);
            }

            // Its low and high floor: the flat floor its lowest and highest samples step onto (within a quarter metre),
            // else the samples' own extremes carried half a lattice step on along the climb.
            double ylo0 = ylo, yhi0 = yhi;
            double slopeMean = t.Average(i => slope[i]);
            double? landLo = null, landHi = null;
            foreach (int i in t)
            {
                foreach (WalkEdge e in _w.Adj[i])
                {
                    int j = e.To;
                    if (e.Link || e.Drop || kind[j] != HeightsReading.KindFlat)
                    {
                        continue;
                    }

                    double fy = flats[feature[j]].Y;
                    if (Math.Abs(_s[i].P.Y - ylo0) <= 0.3 && Math.Abs(fy - ylo0) <= 0.25 + (slopeMean * g) && (landLo is null || fy < landLo))
                    {
                        landLo = fy;
                    }

                    if (Math.Abs(_s[i].P.Y - yhi0) <= 0.3 && Math.Abs(fy - yhi0) <= 0.25 + (slopeMean * g) && (landHi is null || fy > landHi))
                    {
                        landHi = fy;
                    }
                }
            }

            ylo = landLo ?? (ylo0 - (slopeMean * g / 2));
            yhi = landHi ?? (yhi0 + (slopeMean * g / 2));

            // Foot and top: the lowest and highest tenth of its floor, half a lattice step out along the climb.
            double cut = 0.1 * (yhi0 - ylo0);
            List<int> footSet = t.Where(i => _s[i].P.Y <= ylo0 + cut).ToList(), topSet = t.Where(i => _s[i].P.Y >= yhi0 - cut).ToList();
            var foot = new Vec3d(footSet.Average(i => _s[i].P.X) - (ux * g / 2), ylo, footSet.Average(i => _s[i].P.Z) - (uz * g / 2));
            var top = new Vec3d(topSet.Average(i => _s[i].P.X) + (ux * g / 2), yhi, topSet.Average(i => _s[i].P.Z) + (uz * g / 2));

            // Chevron marks: one per 2.5 m band along the climb, at the sample nearest the band's middle across it.
            var marks = new List<HeightsMark>();
            double run = s1 - s0 + g;
            int bands = Math.Max(1, (int)Math.Round(run / 2.5));
            double bw = run / bands;
            for (int b = 0; b < bands; b++)
            {
                double lo = s0 - (g / 2) + (b * bw), hi = lo + bw;
                List<int> inBand = t.Where(i => { double s = (_s[i].P.X * ux) + (_s[i].P.Z * uz); return s >= lo && s < hi; }).ToList();
                if (inBand.Count == 0)
                {
                    continue;
                }

                double vm = SpaceSampler.Median(inBand.Select(i => (-_s[i].P.X * uz) + (_s[i].P.Z * ux)));
                double sm = (lo + hi) / 2;
                int best = inBand.OrderBy(i =>
                {
                    double s = (_s[i].P.X * ux) + (_s[i].P.Z * uz), v = (-_s[i].P.X * uz) + (_s[i].P.Z * ux);
                    return ((s - sm) * (s - sm)) + ((v - vm) * (v - vm));
                }).ThenBy(i => i).First();
                double bx = inBand.Sum(i => upX[i]), bz = inBand.Sum(i => upZ[i]), bl = Math.Sqrt((bx * bx) + (bz * bz));
                marks.Add(new HeightsMark(_s[best].P.X, _s[best].P.Y, _s[best].P.Z, bl > 1e-6 ? bx / bl : ux, bl > 1e-6 ? bz / bl : uz));
            }

            int id = ramps.Count;
            foreach (int i in t)
            {
                kind[i] = HeightsReading.KindRamp;
                feature[i] = id;
            }

            (double lx, double lz) = Middle(t);
            ramps.Add(new HeightsFlight(id, "ramp", ylo, yhi, foot, top, ux, uz, s1 - s0 + g, v1 - v0 + g, area, lx, lz, marks));
        }

        // ---- drops ----
        List<HeightsDrop> drops = ReadDrops(i => kind[i] switch
        {
            HeightsReading.KindFlat => flats[feature[i]].Area,
            HeightsReading.KindRamp => ramps[feature[i]].Area,
            HeightsReading.KindStairs => stairs[feature[i]].Area,
            _ => 0,
        });

        double min = double.MaxValue, max = double.MinValue;
        Vec3d minAt = default, maxAt = default;
        foreach (int i in HIdx)
        {
            if (_s[i].P.Y < min)
            {
                (min, minAt) = (_s[i].P.Y, _s[i].P);
            }

            if (_s[i].P.Y > max)
            {
                (max, maxAt) = (_s[i].P.Y, _s[i].P);
            }
        }

        if (min > max)
        {
            min = max = 0;
        }

        // The colour range: the floor within StandWindow of where players stand (spawns, the player start, items), so a
        // kill pit or a hidden room far below does not wash the playable storeys into one colour; every floor without
        // stand points.
        double rangeMin = min, rangeMax = max;
        var stand = new List<double>();
        foreach (Vec3d p in _obj.Spawns.Select(s => s.P).Concat(_obj.Items.Select(it => it.P)))
        {
            int si = _w.Snap(p);
            if (si >= 0)
            {
                stand.Add(_s[si].P.Y);
            }
        }

        if (stand.Count > 0)
        {
            double lo = stand.Min() - StandWindow, hi = stand.Max() + StandWindow;
            double wmin = double.MaxValue, wmax = double.MinValue;
            foreach (int i in HIdx)
            {
                double y = _s[i].P.Y;
                if (y >= lo && y <= hi)
                {
                    wmin = Math.Min(wmin, y);
                    wmax = Math.Max(wmax, y);
                }
            }

            if (wmin <= wmax)
            {
                (rangeMin, rangeMax) = (wmin, wmax);
            }
        }

        _ = PlayLattice; // built before any (parallel) FloorAt query
        return new HeightsReading(this, min, max, g, kind, feature, slope, flats, ramps, stairs, drops)
        {
            Lowest = minAt,
            Highest = maxAt,
            RangeMin = rangeMin,
            RangeMax = rangeMax,
        };
    }

    /// <summary>
    /// The label point of a set of samples: the member farthest (in lattice steps) from the set's edge, nearest its
    /// centroid among equals.
    /// </summary>
    private (double X, double Z) Middle(List<int> members)
    {
        var at = new Dictionary<(int, int), int>();
        foreach (int i in members)
        {
            at.TryAdd((_s[i].I, _s[i].K), i);
        }

        var dist = new Dictionary<(int, int), int>();
        var q = new Queue<(int, int)>();
        foreach ((int, int) c in at.Keys)
        {
            (int ci, int ck) = c;
            if (!at.ContainsKey((ci + 1, ck)) || !at.ContainsKey((ci - 1, ck)) || !at.ContainsKey((ci, ck + 1)) || !at.ContainsKey((ci, ck - 1)))
            {
                dist[c] = 0;
                q.Enqueue(c);
            }
        }

        while (q.Count > 0)
        {
            (int ci, int ck) = q.Dequeue();
            int d = dist[(ci, ck)];
            foreach ((int, int) nb in new[] { (ci + 1, ck), (ci - 1, ck), (ci, ck + 1), (ci, ck - 1) })
            {
                if (at.ContainsKey(nb) && !dist.ContainsKey(nb))
                {
                    dist[nb] = d + 1;
                    q.Enqueue(nb);
                }
            }
        }

        double cx = members.Average(i => _s[i].P.X), cz = members.Average(i => _s[i].P.Z);
        int far = dist.Values.DefaultIfEmpty(0).Max();
        int best = members
            .Where(i => dist.GetValueOrDefault((_s[i].I, _s[i].K)) == far)
            .OrderBy(i => ((_s[i].P.X - cx) * (_s[i].P.X - cx)) + ((_s[i].P.Z - cz) * (_s[i].P.Z - cz)))
            .ThenBy(i => i)
            .First();
        return (_s[best].P.X, _s[best].P.Z);
    }

    /// <summary>One unit drop edge on the lattice: between corners A and B, high floor, low floor (NaN: none below), the step to the low side.</summary>
    private readonly record struct DropEdge((int I, int K) A, (int I, int K) B, double High, double Low, int Di, int Dk);

    /// <summary>The drop edges of the playable floor, chained into simplified polylines (see the file header).</summary>
    private List<HeightsDrop> ReadDrops(Func<int, double> highArea)
    {
        double g = _g;
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < _s.Count; i++)
        {
            if (_s[i].Valid)
            {
                (int, int) key = (_s[i].I, _s[i].K);
                if (!cells.TryGetValue(key, out List<int>? l))
                {
                    cells[key] = l = new List<int>();
                }

                l.Add(i);
            }
        }

        int[] dis = { 1, -1, 0, 0 };
        int[] dks = { 0, 0, 1, -1 };
        var found = new List<DropEdge>?[_s.Count];
        For(HIdx.Count, k =>
        {
            int i = HIdx[k];
            Vec3d p = _s[i].P;
            if (highArea(i) < DropMinHighArea)
            {
                return; // the top of a crate, a boulder or a rail: a perch, not a floor that ends in a drop
            }

            for (int d = 0; d < 4; d++)
            {
                int ni = _s[i].I + dis[d], nk = _s[i].K + dks[d];
                double lowY = double.NaN;
                bool on = false;
                if (cells.TryGetValue((ni, nk), out List<int>? there))
                {
                    foreach (int j in there)
                    {
                        double dy = _s[j].P.Y - p.Y;
                        if (dy >= -FootStep && dy <= 2.0)
                        {
                            on = true; // the floor goes on (a step at most), or rises in front (a platform, not a drop)
                            break;
                        }

                        if (dy < -FootStep && (double.IsNaN(lowY) || _s[j].P.Y > lowY))
                        {
                            lowY = _s[j].P.Y;
                        }
                    }
                }

                if (on)
                {
                    continue;
                }

                // A wall in the way (head height: a railing stays a drop) is no drop.
                var dir = new Vec3d(dis[d], 0, dks[d]);
                if (_sp.Blocked(new Vec3d(p.X, p.Y + 1.6, p.Z), dir, g, SpaceSampler.HitFilter.Any))
                {
                    continue;
                }

                // The next cell must be open air over the edge (a sample on a slanting wall looks across into solid), and
                // open down to a lower floor (no slab between): the first floor below, straight down from the high floor's
                // height over the next cell. Nothing at all below is no drop either (outside the level).
                double cx = (ni + 0.5) * g, cz = (nk + 0.5) * g;
                if (InSolid(new Vec3d(cx, p.Y + 0.4, cz)) || InSolid(new Vec3d(cx, p.Y + 1.6, cz)))
                {
                    continue;
                }

                double? hit = _sp.Cast(new Vec3d(cx, p.Y + 0.4, cz), new Vec3d(0, -1, 0), SkyFall, SpaceSampler.HitFilter.UpFacing);
                if (hit is not double h || p.Y + 0.4 - h > p.Y - FootStep)
                {
                    continue; // nothing below, or something (a ledge, a prop top, a slab) stands right there: not open
                }

                double below = p.Y + 0.4 - h;
                if (double.IsNaN(lowY) || below > lowY + 0.15)
                {
                    lowY = below; // the first floor below (one no player stands on, when the lattice has none there)
                }

                (int, int) a, b;
                if (dis[d] != 0)
                {
                    int x = dis[d] > 0 ? _s[i].I + 1 : _s[i].I;
                    (a, b) = ((x, _s[i].K), (x, _s[i].K + 1));
                }
                else
                {
                    int z = dks[d] > 0 ? _s[i].K + 1 : _s[i].K;
                    (a, b) = ((_s[i].I, z), (_s[i].I + 1, z));
                }

                (found[i] ??= new List<DropEdge>()).Add(new DropEdge(a, b, p.Y, lowY, dis[d], dks[d]));
            }
        });

        var edges = found.Where(l => l is not null).SelectMany(l => l!).ToList();
        var result = new List<HeightsDrop>();
        if (edges.Count == 0)
        {
            return result;
        }

        // Group: edges sharing a corner with a high floor within 0.35 m and a low floor within 1 m (or both void).
        var euf = new UnionFind(edges.Count);
        var byCorner = new Dictionary<(int, int), List<int>>();
        for (int e = 0; e < edges.Count; e++)
        {
            foreach ((int, int) c in new[] { edges[e].A, edges[e].B })
            {
                if (!byCorner.TryGetValue(c, out List<int>? l))
                {
                    byCorner[c] = l = new List<int>();
                }

                l.Add(e);
            }
        }

        bool Similar(DropEdge x, DropEdge y) => Math.Abs(x.High - y.High) <= 0.35
            && ((double.IsNaN(x.Low) && double.IsNaN(y.Low)) || (!double.IsNaN(x.Low) && !double.IsNaN(y.Low) && Math.Abs(x.Low - y.Low) <= 1.0));
        foreach (List<int> l in byCorner.Values)
        {
            for (int a = 0; a < l.Count; a++)
            {
                for (int b = a + 1; b < l.Count; b++)
                {
                    if (Similar(edges[l[a]], edges[l[b]]))
                    {
                        euf.Union(l[a], l[b]);
                    }
                }
            }
        }

        foreach (List<int> group in Enumerable.Range(0, edges.Count).GroupBy(euf.Find).Select(gr => gr.ToList()))
        {
            _ct.ThrowIfCancellationRequested();
            foreach (List<(int I, int K)> chain in Chains(group.Select(e => edges[e]).ToList()))
            {
                var pts = chain.Select(c => (X: c.I * g, Z: c.K * g)).ToList();
                double length = 0;
                for (int k = 1; k < pts.Count; k++)
                {
                    length += Math.Sqrt(((pts[k].X - pts[k - 1].X) * (pts[k].X - pts[k - 1].X)) + ((pts[k].Z - pts[k - 1].Z) * (pts[k].Z - pts[k - 1].Z)));
                }

                if (length < DropMinLength - 1e-9)
                {
                    continue;
                }

                List<(double X, double Z)> simple = Simplify(pts, 0.3);
                var mine = group.Select(e => edges[e]).Where(e => OnChain(e, chain)).ToList();
                double high = Med(mine.Select(e => e.High));
                double[] lows = mine.Where(e => !double.IsNaN(e.Low)).Select(e => e.Low).ToArray();
                double low = lows.Length > 0 ? Med(lows) : double.NaN;

                // The low side of each simplified segment: the unit edges' step directions that lie along it.
                var side = new List<(double X, double Z)>();
                for (int k = 0; k + 1 < simple.Count; k++)
                {
                    double ax = simple[k].X, az = simple[k].Z, bx = simple[k + 1].X, bz = simple[k + 1].Z;
                    double len = Math.Sqrt(((bx - ax) * (bx - ax)) + ((bz - az) * (bz - az)));
                    double nx = len > 1e-9 ? -(bz - az) / len : 0, nz = len > 1e-9 ? (bx - ax) / len : 0;
                    double acc = 0;
                    foreach (DropEdge e in mine)
                    {
                        double mx = (e.A.I + e.B.I) * g / 2, mz = (e.A.K + e.B.K) * g / 2;
                        if (Analysis.Recreation.RefMask.SegDist(mx, mz, ax, az, bx, bz) <= 0.4)
                        {
                            acc += (e.Di * nx) + (e.Dk * nz);
                        }
                    }

                    side.Add(acc >= 0 ? (nx, nz) : (-nx, -nz));
                }

                result.Add(new HeightsDrop(result.Count, simple, side, high, low, length));
            }
        }

        return result.OrderByDescending(d => d.Length).ThenBy(d => d.Points[0].X).ThenBy(d => d.Points[0].Z)
            .Select((d, k) => d with { Index = k }).ToList();
    }

    /// <summary>How far down (m) the drop probe looks for a lower floor.</summary>
    private const double SkyFall = 60;

    /// <summary>
    /// True when a point lies inside solid: straight up it first meets the back of a floor, or straight down the back of
    /// a ceiling, or nothing either way (outside the level).
    /// </summary>
    private bool InSolid(Vec3d p)
    {
        bool up = _sp.Cast(p, new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int fu) is not null && fu >= 0;
        if (up && _sp.Faces[fu].N.Y > 0.1)
        {
            return true;
        }

        bool down = _sp.Cast(p, new Vec3d(0, -1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int fd) is not null && fd >= 0;
        if (down && _sp.Faces[fd].N.Y < -0.1)
        {
            return true;
        }

        return !up && !down;
    }

    private static bool OnChain(DropEdge e, List<(int I, int K)> chain)
    {
        for (int k = 0; k + 1 < chain.Count; k++)
        {
            if ((chain[k] == e.A && chain[k + 1] == e.B) || (chain[k] == e.B && chain[k + 1] == e.A))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Chains unit edges into corner paths: from each end (a corner of degree other than 2) along unused edges; leftover loops closed.</summary>
    private static List<List<(int I, int K)>> Chains(List<DropEdge> edges)
    {
        var adj = new Dictionary<(int, int), List<int>>();
        for (int e = 0; e < edges.Count; e++)
        {
            foreach ((int, int) c in new[] { edges[e].A, edges[e].B })
            {
                if (!adj.TryGetValue(c, out List<int>? l))
                {
                    adj[c] = l = new List<int>();
                }

                l.Add(e);
            }
        }

        var used = new bool[edges.Count];
        var chains = new List<List<(int I, int K)>>();
        List<(int I, int K)> Walk((int I, int K) start)
        {
            var path = new List<(int I, int K)> { start };
            (int I, int K) at = start;
            while (true)
            {
                int next = adj[at].Where(e => !used[e]).DefaultIfEmpty(-1).First();
                if (next < 0)
                {
                    break;
                }

                used[next] = true;
                at = edges[next].A == at ? edges[next].B : edges[next].A;
                path.Add(at);
                if (adj[at].Count != 2)
                {
                    break;
                }
            }

            return path;
        }

        foreach ((int, int) c in adj.Keys.Where(c => adj[c].Count != 2).OrderBy(c => c.Item1).ThenBy(c => c.Item2))
        {
            while (adj[c].Any(e => !used[e]))
            {
                chains.Add(Walk(c));
            }
        }

        for (int e = 0; e < edges.Count; e++)
        {
            if (!used[e])
            {
                chains.Add(Walk(edges[e].A));
            }
        }

        return chains;
    }

    /// <summary>Douglas-Peucker on a corner path (metres).</summary>
    private static List<(double X, double Z)> Simplify(List<(double X, double Z)> p, double tol)
    {
        if (p.Count <= 2)
        {
            return p;
        }

        var keep = new bool[p.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            (int a, int b) = stack.Pop();
            double worst = -1;
            int at = -1;
            for (int k = a + 1; k < b; k++)
            {
                double d = Analysis.Recreation.RefMask.SegDist(p[k].X, p[k].Z, p[a].X, p[a].Z, p[b].X, p[b].Z);
                if (d > worst)
                {
                    worst = d;
                    at = k;
                }
            }

            if (worst > tol && at > 0)
            {
                keep[at] = true;
                stack.Push((a, at));
                stack.Push((at, b));
            }
        }

        return p.Where((_, k) => keep[k]).ToList();
    }

    /// <summary>
    /// The walkable floor under a point for the overlay's pixels: the first up-facing face below <paramref name="top"/>
    /// (invisible collision helpers skipped, as the renderer skips them), and whether a playable sample stands on it.
    /// </summary>
    internal HeightsHit? FloorAt(double x, double z, double top, int[] kind, int[] feature, double[] slope)
    {
        double y = top;
        for (int k = 0; k < 6; k++)
        {
            double? d = _sp.Cast(new Vec3d(x, y, z), new Vec3d(0, -1, 0), 2000, SpaceSampler.HitFilter.UpFacing, out int f);
            if (d is not double dist || f < 0)
            {
                return null;
            }

            SpaceSampler.SFace face = _sp.Faces[f];
            double hy = y - dist;
            if (!face.Floor && face.N.Y >= SpaceSampler.FloorNormalY)
            {
                y = hy - 0.01; // an invisible collision helper: not drawn
                continue;
            }

            double faceSlope = face.N.Y > 1e-6 ? Math.Sqrt((face.N.X * face.N.X) + (face.N.Z * face.N.Z)) / face.N.Y : 99;
            if (!face.Floor)
            {
                return new HeightsHit(hy, false, false, HeightsReading.KindNone, -1);
            }

            // The nearest playable sample on this floor (this lattice cell or a neighbour).
            int ci = (int)Math.Floor(x / _g), ck = (int)Math.Floor(z / _g);
            double tol = 0.2 + (1.5 * _g * Math.Min(faceSlope, 1.3));
            int best = -1;
            double bestD = double.MaxValue;
            for (int di = -1; di <= 1; di++)
            {
                for (int dk = -1; dk <= 1; dk++)
                {
                    if (!PlayLattice.TryGetValue((ci + di, ck + dk), out List<int>? list))
                    {
                        continue;
                    }

                    foreach (int i in list)
                    {
                        Vec3d p = _s[i].P;
                        if (Math.Abs(p.Y - hy) > tol)
                        {
                            continue;
                        }

                        double dd = ((p.X - x) * (p.X - x)) + ((p.Z - z) * (p.Z - z));
                        if (dd < bestD)
                        {
                            bestD = dd;
                            best = i;
                        }
                    }
                }
            }

            if (best < 0)
            {
                return new HeightsHit(hy, false, faceSlope >= LevelSlope, HeightsReading.KindNone, -1);
            }

            return new HeightsHit(hy, true, faceSlope >= LevelSlope, kind[best], feature[best]);
        }

        return null;
    }

    /// <summary>The samples the heights reading stands on (see <see cref="ReadHeights"/>), by index and as a list.</summary>
    private bool[] HUse { get; set; } = Array.Empty<bool>();

    private List<int> HIdx { get; set; } = new();

    private Dictionary<(int, int), List<int>>? _latPlay;

    /// <summary>The playable samples by lattice cell.</summary>
    private Dictionary<(int, int), List<int>> PlayLattice
    {
        get
        {
            if (_latPlay is null)
            {
                var d = new Dictionary<(int, int), List<int>>();
                foreach (int i in HIdx)
                {
                    (int, int) key = (_s[i].I, _s[i].K);
                    if (!d.TryGetValue(key, out List<int>? l))
                    {
                        d[key] = l = new List<int>();
                    }

                    l.Add(i);
                }

                _latPlay = d;
            }

            return _latPlay;
        }
    }
}

/// <summary>A point a heights symbol is drawn at: world position and the uphill direction there.</summary>
public readonly record struct HeightsMark(double X, double Y, double Z, double DirX, double DirZ);

/// <summary>A flat floor region: its height, area and label point.</summary>
public sealed record HeightsFlat(int Index, double Y, double Area, double X, double Z);

/// <summary>
/// A ramp or slope (<c>Kind</c> "ramp") or a flight of stairs ("stairs"): its low and high floor, foot and top points,
/// climb direction (unit, uphill), run along it, width across it, area, label point and symbol marks (chevron points for
/// a ramp; foot and top of the arrow for stairs).
/// </summary>
public sealed record HeightsFlight(int Index, string Kind, double Low, double High, Vec3d Foot, Vec3d Top, double DirX, double DirZ,
    double Run, double Width, double Area, double X, double Z, IReadOnlyList<HeightsMark> Marks);

/// <summary>
/// A drop edge: a simplified polyline on the lattice cell boundaries (world x, z), the unit normal of each segment toward
/// its low side, the high floor, the low floor (NaN: no floor below within 60 m) and the length.
/// </summary>
public sealed record HeightsDrop(int Index, IReadOnlyList<(double X, double Z)> Points, IReadOnlyList<(double X, double Z)> LowSide,
    double High, double Low, double Length)
{
    /// <summary>The drop in metres (NaN into nothing).</summary>
    public double Drop => High - Low;
}

/// <summary>The floor under a point: its height, whether a player stands there, whether it slopes, and its feature.</summary>
public readonly record struct HeightsHit(double Y, bool Walkable, bool Sloped, int Kind, int Feature);

/// <summary>
/// The heights reading of a level (see <c>SpatialAnalysis.Heights</c>): the walkable floor's height range, its flat
/// regions, ramps and slopes, stairs and drop edges, and the floor under any point. Measured once from the compiled
/// geometry; thread-safe to query.
/// </summary>
public sealed class HeightsReading
{
    /// <summary>A sample on no feature.</summary>
    public const int KindNone = 0;

    /// <summary>A sample on flat floor.</summary>
    public const int KindFlat = 1;

    /// <summary>A sample on a ramp or slope.</summary>
    public const int KindRamp = 2;

    /// <summary>A sample on stairs.</summary>
    public const int KindStairs = 3;

    private readonly SpatialAnalysis _a;
    private readonly int[] _kind;
    private readonly int[] _feature;
    private readonly double[] _slope;

    internal HeightsReading(SpatialAnalysis a, double min, double max, double pitch, int[] kind, int[] feature, double[] slope,
        IReadOnlyList<HeightsFlat> flats, IReadOnlyList<HeightsFlight> ramps, IReadOnlyList<HeightsFlight> stairs, IReadOnlyList<HeightsDrop> drops)
    {
        _a = a;
        Min = min;
        Max = max;
        Pitch = pitch;
        _kind = kind;
        _feature = feature;
        _slope = slope;
        Flats = flats;
        Ramps = ramps;
        Stairs = stairs;
        Drops = drops;
    }

    /// <summary>The lowest playable floor (m).</summary>
    public double Min { get; }

    /// <summary>The highest playable floor (m).</summary>
    public double Max { get; }

    /// <summary>
    /// The heights the colour map spans: the floor within 8 m of the stand points (spawns, the player start, items) —
    /// a kill pit or a hidden room far below or above takes the end colour — or every floor in a level without them.
    /// </summary>
    public double RangeMin { get; init; }

    public double RangeMax { get; init; }

    /// <summary>Where the lowest and highest playable floor are.</summary>
    public Vec3d Lowest { get; init; }

    public Vec3d Highest { get; init; }

    /// <summary>The lattice pitch measured at (m).</summary>
    public double Pitch { get; }

    public IReadOnlyList<HeightsFlat> Flats { get; }

    public IReadOnlyList<HeightsFlight> Ramps { get; }

    public IReadOnlyList<HeightsFlight> Stairs { get; }

    public IReadOnlyList<HeightsDrop> Drops { get; }

    /// <summary>The floor under (x, z), searching down from <paramref name="top"/>; null over nothing.</summary>
    public HeightsHit? FloorAt(double x, double z, double top) => _a.FloorAt(x, z, top, _kind, _feature, _slope);

    /// <summary>
    /// Measures a level's heights: the walk graph over all of its floor (every floor piece of 16 m² or more is playable,
    /// not only the floor the spawns reach; no focus window). Throws <see cref="SpatialException"/> when the level has no
    /// floor to measure.
    /// </summary>
    public static HeightsReading Measure(SpatialLevel level, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        var options = new SpatialOptions
        {
            Focus = false,
            MovementLinks = false,
            PlayableAllFloor = true,
            MaxSamples = 1_500_000,
        };
        SpatialAnalysis a = SpatialAnalysis.Run(level, options, ct);
        return a.ReadHeights();
    }
}
