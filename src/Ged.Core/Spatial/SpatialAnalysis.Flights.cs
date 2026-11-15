using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

// Function checks of the walk (the intent layer, G30-G31): how the floor changes height along the routes.
//   * floor levels: the playable floor split into flat pieces (neighbours within 0.1 m); a piece with at least 1 m² of
//     interior floor (all four lattice neighbours on it) is a landing; the rest is the floor between landings.
//   * flights: a connected run of between-landing floor that climbs at least 1 m on a plane (fit R² ≥ 0.8, slope
//     0.25-1.3, at most 12 m wide) is a flight of stairs or a ramp, with a landing at its foot and its top. A flight
//     serves a route when the floors it joins are otherwise apart, both of its ends lead on and it has room to step on
//     and off; natural rock slopes (terrain) are not flights.
//   * route ledges: two big landings (each a floor of its own space) joined only by an abrupt step of 0.3-1.5 m in a
//     doorway or corridor, with no way round on foot nearby: the route between them makes players hop.
public sealed partial class SpatialAnalysis
{
    /// <summary>Neighbouring floor samples within this height (m) are one flat piece.</summary>
    public const double FlatTolerance = 0.1;

    /// <summary>A step on foot (m): higher needs a hop (the kit's foot graph).</summary>
    public const double FootStep = 0.6;

    /// <summary>A flight climbs at least this much (m).</summary>
    public const double FlightMinRise = 1.0;

    /// <summary>Another way up to a flight's top floor within this many times its length (+1 m) of its foot makes it redundant.</summary>
    public const double FlightRedundantRatio = 2.0;

    /// <summary>A flight needs this much clear run (m) past its top step and in front of its first step: less is blocked.</summary>
    public const double FlightBlocked = 0.3;

    /// <summary>A flight with no floor beyond its top and a wall or solid within this distance (m) of its top step climbs into an obstacle.</summary>
    public const double FlightIntoSolid = 1.5;

    /// <summary>A flight whose top leads on to less floor than this (m², with nothing on it) leads nowhere.</summary>
    public const double FlightDeadEnd = 25;

    /// <summary>Floor reached only by flights from one other floor, under this area (m²), is a dead-end pit or perch.</summary>
    public const double FlightPocket = 150;

    /// <summary>Route ledges join landings of at least this area (m²) each.</summary>
    public const double LedgeMinLanding = 150;

    /// <summary>The smallest height change (m) a route ledge counts.</summary>
    public const double LedgeMinStep = 0.3;

    /// <summary>The flight faults G30 counts; the others (redundant, dead_end) are listed with their reason but not counted, as the well-regarded levels have them too.</summary>
    internal static readonly HashSet<string> CountedFlightFaults = new(StringComparer.Ordinal) { "into_obstacle", "blocked", "onto_nothing", "in_gap" };

    /// <summary>Flight readings (after <see cref="PurposeReport"/>).</summary>
    internal List<FlightRead> FlightReads { get; } = new();

    /// <summary>Route-ledge readings (after <see cref="PurposeReport"/>).</summary>
    internal List<LedgeRead> LedgeReads { get; } = new();

    /// <summary>One flight of stairs or ramp as read for its route.</summary>
    internal sealed record FlightRead(
        Vec3d Centre, Vec3d Foot, Vec3d Top, Vec3d Dir, double Rise, double Run, double Width, double Length,
        double FootLanding, double TopLanding, double Detour, double FootClear, double TopClear, bool TopExit,
        double TopArea, int TopRewards, double PocketArea, Vec3d? Gap, string? Fault, string? Kind);

    /// <summary>One abrupt height change between two big landings on a route.</summary>
    internal sealed record LedgeRead(Vec3d Pos, double Step, double Width, double Low, double High, double LowArea, double HighArea, double Narrow);

    private sealed class FlatPiece
    {
        public required List<int> Members { get; init; }

        public double Y { get; set; }

        public double Area { get; set; }

        public bool Landing { get; set; }
    }

    private int[]? _flatOf;
    private List<FlatPiece>? _flats;

