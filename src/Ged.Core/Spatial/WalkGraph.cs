using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Spatial;

/// <summary>One walk-graph edge out of a floor sample.</summary>
/// <param name="To">The sample it leads to.</param>
/// <param name="Drop">A one-way drop (more than a step down).</param>
/// <param name="Axis">Along a lattice axis (the coarse graphs connect cells only through axis edges and links).</param>
/// <param name="Link">A movement link (lift, jump pad, teleport) rather than walking.</param>
internal readonly record struct WalkEdge(int To, bool Drop, bool Axis, bool Link = false);

/// <summary>A movement link the walk graph traverses besides walking (DESIGN-QUALITY-SPEC §(e) E1).</summary>
/// <param name="Kind"><c>lift</c>, <c>jump_pad</c> or <c>teleport</c>.</param>
/// <param name="From">The world point it starts at.</param>
/// <param name="To">The world point it ends at.</param>
/// <param name="OneWay">True for jump pads and teleports; lifts run both ways.</param>
/// <param name="Uid">The object behind it (mover group's first keyframe, push region or trigger).</param>
public sealed record MovementLink(string Kind, Vec3d From, Vec3d To, bool OneWay, int Uid);

/// <summary>
/// The floor-lattice walk graph (DESIGN-QUALITY-SPEC §0, §(e) E1): 0.5 m lattice samples from
/// <see cref="SpaceSampler"/>; neighbours (8-connected) join when the height step is at most 1.5 m (Alpine
/// <c>waypoints.h:41</c>, <c>kRouteMaxUpwardStepHeight</c>) and nothing blocks a body-height ray between them; steps of
/// 1.5–8 m down are one-way drops when the space above the lower sample is open. Movement links (lifts from mover
/// keyframes, jump pads, teleports) add edges where they are determinable. Playable floor = the components holding a
/// spawn, or at least 3 items, plus what one-way links (jump pads, teleports) lead to from playable floor.
/// </summary>
internal sealed class WalkGraph
{
    /// <summary>Largest walkable step up or down (Alpine waypoints.h:41, kRouteMaxUpwardStepHeight).</summary>
    public const double MaxStep = 1.5;

    /// <summary>Largest drop the walk graph follows (one way).</summary>
    public const double MaxDrop = 8.0;

    private readonly SpaceSampler _sp;
    private readonly IReadOnlyList<SpaceSampler.Sample> _s;
    private readonly Dictionary<(int, int), List<int>> _lat = new();

