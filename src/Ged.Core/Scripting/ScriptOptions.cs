using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Ged.Core.Scripting;

/// <summary>One documented key of a table argument: its Lua name, type, default and meaning.</summary>
/// <param name="Name">The key as written in Lua (snake_case).</param>
/// <param name="Type">The Lua type(s) accepted, e.g. <c>"number"</c>, <c>"vec"</c>, <c>"boolean"</c>.</param>
/// <param name="Default">What an omitted key means, written for a reader (null = required or no default).</param>
/// <param name="Doc">What the key does, units and ranges included.</param>
public sealed record ScriptOption(string Name, string Type, string? Default, string Doc);

/// <summary>
/// The keys one table-argument function accepts. A set is declared once (in <see cref="ScriptOptionSets"/>)
/// and that single declaration drives everything: the function rejects any other key with
/// <see cref="ScriptArgs.RejectUnknown(ScriptOptionSet)"/>, and <c>ged.describe</c>, the generated
/// reference and the Lua stub list the same keys with their types, defaults and meanings — so the
/// documentation cannot drift from what the function accepts.
/// </summary>
public sealed class ScriptOptionSet
{
    public ScriptOptionSet(string api, params ScriptOption[] options)
    {
        Api = api;
        Options = options ?? Array.Empty<ScriptOption>();
        Keys = Options.Select(o => o.Name).ToArray();
    }

    /// <summary>The function the options belong to, as errors name it (e.g. <c>level.brush</c>).</summary>
    public string Api { get; }

    /// <summary>The options in documentation order.</summary>
    public IReadOnlyList<ScriptOption> Options { get; }

    /// <summary>The accepted keys, in documentation order.</summary>
    public string[] Keys { get; }
}

/// <summary>
/// Marks a table-argument facade method with the option set(s) its table accepts, by field name in
/// <see cref="ScriptOptionSets"/>. Two names document a table with two alternative forms (a rotation's
/// <c>{yaw=, pitch=, roll=}</c> or <c>{forward=, up=}</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScriptOptionsAttribute : Attribute
{
    public ScriptOptionsAttribute(params string[] sets) => Sets = sets ?? Array.Empty<string>();

    /// <summary>The <see cref="ScriptOptionSets"/> field names.</summary>
    public IReadOnlyList<string> Sets { get; }
}

/// <summary>Every documented table-argument option set of the scripting API.</summary>
public static partial class ScriptOptionSets
{
    private const string Rotation = "rotation | {yaw=, pitch=, roll=} | {forward=, up=}";
    private const string UidList = "list of handles or UIDs";

    public static readonly ScriptOptionSet Brush = new("level.brush",
        new("shape", "string", "\"box\"", "box, cylinder, cone, sphere, wedge or quad."),
        new("pos", "vec", "{0, 0, 0}", "The brush centre in world metres."),
        new("size", "number | vec", "{4, 4, 4}", "Full extents {w, h, d} in metres, each > 0.01 and <= 4096; a number makes a cube; a quad takes {w, h}."),
        new("rot", Rotation, "no rotation", "Orientation about the centre, in degrees."),
        new("sides", "integer", "12", "Segments around a cylinder, cone or sphere (3 to 64)."),
        new("stacks", "integer", "1 (sphere 6)", "Segments along a cylinder or sphere's height (1 to 64; a sphere needs at least 2)."),
        new("splits", "vec", "{0, 0, 0}", "Extra subdivisions of a box or quad per axis (0 to 32 each)."),
        new("air", "boolean", "false", "true carves space out of the solid world; false adds solid."),
        new("portal", "boolean", "false", "A portal brush (always air): applied after every other brush, it opens the solid it covers and the walls it opens become portals that split rooms for visibility (none in space already open)."),
        new("detail", "boolean", "false", "A detail brush (does not split rooms; can be breakable or geoable)."),
        new("geoable", "boolean", "false", "Geomod can destroy it (implies detail)."),
        new("emits_steam", "boolean", "false", "Faces emit steam."),
        new("life", "integer", "-1", "Hit points: -1 is infinite; finite life on a detail brush makes it breakable."),
        new("texture", "string", "the editor's per-role defaults", "One texture for every face (give texture or textures, not both)."),
        new("textures", "{floor=, wall=, ceiling=}", "the editor's per-role defaults", "Textures by player-relative role (floor = the surface you stand on, for air brushes too); omitted roles use the defaults."),
        new("name", "string", "none", "A name for the brush, written as its kit.name tag (as every kit function names its brushes), so level.brushes_tagged(\"kit.name\", name) and the kit's by-name lookups find it."),
        new("tags", "{key = value}", "none", "Provenance tags written on the new brush."));

