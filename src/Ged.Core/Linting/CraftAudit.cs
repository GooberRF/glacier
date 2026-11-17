using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Analysis;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Mesh;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Linting;

/// <summary>Inputs for a <see cref="CraftAudit"/> run.</summary>
public sealed class CraftAuditOptions
{
    /// <summary>The checks to run (<see cref="CraftAudit.Checks"/>); null = all.</summary>
    public IReadOnlyCollection<string>? Checks { get; init; }

    /// <summary>The most findings reported per check (the counts stay complete).</summary>
    public int MaxPerCheck { get; init; } = 40;

    /// <summary>
    /// The real model-space bounds of an object's mesh (stored vertices plus the submesh offset), or null when unknown
    /// (<see cref="CraftAudit.MeshBoundsFrom"/> reads them from a mounted install). Without them objects are tested at
    /// their origin only.
    /// </summary>
    public Func<LevelObject, (Vec3 Min, Vec3 Max)?>? ObjectBounds { get; init; }

    /// <summary>The measurement budget for the walk-graph checks (stairs, perches, purpose, props, density).</summary>
    public SpatialBudget? Budget { get; init; }
}

/// <summary>One craft-audit finding: what is wrong, where, which object or brush, and how to fix it.</summary>
public sealed record CraftFinding(string Check, string Kind, LintSeverity Severity, string Message, Vec3d Pos, int? Uid, string Fix, double? Value = null, int[]? Uids = null)
{
    /// <summary>The finding as JSON (pos as [x, y, z]).</summary>
    public JsonObject ToJson() => new()
    {
        ["check"] = Check,
        ["kind"] = Kind,
        ["severity"] = Severity.ToString().ToLowerInvariant(),
        ["message"] = Message,
        ["pos"] = new JsonArray(Math.Round(Pos.X, 2), Math.Round(Pos.Y, 2), Math.Round(Pos.Z, 2)),
        ["uid"] = Uid,
        ["uids"] = Uids is { Length: > 0 } u ? new JsonArray(u.Take(12).Select(x => (JsonNode)x).ToArray()) : null,
        ["value"] = Value is double v ? Math.Round(v, 3) : null,
        ["fix"] = Fix,
    };
}

/// <summary>The result of a <see cref="CraftAudit"/> run.</summary>
public sealed class CraftAuditReport
{
    internal CraftAuditReport()
    {
    }

    /// <summary>Every finding (at most <see cref="CraftAuditOptions.MaxPerCheck"/> per check).</summary>
    public List<CraftFinding> Findings { get; } = new();

    /// <summary>Findings per check before the cap.</summary>
    public SortedDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    /// <summary>What could not be checked, and why.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Per-region readings of the rock and density checks.</summary>
    public JsonArray RockRegions { get; } = new();

    /// <summary>Per-region dressing densities.</summary>
    public JsonArray DensityRegions { get; } = new();

    /// <summary>
    /// The checks that were asked for but could not run (the walk-graph measurement hit its time limit or failed): they
    /// have no count, and the report is neither <see cref="Complete"/> nor <see cref="Clean"/>.
    /// </summary>
    public List<string> Skipped { get; } = new();

    /// <summary>True when every check asked for ran.</summary>
    public bool Complete => Skipped.Count == 0;

    /// <summary>True when every check asked for ran and there is no finding at all (the "done" gate).</summary>
    public bool Clean => Complete && Counts.Values.Sum() == 0;

    /// <summary>The report as JSON: <c>{clean, complete, skipped, total, counts, findings, rock_regions, density_regions, notes, rules}</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["clean"] = Clean,
        ["complete"] = Complete,
        ["skipped"] = new JsonArray(Skipped.Select(s => (JsonNode)s).ToArray()),
        ["total"] = Counts.Values.Sum(),
        ["counts"] = new JsonObject(Counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        ["findings"] = new JsonArray(Findings.Select(f => (JsonNode)f.ToJson()).ToArray()),
        ["rock_regions"] = RockRegions.DeepClone(),
        ["density_regions"] = DensityRegions.DeepClone(),
        ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()),
        ["rules"] = CraftAudit.Rules,
    };
}

/// <summary>
/// The craft audit: the "can't report done until clean" gate (AGENT-GUIDE "Craft audit"). It reads the compiled geometry
/// (a sealed build) and the objects of any level — traced, generated or hand-made alike, tagged or not — and reports what
/// a player sees as careless: props sunk into or floating over the floor or pushed into walls, stairs and ramps that lead
/// nowhere (the intent layer's flight, bridge and route-ledge readings, <see cref="SpatialAnalysis.Purpose"/>), dead-end
/// perches, doorways and frames opening into solid or void, set pieces built with their purpose check off that fail it,
/// random props, rock that is flat-sided prisms, and areas dressed far below the corpus norms. Read-only.
/// </summary>
public static class CraftAudit
{
    /// <summary>The check names, in report order.</summary>
    public static readonly IReadOnlyList<string> Checks = new[] { "props", "stairs", "perches", "openings", "purpose", "rock", "density" };

    /// <summary>A prop is sunk when the floor rises this far (m) above its base.</summary>
    public const double SinkTolerance = 0.15;

    /// <summary>A floor-standing prop floats when its base is this far (m) above the floor under it (items: <see cref="ItemFloat"/>).</summary>
    public const double FloatTolerance = 0.3;

    /// <summary>An item floats when its origin is this far (m) above the floor under it.</summary>
    public const double ItemFloat = 1.2;

    /// <summary>A prop pushes into a wall when the wall is this far (m) inside its box side.</summary>
    public const double WallTolerance = 0.15;

    /// <summary>An opening's side must have walkable floor within this drop (m) of the opening's floor.</summary>
    public const double OpeningDrop = 1.5;

    /// <summary>The widest air cut (m) read as a doorway.</summary>
    public const double OpeningMaxWidth = 8;

    /// <summary>The widest door frame (m, outside its posts) read as a doorway.</summary>
    public const double FrameMaxWidth = 10;

    /// <summary>A doorway leads into a space at least this much (m), and half again, wider than the cut on one side at least.</summary>
    public const double OpeningWiden = 2;

    /// <summary>Player headroom (m) beyond a flight or opening.</summary>
    public const double Headroom = SpatialWorld.StandingHeight;

    /// <summary>Rock regions (m, plan) for the flat-rock reading.</summary>
    public const double RockCell = 16;

    /// <summary>A rock region is read when it has this much non-floor rock surface (m²).</summary>
    public const double RockMinArea = 40;

    /// <summary>A rock face this large (m²) and within ~6° of vertical is a flat prism side.</summary>
    public const double RockFlatFace = 2.0;

    /// <summary>A rock region with more than this share of its surface in flat vertical faces is prism-like.</summary>
    public const double RockFlatShare = 0.55;

    /// <summary>A rock face counts where open space (not solid, not inside a detail brush) lies this far (m) in front of it.</summary>
    public const double RockProbe = 0.05;

    /// <summary>A rock face large enough to be a flat side is probed about every this many metres across its surface.</summary>
    public const double RockProbeSpacing = 2.0;

    /// <summary>The most probe points on one rock face.</summary>
    public const int RockMaxProbes = 64;

    /// <summary>Density regions (m, plan).</summary>
    public const double DensityCell = 24;

    /// <summary>A density region is read when it has this much playable floor (m²).</summary>
    public const double DensityMinArea = 150;

    /// <summary>Props + detail brushes per 100 m² below which an area is basic.</summary>
    public const double DensityMinDetail = 1.0;

    /// <summary>Under open sky, a region's lights count up to this far (m) above its floor (under a ceiling, up to the ceiling).</summary>
    public const double LightSkyReach = 16;

    /// <summary>A light counts this far (m) above the ceiling over the floor it lights (a fixture set into the ceiling).</summary>
    public const double LightCeilingSlack = 1.0;

    /// <summary>A free-standing clutter object with no wall within this distance (m) and no other prop within <see cref="LoneNeighbour"/> is lone.</summary>
    public const double LoneWall = 2.5;

    /// <summary>See <see cref="LoneWall"/>.</summary>
    public const double LoneNeighbour = 4.0;

    /// <summary>The rules, as one paragraph (the report carries it).</summary>
    public static string Rules { get; } = string.Create(CultureInfo.InvariantCulture,
        $"props: clutter, items and mesh objects (real mesh bounds when an install is mounted, else the origin), and box-like detail brushes up to 4 m across: sunk when the floor rises over {SinkTolerance} m above the base or the point is inside solid; floating when a floor-standing prop's base is over {FloatTolerance} m (items {ItemFloat} m) above the floor; in a wall when a wall is over {WallTolerance} m inside its box; random when the intent layer reads it as a rock prop on a built floor, scattered rock or orphan cover, when it stands in a doorway or on a flight, or when it is lone (no wall within {LoneWall} m and no other prop within {LoneNeighbour} m). "
        + $"stairs: every flight, ramp and bridge the intent layer reads from the geometry (tags not needed) with a fault, plus a flight whose foot or top has under {Headroom} m headroom, plus route ledges. perches: a flight's dead end (an empty pit or perch reached only by flights). "
        + $"openings: air brushes cut through a wall up to {OpeningMaxWidth} m wide (walkable floor in the cut, a jamb on both sides, and on one side at least a space {OpeningWiden} m and half again wider than the cut; never a cave, canyon or passage segment), door frames (one per kit.opening, else per touching cluster; up to {FrameMaxWidth} m across, with a walkable sill) and door meshes: each side, 1 m out, needs walkable floor within {OpeningDrop} m below the opening's floor with {Headroom} m headroom, not solid, and a clear line from the opening. "
        + $"purpose: elements built with purpose = \"off\" / \"quiet\" / \"report\" (kit.purpose_mode tag) whose built reading still fails, or which the built level does not read as a working route at all. "
        + $"rock: per {RockCell} m region with {RockMinArea} m2 of visible non-floor rock surface (a face counts where the point {RockProbe} m in front of it is in the level's air and inside no detail brush), the share in faces of {RockFlatFace} m2 or more within 6 degrees of vertical; over {RockFlatShare} is prism-like. "
        + $"density: per {DensityCell} m region with {DensityMinArea} m2 of playable floor, props + detail brushes per 100 m2 under {DensityMinDetail}, or lights per 100 m2 under the corpus p5, is basic (lights count from 1 m below the floor up to {LightCeilingSlack} m above the ceiling over it, or {LightSkyReach} m up under open sky). "
        + $"A check whose measurement could not finish is listed under skipped, with no count: the report is then incomplete and not clean.");

