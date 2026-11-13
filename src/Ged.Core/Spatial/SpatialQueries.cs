using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

/// <summary>Limits on one spatial query (DESIGN-QUALITY-SPEC §(e) guardrails).</summary>
public sealed class SpatialBudget
{
    /// <summary>Default lattice sample budget for agent queries: about 150,000 m² of floor at the 0.5 m pitch.</summary>
    public const int DefaultMaxSamples = 600_000;

    /// <summary>Lattice samples before the pitch doubles (coarser, faster).</summary>
    public int MaxSamples { get; init; } = DefaultMaxSamples;

    /// <summary>Wall-clock limit of the measurement (null = none).</summary>
    public TimeSpan? TimeLimit { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Traverse lifts, jump pads and teleports.</summary>
    public bool MovementLinks { get; init; } = true;
}

/// <summary>
/// The single entry point behind Lua <c>spatial.*</c> / <c>design.grade</c>, the <c>spatial</c> and <c>grade</c> automation
/// commands (CLI verbs) and the MCP tools: an operation name and JSON arguments in, a JSON result out. Measurements are
/// cached per compiled geometry, object layout and options, so a sequence of queries on unchanged geometry measures once.
/// </summary>
public static class SpatialQueries
{
    /// <summary>The operations, with one line each (the CLI help and tool schemas list them).</summary>
    public static IReadOnlyList<(string Op, string Doc)> Ops { get; } = new[]
    {
        ("raycast", "First face along a ray: args from, dir, max (m, default 200)."),
        ("los", "Line of sight between two points: args a, b."),
        ("point_info", "Solid or air, floor below, ceiling above, headroom, room: args pos."),
        ("clearance", "Does a player fit at a floor point: args pos, profile (player | crouch)."),
        ("walk", "Reachable floor, unreachable spawns and items, movement links; args from (optional)."),
        ("routes", "Shortest walk path and k node-disjoint routes: args a, b, k (default 3, at most 8)."),
        ("graph", "Layout graph: args kind (cell | space), scale (8 | 4), region, max_nodes."),
        ("sightlines", "Engagement distances, longest sightlines, exposure; args region (optional)."),
        ("cover", "Cover share and distance to cover; args region (optional)."),
        ("cells", "8 m cells with dressing and plane counts (undressed / bare); args region (optional)."),
        ("area_report", "Floor, widths, headroom, ceiling variety, walls, spaces, dressing, textures, items, spawns vs targets; args region or around + radius."),
        ("spawn_report", "Every spawn: nearest spawn, exposure, nearest item, spawns in sight."),
        ("item_report", "Every item: category, traffic percentile, pocket, weapon/powerup spacing."),
        ("setpieces", "Set pieces: bridges (span, width, gap width and depth, space below), landmarks (height), atria, largest spaces; args max."),
        ("locations", "Actionable locations of one kind (chokepoints, pockets, bare_cells, exposed_spawns, …): args kind, max."),
    };

    /// <summary>The operations that only need the face BVH (instant; no floor lattice).</summary>
    public static IReadOnlySet<string> PointOps { get; } = new HashSet<string>(StringComparer.Ordinal) { "raycast", "los", "point_info", "clearance" };

    private sealed record CacheEntry(long ObjectsKey, int MaxSamples, bool Links, string Freshness, bool Textures, SpatialAnalysis Analysis);

    private static readonly ConditionalWeakTable<Geometry, CacheEntry> Cache = new();

    /// <summary>
    /// The measurement of <paramref name="level"/> under <paramref name="budget"/>, from the cache when the geometry,
    /// objects and options are unchanged. Throws <see cref="SpatialException"/> <c>timeout</c> when it runs past the
    /// budget's time limit.
    /// </summary>
    public static SpatialAnalysis Analyze(SpatialLevel level, SpatialBudget? budget = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        budget ??= new SpatialBudget();
        long key = ObjectsKey(level);
        lock (Cache)
        {
            if (Cache.TryGetValue(level.Geometry, out CacheEntry? hit) && hit.ObjectsKey == key && hit.MaxSamples == budget.MaxSamples
                && hit.Links == budget.MovementLinks && hit.Freshness == level.Freshness.State + "/" + level.Freshness.Measured
                && hit.Textures == (level.TextureSize is not null))
            {
                return hit.Analysis;
            }
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget.TimeLimit is { } t)
        {
            limit.CancelAfter(t);
        }

        try
        {
            SpatialAnalysis a = SpatialAnalysis.Run(level, new SpatialOptions { MaxSamples = budget.MaxSamples, MovementLinks = budget.MovementLinks }, limit.Token);
            lock (Cache)
            {
                Cache.AddOrUpdate(level.Geometry, new CacheEntry(key, budget.MaxSamples, budget.MovementLinks, level.Freshness.State + "/" + level.Freshness.Measured, level.TextureSize is not null, a));
            }

            return a;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpatialException("timeout", $"The measurement did not finish within {budget.TimeLimit!.Value.TotalSeconds:0} s.",
                "Raise the time limit (timeout_s), or lower max_samples so the lattice coarsens.");
        }
    }

