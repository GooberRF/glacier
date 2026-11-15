using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

/// <summary>Budgets and switches for a <see cref="SpatialAnalysis"/>.</summary>
public sealed class SpatialOptions
{
    /// <summary>The starting floor-lattice pitch in metres.</summary>
    public double Pitch { get; init; } = 0.5;

    /// <summary>Lattice sample budget: the pitch doubles until the level yields at most this many samples.</summary>
    public int MaxSamples { get; init; } = 2_000_000;

    /// <summary>Traverse lifts, jump pads and teleports (E1). Off reproduces the exemplar study, which lacked them.</summary>
    public bool MovementLinks { get; init; } = true;

    /// <summary>Lay the lattice only within 48 m of the spawns and items, so far-off decorative ground does not dilute it.</summary>
    public bool Focus { get; init; } = true;

    /// <summary>Seed of the sampled measurements (sightlines, exposure, routes, traffic). The same seed gives the same numbers.</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>Threads for the ray-heavy passes (results never depend on it).</summary>
    public int Parallelism { get; init; } = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    /// <summary>
    /// Count every floor component of at least 16 m² as playable, not only those holding a spawn (or 3 items): the
    /// kit's spawn planner measures a level before it has spawns. Off for every grader measurement.
    /// </summary>
    internal bool PlayableAllFloor { get; init; }

    /// <summary>
    /// Playable floor is floor a player can stand on and get to: floor on support at least about 0.6 m across along both
    /// lattice axes (a wall-trim band, a rail or pipe top is out), less the prop-sized tops (16-80 m², a drop of at least
    /// 2.5 m off every edge: a container stack, a hut roof) the walk from the spawns does not reach; sightlines and
    /// exposure are measured only from and to points where a standing eye (1.6 m) is below the ceiling. Off reproduces the
    /// exemplar study, which counted every floor sample of a spawn's walk component.
    /// </summary>
    public bool StandableFloor { get; init; } = true;

    /// <summary>
    /// Sightline, exposure and spawn-exposure samples are chosen by a hash of their lattice position (seeded by
    /// <see cref="Seed"/>) rather than drawn from a random stream over the sample list, and in larger numbers, so an edit
    /// moves the numbers only through the floor it changes. Off reproduces the exemplar study's random draws.
    /// </summary>
    public bool StableSampling { get; init; } = true;

    /// <summary>The settings the exemplar study measured with (no movement links, the study's floor set and random draws).</summary>
    public static SpatialOptions Study { get; } = new() { MovementLinks = false, StandableFloor = false, StableSampling = false };
}

/// <summary>
/// The measurement core of the design-quality batch (DESIGN-QUALITY-SPEC §0, §(e) E1–E8): the walk graph on a 0.5 m
/// floor lattice, the 8 m and 4 m cell graphs and the space graph (loops, chokepoints, pockets), routes, sightlines and
/// exposure, cover, shape and wall statistics, population per cell, spawn and item reports and texturing statistics —
/// all measured from the compiled geometry stored in (or freshly built for) the level, deterministically for a given
/// seed. Sections are computed on first use and cached; the sampled ones always run in one fixed order on one seeded
/// random stream, so a section's numbers never depend on which section was asked for first. The study tool that
/// calibrated the grader (research/exemplar-study/tools/study) is the reference implementation this ports.
/// </summary>
public sealed partial class SpatialAnalysis
{
    /// <summary>Eye height above the floor (m) for sightlines and exposure.</summary>
    public const double Eye = 1.6;

    /// <summary>The fewest playable samples a level must have to be measured.</summary>
    public const int MinPlayableSamples = 20;

    private readonly SpatialLevel _level;
    private readonly SpatialOptions _o;
    private readonly CancellationToken _ct;
    private readonly Random _rng;
    private readonly SpaceSampler _sp;
    private readonly IReadOnlyList<SpaceSampler.Sample> _s;
    private readonly WalkGraph _w;
    private readonly SpatialObjects _obj;
    private readonly double _g;
    private readonly int _focusClipped;

