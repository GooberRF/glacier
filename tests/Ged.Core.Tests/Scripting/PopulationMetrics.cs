using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Analysis;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The population metrics of DESIGN-QUALITY-SPEC §a.3 measured on a sealed compiled level with the
/// exemplar study's rules (research/exemplar-study/tools/study/Program.cs): a 0.5 m floor lattice
/// (<see cref="SpaceSampler"/>), walk edges up/down 1.5 m with clear air 0.5 m above the higher floor,
/// one-way drops up to 8 m; dressing = detail / small / beam / slab brushes (longest side ≤ 24 m) plus
/// clutter and decals; spawn sight pairs within 30 m and exposure (share of floor within 40 m seen
/// from a spawn's eye). A test-side stand-in for the measurement core's spatial queries.
/// </summary>
internal sealed class PopulationMetrics
{
    private const double Pitch = 0.5;
    private const double Eye = 1.5;

    private readonly SpaceSampler _sp;
    private readonly List<SpaceSampler.Sample> _valid;
    private readonly int[] _comp;
    private readonly Dictionary<(int, int), List<int>> _lattice = new();
    private readonly List<int>[] _out;
    private readonly int[] _stepComp;

    public PopulationMetrics(CompiledLevel compiled)
    {
        _sp = SpaceSampler.Build(compiled.Geometry, CancellationToken.None);
        _sp.Measure(Pitch, 600_000);
        // Detail brushes do not carve the world faces they stand on, so a floor face runs on under every
        // prop: a sample whose first face straight up is up-facing (a prop's top seen from inside) is in
        // solid, not floor.
        _valid = _sp.Samples.Where(s => s.Valid && !InsideSolid(s.P)).ToList();
        for (int i = 0; i < _valid.Count; i++)
        {
            var key = (_valid[i].I, _valid[i].K);
            if (!_lattice.TryGetValue(key, out List<int>? l))
            {
                l = new List<int>();
                _lattice[key] = l;
            }

            l.Add(i);
        }

        // Union-find over walk edges (the study's BuildWalk).
        var parent = Enumerable.Range(0, _valid.Count).ToArray();
        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }

