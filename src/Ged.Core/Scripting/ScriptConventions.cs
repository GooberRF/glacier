namespace Ged.Core.Scripting;

/// <summary>
/// The conventions block: one constant served by <c>ged.conventions</c> and embedded verbatim in the
/// generated API reference, so an agent and a human read the same rules. Kept within 40 lines.
/// </summary>
public static class ScriptConventions
{
    /// <summary>The plain-text conventions (≤ 40 lines).</summary>
    public const string Text =
        "Units: metres, degrees, seconds; colours 0-255; texture UVs in tile units (1 = one repeat).\n" +
        "Axes: left-handed. +X right, +Y up, +Z forward.\n" +
        "A brush pos is the centre of its local geometry; size = {w, h, d} full extents along local X, Y, Z.\n" +
        "Rotation is {yaw, pitch, roll} degrees: yaw turns about +Y (0 faces +Z, positive turns toward +X);\n" +
        "  pitch positive tilts the facing direction up; roll positive tilts the right vector up.\n" +
        "Objects face along their Forward axis; a corona aims along its Up axis.\n" +
        "The world starts solid: an air brush carves open space, a solid brush adds geometry.\n" +
        "CSG is strictly time-ordered: later air carves earlier solid, later solid fills earlier air.\n" +
        "  New brushes go at the end of time, so build the air a solid sits in before the solid.\n" +
        "Air brushes that touch merge into one open space; keep at least 1 m of solid between rooms for a wall.\n" +
        "Texture roles are player-relative: floor is the surface you stand on (the bottom face of an air brush,\n" +
        "  the top face of a solid one), ceiling the one above you, walls everything steeper.\n" +
        "Texture scale: a new brush tiles one texture repeat per 4 m; b:box_map() matches it (the kit does), b:box_map(256) maps at RED's 256 px/m.\n" +
        "Outdoors: show_sky faces display a separate sealed air box holding a Sky Room effect; kit.sky_room{open = {room}}.\n" +
        "Portals split the level into rooms for visibility; small levels play fine without them. A portal must\n" +
        "  separate two otherwise unconnected spaces; put one in a doorway that gets a door (kit.doorway{portal = true}).\n" +
        "One script run is one undo step; any error rolls it all back (ged.try(fn, ...) undoes only fn's); dry runs apply nothing.\n" +
        "A playable level is built, lit and saved: ops.holes() must report is_watertight (it checks a sealed\n" +
        "  build; never judge leaks from a preview), then ops.build() (geometry), ops.light() (rebuilds and bakes\n" +
        "  the lightmaps) and ops.save(path).\n" +
        "Links originate only from triggers, events, clutter and nav points; a link to a mover (or one of its\n" +
        "  brushes) is stored as the mover's start keyframe, the UID the game follows.\n" +
        "Generic fields: h:get/set/set_many/fields/props take snake_case keys (the Properties labels);\n" +
        "  enums take option names, colours {r, g, b[, a]}; set_many applies values in schema order.\n" +
        "Fields that apply only in some states (a box trigger's box_*, a directional nav point's rotation) read\n" +
        "  nil until they apply: set the deciding field (shape, directional) first, then those fields (sizes start at 0).\n" +
        "Entity, item and clutter classes come from the game tables (catalog.classes) when an RF install is mounted.\n" +
        "Table arguments are strict: an unknown key is an error that names the nearest valid key. API and kit\n" +
        "  errors point at the line of your script that made the call, with a hint (ged.fail raises one).\n" +
        "Editing members leave the selection alone; use :select() and selection.* to change it\n" +
        "  (exceptions: deleting objects resets the object selection; locking a selected brush deselects it).\n" +
        "A mover keeps its own copy of each member brush; member edits (script or editor) are copied over before\n" +
        "  any build / light / save / package and when the run ends (mover:sync() does it at any point).\n" +
        "Modules: ged.import(name) loads a module shipped with Glacier (\"kit\") or <scripts>/lib/<name>.lua, once\n" +
        "  per run (per session in the console); a module shares the script's sandbox, limits and undo step.\n" +
        "Design kit: local kit = ged.import(\"kit\"). Kit functions are floor-anchored: pos is a point on the floor\n" +
        "  (y = floor height) and size = {w, h, d} the interior clear size; kit openings abut rooms exactly;\n" +
        "  kit brushes are tagged kit / kit.name / kit.part, objects are named, multi-brush parts are grouped.\n" +
        "Discover more: ged.describe(\"brush\") (any type or global name, or a handle; options lists table keys),\n" +
        "  ged.describe(\"kit\") or kit.help(), catalog.kinds(), catalog.describe(kind [, class]) for a class's fields.\n";
}