    public WalkGraph(SpaceSampler sampler, SpatialObjects objects, IReadOnlyList<MovementLink> links, int parallelism, CancellationToken ct, bool allFloor = false, bool standable = false)
    {
        _sp = sampler;
        _s = sampler.Samples;
        G = sampler.Pitch;
        int n = _s.Count;
        Adj = new List<WalkEdge>[n];
        for (int i = 0; i < n; i++)
        {
            Adj[i] = new List<WalkEdge>();
            if (!_s[i].Valid)
            {
                continue;
            }

            (int, int) key = (_s[i].I, _s[i].K);
            if (!_lat.TryGetValue(key, out List<int>? l))
            {
                _lat[key] = l = new List<int>();
            }

            l.Add(i);
        }

        // Candidate edges per sample, found in parallel, then added in sample order (the result is the same as a
        // sequential sweep: adjacency lists keep the sweep's order).
        int[] di = { 1, 0, 1, 1 };
        int[] dk = { 0, 1, 1, -1 };
        var found = new List<(int B, int Q, int Kind)>?[n];
        void Probe(int a)
        {
            SpaceSampler.Sample sa = _s[a];
            if (!sa.Valid)
            {
                return;
            }

            List<(int, int, int)>? res = null;
            for (int q = 0; q < 4; q++)
            {
                if (!_lat.TryGetValue((sa.I + di[q], sa.K + dk[q]), out List<int>? list))
                {
                    continue;
                }

                foreach (int b in list)
                {
                    SpaceSampler.Sample sb = _s[b];
                    double dy = sb.P.Y - sa.P.Y;
                    if (Math.Abs(dy) <= MaxStep)
                    {
                        if (!Clear(sa.P, sb.P, Math.Max(sa.P.Y, sb.P.Y) + 0.5))
                        {
                            continue;
                        }

                        (res ??= new()).Add((b, q, 0));
                    }
                    else if (Math.Abs(dy) <= MaxDrop)
                    {
                        int hi = dy < 0 ? a : b, lo = dy < 0 ? b : a;
                        Vec3d ph = _s[hi].P;
                        Vec3d pl = _s[lo].P;
                        if (!Clear(ph, pl, ph.Y + 0.5))
                        {
                            continue;
                        }

                        double drop = ph.Y + 0.5 - pl.Y;
                        double? hit = _sp.Cast(new Vec3d(pl.X, ph.Y + 0.5, pl.Z), new Vec3d(0, -1, 0), drop + 0.2, SpaceSampler.HitFilter.UpFacing);
                        if (hit is double h && h < drop - 0.15)
                        {
                            continue;
                        }

                        (res ??= new()).Add((b, q, 1));
                    }
                }
            }

            found[a] = res;
        }

        if (parallelism > 1)
        {
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, Probe);
        }
        else
        {
            for (int a = 0; a < n; a++)
            {
                if ((a & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                Probe(a);
            }
        }

        var uf = new UnionFind(n);
        for (int a = 0; a < n; a++)
        {
            if (found[a] is not { } list)
            {
                continue;
            }

            foreach ((int b, int q, int kind) in list)
            {
                bool axis = q < 2;
                if (kind == 0)
                {
                    Adj[a].Add(new WalkEdge(b, false, axis));
                    Adj[b].Add(new WalkEdge(a, false, axis));
                }
                else
                {
                    double dy = _s[b].P.Y - _s[a].P.Y;
                    int hi = dy < 0 ? a : b, lo = dy < 0 ? b : a;
                    Adj[hi].Add(new WalkEdge(lo, true, axis));
                }

                uf.Union(a, b);
            }
        }

        // Movement links: snapped at both ends, one-way or both ways.
        foreach (MovementLink link in links)
        {
            int from = Snap(link.From), to = Snap(link.To);
            if (from < 0 || to < 0 || from == to)
            {
                continue;
            }

            Adj[from].Add(new WalkEdge(to, false, false, Link: true));
            if (!link.OneWay)
            {
                Adj[to].Add(new WalkEdge(from, false, false, Link: true));
                uf.Union(from, to);
            }

            Links.Add((link, from, to));
        }

        Comp = new int[n];
        for (int i = 0; i < n; i++)
        {
            Comp[i] = uf.Find(i);
        }

        // Playable floor: the components holding a spawn, plus components holding at least 3 items (reached by moves
        // the walk graph does not model); when no spawn snaps, the first item component seeds it.
        var seeds = new HashSet<int>();
        SpawnSample = new int[objects.Spawns.Count];
        for (int i = 0; i < objects.Spawns.Count; i++)
        {
            int si = Snap(objects.Spawns[i].P);
            SpawnSample[i] = si;
            if (si >= 0)
            {
                seeds.Add(Comp[si]);
            }
        }

        var itemComp = new Dictionary<int, int>();
        foreach (var it in objects.Items)
        {
            int si = Snap(it.P);
            if (si >= 0)
            {
                itemComp[Comp[si]] = itemComp.GetValueOrDefault(Comp[si]) + 1;
            }
        }

        foreach (KeyValuePair<int, int> kv in itemComp)
        {
            if (kv.Value >= 3 || seeds.Count == 0)
            {
                seeds.Add(kv.Key);
            }
        }

        // The kit's spawn planner: every floor component of at least 16 m² is playable.
        if (allFloor)
        {
            var size = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                if (_s[i].Valid)
                {
                    size[Comp[i]] = size.GetValueOrDefault(Comp[i]) + 1;
                }
            }

            foreach (KeyValuePair<int, int> kv in size)
            {
                if (kv.Value * G * G >= 16)
                {
                    seeds.Add(kv.Key);
                }
            }
        }

        // A one-way link (jump pad, teleport) makes its destination playable when its start is, never the reverse:
        // a pad that launches players back up from an out-of-play floor does not make that floor playable.
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach ((MovementLink link, int from, int to) in Links)
            {
                if (link.OneWay && seeds.Contains(Comp[from]) && seeds.Add(Comp[to]))
                {
                    grew = true;
                }
            }
        }