    /// <summary>Runs one operation. Throws <see cref="SpatialException"/> for bad arguments (<c>usage</c>) and the analysis errors.</summary>
    public static JsonObject Run(SpatialLevel level, string op, JsonObject? args, SpatialBudget? budget = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        args ??= new JsonObject();
        op = (op ?? string.Empty).Trim().ToLowerInvariant();
        if (PointOps.Contains(op))
        {
            SpatialWorld w = SpatialWorld.For(level.Geometry);
            JsonObject r = op switch
            {
                "raycast" => w.Raycast(Point(args, "from", true)!.Value, Point(args, "dir", true)!.Value, Number(args, "max") ?? 200),
                "los" => LosResult(w, Point(args, "a", true)!.Value, Point(args, "b", true)!.Value),
                "point_info" => w.PointInfo(Point(args, "pos", true)!.Value),
                _ => w.Clearance(Point(args, "pos", true)!.Value, Str(args, "profile") ?? "player"),
            };
            r["geometry"] = level.Freshness.ToJson();
            return r;
        }

        if (!Ops.Any(o => o.Op == op))
        {
            throw new SpatialException("usage", $"Unknown spatial operation '{op}'.", $"Operations: {string.Join(", ", Ops.Select(o => o.Op))}.");
        }

        SpatialAnalysis a = Analyze(level, budget, ct);
        SpatialRegion? region = Region(args);
        return op switch
        {
            "walk" => a.WalkQuery(Point(args, "from", false)),
            "routes" => a.Routes(Point(args, "a", true)!.Value, Point(args, "b", true)!.Value, (int)(Number(args, "k") ?? 3)),
            "graph" => a.Graph(Str(args, "kind") ?? "cell", (int)(Number(args, "scale") ?? 8), region, (int)(Number(args, "max_nodes") ?? 200)),
            "sightlines" => a.Sightlines(region),
            "cover" => a.Cover(region),
            "cells" => a.Cells(region, (int)(Number(args, "max") ?? 200)),
            "area_report" => a.AreaReport(region, Str(args, "player_class")),
            "spawn_report" => a.SpawnReportQuery((int)(Number(args, "max") ?? 100)),
            "item_report" => a.ItemReportQuery((int)(Number(args, "max") ?? 200)),
            "setpieces" => a.SetPieces((int)(Number(args, "max") ?? 10)),
            _ => LocationsResult(a, Str(args, "kind") ?? throw new SpatialException("usage", "locations needs a kind.", $"Kinds: {string.Join(", ", SpatialAnalysis.LocationKinds)}."), (int)(Number(args, "max") ?? 20)),
        };
    }

    /// <summary>Grades <paramref name="level"/> (DESIGN-QUALITY-SPEC §(d)); the same code as the benchmark.</summary>
    public static JsonObject Grade(SpatialLevel level, GradeOptions? options = null, SpatialBudget? budget = null, CancellationToken ct = default) =>
        DesignGrader.Grade(Analyze(level, budget, ct), options);

    private static JsonObject LosResult(SpatialWorld w, Vec3d a, Vec3d b) => new()
    {
        ["visible"] = w.LineOfSight(a, b),
        ["a"] = SpatialAnalysis.P3(a),
        ["b"] = SpatialAnalysis.P3(b),
        ["distance"] = Math.Round(a.Sub(b).Length, 3),
    };

    private static JsonObject LocationsResult(SpatialAnalysis a, string kind, int max)
    {
        JsonObject h = a.Header();
        h["kind"] = kind;
        h["locations"] = a.Locations(kind, Math.Clamp(max, 1, 200));
        return h;
    }