    /// <summary>The floor split into flat pieces (neighbours within <see cref="FlatTolerance"/>), each marked landing or not.</summary>
    private void FloorLevels()
    {
        if (_flatOf is not null)
        {
            return;
        }

        var uf = new UnionFind(_s.Count);
        foreach (int i in _w.PlayIdx)
        {
            foreach (WalkEdge e in _w.Adj[i])
            {
                if (!e.Link && !e.Drop && _w.Play[e.To] && Math.Abs(_s[e.To].P.Y - _s[i].P.Y) <= FlatTolerance)
                {
                    uf.Union(i, e.To);
                }
            }
        }

        var flatOf = new int[_s.Count];
        Array.Fill(flatOf, -1);
        var flats = new List<FlatPiece>();
        var rootId = new Dictionary<int, int>();
        foreach (int i in _w.PlayIdx)
        {
            int r = uf.Find(i);
            if (!rootId.TryGetValue(r, out int id))
            {
                id = flats.Count;
                rootId[r] = id;
                flats.Add(new FlatPiece { Members = new List<int>() });
            }

            flatOf[i] = id;
            flats[id].Members.Add(i);
        }

        for (int f = 0; f < flats.Count; f++)
        {
            List<int> m = flats[f].Members;
            int interior = 0;
            foreach (int i in m)
            {
                int axis = 0;
                foreach (WalkEdge e in _w.Adj[i])
                {
                    if (e.Axis && !e.Link && !e.Drop && flatOf[e.To] == f)
                    {
                        axis++;
                    }
                }

                if (axis >= 4)
                {
                    interior++;
                }
            }

            flats[f].Y = Med(m.Select(i => _s[i].P.Y));
            flats[f].Area = m.Count * _g * _g;
            flats[f].Landing = interior * _g * _g >= 1.0;
        }

        _flatOf = flatOf;
        _flats = flats;
    }

    private bool OnLanding(int i) => _flatOf![i] >= 0 && _flats![_flatOf[i]].Landing;

    /// <summary>Walk on foot (steps up to <see cref="FootStep"/>, drops and links allowed) from a sample to the nearest target, never through blocked samples; ∞ beyond the limit (which bounds the search; the callers' passes honour cancellation).</summary>
    private double FootWalk(int from, Func<int, bool> target, bool[] blocked, double limit, Func<int, int, bool>? edgeBlocked = null)
    {
        var d = new Dictionary<int, double> { [from] = 0 };
        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(from, 0);
        while (pq.TryDequeue(out int u, out double du))
        {
            if (du > d[u])
            {
                continue;
            }

            if (target(u))
            {
                return du;
            }

            if (du > limit)
            {
                break;
            }

            foreach (WalkEdge e in _w.Adj[u])
            {
                int v = e.To;
                if (!_w.Play[v] || blocked[v] || (!e.Drop && !e.Link && _s[v].P.Y - _s[u].P.Y > FootStep) || (edgeBlocked is not null && edgeBlocked(u, v)))
                {
                    continue;
                }

                double nd = du + _s[u].P.Sub(_s[v].P).Length;
                if (nd < d.GetValueOrDefault(v, double.PositiveInfinity))
                {
                    d[v] = nd;
                    pq.Enqueue(v, nd);
                }
            }
        }

        return double.PositiveInfinity;
    }

    /// <summary>The floor (m², up to cap) reached from a sample by two-way walking and links, never through blocked samples, and the items and spawns on it.</summary>
    private (double Area, int Rewards) Reach(int start, bool[] blocked, double cap, HashSet<int> rewards)
    {
        var seen = new HashSet<int> { start };
        var q = new Queue<int>();
        q.Enqueue(start);
        int r = rewards.Contains(start) ? 1 : 0;
        double cell = _g * _g;
        while (q.Count > 0 && seen.Count * cell < cap)
        {
            foreach (WalkEdge e in _w.Adj[q.Dequeue()])
            {
                if (!e.Drop && _w.Play[e.To] && !blocked[e.To] && seen.Add(e.To))
                {
                    r += rewards.Contains(e.To) ? 1 : 0;
                    q.Enqueue(e.To);
                }
            }
        }

        return (Math.Min(cap, seen.Count * cell), r);
    }

    /// <summary>The clear run (m, up to cap) along a horizontal direction at chest and head height above a floor point.</summary>
    private double ClearRun(Vec3d p, double ux, double uz, double cap)
    {
        double best = cap;
        foreach (double h in new[] { 0.9, 1.7 })
        {
            best = Math.Min(best, _sp.Cast(new Vec3d(p.X, p.Y + h, p.Z), new Vec3d(ux, 0, uz), cap, SpaceSampler.HitFilter.Any) ?? cap);
        }

        return best;
    }

