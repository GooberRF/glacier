namespace Ged.Core.Scripting;

/// <summary>The table options of the terrain API (<see cref="ScriptTerrains"/>, <see cref="ScriptTerrainHandle"/>).</summary>
public static partial class ScriptOptionSets
{
    public static readonly ScriptOptionSet TerrainCreate = new("level.terrain.create",
        new("center", "vec", "{0, 0, 0}", "The centre of the terrain's footprint; its origin (min-X / min-Z corner) is placed half its size away. Y is the origin's height."),
        new("nx", "number", "129", "Vertices along X (2 to 257; nx - 1 cells)."),
        new("nz", "number", "129", "Vertices along Z (2 to 257)."),
        new("cell_size", "number", "2", "Metres between vertices (0.125 to 64)."),
        new("name", "string", "\"Terrain\"", "The script name."),
        new("height_min", "number", "0", "Metres above the origin that the lowest sample maps to."),
        new("height_range", "number", "64", "Metres the height samples span (0.01 to 16384)."),
        new("layers", "list of strings", "{\"Rck_default.tga\"}", "Texture layers 1 to 8, base first."),
        new("chunk_cells", "number", "32", "Cells per chunk edge: 16, 32, 64 or 128 (capped at 64, or 32 with skirts or geoable, and at 256 chunks)."),
        new("geoable", "boolean", "false", "Closed solid chunks that geomod can carve (needs RF2-style geomod in the Alpine level properties)."),
        new("thickness", "number", "16", "Depth of a geoable terrain's solid below height_min (0.5 to 4096)."),
        new("skirts", "boolean", "false", "Walls hanging below the outer edges and hole rims (non-geoable terrains)."),
        new("skirt_depth", "number", "8", "How far skirts hang, metres."),
        new("fullbright", "boolean", "false", "Drawn at full brightness with no baked light."));

    public static readonly ScriptOptionSet TerrainSetHeights = new("terrain:set_heights",
        new ScriptOption("fit", "boolean", "true", "Refit the height range to exactly the new heights (finest steps); false only widens the range as needed, as Alpine RED's sculpting does."));

    public static readonly ScriptOptionSet TerrainGenerate = new("terrain:generate",
        new("seed", "number", "1", "The random seed; the same settings always give the same heights."),
        new("type", "string", "\"hybrid\"", "\"fbm\" (smooth hills), \"ridged\" (sharp ridges), \"billow\" (rounded hills) or \"hybrid\" (ridged peaks over fBm lowlands)."),
        new("feature_size", "number", "128", "Metres of the largest octave (bigger = broader hills)."),
        new("octaves", "number", "6", "Noise octaves summed (1 to 12)."),
        new("roughness", "number", "0.45", "Amplitude kept per octave (0 to 1)."),
        new("lacunarity", "number", "2", "Frequency gained per octave (1 to 4)."),
        new("warp", "number", "0.25", "Domain warp, in feature sizes (0 to 2)."),
        new("exponent", "number", "1", "Above 1 flattens valleys and sharpens peaks."),
        new("terraces", "number", "0", "Soft terrace steps (0 = none)."),
        new("falloff", "string", "\"none\"", "\"none\", \"island\" (edges sink to the lowest height) or \"basin\" (edges rise to the highest)."),
        new("falloff_width", "number", "0.3", "Share of the half extent that blends to the edge (0.01 to 1)."),
        new("offset", "number", "0", "Added before shaping (-1 to 1); clamping makes flat floors or plateaus."),
        new("smooth", "number", "1", "3 x 3 smoothing passes (0 to 16)."),
        new("height_range", "number", "the current range", "Metres the generated heights span."),
        new("height_min", "number", "the current minimum", "Metres above the origin of the lowest generated point."));

    public static readonly ScriptOptionSet TerrainResize = new("terrain:resize",
        new("nx", "number", "the current count", "Vertices along X (2 to 257)."),
        new("nz", "number", "the current count", "Vertices along Z (2 to 257)."),
        new("weight_resolution", "number", "the current value", "Paint texels per cell edge: 1, 2 or 4."));