        Play = new bool[n];
        bool[]? reach = standable ? Reachable(objects, seeds, parallelism, ct, allFloor) : null;
        for (int i = 0; i < n; i++)
        {
            if (_s[i].Valid && seeds.Contains(Comp[i]) && (reach is null || reach[i]))
            {
                Play[i] = true;
                PlayIdx.Add(i);
            }
        }

        PlayArea = PlayIdx.Count * G * G;
        Standable = standable;
    }

    /// <summary>Half the narrowest strip counted as standable floor (m): support must reach this far to one side or the other.</summary>
    public const double SupportReach = 0.3;

    /// <summary>The height difference (m) within which neighbouring floor supports a sample (a stair riser, a rail on a deck).</summary>
    public const double SupportRise = 0.6;

    /// <summary>True when playable floor is the standable floor reached from the spawns (<see cref="SpatialOptions.StandableFloor"/>).</summary>
    public bool Standable { get; }

    /// <summary>
    /// The standable floor reached from the spawns: a breadth-first walk from every spawn's sample (and, in a component no
    /// spawn snaps to, from its items' samples) along the walk edges in their direction — walking both ways, drops and
    /// one-way links only forward — through supported samples only (<see cref="Supported"/>). Of the supported floor it
    /// does not reach, only prop-sized tops left by drops of at least <see cref="RoofMinDrop"/> are out. With
    /// <paramref name="allFloor"/> (the kit's spawn planner, measuring a level before it has spawns) the walk also starts
    /// from the floor the spawns will stand on: the largest supported piece of every playable component and every piece
    /// larger than <see cref="RoofMaxArea"/>.
    /// </summary>
    private bool[] Reachable(SpatialObjects objects, HashSet<int> seedComps, int parallelism, CancellationToken ct, bool allFloor = false)
    {
        int n = _s.Count;
        var supported = new bool[n];
        void Check(int i) => supported[i] = _s[i].Valid && seedComps.Contains(Comp[i]) && Supported(i);
        if (parallelism > 1)
        {
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, Check);
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                Check(i);
            }
        }

        var spawnComps = new HashSet<int>();
        var sources = new List<int>();
        foreach (int si in SpawnSample)
        {
            if (si >= 0)
            {
                spawnComps.Add(Comp[si]);
                sources.Add(si);
            }
        }

        foreach (var it in objects.Items)
        {
            int si = Snap(it.P);
            if (si >= 0 && seedComps.Contains(Comp[si]) && !spawnComps.Contains(Comp[si]))
            {
                sources.Add(si);
            }
        }

        if (allFloor)
        {
            sources.AddRange(MainFloor(supported));
        }

        var reach = new bool[n];
        var queue = new Queue<int>();
        foreach (int s in sources)
        {
            // A spawn or item stands where it was placed, even on an unsupported spot: it seeds the walk.
            if (!reach[s])
            {
                reach[s] = true;
                queue.Enqueue(s);
            }
        }

        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (WalkEdge e in Adj[u])
            {
                if (!reach[e.To] && supported[e.To])
                {
                    reach[e.To] = true;
                    queue.Enqueue(e.To);
                }
            }
        }

        // The walk graph does not model every way up (a jump, a crouch-jump, a ladder, a lift it cannot snap): only a
        // prop-sized top — RoofMinArea to RoofMaxArea, every way off it a drop of at least RoofMinDrop — counts as out of
        // reach (a container or stack top, a hut roof). Any other supported floor the walk did not reach is kept: stock
        // levels have whole areas reached by moves the graph does not model, and the grader's calibration keeps them.
        var done = new bool[n];
        for (int s0 = 0; s0 < n; s0++)
        {
            if (reach[s0] || done[s0] || !supported[s0])
            {
                continue;
            }

            var piece = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(s0);
            done[s0] = true;
            double minDrop = double.PositiveInfinity;
            bool walksOn = false;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                piece.Add(u);
                foreach (WalkEdge e in Adj[u])
                {
                    if (reach[e.To])
                    {
                        if (e.Drop)
                        {
                            minDrop = Math.Min(minDrop, _s[u].P.Y - _s[e.To].P.Y);
                        }
                        else
                        {
                            walksOn = true; // a one-way link out, or a walk edge into reached floor the BFS could not take back
                        }
                    }
                    else if (!e.Drop && !e.Link && supported[e.To] && !done[e.To])
                    {
                        done[e.To] = true;
                        q.Enqueue(e.To);
                    }
                }
            }

            double pieceArea = piece.Count * G * G;
            if (walksOn || pieceArea > RoofMaxArea || pieceArea < RoofMinArea || minDrop < RoofMinDrop)
            {
                foreach (int i in piece)
                {
                    reach[i] = true;
                }
            }
        }

        return reach;
    }

    /// <summary>
    /// The samples of the floor spawns stand on when there are none yet: the supported pieces (joined by walk edges, not
    /// drops or links) larger than <see cref="RoofMaxArea"/>, and the largest piece of each component.
    /// </summary>
    private List<int> MainFloor(bool[] supported)
    {
        int n = _s.Count;
        var done = new bool[n];
        var largest = new Dictionary<int, List<int>>();
        var bigComps = new HashSet<int>();
        var result = new List<int>();
        for (int s0 = 0; s0 < n; s0++)
        {
            if (done[s0] || !supported[s0])
            {
                continue;
            }

            var piece = new List<int>();
            var q = new Queue<int>();
            q.Enqueue(s0);
            done[s0] = true;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                piece.Add(u);
                foreach (WalkEdge e in Adj[u])
                {
                    if (!e.Drop && !e.Link && supported[e.To] && !done[e.To])
                    {
                        done[e.To] = true;
                        q.Enqueue(e.To);
                    }
                }
            }

            if (piece.Count * G * G > RoofMaxArea)
            {
                result.AddRange(piece);
                bigComps.Add(Comp[s0]);
            }
            else if (!largest.TryGetValue(Comp[s0], out List<int>? best) || piece.Count > best.Count)
            {
                largest[Comp[s0]] = piece;
            }
        }

        foreach (KeyValuePair<int, List<int>> kv in largest)
        {
            if (!bigComps.Contains(kv.Key))
            {
                result.AddRange(kv.Value);
            }
        }

        return result;
    }

    /// <summary>The largest floor piece (m²) that counts as an unreachable top: a prop, stack or roof, not a part of the level.</summary>
    public const double RoofMaxArea = 80;

    /// <summary>The smallest floor piece (m²) that counts as an unreachable top (smaller pieces, as the space graph's, are left as they are).</summary>
    public const double RoofMinArea = 16;

    /// <summary>The smallest drop (m) off an unreachable top: anything lower may be reached by a jump the walk graph does not model.</summary>
    public const double RoofMinDrop = 2.5;

    /// <summary>
    /// True when sample <paramref name="i"/> stands on floor at least about 0.6 m across along both lattice axes: on each
    /// axis a neighbouring lattice sample within <see cref="SupportRise"/> of its height, or floor found by a short downward
    /// probe <see cref="SupportReach"/> to one side or the other. A wall-trim band, a rail, beam or pipe top narrower than
    /// that is not standable; a stair tread, a rail on a deck or a skirting step is (the floor beside it is within a step).
    /// </summary>
    internal bool Supported(int i)
    {
        SpaceSampler.Sample s = _s[i];
        return Axis(1, 0) && Axis(0, 1);

        bool Axis(int di, int dk)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                if (_lat.TryGetValue((s.I + (sign * di), s.K + (sign * dk)), out List<int>? list))
                {
                    foreach (int j in list)
                    {
                        if (Math.Abs(_s[j].P.Y - s.P.Y) <= SupportRise)
                        {
                            return true;
                        }
                    }
                }
            }

            for (int sign = -1; sign <= 1; sign += 2)
            {
                var o = new Vec3d(s.P.X + (sign * di * SupportReach), s.P.Y + SupportRise, s.P.Z + (sign * dk * SupportReach));
                if (_sp.Cast(o, new Vec3d(0, -1, 0), 2 * SupportRise, SpaceSampler.HitFilter.UpFacing) is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Edges out of each sample.</summary>
    public List<WalkEdge>[] Adj { get; }

    /// <summary>Union-find component of each sample (links and drops join components).</summary>
    public int[] Comp { get; }

    /// <summary>True for playable samples.</summary>
    public bool[] Play { get; }

    /// <summary>The playable samples, ascending.</summary>
    public List<int> PlayIdx { get; } = new();

    /// <summary>Playable floor area (m²).</summary>
    public double PlayArea { get; }

    /// <summary>The lattice pitch (m).</summary>
    public double G { get; }

    /// <summary>The sample each spawn snapped to (−1 = none), in <see cref="SpatialObjects.Spawns"/> order.</summary>
    public int[] SpawnSample { get; }

    /// <summary>The movement links that snapped at both ends, with their samples.</summary>
    public List<(MovementLink Link, int From, int To)> Links { get; } = new();

    public IReadOnlyList<SpaceSampler.Sample> S => _s;

    public Dictionary<(int, int), List<int>> Lattice => _lat;

    /// <summary>True when nothing blocks a horizontal ray from above <paramref name="a"/> to above <paramref name="b"/> at height <paramref name="y"/>.</summary>
    private bool Clear(Vec3d a, Vec3d b, double y)
    {
        var d = new Vec3d(b.X - a.X, 0, b.Z - a.Z);
        double len = d.Length;
        if (len < 1e-6)
        {
            return true;
        }

        var o = new Vec3d(a.X, y, a.Z);
        return !_sp.Blocked(o, d.Scale(1 / len), len, SpaceSampler.HitFilter.Any);
    }

    /// <summary>The valid sample under <paramref name="p"/> (2 m around, then 4 m), or −1.</summary>
    public int Snap(Vec3d p)
    {
        int s = _sp.NearestValid(p, 2.0, 1.0, 3.5);
        if (s < 0)
        {
            s = _sp.NearestValid(p, 4.0, 1.5, 6.0);
        }

        return s;
    }

    /// <summary>The playable sample under <paramref name="p"/>: <see cref="Snap"/> when playable, else the nearest playable within 6 m (−1 = none).</summary>
    public int SnapPlay(Vec3d p)
    {
        int s = Snap(p);
        if (s >= 0 && Play[s])
        {
            return s;
        }

        double best = 36;
        int bi = -1;
        foreach (int i in PlayIdx)
        {
            Vec3d q = _s[i].P;
            double dx = q.X - p.X, dz = q.Z - p.Z, dy = q.Y - p.Y;
            if (dy > 1.5 || dy < -6)
            {
                continue;
            }

            double d = (dx * dx) + (dz * dz);
            if (d < best)
            {
                best = d;
                bi = i;
            }
        }

        return bi;
    }

    /// <summary>Walk distance from the source samples to every sample (playable samples only; ∞ = unreachable).</summary>
    public double[] Dijkstra(IEnumerable<int> sources) => Dijkstra(sources, out _);

    /// <summary>As <see cref="Dijkstra(IEnumerable{int})"/>, with the predecessor of each sample on its shortest path (−1 at sources).</summary>
    public double[] Dijkstra(IEnumerable<int> sources, out int[] prev)
    {
        var d = new double[_s.Count];
        prev = new int[_s.Count];
        Array.Fill(d, double.PositiveInfinity);
        Array.Fill(prev, -1);
        var pq = new PriorityQueue<int, double>();
        foreach (int s in sources)
        {
            d[s] = 0;
            pq.Enqueue(s, 0);
        }

        while (pq.TryDequeue(out int u, out double du))
        {
            if (du > d[u])
            {
                continue;
            }

            foreach (WalkEdge e in Adj[u])
            {
                if (!Play[e.To])
                {
                    continue;
                }

                double w = _s[u].P.Sub(_s[e.To].P).Length;
                if (du + w < d[e.To])
                {
                    d[e.To] = du + w;
                    prev[e.To] = u;
                    pq.Enqueue(e.To, d[e.To]);
                }
            }
        }

        return d;
    }

    /// <summary>The XZ lattice cell a world point falls in at this pitch.</summary>
    public (int I, int K) CellOf(Vec3d p) => ((int)Math.Floor(p.X / G), (int)Math.Floor(p.Z / G));
}

/// <summary>
/// The coarse cell graph (DESIGN-QUALITY-SPEC §0): a node is a connected piece of playable floor inside a C × C metre
/// cell; nodes join through lattice-axis walk edges and movement links. Loops are the cycle rank minus the filled
/// 2 × 2 squares.
/// </summary>
internal sealed class CellGraph
{
    public readonly UGraph Graph = new();
    public readonly List<double> Area = new();
    public readonly List<Vec3d> Cent = new();
    public readonly List<double> Headroom = new();
    public readonly List<(int Cx, int Cz)> CellOf = new();
    public readonly List<List<int>> Members = new();
    public readonly int[] NodeOfSample;
    private readonly Dictionary<(int, int), List<int>> _byCell = new();
    private readonly double _c;
    private readonly int _squares;

    public CellGraph(WalkGraph w, double c)
    {
        _c = c;
        IReadOnlyList<SpaceSampler.Sample> s = w.S;
        NodeOfSample = new int[s.Count];
        Array.Fill(NodeOfSample, -1);
        (int, int) Cell(int i) => ((int)Math.Floor(s[i].P.X / c), (int)Math.Floor(s[i].P.Z / c));
        var uf = new UnionFind(s.Count);
        foreach (int i in w.PlayIdx)
        {
            foreach (WalkEdge e in w.Adj[i])
            {
                if (!e.Link && w.Play[e.To] && Cell(e.To) == Cell(i))
                {
                    uf.Union(i, e.To);
                }
            }
        }

        var rootNode = new Dictionary<int, int>();
        foreach (int i in w.PlayIdx)
        {
            int r = uf.Find(i);
            if (!rootNode.TryGetValue(r, out int n))
            {
                n = Graph.AddNode();
                rootNode[r] = n;
                Members.Add(new List<int>());
                CellOf.Add(Cell(i));
                if (!_byCell.TryGetValue(Cell(i), out List<int>? l))
                {
                    _byCell[Cell(i)] = l = new List<int>();
                }

                l.Add(n);
            }

            NodeOfSample[i] = n;
            Members[n].Add(i);
        }

        foreach (List<int> m in Members)
        {
            Area.Add(m.Count * w.G * w.G);
            Cent.Add(new Vec3d(m.Average(i => s[i].P.X), m.Average(i => s[i].P.Y), m.Average(i => s[i].P.Z)));
            double[] hr = m.Where(i => !s[i].OpenSky).Select(i => s[i].Headroom).OrderBy(x => x).ToArray();
            Headroom.Add(hr.Length > m.Count / 2 ? Percentiles.Type7(hr, 0.5) : double.NaN);
        }

        foreach (int i in w.PlayIdx)
        {
            foreach (WalkEdge e in w.Adj[i])
            {
                if (!(e.Axis || e.Link) || !w.Play[e.To])
                {
                    continue;
                }

                int a = NodeOfSample[i], b = NodeOfSample[e.To];
                if (a != b)
                {
                    Graph.AddEdge(a, b);
                }
            }
        }

        // Filled squares: four cell nodes around a lattice corner joined in a ring are open floor, not a loop.
        for (int a = 0; a < N; a++)
        {
            (int cx, int cz) = CellOf[a];
            foreach (int b in Graph.Adj[a].Where(x => CellOf[x] == (cx + 1, cz)))
            {
                foreach (int cc in Graph.Adj[a].Where(x => CellOf[x] == (cx, cz + 1)))
                {
                    foreach (int d in Graph.Adj[b].Where(x => CellOf[x] == (cx + 1, cz + 1)))
                    {
                        if (Graph.Adj[cc].Contains(d))
                        {
                            _squares++;
                        }
                    }
                }
            }
        }
    }

    public int N => Area.Count;

    public int E => Graph.EdgeCount;

    public double CellSize => _c;

    /// <summary>Loops: cycle rank (E − V + components) minus filled squares, at least 0.</summary>
    public int B1() => Math.Max(0, E - N + Graph.Components() - _squares);

    /// <summary>Nodes whose centroid lies within <paramref name="r"/> of <paramref name="p"/>; the nearest one when none does.</summary>
    public List<int> NodesNear(Vec3d p, double r)
    {
        var l = Enumerable.Range(0, N).Where(i => Cent[i].Sub(p).Length <= r).ToList();
        if (l.Count == 0 && N > 0)
        {
            l.Add(Enumerable.Range(0, N).OrderBy(i => Cent[i].Sub(p).Length).First());
        }

        return l;
    }

    /// <summary>The node in <paramref name="p"/>'s cell whose centroid height is nearest (within −below … +above), or −1.</summary>
    public int NodeAt(Vec3d p, double below, double above)
    {
        var key = ((int)Math.Floor(p.X / _c), (int)Math.Floor(p.Z / _c));
        if (!_byCell.TryGetValue(key, out List<int>? l))
        {
            return -1;
        }

        int best = -1;
        double bd = double.MaxValue;
        foreach (int n in l)
        {
            double dy = p.Y - Cent[n].Y;
            if (dy < -below || dy > above)
            {
                continue;
            }

            if (Math.Abs(dy) < bd)
            {
                bd = Math.Abs(dy);
                best = n;
            }
        }

        return best;
    }

    /// <summary>The world centre of a node's cell at its floor height (for locations an agent can act on).</summary>
    public Vec3d CellCentre(int n) => new((CellOf[n].Cx + 0.5) * _c, Cent[n].Y, (CellOf[n].Cz + 0.5) * _c);

    /// <summary>Route-centrality traffic: how many random shortest node-to-node paths pass each node.</summary>
    public double[] Traffic(int pairs, Random rng)
    {
        var t = new double[N];
        if (N < 2)
        {
            return t;
        }

        var d = new double[N];
        var prev = new int[N];
        for (int k = 0; k < pairs; k++)
        {
            int a = rng.Next(N), b = rng.Next(N);
            if (a == b)
            {
                continue;
            }

            Array.Fill(d, double.PositiveInfinity);
            Array.Fill(prev, -1);
            d[a] = 0;
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(a, 0);
            while (pq.TryDequeue(out int u, out double du))
            {
                if (u == b)
                {
                    break;
                }

                if (du > d[u])
                {
                    continue;
                }

                foreach (int v in Graph.Adj[u])
                {
                    double w = du + Cent[u].Sub(Cent[v]).Length;
                    if (w < d[v])
                    {
                        d[v] = w;
                        prev[v] = u;
                        pq.Enqueue(v, w);
                    }
                }
            }

            for (int v = b; v >= 0 && v != a; v = prev[v])
            {
                t[v]++;
            }
        }

        return t;
    }

    /// <summary>Chokepoints and pockets (DESIGN-QUALITY-SPEC §(d) G12/G13), computed once.</summary>
    public CutReport Cuts => _cuts ??= CutReport.Compute(Graph, Area);

    private CutReport? _cuts;
}

/// <summary>
/// Articulation analysis of a coarse graph: a chokepoint is a node whose removal cuts off at least 10% of the area
/// (besides the largest remaining piece); pockets are the pieces under 10% that such cuts leave behind.
/// </summary>
internal sealed class CutReport
{
    private CutReport()
    {
    }

    public HashSet<int> Articulation { get; private set; } = new();

    /// <summary>Chokepoint nodes with the area share each cuts off (the largest piece it separates from the rest).</summary>
    public List<(int Node, double CutShare)> Chokes { get; } = new();

    public bool[] Pocket { get; private set; } = Array.Empty<bool>();

    public double PocketArea { get; private set; }

    public static CutReport Compute(UGraph g, IReadOnlyList<double> area)
    {
        var r = new CutReport { Articulation = g.Articulation(), Pocket = new bool[g.N] };
        double total = area.Sum();
        foreach (int v in r.Articulation.OrderBy(x => x))
        {
            List<List<int>> comps = g.ComponentsWithout(v);
            if (comps.Count < 2)
            {
                continue;
            }

            List<(List<int> C, double Share)> ordered = comps.Select(c => (c, c.Sum(i => area[i]) / total)).OrderByDescending(x => x.Item2).ToList();
            double cut = ordered.Skip(1).Select(x => x.Share).DefaultIfEmpty(0).Max();
            if (cut >= 0.10)
            {
                r.Chokes.Add((v, cut));
            }

            foreach ((List<int> c, double share) in ordered.Skip(1))
            {
                if (share < 0.10)
                {
                    foreach (int i in c)
                    {
                        r.Pocket[i] = true;
                    }
                }
            }
        }

        r.PocketArea = total > 0 ? Enumerable.Range(0, g.N).Where(i => r.Pocket[i]).Sum(i => area[i]) / total : 0;
        return r;
    }

    /// <summary>Connected groups of pocket nodes: (nodes, area share).</summary>
    public List<(List<int> Nodes, double Share)> PocketGroups(UGraph g, IReadOnlyList<double> area)
    {
        double total = area.Sum();
        var seen = new bool[g.N];
        var groups = new List<(List<int>, double)>();
        for (int i = 0; i < g.N; i++)
        {
            if (!Pocket[i] || seen[i])
            {
                continue;
            }

            var comp = new List<int>();
            var st = new Stack<int>();
            st.Push(i);
            seen[i] = true;
            while (st.Count > 0)
            {
                int u = st.Pop();
                comp.Add(u);
                foreach (int v in g.Adj[u])
                {
                    if (Pocket[v] && !seen[v])
                    {
                        seen[v] = true;
                        st.Push(v);
                    }
                }
            }

            comp.Sort();
            groups.Add((comp, comp.Sum(n => area[n]) / Math.Max(1e-9, total)));
        }

        return groups.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1[0]).ToList();
    }
}