            return a;
        }

        int[] di = { 1, 0, 1, 1 };
        int[] dk = { 0, 1, 1, -1 };
        _out = new List<int>[_valid.Count];
        for (int i = 0; i < _valid.Count; i++)
        {
            _out[i] = new List<int>();
        }

        var stepParent = Enumerable.Range(0, _valid.Count).ToArray();
        int StepFind(int a)
        {
            while (stepParent[a] != a)
            {
                stepParent[a] = stepParent[stepParent[a]];
                a = stepParent[a];
            }

            return a;
        }

        for (int a = 0; a < _valid.Count; a++)
        {
            SpaceSampler.Sample sa = _valid[a];
            for (int q = 0; q < 4; q++)
            {
                if (!_lattice.TryGetValue((sa.I + di[q], sa.K + dk[q]), out List<int>? list))
                {
                    continue;
                }

                foreach (int b in list)
                {
                    SpaceSampler.Sample sb = _valid[b];
                    double dy = sb.P.Y - sa.P.Y;
                    if (Math.Abs(dy) <= 1.5)
                    {
                        if (Clear(sa.P, sb.P, Math.Max(sa.P.Y, sb.P.Y) + 0.5))
                        {
                            parent[Find(a)] = Find(b);
                            stepParent[StepFind(a)] = StepFind(b);
                            _out[a].Add(b);
                            _out[b].Add(a);
                        }
                    }
                    else if (Math.Abs(dy) <= 8.0)
                    {
                        Vec3d hi = dy < 0 ? sa.P : sb.P;
                        Vec3d lo = dy < 0 ? sb.P : sa.P;
                        if (Clear(hi, lo, hi.Y + 0.5))
                        {
                            parent[Find(a)] = Find(b);
                            _out[dy < 0 ? a : b].Add(dy < 0 ? b : a); // one way: down
                        }
                    }
                }
            }
        }

        _comp = new int[_valid.Count];
        _stepComp = new int[_valid.Count];
        for (int i = 0; i < _valid.Count; i++)
        {
            _comp[i] = Find(i);
            _stepComp[i] = StepFind(i);
        }
    }

    /// <summary>
    /// The reachable floor (m²): everything reachable along walk edges (steps both ways, drops only down)
    /// from the level's floors — the step-connected components of at least 16 m². Prop and pipe tops a
    /// player cannot climb onto are not reachable; low props they can step onto are.
    /// </summary>
    public double ReachableArea()
    {
        var size = new Dictionary<int, int>();
        foreach (int c in _stepComp)
        {
            size[c] = size.GetValueOrDefault(c) + 1;
        }

        var seen = new bool[_valid.Count];
        var queue = new Queue<int>();
        for (int i = 0; i < _valid.Count; i++)
        {
            if (size[_stepComp[i]] >= 64)
            {
                seen[i] = true;
                queue.Enqueue(i);
            }
        }

        int n = queue.Count;
        while (queue.Count > 0)
        {
            foreach (int j in _out[queue.Dequeue()])
            {
                if (!seen[j])
                {
                    seen[j] = true;
                    n++;
                    queue.Enqueue(j);
                }
            }
        }

        return n * Pitch * Pitch;
    }

    private bool InsideSolid(Vec3d p)
    {
        if (_sp.Cast(new Vec3d(p.X, p.Y + 0.02, p.Z), new Vec3d(0, 1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int face) is null || face < 0)
        {
            return false;
        }

        return _sp.Faces[face].N.Y > 0.1;
    }

    /// <summary>The floor area (m²) of every walk component of at least 8 m² (the walkable floor).</summary>
    public double WalkArea()
    {
        var size = new Dictionary<int, int>();
        foreach (int c in _comp)
        {
            size[c] = size.GetValueOrDefault(c) + 1;
        }

        return _comp.Count(c => size[c] >= 32) * Pitch * Pitch;
    }

    private bool Clear(Vec3d a, Vec3d b, double y)
    {
        var d = new Vec3d(b.X - a.X, 0, b.Z - a.Z);
        double len = d.Length;
        if (len < 1e-6)
        {
            return true;
        }

        return _sp.Cast(new Vec3d(a.X, y, a.Z), d.Scale(1 / len), len, SpaceSampler.HitFilter.Any) is null;
    }

    private bool Los(Vec3d a, Vec3d b)
    {
        Vec3d d = b.Sub(a);
        double len = d.Length;
        return len < 1e-3 || _sp.Cast(a, d.Scale(1 / len), len - 0.05, SpaceSampler.HitFilter.Any) is null;
    }

    private int Snap(Vec3d p)
    {
        int best = -1;
        double bd = double.MaxValue;
        int ci = (int)Math.Floor(p.X / Pitch), ck = (int)Math.Floor(p.Z / Pitch);
        for (int i = ci - 4; i <= ci + 4; i++)
        {
            for (int k = ck - 4; k <= ck + 4; k++)
            {
                if (!_lattice.TryGetValue((i, k), out List<int>? l))
                {
                    continue;
                }

                foreach (int s in l)
                {
                    Vec3d q = _valid[s].P;
                    double dy = p.Y - q.Y;
                    if (dy < -0.5 || dy > 3.5)
                    {
                        continue;
                    }

                    double d = ((q.X - p.X) * (q.X - p.X)) + ((q.Z - p.Z) * (q.Z - p.Z)) + (0.1 * dy * dy);
                    if (d < bd)
                    {
                        bd = d;
                        best = s;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>The playable floor: walk components holding a spawn (or the largest, without spawns).</summary>
    public (List<int> Samples, double Area) Playable(IEnumerable<Vec3> spawns)
    {
        var seeds = new HashSet<int>();
        foreach (Vec3 s in spawns)
        {
            int i = Snap(new Vec3d(s.X, s.Y, s.Z));
            if (i >= 0)
            {
                seeds.Add(_comp[i]);
            }
        }

        if (seeds.Count == 0 && _comp.Length > 0)
        {
            seeds.Add(_comp.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key);
        }

        List<int> play = Enumerable.Range(0, _valid.Count).Where(i => seeds.Contains(_comp[i])).ToList();
        return (play, play.Count * Pitch * Pitch);
    }

    public sealed record Report(
        double PlayArea, double Items100, double WeaponNnMedian, double SpawnToItemMedian, double SpawnToItemMax,
        double Dressing100, double Clutter100, double Decals100, double Lights100, double Undressed,
        double SpawnLosPairs, double SpawnExposureMedian, double SpawnExposureMax, double SpawnNnMin, int Spawns, int Items);

    /// <summary>Measures the population of <paramref name="doc"/> on its compiled level.</summary>
    public Report Measure(EditorDocument doc)
    {
        List<LevelObject> objects = doc.Objects.ToList();
        List<Vec3> spawns = objects.Where(o => o.Kind == LevelObjectKind.MpRespawnPoint).Select(o => o.Position).ToList();
        (List<int> play, double area) = Playable(spawns);
        double per100 = 100 / Math.Max(area, 1);

        // Items.
        List<LevelObject> items = objects.Where(o => o.Kind == LevelObjectKind.Item).ToList();
        List<Vec3> majors = items.Where(o => ItemCat(o.ClassName ?? string.Empty) is "weapon" or "powerup").Select(o => o.Position).ToList();
        List<double> nn = majors.Select((p, i) => majors.Where((q, j) => j != i).Select(q => (double)p.Distance(q)).DefaultIfEmpty(double.NaN).Min()).ToList();
        List<double> s2i = spawns.Select(s => items.Count == 0 ? double.NaN : items.Min(it => (double)it.Position.Distance(s))).ToList();

        // Dressing points (the study's population classes).
        var dress = new List<Vec3>();
        foreach (Brush b in new BrushEditor(doc).Brushes)
        {
            if ((b.Flags & (uint)BrushFlags.Air) != 0 || b.Geometry.Vertices.Count == 0)
            {
                continue;
            }

            (Vec3 mn, Vec3 mx) = WorldBounds(b);
            double[] e = new double[] { mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z }.OrderBy(x => x).ToArray();
            bool detail = (b.Flags & (uint)BrushFlags.Detail) != 0;
            bool beam = e[0] <= 0.75 && e[1] <= 0.75 && e[2] >= 2;
            bool slab = e[0] <= 0.5 && e[1] > 0.75 && e[2] <= 16;
            bool small = e[2] <= 4 && e[0] * e[1] * e[2] <= 16;
            if ((beam || slab || small || detail) && e[2] <= 24)
            {
                dress.Add(mn.Add(mx).Scale(0.5f));
            }
        }

        int clutter = objects.Count(o => o.Kind == LevelObjectKind.Clutter);
        int decals = objects.Count(o => o.Kind == LevelObjectKind.Decal);
        dress.AddRange(objects.Where(o => o.Kind is LevelObjectKind.Clutter or LevelObjectKind.Decal).Select(o => o.Position));
        int lights = objects.Count(o => o.Kind == LevelObjectKind.Light);

        // Undressed 8 m cells (cells with at least 8 m² of playable floor and no dressing point in their
        // column, from 1.5 m under the floor to 9 m over it).
        var cellFloor = new Dictionary<(int, int, int), (int N, double Y)>();
        foreach (int i in play)
        {
            Vec3d p = _valid[i].P;
            var key = ((int)Math.Floor(p.X / 8), (int)Math.Floor(p.Z / 8), (int)Math.Floor(p.Y / 6));
            (int n, double y) = cellFloor.GetValueOrDefault(key);
            cellFloor[key] = (n + 1, y + p.Y);
        }

        int cells = 0, undressed = 0;
        foreach (((int cx, int cz, int _), (int n, double ysum)) in cellFloor)
        {
            if (n < 32)
            {
                continue;
            }

            cells++;
            double fy = ysum / n;
            bool any = dress.Any(d => Math.Floor(d.X / 8) == cx && Math.Floor(d.Z / 8) == cz && d.Y >= fy - 1.5 && d.Y <= fy + 9);
            if (!any)
            {
                undressed++;
            }
        }

        // Spawns: sight pairs within 30 m, exposure 40 m, nearest neighbour.
        var rng = new Random(12345);
        int pairs = 0, seen = 0;
        var snap = spawns.Select(s => Snap(new Vec3d(s.X, s.Y, s.Z))).ToList();
        var expo = new List<double>();
        var snn = new List<double>();
        for (int a = 0; a < spawns.Count; a++)
        {
            double best = double.MaxValue;
            for (int b = 0; b < spawns.Count; b++)
            {
                if (a == b)
                {
                    continue;
                }

                double d = spawns[a].Distance(spawns[b]);
                best = Math.Min(best, d);
                if (a < b && d <= 30 && snap[a] >= 0 && snap[b] >= 0)
                {
                    pairs++;
                    if (Los(EyeOf(snap[a]), EyeOf(snap[b])))
                    {
                        seen++;
                    }
                }
            }

            if (best < double.MaxValue)
            {
                snn.Add(best);
            }

            if (snap[a] >= 0 && play.Count > 0)
            {
                Vec3d e = EyeOf(snap[a]);
                int tried = 0, vis = 0;
                for (int t = 0; t < 480 && tried < 120; t++)
                {
                    Vec3d q = EyeOf(play[rng.Next(play.Count)]);
                    if (q.Sub(e).Length > 40)
                    {
                        continue;
                    }

                    tried++;
                    if (Los(e, q))
                    {
                        vis++;
                    }
                }

                expo.Add(tried > 0 ? vis / (double)tried : 0);
            }
        }

        return new Report(
            area, items.Count * per100, Median(nn), Median(s2i), s2i.Count == 0 ? double.NaN : s2i.Max(),
            dress.Count * per100, clutter * per100, decals * per100, lights * per100, cells > 0 ? undressed / (double)cells : 0,
            pairs > 0 ? seen / (double)pairs : 0, Median(expo), expo.Count == 0 ? 0 : expo.Max(), snn.Count == 0 ? double.NaN : snn.Min(),
            spawns.Count, items.Count);
    }

    private Vec3d EyeOf(int i) => _valid[i].P.Add(new Vec3d(0, Eye, 0));

    private static double Median(List<double> v)
    {
        List<double> s = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        return s.Count == 0 ? double.NaN : s[s.Count / 2];
    }

    private static (Vec3 Min, Vec3 Max) WorldBounds(Brush b)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            Vec3 w = BrushTransform.WorldVertex(b, v);
            mn = Vec3Math.Min(mn, w);
            mx = Vec3Math.Max(mx, w);
        }

        return (mn, mx);
    }

    /// <summary>The study's item categories (Program.cs ItemCat).</summary>
    public static string ItemCat(string c)
    {
        string l = c.ToLowerInvariant();
        if (l.Contains("flag", StringComparison.Ordinal)) return "flag";
        if (l.Contains("amp", StringComparison.Ordinal) || l.Contains("invuln", StringComparison.Ordinal) || l.Contains("super armor", StringComparison.Ordinal) || l.Contains("super health", StringComparison.Ordinal) || l.Contains("multi ", StringComparison.Ordinal)) return "powerup";
        if (l.Contains("ammo", StringComparison.Ordinal) || l.Contains("shells", StringComparison.Ordinal) || l.Contains("rounds", StringComparison.Ordinal)) return "ammo";
        if (l.Contains("medic", StringComparison.Ordinal) || l.Contains("first aid", StringComparison.Ordinal) || l.Contains("health", StringComparison.Ordinal)) return "health";
        if (l.Contains("armor", StringComparison.Ordinal) || l.Contains("repair", StringComparison.Ordinal) || l.Contains("envirosuit", StringComparison.Ordinal)) return "armor";
        string[] w = { "rifle", "shotgun", "gun", "launcher", "pistol", "smg", "grenade", "flamethrower", "rail", "charge", "shield", "baton", "sniper", "minigun", "machine" };
        return w.Any(x => l.Contains(x, StringComparison.Ordinal)) ? "weapon" : "other";
    }
}
