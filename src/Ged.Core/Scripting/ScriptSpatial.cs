using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>spatial</c> global (DESIGN-QUALITY-SPEC §(e) E1–E8): raycast, line of sight, point info and clearance on the
/// compiled geometry, and the measurements behind the design grader — walk graph, routes, layout graphs, sightlines,
/// cover, cells, area report, spawn and item reports, and actionable locations. Everything reads the level's compiled
/// geometry (build first: <c>ops.build()</c>, or pass <c>build = "sealed"</c>); every result carries <c>geometry</c>
/// (state built / preview / stale / none / unknown, and what was measured), so a script can tell whether it measured
/// its latest brushes. Results are tables shaped exactly like the CLI / MCP JSON; points are vec values.
/// Measurements are cached per geometry and object layout, and budgeted (sample cap, time limit).
/// </summary>
[ScriptDoc("Spatial queries on the compiled geometry: raycast, los, point_info, clearance, walk, routes, graph, sightlines, cover, cells, area_report, spawn_report, item_report, locations, setpieces. Results carry geometry freshness.", Since = 2)]
public sealed class ScriptSpatial
{
    /// <summary>Keys whose 3-number arrays become vec values in Lua results.</summary>
    internal static readonly HashSet<string> PointKeys = new(StringComparer.Ordinal)
    {
        "pos", "to", "from", "a", "b", "snapped", "normal", "around", "min", "max", "widest_cell", "start", "end",
    };

    private readonly ScriptContext _ctx;
    private readonly Geometry? _baseGeometry;
    private readonly long _baseBrushes;
    private Geometry? _seenGeometry;
    private long _seenBrushes;

    internal ScriptSpatial(ScriptContext ctx)
    {
        _ctx = ctx;
        _baseGeometry = SpatialLevel.StoredGeometry(ctx.Document.Rfl);
        _baseBrushes = SpatialLevel.BrushFingerprint(ctx.Document.Rfl);
    }

