using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Analysis;

/// <summary>How far a metric can be trusted (CORPUS-NORMS-SPEC §3).</summary>
public enum MetricReliability
{
    Low,
    Medium,
    High,
}

/// <summary>
/// One fingerprint metric: its id (<c>group.field</c> — the JSON path in the fingerprint), unit, reliability,
/// whether a level records a scalar or a distribution (whose per-level summary value is its p50), and the
/// one-line description the norms file and help text show.
/// </summary>
public sealed record MetricDef(string Id, string Unit, MetricReliability Reliability, bool IsDistribution, string Doc)
{
    /// <summary>The fingerprint group (the part before the first dot).</summary>
    public string Group => Id[..Id.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>The field inside the group.</summary>
    public string Field => Id[(Id.IndexOf('.', StringComparison.Ordinal) + 1)..];
}

/// <summary>
/// The <c>glacier.fingerprint/1</c> schema: version constants, statuses, and the table of numeric metrics a
/// fingerprint carries. A metric id <c>a.b</c> is always found at <c>fingerprint["a"]["b"]</c>; a group that could
/// not be measured is <c>null</c> with its reason under <c>skipped["a"]</c>.
/// </summary>
public static class FingerprintSchema
{
    /// <summary>The schema id written into every fingerprint.</summary>
    public const string Schema = "glacier.fingerprint/1";

    /// <summary>
    /// The extractor version. Bump it whenever a metric's definition or the fingerprint layout changes: cached
    /// fingerprints from another version are recomputed by the corpus sweep. Version 2 adds the level class and the
    /// player class to <c>identity</c> (the per-class norm buckets, DESIGN-QUALITY-SPEC E12).
    /// </summary>
    public const int ExtractorVersion = 2;

    /// <summary>The level classes (<c>identity.class.value</c>): the design grader's classes (DESIGN-QUALITY-SPEC §(h)).</summary>
    public static readonly IReadOnlyList<string> LevelClasses = new[] { "standard", "open_air", "payload" };

    /// <summary>The player classes (<c>identity.playerClass</c>, by spawn count: S &lt;= 10, M 11-18, L &gt; 18).</summary>
    public static readonly IReadOnlyList<string> PlayerClasses = new[] { "S", "M", "L" };

    /// <summary>Per-level status values (the index and failures rows use the same strings).</summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string OkPartial = "ok_partial";
        public const string UnsupportedVersion = "unsupported_version";
        public const string ParseError = "parse_error";
        public const string Timeout = "timeout";
        public const string Excluded = "excluded";
    }

