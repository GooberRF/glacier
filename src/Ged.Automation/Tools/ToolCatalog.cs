using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Automation.Reference;
using Ged.Core.Scripting;

namespace Ged.Automation.Tools;

/// <summary>
/// The safety class of a tool (MCP-SERVER-SPEC Â§7): what a session must allow before the tool may run.
/// </summary>
public enum ToolSafety
{
    /// <summary>Never changes the level, its undo history, its dirty flag or its compiled geometry.</summary>
    Read,

    /// <summary>Changes the level (one undoable step) or writes a file inside the allowed folders.</summary>
    Mutate,

    /// <summary>Could discard or overwrite work; only with destructive actions allowed when the session was started.</summary>
    Destructive,
}

/// <summary>One tool argument.</summary>
/// <param name="Name">The tool-facing name (snake_case).</param>
/// <param name="Type">JSON Schema type: string | number | integer | boolean | array | object.</param>
/// <param name="Doc">One line: what it is, its unit and default.</param>
/// <param name="Required">True when the tool cannot run without it.</param>
/// <param name="Default">The default (documented in the schema; the command applies it).</param>
/// <param name="Enum">The allowed values of a string argument.</param>
/// <param name="Min">Inclusive minimum of a number / integer.</param>
/// <param name="Max">Inclusive maximum of a number / integer.</param>
/// <param name="CliFlag">The glacier-cli flag carrying the same value (null = positional or none).</param>
/// <param name="ItemsType">The item type of an array argument.</param>
/// <param name="CommandArg">The command argument it maps to (null = the camelCase of <paramref name="Name"/>; "" = handled by the tool mapping).</param>
/// <param name="Items">The exact item count of an array argument.</param>
/// <param name="MaxLength">The longest string accepted.</param>
public sealed record ToolArg(
    string Name,
    string Type,
    string Doc,
    bool Required = false,
    object? Default = null,
    string[]? Enum = null,
    double? Min = null,
    double? Max = null,
    string? CliFlag = null,
    string? ItemsType = null,
    string? CommandArg = null,
    int? Items = null,
    int? MaxLength = null)
{
    /// <summary>The command argument name this tool argument is sent as ("" = mapped by the tool itself).</summary>
    public string CommandName => CommandArg ?? ToolCatalog.Camel(Name);
}

/// <summary>One tool: a name, the automation command it runs, its safety class, documentation and arguments.</summary>
/// <param name="Name">The tool name (unprefixed snake_case).</param>
/// <param name="Command">The <see cref="AutomationCommands"/> command it executes.</param>
/// <param name="Safety">The safety class (Â§7).</param>
/// <param name="Summary">One line, for tables.</param>
/// <param name="Description">When to use it, what it costs, what to do next (â‰¤ 700 characters).</param>
/// <param name="Args">The arguments.</param>
/// <param name="ResultDoc">What the result contains.</param>
/// <param name="ReturnsImage">True when the result carries an image.</param>
/// <param name="LongRunning">True when the tool reports progress (compile stages).</param>
/// <param name="HeadlessOnly">Only offered by a headless session.</param>
/// <param name="AttachOnly">Only offered when attached to the editor.</param>
/// <param name="CliVerb">The glacier-cli verb that runs the same command (null = none).</param>
/// <param name="FixedArgs">Command arguments the tool always sends (e.g. the catalog op or the plan view).</param>
public sealed record ToolSpec(
    string Name,
    string Command,
    ToolSafety Safety,
    string Summary,
    string Description,
    IReadOnlyList<ToolArg> Args,
    string ResultDoc,
    bool ReturnsImage = false,
    bool LongRunning = false,
    bool HeadlessOnly = false,
    bool AttachOnly = false,
    string? CliVerb = null,
    IReadOnlyDictionary<string, string>? FixedArgs = null)
{
    /// <summary>True when the tool can change the level or write files (hidden in a read-only session).</summary>
    public bool Mutates => Safety != ToolSafety.Read;
}

/// <summary>
/// The single source of truth for the tools an AI agent can call (MCP-SERVER-SPEC Â§2, Â§4): the MCP
/// <c>tools/list</c> (names, descriptions, JSON Schemas, annotations), the per-tool request mapping, the
/// CLI option lines of the matching verbs and the agent guide's tool table are all generated from it, and
/// tests hold them in step. Plain C# with no protocol dependency, so the editor bridge and the tests use it too.
/// </summary>
public static class ToolCatalog
{
    /// <summary>The largest image side a render tool returns (the server's <c>--max-image</c> may lower it).</summary>
    public const int MaxImageSide = 1568;

    /// <summary>The default rendered image side.</summary>
    public const int DefaultImageSide = 1024;

    /// <summary>The default size budget of one text result, in characters (about 6k tokens).</summary>
    public const int DefaultResultBudget = 24_000;

    /// <summary>The largest size budget a call may ask for (<c>max_chars</c>).</summary>
    public const int MaxResultBudget = 60_000;

    private static readonly string[] RenderModes = { "lit", "textured", "lightmaps", "rooms", "wireframe", "seethrough", "heights" };
    private static readonly string[] FreshBuild = { "auto", "preview", "sealed", "none" };
    private static readonly string[] SpatialOps = Ged.Core.Spatial.SpatialQueries.Ops.Select(o => o.Op).ToArray();

    /// <summary>Every tool, in presentation order.</summary>
    public static IReadOnlyList<ToolSpec> All { get; } = Build();