/// <summary>
/// The space/connector graph (DESIGN-QUALITY-SPEC §0): playable floor split into spaces (wide floor) and connectors
/// (corridor samples and gaps under 4 m wide); space pieces under 16 m² become connector.
/// </summary>
internal sealed class SpaceGraph
{
    public readonly UGraph Graph = new();
    public readonly List<bool> IsSpace = new();
    public readonly List<double> Area = new();
    public readonly List<Vec3d> Cent = new();
    public readonly List<List<int>> Members = new();
    private readonly int[] _nodeOf;

    public SpaceGraph(WalkGraph w)
    {
        IReadOnlyList<SpaceSampler.Sample> s = w.S;
        _nodeOf = new int[s.Count];
        Array.Fill(_nodeOf, -1);
        var cls = new bool[s.Count];
        foreach (int i in w.PlayIdx)
        {
            cls[i] = s[i].Corridor || s[i].Width < 4.0;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            var uf = new UnionFind(s.Count);
            foreach (int i in w.PlayIdx)
            {
                foreach (WalkEdge e in w.Adj[i])
                {
                    if (!e.Link && w.Play[e.To] && cls[i] == cls[e.To])
                    {
                        uf.Union(i, e.To);
                    }
                }
            }

            List<IGrouping<int, int>> groups = w.PlayIdx.GroupBy(i => uf.Find(i)).ToList();
            if (pass == 0)
            {
                foreach (IGrouping<int, int> g in groups)
                {
                    if (!cls[g.First()] && g.Count() * w.G * w.G < 16)
                    {
                        foreach (int i in g)
                        {
                            cls[i] = true;
                        }
                    }
                }

                continue;
            }

            foreach (IGrouping<int, int> g in groups)
            {
                int n = Graph.AddNode();
                List<int> m = g.ToList();
                Members.Add(m);
                IsSpace.Add(!cls[m[0]]);
                Area.Add(m.Count * w.G * w.G);
                Cent.Add(new Vec3d(m.Average(i => s[i].P.X), m.Average(i => s[i].P.Y), m.Average(i => s[i].P.Z)));
                foreach (int i in m)
                {
                    _nodeOf[i] = n;
                }
            }
        }

        foreach (int i in w.PlayIdx)
        {
            foreach (WalkEdge e in w.Adj[i])
            {
                if (!w.Play[e.To])
                {
                    continue;
                }

                int a = _nodeOf[i], b = _nodeOf[e.To];
                if (a != b)
                {
                    Graph.AddEdge(a, b);
                }
            }
        }
    }

    public int N => Area.Count;

    public int NodeOf(int sample) => sample >= 0 ? _nodeOf[sample] : -1;

    public CutReport Cuts => _cuts ??= CutReport.Compute(Graph, Area);

    private CutReport? _cuts;

    public bool InPocket(int sample)
    {
        int n = NodeOf(sample);
        return n >= 0 && Cuts.Pocket[n];
    }
}