    [ScriptDoc("The first face along a ray: {hit, distance, pos, normal, back_face, face, room, texture, sky, geometry}. Instant.",
        Example = "local h = spatial.raycast{from = {0, 1.6, 0}, dir = {1, 0, 0}} if h.hit then print(h.distance) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialRaycast))]
    public Dictionary<string, object?> Raycast(ScriptArgs args) => Point("spatial.raycast", "raycast", args, ScriptOptionSets.SpatialRaycast);

    [ScriptDoc("True when nothing blocks the straight line between two points (vec, {x, y, z} or an object/brush handle). Instant.",
        Example = "if spatial.los(a.pos + ged.vec(0, 1.6, 0), b.pos + ged.vec(0, 1.6, 0)) then log.warn(\"spawns see each other\") end", Since = 2)]
    public bool Los(object a, object b)
    {
        JsonObject r = Run("spatial.los", "los", new JsonObject { ["a"] = Pt("spatial.los", "a", a), ["b"] = Pt("spatial.los", "b", b) });
        return r["visible"]!.GetValue<bool>();
    }

    [ScriptDoc("What is at a point: {in_solid, floor{y, distance}, ceiling{y, distance}, headroom, open_sky, room, geometry}. Instant.",
        Example = "local p = spatial.point_info({4, 1, 4}) assert(not p.in_solid)", Since = 2)]
    public Dictionary<string, object?> PointInfo(object pos) =>
        ToLua(Run("spatial.point_info", "point_info", new JsonObject { ["pos"] = Pt("spatial.point_info", "pos", pos) }));

    [ScriptDoc("Does a player fit at a floor point: {fits, floor_y, headroom, nearest_wall, reason}. profile \"player\" (2 m) or \"crouch\" (1.375 m), radius 0.5 m. Instant.",
        Example = "assert(spatial.clearance(spawn.pos).fits)", Since = 2)]
    public Dictionary<string, object?> Clearance(object pos, string profile = "player") =>
        ToLua(Run("spatial.clearance", "clearance", new JsonObject { ["pos"] = Pt("spatial.clearance", "pos", pos), ["profile"] = profile }));

    [ScriptDoc("The walk graph: floor components, unreachable spawns and items, movement links (lifts, jump pads, teleports); with from, the floor reachable from there. Seconds, then cached.",
        Example = "local w = spatial.walk() for _, s in ipairs(w.unreachable_spawns) do log.warn(\"spawn off the floor at \" .. tostring(s.pos)) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialWalk))]
    public Dictionary<string, object?> Walk(ScriptArgs? args = null) => Measure("spatial.walk", "walk", args, ScriptOptionSets.SpatialWalk);

    [ScriptDoc("Routes between two points: the shortest walk (length, waypoints) and up to k node-disjoint routes on the 8 m cell graph (lengths, waypoints).",
        Example = "local r = spatial.routes{a = red_flag, b = blue_flag, k = 3} print(r.disjoint_routes, r.walk_distance)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialRoutes))]
    public Dictionary<string, object?> Routes(ScriptArgs args) => Measure("spatial.routes", "routes", args, ScriptOptionSets.SpatialRoutes);

    [ScriptDoc("A layout graph: the 8 m / 4 m cell graph or the space graph, with loops, chokepoints (and the share each cuts off), pockets, dead ends and nodes.",
        Example = "local g = spatial.graph{kind = \"cell\", scale = 8} for _, c in ipairs(g.chokepoints) do print(c.pos, c.cuts_off) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialGraph))]
    public Dictionary<string, object?> Graph(ScriptArgs? args = null) => Measure("spatial.graph", "graph", args, ScriptOptionSets.SpatialGraph);

    [ScriptDoc("Sightlines and AREA exposure (G20: floor within 50 m a spot sees; not the spawn measure): engagement, longest sightlines, visible pairs, exposed spots; with region, from spots in it.",
        Example = "local s = spatial.sightlines{region = {-20, 0, -20, 20, 10, 20}} print(s.region.exposure.p50)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialSightlines))]
    public Dictionary<string, object?> Sightlines(ScriptArgs? args = null) => Measure("spatial.sightlines", "sightlines", args, ScriptOptionSets.SpatialSightlines);

    [ScriptDoc("Cover: share of the floor beside cover, walk distance to cover (all and open floor), and the cells farthest from cover.",
        Example = "print(spatial.cover().dist_to_cover_open.p90)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialCover))]
    public Dictionary<string, object?> Cover(ScriptArgs? args = null) => Measure("spatial.cover", "cover", args, ScriptOptionSets.SpatialCover);

    [ScriptDoc("The 8 m cells with their dressing count, visible planes, undressed and bare flags (the cells kit.dress fills).",
        Example = "for _, c in ipairs(spatial.cells().cell_list) do if c.bare then print(c.pos) end end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialCells))]
    public Dictionary<string, object?> Cells(ScriptArgs? args = null) => Measure("spatial.cells", "cells", args, ScriptOptionSets.SpatialCells);

    [ScriptDoc("An area report (region, around + radius, or the level): floor area, widths, headroom, ceilings, walls, spaces, dressing, textures, items, spawns, with the exemplar targets.",
        Example = "local r = spatial.area_report{around = {0, 0, 0}, radius = 16} print(r.floor_area, r.dressing_per100)", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialArea))]
    public Dictionary<string, object?> AreaReport(ScriptArgs? args = null) => Measure("spatial.area_report", "area_report", args, ScriptOptionSets.SpatialArea);

    [ScriptDoc("Every playable spawn: nearest spawn, SPAWN exposure (G19: playable floor within 40 m seen; kit.spawn_points uses it), nearest item, spawns in sight within 30 m; plus the level figures.",
        Example = "for _, s in ipairs(spatial.spawn_report().spawn_list) do if s.exposure > 0.25 then print(s.uid) end end", Since = 2)]
    public Dictionary<string, object?> SpawnReport() => Measure("spatial.spawn_report", "spawn_report", null, null);

    [ScriptDoc("Every item: category, route-traffic percentile, pocket membership, nearest weapon/powerup; plus the level figures.",
        Example = "for _, i in ipairs(spatial.item_report().item_list) do if i.in_pocket and i.category == \"powerup\" then print(i.uid) end end", Since = 2)]
    public Dictionary<string, object?> ItemReport() => Measure("spatial.item_report", "item_report", null, null);

    [ScriptDoc("Locations of one kind, worst first: chokepoints, pockets, dead_ends, bare_cells, undressed_cells, sightlines, exposed_spawns, exposed_spots, items, spawns_far_from_items, spaces, …",
        Example = "for _, c in ipairs(spatial.locations(\"bare_cells\", 10).locations) do print(c.pos) end", Since = 2)]
    public Dictionary<string, object?> Locations(string kind, int max = 20) =>
        ToLua(Run("spatial.locations", "locations", new JsonObject { ["kind"] = kind, ["max"] = max }));

    [ScriptDoc("How fresh the compiled geometry is: {state = built | preview | stale | none | unknown, measured = \"stored\", note}. stale = brushes changed since the last build.",
        Example = "if spatial.freshness().state == \"stale\" then ops.build() end", Since = 2)]
    public Dictionary<string, object?> Freshness() => ToLua(CurrentFreshness().ToJson());

    // ---- plumbing ---------------------------------------------------------------------------------------------

    internal GeometryFreshness CurrentFreshness()
    {
        Geometry? geo = SpatialLevel.StoredGeometry(_ctx.Document.Rfl);
        if (geo is null)
        {
            return new GeometryFreshness("none", "stored", "The level has no compiled geometry: build it first (ops.build()).");
        }

        long h = SpatialLevel.BrushFingerprint(_ctx.Document.Rfl);
        string state;
        if (ReferenceEquals(geo, _baseGeometry))
        {
            state = h != _baseBrushes ? "stale" : _ctx.Services.BuildState?.Invoke() is { } bs
                ? (bs.GeometryIsPreview ? "preview" : bs.GeometryDirty ? "stale" : "built")
                : "unknown";
        }
        else
        {
            if (!ReferenceEquals(geo, _seenGeometry))
            {
                _seenGeometry = geo;
                _seenBrushes = h;
            }

            state = h == _seenBrushes ? "built" : "stale";
        }

        return new GeometryFreshness(state, "stored", state switch
        {
            "stale" => "Brushes changed since the last build: results describe the last build. Run ops.build() (or pass build = \"sealed\") to measure the current brushes.",
            "preview" => "The stored geometry is an unsealed preview build.",
            "unknown" => "The host did not report whether the stored geometry matches the brushes.",
            _ => null,
        });
    }

    /// <summary>The level as the spatial queries see it (with texture sizes from the mounted library).</summary>
    internal SpatialLevel Level(string api, ScriptArgs? args)
    {
        if (args?.String(api, "build") is { } build)
        {
            switch (build)
            {
                case "sealed":
                    _ctx.Ops.Build();
                    break;
                case "none":
                    break;
                default:
                    throw new ScriptApiException($"{api}: build must be \"none\" or \"sealed\" (got \"{build}\").", "build = \"sealed\" runs ops.build() first (it applies the build, like ops.build).");
            }
        }

        GeometryFreshness f = CurrentFreshness();
        if (f.State == "none")
        {
            throw new ScriptApiException($"{api}: the level has no compiled geometry to measure.", "Run ops.build() first, or pass build = \"sealed\".");
        }

        try
        {
            SpatialLevel level = SpatialLevel.FromDocument(_ctx.Document, null, f);
            var sizes = new Assets.TextureSizeLookup(_ctx.Assets);
            return sizes.Available ? level.WithTextureSize(n => sizes.Get(n)) : level;
        }
        catch (SpatialException ex)
        {
            throw new ScriptApiException($"{api}: {ex.Message}", ex.Hint);
        }
    }

    private Dictionary<string, object?> Point(string api, string op, ScriptArgs args, ScriptOptionSet set)
    {
        args = args ?? throw new ScriptApiException($"{api}: a table argument is required.");
        args.RejectUnknown(set);
        return ToLua(Run(api, op, ToJson(api, args)));
    }

    private Dictionary<string, object?> Measure(string api, string op, ScriptArgs? args, ScriptOptionSet? set)
    {
        if (args is not null && set is not null)
        {
            args.RejectUnknown(set);
        }

        return ToLua(Run(api, op, args is null ? new JsonObject() : ToJson(api, args), args));
    }

    private JsonObject Run(string api, string op, JsonObject json, ScriptArgs? raw = null)
    {
        SpatialLevel level = Level(api, raw);
        var budget = new SpatialBudget
        {
            MaxSamples = raw?.Int(api, "max_samples", SpatialBudget.DefaultMaxSamples) ?? SpatialBudget.DefaultMaxSamples,
            TimeLimit = TimeSpan.FromSeconds(raw?.Number(api, "timeout_s", 60) ?? 60),
        };
        json.Remove("build");
        json.Remove("max_samples");
        json.Remove("timeout_s");
        using (_ctx.EnterHostOperation())
        {
            try
            {
                return SpatialQueries.Run(level, op, json, budget, _ctx.Cancellation);
            }
            catch (SpatialException ex)
            {
                throw new ScriptApiException($"{api}: {ex.Message}", ex.Hint);
            }
        }
    }

    /// <summary>A Lua point argument (vec, {x, y, z} or a handle) as JSON [x, y, z].</summary>
    internal static JsonNode Pt(string api, string what, object? value) => value switch
    {
        ScriptObjectHandle o => V(o.Pos),
        ScriptBrushHandle b => V(b.Pos),
        _ => V(ScriptArgs.ToVec(api, what, value)),
    };

    private static JsonArray V(Vec3 v) => new(Math.Round((double)v.X, 4), Math.Round((double)v.Y, 4), Math.Round((double)v.Z, 4));

    /// <summary>A Lua table argument as JSON (points and handles as [x, y, z]).</summary>
    internal static JsonObject ToJson(string api, ScriptArgs args)
    {
        var o = new JsonObject();
        foreach (KeyValuePair<string, object?> kv in args.Named)
        {
            o[kv.Key] = Value(api, kv.Key, kv.Value);
        }

        return o;
    }

    private static JsonNode? Value(string api, string key, object? v) => v switch
    {
        null => null,
        double d => d,
        bool b => b,
        string s => s,
        Vec3 or ScriptObjectHandle or ScriptBrushHandle => Pt(api, key, v),
        ScriptArgs t when t.Named.Count == 0 && t.Items.Count == 3 && PointKeys.Contains(key) => Pt(api, key, t),
        ScriptArgs t when t.Named.Count == 0 => new JsonArray(t.Items.Select(x => Value(api, key, x)).ToArray()),
        ScriptArgs t => ToJson(api, t),
        _ => throw new ScriptApiException($"{api}: '{key}' has an unsupported value ({v.GetType().Name})."),
    };

    /// <summary>A JSON result as a Lua table: objects become tables, arrays 1-based lists, [x, y, z] under point keys (and waypoints) vec values.</summary>
    internal static Dictionary<string, object?> ToLua(JsonObject o)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> kv in o)
        {
            d[kv.Key] = Lua(kv.Key, kv.Value);
        }

        return d;
    }

    private static object? Lua(string key, JsonNode? n)
    {
        switch (n)
        {
            case null:
                return null;
            case JsonObject o:
                return ToLua(o);
            case JsonArray a when a.Count == 3 && PointKeys.Contains(key) && a.All(x => DesignMetrics.Number(x) is not null):
                return new Vec3((float)DesignMetrics.Number(a[0])!.Value, (float)DesignMetrics.Number(a[1])!.Value, (float)DesignMetrics.Number(a[2])!.Value);
            case JsonArray a:
                string itemKey = key == "waypoints" ? "pos" : key;
                return a.Select(x => Lua(itemKey, x)).ToArray();
            case JsonValue v when v.TryGetValue(out bool b):
                return b;
            case JsonValue v when v.TryGetValue(out string? s):
                return s;
            default:
                return DesignMetrics.Number(n);
        }
    }

    [ScriptDoc("Set pieces: bridges (span = deck end to end, gap_width, width, gap_depth), walkways that are not bridges (with why), landmarks (height), atria and the largest spaces. Seconds, then cached.",
        Example = "local s = spatial.setpieces() if (s.bridge_span_max or 0) < 20 then log.warn(\"no landmark-scale bridge\") end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.SpatialSetPieces))]
    public Dictionary<string, object?> Setpieces(ScriptArgs? args = null) => Measure("spatial.setpieces", "setpieces", args, ScriptOptionSets.SpatialSetPieces);
}

/// <summary>The <c>design</c> global: the self-review grader (DESIGN-QUALITY-SPEC §(d), E11).</summary>
[ScriptDoc("The self-review grader: design.grade{} measures the level and returns the verdict (PASS / REVIEW / FAIL), the failing checks with values, fix hints and locations; design.checks() lists the checks.", Since = 2)]
public sealed class ScriptDesign
{
    private readonly ScriptContext _ctx;

    internal ScriptDesign(ScriptContext ctx) => _ctx = ctx;

    [ScriptDoc("Grades like glacier-cli grade: {verdict, failures_count, class, failures[{id, check, values, fix, locations}], passed, targets, advisories, set_pieces, geometry}. Grade, fix by location, regrade.",
        Example = "local g = design.grade{gametype = \"dm\", players = 8} for _, f in ipairs(g.failures) do log.warn(f.id .. \": \" .. f.fix) end", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.DesignGrade))]
    public Dictionary<string, object?> Grade(ScriptArgs? args = null)
    {
        const string Api = "design.grade";
        args?.RejectUnknown(ScriptOptionSets.DesignGrade);
        SpatialLevel level = _ctx.Spatial.Level(Api, args);
        var options = new GradeOptions
        {
            Gametype = args?.String(Api, "gametype"),
            Players = args?.NumberOrNull(Api, "players") is double p ? (int)p : null,
            Class = args?.String(Api, "class"),
            MaxLocations = args?.Int(Api, "max_locations", 6) ?? 6,
            IncludeMetrics = args?.Bool(Api, "metrics", false) ?? false,
        };
        var budget = new SpatialBudget
        {
            MaxSamples = args?.Int(Api, "max_samples", SpatialBudget.DefaultMaxSamples) ?? SpatialBudget.DefaultMaxSamples,
            TimeLimit = TimeSpan.FromSeconds(args?.Number(Api, "timeout_s", 120) ?? 120),
        };
        using (_ctx.EnterHostOperation())
        {
            try
            {
                return ScriptSpatial.ToLua(SpatialQueries.Grade(level, options, budget, _ctx.Cancellation));
            }
            catch (SpatialException ex)
            {
                throw new ScriptApiException($"{Api}: {ex.Message}", ex.Hint);
            }
        }
    }

    [ScriptDoc("The grader's checks: a list of {id, group, hard, check, fix, class} from the shipped threshold table.",
        Example = "for _, c in ipairs(design.checks()) do print(c.id, c.check) end", Since = 2)]
    public object?[] Checks() => DesignGrader.Table["checks"]!.AsArray().Select(c => (object?)new Dictionary<string, object?>
    {
        ["id"] = c!["id"]!.GetValue<string>(),
        ["group"] = c["group"]?.GetValue<string>(),
        ["hard"] = c["hard"]?.GetValue<bool>() == true,
        ["check"] = c["doc"]?.GetValue<string>(),
        ["fix"] = c["fix"]?.GetValue<string>(),
        ["class"] = c["class"]?.GetValue<string>(),
    }).ToArray();
}