    /// <summary>The samples carrying an item or a spawn.</summary>
    private HashSet<int> RewardSamples()
    {
        var set = new HashSet<int>();
        foreach (Vec3d p in _obj.Items.Select(i => i.P).Concat(_obj.Spawns.Select(sp => sp.P)))
        {
            int si = _w.Snap(p);
            if (si >= 0)
            {
                set.Add(si);
            }
        }

        return set;
    }

    private sealed record FlightShape(List<int> T, double Ux, double Uz, double Run, double Width, double Ymin, double Ymax, int Bot, int Top, int FootEnd, int TopEnd, Vec3d FootEdge, Vec3d TopEdge, Vec3d Centre);

    /// <summary>The flights: runs of between-landing floor that climb on a plane, with their landings and end samples.</summary>
    private List<FlightShape> FindFlights()
    {
        FloorLevels();
        List<FlatPiece> flats = _flats!;
        int[] flatOf = _flatOf!;
        var uf = new UnionFind(_s.Count);
        foreach (int i in _w.PlayIdx)
        {
            if (OnLanding(i))
            {
                continue;
            }

            foreach (WalkEdge e in _w.Adj[i])
            {
                if (!e.Link && !e.Drop && _w.Play[e.To] && !OnLanding(e.To))
                {
                    uf.Union(i, e.To);
                }
            }
        }

        var found = new List<FlightShape>();
        foreach (List<int> t in _w.PlayIdx.Where(i => !OnLanding(i)).GroupBy(i => uf.Find(i)).Select(g => g.ToList()))
        {
            _ct.ThrowIfCancellationRequested();
            if (t.Count < 6)
            {
                continue;
            }

            double ymin = t.Min(i => _s[i].P.Y), ymax = t.Max(i => _s[i].P.Y);
            if (ymax - ymin < FlightMinRise)
            {
                continue;
            }

            // The plane y = a x + b z + c through the run (least squares): its slope, direction and fit.
            double mx = t.Average(i => _s[i].P.X), mz = t.Average(i => _s[i].P.Z), my = t.Average(i => _s[i].P.Y);
            double sxx = 0, szz = 0, sxz = 0, sxy = 0, szy = 0, syy = 0;
            foreach (int i in t)
            {
                double dx = _s[i].P.X - mx, dz = _s[i].P.Z - mz, dy = _s[i].P.Y - my;
                sxx += dx * dx;
                szz += dz * dz;
                sxz += dx * dz;
                sxy += dx * dy;
                szy += dz * dy;
                syy += dy * dy;
            }

            double det = (sxx * szz) - (sxz * sxz);
            if (det < 1e-9 || syy < 1e-9)
            {
                continue;
            }

            double a = ((sxy * szz) - (szy * sxz)) / det, b = ((szy * sxx) - (sxy * sxz)) / det;
            double ssres = 0;
            foreach (int i in t)
            {
                double r = (_s[i].P.Y - my) - (a * (_s[i].P.X - mx)) - (b * (_s[i].P.Z - mz));
                ssres += r * r;
            }

            double slope = Math.Sqrt((a * a) + (b * b));
            if (1 - (ssres / syy) < 0.8 || slope < 0.25 || slope > 1.3)
            {
                continue;
            }

            double ux = a / slope, uz = b / slope;
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (int i in t)
            {
                double u = ((_s[i].P.X - mx) * ux) + ((_s[i].P.Z - mz) * uz), v = (-(_s[i].P.X - mx) * uz) + ((_s[i].P.Z - mz) * ux);
                u0 = Math.Min(u0, u);
                u1 = Math.Max(u1, u);
                v0 = Math.Min(v0, v);
                v1 = Math.Max(v1, v);
            }

            if (v1 - v0 > 12 || u1 - u0 < 1.5)
            {
                continue;
            }

            // The landings at its foot and top: the lowest landing touching its low end, the highest touching its high end.
            int bot = -1, top = -1, footEnd = -1, topEnd = -1;
            double bestFoot = double.MaxValue, bestTop = double.MinValue, vm = (v0 + v1) / 2;
            foreach (int i in t)
            {
                foreach (WalkEdge e in _w.Adj[i])
                {
                    if (e.Link || e.Drop || !_w.Play[e.To] || !OnLanding(e.To))
                    {
                        continue;
                    }

                    Vec3d p = _s[e.To].P;
                    double u = ((p.X - mx) * ux) + ((p.Z - mz) * uz), v = (-(p.X - mx) * uz) + ((p.Z - mz) * ux);
                    if (p.Y <= ymin + 0.5 && u < u0 + 1.0 && u + Math.Abs(v - vm) < bestFoot)
                    {
                        bestFoot = u + Math.Abs(v - vm);
                        footEnd = e.To;
                        bot = flatOf[e.To];
                    }
                    else if (p.Y >= ymax - 0.5 && u > u1 - 1.0 && u - Math.Abs(v - vm) > bestTop)
                    {
                        bestTop = u - Math.Abs(v - vm);
                        topEnd = e.To;
                        top = flatOf[e.To];
                    }
                }
            }

            if (bot < 0)
            {
                continue; // no floor at its foot
            }

            double yb = flats[bot].Y, yt = top >= 0 ? flats[top].Y : ymax;
            if (yt - yb < FlightMinRise || NaturalSlope(t))
            {
                continue; // a ridge or a dip between landings at one height, or a rock slope (terrain): not built stairs or a ramp
            }

            var footEdge = new Vec3d(mx + (ux * u0) - (uz * vm), yb, mz + (uz * u0) + (ux * vm));
            var topEdge = new Vec3d(mx + (ux * u1) - (uz * vm), yt, mz + (uz * u1) + (ux * vm));
            found.Add(new FlightShape(t, ux, uz, u1 - u0, v1 - v0, ymin, ymax, bot, top, footEnd, topEnd, footEdge, topEdge, new Vec3d(mx, (yb + yt) / 2, mz)));
        }

        return found;
    }