    private SpatialAnalysis(SpatialLevel level, SpatialOptions options, CancellationToken ct)
    {
        _level = level;
        _o = options;
        _ct = ct;
        _rng = new Random(options.Seed);
        _obj = new SpatialObjects(level);
        _sp = SpaceSampler.Build(level.Geometry, ct);
        SpaceSampler.FocusRect? focus = null;
        if (options.Focus)
        {
            List<Vec3d> pts = _obj.Spawns.Select(s => s.P).Concat(_obj.Items.Select(i => i.P)).ToList();
            if (pts.Count > 0)
            {
                focus = new SpaceSampler.FocusRect(pts.Min(p => p.X) - 48, pts.Max(p => p.X) + 48, pts.Min(p => p.Z) - 48, pts.Max(p => p.Z) + 48);
                _focusClipped = _sp.Faces.Count(f => f.Floor && (f.MinX < focus.Value.MinX || f.MaxX > focus.Value.MaxX || f.MinZ < focus.Value.MinZ || f.MaxZ > focus.Value.MaxZ));
            }
        }

        _sp.Measure(options.Pitch, options.MaxSamples, focus, options.Parallelism);
        _s = _sp.Samples;
        _g = _sp.Pitch;
        ct.ThrowIfCancellationRequested();
        Links = options.MovementLinks ? MovementLinks.Find(level, _sp) : new List<MovementLink>();
        _w = new WalkGraph(_sp, _obj, Links, options.Parallelism, ct, options.PlayableAllFloor, options.StandableFloor);
    }

    /// <summary>
    /// Measures <paramref name="level"/>. Throws <see cref="SpatialException"/> (<c>no_floor</c>) when fewer than
    /// <see cref="MinPlayableSamples"/> playable floor samples exist, and <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> fires (callers map their time limit to it).
    /// </summary>
    public static SpatialAnalysis Run(SpatialLevel level, SpatialOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        var a = new SpatialAnalysis(level, options ?? new SpatialOptions(), ct);
        if (a._w.PlayIdx.Count < MinPlayableSamples)
        {
            throw new SpatialException("no_floor",
                $"The level has {a._w.PlayArea:0.#} m² of playable floor (spawns and items must stand on compiled floor that is reachable on foot).",
                "Place multiplayer respawn points (or a Player Start) on walkable floor, build the level, and measure again.");
        }

        return a;
    }

    /// <summary>The level measured.</summary>
    public SpatialLevel Level => _level;

    /// <summary>The options used.</summary>
    public SpatialOptions Options => _o;

    /// <summary>The movement links found (empty when disabled).</summary>
    public IReadOnlyList<MovementLink> Links { get; }

    /// <summary>The lattice pitch actually used (doubled from <see cref="SpatialOptions.Pitch"/> when over budget).</summary>
    public double Pitch => _g;

    /// <summary>Playable floor area (m²).</summary>
    public double PlayableArea => _w.PlayArea;

    internal WalkGraph Walk => _w;

    internal SpaceSampler Sampler => _sp;

    internal SpatialObjects Objects => _obj;

    private static double R(double v) => double.IsFinite(v) ? Math.Round(v, 3) : -1;

    private static JsonNode? Dist(IEnumerable<double> values)
    {
        double[] a = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (a.Length == 0)
        {
            return null;
        }

        double P(double p) => Percentiles.Type7(a, p);
        return new JsonObject
        {
            ["n"] = a.Length, ["p5"] = R(P(0.05)), ["p25"] = R(P(0.25)), ["p50"] = R(P(0.5)),
            ["p75"] = R(P(0.75)), ["p90"] = R(P(0.9)), ["p95"] = R(P(0.95)), ["max"] = R(a[^1]),
        };
    }

    private static double Med(IEnumerable<double> v)
    {
        double[] a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : Percentiles.Type7(a, 0.5);
    }

    private Vec3d EyeOf(int i) => _s[i].P.Add(new Vec3d(0, Eye, 0));