    /// <summary>A cheap fingerprint of what the measurement reads besides geometry: objects (kind, uid, class, position) and respawn points.</summary>
    internal static long ObjectsKey(SpatialLevel level)
    {
        var h = new HashCode();
        foreach (LevelObject o in level.Objects)
        {
            h.Add(o.Kind);
            h.Add(o.Uid);
            h.Add(o.ClassName);
            h.Add(o.Position);
        }

        foreach (MpRespawnPoint p in level.Sections<MpRespawnPointsSection>().SelectMany(s => s.Points))
        {
            h.Add(p.Uid);
            h.Add(p.Position);
            h.Add(p.RedTeam);
            h.Add(p.BlueTeam);
        }

        h.Add(level.Brushes().Count);
        return h.ToHashCode();
    }

    // ---- argument parsing (JSON shapes shared by every surface) ---------------------------------------

    /// <summary>A point argument: [x, y, z] or {x, y, z}; null when absent and not required.</summary>
    public static Vec3d? Point(JsonObject args, string name, bool required)
    {
        JsonNode? n = args[name];
        if (n is null)
        {
            return required ? throw new SpatialException("usage", $"'{name}' is required: a point [x, y, z] in metres.") : null;
        }

        double[]? v = n switch
        {
            JsonArray a when a.Count == 3 => a.Select(x => Num(x)).ToArray() is { } arr && arr.All(x => x is not null) ? arr.Select(x => x!.Value).ToArray() : null,
            JsonObject o => new[] { Num(o["x"]), Num(o["y"]), Num(o["z"]) } is { } arr2 && arr2.All(x => x is not null) ? arr2.Select(x => x!.Value).ToArray() : null,
            _ => null,
        };
        return v is not null && v.All(double.IsFinite)
            ? new Vec3d(v[0], v[1], v[2])
            : throw new SpatialException("usage", $"'{name}' must be a point [x, y, z] of 3 finite numbers.");
    }

    /// <summary>
    /// The region argument: <c>region</c> = [minx, miny, minz, maxx, maxy, maxz] (or {min, max}), or <c>around</c> = a point
    /// with <c>radius</c> (m); null when neither is given.
    /// </summary>
    public static SpatialRegion? Region(JsonObject args)
    {
        if (args["around"] is not null)
        {
            double r = Number(args, "radius") ?? throw new SpatialException("usage", "'around' needs a 'radius' in metres.");
            return r > 0 ? SpatialRegion.Around(Point(args, "around", true)!.Value, r) : throw new SpatialException("usage", "'radius' must be positive.");
        }

        switch (args["region"])
        {
            case null:
                return null;
            case JsonArray a when a.Count == 6 && a.All(x => Num(x) is double d && double.IsFinite(d)):
                return SpatialRegion.Box(new Vec3d(Num(a[0])!.Value, Num(a[1])!.Value, Num(a[2])!.Value), new Vec3d(Num(a[3])!.Value, Num(a[4])!.Value, Num(a[5])!.Value));
            case JsonObject o when o["min"] is not null && o["max"] is not null:
                return SpatialRegion.Box(Point(o, "min", true)!.Value, Point(o, "max", true)!.Value);
            default:
                throw new SpatialException("usage", "'region' must be 6 numbers (minx, miny, minz, maxx, maxy, maxz) or {min, max}.");
        }
    }

    private static double? Number(JsonObject args, string name) => args[name] is null ? null
        : Num(args[name]) ?? throw new SpatialException("usage", $"'{name}' must be a number.");

    private static string? Str(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => throw new SpatialException("usage", $"'{name}' must be a string."),
    };

    private static double? Num(JsonNode? n) => DesignMetrics.Number(n);

    /// <summary>The level of an editor document with its freshness, for the editing hosts (<paramref name="state"/>: built, preview, stale, none).</summary>
    public static SpatialLevel DocumentLevel(EditorDocument doc, string state, Func<string, (int, int)?>? textureSize, Geometry? fresh = null, string? freshKind = null)
    {
        GeometryFreshness f = fresh is not null
            ? new GeometryFreshness(state, freshKind ?? "preview", $"Measured on a fresh {freshKind ?? "preview"} build made for this query (not applied to the level).")
            : new GeometryFreshness(state, "stored", state switch
            {
                "stale" => "The brushes changed since the last build: these results describe the last build, not the current brushes. Build again, or ask for a fresh build.",
                "preview" => "The stored geometry is an unsealed preview build.",
                _ => null,
            });
        SpatialLevel level = SpatialLevel.FromDocument(doc, fresh, f);
        return textureSize is null ? level : level.WithTextureSize(textureSize);
    }
}