    /// <summary>True when most of a run's floor is natural rock (a terrain slope).</summary>
    private bool NaturalSlope(List<int> t)
    {
        int rock = 0, n = 0;
        for (int k = 0; k < t.Count; k += Math.Max(1, t.Count / 9))
        {
            Vec3d p = _s[t[k]].P;
            if (_sp.Cast(new Vec3d(p.X, p.Y + 0.2, p.Z), new Vec3d(0, -1, 0), 0.6, SpaceSampler.HitFilter.UpFacing, out int fi) is not null && fi >= 0)
            {
                n++;
                rock += RockTexture(TexName(_level.Geometry.Faces[_sp.Faces[fi].Source])) ? 1 : 0;
            }
        }

        return n > 0 && rock * 2 > n;
    }

    /// <summary>Reads every flight for its route (see the file header) into <see cref="FlightReads"/>.</summary>
    private void ReadFlights()
    {
        FlightReads.Clear();
        List<FlightShape> flights = FindFlights();
        List<FlatPiece> flats = _flats!;
        int[] flatOf = _flatOf!;
        HashSet<int> rewards = RewardSamples();

        // The floor with every flight taken out, as regions of two-way walking: a region reached only by flights from one
        // other region (no drop or link in or out), under FlightPocket, is a dead-end pit or perch.
        var anyFlight = new bool[_s.Count];
        foreach (FlightShape f in flights)
        {
            foreach (int i in f.T)
            {
                anyFlight[i] = true;
            }
        }

        var regionOf = new int[_s.Count];
        Array.Fill(regionOf, -1);
        var regionArea = new List<double>();
        var regionRewards = new List<int>();
        var regionOpen = new List<bool>();
        var regionMembers = new List<List<int>>();
        foreach (int s0 in _w.PlayIdx)
        {
            if (anyFlight[s0] || regionOf[s0] >= 0)
            {
                continue;
            }

            _ct.ThrowIfCancellationRequested();

            int rid = regionArea.Count, rw = 0;
            bool open = false;
            var members = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(s0);
            regionOf[s0] = rid;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                members.Add(u);
                rw += rewards.Contains(u) ? 1 : 0;
                foreach (WalkEdge e in _w.Adj[u])
                {
                    if (!_w.Play[e.To] || anyFlight[e.To])
                    {
                        continue;
                    }

                    if (e.Link)
                    {
                        open = true; // a lift, jump pad or teleport out of it: not a closed pocket
                        continue;
                    }

                    if (e.Drop)
                    {
                        continue; // dropping out of a perch still leaves it a dead end of the flight
                    }

                    if (regionOf[e.To] < 0)
                    {
                        regionOf[e.To] = rid;
                        q.Enqueue(e.To);
                    }
                }
            }

            regionArea.Add(members.Count * _g * _g);
            regionRewards.Add(rw);
            regionOpen.Add(open);
            regionMembers.Add(members);
        }