    /// <summary>Audits <paramref name="level"/>. Never throws for an unmeasurable level: the checks it cannot run are noted.</summary>
    public static CraftAuditReport Run(SpatialLevel level, CraftAuditOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        options ??= new CraftAuditOptions();
        var run = new Runner(level, options, ct);
        run.Execute();
        return run.Report;
    }

    /// <summary>The lint category of a finding's check.</summary>
    public static LintCategory Category(string check) => check switch
    {
        "props" => LintCategory.CraftProp,
        "stairs" => LintCategory.CraftStairs,
        "perches" => LintCategory.CraftPerch,
        "openings" => LintCategory.CraftOpening,
        "purpose" => LintCategory.CraftPurpose,
        "rock" => LintCategory.CraftFlatRock,
        _ => LintCategory.CraftSparse,
    };

    /// <summary>
    /// Mesh bounds from a mounted install: the class's (or mesh object's) V3D, every submesh's bounding box moved by its
    /// submesh offset (FEATURES approximation 39: the engine draws stored vertex + offset). Cached per mesh name.
    /// </summary>
    public static Func<LevelObject, (Vec3 Min, Vec3 Max)?> MeshBoundsFrom(AssetVfs vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        Tables.GameCatalogs cats = Tables.GameCatalogs.Load(vfs);
        var cache = new Dictionary<string, (Vec3, Vec3)?>(StringComparer.OrdinalIgnoreCase);
        return o =>
        {
            string? mesh = o.Model switch
            {
                AlpineMeshObject m => m.MeshFilename,
                _ when o.Kind == LevelObjectKind.Clutter => cats.Clutter?.Find(o.ClassName)?.V3dFilename,
                _ when o.Kind == LevelObjectKind.Item => cats.Items?.Find(o.ClassName)?.V3dFilename,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(mesh))
            {
                return null;
            }

            lock (cache)
            {
                if (cache.TryGetValue(mesh, out (Vec3, Vec3)? hit))
                {
                    return hit;
                }

                (Vec3, Vec3)? b = null;
                try
                {
                    if (vfs.LoadMesh(mesh) is { } v3d && !v3d.IsCharacter)
                    {
                        b = Bounds(v3d);
                    }
                }
                catch (Exception)
                {
                    b = null;
                }

                cache[mesh] = b;
                return b;
            }
        };
    }

    /// <summary>The model-space bounds of a static mesh: each submesh's box plus its offset.</summary>
    internal static (Vec3 Min, Vec3 Max)? Bounds(V3dFile v3d)
    {
        bool any = false;
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        foreach (V3dSubmesh sm in v3d.Submeshes)
        {
            Vec3 off = v3d.ModelOffset(sm);
            foreach (Vec3 p in new[] { sm.BoundingBox.P1, sm.BoundingBox.P2 })
            {
                any = true;
                x0 = Math.Min(x0, p.X + off.X);
                y0 = Math.Min(y0, p.Y + off.Y);
                z0 = Math.Min(z0, p.Z + off.Z);
                x1 = Math.Max(x1, p.X + off.X);
                y1 = Math.Max(y1, p.Y + off.Y);
                z1 = Math.Max(z1, p.Z + off.Z);
            }
        }

        return any && x1 > x0 && y1 >= y0 && z1 > z0 ? (new Vec3(x0, y0, z0), new Vec3(x1, y1, z1)) : null;
    }

    /// <summary>Clutter class words that mark a floor-standing prop (wall and ceiling fixtures are left alone).</summary>
    internal static readonly string[] FloorWords =
    {
        "crate", "barrel", "drum", "box", "spool", "generator", "cart", "table", "chair", "desk", "locker", "cabinet", "keg",
        "tank", "pallet", "bench", "trash", "toolbox", "pump", "compressor", "console", "debris", "computer", "bed", "rock", "boulder",
    };

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string P(Vec3d p) => string.Create(CultureInfo.InvariantCulture, $"({p.X:0.#}, {p.Y:0.#}, {p.Z:0.#})");

    private static Vec3d V(Vec3 v) => new(v.X, v.Y, v.Z);

    private sealed class Runner
    {
        private static readonly Vec3d Down = new(0, -1, 0), Up = new(0, 1, 0);

        private readonly SpatialLevel _level;
        private readonly CraftAuditOptions _o;
        private readonly CancellationToken _ct;
        private readonly SpatialWorld _world;
        private readonly SpaceSampler _sp;
        private readonly TerrainGround? _ground;
        private readonly IReadOnlyDictionary<int, Dictionary<string, string>> _tags;
        private readonly List<Brush> _brushes;
        private readonly List<(Vec3d Centre, Vec3d Axis, double Width, int? Uid)> _openings = new();
        private SpatialAnalysis? _analysis;

        public Runner(SpatialLevel level, CraftAuditOptions o, CancellationToken ct)
        {
            _level = level;
            _o = o;
            _ct = ct;
            _world = SpatialWorld.For(level.Geometry);
            _sp = _world.Sampler;
            _ground = TerrainGround.Of(level.Rfl);
            _brushes = level.Brushes().ToList();
            try
            {
                _tags = new GedObjectMetadataService(new EditorDocument(level.Rfl)).AllTags();
            }
            catch (Exception)
            {
                _tags = new Dictionary<int, Dictionary<string, string>>();
            }
        }

        public CraftAuditReport Report { get; } = new();

        private bool On(string check) => _o.Checks is null || _o.Checks.Contains(check, StringComparer.OrdinalIgnoreCase);

        private void Add(CraftFinding f)
        {
            Report.Counts[f.Check] = Report.Counts.GetValueOrDefault(f.Check) + 1;
            if (Report.Findings.Count(x => x.Check == f.Check) < Math.Max(1, _o.MaxPerCheck))
            {
                Report.Findings.Add(f);
            }
        }

        private Dictionary<string, string> TagsOf(int uid) => _tags.TryGetValue(uid, out Dictionary<string, string>? t) ? t : new Dictionary<string, string>();

        public void Execute()
        {
            foreach (string c in Checks)
            {
                if (On(c))
                {
                    Report.Counts[c] = 0;
                }
            }

            // Openings first: the prop check reads them (a prop standing in a doorway).
            FindOpenings();
            if (On("openings"))
            {
                CheckOpenings();
            }

            if (On("props"))
            {
                CheckObjects();
                CheckBrushProps();
            }

            if (On("stairs") || On("perches") || On("purpose") || On("props") || On("density"))
            {
                try
                {
                    _analysis = SpatialQueries.Analyze(_level, _o.Budget ?? new SpatialBudget(), _ct);
                }
                catch (SpatialException ex)
                {
                    // Not a clean result: the checks that read the walk graph have no count, so a measurement cut short
                    // (a time limit on a loaded machine) never reads as zero findings.
                    foreach (string c in new[] { "stairs", "perches", "purpose", "density" }.Where(On))
                    {
                        Report.Counts.Remove(c);
                        Report.Skipped.Add(c);
                    }

                    if (On("props"))
                    {
                        Report.Skipped.Add("props.random");
                    }

                    Report.Notes.Add($"The walk-graph checks (stairs, perches, purpose, random props, density) did not run, so the audit is incomplete (not clean): {ex.Message} {ex.Hint}");
                }
            }

            if (_analysis is not null)
            {
                IntentChecks(_analysis);
                if (On("density"))
                {
                    CheckDensity(_analysis);
                }
            }

            if (On("rock"))
            {
                CheckRock();
            }

            Report.Findings.Sort((a, b) =>
            {
                int c = Checks.ToList().IndexOf(a.Check).CompareTo(Checks.ToList().IndexOf(b.Check));
                return c != 0 ? c : string.CompareOrdinal(a.Kind, b.Kind);
            });
        }

        // ---- props ---------------------------------------------------------------------------------------------

        private double? FloorBelow(Vec3d from, double maxDrop)
        {
            if (_sp.Cast(from, Down, maxDrop, SpaceSampler.HitFilter.UpFacing, out int fi) is double t && fi >= 0)
            {
                return from.Y - t;
            }

            return null;
        }

