namespace Ged.Core.Scripting;

// The table options of the spatial and design globals (DESIGN-QUALITY-SPEC §(e)).
public static partial class ScriptOptionSets
{
    private const string Point = "vec | {x, y, z} | handle";
    private const string RegionDoc = "Only this box: {minx, miny, minz, maxx, maxy, maxz} in metres.";
    private const string BuildDoc = "\"sealed\" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; \"none\" measures the stored geometry.";
    private const string SamplesDoc = "Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).";
    private const string TimeoutDoc = "Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).";

    public static readonly ScriptOptionSet SpatialRaycast = new("spatial.raycast",
        new("from", Point, null, "Required: the ray origin (m)."),
        new("dir", "vec | {x, y, z}", null, "Required: the direction (any length)."),
        new("max", "number", "200", "The longest distance to look (m)."));

    public static readonly ScriptOptionSet SpatialWalk = new("spatial.walk",
        new("from", Point, "nil", "Also report the floor reachable on foot (and by lifts, jump pads, teleports) from this point."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialRoutes = new("spatial.routes",
        new("a", Point, null, "Required: the start (snapped to the playable floor within 6 m)."),
        new("b", Point, null, "Required: the end."),
        new("k", "integer", "3", "How many node-disjoint routes to look for (1-8)."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialGraph = new("spatial.graph",
        new("kind", "string", "\"cell\"", "\"cell\" (connected floor per cell) or \"space\" (spaces and connectors)."),
        new("scale", "integer", "8", "Cell size for kind \"cell\": 8 (layout) or 4 (fine loops)."),
        new("region", "list of 6 numbers", "nil", "List only the nodes inside this box (the totals stay level-wide)."),
        new("max_nodes", "integer", "200", "At most this many nodes listed."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialSightlines = new("spatial.sightlines",
        new("region", "list of 6 numbers", "nil", RegionDoc),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialCover = new("spatial.cover",
        new("region", "list of 6 numbers", "nil", RegionDoc),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialCells = new("spatial.cells",
        new("region", "list of 6 numbers", "nil", RegionDoc),
        new("max", "integer", "200", "At most this many cells listed."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialArea = new("spatial.area_report",
        new("region", "list of 6 numbers", "nil", RegionDoc),
        new("around", Point, "nil", "Instead of region: everything within radius metres of this point."),
        new("radius", "number", "nil", "With around: the radius (m)."),
        new("player_class", "string", "from the spawns", "The targets' player class: \"S\", \"M\" or \"L\"."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet DesignGrade = new("design.grade",
        new("gametype", "string", "detected", "dm, ctf, koth, payload, …; detected from the level (Payload_Handler, CTF flags, capture points, file name) when omitted."),
        new("players", "integer", "from the spawns", "The intended player count (sets the player class whose targets are reported)."),
        new("class", "string", "detected", "Force the level class: \"standard\", \"open_air\" or \"payload\"."),
        new("max_locations", "integer", "6", "Locations listed per failure."),
        new("metrics", "boolean", "false", "Also return the whole metric table."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "120", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialPurpose = new("spatial.purpose",
        new("max", "integer", "20", "At most this many entries per list (bridges, mismatched, orphans, landmarks)."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));

    public static readonly ScriptOptionSet SpatialSetPieces = new("spatial.setpieces",
        new("max", "integer", "10", "At most this many entries per list (bridges, walkways, landmarks, atria, largest spaces)."),
        new("build", "string", "\"none\"", BuildDoc),
        new("max_samples", "integer", "600000", SamplesDoc),
        new("timeout_s", "number", "60", TimeoutDoc));
}