    public static readonly ScriptOptionSet TerrainSculpt = new("terrain:sculpt",
        new("tool", "string", "\"raise\" (terrain:paint: \"paint_layer\")", "Alpine RED's Terrain Tools mode: \"raise\", \"lower\", \"smooth_heights\", \"flatten\", \"set_height\", \"noise\", \"ramp\", \"bridge_points\", \"paint_layer\", \"erase_to_base\", \"smooth_weights\", \"paint_holes\", \"clear_holes\" or \"geoable_chunks\"."),
        new("at", "vec", null, "One dab at world {x, z} (or {x, y, z}; y is ignored)."),
        new("path", "list of vecs", null, "A stroke along world points {{x, z}, ...}: one dab per spacing step, as a drag does. Bridge Points uses the first and last point."),
        new("radius", "number", "8", "Brush radius, metres (0.1 to 4096)."),
        new("strength", "number", "0.5", "0.01 to 1: the blend per dab (raise / lower: strength x radius / 20 metres per dab; noise: up to strength x radius / 4)."),
        new("falloff", "string", "\"smooth\"", "\"smooth\", \"linear\" or \"constant\"."),
        new("spacing", "number", "0.25", "Dab spacing along a path as a share of the radius (0.05 to 2)."),
        new("height", "number", "0", "Set Height's target, world Y."),
        new("angle", "number", "15", "Ramp slope in degrees along the path (negative goes down)."),
        new("layer", "number", "1", "paint_layer / erase / smooth: the base layer (1-based)."),
        new("overlay", "number", null, "Paint an overlay's coverage instead (1-based)."),
        new("decoration", "number", null, "Paint a decoration's coverage instead (1-based)."),
        new("mirror_x", "boolean", "false", "Mirror the stroke across a line along Z (at the centre plus mirror_x_offset cells)."),
        new("mirror_z", "boolean", "false", "Mirror the stroke across a line along X."),
        new("mirror_x_offset", "number", "0", "The X mirror line's offset from the terrain's centre, cells (half cells allowed)."),
        new("mirror_z_offset", "number", "0", "The Z mirror line's offset from the terrain's centre, cells."),
        new("hold", "number", "0", "Extra dabs held at the last point (Raise, Lower, Smooth, Flatten, Set Height and Ramp keep dabbing when held still)."),
        new("seed", "number", "1", "Noise pattern (Alpine RED draws a new one per stroke; scripts choose)."));

    public static readonly ScriptOptionSet TerrainDecoration = new("terrain:set_decoration",
        new("index", "number", "a new decoration", "Which decoration to change (1-based); omit to add one."),
        new("mesh", "string", null, "The mesh (.v3m / .v3c, 31 characters at most)."),
        new("density", "number", "0.5", "Instances per square metre at full coverage (0 to 16)."),
        new("scale_min", "number", "1", "Smallest instance scale (0.01 to 16)."),
        new("scale_max", "number", "1", "Largest instance scale."),
        new("max_slope", "number", "35", "Steepest ground, degrees (0 to 90)."),
        new("draw_distance", "number", "80", "Metres the game draws them to (1 to 1024)."),
        new("vertical_offset", "number", "0", "Mesh units along the instance's up axis (-16 to 16)."),
        new("link_layer", "number", "none", "A base layer (1-based) whose weight scales the coverage; 0 or omitted for none."),
        new("align_to_slope", "boolean", "false", "Stand instances on the surface normal."),
        new("random_yaw", "boolean", "true", "Turn each instance at random."),
        new("casts_shadows", "boolean", "false", "Bake their shadows into the terrain (Alpine Faction; Glacier's bake leaves them out)."),
        new("dither_fade", "boolean", "false", "Dither out over the end of the draw distance (Direct3D 11)."),
        new("coverage", "number", "unchanged (a new one: full when linked, else empty)", "Paint the decoration's coverage everywhere: 0 none to 255 full."));

    public static readonly ScriptOptionSet TerrainToBrushes = new("terrain:to_brushes",
        new ScriptOption("keep", "boolean", "false", "Keep the terrain after converting it (by default it is deleted, a destructive operation)."));
}