        // A link INTO a region opens it too (it is reached some other way; dropping in from the rim does not).
        foreach (int u in _w.PlayIdx)
        {
            foreach (WalkEdge e in _w.Adj[u])
            {
                if (e.Link && _w.Play[e.To] && regionOf[e.To] >= 0 && regionOf[u] != regionOf[e.To])
                {
                    regionOpen[regionOf[e.To]] = true;
                }
            }
        }

        // Pockets: closed regions every flight of which leads to one other region.
        var pocketOther = new Dictionary<int, int>();
        var pocketFlights = new Dictionary<int, List<int>>();
        for (int k = 0; k < flights.Count; k++)
        {
            int rb = regionOf[flights[k].FootEnd], rt = flights[k].TopEnd >= 0 ? regionOf[flights[k].TopEnd] : -1;
            foreach ((int mine, int other) in new[] { (rb, rt), (rt, rb) })
            {
                if (mine < 0)
                {
                    continue;
                }

                if (!pocketFlights.TryGetValue(mine, out List<int>? l))
                {
                    pocketFlights[mine] = l = new List<int>();
                    pocketOther[mine] = other;
                }
                else if (pocketOther[mine] != other)
                {
                    pocketOther[mine] = -2; // flights to more than one other floor: a through route
                }

                l.Add(k);
            }
        }

        // Each pocket: its area, rewards, and the bridge whose gap it lies in (if any).
        var pocketGap = new Dictionary<int, Vec3d?>();
        foreach (int r in pocketFlights.Keys)
        {
            if (regionArea[r] >= FlightPocket || regionOpen[r] || pocketOther[r] < 0)
            {
                continue;
            }

            Vec3d? gap = null;
            foreach (SetPieceBridge b in SetPieceBridges)
            {
                if (b.Frame is not { } fr)
                {
                    continue;
                }

                foreach (int i in regionMembers[r])
                {
                    Vec3d p = _s[i].P;
                    double u = fr.U(p), v = fr.V(p);
                    if (u >= fr.U0 && u <= fr.U1 && v >= fr.V0 - 1 && v <= fr.V1 + 1 && p.Y <= b.DeckY - 2)
                    {
                        gap = b.Centre;
                        break;
                    }
                }

                if (gap is not null)
                {
                    break;
                }
            }

            pocketGap[r] = gap;
        }