    /// <summary>Group skip reasons that mean "not applicable" (the level is still <c>ok</c>, not <c>ok_partial</c>).</summary>
    public static readonly IReadOnlySet<string> NotApplicableReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "no_portals", "no_stairs", "no_openings", "fewer_than_4_rooms", "no_lights", "no_spawns", "no_items",
        "not_ctf", "no_events", "no_brushes", "no_corridors",
    };

    private static MetricDef D(string id, string unit, MetricReliability r, string doc) => new(id, unit, r, true, doc);

    private static MetricDef S(string id, string unit, MetricReliability r, string doc) => new(id, unit, r, false, doc);

    /// <summary>Every numeric metric, in a stable order.</summary>
    public static readonly IReadOnlyList<MetricDef> Metrics = new[]
    {
        S("space.floor_area", "m2", MetricReliability.High, "Walkable floor area (floor faces with at least 1 m of headroom)."),
        D("space.headroom", "m", MetricReliability.High, "Clear height above the floor (open-sky floor excluded)."),
        S("space.open_sky_fraction", "ratio", MetricReliability.High, "Share of the floor with no ceiling within 200 m."),
        D("space.clear_width", "m", MetricReliability.High, "Narrowest horizontal span through each floor point, at chest height."),
        D("corridor.width", "m", MetricReliability.Medium, "Clear width of corridors (spaces at most 8 m wide and at least 2.5 times as long)."),
        D("corridor.height", "m", MetricReliability.Medium, "Clear height of corridors."),
        S("corridor.fraction", "ratio", MetricReliability.Medium, "Share of the floor area that is corridor."),
        D("opening.width", "m", MetricReliability.Medium, "Clear width of doorways and other narrowings between spaces."),
        D("opening.height", "m", MetricReliability.Medium, "Clear height (lintel) of doorways and other narrowings."),
        S("opening.count", "count", MetricReliability.Medium, "Number of detected doorways and narrowings."),
        D("portal.width", "m", MetricReliability.High, "Width of vertical portals."),
        D("portal.height", "m", MetricReliability.High, "Height of vertical portals."),
        S("portal.horizontal_count", "count", MetricReliability.High, "Number of horizontal portals (floor hatches)."),
        D("stair.riser", "m", MetricReliability.Medium, "Step height of stairs."),
        D("stair.tread", "m", MetricReliability.Medium, "Step depth of stairs."),
        S("stair.count", "count", MetricReliability.Medium, "Number of detected stairs."),
        S("vertical.span", "m", MetricReliability.High, "Height range of the walkable floor (p99 - p1)."),
        S("vertical.layers", "count", MetricReliability.Medium, "Number of distinct floor levels (1 m bins holding at least 5% of the floor)."),
        D("room.width", "m", MetricReliability.Low, "Larger horizontal side of each room's bounding box."),
        D("room.depth", "m", MetricReliability.Low, "Smaller horizontal side of each room's bounding box."),
        D("room.height", "m", MetricReliability.Low, "Height of each room's bounding box."),
        D("room.volume", "m3", MetricReliability.Low, "Volume of each room's bounding box."),
        S("topology.rooms", "count", MetricReliability.High, "Main rooms (not sub-rooms or sky rooms)."),
        S("topology.portals", "count", MetricReliability.High, "Portal connections between main rooms."),
        S("topology.components", "count", MetricReliability.High, "Connected groups of rooms."),
        D("topology.degree", "count", MetricReliability.High, "Portal connections per room."),
        S("topology.dead_ends", "count", MetricReliability.High, "Rooms with exactly one portal connection."),
        S("topology.loops", "count", MetricReliability.High, "Independent loops in the room graph (E - V + C)."),
        S("topology.diameter", "count", MetricReliability.High, "Longest shortest room-to-room path in the largest group."),
        S("textures.count", "count", MetricReliability.High, "Distinct textures on the compiled faces."),
        D("texel.tile_metres", "m", MetricReliability.High, "World size of one texture repeat (area-weighted)."),
        S("detail.area_fraction", "ratio", MetricReliability.High, "Share of the face area that comes from detail brushes."),
        S("brush.count", "count", MetricReliability.High, "Number of brushes."),
        D("brush.faces", "count", MetricReliability.High, "Faces per brush."),
        S("brush.air_fraction", "ratio", MetricReliability.High, "Share of brushes that are air."),
        S("lights.count", "count", MetricReliability.High, "Number of lights."),
        S("lights.per_100m2", "per 100 m2", MetricReliability.High, "Lights per 100 m2 of floor."),
        D("lights.range", "m", MetricReliability.High, "Light range."),
        D("lights.intensity", "ratio", MetricReliability.High, "Light on-intensity."),
        S("lights.dynamic_fraction", "ratio", MetricReliability.High, "Share of dynamic lights."),
        S("lights.warm_fraction", "ratio", MetricReliability.High, "Share of lights with a warm hue (0-70 degrees)."),
        S("lights.cool_fraction", "ratio", MetricReliability.High, "Share of lights with a cool hue (170-260 degrees)."),
        S("lights.neutral_fraction", "ratio", MetricReliability.High, "Share of lights with little colour (saturation below 0.12)."),
        D("lights.height_above_floor", "m", MetricReliability.Medium, "Light height above the floor below it."),
        S("spawns.count", "count", MetricReliability.High, "Multiplayer respawn points."),
        D("spawns.nn_distance", "m", MetricReliability.High, "Distance from each respawn point to its nearest neighbour."),
        S("spawns.per_100m2", "per 100 m2", MetricReliability.High, "Respawn points per 100 m2 of floor."),
        D("spawns.clear_width", "m", MetricReliability.Medium, "Clear width at each respawn point."),
        S("items.count", "count", MetricReliability.High, "Number of items."),
        D("items.nn_distance", "m", MetricReliability.High, "Distance from each item to its nearest item."),
        D("items.to_nearest_spawn", "m", MetricReliability.High, "Distance from each item to the nearest respawn point."),
        D("items.height_above_floor", "m", MetricReliability.Medium, "Item height above the floor below it."),
        S("ctf.flag_distance", "m", MetricReliability.High, "Distance between the two flags."),
        S("events.count", "count", MetricReliability.High, "Number of events."),
        S("events.triggers", "count", MetricReliability.High, "Number of triggers."),
        S("events.links", "count", MetricReliability.High, "Number of object links."),
    };

    private static readonly Dictionary<string, MetricDef> ById = Metrics.ToDictionary(m => m.Id, StringComparer.Ordinal);

    /// <summary>The metric with id <paramref name="id"/>, or null.</summary>
    public static MetricDef? Find(string id) => id is not null && ById.TryGetValue(id, out MetricDef? m) ? m : null;

    /// <summary>The lower-case JSON spelling of a reliability.</summary>
    public static string Name(MetricReliability r) => r switch
    {
        MetricReliability.High => "high",
        MetricReliability.Medium => "medium",
        _ => "low",
    };
}