        /// <summary>
        /// The floor a prop with its base at <paramref name="baseY"/> stands on (or is sunk into) in the column at (x, z):
        /// the first up-facing surface looking down from <paramref name="fromY"/>, any face (detail brushes are floor too),
        /// passing over pieces overhead — a balcony, shelf or beam whose underside is above the base is not the floor the
        /// prop stands on, however far its top rises over the prop's box. Null when there is none within
        /// <paramref name="maxDrop"/> below the base.
        /// </summary>
        private double? SupportUnder(double x, double z, double fromY, double baseY, double maxDrop)
        {
            double from = fromY;
            for (int k = 0; k < 8; k++)
            {
                if (from - (baseY - maxDrop) <= 0 || FloorBelow(new Vec3d(x, from, z), from - (baseY - maxDrop)) is not double fy)
                {
                    return null;
                }

                if (fy <= baseY + 0.05)
                {
                    return fy;
                }

                // Above the base: solid from the base up to it is the floor the prop is sunk into; an underside between
                // them makes it a piece overhead, and the floor is looked for under that underside.
                const double lift = 0.02;
                if (_sp.Cast(new Vec3d(x, baseY + lift, z), Up, fy - baseY - lift, SpaceSampler.HitFilter.DownFacing) is not double u)
                {
                    return fy;
                }

                double next = baseY + lift + u - 0.01;
                if (next >= from - 1e-3)
                {
                    return fy;
                }

                from = next;
            }

            return null;
        }

        /// <summary>A detail piece no deeper than this (m) off the surface it is flush against is a wall-mounted fixture.</summary>
        private const double MountMaxDepth = 0.6;

        /// <summary>How far (m) in front of a piece's side the surface it is mounted on may stand.</summary>
        private const double MountGap = 0.06;