        var reads = new FlightRead[flights.Count];
        For(flights.Count, k =>
        {
            FlightShape f = flights[k];
            var blocked = new bool[_s.Count];
            foreach (int i in f.T)
            {
                blocked[i] = true;
            }

            double yb = flats[f.Bot].Y, yt = f.Top >= 0 ? flats[f.Top].Y : f.Ymax;
            double rise = yt - yb, len = Math.Sqrt((f.Run * f.Run) + (rise * rise));

            // Another way up to its top floor on foot, starting at its foot.
            double detour = f.Top >= 0
                ? FootWalk(f.FootEnd, i => flatOf[i] == f.Top, blocked, (FlightRedundantRatio * len) + 1)
                : double.PositiveInfinity;

            // Room to step on and off: the clear run on three lanes across it, the middle one.
            var foot = new double[3];
            var top = new double[3];
            for (int l = 0; l < 3; l++)
            {
                double off = (l - 1) * f.Width / 3;
                foot[l] = ClearRun(new Vec3d(f.FootEdge.X - (f.Uz * off) - (f.Ux * 0.25), yb, f.FootEdge.Z + (f.Ux * off) - (f.Uz * 0.25)), -f.Ux, -f.Uz, 3);
                top[l] = ClearRun(new Vec3d(f.TopEdge.X - (f.Uz * off) + (f.Ux * 0.25), yt, f.TopEdge.Z + (f.Ux * off) + (f.Uz * 0.25)), f.Ux, f.Uz, 3);
            }

            Array.Sort(foot);
            Array.Sort(top);

            // Floor beyond its top step (walking on, dropping off or a link), and what its top leads on to.
            bool topExit = false;
            foreach (int i in f.T)
            {
                if (_s[i].P.Y < f.Ymax - 0.6)
                {
                    continue;
                }

                foreach (WalkEdge e in _w.Adj[i])
                {
                    if (_w.Play[e.To] && !blocked[e.To] && (e.Link || e.Drop || _s[e.To].P.Y >= f.Ymax - 0.5))
                    {
                        topExit = true;
                        break;
                    }
                }

                if (topExit)
                {
                    break;
                }
            }

            (double topArea, int topRewards) = f.TopEnd >= 0 ? Reach(f.TopEnd, blocked, 4 * FlightDeadEnd, rewards) : (0, 0);

            // A dead-end pocket at its foot or top.
            double pocketArea = double.NaN;
            Vec3d? gap = null;
            string? pocketFault = null;
            foreach ((int end, string side) in new[] { (f.FootEnd, "foot"), (f.TopEnd, "top") })
            {
                int r = end >= 0 ? regionOf[end] : -1;
                if (r < 0 || !pocketGap.TryGetValue(r, out Vec3d? g))
                {
                    continue;
                }

                pocketArea = regionArea[r];
                gap = g;
                if (g is { } gp)
                {
                    pocketFault = string.Create(CultureInfo.InvariantCulture,
                        $"leads into the gap the bridge at ({gp.X:0}, {gp.Y:0}, {gp.Z:0}) spans: a {regionArea[r]:0} m2 dead-end pit reached only by flights from the floor the bridge already joins");
                }
                else if (regionRewards[r] == 0)
                {
                    pocketFault = string.Create(CultureInfo.InvariantCulture,
                        $"its {side} leads into a {regionArea[r]:0} m2 dead end with nothing in it, reached only by flights from one floor");
                }
                else if (pocketFlights[r].Count > 1 && pocketFlights[r][0] != k)
                {
                    pocketFault = string.Create(CultureInfo.InvariantCulture,
                        $"a second flight into the same {regionArea[r]:0} m2 dead end from the same floor: one way in is enough");
                }

                break;
            }

            // Counted faults first (the kinds G30 counts), then the noted ones (listed with their reason, not counted: the
            // well-regarded levels have them too).
            double footClear = foot[1], topClear = top[1];
            (string? kind, string? fault) = (!topExit && topClear < FlightIntoSolid)
                ? ("into_obstacle", string.Create(CultureInfo.InvariantCulture, $"climbs into a wall or onto a solid: no floor beyond its top step and {topClear:0.0} m clear past it"))
                : topExit && Math.Min(footClear, topClear) < FlightBlocked
                ? ("blocked", string.Create(CultureInfo.InvariantCulture, $"its {(footClear < topClear ? "foot" : "top")} is blocked: a wall or prop {Math.Min(footClear, topClear):0.0} m from the {(footClear < topClear ? "first" : "top")} step"))
                : topExit && f.TopEnd < 0
                ? ("onto_nothing", "leads onto nothing: no landing beyond its top step, only a drop or a step off its side")
                : pocketFault is not null && gap is not null
                ? ("in_gap", pocketFault)
                : double.IsFinite(detour)
                ? ("redundant", string.Create(CultureInfo.InvariantCulture, $"redundant: another way up to the same floor is a {detour:0} m walk from its foot (it is {len:0} m long)"))
                : pocketFault is not null
                ? ("dead_end", pocketFault)
                : topExit && topArea < FlightDeadEnd && topRewards == 0
                ? ("dead_end", string.Create(CultureInfo.InvariantCulture, $"leads nowhere: its top lands on {topArea:0} m2 of floor with nothing on it"))
                : !topExit
                ? ("dead_end", "no floor beyond its top step (it ends in the open)")
                : ((string?)null, (string?)null);
            reads[k] = new FlightRead(f.Centre, f.FootEdge, f.TopEdge, new Vec3d(f.Ux, 0, f.Uz), rise, f.Run, f.Width, len, flats[f.Bot].Area,
                f.Top >= 0 ? flats[f.Top].Area : 0, detour, footClear, topClear, topExit, topArea, topRewards, pocketArea, gap, fault, kind);
        });
        FlightReads.AddRange(reads.OrderBy(r => r.Centre.X).ThenBy(r => r.Centre.Z).ThenBy(r => r.Centre.Y));
    }

    /// <summary>Reads the route ledges (see the file header) into <see cref="LedgeReads"/>.</summary>
    private void ReadLedges()
    {
        LedgeReads.Clear();
        FloorLevels();
        List<FlatPiece> flats = _flats!;
        int[] flatOf = _flatOf!;
        var steps = new Dictionary<(int Lo, int Hi), List<(int Lo, int Hi)>>();
        foreach (int i in _w.PlayIdx)
        {
            if (!OnLanding(i) || flats[flatOf[i]].Area < LedgeMinLanding)
            {
                continue;
            }

            foreach (WalkEdge e in _w.Adj[i])
            {
                int j = e.To;
                if (e.Link || e.Drop || !_w.Play[j] || !OnLanding(j) || flatOf[j] == flatOf[i] || flats[flatOf[j]].Area < LedgeMinLanding
                    || _s[j].P.Y - _s[i].P.Y < LedgeMinStep)
                {
                    continue;
                }

                var key = (flatOf[i], flatOf[j]);
                if (!steps.TryGetValue(key, out List<(int, int)>? l))
                {
                    steps[key] = l = new List<(int, int)>();
                }

                l.Add((i, j));
            }
        }

        var none = new bool[_s.Count];
        foreach (KeyValuePair<(int Lo, int Hi), List<(int Lo, int Hi)>> kv in steps.OrderBy(k => k.Key.Lo).ThenBy(k => k.Key.Hi))
        {
            _ct.ThrowIfCancellationRequested();
            List<(int Lo, int Hi)> edges = kv.Value;
            if (edges.Count < 6)
            {
                continue; // a corner touch, not a passage
            }

            double narrow = edges.Count(p => _s[p.Lo].Corridor || _s[p.Lo].Width < 4) / (double)edges.Count;
            if (narrow < 0.8)
            {
                continue; // the edge of a dais, terrace or pit in an open space, not a passage between spaces
            }

            (int a, int b) = edges[edges.Count / 2];
            var cut = new HashSet<(int, int)>(edges.SelectMany(p => new[] { p, (p.Hi, p.Lo) }));
            if (double.IsFinite(FootWalk(a, i => i == b, none, 60, (u, v) => cut.Contains((u, v)))))
            {
                continue; // a way round on foot nearby: the step is not the route
            }

            double step = flats[kv.Key.Hi].Y - flats[kv.Key.Lo].Y;
            double width = edges.Select(p => p.Lo).Distinct().Count() * _g;
            var pos = new Vec3d(edges.Average(p => _s[p.Lo].P.X), flats[kv.Key.Lo].Y, edges.Average(p => _s[p.Lo].P.Z));
            LedgeReads.Add(new LedgeRead(pos, step, width, flats[kv.Key.Lo].Y, flats[kv.Key.Hi].Y, flats[kv.Key.Lo].Area, flats[kv.Key.Hi].Area, narrow));
        }
    }

    private static JsonObject FlightJson(FlightRead f) => new()
    {
        ["pos"] = P3(f.Centre),
        ["foot"] = P3(f.Foot),
        ["top"] = P3(f.Top),
        ["dir"] = new JsonArray(R(f.Dir.X), R(f.Dir.Z)),
        ["rise"] = R(f.Rise),
        ["run"] = R(f.Run),
        ["width"] = R(f.Width),
        ["length"] = R(f.Length),
        ["foot_landing"] = R(f.FootLanding),
        ["top_landing"] = R(f.TopLanding),
        ["detour"] = double.IsFinite(f.Detour) ? R(f.Detour) : null,
        ["foot_clear"] = R(f.FootClear),
        ["top_clear"] = R(f.TopClear),
        ["top_exit"] = f.TopExit,
        ["top_leads_to"] = R(f.TopArea),
        ["pocket"] = double.IsNaN(f.PocketArea) ? null : R(f.PocketArea),
        ["bridge_gap"] = f.Gap is { } g ? P3(g) : null,
        ["role"] = f.Fault is null ? "route" : null,
        ["kind"] = f.Kind,
        ["counted"] = f.Kind is { } kind && CountedFlightFaults.Contains(kind),
        ["fault"] = f.Fault,
    };

    private static JsonObject LedgeJson(LedgeRead l) => new()
    {
        ["pos"] = P3(l.Pos),
        ["step"] = R(l.Step),
        ["width"] = R(l.Width),
        ["low"] = R(l.Low),
        ["high"] = R(l.High),
        ["low_landing"] = R(l.LowArea),
        ["high_landing"] = R(l.HighArea),
        ["fault"] = string.Create(CultureInfo.InvariantCulture,
            $"a {l.Step:0.0#} m {(l.Step > FootStep ? "ledge to hop" : "step")} between two floors of their own in a doorway or corridor, with no way round on foot: level the floors, or join them with a ramp or stairs"),
    };

    /// <summary>Spaces of at least this area (m²) are the areas the connection reading counts.</summary>
    public const double ConnectionMinArea = 30;

    /// <summary>A space with at most this many ways out to other spaces is poorly connected.</summary>
    public const int ConnectionFew = 2;

    /// <summary>Per area (a space of at least <see cref="ConnectionMinArea"/>): its ways out (doorways, corridors, drops, links: distinct connector pieces or neighbouring spaces that lead to another area) and the other areas they reach.</summary>
    internal List<(int Node, int Exits, int Linked)> AreaConnections { get; } = new();

    /// <summary>
    /// The connection reading (G32): every area's ways out and the areas they reach through connectors, the share of the
    /// area floor in areas with at most <see cref="ConnectionFew"/> ways out, and the area-weighted mean.
    /// </summary>
    private JsonObject ConnectionsReport()
    {
        AreaConnections.Clear();
        SpaceGraph sg = Spaces;
        bool Big(int n) => sg.IsSpace[n] && sg.Area[n] >= ConnectionMinArea;

        // The big areas a connector piece reaches without passing through a big area, memoised per piece of connectors.
        var reachOf = new Dictionary<int, HashSet<int>>();
        var pieceOf = new int[sg.N];
        Array.Fill(pieceOf, -1);
        for (int c = 0; c < sg.N; c++)
        {
            if (Big(c) || pieceOf[c] >= 0)
            {
                continue;
            }

            var bigs = new HashSet<int>();
            var st = new Stack<int>();
            st.Push(c);
            pieceOf[c] = c;
            while (st.Count > 0)
            {
                foreach (int v in sg.Graph.Adj[st.Pop()])
                {
                    if (Big(v))
                    {
                        bigs.Add(v);
                    }
                    else if (pieceOf[v] < 0)
                    {
                        pieceOf[v] = c;
                        st.Push(v);
                    }
                }
            }

            reachOf[c] = bigs;
        }

        double total = 0, low = 0, wExits = 0, wLinked = 0;
        var list = new JsonArray();
        foreach (int n in Enumerable.Range(0, sg.N).Where(Big).OrderByDescending(n => sg.Area[n]).ThenBy(n => n))
        {
            var linked = new HashSet<int>();
            var exits = new HashSet<int>();
            foreach (int v in sg.Graph.Adj[n])
            {
                if (Big(v))
                {
                    exits.Add(-1 - v);
                    linked.Add(v);
                }
                else if (reachOf[pieceOf[v]].Any(b => b != n))
                {
                    exits.Add(v); // each connector node it touches that leads on is one way out
                    linked.UnionWith(reachOf[pieceOf[v]].Where(b => b != n));
                }
            }

            AreaConnections.Add((n, exits.Count, linked.Count));
            double a = sg.Area[n];
            total += a;
            wExits += a * exits.Count;
            wLinked += a * linked.Count;
            if (exits.Count <= ConnectionFew)
            {
                low += a;
            }

            list.Add(new JsonObject
            {
                ["pos"] = P3(sg.Cent[n]),
                ["area"] = R(a),
                ["ways_out"] = exits.Count,
                ["areas_reached"] = linked.Count,
            });
        }

        return new JsonObject
        {
            ["areas"] = AreaConnections.Count,
            ["ways_out_mean"] = total > 0 ? R(wExits / total) : null,
            ["areas_reached_mean"] = total > 0 ? R(wLinked / total) : null,
            ["poorly_connected_share"] = total > 0 ? R(low / total) : null,
            ["poorly_connected_count"] = AreaConnections.Count(c => c.Exits <= ConnectionFew),
            ["list"] = list,
        };
    }
}