    /// <summary>True when nothing blocks the segment from <paramref name="a"/> to <paramref name="b"/> (5 cm short of it).</summary>
    internal bool Los(Vec3d a, Vec3d b)
    {
        Vec3d d = b.Sub(a);
        double len = d.Length;
        if (len < 1e-3)
        {
            return true;
        }

        return !_sp.Blocked(a, d.Scale(1 / len), len - 0.05, SpaceSampler.HitFilter.Any);
    }

    /// <summary>Runs <paramref name="body"/> for 0 … n−1 with the configured parallelism (honouring cancellation).</summary>
    private void For(int n, Action<int> body)
    {
        if (_o.Parallelism <= 1 || n < 64)
        {
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                }

                body(i);
            }

            return;
        }

        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = _o.Parallelism, CancellationToken = _ct }, body);
    }

    // ---- the study-shaped report -----------------------------------------------------------------

    private JsonObject? _report;

    /// <summary>
    /// The whole measurement, in the exemplar study's shape (<c>walk</c>, <c>storeys</c>, <c>loops</c>, <c>cell8</c>,
    /// <c>spaces</c>, <c>cover</c>, <c>routes</c>, <c>sight</c>, <c>shape</c>, <c>bridges</c> (the study's proxy),
    /// <c>setpieces</c> (E9), <c>purpose</c> (the function checks), <c>population</c>,
    /// <c>spawns</c>, <c>items</c>, <c>texturing</c>) plus <c>links</c>, <c>class_features</c> and <c>fingerprint</c> (the
    /// corridor, opening and vertical-layer numbers as <c>analyze</c> measures them). Computes every section.
    /// </summary>
    public JsonObject Report()
    {
        if (_report is not null)
        {
            return _report;
        }

        var root = new JsonObject
        {
            ["file"] = _level.FileName,
            ["focus_clipped_faces"] = _focusClipped,
            ["pitch"] = _g,
            ["walk"] = WalkReport(),
            ["storeys"] = Storeys(),
            ["loops"] = LoopsReport(),
            ["cell8"] = Cell8Report(),
            ["spaces"] = SpacesReport(),
            ["cover"] = CoverReport().DeepClone(),
            ["routes"] = RoutesReport().DeepClone(),
            ["sight"] = SightReport().DeepClone(),
            ["shape"] = ShapeReport().DeepClone(),
            ["bridges"] = BridgesReport().DeepClone(),
            ["setpieces"] = SetPiecesReport().DeepClone(),
            ["population"] = PopulationReport().DeepClone(),
            ["spawns"] = SpawnReport().DeepClone(),
            ["items"] = ItemReport().DeepClone(),
            ["texturing"] = TexturingReport().DeepClone(),
            ["links"] = LinksReport(),
            ["class_features"] = ClassFeatures().DeepClone(),
            ["fingerprint"] = FingerprintScale().DeepClone(),
            ["payload"] = PayloadReport()?.DeepClone(),
            ["purpose"] = PurposeReport().DeepClone(),
        };
        _report = root;
        return root;
    }

    private JsonObject WalkReport()
    {
        int valid = _s.Count(x => x.Valid);
        double all = valid * _g * _g;
        int snapped = _w.SpawnSample.Count(x => x >= 0);
        var compArea = new Dictionary<int, int>();
        for (int i = 0; i < _s.Count; i++)
        {
            if (_s[i].Valid)
            {
                compArea[_w.Comp[i]] = compArea.GetValueOrDefault(_w.Comp[i]) + 1;
            }
        }

        var compSp = new Dictionary<int, int>();
        foreach (int si in _w.SpawnSample)
        {
            if (si >= 0)
            {
                compSp[_w.Comp[si]] = compSp.GetValueOrDefault(_w.Comp[si]) + 1;
            }
        }

        var compIt = new Dictionary<int, int>();
        foreach (var it in _obj.Items)
        {
            int si = _w.Snap(it.P);
            if (si >= 0)
            {
                compIt[_w.Comp[si]] = compIt.GetValueOrDefault(_w.Comp[si]) + 1;
            }
        }

        var top = new JsonArray(compArea.OrderByDescending(k => k.Value).Take(8)
            .Select(k => (JsonNode)$"{k.Value * _g * _g:0}m2 sp{compSp.GetValueOrDefault(k.Key)} it{compIt.GetValueOrDefault(k.Key)}").ToArray());
        int mp = _obj.MpSpawnCount;
        return new JsonObject
        {
            ["components_top"] = top,
            ["valid_area"] = R(all),
            ["playable_area"] = R(_w.PlayArea),
            ["playable_fraction"] = all > 0 ? R(_w.PlayArea / all) : null,
            ["spawns"] = mp,
            ["spawns_snapped"] = snapped,
            ["playable_per_spawn"] = mp > 0 ? R(_w.PlayArea / mp) : null,
        };
    }

    private JsonArray Storeys()
    {
        var bins = _w.PlayIdx.GroupBy(i => Math.Floor(_s[i].P.Y)).Select(g => (Y: g.Key, A: g.Count() * _g * _g)).OrderByDescending(b => b.A).ToList();
        var chosen = new List<(double Y, double A)>();
        foreach ((double y, double a) in bins)
        {
            if (chosen.Count >= 3 || a < 0.08 * _w.PlayArea)
            {
                break;
            }

            if (chosen.All(c => Math.Abs(c.Y - y) >= 3))
            {
                chosen.Add((y, a));
            }
        }

        return new JsonArray(chosen.OrderBy(c => c.Y).Select(c => (JsonNode)new JsonObject { ["y"] = c.Y, ["area"] = R(c.A) }).ToArray());
    }

    // ---- coarse graphs -----------------------------------------------------------------------------

    private CellGraph? _cell8, _cell4;
    private SpaceGraph? _spaces;

    internal CellGraph Cell8 => _cell8 ??= new CellGraph(_w, 8);

    internal CellGraph Cell4 => _cell4 ??= new CellGraph(_w, 4);

    internal SpaceGraph Spaces => _spaces ??= new SpaceGraph(_w);

    private JsonObject LoopsReport() => new()
    {
        ["b1_cell4"] = Cell4.B1(),
        ["b1_cell8"] = Cell8.B1(),
        ["b1_cell8_per1000m2"] = R(Cell8.B1() / (_w.PlayArea / 1000)),
        ["b1_cell4_per1000m2"] = R(Cell4.B1() / (_w.PlayArea / 1000)),
        ["cell8_nodes"] = Cell8.N,
        ["cell8_edges"] = Cell8.E,
    };

    private JsonObject Cell8Report()
    {
        CellGraph cg = Cell8;
        CutReport cuts = cg.Cuts;
        return new JsonObject
        {
            ["nodes"] = cg.N,
            ["articulation_nodes"] = cuts.Articulation.Count,
            ["choke_nodes_10pct"] = cuts.Chokes.Count,
            ["pocket_area_fraction"] = Math.Round(cuts.PocketArea, 3),
            ["leaf_nodes"] = Enumerable.Range(0, cg.N).Count(i => cg.Graph.Adj[i].Count == 1),
        };
    }

    private JsonObject LinksReport()
    {
        var arr = new JsonArray();
        foreach ((MovementLink link, int from, int to) in _w.Links)
        {
            arr.Add(new JsonObject
            {
                ["kind"] = link.Kind,
                ["uid"] = link.Uid,
                ["from"] = P3(_s[from].P),
                ["to"] = P3(_s[to].P),
                ["one_way"] = link.OneWay,
            });
        }

        return new JsonObject
        {
            ["enabled"] = _o.MovementLinks,
            ["found"] = Links.Count,
            ["traversed"] = _w.Links.Count,
            ["list"] = arr,
        };
    }

    /// <summary>A world point as a JSON [x, y, z] (centimetre precision).</summary>
    internal static JsonArray P3(Vec3d p) => new(Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2));
}