    /// <summary>The tool named <paramref name="name"/>, or null.</summary>
    public static ToolSpec? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>snake_case â†’ camelCase (<c>max_changes</c> â†’ <c>maxChanges</c>).</summary>
    public static string Camel(string snake)
    {
        string[] parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? snake : parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static List<ToolSpec> Build()
    {
        var region = new ToolArg("region", "array", "Frame (render) or filter (query) on a world box: minx, miny, minz, maxx, maxy, maxz in metres.",
            ItemsType: "number", Items: 6, CliFlag: "--region");
        ToolArg Size() => new("size", "string", $"Image size WIDTHxHEIGHT in pixels (default {DefaultImageSide}x{DefaultImageSide}, at most {MaxImageSide} per side).",
            CommandArg: string.Empty, CliFlag: "--size");
        ToolArg Mode() => new("mode", "string", "Shading: lit (lightmaps; else textured, then room colours), textured, lightmaps, rooms, wireframe, seethrough, heights.",
            Enum: RenderModes, Default: "lit", CliFlag: "--mode");
        ToolArg Overlays(string dflt, bool icons = false) => new("overlays", "string",
            $"Comma list of grid, scale, objects, labels, links, holes (leak check), legend, heights{(icons ? ", icons (editor object icons; off by default in iso/camera)" : string.Empty)}; or none. Default: {dflt}.", CliFlag: "--overlays");
        ToolArg Fresh() => new("fresh_build", "string",
            "Geometry for the image: auto (a quick preview build on a private copy when the level's geometry is missing or stale), preview, sealed, or none.",
            Enum: FreshBuild, Default: "auto", CommandArg: "build");
        ToolArg Room() => new("room", "integer", "Frame on compiled room N (see level_summary rooms).", Min: 0, CliFlag: "--room");
        ToolArg Uid() => new("uid", "integer", "Frame on one object or brush.", Min: 0, CliFlag: "--uid");
        ToolArg MaxChars() => new("max_chars", "integer",
            $"Result size budget in characters (default {DefaultResultBudget}); longer lists are cut with truncated and a hint.",
            Default: DefaultResultBudget, Min: 2000, Max: MaxResultBudget, CommandArg: string.Empty);

        ToolArg RefsDir() => new("refs_dir", "string", "Reference set folder (MCP default: scratch/references; the CLI needs --refs).", CommandArg: "refs", CliFlag: "--refs");

        ToolArg SpatialFresh() => new("fresh_build", "string", "auto (preview build when stale), preview, sealed, none.",
            Enum: FreshBuild, Default: "auto", CommandArg: "build", CliFlag: "--fresh");

        string globals = string.Join(", ", ScriptApiReference.GlobalNames());
        string conventions = string.Join(" ", ScriptConventions.Text.Split('\n').Take(2)).Trim();

        return new List<ToolSpec>
        {
            new("session_info", "hello", ToolSafety.Read,
                "The session: mode, version, open level, write folders, enabled safety classes.",
                "Start here. Reports how this server runs (headless, or attached to the running editor; fixed at startup), its version, the open level (path, unsaved changes, geometry state, counts) or that none is open, the folders tools may write, which safety classes are enabled (set by how the server was started and, attached, the editor settings; no tool can change them), and whether an RF install is mounted. Instant. Attached, busy means the editor is occupied (its own build): retry shortly. Next: open_level or new_level when no level is open, else level_summary.",
                Array.Empty<ToolArg>(),
                "{mode, version, document{path, name, dirty, geometry, brushes, objects, links, rooms}?, editor{connected, capabilities, destructiveSetting}?, writeRoots[], safety{read, mutate, destructive}, rfInstall, renderAvailable?, norms}"),

            new("open_level", "open", ToolSafety.Mutate,
                "Open a .rfl level file as the session's document.",
                "Opens a level file (.rfl) as the one document every other tool works on; its folder becomes writable. Fails when the current level has unsaved changes unless the session allows destructive actions: save_level first. Fast (seconds for large levels). Next: level_summary, render_plan.",
                new[] { new ToolArg("path", "string", "The level file (.rfl), absolute or relative to the server's working folder.", Required: true) },
                "{path, name, rflVersion, alpine, dirty, brushes, objects, links, geometry, rooms, mode}",
                CliVerb: "open"),

            new("new_level", "new", ToolSafety.Mutate,
                "Start a new level from the File > New template.",
                "Replaces the document with a new level from the File > New template (level properties, a Player Start, no geometry). Same unsaved-changes rule as open_level. With out, saves it there at once (a path inside the allowed folders). Instant. Next: run_lua to build geometry with the design kit.",
                new[] { new ToolArg("out", "string", "Save the new level to this .rfl path (optional).", CliFlag: "--out") },
                "{path?, name, rflVersion, dirty, brushes, objects, links, geometry, rooms}",
                CliVerb: "new"),

            new("save_level", "save", ToolSafety.Mutate,
                "Save the level (Alpine v306) with the editor's save rules.",
                "Saves the open level, to its own file or to out (inside the allowed folders; overwriting another existing file needs destructive actions). Missing or stale geometry is built (sealed, unlit) first because Red Faction cannot load a level without it; lighting is only baked by build with light. Seconds. Do this last, after check_holes and lint.",
                new[] { new ToolArg("out", "string", "Save to this .rfl path instead of the level's own file.", CliFlag: "--out") },
                "{path, rflVersion, saveAs, previousPath?, stamped, notice?, built, warning?}",
                CliVerb: "save"),

            new("undo", "undo", ToolSafety.Mutate,
                "Undo the latest automation run(s).",
                "Takes back the latest steps, only when every one is an automation run (undo label starting \"AI:\" or \"CLI:\"); it refuses rather than undo the designer's own edits, and names the step in the way. Instant. Use after a run_lua that went wrong; redo restores what you undid.",
                new[] { new ToolArg("steps", "integer", "How many steps to undo (default 1).", Default: 1, Min: 1, Max: 20, CliFlag: "--steps") },
                "{undone[], next?}",
                CliVerb: "undo"),

            new("redo", "redo", ToolSafety.Mutate,
                "Redo steps this session undid.",
                "Re-applies steps this session took back with undo, in order; refuses when the next redo step was not undone by this session or anything was done since. Instant.",
                new[] { new ToolArg("steps", "integer", "How many steps to redo (default 1).", Default: 1, Min: 1, Max: 20, CliFlag: "--steps") },
                "{undone[], redone[], next?}",
                CliVerb: "redo"),

            new("checkpoint", "checkpoint", ToolSafety.Read,
                "Remember the level's structure under a name (for diff_since).",
                "Records every object's fields and every brush's position, flags, geometry and textures in memory under a name (no level change, no undo step; 16 kept, the oldest dropped). Fast. Take one before an edit you want to review, then call diff_since.",
                new[] { new ToolArg("name", "string", "The checkpoint name (1-64 characters; reusing a name replaces it).", Required: true, MaxLength: AutomationCommands.MaxCheckpointName) },
                "{name, objects, brushes, replaced, kept[]}",
                CliVerb: "checkpoint"),

            new("diff_since", "diff", ToolSafety.Read,
                "What changed since a checkpoint.",
                "Lists objects and brushes added, removed and changed since a checkpoint, by UID; object changes give each field's before and after value, brush changes name position, rotation, flags, tags, geometry or textures. Fast. Use it to confirm a run_lua changed exactly what you meant.",
                new[]
                {
                    new ToolArg("checkpoint", "string", "The checkpoint name.", Required: true, CommandArg: "since", CliFlag: "--since"),
                    new ToolArg("max_changes", "integer", "List at most this many changes (default 100).", Default: 100, Min: 1, Max: 1000, CliFlag: "--max-changes"),
                    MaxChars(),
                },
                "{since, added[], removed[], changed[{uid, kind, fields[{name, before, after}]}], counts, truncated}",
                CliVerb: "diff"),

            new("run_lua", "run", ToolSafety.Mutate,
                "Run a Lua script against the level as one undo step.",
                $"The way to build and edit: runs Lua against the open level as ONE undo step labelled \"AI: <name>\" (any error rolls it all back). Prefer the design kit (local kit = ged.import(\"kit\")). {conventions} Globals: {globals}. Seconds; limit timeout_s. Use dry_run to preview. Returns changes, created UIDs, log, errors with line:column and a hint. Next: build (sealed), check_holes, render_plan.",
                new[]
                {
                    new ToolArg("code", "string", "The Lua source (give code or file).", CommandArg: "script"),
                    new ToolArg("file", "string", "A Lua script file to run instead of code, inside the folders this server may write (session_info writeRoots); ged.import(\"name\") then also finds name.lua beside it (that folder only, literal names).", CommandArg: string.Empty, MaxLength: 1024),
                    new ToolArg("name", "string", "Script name for diagnostics, tags and undo label (default mcp-<n>.lua).", MaxLength: 128),
                    new ToolArg("label", "string", "Short note appended to the undo label.", MaxLength: AutomationCommands.MaxRunLabel, CliFlag: "--label"),
                    new ToolArg("dry_run", "boolean", "Report what would change, then roll it all back.", Default: false, CliFlag: "--dry-run"),
                    new ToolArg("build", "string", "After a successful run: none, sealed (compile) or lit (compile and bake lighting).", Enum: new[] { "none", "sealed", "lit" }, Default: "none", CommandArg: string.Empty),
                    new ToolArg("timeout_s", "number", "Script time limit in seconds; builds do not count.", Default: 30, Min: 1, Max: 300, CommandArg: "timeoutSeconds", CliFlag: "--timeout"),
                    new ToolArg("budget", "integer", "Max Lua instructions (default 200000000; 0 = none).", CommandArg: "instructionBudget", CliFlag: "--budget"),
                },
                "{script, success, dryRun, committed, undoLabel?, changes{}, changeCount, returnValue?, created[], tags?, error?{kind, message, line, column, hint}, log[], logTruncated?, build?}",
                LongRunning: true,
                CliVerb: "run"),

            new("level_summary", "summary", ToolSafety.Read,
                "Structured level summary: bounds, brushes, objects, links, rooms, lint counts.",
                "Facts about the open level: bounds, brush counts by type, objects by kind with positions and key properties (normal, full), links, spawns, rooms with portal neighbours, tag counts and lint totals. brief is a few hundred tokens; full can be large and falls back to normal when over budget. Fast. Use for facts; render_plan for layout.",
                new[] { new ToolArg("detail", "string", "brief, normal (default) or full.", Enum: new[] { "brief", "normal", "full" }, Default: "normal", CliFlag: "--detail"), MaxChars() },
                "{level, bounds, brushes, objects, links, rooms, lint, ...}",
                CliVerb: "summary"),

            new("inspect", "inspect", ToolSafety.Read,
                "Everything about one object or brush.",
                "One object or brush by UID: kind, class, name, position, rotation, every field with type and value (the same fields Lua obj:fields() lists, set with obj:set), links out and in, tags; for a brush its flags, faces, size, bounds and textures. Instant. Find UIDs with query or level_summary.",
                new[] { new ToolArg("uid", "integer", "The UID.", Required: true, Min: 0) },
                "{uid, kind, class?, name?, pos, rot?, fields[{name, type, value, readOnly?, doc?}], links{out[], in[]}, tags{}, brush?{flags[], faces, size, bounds, textures[]}}",
                CliVerb: "inspect"),

            new("query", "query", ToolSafety.Read,
                "Find objects or brushes by kind, class, name, tag or region.",
                "Lists matching UIDs (sorted, paged) with kind, class, name and position. Objects filter by kind, class and name (globs with * and ?), tag (key or key=value) and region; brushes by tag and region. Instant. Page with offset; inspect a UID for its fields.",
                new[]
                {
                    new ToolArg("what", "string", "objects (default) or brushes.", Enum: new[] { "objects", "brushes" }, Default: "objects", CliFlag: "--what"),
                    new ToolArg("kind", "string", "Object kind, e.g. Light, Item, Event, respawn (see list_classes).", CliFlag: "--kind"),
                    new ToolArg("class", "string", "Class name or glob, e.g. \"Medikit*\".", CliFlag: "--class"),
                    new ToolArg("name", "string", "Script name or glob.", CliFlag: "--name"),
                    new ToolArg("tag", "string", "A tag key, or key=value (e.g. ai.session).", CliFlag: "--tag"),
                    region,
                    new ToolArg("limit", "integer", "Page size (default 50).", Default: AutomationCommands.QueryDefaultLimit, Min: 1, Max: AutomationCommands.QueryMaxLimit, CliFlag: "--limit"),
                    new ToolArg("offset", "integer", "Skip this many matches (default 0).", Default: 0, Min: 0, CliFlag: "--offset"),
                    MaxChars(),
                },
                "{what, total, offset, returned, truncated, hint?, items[{uid, kind, class?, name?, pos, flags?}]}",
                CliVerb: "query"),

            new("render_plan", "render", ToolSafety.Read,
                "Top-down floor plan of one storey (image + pixel-to-world mapping).",
                "Draws a floor plan of one storey: walls, solid hatching, grid, scale, labelled object glyphs, links, legend. Image right is +X, image up is +Z. Returns the PNG and the mapping (worldPerPixel, origin) to turn pixels into metres. Renders a private copy; the level never changes. A few seconds. Render every storey (y) after each change.",
                new[]
                {
                    new ToolArg("y", "number", "Floor height of the storey in metres (default: under the Player Start).", CliFlag: "--y"),
                    new ToolArg("thickness", "number", "Keep geometry up to y + thickness (default 2).", Min: 0.1, Max: 1000, CliFlag: "--thickness"),
                    new ToolArg("below", "number", "Keep geometry down to y - below (default 1).", Min: 0, Max: 1000, CliFlag: "--below"),
                    region, Room(), Uid(), Overlays("grid,scale,objects,labels,links,legend"), Mode(), Size(), Fresh(),
                },
                "image/png + {width, height, view, worldPerPixel, origin, pixelAxes, mapping, plan, framing, legend, objects[], geometry, notes[], path}",
                ReturnsImage: true,
                CliVerb: "render",
                FixedArgs: new Dictionary<string, string> { ["view"] = "plan" }),

            new("render_view", "render", ToolSafety.Read,
                "Orthographic, isometric or free-camera view (image + mapping).",
                "Draws the level from a side (top, bottom, front, back, left, right), in iso perspective, or from a camera point looking at a target (view camera with camera and look). Returns the PNG plus the projection to map pixels to the world. Renders a private copy; the level never changes. A few seconds. Use to check heights, stairs and how rooms connect.",
                new[]
                {
                    new ToolArg("view", "string", "top, bottom, front, back, left, right, iso, or camera.", Required: true,
                        Enum: new[] { "top", "bottom", "front", "back", "left", "right", "iso", "camera" }, CliFlag: "--view"),
                    new ToolArg("camera", "array", "view camera: eye position x, y, z.", ItemsType: "number", Items: 3, CliFlag: "--camera"),
                    new ToolArg("look", "array", "view camera: target point x, y, z.", ItemsType: "number", Items: 3, CliFlag: "--look"),
                    new ToolArg("fov", "number", "iso / camera: vertical field of view in degrees (default 60).", Min: 10, Max: 120, CliFlag: "--fov"),
                    new ToolArg("hfov", "number", "iso / camera: horizontal fov instead (RF: 90).", Min: 10, Max: 170, CliFlag: "--hfov"),
                    new ToolArg("eye_height", "number", "camera: eye m above the floor below it.", Min: 0.05, Max: 200, CliFlag: "--eye-height"),
                    region, Room(), Uid(), Overlays("none", icons: true), Mode(), Size(), Fresh(),
                },
                "image/png + {width, height, view, projection, camera, worldPerPixel, origin?, viewProjection, framing, legend, objects[], geometry, notes[], path}",
                ReturnsImage: true,
                CliVerb: "render"),

            new("reference_view", "reference", ToolSafety.Read,
                "Reference pictures: a concept image or a stored reference with a grid, or the list of references.",
                "Reference matching, step 1. image: the concept with a grid in its pixels (crop zooms on and outlines a rectangle; snap shows it snapped onto the panel frame): read a screenshot's crop for reference_edit add. id: the stored reference with a grid in fractions (0-1 from the top-left) and its marks: read the horizon, vanishing point and object positions for reference_edit camera. Neither: the list of references with their cameras and marks. Known sizes for reading scale: glacier://reference/scale. Instant.",
                new[]
                {
                    new ToolArg("image", "string", "A concept image (png, jpg).", CliFlag: "--image"),
                    new ToolArg("id", "string", "A stored reference.", MaxLength: 64, CliFlag: "--id"),
                    new ToolArg("crop", "array", "image: x, y, w, h pixels.", ItemsType: "number", Items: 4, CliFlag: "--crop"),
                    new ToolArg("snap", "boolean", "image + crop: show the snapped crop.", CliFlag: "--snap"),
                    new ToolArg("size", "integer", "Longest side, px.", Min: 200, Max: 4096, CliFlag: "--side"),
                    new ToolArg("step", "integer", "image: fine grid step, px.", Min: 1, Max: 5000, CliFlag: "--step"),
                    new ToolArg("zoom", "number", "image: crop zoom.", Min: 0.25, Max: 32, CliFlag: "--zoom"),
                    RefsDir(),
                },
                "image/png + {op, kind, path, width, height, crop?, snapped?, reference?, read} | {op: list, count, references[]}",
                ReturnsImage: true,
                CliVerb: "ref",
                FixedArgs: new Dictionary<string, string> { ["op"] = "view" }),

            new("reference_edit", "reference", ToolSafety.Mutate,
                "Register a reference crop (add) or set its matched camera and marks (camera).",
                "add: store the image's crop (x, y, w, h px; snap moves it onto the panel frame) as reference id, with its area and notes. camera: set the camera reference_compare renders from: camera + look, or heading / pitch; move (forward, right, up m) and turn (heading, pitch deg) nudge it; hfov (default 90); eye_height (m above the floor under it). Marks in picture fractions give an estimate: horizon = pitch and roll, vp + vp_dir = heading, object (x, base, top, metres) = eye height, measure (x, base, top...) = heights in m; apply_estimate applies it; ignore (x, y, w, h) masks a weapon or caption. Writes only the reference set. Instant.",
                new[]
                {
                    new ToolArg("op", "string", "add or camera.", Required: true, Enum: new[] { "add", "camera" }),
                    new ToolArg("id", "string", "The reference id (letters, digits, - _).", Required: true, MaxLength: 64, CliFlag: "--id"),
                    new ToolArg("image", "string", "add: the concept image.", CliFlag: "--image"),
                    new ToolArg("crop", "array", "add: x, y, w, h pixels.", ItemsType: "number", Items: 4, CliFlag: "--crop"),
                    new ToolArg("snap", "boolean", "add: snap the crop onto the frame.", CliFlag: "--snap"),
                    new ToolArg("area", "string", "add: the area it shows.", MaxLength: 200, CliFlag: "--area"),
                    new ToolArg("notes", "string", "add: what matters in it.", MaxLength: 2000, CliFlag: "--notes"),
                    new ToolArg("replace", "boolean", "add: overwrite the id (keeps camera, marks).", CliFlag: "--replace"),
                    new ToolArg("camera", "array", "Eye x, y, z.", ItemsType: "number", Items: 3, CliFlag: "--camera"),
                    new ToolArg("look", "array", "Look-at x, y, z.", ItemsType: "number", Items: 3, CliFlag: "--look"),
                    new ToolArg("heading", "number", "Degrees, 0 = +Z, 90 = +X.", Min: -360, Max: 360, CliFlag: "--heading"),
                    new ToolArg("pitch", "number", "Degrees, up positive.", Min: -89, Max: 89, CliFlag: "--pitch"),
                    new ToolArg("move", "array", "Nudge forward, right, up m.", ItemsType: "number", Items: 3, CliFlag: "--move"),
                    new ToolArg("turn", "array", "Nudge heading, pitch deg.", ItemsType: "number", Items: 2, CliFlag: "--turn"),
                    new ToolArg("hfov", "number", "Horizontal fov deg (90).", Min: 10, Max: 170, CliFlag: "--hfov"),
                    new ToolArg("eye_height", "number", "Eye m above floor; 0 clears.", Min: 0, Max: 200, CliFlag: "--eye-height"),
                    new ToolArg("horizon", "array", "[y] or [x1, y1, x2, y2].", ItemsType: "number", CliFlag: "--horizon"),
                    new ToolArg("vp", "array", "Vanishing point x, y.", ItemsType: "number", Items: 2, CliFlag: "--vp"),
                    new ToolArg("vp_dir", "array", "World x, z of the vp lines.", ItemsType: "number", Items: 2, CliFlag: "--vp-dir"),
                    new ToolArg("object", "array", "x, base, top, metres.", ItemsType: "number", Items: 4, CliFlag: "--object"),
                    new ToolArg("measure", "array", "x, base, top triples.", ItemsType: "number", CliFlag: "--measure"),
                    new ToolArg("ignore", "array", "x, y, w, h rectangles.", ItemsType: "number", CliFlag: "--ignore"),
                    new ToolArg("clear_marks", "boolean", "Drop stored marks first.", CliFlag: "--clear-marks"),
                    new ToolArg("apply_estimate", "boolean", "Put the estimate on the camera.", CliFlag: "--apply-estimate"),
                    RefsDir(),
                },
                "{op, refs, reference | id, camera{position, look, heading, pitch, hfov, vfov, eyeHeight?}, marks?, estimate{pitchDeg, rollDeg, headingDeg, eyeHeight, objectDistance, measurements[]}?, renderFlags, next, notes[]}",
                CliVerb: "ref"),

            new("reference_compare", "reference", ToolSafety.Read,
                "Render the level from a reference's camera beside the reference: one comparison sheet plus cues.",
                "Reference matching, the loop: renders a private copy of the level from the reference's camera at its aspect and fov and returns one sheet: reference | render, blend | edge overlay (orange ref, cyan render, white both; skylines), one fraction grid, both horizons. Also cues (detail, plain top, skyline, brightness, warmth, horizons, edge overlap) and hints: hints, not a score. Align the camera first, then write a difference list (massing, height, openings, structure, detail, materials), fix, compare again. Camera arguments here are one-off. Seconds.",
                new[]
                {
                    new ToolArg("id", "string", "The reference.", Required: true, MaxLength: 64, CliFlag: "--id"),
                    new ToolArg("camera", "array", "Eye x, y, z.", ItemsType: "number", Items: 3, CliFlag: "--camera"),
                    new ToolArg("look", "array", "Look-at x, y, z.", ItemsType: "number", Items: 3, CliFlag: "--look"),
                    new ToolArg("move", "array", "Nudge forward, right, up m.", ItemsType: "number", Items: 3, CliFlag: "--move"),
                    new ToolArg("turn", "array", "Nudge heading, pitch deg.", ItemsType: "number", Items: 2, CliFlag: "--turn"),
                    new ToolArg("hfov", "number", "Horizontal fov deg.", Min: 10, Max: 170, CliFlag: "--hfov"),
                    new ToolArg("eye_height", "number", "Eye m above floor.", Min: 0, Max: 200, CliFlag: "--eye-height"),
                    new ToolArg("panel", "integer", "Picture width px (640, fitted to the cap).", Min: ReferenceCommand.MinPanel, Max: ReferenceCommand.MaxPanel, CliFlag: "--panel"),
                    Mode(),
                    RefsDir(),
                },
                "image/png + {op, id, area, path, width, height, render, panel, camera{position, look, heading, pitch, hfov, floor?}, cues{reference, render, edgeOverlapReference, edgeOverlapRender, hints[]}, geometry, notes[]}",
                ReturnsImage: true,
                CliVerb: "ref",
                FixedArgs: new Dictionary<string, string> { ["op"] = "compare" }),

            new("build", "build", ToolSafety.Mutate,
                "Compile the level's geometry (sealed by default), optionally bake lighting.",
                "Compiles like the editor's Build and applies the result (brushes unchanged). Sealed by default: never measure holes on a preview. light also bakes lightmaps. Seconds to minutes; reports progress. Next: check_holes, then lint.",
                new[]
                {
                    new ToolArg("sealed", "boolean", "Full sealed build (default true); false = the fast unsealed preview.", Default: true, CliFlag: "--sealed"),
                    new ToolArg("light", "boolean", "Also bake lighting (always sealed).", Default: false, CliFlag: "--light"),
                    new ToolArg("shadows", "boolean", "Cast shadows while baking (default true).", Default: true, CliFlag: "--no-shadows"),
                    new ToolArg("pipeline", "string", "Store the lightmap pipeline first: alpine (stock + Alpine D3D11 charts, default) or legacy (stock only). Omit to keep it.", Enum: new[] { "alpine", "legacy" }, CliFlag: "--pipeline"),
                    new ToolArg("density", "integer", "Store the Alpine lightmap density first: 0 = default (8 px/m), 1-128, 255 = charts off. Omit to keep it.", Min: 0, Max: 255, CliFlag: "--density"),
                    new ToolArg("codec", "string", "Alpine lightmap codec for this bake: bc7 (default, as Alpine ships) or raw (uncompressed RGB8). Not stored in the level.", Enum: new[] { "bc7", "raw" }, CliFlag: "--codec"),
                },
                "{sealed, lit, method, brushes, rooms, subrooms, portals, faces, vertices, surfaces, lightmapPages, elapsedMs, hasErrors, repair?{holesAfterRooms, portalPlanesUnified, portalCopiesMoved, splitRoomsJoined, verticesWelded, edgesSplit, collapsedFacesRemoved, portalStripsRemoved, portalGapsFilled, wallGapsFilled, unplacedPortalFacesRemoved}, lightmaps?{pipeline, density, requestedDensity, halvings, pages, charts, tiles, directionalLights, skipped?, codec, compression, movers, moverCharts, liquidEmitters, sectionBytes}, messages[{severity, text, pos?, brushUid?}]}",
                LongRunning: true,
                CliVerb: "build"),

            new("check_holes", "holes", ToolSafety.Read,
                "Leak check on a sealed build (positions of open edges).",
                "Compiles a sealed build privately (nothing is applied) and lists where the level leaks: the midpoint of each open edge. Watertight is required before handing a level back. Seconds to minutes; reports progress. With leaks: render_plan near the positions (overlays with holes), fix with run_lua, check again.",
                Array.Empty<ToolArg>(),
                "{count, watertight, positions[[x, y, z]] (first 100), truncated}",
                LongRunning: true,
                CliVerb: "holes"),

            new("lint", "lint", ToolSafety.Read,
                "Level linter: links, spawns, budgets, leaks, missing assets.",
                "Runs the editor's linter on the open level: broken links, missing spawns, budget overruns, leaks, missing assets, each with a severity, message, UID and position. Errors come first; up to 100 findings are listed; info findings of category outOfNorm are corpus-norms design hints, never failures. Fast (skips the leak and norms checks on a preview build). Fix errors before handing back.",
                new[] { new ToolArg("min_severity", "string", "List only findings at or above: info (default), warning, error. Counts stay totals.", Enum: new[] { "info", "warning", "error" }, Default: "info", CliFlag: "--min-severity"), MaxChars() },
                "{errors, warnings, info, findings[{severity, category, message, uid?, pos?, blocksSave}], notes[]}",
                CliVerb: "lint"),

            new("norms", "norms", ToolSafety.Read,
                "Corpus norms: typical level proportions (doorways, corridors, headroom, light density).",
                "How released Red Faction levels are proportioned, as percentiles (p5-p95; one vote per level). Without metric: every metric with unit, reliability and whether it drives the kit's defaults. With metric (e.g. opening.width, lights.per_100m2): its numbers per bucket (exemplar = curated reference levels, overall, dm, ctf, sp, retail, ...); gametype narrows to one bucket. Instant; needs no level. For proportions, never geometry to copy.",
                new[]
                {
                    new ToolArg("metric", "string", "A metric id, e.g. opening.width (omit to list metrics).", CliFlag: "--metric"),
                    new ToolArg("gametype", "string", "One bucket of the metric: exemplar, overall, dm, ctf, sp, exemplar.dm, retail, ...", CommandArg: "bucket", CliFlag: "--gametype"),
                },
                "{generated, referenceBucket, corpus{}, metrics[{id, unit, reliability, use, doc, buckets[]}] | metric{id, unit, reliability, use, doc, reference, buckets{name: {n, p5, p25, p50, p75, p95}}}}",
                CliVerb: "norms"),

            new("compare_to_norms", "norms", ToolSafety.Read,
                "Compare the open level with the corpus norms (below / within / above).",
                "Measures the open level from its compiled geometry (build sealed first after edits) and compares every metric that drives the kit's defaults with the reference range: the level's value, the bucket's p5-p95 and p50, and below / within / above. Out-of-range metrics are design hints, not errors. Seconds. Use while reviewing proportions; lint reports the same hints as info findings.",
                Array.Empty<ToolArg>(),
                "{referenceBucket, gametype?, status, comparisons[{metric, value, unit, bucket, p5, p25, p50, p75, p95, verdict, message?}], outOfNorm}",
                CliVerb: "norms",
                FixedArgs: new Dictionary<string, string> { ["compare"] = "true" }),

            new("spatial_query", "spatial", ToolSafety.Read,
                "Spatial queries and design measurements.",
                "Instant: raycast(p, dir), los(p, q), point_info(p), clearance(p). Measured, cached: walk(p?), routes(p, q, k), graph(kind, scale), sightlines, cover, cells, area_report(region | p + radius), spawn_report, item_report, locations(kind), setpieces, purpose. Results carry geometry freshness.",
                new[]
                {
                    new ToolArg("op", "string", "The query.", Required: true, Enum: SpatialOps),
                    new ToolArg("p", "array", "First point.", ItemsType: "number", Items: 3),
                    new ToolArg("q", "array", "Second point.", ItemsType: "number", Items: 3),
                    new ToolArg("dir", "array", "raycast direction.", ItemsType: "number", Items: 3, CliFlag: "--dir"),
                    new ToolArg("region", "array", "Box: min xyz, max xyz.", ItemsType: "number", Items: 6, CliFlag: "--region"),
                    new ToolArg("radius", "number", "With p: a circle (m).", CliFlag: "--radius"),
                    new ToolArg("k", "integer", "routes wanted (3).", Min: 1, Max: 8, CliFlag: "--k"),
                    new ToolArg("kind", "string", "graph kind; locations kind.", CliFlag: "--kind"),
                    new ToolArg("scale", "integer", "graph cell: 8 or 4.", Min: 4, Max: 8, CliFlag: "--scale"),
                    SpatialFresh(),
                },
                "{op result, geometry{state, measured, note}, ...}",
                CliVerb: "spatial"),

            new("align_textures", "run", ToolSafety.Mutate,
                "Line textures up like a mapper (level.uv_align), or report UV problems.",
                "One undo step: walls on the floor line, wrapped round corners, never mirrored; slopes and rock at true length; girder and trim textures run along beams, posts and pilasters; fitted faces kept. report = true only measures (level.uv_report). Then build; grade_level G21 checks it.",
                new[]
                {
                    new ToolArg("tag", "string", "Only brushes with this tag (key or key=value).", MaxLength: 128, CommandArg: string.Empty),
                    new ToolArg("report", "boolean", "Measure only; change nothing.", Default: false, CommandArg: string.Empty),
                },
                "{success, committed, returnValue}",
                CliVerb: "run"),

            new("terrain", "run", ToolSafety.Mutate,
                "Create, generate, edit, sculpt, query or convert a terrain.",
                "One undo step. create: grid at center. generate: seeded hills. edit: resize, layers, holes, heightmap. sculpt: points = stroke, layers = mode[:radius:strength:layer]. query: facts, lint, lighting, y at points. to_brushes (keep = false: destructive).",
                new[]
                {
                    new ToolArg("op", "string", "Action.", Required: true, Enum: new[] { "create", "generate", "edit", "sculpt", "query", "to_brushes" }, CommandArg: string.Empty),
                    new ToolArg("terrain", "string", "UID or name.", MaxLength: 64, CommandArg: string.Empty),
                    new ToolArg("center", "array", "Centre (create).", ItemsType: "number", Items: 3, CommandArg: string.Empty),
                    new ToolArg("nx", "integer", "Vertices X, 2-257.", CommandArg: string.Empty),
                    new ToolArg("nz", "integer", "Vertices Z.", CommandArg: string.Empty),
                    new ToolArg("layers", "string", "Textures, ';' list.", CommandArg: string.Empty),
                    new ToolArg("seed", "integer", "Noise seed.", Min: 0, CommandArg: string.Empty),
                    new ToolArg("type", "string", "Noise.", Enum: new[] { "fbm", "ridged", "billow", "hybrid" }, CommandArg: string.Empty),
                    new ToolArg("height_range", "number", "Height span, m.", CommandArg: string.Empty),
                    new ToolArg("holes", "array", "Hole cells x, z, ...", ItemsType: "number", CommandArg: string.Empty),
                    new ToolArg("heightmap", "string", "PNG / RAW16 file.", CommandArg: string.Empty),
                    new ToolArg("points", "array", "x, z, ...", ItemsType: "number", CommandArg: string.Empty),
                    new ToolArg("keep", "boolean", "Keep it.", Default: false, CommandArg: string.Empty),
                },
                "{success, committed, returnValue}",
                CliVerb: "run"),

            new("grade_level", "grade", ToolSafety.Read,
                "Design grader: verdict, fixes and locations.",
                "Self-review checks; each failure gives values, a kit fix and locations to act on. PASS <= 2 failures; FAIL >= 5 or a hard check. Loop: generate, grade, fix by location, regrade. Seconds.",
                new[]
                {
                    new ToolArg("gametype", "string", "Default: detected.", CliFlag: "--gametype"),
                    new ToolArg("players", "integer", "Player count.", Min: 1, Max: 64, CliFlag: "--players"),
                    new ToolArg("class", "string", "Default: detected.", Enum: new[] { "standard", "open_air", "payload" }, CliFlag: "--class"),
                    SpatialFresh(),
                },
                "{verdict, failures[{id, check, values, fix, locations[]}], passed[], advisories[], set_pieces{}, class, geometry}",
                CliVerb: "grade"),

            new("list_classes", "catalog", ToolSafety.Read,
                "Class names of a placeable kind (items, entities, clutter, events).",
                "The exact class names a kind accepts in level.place{}: Item, Entity and Clutter from the game tables (needs the RF install), Event from the event catalog; other kinds have none. Never guess a class name: list it. Instant. Next: describe_class for its fields.",
                new[]
                {
                    new ToolArg("kind", "string", "A placeable kind, e.g. Item, Entity, Clutter, Event, Light, respawn.", Required: true, CliFlag: "--kind"),
                    new ToolArg("filter", "string", "Substring or glob to narrow the list.", CliFlag: "--filter"),
                },
                "{op, kind, tablesLoaded, total, returned, truncated, classes[], note?}",
                CliVerb: "catalog",
                FixedArgs: new Dictionary<string, string> { ["op"] = "classes" }),

            new("describe_class", "catalog", ToolSafety.Read,
                "Fields, links and description of a kind or class.",
                "Describes a kind, or one class of it: category, description, whether it has an orientation, which kinds an event links to, and every field you can set (name, type, options, range). Instant. Use before placing or editing an object you have not used yet.",
                new[]
                {
                    new ToolArg("kind", "string", "A placeable kind, e.g. Event, Item, Light.", Required: true, CliFlag: "--kind"),
                    new ToolArg("class", "string", "A class of the kind (omit to describe the kind).", CliFlag: "--class"),
                },
                "{op, kind, class?, category?, description?, alpine, hasRotation, linkTargets[], fields[{name, type, options?, min?, max?, readOnly?, doc?}]}",
                CliVerb: "catalog",
                FixedArgs: new Dictionary<string, string> { ["op"] = "describe" }),

            new("list_textures", "catalog", ToolSafety.Read,
                "Texture names in the mounted RF install, with provenance.",
                "Lists texture names you can apply (sorted), each with whether it is a stock game texture and where it comes from. Needs the RF install. Instant. Filter with a substring or glob (e.g. \"*rock*\"); prefer stock textures for levels others will play.",
                new[]
                {
                    new ToolArg("filter", "string", "Substring or glob with * and ?.", CliFlag: "--filter"),
                    new ToolArg("limit", "integer", "At most this many (default 50).", Default: 50, Min: 1, Max: 5000, CliFlag: "--limit"),
                    new ToolArg("stock_only", "boolean", "Only stock game textures.", Default: false, CliFlag: "--stock-only"),
                },
                "{op, available, returned, limit, textures[{name, stock, origin?, overridden?}], note?, hint?}",
                CliVerb: "catalog",
                FixedArgs: new Dictionary<string, string> { ["op"] = "textures" }),

            new("module_library", "modules", ToolSafety.Read,
                "Detail/architecture module library: stats, best matches, or one module.",
                "Modules are recurring detail pieces harvested from released levels (pilaster, trims, frames, beam, railing, catwalk, stairs, pipe, machinery, rock...) with an anchor frame, stretch axes, repeating units, styles and a score. Without id: stats and the best matches. With id: the full entry. Needs no level; a missing library answers available=false.",
                new[]
                {
                    new ToolArg("id", "string", "A module id: its full entry."),
                    new ToolArg("category", "string", "e.g. pilaster, railing, door_frame.", CliFlag: "--category"),
                    new ToolArg("attach", "string", "wall, floor, ceiling, span, edge, opening, ...", CliFlag: "--attach"),
                    new ToolArg("style", "string", "e.g. industrial, bunker, cave.", CliFlag: "--style"),
                    new ToolArg("limit", "integer", "Max listed (50).", Min: 1, Max: ModuleLibraryCommands.MaxLimit, CliFlag: "--limit"),
                    new ToolArg("include_rejected", "boolean", "Also curation-rejected.", Default: false, CliFlag: "--include-rejected"),
                    new ToolArg("library", "string", "A library/modules folder.", CliFlag: "--modules"),
                },
                "{available, reason?, library, stats{}, total, modules[] | module{}}",
                CliVerb: "modules"),

            new("harvest_modules", "harvest", ToolSafety.Mutate,
                "Harvest modules from levels (read-only) into a library folder.",
                "Reads library/sources.json's levels, or one level/packfile (source + group), read-only, and writes the library (index.json + one .rfg per module) to out, inside the allowed folders and outside every source folder. Deterministic. Seconds per level. Next: module_library.",
                new[]
                {
                    new ToolArg("source", "string", "One .rfl or .vpp instead of the manifest."),
                    new ToolArg("group", "string", "Group name (single source) or group to harvest.", CliFlag: "--group"),
                    new ToolArg("out", "string", "The library/modules folder to write.", CliFlag: "--out"),
                },
                "{levels_read, levels_failed, modules, rejected{}, by_category_group, output}",
                CliVerb: "modules"),

            new("api_reference", "api", ToolSafety.Read,
                "The Lua scripting reference: one global, type or member, or the index.",
                "The scripting reference generated from this build. Without member: an index of every global, type and design-kit function, one line each. With member (level, brush, level.brush, brush:set_pos, kit.room): its signature, options, defaults and example. Instant. Look a member up before using it in run_lua.",
                new[] { new ToolArg("member", "string", "A global, type or member, e.g. kit.room or level.place.", CliFlag: "--member") },
                "{name, apiVersion, text, truncated}",
                CliVerb: "api"),
        };
    }
}