    private const string HullTextureDefault = "the editor's per-role defaults";

    public static readonly ScriptOptionSet BrushFromPoints = new("level.brush_from_points",
        new("points", "vec[]", null, "Required: 4 to 256 world points in metres; the brush is their convex hull. Points inside the hull are ignored."),
        new("air", "boolean", "false", "true carves space out of the solid world; false adds solid."),
        new("detail", "boolean", "false", "A detail brush (never seals or splits rooms)."),
        new("geoable", "boolean", "false", "Geomod can destroy it (implies detail, as level.brush)."),
        new("emits_steam", "boolean", "false", "Faces emit steam."),
        new("life", "integer", "-1", "Hit points: -1 is infinite; finite life on a detail brush makes it breakable (as level.brush)."),
        new("texture", "string", HullTextureDefault, "One texture for every face (give texture or textures, not both)."),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, "Textures by player-relative role (floor = the surface you stand on, for air brushes too); faces steeper than 45° are walls."),
        new("tile", "number", "4", "Metres per texture repeat of the world box map (0.25 to 64)."),
        new("snap", "number", "1/1024", "The grid the points are snapped to: a power of two from 1/1024 to 1 metre."),
        new("strict", "boolean", "false", "true: error when a point is not a corner of the hull (inside it, a repeat after snapping, or removed as a sliver)."),
        new("name", "string", "none", "A name for the brush, written as its kit.name tag (as every kit function names its brushes), so level.brushes_tagged(\"kit.name\", name) and the kit's by-name lookups find it."),
        new("tags", "{key = value}", "none", "Provenance tags written on the new brush."));

    public static readonly ScriptOptionSet BrushesFromPoints = new("level.brushes_from_points",
        new("hulls", "vec[][]", null, "Required: 1 to 2000 point lists (4 to 256 world points each, metres); one convex brush per list, in list order."),
        new("air", "boolean", "false", "true carves space out of the solid world; false adds solid (every brush)."),
        new("detail", "boolean", "false", "Detail brushes (never seal or split rooms)."),
        new("geoable", "boolean", "false", "Geomod can destroy them (implies detail, as level.brush)."),
        new("emits_steam", "boolean", "false", "Faces emit steam."),
        new("life", "integer", "-1", "Hit points: -1 is infinite; finite life on a detail brush makes it breakable (as level.brush)."),
        new("texture", "string", HullTextureDefault, "One texture for every face (give texture or textures, not both)."),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, "Textures by player-relative role (floor = the surface you stand on, for air brushes too); faces steeper than 45° are walls."),
        new("tile", "number", "4", "Metres per texture repeat of the world box map (0.25 to 64)."),
        new("snap", "number", "1/1024", "The grid the points are snapped to: a power of two from 1/1024 to 1 metre."),
        new("strict", "boolean", "false", "true: error when a point is not a corner of its hull (inside it, a repeat after snapping, or removed as a sliver)."),
        new("name", "string", "none", "A name for every brush, written as their kit.name tag (as level.brush)."),
        new("tags", "{key = value}", "none", "Provenance tags written on every new brush."));

    private const string GeoBrushes = "brush | list | brush_query";
    private const string GeoBrushesDoc = "Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.";
    private const string GeoMode = "\"hull\" (default): world vertices are moved, snapped to 1/1024 m and rebuilt as a convex hull (needs a convex brush; UVs are re-box-mapped). \"triangulate\": the editor's deformer, then bent faces are triangulated (keeps UVs; may leave a non-convex brush).";
    private const string GeoTile = "Metres per texture repeat of the world box map given to rebuilt faces (0.25 to 64).";
    private const string GeoAxis = "\"x\", \"y\" or \"z\": a world axis through the brush's bounds centre (mode = \"triangulate\": the brush's own axis).";

    public static readonly ScriptOptionSet GeoJitter = new("geo.jitter",
        new("brushes", GeoBrushes, null, GeoBrushesDoc),
        new("amount", "number | vec", null, "Required: the largest move per axis in metres (0 to 16); a vec sets each axis — {0.3, 0, 0.3} keeps every vertex at its height, so flat floors stay flat."),
        new("seed", "integer", "1", "The random stream; brush k of the (time-ordered) list draws from stream (seed, k), so the result does not depend on UIDs."),
        new("mode", "string", "\"hull\"", GeoMode),
        new("tile", "number", "4", GeoTile));

    public static readonly ScriptOptionSet GeoTwist = new("geo.twist",
        new("brushes", GeoBrushes, null, GeoBrushesDoc),
        new("axis", "string", "\"y\"", "The twist axis: " + GeoAxis),
        new("degrees", "number", null, "Required: the rotation at the high end of the axis (−360 to 360); 0 at the low end."),
        new("pieces", "integer", "1", "Slabs along the axis (1 to 32), each rebuilt convex; neighbours meet on identical flat faces. Hull mode only."),
        new("mode", "string", "\"hull\"", GeoMode),
        new("tile", "number", "4", GeoTile));

    public static readonly ScriptOptionSet GeoBend = new("geo.bend",
        new("brushes", GeoBrushes, null, GeoBrushesDoc),
        new("axis", "string", "\"z\"", "The length axis that wraps into the arc: " + GeoAxis),
        new("toward", "string", "\"y\"", "The axis the ends curl toward (not the length axis)."),
        new("degrees", "number", null, "Required: the total arc (−180 to 180, not 0); negative curls the other way."),
        new("pieces", "integer", "max(2, ceil(|degrees| / 15))", "Slabs along the length axis (1 to 32), each rebuilt convex; neighbours overlap by a small lap. Hull mode only."),
        new("mode", "string", "\"hull\"", GeoMode),
        new("tile", "number", "4", GeoTile));

    public static readonly ScriptOptionSet GeoTaper = new("geo.taper",
        new("brushes", GeoBrushes, null, GeoBrushesDoc),
        new("axis", "string", "\"y\"", "The taper axis: " + GeoAxis),
        new("scale", "number", null, "Required: the cross-section scale at the high end of the axis (0.05 to 20); 1 at the low end."),
        new("pieces", "integer", "1", "Slabs along the axis (1 to 32), each rebuilt convex; neighbours meet on identical flat faces. Hull mode only."),
        new("mode", "string", "\"hull\"", GeoMode),
        new("tile", "number", "4", GeoTile));

    public static readonly ScriptOptionSet GeoExtrude = new("geo.extrude",
        new("faces", "face | face[]", null, "Required: the faces to extrude (several faces of one brush are extruded in descending face index)."),
        new("distance", "number", null, "Required: metres along each face's outward normal (0.001 to 4096 in size; negative pushes the face in)."),
        new("tile", "number", "4", "Metres per texture repeat of the world box map on the new side walls (0.25 to 64)."));

    public static readonly ScriptOptionSet GeoBevel = new("geo.bevel",
        new("faces", "face | face[]", null, "Required: the faces to bevel."),
        new("amount", "number", null, "Required: how far each corner moves toward the face centroid, as a fraction (0.01 to 0.99)."),
        new("tile", "number", "4", "Metres per texture repeat of the world box map on the new ring faces (0.25 to 64)."));

    public static readonly ScriptOptionSet GeoWeld = new("geo.weld",
        new("brushes", GeoBrushes, null, GeoBrushesDoc),
        new("tolerance", "number", "0.001", "Vertices closer than this on every axis merge (metres, 0.00001 to 1); faces that collapse are removed."));

    public static readonly ScriptOptionSet GeoSnap = new("geo.snap",
        new("brushes", GeoBrushes, null, GeoBrushesDoc),
        new("grid", "number", "1/1024", "The world grid every vertex snaps to: a power of two from 1/1024 to 16 metres."),
        new("mode", "string", "\"hull\"", "\"hull\" (default): snap, then rebuild the convex hull (needs a convex brush). \"triangulate\": the editor's Snap to Grid, then bent faces are triangulated."),
        new("tile", "number", "4", GeoTile));

    private const string BuilderTexture = "One texture for every face (give texture or textures, not both).";
    private const string BuilderTextures = "Textures by player-relative role (floor = the surface you stand on); faces steeper than 45° are walls.";
    private const string BuilderTile = "Metres per texture repeat of the world box map (0.25 to 64).";
    private const string BuilderTags = "Provenance tags written on every brush.";
    private const string BuilderPart = "When given, every brush is tagged kit.part = part followed by its 1-based number.";
    private const string BuilderSeed = "The random stream (an integer); the same options and seed give the same brushes. Every brush is tagged geo.seed.";
    private const string LimitClear = "The walkway width the floor must hold along the path, metres (0 skips the check).";
    private const string LimitHeadroom = "The clear height over that walkway, metres (0 skips the check).";

    public static readonly ScriptOptionSet GeoTube = new("geo.tube",
        new("path", "vec[]", null, "Required: 2 to 256 floor points (x, z snap to 1/1024 m; y rounds to 1/16 m); legs at least 2 m apart, turning at most 90° per point, climbing at most max_slope (the floor slopes smoothly along each leg). Each leg's end moves by up to half a grid step per segment so its stations are exact, and later points move with it (a centimetre or two on very long paths); read stations for the points used."),
        new("profile", "string", "\"cave\"", "\"cave\" (flat floor under a rough arch) or \"canyon\" (flat floor, leaning walls, one common horizontal top)."),
        new("radius", "number", "3 (cave) / 6 (canyon)", "Half-width in metres (give one of radius, radii, width, widths)."),
        new("radii", "number[]", "none", "Half-width per path point, interpolated along the path."),
        new("width", "number", "6 (cave) / 12 (canyon)", "Full width in metres (2 × radius); a canyon's floor width."),
        new("widths", "number[]", "none", "Full width per path point, interpolated along the path."),
        new("height", "number", "4.5 (cave) / 16 (canyon)", "Cave: clear height at the crown. Canyon: rim height above the highest floor."),
        new("heights", "number[]", "none", "Cave only: clear height per path point."),
        new("roughness", "number", "0.2", "Wall jitter as a fraction of the size (0.05 to 0.5)."),
        new("floor_flatness", "number", "0.6", "Cave only: floor half-width as a fraction of the radius (0.3 to 1)."),
        new("lean", "number", "0.15", "Canyon only: how far the walls lean out per metre of height (0 to 0.5)."),
        new("sides", "integer", "8", "Cave only: points per ring (6 to 14)."),
        new("segments", "integer", "from spacing and the climb", "Segments per leg (give segments or spacing)."),
        new("spacing", "number", "max(3, radius) (cave) / width / 2 (canyon)", "Target distance between stations, metres."),
        new("seed", "integer", "1", BuilderSeed),
        new("min_clear_width", "number", "1.8", LimitClear),
        new("min_headroom", "number", "2", LimitHeadroom),
        new("max_slope", "number", "1", "The steepest average rise/run of a leg (1 = 45°)."),
        new("texture", "string", HullTextureDefault, BuilderTexture),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, BuilderTextures),
        new("tile", "number", "4", BuilderTile),
        new("tags", "{key = value}", "none", BuilderTags),
        new("part", "string", "none", BuilderPart + " Zero-padded to 3 digits (seg001)."));

    public static readonly ScriptOptionSet GeoPassage = new("geo.passage",
        new("path", "vec[]", null, "Required: 2 to 256 floor points at one height (after rounding to 1/16 m), turning at most 120° per point."),
        new("width", "number", "4", "Clear width in metres; corners are mitred so the width holds on every heading."),
        new("height", "number", "4", "Clear height in metres (rounded to 1/16 m)."),
        new("detail", "boolean", "false", "Make the air brushes detail."),
        new("min_clear_width", "number", "1.8", "The smallest width allowed, metres (0 allows any)."),
        new("min_headroom", "number", "2", "The smallest height allowed, metres (0 allows any)."),
        new("texture", "string", HullTextureDefault, BuilderTexture),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, BuilderTextures),
        new("tile", "number", "4", BuilderTile),
        new("tags", "{key = value}", "none", BuilderTags),
        new("part", "string", "none", BuilderPart + " Not padded (seg1)."));

    public static readonly ScriptOptionSet GeoRockRow = new("geo.rock_row",
        new("from", "vec", null, "Required: the floor point the row starts at (y rounds to 1/16 m)."),
        new("to", "vec", null, "Required: the floor point the row ends at."),
        new("height", "number", null, "Required: visible height above the floor, metres (0.1 to 256)."),
        new("depth", "number", "2", "Thickness across the line, metres (0.25 to 256)."),
        new("spacing", "number", "0.8 × depth", "Distance between rock centres, metres; each rock is 1.5 × spacing long, so neighbours overlap."),
        new("flat_top", "boolean", "false", "Clip every rock at one 1/16 m-aligned height: a flat, walkable top."),
        new("structural", "boolean", "false", "A structural ridge instead of detail rocks: one chain of convex pieces meeting on identical faces, standing exactly on the floor (seals and splits rooms; audited against the level)."),
        new("roughness", "number", "0.2", "Jitter as a fraction of the rock size (0.05 to 0.5)."),
        new("points", "integer", "12", "Samples per rock (8 to 24)."),
        new("seed", "integer", "1", BuilderSeed),
        new("texture", "string", HullTextureDefault, BuilderTexture),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, BuilderTextures),
        new("tile", "number", "4", BuilderTile),
        new("tags", "{key = value}", "none", BuilderTags),
        new("part", "string", "none", BuilderPart + " Zero-padded to 3 digits (rock001)."));

    public static readonly ScriptOptionSet GeoScatter = new("geo.scatter",
        new("area", "{min=, max=}", null, "A floor rectangle (its floor is min.y); give area or along."),
        new("along", "tube", null, "A tube (geo.tube / geo.passage record): rocks go on its floor, clear of its walkway."),
        new("count", "integer", "10", "How many rocks (1 to 500); an error names how many fit when not all do."),
        new("size", "number | {min, max}", "{0.6, 2}", "Rock diameter in metres, or the range it is drawn from."),
        new("margin", "number", "0.5 (area) / 0.25 (along)", "Area: how far every rock stays inside the rectangle's edges. Along: how far a rock's centre stays inside the floor's edge (rocks may lean into the wall foot), metres."),
        new("gap", "number", "0.25", "Extra clearance between neighbouring rocks, metres."),
        new("band", "number", "none", "Along only: keep each rock's centre within this many metres of the floor's edge (rubble at the wall foot, the open middle left clear); none = anywhere outside the walkway."),
        new("roughness", "number", "0.2", "Jitter as a fraction of the rock size (0.05 to 0.5)."),
        new("points", "integer", "12", "Samples per rock (8 to 24)."),
        new("seed", "integer", "1", BuilderSeed),
        new("texture", "string", HullTextureDefault, BuilderTexture),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, BuilderTextures),
        new("tile", "number", "4", BuilderTile),
        new("tags", "{key = value}", "none", BuilderTags),
        new("part", "string", "none", BuilderPart + " Zero-padded to 3 digits (rock001)."));

    public static readonly ScriptOptionSet GeoPit = new("geo.pit",
        new("pos", "vec", null, "Required: a point on a level floor where the pit opens (y rounds to 1/16 m; the pit's mouth lies exactly in that floor's plane)."),
        new("radius", "number", "4", "Radius at the top, metres (0.5 to 512)."),
        new("depth", "number", "4", "Depth of the flat floor below pos, metres (rounded to 1/16, 1 to 1024)."),
        new("sides", "integer", "8", "Points per ring (6 to 16)."),
        new("taper", "number", "0.7", "Bottom radius as a fraction of the top radius (0.2 to 2)."),
        new("roughness", "number", "0.2", "Radial jitter as a fraction of the radius (0.05 to 0.5)."),
        new("seed", "integer", "1", BuilderSeed),
        new("texture", "string", HullTextureDefault, BuilderTexture),
        new("textures", "{floor=, wall=, ceiling=}", HullTextureDefault, BuilderTextures),
        new("tile", "number", "4", BuilderTile),
        new("tags", "{key = value}", "none", BuilderTags),
        new("part", "string", "none", BuilderPart + " Not padded (ring1)."));

    public static readonly ScriptOptionSet ImportMesh = new("level.import_mesh",
        new("path", "string", null, "Required: a .obj, .gltf, .glb, .fbx or .dae file. Relative paths resolve against the level's folder, then <scripts>/import. Only the model file (and an OBJ's .mtl in the same folder) is read."),
        new("as", "string", "\"detail\"", "\"detail\": one detail brush per material group (triangle faces, UVs kept). \"mesh\": an Alpine Mesh object using a .v3m written to <install>/user_maps/meshes (a destructive write)."),
        new("pos", "vec", "{0, 0, 0}", "Where the model's origin lands, metres."),
        new("rot", Rotation, "no rotation", "The brushes' rotation / the mesh object's orientation, in degrees (never baked into the vertices)."),
        new("scale", "number", "1", "Uniform scale applied to the model (0.0001 to 10000; 0.01 converts centimetres)."),
        new("axis", "string", "by format (glTF: \"gltf\", others: \"rf\")", "\"rf\" (+Y up, +Z forward), \"gltf\" (Y up, −Z forward) or \"z_up\" (Blender / 3ds Max)."),
        new("flip_winding", "boolean", "false", "Reverse every triangle (when the model renders inside out)."),
        new("name", "string", "the file name without extension", "The group name (\"detail\") or the mesh object's script name and .v3m file name (\"mesh\")."),
        new("tags", "{key = value}", "none", "Provenance tags written on every brush / the mesh object."));

    public static readonly ScriptOptionSet NormsFor = new("catalog.norms_for",
        new("metric", "string", null, "Required: the metric id (catalog.norm_metrics())."),
        new("gametype", "string", "any", "The level's gametype bucket: \"dm\", \"ctf\", \"koth\", \"pay\", … (none: the reference set, else overall)."),
        new("class", "string", "any", "The level class: \"standard\", \"open_air\" or \"payload\" (the design grader's classes)."),
        new("players", "integer", "any", "The intended player count: picks the player-class bucket like the design grader (up to 8 players S, up to 16 M, more L; corpus levels are classed by spawn count)."));

    public static readonly ScriptOptionSet TextureQuery = new("catalog.textures",
        new("filter", "string", "none", "A substring, or a glob with * and ?, matched case-insensitively against the texture name."),
        new("limit", "integer", "200", "The most results (1 to 5000)."),
        new("stock", "boolean", "either", "true: only textures every player has (a retail packfile contains them); false: only the others."),
        new("origin", "string", "any", "Only textures whose shown file comes from \"stock\", \"alpine\", \"packfile\" (another .vpp) or \"loose\" (a folder)."));

    public static readonly ScriptOptionSet Place = new("level.place",
        new("kind", "string", null, "Required: the object kind (light, trigger, item, clutter, entity, event, respawn, …; see catalog.kinds())."),
        new("class", "string", null, "The class for entity, item, clutter and event (required for those; validated against the game tables when an install is mounted)."),
        new("pos", "vec", null, "Required: the world position in metres."),
        new("rot", Rotation, "the blueprint's orientation", "Orientation in degrees (give rot or yaw, not both); applied after props."),
        new("yaw", "number", "the blueprint's orientation", "Heading in degrees about +Y (0 faces +Z, positive turns toward +X)."),
        new("name", "string", "none", "The script name."),
        new("props", "{field = value}", "the blueprint values", "Field values (keys as listed by catalog.describe(kind [, class])), applied in schema order."),
        new("links", UidList, "none", "Objects (or movers) to link to."),
        new("tags", "{key = value}", "none", "Provenance tags written on the new object."),
        new("seat", "boolean", "false", "Clutter, items and mesh objects: treat pos as the floor point and lift the object so the bottom of its mesh (as rotated) rests on it. Stock clutter meshes have their origin at their centre, so without seat they sink half their height (an Ultor crate about 0.46 m). Needs an RF install for the meshes."));

    public static readonly ScriptOptionSet PlayerStart = new("level.player_start",
        new("pos", "vec", "unchanged (required to create one)", "The Player Start position in metres."),
        new("rot", Rotation, "unchanged", "Orientation in degrees (give rot or yaw, not both)."),
        new("yaw", "number", "unchanged", "Heading in degrees about +Y (0 faces +Z)."));

    public static readonly ScriptOptionSet CreateMover = new("level.create_mover",
        new("name", "string", "\"Mover\"", "The mover's name."),
        new("brushes", UidList, "none", "The member brushes (at least one brush or object is required)."),
        new("objects", UidList, "none", "Member objects that ride the mover (triggers, lights, clutter…)."),
        new("props", "{field = value}", "the editor defaults", "Mover fields (movement_type, is_door, sounds…; see m:fields())."),
        new("tags", "{key = value}", "none", "Provenance tags written on the start keyframe."));

    public static readonly ScriptOptionSet CreateGroup = new("level.create_group",
        new("name", "string", "\"Group\"", "The group's name."),
        new("brushes", UidList, "none", "Member brushes (at least one brush or object is required)."),
        new("objects", UidList, "none", "Member objects."));

    public static readonly ScriptOptionSet Box = new("level.brushes_in",
        new("min", "vec", null, "Required: one corner of the box, metres."),
        new("max", "vec", null, "Required: the opposite corner (the corners may be given in either order)."));

    public static readonly ScriptOptionSet AddKeyframe = new("mover:add_keyframe",
        new("pos", "vec", null, "The keyframe's world position (give pos or offset, exactly one)."),
        new("offset", "vec", null, "The keyframe's position relative to the start keyframe."),
        new("rot", Rotation, "no rotation", "The keyframe's orientation in degrees."),
        new("props", "{field = value}", "RED's keyframe defaults", "Keyframe fields (pause_time, travel_time_to_next, return_travel, name…). A new keyframe starts with RED's defaults: name \"Keyframe\", travel_time_to_next = 1 and return_travel = 1 second, and 0 pause, accel and decel."));

    public static readonly ScriptOptionSet GroupMembers = new("group:add",
        new("brushes", UidList, "none", "Brushes to add (existing members are skipped)."),
        new("objects", UidList, "none", "Objects to add (existing members are skipped)."));

    public static readonly ScriptOptionSet PrefabPlace = new("prefab.place",
        new("name", "string", null, "Required: the prefab's name, or its file as prefab.list() shows it."),
        new("pos", "vec", null, "Required: where the prefab's pivot lands, metres."),
        new("rot", Rotation, "no rotation", "Orientation in degrees (give rot or yaw, not both)."),
        new("yaw", "number", "0", "Heading in degrees about +Y."),
        new("tags", "{key = value}", "none", "Provenance tags written on every placed member."));

    public static readonly ScriptOptionSet Clip = new("brush:clip",
        new("point", "vec", null, "Required: a world point on the clip plane."),
        new("normal", "vec", null, "Required: the plane normal."),
        new("mode", "string", "\"cut\"", "\"cut\" discards the side the normal points toward; \"split\" keeps both pieces."));

    public static readonly ScriptOptionSet RoleTextures = new("brush:set_textures",
        new("floor", "string", "unchanged", "The texture of the surface you stand on (the bottom face of an air brush)."),
        new("wall", "string", "unchanged", "The texture of the side faces."),
        new("ceiling", "string", "unchanged", "The texture of the overhead surface."));

    public static readonly ScriptOptionSet Flags = new("brush_query:set_flags",
        new("air", "boolean", "unchanged", "Air (carves) or solid."),
        new("detail", "boolean", "unchanged", "Detail brush."),
        new("portal", "boolean", "unchanged", "Portal brush. An air portal opens the solid it covers (its opened walls become portals); a solid portal carves nothing and its faces in open space become a portal membrane (logs a note)."),
        new("geoable", "boolean", "unchanged", "Geomod can destroy it (implies detail)."),
        new("emits_steam", "boolean", "unchanged", "Faces emit steam."));

    public static readonly ScriptOptionSet BoxMap = new("brush:box_map",
        new("tile", "number", null, "Metres one texture tile spans on every face, whatever the texture's pixel size (4 matches a newly drawn brush)."),
        new("ppm", "number", "256", "Pixels per metre, using each texture's pixel size (give tile or ppm, not both)."));

    public static readonly ScriptOptionSet EulerRotation = new("rotation",
        new("yaw", "number", "0", "Degrees about +Y: 0 faces +Z, positive turns toward +X."),
        new("pitch", "number", "0", "Degrees; positive tilts the facing direction up."),
        new("roll", "number", "0", "Degrees; positive tilts the right vector up."));

    public static readonly ScriptOptionSet BasisRotation = new("rotation",
        new("forward", "vec", null, "The direction the object faces (required with up)."),
        new("up", "vec", null, "The object's up direction (orthonormalised against forward)."));

    private static readonly Lazy<IReadOnlyDictionary<string, ScriptOptionSet>> ByName = new(() =>
        typeof(ScriptOptionSets).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ScriptOptionSet))
            .ToDictionary(f => f.Name, f => (ScriptOptionSet)f.GetValue(null)!, StringComparer.Ordinal));

    /// <summary>The option set declared under field name <paramref name="name"/>, or null.</summary>
    public static ScriptOptionSet? Find(string name) => ByName.Value.TryGetValue(name, out ScriptOptionSet? set) ? set : null;

    /// <summary>Every declared set by field name.</summary>
    public static IReadOnlyDictionary<string, ScriptOptionSet> All => ByName.Value;

    /// <summary>The option sets a facade method documents through <see cref="ScriptOptionsAttribute"/> (empty when none).</summary>
    public static IReadOnlyList<ScriptOptionSet> Of(MethodInfo method) =>
        method.GetCustomAttribute<ScriptOptionsAttribute>() is { } attr
            ? attr.Sets.Select(n => Find(n) ?? throw new InvalidOperationException($"Unknown option set '{n}' on {method.DeclaringType?.Name}.{method.Name}.")).ToList()
            : Array.Empty<ScriptOptionSet>();
}