        /// <summary>
        /// True when the detail brush hangs on a wall: one of its near-vertical sides is flush (within <see cref="MountGap"/>
        /// m) against solid — a structural wall or another detail brush, curved ones included — and the piece is no deeper
        /// than <see cref="MountMaxDepth"/> m off that side (a lamp, a plate, an emblem strip; not a crate pushed against
        /// the wall). A wall fixture is held by its wall: it neither floats nor sinks.
        /// </summary>
        private bool WallMounted(Brush b, DetailVolumes detail)
        {
            Geometry g = b.Geometry;
            Mat3 r = b.Rotation;
            Vec3d pos = V(b.Position), right = V(r.Right), up = V(r.Up), fwd = V(r.Forward);
            var w = new Vec3d[g.Vertices.Count];
            Vec3d mid = new(0, 0, 0);
            for (int i = 0; i < w.Length; i++)
            {
                Vec3 v = g.Vertices[i];
                w[i] = pos.Add(right.Scale(v.X)).Add(up.Scale(v.Y)).Add(fwd.Scale(v.Z));
                mid = mid.Add(w[i]);
            }

            if (w.Length == 0)
            {
                return false;
            }

            mid = mid.Scale(1.0 / w.Length);
            foreach (Face f in g.Faces)
            {
                if (f.Vertices.Count < 3 || f.Vertices.Any(fv => fv.Index < 0 || fv.Index >= w.Length))
                {
                    continue;
                }

                // Newell normal (its length is twice the area) and centroid.
                Vec3d n = new(0, 0, 0), c = new(0, 0, 0);
                for (int k = 0; k < f.Vertices.Count; k++)
                {
                    Vec3d a = w[f.Vertices[k].Index], bb = w[f.Vertices[(k + 1) % f.Vertices.Count].Index];
                    n = n.Add(new Vec3d((a.Y - bb.Y) * (a.Z + bb.Z), (a.Z - bb.Z) * (a.X + bb.X), (a.X - bb.X) * (a.Y + bb.Y)));
                    c = c.Add(a);
                }

                double len = n.Length;
                if (len / 2 < 0.02)
                {
                    continue;
                }

                n = n.Scale(1 / len);
                c = c.Scale(1.0 / f.Vertices.Count);
                if (n.Dot(c.Sub(mid)) < 0)
                {
                    n = n.Scale(-1); // outward, whichever way the brush is wound
                }

                if (Math.Abs(n.Y) > 0.35)
                {
                    continue; // a top or bottom, not a side
                }

                double lo = double.MaxValue, hi = double.MinValue;
                foreach (Vec3d p in w)
                {
                    double d = p.Dot(n);
                    lo = Math.Min(lo, d);
                    hi = Math.Max(hi, d);
                }

                if (hi - lo > MountMaxDepth)
                {
                    continue;
                }

                if (MountedOn(c, n, detail))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when solid stands within <see cref="MountGap"/> m in front of a piece's side (centre <paramref name="c"/>,
        /// outward normal <paramref name="n"/>): a surface facing it across the gap, or the point the gap away inside
        /// solid — a detail brush, or a brush whose far side is then seen from behind (a round silo, a column), or the
        /// world's solid beyond a wall.
        /// </summary>
        private bool MountedOn(Vec3d c, Vec3d n, DetailVolumes detail)
        {
            if (_sp.Cast(c.Add(n.Scale(0.005)), n, MountGap, SpaceSampler.HitFilter.Any, out int fi) is not null && fi >= 0 && _sp.Faces[fi].N.Dot(n) < 0)
            {
                return true; // a wall facing the piece across a gap of a few cm
            }

            Vec3d q = c.Add(n.Scale(MountGap));
            if (detail.Contains(q))
            {
                return true;
            }

            if (_sp.Cast(q, n, 50, SpaceSampler.HitFilter.Any, out fi) is not null && fi >= 0)
            {
                return _sp.Faces[fi].N.Dot(n) > 0; // the first face ahead seen from behind: q is inside that brush
            }

            return _world.IsInSolid(q, _ground);
        }

        private bool FloorStanding(LevelObject o)
        {
            if (o.Kind == LevelObjectKind.Item)
            {
                return true;
            }

            string part = TagsOf(o.Uid).GetValueOrDefault("kit.part") ?? string.Empty;
            if (part is "clutter" or "prop")
            {
                return true;
            }

            string cls = (o.ClassName ?? string.Empty).ToLowerInvariant();
            return FloorWords.Any(w => cls.Contains(w, StringComparison.Ordinal));
        }

        private static Mat3 Orientation(LevelObject o) => o.Model switch
        {
            Clutter c => c.Header.Rotation,
            Item i => i.Header.Rotation,
            AlpineMeshObject m => m.Orientation,
            _ => Mat3.Identity,
        };

        private void CheckObjects()
        {
            // Respawns, the Player Start and waypoint helpers: the SpawnHeight lint's rule (in solid, under 0.5 m up).
            var spawn = new List<LintFinding>();
            SpawnHeightCheck.Run(_level.Rfl, _level.Objects.ToList(), spawn);
            foreach (LintFinding s in spawn)
            {
                Vec3 at = s.Location ?? default;
                Add(new CraftFinding("props", "spawn_height", LintSeverity.Warning, s.Message, V(at), s.Uid, "Stand it about 1 m above walkable floor, in open space."));
            }

            List<LevelObject> objects = _level.Objects.Where(o => !o.IsReadOnly && o.Kind is LevelObjectKind.Clutter or LevelObjectKind.Item or LevelObjectKind.MeshObject).ToList();
            foreach (LevelObject o in objects)
            {
                _ct.ThrowIfCancellationRequested();
                Vec3d p = V(o.Position);
                string name = $"{o.Kind} '{o.DisplayName}' (uid {o.Uid}) at {P(p)}";
                bool standing = FloorStanding(o);
                (Vec3 Min, Vec3 Max)? local = _o.ObjectBounds?.Invoke(o);
                if (local is null)
                {
                    // No mesh bounds: the origin only.
                    if (_world.IsInSolid(p.Add(new Vec3d(0, 0.05, 0)), _ground))
                    {
                        Add(new CraftFinding("props", "sunk", LintSeverity.Warning, $"{name} is inside solid geometry (sunk into the floor or a wall).", p, o.Uid,
                            "Move it into open space: its base on the floor, its back against (not into) the wall."));
                        continue;
                    }

                    if (standing && FloorBelow(p.Add(new Vec3d(0, 0.05, 0)), 30) is double fy)
                    {
                        double gap = p.Y - fy;
                        double limit = o.Kind == LevelObjectKind.Item ? ItemFloat : FloatTolerance;
                        if (gap > limit)
                        {
                            Add(new CraftFinding("props", "floating", LintSeverity.Warning, $"{name} floats {F(gap)} m above the floor.", p, o.Uid,
                                $"Lower it onto the floor at y = {F(fy)}.", gap));
                        }
                    }

                    continue;
                }

                Mat3 r = Orientation(o);
                (Vec3 mn, Vec3 mx) = local.Value;
                Vec3d right = V(r.Right), up = V(r.Up), fwd = V(r.Forward);
                Vec3d World(double x, double y, double z) => p.Add(right.Scale(x)).Add(up.Scale(y)).Add(fwd.Scale(z));
                var corners = new List<Vec3d>();
                foreach (double x in new double[] { mn.X, mx.X })
                {
                    foreach (double y in new double[] { mn.Y, mx.Y })
                    {
                        foreach (double z in new double[] { mn.Z, mx.Z })
                        {
                            corners.Add(World(x, y, z));
                        }
                    }
                }

                double minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
                Vec3d centre = World((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);

                // The floor under the centre and four inset corners, looked for from the box's top down (a balcony or
                // shelf overhead is passed over: the floor is what the base stands on).
                double hx = (mx.X - mn.X) / 2 * 0.7, hz = (mx.Z - mn.Z) / 2 * 0.7;
                Vec3d mid = World((mn.X + mx.X) / 2, 0, (mn.Z + mx.Z) / 2);
                var floors = new List<double>();
                foreach ((double dx, double dz) in new[] { (0.0, 0.0), (hx, hz), (-hx, hz), (hx, -hz), (-hx, -hz) })
                {
                    Vec3d q = mid.Add(right.Scale(dx)).Add(fwd.Scale(dz));
                    if (SupportUnder(q.X, q.Z, maxY + 0.02, minY, 30) is double fy)
                    {
                        floors.Add(fy);
                    }
                }

                double sink = floors.Count > 0 ? floors.Where(f => f < maxY - 0.05).DefaultIfEmpty(double.NegativeInfinity).Max() - minY : double.NegativeInfinity;
                if (standing && sink > SinkTolerance)
                {
                    Add(new CraftFinding("props", "sunk", LintSeverity.Warning,
                        $"{name} is sunk {F(sink)} m into the floor (its mesh base is at y = {F(minY)}, the floor at {F(minY + sink)}).", centre, o.Uid,
                        $"Raise it by {F(sink)} m so its mesh base rests on the floor.", sink));
                    continue;
                }

                if (standing && floors.Count > 0)
                {
                    double gap = minY - floors.Max();
                    double limit = o.Kind == LevelObjectKind.Item ? ItemFloat : FloatTolerance;
                    if (gap > limit)
                    {
                        Add(new CraftFinding("props", "floating", LintSeverity.Warning, $"{name} floats {F(gap)} m above the floor.", centre, o.Uid,
                            $"Lower it by {F(gap)} m so its mesh base rests on the floor.", gap));
                        continue;
                    }
                }

                if (!standing)
                {
                    // Wall and ceiling fixtures are mounted into their surface by design: only one buried whole (its
                    // centre and its bottom both inside solid) is reported.
                    if (_world.IsInSolid(centre, _ground) && _world.IsInSolid(new Vec3d(centre.X, minY + 0.02, centre.Z), _ground))
                    {
                        Add(new CraftFinding("props", "in_wall", LintSeverity.Warning, $"{name} is buried inside solid geometry.", centre, o.Uid,
                            "Move it out so it hangs from (or sits on) the surface, not inside it."));
                    }

                    continue;
                }

                // Into a wall: rays from the centre (at least 0.3 m above the base) along its own axes.
                Vec3d probe = new(centre.X, Math.Max(centre.Y, minY + 0.3), centre.Z);
                if (_world.IsInSolid(probe, _ground))
                {
                    Add(new CraftFinding("props", "in_wall", LintSeverity.Warning, $"{name} has its centre inside solid geometry.", centre, o.Uid,
                        "Pull it out into the room: its back against the wall, not through it."));
                    continue;
                }

                foreach ((Vec3d dir, double half) in new[] { (right, (mx.X - mn.X) / 2), (right.Scale(-1), (mx.X - mn.X) / 2), (fwd, (mx.Z - mn.Z) / 2), (fwd.Scale(-1), (mx.Z - mn.Z) / 2) })
                {
                    Vec3d d = new Vec3d(dir.X, 0, dir.Z);
                    if (d.Length < 1e-6)
                    {
                        continue;
                    }

                    d = d.Scale(1 / d.Length);
                    if (_sp.Cast(probe, d, half + 1, SpaceSampler.HitFilter.Any, out int fi) is double t && fi >= 0 && _sp.Faces[fi].N.Dot(d) < 0 && half - t > WallTolerance)
                    {
                        Add(new CraftFinding("props", "in_wall", LintSeverity.Warning, $"{name} pushes {F(half - t)} m into a wall.", centre, o.Uid,
                            $"Move it {F(half - t)} m away from the wall (along {F(-d.X)}, {F(-d.Z)}).", half - t));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Box-like detail brushes (crates, barrels) sunk into the floor or floating. Any face is floor, detail included: a
        /// crate on a crate or a lamp on a trim rests within a few cm of the top it stands on. Wall-mounted pieces
        /// (<see cref="WallMounted"/>) are left alone.
        /// </summary>
        private void CheckBrushProps()
        {
            DetailVolumes? detailVolumes = null;
            foreach (Brush b in _brushes)
            {
                if ((b.Flags & (uint)BrushFlags.Detail) == 0 || (b.Flags & (uint)BrushFlags.Air) != 0 || b.Geometry.Vertices.Count == 0)
                {
                    continue;
                }

                (Vec3d mn, Vec3d mx) = SpatialAnalysis.BrushBounds(b);
                double w = mx.X - mn.X, d = mx.Z - mn.Z, h = mx.Y - mn.Y;
                if (w > 4 || d > 4 || h > 3.2 || h < 0.3 || Math.Min(w, d) < 0.4)
                {
                    continue; // not a prop-sized box: trim, beams and structure are left alone
                }

                string part = TagsOf(b.Uid).GetValueOrDefault("kit.part") ?? string.Empty;
                if (part.Contains("step", StringComparison.Ordinal) || part.Contains("stair", StringComparison.Ordinal) || part.Contains("rail", StringComparison.Ordinal)
                    || SpatialAnalysis.BrushRockShare(b) >= 0.5)
                {
                    continue; // steps and rails are structure; a rock half buried in the ground is how rock sits
                }

                // The floor around it, 0.3 m outside each side, looked for from above its top (pieces overhead passed over).
                var floors = new List<double>();
                Vec3d c = mn.Add(mx).Scale(0.5);
                foreach ((double x, double z) in new[] { (mn.X - 0.3, c.Z), (mx.X + 0.3, c.Z), (c.X, mn.Z - 0.3), (c.X, mx.Z + 0.3) })
                {
                    if (SupportUnder(x, z, mx.Y + 0.3, mn.Y, 3) is double fy)
                    {
                        floors.Add(fy);
                    }
                }

                if (floors.Count < 2)
                {
                    continue;
                }

                if (WallMounted(b, detailVolumes ??= new DetailVolumes(_brushes)))
                {
                    continue; // a wall fixture (a lamp, a plate, an emblem strip): its wall holds it
                }

                floors.Sort();
                double floorY = floors[floors.Count / 2];
                double sink = floorY - mn.Y;
                var pos = new Vec3d(c.X, floorY, c.Z);
                if (sink > SinkTolerance && floorY < mx.Y - 0.1)
                {
                    Add(new CraftFinding("props", "sunk", LintSeverity.Warning,
                        $"Detail brush {b.Uid} (a {F(w)} x {F(h)} x {F(d)} m prop) is sunk {F(sink)} m into the floor at {P(pos)}.", pos, b.Uid,
                        $"Raise it by {F(sink)} m so its bottom rests on the floor.", sink));
                    continue;
                }

                // Floating: no top within 0.3 m under its bottom at its centre or inset corners, cast from just above its
                // bottom face so a support exactly at (or a few cm into) its bottom — a crate on a crate, a lamp on a
                // trim — is found.
                double lift = Math.Min(0.1, h / 2), ix = w / 2 * 0.7, iz = d / 2 * 0.7;
                double? under = null;
                foreach ((double dx, double dz) in new[] { (0.0, 0.0), (ix, iz), (-ix, iz), (ix, -iz), (-ix, -iz) })
                {
                    if (FloorBelow(new Vec3d(c.X + dx, mn.Y + lift, c.Z + dz), 30 + lift) is double fy && (under is null || fy > under))
                    {
                        under = fy;
                    }
                }

                if (under is double uy && mn.Y - uy > FloatTolerance && floors.All(f => mn.Y - f > FloatTolerance))
                {
                    Add(new CraftFinding("props", "floating", LintSeverity.Warning,
                        $"Detail brush {b.Uid} (a {F(w)} x {F(h)} x {F(d)} m prop) floats {F(mn.Y - uy)} m above the floor at {P(pos)}.", new Vec3d(c.X, mn.Y, c.Z), b.Uid,
                        $"Lower it by {F(mn.Y - uy)} m onto the floor (or support it).", mn.Y - uy));
                }
            }
        }

        // ---- openings -------------------------------------------------------------------------------------------

        /// <summary>The kit's organic tube builders: their air segments are slices of one tube, never doorways.</summary>
        private static readonly string[] TubeKits = { "cave", "canyon", "passage" };

        /// <summary>True when the brush is a segment of an organic tube (kit.cave, kit.canyon, kit.passage, or geo.tube / geo.passage segments tagged part = "seg").</summary>
        private bool TubeSegment(Brush b)
        {
            Dictionary<string, string> t = TagsOf(b.Uid);
            string kit = t.GetValueOrDefault("kit") ?? string.Empty, part = t.GetValueOrDefault("kit.part") ?? string.Empty;
            return TubeKits.Contains(kit, StringComparer.Ordinal)
                || (part.StartsWith("seg", StringComparison.Ordinal) && part.Length > 3 && char.IsDigit(part[3]));
        }

        /// <summary>
        /// The clear width across <paramref name="along"/> at <paramref name="q"/> (the two walls either side), capped at
        /// <paramref name="cap"/> each way; +∞ when a side is open.
        /// </summary>
        private double WidthAt(Vec3d q, Vec3d along, double cap)
        {
            double a = _sp.Cast(q, along, cap, SpaceSampler.HitFilter.Any) ?? double.PositiveInfinity;
            double b = _sp.Cast(q, along.Scale(-1), cap, SpaceSampler.HitFilter.Any) ?? double.PositiveInfinity;
            return a + b;
        }

        /// <summary>
        /// True when the cut at <paramref name="mid"/> (half depth <paramref name="half"/> along <paramref name="axis"/>,
        /// <paramref name="wide"/> m across) opens a wall: on at least one side, 1 m out, the space beyond is clearly
        /// wider than the cut (a room or yard the doorway leads into). A slice of a tube or a corridor has the same walls
        /// running on past it on both sides (or solid, where a tube climbs), so it is no doorway.
        /// </summary>
        private bool OpensWall(Vec3d mid, Vec3d axis, double half, Vec3d along, double wide)
        {
            double need = Math.Max(wide + OpeningWiden, wide * 1.5);
            foreach (int s in new[] { 1, -1 })
            {
                Vec3d q = mid.Add(axis.Scale(s * (half + 1.0)));
                if (!_world.IsInSolid(q, _ground) && WidthAt(q, along, need) >= need)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Doorway candidates: door-sized air brushes cut through a wall, door frames, door meshes.</summary>
        private void FindOpenings()
        {
            foreach (Brush b in _brushes)
            {
                if ((b.Flags & (uint)BrushFlags.Air) == 0 || b.Geometry.Vertices.Count == 0 || TubeSegment(b))
                {
                    continue;
                }

                (Vec3d mn, Vec3d mx) = SpatialAnalysis.BrushBounds(b);
                double w = mx.X - mn.X, d = mx.Z - mn.Z, h = mx.Y - mn.Y;
                double thin = Math.Min(w, d), wide = Math.Max(w, d);
                if (h < 1.8 || h > 6 || wide < 0.8 || wide > OpeningMaxWidth || thin > 4.5 || thin > 0.6 * wide)
                {
                    continue;
                }

                Vec3d axis = w < d ? new Vec3d(1, 0, 0) : new Vec3d(0, 0, 1);  // through the wall
                Vec3d along = w < d ? new Vec3d(0, 0, 1) : new Vec3d(1, 0, 0);
                var c = new Vec3d((mn.X + mx.X) / 2, mn.Y, (mn.Z + mx.Z) / 2);
                var mid = new Vec3d(c.X, mn.Y + 1, c.Z);
                if (_world.HeightAboveWalkableFloor(mid, 1.3) is null)
                {
                    continue; // no floor in it: a window or a vent, not a doorway
                }

                // Jambs: a wall on both sides of the opening, across its width.
                double j1 = _sp.Cast(mid, along, (wide / 2) + 0.6, SpaceSampler.HitFilter.Any) ?? double.PositiveInfinity;
                double j2 = _sp.Cast(mid, along.Scale(-1), (wide / 2) + 0.6, SpaceSampler.HitFilter.Any) ?? double.PositiveInfinity;
                if (double.IsInfinity(j1) || double.IsInfinity(j2) || j1 < (wide / 2) - 0.6 || j2 < (wide / 2) - 0.6)
                {
                    continue; // in open space (it opens no wall), or narrower inside than the cut: not a doorway
                }

                // A doorway is cut through a wall: just beside the cut, at its middle, is solid.
                if (!_world.IsInSolid(mid.Add(along.Scale((wide / 2) + 0.3)), _ground) || !_world.IsInSolid(mid.Add(along.Scale(-(wide / 2) - 0.3)), _ground))
                {
                    continue;
                }

                if (!OpensWall(mid, axis, thin / 2, along, wide))
                {
                    continue; // a slice of a tube or corridor: the same walls run on past it on both sides
                }

                _openings.Add((new Vec3d(c.X, mn.Y, c.Z), axis.Scale(thin / 2), wide, b.Uid));
            }

            FindFrames();

            // Door meshes.
            foreach (LevelObject o in _level.Objects.Where(o => o.Kind is LevelObjectKind.Clutter or LevelObjectKind.MeshObject or LevelObjectKind.Entity
                && ((o.ClassName ?? string.Empty).Contains("door", StringComparison.OrdinalIgnoreCase)
                    || (o.Model is AlpineMeshObject m && m.MeshFilename.Contains("door", StringComparison.OrdinalIgnoreCase)))))
            {
                Vec3d f = V(Orientation(o).Forward);
                f = new Vec3d(f.X, 0, f.Z);
                if (f.Length < 1e-6)
                {
                    continue;
                }

                Vec3d p = V(o.Position);
                double floorY = FloorBelow(p.Add(new Vec3d(0, 0.5, 0)), 4) ?? p.Y;
                _openings.Add((new Vec3d(p.X, floorY, p.Z), f.Scale(0.3 / f.Length), 2, o.Uid));
            }
        }

        /// <summary>
        /// Door frames (kit door frame modules and the kit's primitive jamb-and-lintel frames): one candidate per frame,
        /// its pieces found by their kit.opening tag (else their kit.name, split into touching clusters, so the frames of
        /// one detail pass are never merged into one wide "opening"). A frame counts only when it is door-sized (at most
        /// <see cref="FrameMaxWidth"/> m across), has a jamb on both sides of its clear middle and walkable floor at its
        /// sill; its doorway is skipped when an air-brush candidate already stands there.
        /// </summary>
        private void FindFrames()
        {
            var brushByUid = new Dictionary<int, Brush>();
            foreach (Brush b in _brushes)
            {
                brushByUid.TryAdd(b.Uid, b);
            }

            IEnumerable<IGrouping<string, KeyValuePair<int, Dictionary<string, string>>>> groups = _tags
                .Where(kv => (kv.Value.GetValueOrDefault("kit.module") ?? string.Empty).Contains("door_frame", StringComparison.Ordinal)
                    || ((kv.Value.GetValueOrDefault("kit.part") ?? string.Empty).Contains("door_frame", StringComparison.Ordinal) && kv.Value.ContainsKey("kit.opening")))
                .GroupBy(kv => kv.Value.TryGetValue("kit.opening", out string? op) ? "opening:" + op
                    : "name:" + (kv.Value.GetValueOrDefault("kit.name") ?? kv.Key.ToString(CultureInfo.InvariantCulture)));
            foreach (IGrouping<string, KeyValuePair<int, Dictionary<string, string>>> g in groups)
            {
                var boxes = g.Select(kv => brushByUid.TryGetValue(kv.Key, out Brush? b) && b.Geometry.Vertices.Count > 0 ? b : null)
                    .Where(b => b is not null).Cast<Brush>()
                    .Select(b => (Uid: b.Uid, Box: SpatialAnalysis.BrushBounds(b))).ToList();
                if (boxes.Count == 0)
                {
                    continue;
                }

                // One opening's frame (both faces of a thick wall included) is one candidate; a name shared by many
                // frames is split into its touching pieces.
                bool oneOpening = g.Key.StartsWith("opening:", StringComparison.Ordinal);
                foreach (List<(int Uid, (Vec3d Min, Vec3d Max) Box)> cluster in oneOpening ? new List<List<(int Uid, (Vec3d Min, Vec3d Max) Box)>> { boxes } : Touching(boxes))
                {
                    AddFrame(cluster);
                }
            }
        }

        /// <summary>The boxes split into clusters whose boxes touch (within 0.5 m).</summary>
        private static List<List<(int Uid, (Vec3d Min, Vec3d Max) Box)>> Touching(List<(int Uid, (Vec3d Min, Vec3d Max) Box)> boxes)
        {
            const double Gap = 0.5;
            var parent = Enumerable.Range(0, boxes.Count).ToArray();
            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    (Vec3d a0, Vec3d a1) = boxes[i].Box;
                    (Vec3d b0, Vec3d b1) = boxes[j].Box;
                    if (a0.X <= b1.X + Gap && b0.X <= a1.X + Gap && a0.Y <= b1.Y + Gap && b0.Y <= a1.Y + Gap && a0.Z <= b1.Z + Gap && b0.Z <= a1.Z + Gap)
                    {
                        parent[Find(i)] = Find(j);
                    }
                }
            }

            return Enumerable.Range(0, boxes.Count).GroupBy(Find).Select(c => c.Select(i => boxes[i]).ToList()).ToList();
        }

        private void AddFrame(List<(int Uid, (Vec3d Min, Vec3d Max) Box)> parts)
        {
            var mn = new Vec3d(parts.Min(x => x.Box.Min.X), parts.Min(x => x.Box.Min.Y), parts.Min(x => x.Box.Min.Z));
            var mx = new Vec3d(parts.Max(x => x.Box.Max.X), parts.Max(x => x.Box.Max.Y), parts.Max(x => x.Box.Max.Z));
            double w = mx.X - mn.X, d = mx.Z - mn.Z, h = mx.Y - mn.Y;
            double wide = Math.Max(w, d);
            if (wide > FrameMaxWidth || wide < 0.8 || h < 1.8)
            {
                return; // not a door-sized frame (trim runs, a whole wall's dressing)
            }

            Vec3d axis = w < d ? new Vec3d(1, 0, 0) : new Vec3d(0, 0, 1);
            Vec3d along = w < d ? new Vec3d(0, 0, 1) : new Vec3d(1, 0, 0);
            var c = new Vec3d((mn.X + mx.X) / 2, mn.Y, (mn.Z + mx.Z) / 2);
            var mid = new Vec3d(c.X, mn.Y + 1, c.Z);
            if (_openings.Any(o => Math.Abs(o.Centre.X - c.X) < 1 && Math.Abs(o.Centre.Z - c.Z) < 1))
            {
                return; // its doorway is already a candidate
            }

            // The clear middle is open, its sill walkable, with a jamb (the frame's post or the wall) on both sides.
            if (_world.IsInSolid(mid, _ground) || _world.HeightAboveWalkableFloor(mid, 1.6) is null)
            {
                return;
            }

            double reach = (wide / 2) + 0.3;
            if (_sp.Cast(mid, along, reach, SpaceSampler.HitFilter.Any) is null || _sp.Cast(mid, along.Scale(-1), reach, SpaceSampler.HitFilter.Any) is null)
            {
                return;
            }

            _openings.Add((c, axis.Scale(Math.Min(w, d) / 2), wide, parts[0].Uid));
        }

        private void CheckOpenings()
        {
            foreach ((Vec3d c, Vec3d halfAxis, double width, int? uid) in _openings)
            {
                _ct.ThrowIfCancellationRequested();
                double hd = halfAxis.Length;
                Vec3d n = halfAxis.Scale(1 / Math.Max(hd, 1e-6));
                var mid = new Vec3d(c.X, c.Y + 1, c.Z);
                foreach (int s in new[] { 1, -1 })
                {
                    Vec3d dir = n.Scale(s);
                    Vec3d q = mid.Add(dir.Scale(hd + 1.0));
                    string? why = null;
                    if (_world.IsInSolid(q, _ground))
                    {
                        why = "solid (rock or a wall) behind it";
                    }
                    else if (!_world.LineOfSight(mid, q))
                    {
                        why = "a wall right behind it";
                    }
                    else if (_world.HeightAboveWalkableFloor(q, 1 + OpeningDrop + 0.5) is not double gap)
                    {
                        why = $"no walkable floor within {F(OpeningDrop)} m below (a drop or void)";
                    }
                    else if (gap - 1 > OpeningDrop)
                    {
                        why = $"a {F(gap - 1)} m drop";
                    }
                    else
                    {
                        double floorY = q.Y - gap;
                        double room = _sp.Cast(new Vec3d(q.X, floorY + 0.02, q.Z), Up, SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing) ?? double.PositiveInfinity;
                        if (room < Headroom)
                        {
                            why = $"only {F(room)} m headroom";
                        }
                    }

                    if (why is not null)
                    {
                        Add(new CraftFinding("openings", "into_nothing", LintSeverity.Warning,
                            $"The {F(width)} m opening at {P(c)} opens onto {why} on its {(s > 0 ? "+" : "-")}{(Math.Abs(n.X) > Math.Abs(n.Z) ? "X" : "Z")} side.",
                            c, uid, "Carve walkable floor with headroom on both sides (or remove the opening / frame); a doorway must join two walkable places."));
                        break;
                    }
                }
            }
        }

        // ---- the intent layer: flights, bridges, ledges, props, purpose tags -------------------------------------

        private void IntentChecks(SpatialAnalysis a)
        {
            var (flights, bridges, props, ledges) = a.PurposeReadings();
            foreach (SpatialAnalysis.FlightRead f in flights)
            {
                string what = f.Rise >= 1 && f.Run > 0 && f.Rise / f.Run > 0.6 ? "stair flight" : "ramp";
                string head = $"The {what} from {P(f.Foot)} up {F(f.Rise)} m to {P(f.Top)}";
                if (f.Kind is "dead_end")
                {
                    if (On("perches"))
                    {
                        Add(new CraftFinding("perches", "dead_end", LintSeverity.Warning, $"{head} {f.Fault}.", f.Top, null,
                            "Lead the top on to the rest of the level (a landing that joins a route), give the perch a reason (an item, a sightline, a way down), or remove the flight."));
                    }
                }
                else if (f.Kind is not null and not "redundant" && On("stairs"))
                {
                    Add(new CraftFinding("stairs", f.Kind, LintSeverity.Warning, $"{head} {f.Fault}.", f.Kind == "blocked" && f.FootClear < f.TopClear ? f.Foot : f.Top, null,
                        f.Kind switch
                        {
                            "into_obstacle" => "Clear the space past the top step (carve the wall, lower the ceiling's rock) or end the flight at a landing that leads on.",
                            "blocked" => "Move the wall or prop so at least 1 m is clear in front of the first step and past the top step.",
                            "onto_nothing" => "Build a landing beyond the top step that joins walkable floor.",
                            _ => "Join the pit to the route, or remove the flight.",
                        }));
                }

                if (f.Kind is null && On("stairs"))
                {
                    // Headroom at both ends, 1 m beyond the steps.
                    foreach ((Vec3d end, Vec3d dir, string side) in new[] { (f.Top, f.Dir, "top"), (f.Foot, f.Dir.Scale(-1), "foot") })
                    {
                        Vec3d q = end.Add(dir.Scale(1.0)).Add(new Vec3d(0, 0.5, 0));
                        if (FloorBelow(q, 2) is double fy)
                        {
                            double room = _sp.Cast(new Vec3d(q.X, fy + 0.02, q.Z), Up, SpaceSampler.SkyCap, SpaceSampler.HitFilter.DownFacing) ?? double.PositiveInfinity;
                            if (room < Headroom)
                            {
                                Add(new CraftFinding("stairs", "low_headroom", LintSeverity.Warning, $"{head} has only {F(room)} m headroom past its {side}.", q, null,
                                    $"Raise the ceiling over the {side} landing to at least {F(Headroom)} m, or move the flight.", room));
                                break;
                            }
                        }
                    }
                }
            }

            if (On("stairs"))
            {
                foreach (SpatialAnalysis.BridgeRoute b in bridges.Where(b => b.Fault is not null && b.NotBridge is null))
                {
                    int[] deck = a.DeckBrushes(b.Bridge);
                    Add(new CraftFinding("stairs", "purposeless_bridge", LintSeverity.Warning,
                        $"The {F(b.Bridge.Span)} m bridge at {P(b.Bridge.Centre)}{(deck.Length > 0 ? $" (deck brush {deck[0]})" : string.Empty)}: {b.Fault}.", b.Bridge.Centre,
                        deck.Length > 0 ? deck[0] : null,
                        "Span a real gap between two walkable places that are otherwise far apart, or remove the bridge.", Uids: deck));
                }

                foreach (SpatialAnalysis.LedgeRead l in ledges)
                {
                    Add(new CraftFinding("stairs", "route_ledge", LintSeverity.Warning,
                        $"A {F(l.Step)} m step between two floors at {P(l.Pos)} with no way round on foot.", l.Pos, null,
                        "Level the two floors, or join them with stairs or a ramp."));
                }
            }

            if (On("props"))
            {
                foreach (SpatialAnalysis.PropRead p in props.Where(p => p.Fault is "theme" or "scattered" or "orphan_cover"))
                {
                    string why = p.Fault switch
                    {
                        "theme" => "a rock prop free-standing in a built space (an interior or a walled yard)",
                        "scattered" => "a free-standing rock that is not cover (rubble belongs against walls and edges)",
                        _ => "cover with no sightline past it to break",
                    };
                    var at = new Vec3d(p.Centre.X, p.FloorY, p.Centre.Z);
                    Add(new CraftFinding("props", "random", LintSeverity.Warning, $"The prop at {P(at)} is {why}.", at, p.Uids.Length > 0 ? p.Uids[0] : null,
                        p.Fault == "orphan_cover" ? "Move it onto a sightline it can break, or against a wall as dressing." : "Move it against a wall or floor edge, or replace it with a built prop that fits the space.",
                        Uids: p.Uids));
                }

                RandomClutter(flights, props);
            }

            if (On("purpose"))
            {
                PurposeTags(flights, bridges.Where(b => b.NotBridge is null).ToList());
            }
        }

        /// <summary>Clutter objects in a doorway, on a flight, or lone in the open.</summary>
        private void RandomClutter(IReadOnlyList<SpatialAnalysis.FlightRead> flights, IReadOnlyList<SpatialAnalysis.PropRead> props)
        {
            List<LevelObject> clutter = _level.Objects.Where(o => o.Kind is LevelObjectKind.Clutter or LevelObjectKind.MeshObject && !o.IsReadOnly).ToList();
            var others = clutter.Select(o => V(o.Position)).Concat(props.Select(p => p.Centre)).ToList();
            foreach (LevelObject o in clutter)
            {
                Vec3d p = V(o.Position);
                string name = $"{o.Kind} '{o.DisplayName}' (uid {o.Uid})";
                var door = _openings.FirstOrDefault(d => Math.Abs(d.Centre.Y - p.Y) < 2.5 && Plan(d.Centre, p) < Math.Min(1.5, (d.Width / 2) + 0.3));
                if (door.Axis.Length > 0)
                {
                    Add(new CraftFinding("props", "in_doorway", LintSeverity.Warning, $"{name} stands in the doorway at {P(door.Centre)}.", p, o.Uid,
                        "Move it out of the doorway, beside the frame or against a wall inside the room."));
                    continue;
                }

                var flight = flights.FirstOrDefault(f => OnFlight(f, p));
                if (flight is not null)
                {
                    Add(new CraftFinding("props", "on_stairs", LintSeverity.Warning, $"{name} stands on the flight from {P(flight.Foot)} to {P(flight.Top)}.", p, o.Uid,
                        "Move it off the steps, onto a landing beside the flight."));
                    continue;
                }

                if (!FloorStanding(o))
                {
                    continue;
                }

                var eye = new Vec3d(p.X, p.Y + 0.5, p.Z);
                double wall = SpaceSampler.Directions.Select(d => _sp.Cast(eye, d, LoneWall, SpaceSampler.HitFilter.Any) ?? double.PositiveInfinity).Min();
                if (double.IsInfinity(wall) && !others.Any(q => Plan(q, p) > 0.05 && Plan(q, p) < LoneNeighbour && Math.Abs(q.Y - p.Y) < 3))
                {
                    Add(new CraftFinding("props", "lone", LintSeverity.Warning, $"{name} stands alone in the open at {P(p)}: no wall within {F(LoneWall)} m and no other prop within {F(LoneNeighbour)} m.", p, o.Uid,
                        "Set it against a wall or edge, group it with related props, or place it where it covers a sightline."));
                }
            }
        }

        private static double Plan(Vec3d a, Vec3d b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

        private static bool OnFlight(SpatialAnalysis.FlightRead f, Vec3d p)
        {
            Vec3d ab = new(f.Top.X - f.Foot.X, 0, f.Top.Z - f.Foot.Z);
            double len2 = (ab.X * ab.X) + (ab.Z * ab.Z);
            if (len2 < 1e-6)
            {
                return false;
            }

            double t = (((p.X - f.Foot.X) * ab.X) + ((p.Z - f.Foot.Z) * ab.Z)) / len2;
            if (t < 0.05 || t > 0.95)
            {
                return false;
            }

            double y = f.Foot.Y + (t * (f.Top.Y - f.Foot.Y));
            double ax = f.Foot.X + (t * ab.X), az = f.Foot.Z + (t * ab.Z);
            return Math.Sqrt(((p.X - ax) * (p.X - ax)) + ((p.Z - az) * (p.Z - az))) < (f.Width / 2) + 0.2 && Math.Abs(p.Y - y) < 1.5;
        }

        /// <summary>Elements tagged kit.purpose_mode off / quiet / report: findings unless the built level satisfies their purpose.</summary>
        private void PurposeTags(IReadOnlyList<SpatialAnalysis.FlightRead> flights, IReadOnlyList<SpatialAnalysis.BridgeRoute> bridges)
        {
            foreach (IGrouping<string, KeyValuePair<int, Dictionary<string, string>>> g in _tags
                .Where(kv => kv.Value.GetValueOrDefault("kit.purpose_mode") is "off" or "quiet" or "report")
                .GroupBy(kv => kv.Value.GetValueOrDefault("kit.name") ?? kv.Key.ToString(CultureInfo.InvariantCulture)))
            {
                List<Brush> parts = g.Select(kv => _brushes.FirstOrDefault(b => b.Uid == kv.Key)).Where(b => b is not null).Cast<Brush>().ToList();
                if (parts.Count == 0)
                {
                    continue;
                }

                Dictionary<string, string> tags = g.First().Value;
                string mode = tags["kit.purpose_mode"];
                string role = tags.GetValueOrDefault("kit.role") ?? "route";
                if (role != "route")
                {
                    continue; // landmarks and theme details: the visibility reading is the grader's (spatial.purpose landmarks)
                }

                var boxes = parts.Select(SpatialAnalysis.BrushBounds).ToList();
                var mn = new Vec3d(boxes.Min(x => x.Min.X) - 1.5, boxes.Min(x => x.Min.Y) - 1.5, boxes.Min(x => x.Min.Z) - 1.5);
                var mx = new Vec3d(boxes.Max(x => x.Max.X) + 1.5, boxes.Max(x => x.Max.Y) + 1.5, boxes.Max(x => x.Max.Z) + 1.5);
                bool In(Vec3d p) => p.X >= mn.X && p.X <= mx.X && p.Y >= mn.Y && p.Y <= mx.Y && p.Z >= mn.Z && p.Z <= mx.Z;
                var centre = mn.Add(mx).Scale(0.5);
                string what = $"'{g.Key}' (built with purpose = \"{mode}\")";
                SpatialAnalysis.FlightRead? fr = flights.FirstOrDefault(f => In(f.Centre));
                SpatialAnalysis.BridgeRoute? br = bridges.FirstOrDefault(b => In(b.Bridge.Centre));
                string? fault = fr is { Kind: not null and not "redundant" } ? fr.Fault : br?.Fault;
                if (fault is not null)
                {
                    Add(new CraftFinding("purpose", "purpose_failed", LintSeverity.Warning, $"{what} still fails its purpose: {fault}.", centre, parts[0].Uid,
                        "Rebuild it with purpose = \"require\" where it serves a route, or remove it.", Uids: parts.Select(b => b.Uid).ToArray()));
                }
                else if (fr is null && br is null && boxes.Max(x => x.Max.Y) - boxes.Min(x => x.Min.Y) >= SpatialAnalysis.FlightMinRise + 0.5)
                {
                    Add(new CraftFinding("purpose", "purpose_unread", LintSeverity.Warning,
                        $"{what}: the built level shows no working flight or bridge there (it is not on the floor the spawns reach, or it joins nothing).", centre, parts[0].Uid,
                        "Connect it to the routes (spatial.purpose{} lists what is read), rebuild it with purpose = \"require\", or remove it.", Uids: parts.Select(b => b.Uid).ToArray()));
                }
            }
        }

        // ---- rock and density -----------------------------------------------------------------------------------

        /// <summary>
        /// The share of a face that a player can see: of its probe points (the centroid; on a face large enough to be a flat
        /// side, a grid over its surface, <see cref="ProbeGrid"/>), those with open space just in front of them
        /// (<see cref="RockProbe"/> m along the normal): in the level's air and not inside a detail brush. A structural wall
        /// behind a detail rock skin is hidden (detail brushes do not carve, so it survives the build), and the skin's own
        /// back faces stand in solid, in the void outside the level or inside other rock.
        /// </summary>
        private double VisibleShare((Vec3d[] Poly, Vec3d Normal, double Area) poly, Vec3d centre, DetailVolumes detail, StructuralFaces world)
        {
            Vec3d[] pts = poly.Poly;
            int probes = 0, open = 0;
            Vec3d lift = poly.Normal.Scale(RockProbe);

            // A face too small to be a flat side only counts toward the region's total: its centroid decides. A larger
            // face is probed over its whole surface (a wall a skin covers in part is seen in part).
            foreach (Vec3d s in poly.Area >= RockFlatFace ? ProbeGrid(pts, poly.Normal, poly.Area, centre) : new List<Vec3d> { centre })
            {
                probes++;
                Vec3d q = s.Add(lift);
                if (!detail.Contains(q) && world.InAir(q, _ground))
                {
                    open++;
                }
            }

            return probes > 0 ? open / (double)probes : 0;
        }

        /// <summary>
        /// The compiled level's structural (non-detail) faces, for "is this point in the level's air": the first structural
        /// face straight below it is met from the front. Detail faces bound no space, so the void outside the level, where
        /// the backs of detail rocks poke out, does not read as open.
        /// </summary>
        private sealed class StructuralFaces
        {
            private readonly List<SpaceSampler.SFace> _faces;
            private readonly FaceBvh _bvh;

            public StructuralFaces(SpaceSampler sp, Geometry geo)
            {
                _faces = sp.Faces.Where(f => (geo.Faces[f.Source].Flags & (ushort)FaceFlags.IsDetail) == 0).ToList();
                _bvh = new FaceBvh(_faces);
            }

            public bool InAir(Vec3d p, TerrainGround? ground)
            {
                if (ground is { } g && g.IsUnder(p))
                {
                    return false;
                }

                return _bvh.Raycast(p, Down, SpaceSampler.SkyCap, SpaceSampler.HitFilter.Any, out int fi) is not null && fi >= 0 && _faces[fi].N.Dot(Down) < 0;
            }
        }

        /// <summary>
        /// Probe points spread over a face: the centres of a square grid in its plane that fall inside it, about one per
        /// <see cref="RockProbeSpacing"/>² (at least 5, at most <see cref="RockMaxProbes"/>); the centroid when the grid
        /// misses a thin face.
        /// </summary>
        private static List<Vec3d> ProbeGrid(Vec3d[] pts, Vec3d normal, double area, Vec3d centre)
        {
            Vec3d ua = pts[1].Sub(pts[0]);
            if (ua.Length < 1e-9)
            {
                return new List<Vec3d> { centre };
            }

            Vec3d u = ua.Scale(1 / ua.Length), v = normal.Cross(u);
            var q = new (double U, double V)[pts.Length];
            double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                Vec3d d = pts[i].Sub(centre);
                q[i] = (d.Dot(u), d.Dot(v));
                u0 = Math.Min(u0, q[i].U);
                u1 = Math.Max(u1, q[i].U);
                v0 = Math.Min(v0, q[i].V);
                v1 = Math.Max(v1, q[i].V);
            }

            double target = Math.Clamp(area / (RockProbeSpacing * RockProbeSpacing), 5, RockMaxProbes);
            double step = Math.Sqrt(area / target);
            var probes = new List<Vec3d>();
            for (double a = u0 + (step / 2); a < u1; a += step)
            {
                for (double b = v0 + (step / 2); b < v1; b += step)
                {
                    if (InPolygon(q, a, b))
                    {
                        probes.Add(centre.Add(u.Scale(a)).Add(v.Scale(b)));
                    }
                }
            }

            if (probes.Count == 0)
            {
                probes.Add(centre);
            }

            // A long thin face can give more cells than the target: keep an even spread of them.
            if (probes.Count > RockMaxProbes)
            {
                double stride = probes.Count / (double)RockMaxProbes;
                probes = Enumerable.Range(0, RockMaxProbes).Select(i => probes[(int)(i * stride)]).ToList();
            }

            return probes;
        }

        private static bool InPolygon((double U, double V)[] q, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            {
                if ((q[i].V > y) != (q[j].V > y) && x < ((q[j].U - q[i].U) * (y - q[i].V) / (q[j].V - q[i].V)) + q[i].U)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private void CheckRock()
        {
            Geometry geo = _level.Geometry;
            var detail = new DetailVolumes(_brushes);
            var world = new StructuralFaces(_sp, geo);
            var cells = new Dictionary<(int, int), (double All, double Flat, double Y, double Hidden)>();
            var flatFaces = new Dictionary<(int, int), List<(Vec3d P, Vec3d N, double Weight)>>();
            for (int i = 0; i < geo.Faces.Count; i++)
            {
                if ((i & 1023) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                }

                Face f = geo.Faces[i];
                if (f.IsPortalFace || f.Vertices.Count < 3 || f.Texture < 0 || f.Texture >= geo.Textures.Count
                    || !SpatialAnalysis.RockTexture(geo.Textures[f.Texture].ToLowerInvariant()))
                {
                    continue;
                }

                if (f.RoomIndex >= 0 && f.RoomIndex < geo.Rooms.Count && geo.Rooms[f.RoomIndex].IsSkyroom != 0)
                {
                    continue; // the sky room's backdrop is not played in
                }

                if (SpaceSampler.Polygon(geo, f) is not { } poly || poly.Normal.Y >= SpaceSampler.FloorNormalY)
                {
                    continue; // floors are not rock walls
                }

                Vec3d c = poly.Poly.Aggregate(new Vec3d(0, 0, 0), (s, p) => s.Add(p)).Scale(1.0 / poly.Poly.Length);
                var key = ((int)Math.Floor(c.X / RockCell), (int)Math.Floor(c.Z / RockCell));
                (double all, double flat, double y, double hidden) = cells.GetValueOrDefault(key);
                double seen = VisibleShare(poly, c, detail, world);
                double area = poly.Area * seen;
                bool flatSide = Math.Abs(poly.Normal.Y) < 0.1 && poly.Area >= RockFlatFace;
                cells[key] = (all + area, flat + (flatSide ? area : 0), y + (c.Y * area), hidden + (poly.Area - area));
                if (flatSide && area > 0)
                {
                    if (!flatFaces.TryGetValue(key, out List<(Vec3d, Vec3d, double)>? fl))
                    {
                        flatFaces[key] = fl = new List<(Vec3d, Vec3d, double)>();
                    }

                    fl.Add((c, poly.Normal, area));
                }
            }

            foreach (KeyValuePair<(int, int), (double All, double Flat, double Y, double Hidden)> kv in cells.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            {
                if (kv.Value.All < RockMinArea)
                {
                    continue;
                }

                double share = kv.Value.Flat / kv.Value.All;
                var pos = new Vec3d((kv.Key.Item1 + 0.5) * RockCell, kv.Value.Y / kv.Value.All, (kv.Key.Item2 + 0.5) * RockCell);
                bool prism = share > RockFlatShare;
                Report.RockRegions.Add(new JsonObject
                {
                    ["pos"] = new JsonArray(Math.Round(pos.X, 1), Math.Round(pos.Y, 1), Math.Round(pos.Z, 1)),
                    ["rock_area"] = Math.Round(kv.Value.All, 1),
                    ["hidden_area"] = Math.Round(kv.Value.Hidden, 1),
                    ["flat_share"] = Math.Round(share, 3),
                    ["verdict"] = prism ? "prism" : "organic",
                });
                if (prism)
                {
                    // The brushes behind the flat sides, the largest flat area first.
                    int[] uids = flatFaces.TryGetValue(kv.Key, out List<(Vec3d, Vec3d, double)>? fl)
                        ? SpatialAnalysis.BrushesOnFaces(_brushes, fl)
                        : Array.Empty<int>();
                    string who = uids.Length switch
                    {
                        0 => string.Empty,
                        1 => $" (brush {uids[0]})",
                        _ => $" (brushes {string.Join(", ", uids.Take(6))}{(uids.Length > 6 ? $" and {uids.Length - 6} more" : string.Empty)})",
                    };
                    Add(new CraftFinding("rock", "flat_sided", LintSeverity.Warning,
                        $"The rock around {P(pos)} is flat-sided{who}: {share:P0} of its {kv.Value.All:0} m2 surface is in large vertical planes (stacked prisms).", pos,
                        uids.Length > 0 ? uids[0] : null,
                        "Rebuild it with the organic builders (kit.rock_wall, kit.canyon, kit.cave, kit.boulders, kit.rock_detail along the trace) so the rock leans, overhangs and breaks up.", share, uids));
                }
            }
        }

        private void CheckDensity(SpatialAnalysis a)
        {
            double g2 = a.Pitch * a.Pitch;
            var cells = new Dictionary<(int, int), List<Vec3d>>();
            var ceilings = new Dictionary<(int, int), double>();
            foreach ((Vec3d p, double headroom) in a.PlayableSamples())
            {
                var key = ((int)Math.Floor(p.X / DensityCell), (int)Math.Floor(p.Z / DensityCell));
                if (!cells.TryGetValue(key, out List<Vec3d>? l))
                {
                    cells[key] = l = new List<Vec3d>();
                }

                l.Add(p);

                // The space over this floor: up to its ceiling (a fixture hangs at or just into it), or the open-sky reach.
                double top = p.Y + Math.Min(double.IsFinite(headroom) ? headroom : LightSkyReach, LightSkyReach) + LightCeilingSlack;
                ceilings[key] = Math.Max(ceilings.GetValueOrDefault(key, double.NegativeInfinity), top);
            }

            double? lightsP5 = CorpusNorms.Available ? CorpusNorms.Get("lights.per_100m2")?.P5 : null;
            var props = _level.Objects.Where(o => o.Kind is LevelObjectKind.Clutter or LevelObjectKind.MeshObject or LevelObjectKind.Item or LevelObjectKind.Decal).Select(o => V(o.Position)).ToList();
            var lights = _level.Objects.Where(o => o.Kind is LevelObjectKind.Light).Select(o => V(o.Position)).ToList();
            var detail = _brushes.Where(b => (b.Flags & (uint)BrushFlags.Detail) != 0 && (b.Flags & (uint)BrushFlags.Air) == 0 && b.Geometry.Vertices.Count > 0)
                .Select(b => SpatialAnalysis.BrushBounds(b)).Select(x => x.Min.Add(x.Max).Scale(0.5)).ToList();
            foreach (KeyValuePair<(int, int), List<Vec3d>> kv in cells.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            {
                double area = kv.Value.Count * g2;
                if (area < DensityMinArea)
                {
                    continue;
                }

                double y0 = kv.Value.Min(p => p.Y) - 1, y1 = kv.Value.Max(p => p.Y) + 8;
                bool InPlan(Vec3d p) => Math.Floor(p.X / DensityCell) == kv.Key.Item1 && Math.Floor(p.Z / DensityCell) == kv.Key.Item2;
                bool In(Vec3d p) => InPlan(p) && p.Y >= y0 && p.Y <= y1;

                // Lights count up to the ceiling over the region's floor (a tall hall's ceiling fixtures light it).
                double lightTop = Math.Max(y1, ceilings[kv.Key]);
                int nProps = props.Count(In), nLights = lights.Count(p => InPlan(p) && p.Y >= y0 && p.Y <= lightTop), nDetail = detail.Count(In);
                double per = 100 / area;
                double dressing = (nProps + nDetail) * per, lightD = nLights * per;
                double ys = Math.Sqrt(kv.Value.Select(p => p.Y).Select(y => y * y).Average() - Math.Pow(kv.Value.Average(p => p.Y), 2));
                var pos = new Vec3d((kv.Key.Item1 + 0.5) * DensityCell, kv.Value.Average(p => p.Y), (kv.Key.Item2 + 0.5) * DensityCell);
                bool basic = dressing < DensityMinDetail || (lightsP5 is double l5 && lightD < l5);
                Report.DensityRegions.Add(new JsonObject
                {
                    ["pos"] = new JsonArray(Math.Round(pos.X, 1), Math.Round(pos.Y, 1), Math.Round(pos.Z, 1)),
                    ["floor_area"] = Math.Round(area, 1),
                    ["props_per_100m2"] = Math.Round(nProps * per, 2),
                    ["detail_per_100m2"] = Math.Round(nDetail * per, 2),
                    ["lights_per_100m2"] = Math.Round(lightD, 2),
                    ["floor_height_sd"] = Math.Round(double.IsFinite(ys) ? ys : 0, 2),
                    ["verdict"] = basic ? "basic" : "ok",
                });
                if (basic)
                {
                    string lightNote = lightsP5 is double p5 && lightD < p5 ? $"; {lightD:0.##} lights per 100 m2 (corpus p5 {p5:0.##})" : string.Empty;
                    Add(new CraftFinding("density", "basic", LintSeverity.Warning,
                        $"The {area:0} m2 of floor around {P(pos)} is basic: {dressing:0.##} props and detail pieces per 100 m2 (at least {F(DensityMinDetail)}){lightNote}.", pos, null,
                        "Dress it: kit.dress / kit.dress_trace (trim, pillars, props against walls, decals) and light it (kit.light_room); vary the floor and ceiling heights.", dressing));
                }
            }
        }
    }
}
