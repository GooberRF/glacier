using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>level</c> global: enumeration + lookup + placement over the open
/// <see cref="EditorDocument"/>. Placement routes through the document's undoable
/// <c>PlaceObject</c>/<c>PlaceEvent</c>; deletion collapses into one undo entry.
/// </summary>
public sealed partial class ScriptLevel
{
    private readonly ScriptContext _ctx;

    internal ScriptLevel(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>level.count</c> — number of level objects.</summary>
    [ScriptDoc("The number of level objects (lights, triggers, events, keyframes, the Player Start…), brushes not included.", Example = "log.info(level.count .. \" objects\")")]
    public int Count => _ctx.Document.Objects.Count;

    /// <summary>Lua: <c>level.brush_count</c> — number of brushes.</summary>
    [ScriptDoc("The number of brushes in the level.", Example = "log.info(level.brush_count .. \" brushes\")")]
    public int BrushCount => _ctx.Brushes.Brushes.Count;

    [ScriptDoc("Counts from the last build: {brushes, objects, built, faces, rooms, surfaces, surface_cap (32767), unlit_faces (lit faces with no lightmap surface), unlit_min/max, surface_budget_ok}.", Example = "local s = level.stats(); log.info(s.surfaces .. ' of ' .. s.surface_cap .. ' lightmap surfaces')", Since = 2)]
    public Dictionary<string, object?> Stats()
    {
        Geometry? g = null;
        foreach (IO.Rfl.RflSection s in _ctx.Document.Rfl.Sections)
        {
            if (s.Content is IO.Rfl.Sections.GeometrySection geo)
            {
                g = geo.Geometry;
                break;
            }
        }

        Compiler.SurfaceBudgetState budget = Compiler.SurfaceBudgetState.FromGeometry(g);
        var o = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["brushes"] = (double)_ctx.Brushes.Brushes.Count,
            ["objects"] = (double)_ctx.Document.Objects.Count,
            ["built"] = g is { Faces.Count: > 0 },
            ["faces"] = (double)(g?.Faces.Count ?? 0),
            ["rooms"] = (double)(g?.Rooms.Count ?? 0),
            ["surfaces"] = (double)(g?.Surfaces.Count ?? 0),
            ["surface_cap"] = (double)Compiler.SurfaceBuilder.MaxLevelSurfaces,
            ["unlit_faces"] = (double)budget.UnlitFaces,
            ["surface_budget_ok"] = !budget.Over,
        };
        if (budget.UnlitMin is { } a && budget.UnlitMax is { } b)
        {
            o["unlit_min"] = a;
            o["unlit_max"] = b;
        }

        return o;
    }

    /// <summary>Lua: <c>level.objects</c> — handles for every level object (iterate with <c>ipairs</c>).</summary>
    [ScriptDoc("Every level object as object[] (iterate with ipairs).", Example = "for _, o in ipairs(level.objects) do log.info(o.kind) end")]
    public ScriptObjectHandle[] Objects =>
        _ctx.Document.Objects.Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    /// <summary>Lua: <c>level.brushes</c> — handles for every brush.</summary>
    [ScriptDoc("Every brush as brush[], in CSG time order.", Example = "for _, b in ipairs(level.brushes) do log.info(b.uid) end")]
    public ScriptBrushHandle[] Brushes =>
        _ctx.Brushes.Brushes.Select(b => new ScriptBrushHandle(_ctx, b.Uid)).ToArray();

    /// <summary>Lua: <c>level.objects_of("Light")</c> — objects of one kind.</summary>
    [ScriptDoc("The objects of one kind as object[]: kind names as o.kind spells them (\"Light\", \"MpRespawnPoint\", \"Keyframe\", …) or as level.place{} accepts them (\"respawn\", \"corona\", …).", Example = "local lights = level.objects_of(\"Light\")")]
    public ScriptObjectHandle[] ObjectsOf(string kind)
    {
        LevelObjectKind k = ParseKind(kind);
        return _ctx.Document.Objects.Where(o => o.Kind == k)
            .Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();
    }

    /// <summary>Lua: <c>level.find_uid(n)</c> — the object with UID <paramref name="uid"/>, or nil.</summary>
    [ScriptDoc("The level object with this UID, or nil (brushes: use find_brush).", Example = "local o = level.find_uid(42)")]
    public ScriptObjectHandle? FindUid(int uid)
    {
        LevelObject? o = _ctx.Document.FindByUid(uid);
        return o is null ? null : new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>Lua: <c>level.object(uid)</c> — the object with UID <paramref name="uid"/>, or nil (as find_uid).</summary>
    [ScriptDoc("The level object with this UID, or nil — the handle for a uid an audit finding, a report or a log names (find_uid under another name; brushes: level.find_brush).", Example = "local o = level.object(42) if o then log.info(o.kind) end", Since = 2)]
    public ScriptObjectHandle? Object(int uid) => FindUid(uid);

    /// <summary>Lua: <c>level.find_brush(uid)</c> — the brush with UID <paramref name="uid"/>, or nil.</summary>
    [ScriptDoc("The brush with this UID, or nil.", Example = "local b = level.find_brush(7)")]
    public ScriptBrushHandle? FindBrush(int uid) =>
        _ctx.Brushes.FindBrush(uid) is null ? null : new ScriptBrushHandle(_ctx, uid);

    /// <summary>Lua: <c>level.place("light", x, y, z [, class_name])</c> — places a new object (undoable).</summary>
    [ScriptDoc("Places a new object of kind at (x, y, z) metres, with an optional class name, and returns it. Prefer level.place{…} for options.", Example = "level.place(\"Light\", 0, 4, 0)")]
    public ScriptObjectHandle Place(string kind, double x, double y, double z, string? className = null)
    {
        LevelObjectKind k = ParseKind(kind, "level.place");
        LevelObject? o = _ctx.Document.PlaceObject(k, new Vec3((float)x, (float)y, (float)z), className, ItemCountFor("level.place", k, className))
            ?? throw new ScriptApiException($"Could not place a '{kind}'.", "This kind may not be placeable via scripts.");
        _ctx.NoteCreated(o.Uid);
        _ctx.Changes.Record("placed");
        return new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>Lua: <c>level.place_event("class", x, y, z)</c> — places a new event (undoable).</summary>
    [ScriptDoc("Places a new event of the given class at (x, y, z) metres and returns it.", Example = "level.place_event(\"Explode\", 0, 1, 0)")]
    public ScriptObjectHandle PlaceEvent(string className, double x, double y, double z)
    {
        EventSchema schema = EventSchemaCatalog.Find(className)
            ?? throw new ScriptApiException($"Unknown event class '{className}'.", "Check the class name against the event catalog.");
        LevelObject? o = _ctx.Document.PlaceEvent(schema, new Vec3((float)x, (float)y, (float)z))
            ?? throw new ScriptApiException($"Could not place event '{className}'.");
        _ctx.NoteCreated(o.Uid);
        _ctx.Changes.Record("placed");
        return new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>
    /// Lua: <c>level.place_box(x, y, z, w, h, d [, texture])</c> — creates + adds a box brush
    /// (undoable). Textures follow the SAME path as the interactive Draw Brush tool (the
    /// white-brush fix): without an explicit <paramref name="texture"/>, the editor's configured
    /// per-orientation defaults are applied, each resolved through the shared
    /// <see cref="Editing.DefaultBrushTexture"/> guard (dead/blank names fall back to the stock
    /// rock default) and registered in the brush's texture table by the factory. An explicit
    /// texture is applied to every face (registered in the table); if it does not resolve in the
    /// mounted library, a warning is logged since it will render as the white fallback until the
    /// texture exists.
    /// </summary>
    [ScriptDoc("Adds a solid box brush centred at (x, y, z), w×h×d metres, at the end of time; texture defaults to the editor's per-orientation defaults. Returns the brush.",
        Example = "level.place_box(0, 1, 0, 4, 2, 4, \"rock02.tga\")")]
    public ScriptBrushHandle PlaceBox(double x, double y, double z, double w, double h, double d, string? texture = null)
    {
        var p = new Editing.BrushCreateParams
        {
            Shape = Editing.BrushShape.Box,
            Width = (float)w,
            Height = (float)h,
            Depth = (float)d,
        };

        if (!string.IsNullOrWhiteSpace(texture))
        {
            // Explicit texture: every orientation uses it (author intent overrides the
            // orientation preferences). The factory registers it in the geometry texture table.
            p.Texture = texture!;
            p.FloorTexture = texture;
            p.WallTexture = texture;
            p.CeilingTexture = texture;
            if (_ctx.Assets is { } vfs && vfs.ResolveTexture(texture!) is null)
            {
                _ctx.Log.Warn($"texture '{texture}' was not found in the mounted library — " +
                              "faces will render white until it exists.");
            }
        }
        else
        {
            // Same unresolvable-name guard as the Draw Brush tool / Brush panel (white-brush fix):
            // the editor's configured defaults, resolved against the mounted VFS with a stock fallback.
            p.FloorTexture = Editing.DefaultBrushTexture.Resolve(_ctx.Assets, _ctx.Services.DefaultFloorTexture);
            p.WallTexture = Editing.DefaultBrushTexture.Resolve(_ctx.Assets, _ctx.Services.DefaultWallTexture);
            p.CeilingTexture = Editing.DefaultBrushTexture.Resolve(_ctx.Assets, _ctx.Services.DefaultCeilingTexture);
        }

        int uid = _ctx.Brushes.CreateBrush(p, new Vec3((float)x, (float)y, (float)z), Mat3.Identity);
        _ctx.NoteCreated(uid);
        _ctx.Changes.Record("placed");
        return new ScriptBrushHandle(_ctx, uid);
    }

    // ---- V2: table-arg brush creation ------------------------------

    private static readonly (string Name, Editing.BrushShape Shape)[] BrushShapes =
    {
        ("box", Editing.BrushShape.Box),
        ("cylinder", Editing.BrushShape.Cylinder),
        ("cone", Editing.BrushShape.Cone),
        ("sphere", Editing.BrushShape.Sphere),
        ("wedge", Editing.BrushShape.Wedge),
        ("quad", Editing.BrushShape.Face),
        ("face", Editing.BrushShape.Face),
    };

    /// <summary>
    /// Lua: <c>level.brush{shape=, pos=, size=, rot=, air=, …}</c> — creates one primitive brush at
    /// the end of time (one "Create brush" undo command) and returns its handle. Its keys are declared in
    /// <see cref="ScriptOptionSets.Brush"/>; unknown keys are rejected with the nearest valid name.
    /// </summary>
    [ScriptDoc("Creates a brush (metres; pos = centre, size = {w,h,d} full extents) at the end of time. Returns the brush. air=true carves, solid adds.",
        Example = "level.brush{shape=\"box\", pos={0,2,0}, size={8,4,8}, air=true}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.Brush))]
    public ScriptBrushHandle Brush(ScriptArgs args)
    {
        const string Api = "level.brush";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.Brush);

        // Shape.
        string shapeName = (args.String(Api, "shape", "box") ?? "box").Trim();
        if (string.Equals(shapeName, "mesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new ScriptApiException($"{Api}: shape 'mesh' is not supported yet.",
                "Use box, cylinder, cone, sphere, wedge or quad.");
        }

        Editing.BrushShape shape = BrushShapes.FirstOrDefault(s => string.Equals(s.Name, shapeName, StringComparison.OrdinalIgnoreCase)).Shape;
        if (!BrushShapes.Any(s => string.Equals(s.Name, shapeName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ScriptApiException($"{Api}: unknown shape '{shapeName}'.",
                ScriptSuggest.Hint(shapeName, BrushShapes.Select(s => s.Name).Where(n => n != "face"), "shape"));
        }

        bool radial = shape is Editing.BrushShape.Cylinder or Editing.BrushShape.Cone or Editing.BrushShape.Sphere;
        bool quad = shape == Editing.BrushShape.Face;

        // Size: a number (cube) or vec-like {w, h, d}; a quad uses w, h only.
        Vec3 size = BrushSize(Api, args, quad);

        var p = new Editing.BrushCreateParams
        {
            Shape = shape,
            Width = size.X,
            Height = size.Y,
            Depth = size.Z,
        };

        // Tessellation.
        if (args.Has("sides"))
        {
            if (!radial)
            {
                throw new ScriptApiException($"{Api}: 'sides' applies only to cylinder, cone and sphere (shape is '{shapeName}').");
            }

            p.WidthSplits = Ranged(Api, "sides", args.Int(Api, "sides", 12), 3, 64);
        }
        else if (radial)
        {
            p.WidthSplits = 12;
        }

        if (args.Has("stacks"))
        {
            if (shape is not (Editing.BrushShape.Cylinder or Editing.BrushShape.Sphere))
            {
                throw new ScriptApiException($"{Api}: 'stacks' applies only to cylinder and sphere (shape is '{shapeName}').");
            }

            int stacks = Ranged(Api, "stacks", args.Int(Api, "stacks", 1), shape == Editing.BrushShape.Sphere ? 2 : 1, 64);
            p.HeightSplits = stacks - 1;
        }
        else if (shape == Editing.BrushShape.Sphere)
        {
            p.HeightSplits = 6 - 1;
        }

        if (args.Has("splits"))
        {
            if (shape is not (Editing.BrushShape.Box or Editing.BrushShape.Face))
            {
                throw new ScriptApiException($"{Api}: 'splits' applies only to box and quad (shape is '{shapeName}').");
            }

            Vec3 s = args.Vec(Api, "splits", Vec3.Zero);
            p.WidthSplits = Ranged(Api, "splits.x", ScriptArgs.ToInt(Api, "splits.x", s.X), 0, 32);
            p.HeightSplits = Ranged(Api, "splits.y", ScriptArgs.ToInt(Api, "splits.y", s.Y), 0, 32);
            p.DepthSplits = Ranged(Api, "splits.z", ScriptArgs.ToInt(Api, "splits.z", s.Z), 0, 32);
        }

        // Flags.
        bool portal = args.Bool(Api, "portal", false);
        if (portal && args.Has("air") && !args.Bool(Api, "air", true))
        {
            throw new ScriptApiException($"{Api}: a portal brush is always air; air = false contradicts portal = true.",
                "Drop 'air' (portal implies air).");
        }

        p.Portal = portal;
        p.Air = portal || args.Bool(Api, "air", false);
        p.Detail = args.Bool(Api, "detail", false);
        p.Geoable = args.Bool(Api, "geoable", false);
        p.EmitsSteam = args.Bool(Api, "emits_steam", false);
        int life = args.Int(Api, "life", -1);
        if (life < -1)
        {
            throw new ScriptApiException($"{Api}: 'life' must be -1 (infinite) or more (got {life}).");
        }

        p.Life = life;

        // Textures by player-relative role.
        ApplyBrushTextures(Api, args, p);

        Vec3 pos = args.Vec(Api, "pos", Vec3.Zero);
        Mat3 rot = args.Has("rot") ? ScriptRotation.FromArgs(Api, "rot", args.Named["rot"]) : Mat3.Identity;
        Dictionary<string, string> tags = ScriptTags.TableWithName(Api, args);
        int uid = _ctx.Brushes.CreateBrush(p, pos, rot);
        _ctx.NoteCreated(uid);
        _ctx.Changes.Record("placed");
        ScriptTags.Merge(_ctx, Api, new[] { uid }, tags);
        return new ScriptBrushHandle(_ctx, uid);
    }

    private static Vec3 BrushSize(string api, ScriptArgs args, bool quad)
    {
        Vec3 size;
        if (!args.Named.TryGetValue("size", out object? raw) || raw is null)
        {
            size = new Vec3(4, 4, 4);
        }
        else if (raw is double cube)
        {
            size = new Vec3((float)cube, (float)cube, (float)cube);
            if (!double.IsFinite(cube))
            {
                throw new ScriptApiException($"{api}: 'size' must be finite.");
            }
        }
        else if (quad && raw is ScriptArgs t2 && t2.Items.Count == 2 && t2.Named.Count == 0)
        {
            size = ScriptArgs.ToVec(api, "size", new ScriptArgs(items: new[] { t2.Items[0], t2.Items[1], (object?)4.0 }));
        }
        else
        {
            size = ScriptArgs.ToVec(api, "size", raw);
        }

        CheckExtent(api, "size.w", size.X);
        CheckExtent(api, "size.h", size.Y);
        if (!quad)
        {
            CheckExtent(api, "size.d", size.Z);
        }

        return size;
    }

    private static void CheckExtent(string api, string what, float v)
    {
        if (!float.IsFinite(v) || v <= 0.01f || v > ScriptBrushHandle.MaxExtent)
        {
            throw new ScriptApiException(
                $"{api}: '{what}' must be greater than 0.01 and at most {ScriptBrushHandle.MaxExtent} metres (got {ScriptArgs.Format(v)}).");
        }
    }

    private static int Ranged(string api, string what, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new ScriptApiException($"{api}: '{what}' must be between {min} and {max} (got {value}).");
        }

        return value;
    }

    /// <summary>
    /// Fills the factory's orientation textures from <c>texture</c> / <c>textures</c>: each role falls
    /// back to <c>texture</c>, then to the editor default for that role. Roles are player-relative,
    /// so for an air brush the floor role goes on the down-facing (bottom) face — the factory assigns
    /// by outward normal, so floor/ceiling are swapped before filling it.
    /// </summary>
    private void ApplyBrushTextures(string api, ScriptArgs args, Editing.BrushCreateParams p)
    {
        (string floor, string wall, string ceiling) = ResolveBrushTextures(api, args, p.Air);
        p.Texture = wall;
        p.FloorTexture = floor;
        p.WallTexture = wall;
        p.CeilingTexture = ceiling;
    }

    /// <summary>
    /// The three role textures from <c>texture</c> / <c>textures</c> (validated, unresolved names warned),
    /// each falling back to <c>texture</c> then to the editor default — returned in the factory's
    /// outward-normal order (up-facing, side, down-facing): for an air brush floor and ceiling are swapped,
    /// because the player stands on an air brush's down-facing face.
    /// </summary>
    private (string UpFacing, string Wall, string DownFacing) ResolveBrushTextures(string api, ScriptArgs args, bool air) =>
        ResolveBrushTextures(_ctx, api, args, air);

    /// <summary><see cref="ResolveBrushTextures(string, ScriptArgs, bool)"/> for any facade (the <c>geo</c> builders share it).</summary>
    internal static (string UpFacing, string Wall, string DownFacing) ResolveBrushTextures(ScriptContext ctx, string api, ScriptArgs args, bool air)
    {
        if (args.Has("texture") && args.Has("textures"))
        {
            throw new ScriptApiException($"{api}: give either 'texture' (every face) or 'textures' (by role), not both.");
        }

        string? all = args.String(api, "texture");
        if (all is not null && all.Trim().Length == 0)
        {
            throw new ScriptApiException($"{api}: 'texture' must be a texture name (got an empty string).");
        }

        string? floor = all, wall = all, ceiling = all;
        if (args.Table(api, "textures") is { } roles)
        {
            roles.RejectUnknown($"{api} textures", ScriptOptionSets.RoleTextures);
            floor = roles.String(api, "floor") ?? floor;
            wall = roles.String(api, "wall") ?? wall;
            ceiling = roles.String(api, "ceiling") ?? ceiling;
        }

        foreach (string name in new[] { floor, wall, ceiling }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Trim().Length == 0)
            {
                throw new ScriptApiException($"{api}: texture names must not be empty.");
            }

            RoleTextures.WarnIfUnresolved(ctx, name);
        }

        floor ??= Editing.DefaultBrushTexture.Resolve(ctx.Assets, ctx.Services.DefaultFloorTexture);
        wall ??= Editing.DefaultBrushTexture.Resolve(ctx.Assets, ctx.Services.DefaultWallTexture);
        ceiling ??= Editing.DefaultBrushTexture.Resolve(ctx.Assets, ctx.Services.DefaultCeilingTexture);
        return air ? (ceiling, wall, floor) : (floor, wall, ceiling);
    }

    // ---- V2: convex-hull brushes ------------------------------------------------

    [ScriptDoc("Creates a convex brush: the hull of 4 to 256 world points (metres, snapped to 1/1024 m), unrotated and box-mapped, at the end of time; air=true carves. Returns the brush.",
        Example = "level.brush_from_points{points = {{0,0,0}, {4,0,0}, {0,0,4}, {4,0,4}, {2,3,2}}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.BrushFromPoints))]
    public ScriptBrushHandle BrushFromPoints(ScriptArgs args)
    {
        const string Api = "level.brush_from_points";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{points = {{…}}}} is required.", "For example level.brush_from_points{points = {{0,0,0}, {4,0,0}, {0,0,4}, {2,3,2}}}.");
        }

        args.RejectUnknown(ScriptOptionSets.BrushFromPoints);
        Editing.Organic.HullBrushParams p = HullParams(Api, args);
        List<Editing.Organic.Vec3d> points = HullPoints(Api, "'points'", null, args.Named.TryGetValue("points", out object? raw) ? raw : null);
        Dictionary<string, string> tags = ScriptTags.TableWithName(Api, args);
        Brush brush = BuildHull(Api, null, points, p);

        brush.Uid = _ctx.Document.AllocateUid();
        _ctx.Brushes.AddBrush(brush, "Create brush");
        _ctx.NoteCreated(brush.Uid);
        _ctx.Changes.Record("placed");
        ScriptTags.Merge(_ctx, Api, new[] { brush.Uid }, tags);
        return new ScriptBrushHandle(_ctx, brush.Uid);
    }

    [ScriptDoc("Creates one convex brush per point list in hulls (1 to 2000 lists; same options as brush_from_points, applied to every brush) as one undo command. Returns a brush_query in list order.",
        Example = "level.brushes_from_points{hulls = {pts1, pts2}, detail = true}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.BrushesFromPoints))]
    public ScriptBrushQuery BrushesFromPoints(ScriptArgs args)
    {
        const string Api = "level.brushes_from_points";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{hulls = {{…}}}} is required.", "For example level.brushes_from_points{hulls = {pts1, pts2}}.");
        }

        args.RejectUnknown(ScriptOptionSets.BrushesFromPoints);
        Editing.Organic.HullBrushParams p = HullParams(Api, args);
        object? rawHulls = args.Named.TryGetValue("hulls", out object? h) ? h : null;
        if (rawHulls is not ScriptArgs hulls || hulls.Named.Count > 0 || hulls.Items.Count < 1 || hulls.Items.Count > Editing.Organic.OrganicLimits.MaxBulkHulls)
        {
            string got = rawHulls is ScriptArgs t && t.Named.Count == 0 ? t.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : rawHulls is null ? "none" : $"a {ScriptArgs.LuaTypeOf(rawHulls)}";
            throw new ScriptApiException($"{Api}: 'hulls' must list 1 to {Editing.Organic.OrganicLimits.MaxBulkHulls} point lists (got {got}).",
                "Each entry is a list of 4 to 256 points, e.g. hulls = {{{0,0,0}, {4,0,0}, {0,0,4}, {2,3,2}}}.");
        }

        Dictionary<string, string> tags = ScriptTags.TableWithName(Api, args);

        // Validate every point list, check the call's hull-building budget, then build every hull before
        // anything is added (all-or-nothing, P6).
        var lists = new List<List<Editing.Organic.Vec3d>>(hulls.Items.Count);
        long work = 0;
        for (int k = 0; k < hulls.Items.Count; k++)
        {
            string prefix = $"hulls[{k + 1}]";
            List<Editing.Organic.Vec3d> points = HullPoints(Api, prefix, prefix, hulls.Items[k]);
            lists.Add(points);
            work += (long)points.Count * points.Count;
        }

        if (work > Editing.Organic.OrganicLimits.MaxHullWork)
        {
            throw new ScriptApiException(
                $"{Api}: the {hulls.Items.Count} hulls hold {work} points² of hull-building work; one call may hold at most {Editing.Organic.OrganicLimits.MaxHullWork} (the sum of each hull's point count squared).",
                "Use fewer points per hull (a hull needs only its corners), or split the hulls over several calls.");
        }

        var brushes = new List<Brush>(hulls.Items.Count);
        for (int k = 0; k < lists.Count; k++)
        {
            _ctx.Cancellation.ThrowIfCancellationRequested();
            brushes.Add(BuildHull(Api, $"hulls[{k + 1}]", lists[k], p));
        }

        foreach (Brush b in brushes)
        {
            b.Uid = _ctx.Document.AllocateUid();
        }

        _ctx.Brushes.AddBrushes(brushes, brushes.Count == 1 ? "Create brush" : $"Create {brushes.Count} brushes");
        var uids = brushes.Select(b => b.Uid).ToList();
        foreach (int uid in uids)
        {
            _ctx.NoteCreated(uid);
            _ctx.Changes.Record("placed");
        }

        ScriptTags.Merge(_ctx, Api, uids, tags);
        return new ScriptBrushQuery(_ctx, uids);
    }

    /// <summary>The flag, texture, tile, snap and strict options shared by the two hull creators.</summary>
    private Editing.Organic.HullBrushParams HullParams(string api, ScriptArgs args)
    {
        var p = new Editing.Organic.HullBrushParams
        {
            Air = args.Bool(api, "air", false),
            Detail = args.Bool(api, "detail", false),
            Geoable = args.Bool(api, "geoable", false),
            EmitsSteam = args.Bool(api, "emits_steam", false),
            Strict = args.Bool(api, "strict", false),
        };

        int life = args.Int(api, "life", -1);
        if (life < -1)
        {
            throw new ScriptApiException($"{api}: 'life' must be -1 (infinite) or more (got {life}).");
        }

        p.Life = life;
        double tile = args.Number(api, "tile", Editing.GeometryUtil.DefaultTileMetres);
        if (Editing.Organic.HullBrush.ValidateTile(tile) is { } badTile)
        {
            throw new ScriptApiException($"{api}: {badTile.Message}", badTile.Hint);
        }

        p.TileMetres = (float)tile;
        double snap = args.Number(api, "snap", Editing.Organic.OrganicLimits.MinSnap);
        if (Editing.Organic.HullBrush.ValidateSnap(snap) is { } badSnap)
        {
            throw new ScriptApiException($"{api}: {badSnap.Message}", badSnap.Hint);
        }

        p.Snap = snap;
        (p.Floor, p.Wall, p.Ceiling) = ResolveBrushTextures(api, args, p.Air);
        return p;
    }

    /// <summary>
    /// Reads a point list: 4 to 256 entries, each a vec, <c>{x, y, z}</c> or <c>{x=, y=, z=}</c> of finite
    /// numbers (read in double precision). <paramref name="what"/> names the list in the count error;
    /// <paramref name="prefix"/> (bulk form) prefixes the per-point errors.
    /// </summary>
    private static List<Editing.Organic.Vec3d> HullPoints(string api, string what, string? prefix, object? raw)
    {
        int min = Editing.Organic.OrganicLimits.MinInputPoints, max = Editing.Organic.OrganicLimits.MaxInputPoints;
        if (raw is not ScriptArgs list || list.Named.Count > 0 || list.Items.Count < min || list.Items.Count > max)
        {
            string got = raw is ScriptArgs t && t.Named.Count == 0 ? t.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : raw is null ? "none" : $"a {ScriptArgs.LuaTypeOf(raw)}";
            throw new ScriptApiException($"{api}: {what} must list {min} to {max} points (got {got}).",
                "Split a larger shape into several convex brushes.");
        }

        string lead = prefix is null ? string.Empty : prefix + ": ";
        var points = new List<Editing.Organic.Vec3d>(list.Items.Count);
        for (int i = 0; i < list.Items.Count; i++)
        {
            Editing.Organic.Vec3d? v = list.Items[i] switch
            {
                Vec3 f when float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z) => new Editing.Organic.Vec3d(f.X, f.Y, f.Z),
                ScriptArgs t when t.Named.Count == 0 && t.Items.Count == 3 => Point(t.Items[0], t.Items[1], t.Items[2]),
                ScriptArgs t when t.Items.Count == 0 && t.Named.Count == 3 && t.Has("x") && t.Has("y") && t.Has("z") => Point(t.Named["x"], t.Named["y"], t.Named["z"]),
                _ => null,
            };
            points.Add(v ?? throw new ScriptApiException($"{api}: {lead}points[{i + 1}] must be a vec, {{x, y, z}} or {{x=, y=, z=}} of finite numbers."));
        }

        return points;

        static Editing.Organic.Vec3d? Point(object? x, object? y, object? z) =>
            x is double a && y is double b && z is double c && double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c)
                ? new Editing.Organic.Vec3d(a, b, c)
                : null;
    }

    /// <summary>Builds one hull brush (UID 0, assigned by the caller once every hull is valid); throws the §1.3 errors.</summary>
    private static Brush BuildHull(string api, string? prefix, List<Editing.Organic.Vec3d> points, Editing.Organic.HullBrushParams p)
    {
        Editing.Organic.HullBrush.TryBuild(points, p, 0, out Brush? brush, out _, out Editing.Organic.HullBrushError? error);
        if (error is not null || brush is null)
        {
            string lead = prefix is null ? string.Empty : prefix + ": ";
            throw new ScriptApiException($"{api}: {lead}{error?.Message ?? "the hull could not be built."}", error?.Hint);
        }

        return brush;
    }

    // ---- V2: mesh import ------------------------------------------------

    /// <summary>The most triangles one material group may bring in as a detail brush (Cairn LC073: the brush report line, ORGANIC-GEOMETRY-SPEC §2.5).</summary>
    internal const int MaxImportTrianglesPerGroup = 5000;

    [ScriptDoc("Imports a .obj/.gltf/.glb/.fbx/.dae model: as detail brushes (default, one per material, UVs kept) or as an Alpine Mesh object with a .v3m. Returns mesh_import.",
        Example = "local m = level.import_mesh{path = \"rock.obj\", pos = {0, 0, 8}, scale = 0.01}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.ImportMesh))]
    public ScriptMeshImport ImportMesh(ScriptArgs args)
    {
        const string Api = "level.import_mesh";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{path = …}} is required.", "For example level.import_mesh{path = \"rock.obj\"}.");
        }

        args.RejectUnknown(ScriptOptionSets.ImportMesh);
        string path = args.String(Api, "path") is { } p && p.Trim().Length > 0
            ? p.Trim()
            : throw new ScriptApiException($"{Api}: 'path' is required.", "The model file, e.g. path = \"rock.obj\" (relative to the level's folder, then <scripts>/import).");
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (!IO.Mesh.Import.MeshImporter.SupportedExtensions.Contains(ext))
        {
            throw new ScriptApiException($"{Api}: '{System.IO.Path.GetFileName(path)}' is not a supported model file.",
                $"Use one of: {string.Join(", ", IO.Mesh.Import.MeshImporter.SupportedExtensions)}.");
        }

        string target = (args.String(Api, "as", "detail") ?? "detail").Trim().ToLowerInvariant();
        if (target is not ("detail" or "mesh"))
        {
            throw new ScriptApiException($"{Api}: unknown 'as' value '{target}'.", ScriptSuggest.Hint(target, new[] { "detail", "mesh" }, "value"));
        }

        Vec3 pos = args.Vec(Api, "pos", Vec3.Zero);
        Mat3 rot = args.Has("rot") ? ScriptRotation.FromArgs(Api, "rot", args.Named["rot"]).Orthonormalize() : Mat3.Identity;
        double scale = args.Number(Api, "scale", 1);
        if (!double.IsFinite(scale) || scale < 0.0001 || scale > 10000)
        {
            throw new ScriptApiException($"{Api}: 'scale' must be 0.0001 to 10000 (got {ScriptArgs.Format(scale)}).",
                $"Nearest valid value: {ScriptArgs.Format(double.IsFinite(scale) ? Math.Clamp(scale, 0.0001, 10000) : 1)}.");
        }

        IO.Mesh.Import.MeshAxisConversion? axis = null;
        if (args.String(Api, "axis") is { } axisName)
        {
            axis = axisName.Trim().ToLowerInvariant() switch
            {
                "rf" => IO.Mesh.Import.MeshAxisConversion.RfNative,
                "gltf" => IO.Mesh.Import.MeshAxisConversion.GltfYUp,
                "z_up" => IO.Mesh.Import.MeshAxisConversion.ZUp,
                _ => throw new ScriptApiException($"{Api}: unknown axis '{axisName}'.", ScriptSuggest.Hint(axisName, new[] { "rf", "gltf", "z_up" }, "axis")),
            };
        }

        bool flip = args.Bool(Api, "flip_winding", false);
        string? name = args.String(Api, "name");
        if (name is not null && name.Trim().Length == 0)
        {
            throw new ScriptApiException($"{Api}: 'name' must not be empty.");
        }

        name ??= System.IO.Path.GetFileNameWithoutExtension(path);
        Dictionary<string, string> tags = ScriptTags.Table(Api, args.Table(Api, "tags"));

        // Resolve, then authorise, then read: nothing is read outside the host's allowed folders.
        Func<string, string> access = _ctx.Services.ImportFileAccess
            ?? throw new ScriptApiException($"{Api}: file import is not available in this host.");
        string full = access(ResolveImportPath(Api, path));
        if (!System.IO.File.Exists(full))
        {
            throw new ScriptApiException($"{Api}: '{full}' was not found.", "Check the path; relative paths resolve against the level's folder, then <scripts>/import.");
        }

        IO.Mesh.Import.ImportedModel model;
        try
        {
            model = IO.Mesh.Import.MeshImporter.LoadConfined(full);
        }
        catch (Exception ex) when (ex is not ScriptApiException)
        {
            throw new ScriptApiException($"{Api}: could not read '{System.IO.Path.GetFileName(full)}': {ex.Message}",
                ext == ".gltf" ? "Only the model file is read: export a .glb, or a .gltf with embedded buffers." : null);
        }

        int triangles = model.TotalTriangles;
        if (model.Groups.Count == 0 || triangles == 0)
        {
            throw new ScriptApiException($"{Api}: '{System.IO.Path.GetFileName(full)}' contains no triangles.", "Export the model with its faces (not only points or lines).");
        }

        if (target == "detail" && model.Groups.FirstOrDefault(g => g.Indices.Count / 3 > MaxImportTrianglesPerGroup) is { } dense)
        {
            throw new ScriptApiException(
                $"{Api}: material group '{(dense.Name.Length > 0 ? dense.Name : dense.Texture)}' has {dense.Indices.Count / 3} triangles; as = \"detail\" allows at most {MaxImportTrianglesPerGroup} per group.",
                "Use as = \"mesh\" for dense models.");
        }

        var options = new IO.Mesh.Import.MeshImportOptions
        {
            Scale = (float)scale,
            Axis = axis ?? IO.Mesh.Import.MeshAxis.DefaultFor(model.Format),
            FlipWinding = flip,
            Target = target == "mesh" ? IO.Mesh.Import.MeshImportTarget.MeshObject : IO.Mesh.Import.MeshImportTarget.Brushes,
        };
        IO.Mesh.Import.MeshImportPipeline.ApplyTransform(model, options);

        string[] textures = model.ReferencedTextures.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ThenBy(t => t, StringComparer.Ordinal).ToArray();
        string[] missing = _ctx.Assets is { } vfs ? textures.Where(t => vfs.ResolveTexture(t) is null).ToArray() : Array.Empty<string>();
        if (missing.Length > 0)
        {
            _ctx.Log.Warn($"{Api}: textures not found in the mounted library (they will be flagged when packing): {string.Join(", ", missing)}.");
        }

        if (target == "mesh")
        {
            return ImportAsMeshObject(Api, model, name, pos, rot, tags, triangles, textures, missing);
        }

        IReadOnlyList<Brush> brushes = IO.Mesh.Import.MeshImportPipeline.ToBrushes(model, options, _ctx.Document.AllocateUid);
        if (brushes.Count == 0)
        {
            throw new ScriptApiException($"{Api}: '{System.IO.Path.GetFileName(full)}' has only degenerate triangles.", "Check the model's scale (tiny models collapse when vertices weld at 0.1 mm).");
        }

        foreach (Brush b in brushes)
        {
            b.Flags |= (uint)BrushFlags.Detail;
            b.Position = pos;
            b.Rotation = rot;
        }

        _ctx.Brushes.AddBrushes(brushes, brushes.Count == 1 ? "Import mesh" : $"Import mesh ({brushes.Count} brushes)");
        var uids = brushes.Select(b => b.Uid).ToList();
        foreach (int uid in uids)
        {
            _ctx.NoteCreated(uid);
            _ctx.Changes.Record("placed");
        }

        ScriptTags.Merge(_ctx, Api, uids, tags);
        ScriptGroupHandle? group = null;
        if (uids.Count >= 2)
        {
            group = new ScriptGroupHandle(_ctx, _ctx.Groups.CreateGroup(name, uids, Array.Empty<int>()));
            _ctx.Changes.Record("grouped");
        }

        return new ScriptMeshImport(new ScriptBrushQuery(_ctx, uids), null, group, triangles, model.Groups.Count, textures, missing, null);
    }

    private ScriptMeshImport ImportAsMeshObject(string api, IO.Mesh.Import.ImportedModel model, string name, Vec3 pos, Mat3 rot,
        Dictionary<string, string> tags, int triangles, string[] textures, string[] missing)
    {
        string stem = IO.Mesh.Import.MeshObjectImport.SanitizeMeshName(name);
        string meshFile = stem + ".v3m";
        string install = _ctx.Services.InstallDirectory is { Length: > 0 } dir
            ? dir
            : throw new ScriptApiException($"{api}: as = \"mesh\" needs a mounted RF install (the .v3m is written to <install>/user_maps/meshes).",
                "Run with an RF install, or import as = \"detail\".");
        string outPath = System.IO.Path.Combine(IO.Mesh.Import.MeshObjectImport.MeshesDirectory(install), meshFile);
        _ctx.RequireDestructive($"write {meshFile} to user_maps/meshes");
        if (_ctx.Services.MeshWriteAccess is { } writeAccess)
        {
            outPath = writeAccess(outPath);
        }

        byte[] bytes = IO.Mesh.Import.MeshObjectImport.BuildV3m(model, meshFile);
        if (System.IO.File.Exists(outPath))
        {
            if (!System.IO.File.ReadAllBytes(outPath).AsSpan().SequenceEqual(bytes))
            {
                throw new ScriptApiException($"{api}: user_maps/meshes/{meshFile} already exists with different content; it is never overwritten.", "Pass a different name.");
            }
        }
        else
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
            System.IO.File.WriteAllBytes(outPath, bytes);
            _ctx.Log.Info($"{api}: wrote user_maps/meshes/{meshFile}.");
        }

        _ctx.Assets?.Rescan();
        LevelObject placed = _ctx.Document.PlaceObject(LevelObjectKind.MeshObject, pos, meshFile)
            ?? throw new ScriptApiException($"{api}: could not place a Mesh object for {meshFile}.");
        _ctx.NoteCreated(placed.Uid);
        _ctx.Changes.Record("placed");
        var handle = new ScriptObjectHandle(_ctx, placed);
        handle.Set("name", name);
        if (rot != Mat3.Identity)
        {
            handle.SetRot(new ScriptRotation(rot));
        }

        ScriptTags.Merge(_ctx, api, new[] { placed.Uid }, tags);
        return new ScriptMeshImport(new ScriptBrushQuery(_ctx, Array.Empty<int>()), handle, null, triangles, model.Groups.Count, textures, missing, outPath);
    }

    /// <summary>A rooted path as given; a relative one against the level's folder, then <c>&lt;scripts&gt;/import</c> (the first that exists).</summary>
    private string ResolveImportPath(string api, string path)
    {
        if (System.IO.Path.IsPathRooted(path))
        {
            return System.IO.Path.GetFullPath(path);
        }

        var candidates = new List<string>();
        if (_ctx.Document.Path is { Length: > 0 } levelPath && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(levelPath)) is { } levelDir)
        {
            candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(levelDir, path)));
        }

        candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppPaths.ScriptsDirectory, "import", path)));
        foreach (string c in candidates)
        {
            if (System.IO.File.Exists(c))
            {
                return c;
            }
        }

        throw new ScriptApiException($"{api}: '{path}' was not found ({(candidates.Count > 1 ? "looked in the level's folder and" : "the level is unsaved; looked in")} <scripts>/import).",
            "Pass an absolute path, or put the file next to the level or in <scripts>/import.");
    }

    // ---- V2: queries ---------------------------------------------------

    [ScriptDoc("A brush_query over every brush in CSG time order.", Example = "level.all_brushes():where(function(b) return b.air end)", Since = 2)]
    public ScriptBrushQuery AllBrushes() => new(_ctx, _ctx.Brushes.Brushes.Select(b => b.Uid).ToList());

    [ScriptDoc("A brush_query of the brushes for which fn(brush) returns true, in time order.", Example = "level.brushes_where(function(b) return b.detail end)", Since = 2)]
    public ScriptBrushQuery BrushesWhere(ScriptBrushPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var uids = _ctx.Brushes.Brushes.Select(b => b.Uid).ToList();
        return new ScriptBrushQuery(_ctx, uids.Where(u => predicate(new ScriptBrushHandle(_ctx, u))).ToList());
    }

    [ScriptDoc("A brush_query of the brushes whose world bounds intersect the box {min=, max=} (metres, touching counts).", Example = "level.brushes_in{min={-8,0,-8}, max={8,6,8}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.Box))]
    public ScriptBrushQuery BrushesIn(ScriptArgs box)
    {
        (Vec3 min, Vec3 max) = Box("level.brushes_in", box);
        return new ScriptBrushQuery(_ctx, _ctx.BrushIndex.UidsIn(min, max));
    }

    [ScriptDoc("An object_query of the objects whose position lies inside the box {min=, max=} (metres, inclusive).", Example = "level.objects_in{min={-8,0,-8}, max={8,6,8}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.Box))]
    public ScriptObjectQuery ObjectsIn(ScriptArgs box)
    {
        (Vec3 min, Vec3 max) = Box("level.objects_in", box);
        return new ScriptObjectQuery(_ctx, _ctx.Document.Objects.Where(o =>
        {
            Vec3 p = o.Position;
            return p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
        }).ToList());
    }

    [ScriptDoc("The objects whose script name equals name (case-insensitive), in level order. Returns object[].", Example = "local doors = level.objects_named(\"door1\")", Since = 2)]
    public ScriptObjectHandle[] ObjectsNamed(string name)
    {
        string n = name ?? string.Empty;
        return _ctx.Document.Objects
            .Where(o => string.Equals(ScriptObjectNames.Get(o), n, StringComparison.OrdinalIgnoreCase))
            .Select(o => new ScriptObjectHandle(_ctx, o))
            .ToArray();
    }

    [ScriptDoc("A brush_query of the brushes tagged key (with exactly value when given), in time order.", Example = "level.brushes_tagged(\"kit.part\", \"air\"):set_textures{floor = \"floor01.tga\"}", Since = 2)]
    public ScriptBrushQuery BrushesTagged(string key, string? value = null)
    {
        string k = ScriptTags.Key("level.brushes_tagged", key);
        IReadOnlyDictionary<int, Dictionary<string, string>> all = _ctx.Metadata.AllTags();
        return new ScriptBrushQuery(_ctx, _ctx.Brushes.Brushes.Select(b => b.Uid).Where(u => ScriptTags.Matches(all, u, k, value)).ToList());
    }

    [ScriptDoc("An object_query of the objects tagged key (with exactly value when given), in level order.", Example = "level.objects_tagged(\"ai.session\"):tag(\"reviewed\", \"yes\")", Since = 2)]
    public ScriptObjectQuery ObjectsTagged(string key, string? value = null)
    {
        string k = ScriptTags.Key("level.objects_tagged", key);
        IReadOnlyDictionary<int, Dictionary<string, string>> all = _ctx.Metadata.AllTags();
        return new ScriptObjectQuery(_ctx, _ctx.Document.Objects.Where(o => ScriptTags.Matches(all, o.Uid, k, value)
            && (o.Kind != LevelObjectKind.Mover || _ctx.Brushes.FindBrush(o.Uid) is null)).ToList());
    }

    // ---- V2: movers and groups ----------------------------------------

    [ScriptDoc("Every mover in the level, in file order (mover[]).", Example = "for _, m in ipairs(level.movers) do print(m.name) end", Since = 2)]
    public ScriptMoverHandle[] Movers => _ctx.Movers.Movers.Select(g => new ScriptMoverHandle(_ctx, g)).ToArray();

    [ScriptDoc("The first mover named name (case-insensitive), or nil.", Example = "local door = level.find_mover(\"door1\")", Since = 2)]
    public ScriptMoverHandle? FindMover(string name) =>
        _ctx.Movers.Movers.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } g
            ? new ScriptMoverHandle(_ctx, g)
            : null;

    [ScriptDoc("Makes a mover from brushes (and/or objects) — handles or UIDs — with a start keyframe at their centre; name defaults to \"Mover\"; props = mover fields; tags go on the start keyframe. Returns the mover.",
        Example = "local m = level.create_mover{name = \"door1\", brushes = {door}, props = {is_door = true}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.CreateMover))]
    public ScriptMoverHandle CreateMover(ScriptArgs args)
    {
        const string Api = "level.create_mover";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{name=, brushes=}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.CreateMover);
        string name = MemberSetName(Api, args.String(Api, "name"), "Mover");
        List<int> brushes = ScriptMembers.Brushes(_ctx, Api, "brushes", args.Table(Api, "brushes"));
        List<int> objects = ScriptMembers.Objects(_ctx, Api, "objects", args.Table(Api, "objects"));
        if (brushes.Count == 0 && objects.Count == 0)
        {
            throw new ScriptApiException($"{Api}: 'brushes' is required (a list of brush handles or UIDs).", "For example level.create_mover{brushes = {door}}.");
        }

        foreach (int uid in brushes.Concat(objects))
        {
            if (_ctx.Movers.FindGroupForMember(uid) is { } owner)
            {
                throw new ScriptApiException($"{Api}: {(brushes.Contains(uid) ? "brush" : "object")} {uid} is already part of mover '{owner.Name}'.",
                    "A brush or object can belong to one mover only; dissolve the other mover first.");
            }
        }

        foreach (int uid in objects)
        {
            LevelObject o = _ctx.Document.FindByUid(uid)!;
            if (o.Kind is LevelObjectKind.Keyframe or LevelObjectKind.Mover or LevelObjectKind.PlayerStart)
            {
                throw new ScriptApiException($"{Api}: a {o.Kind} cannot ride a mover (object {uid}).", "Mover objects are things like triggers, lights and clutter.");
            }
        }

        ScriptArgs? props = args.Table(Api, "props");
        Dictionary<string, string> tags = ScriptTags.Table(Api, args.Table(Api, "tags"));

        Group group = _ctx.Movers.CreateMover(brushes, objects, name);
        _ctx.Brushes.NotifyExternalBrushChange(); // the member brushes leave the static fold
        _ctx.Changes.Record("mover");
        var handle = new ScriptMoverHandle(_ctx, group);
        int start = handle.StartKeyframeUid(Api);
        _ctx.NoteCreated(start);
        if (props is not null)
        {
            handle.FieldSet().SetMany($"{Api} props", props);
        }

        ScriptTags.Merge(_ctx, Api, new[] { start }, tags);
        return handle;
    }

    [ScriptDoc("Every user group in the level, in file order (group[]).", Example = "for _, g in ipairs(level.groups) do print(g.name) end", Since = 2)]
    public ScriptGroupHandle[] Groups => _ctx.Groups.Groups.Select(g => new ScriptGroupHandle(_ctx, g)).ToArray();

    [ScriptDoc("The first user group named name (case-insensitive), or nil.", Example = "local g = level.find_group(\"stairs1\")", Since = 2)]
    public ScriptGroupHandle? FindGroup(string name) =>
        _ctx.Groups.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } g
            ? new ScriptGroupHandle(_ctx, g)
            : null;

    [ScriptDoc("Groups brushes and/or objects (handles or UIDs) under a name (default \"Group\"); the editor then selects them together. Returns the group.",
        Example = "local g = level.create_group{name = \"stairs1\", brushes = {s1, s2, s3}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.CreateGroup))]
    public ScriptGroupHandle CreateGroup(ScriptArgs args)
    {
        const string Api = "level.create_group";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{name=, brushes=, objects=}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.CreateGroup);
        string name = MemberSetName(Api, args.String(Api, "name"), "Group");
        List<int> brushes = ScriptMembers.Brushes(_ctx, Api, "brushes", args.Table(Api, "brushes"));
        List<int> objects = ScriptMembers.Objects(_ctx, Api, "objects", args.Table(Api, "objects"));
        if (brushes.Count == 0 && objects.Count == 0)
        {
            throw new ScriptApiException($"{Api}: give at least one member in 'brushes' or 'objects'.", "For example level.create_group{name = \"g1\", brushes = {b1, b2}}.");
        }

        Group group = _ctx.Groups.CreateGroup(name, brushes, objects);
        _ctx.Changes.Record("grouped");
        return new ScriptGroupHandle(_ctx, group);
    }

    private static string MemberSetName(string api, string? name, string fallback)
    {
        if (name is null)
        {
            return fallback;
        }

        if (name.Trim().Length == 0)
        {
            throw new ScriptApiException($"{api}: 'name' must not be empty.");
        }

        return name;
    }

    private static (Vec3 Min, Vec3 Max) Box(string api, ScriptArgs box)
    {
        if (box is null)
        {
            throw new ScriptApiException($"{api}: a table {{min=, max=}} is required.");
        }

        box.RejectUnknown(api, ScriptOptionSets.Box);
        Vec3 a = box.VecOrNull(api, "min") ?? throw new ScriptApiException($"{api}: 'min' is required.");
        Vec3 b = box.VecOrNull(api, "max") ?? throw new ScriptApiException($"{api}: 'max' is required.");
        return (Vec3Math.Min(a, b), Vec3Math.Max(a, b));
    }

    // ---- V2: table-arg placement -----------------------------------------

    private bool _warnedUnvalidatedClasses;

    private bool _notedDefaultItemCount;

    /// <summary>
    /// A new item's count: its class's <c>items.tbl</c> Count (what RED gives a new item), else the
    /// document host's default, else 1 — noted once per run in the log, since a pickup of 1 is rarely
    /// what a level wants. An explicit <c>count</c> in <c>props</c> is applied afterwards and wins.
    /// </summary>
    private int? ItemCountFor(string api, LevelObjectKind kind, string? className)
    {
        if (kind != LevelObjectKind.Item)
        {
            return null;
        }

        int? count = _ctx.Catalogs.ItemCount(className) ?? (className is null ? null : _ctx.Document.ItemDefaultCount?.Invoke(className));
        if (count is null && !_notedDefaultItemCount)
        {
            _notedDefaultItemCount = true;
            _ctx.Log.Info($"{api}: no items.tbl count for '{className}' (no RF install mounted, or the class has none), so the item's count is 1; set props = {{count = …}} to choose one.");
        }

        return count;
    }

    /// <summary>
    /// Lua: <c>level.place{kind=, class=, pos=, rot=|yaw=, name=, props=, links=}</c> — places one
    /// object and configures it in a fixed order (place → name → props in schema order → rotation →
    /// links); any failure rolls the whole run back.
    /// </summary>
    [ScriptDoc("Places an object: kind (+ class for entity/item/clutter/event), pos (metres), optional rot or yaw, name, props {field = value}, links {handles/UIDs}. Returns the object.",
        Example = "level.place{kind = \"item\", class = \"Medical Kit\", pos = {0, 0.5, 4}, name = \"medkit1\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.Place))]
    public ScriptObjectHandle Place(ScriptArgs args)
    {
        const string Api = "level.place";
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{kind=, pos=, …}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.Place);
        string kindName = args.String(Api, "kind") ?? throw new ScriptApiException($"{Api}: 'kind' is required.",
            $"Placeable kinds: {string.Join(", ", ScriptCatalog.PlaceableKinds())}.");
        LevelObjectKind kind = ResolvePlaceKind(Api, kindName);
        Vec3 pos = args.VecOrNull(Api, "pos") ?? throw new ScriptApiException($"{Api}: 'pos' is required.", "A world point in metres, e.g. pos = {0, 0, 0}.");
        if (args.Has("rot") && args.Has("yaw"))
        {
            throw new ScriptApiException($"{Api}: give either 'rot' or 'yaw', not both.");
        }

        Mat3? rot = args.Has("rot") ? ScriptRotation.FromArgs(Api, "rot", args.Named["rot"])
            : args.NumberOrNull(Api, "yaw") is double yaw ? ScriptRotation.FromEuler(yaw, 0, 0) : null;
        string? name = args.String(Api, "name");
        ScriptArgs? props = args.Table(Api, "props");
        ScriptArgs? links = args.Table(Api, "links");
        Dictionary<string, string> tags = ScriptTags.Table(Api, args.Table(Api, "tags"));

        string? className = args.String(Api, "class");
        LevelObject placed;
        if (kind == LevelObjectKind.Event)
        {
            EventSchema schema = ScriptCatalog.FindEvent(Api, className ?? throw new ScriptApiException($"{Api}: 'class' is required for an event.",
                "Use catalog.classes(\"Event\") to list event classes."));
            if (schema.MinVersion > _ctx.Document.Rfl.Header.Version)
            {
                _ctx.Log.Warn($"{Api}: {schema.ClassName} needs RFL version {schema.MinVersion} or later; the level is saved at the Alpine version, which supports it.");
            }

            placed = _ctx.Document.PlaceEvent(schema, pos)
                ?? throw new ScriptApiException($"{Api}: could not place event '{schema.ClassName}'.");
        }
        else
        {
            string? cls = ResolveClass(Api, kind, className);
            placed = _ctx.Document.PlaceObject(kind, pos, cls, ItemCountFor(Api, kind, cls))
                ?? throw new ScriptApiException($"{Api}: could not place a {kind}.");
        }

        _ctx.NoteCreated(placed.Uid);
        _ctx.Changes.Record("placed");
        var handle = new ScriptObjectHandle(_ctx, placed);
        if (name is not null)
        {
            handle.Set("name", name);
        }

        if (props is not null)
        {
            handle.FieldSet().SetMany($"{Api} props", props);
        }

        if (rot is { } r)
        {
            handle.SetRot(new ScriptRotation(r));
        }

        if (links is not null)
        {
            ScriptLinkReport report = handle.LinkMany(links);
            if (!report.Ok)
            {
                throw new ScriptApiException($"{Api}: could not link {placed.Kind} {placed.Uid} to " +
                    string.Join("; ", report.Rejected.Select(x => $"{x.Uid} ({x.Reason})")) + ".");
            }
        }

        ScriptTags.Merge(_ctx, Api, new[] { placed.Uid }, tags);
        if (args.Bool(Api, "seat", false))
        {
            Seat(Api, handle, placed, pos.Y);
        }

        return handle;
    }

    private Func<LevelObject, (Vec3 Min, Vec3 Max)?>? _meshBounds;

    /// <summary>
    /// level.place{seat = true}: stock clutter and item meshes have their origin at their centre, so an object placed at
    /// a floor point sinks half its height into the floor. Moves it up so the lowest corner of its mesh bounds (as
    /// rotated) rests on <paramref name="floorY"/>. Needs a mounted install for the meshes; without one it warns.
    /// </summary>
    private void Seat(string api, ScriptObjectHandle handle, LevelObject placed, float floorY)
    {
        if (_ctx.Assets is not { } vfs)
        {
            _ctx.Log.Warn($"{api}: seat needs the game's meshes (an RF install: --rf-dir or GED_RF_DIR); {placed.Kind} {placed.Uid} is left at its position.");
            return;
        }

        _meshBounds ??= Linting.CraftAudit.MeshBoundsFrom(vfs);
        if (_meshBounds(placed) is not { } mb)
        {
            _ctx.Log.Warn($"{api}: no mesh bounds for {placed.Kind} '{placed.ClassName}', so it cannot be seated; it is left at its position.");
            return;
        }

        Mat3 m = Editing.ObjectRotation.TryGetOrientation(placed.Model, out Mat3 o, out _) ? o : Mat3.Identity;
        float low = float.MaxValue;
        foreach (float x in new[] { mb.Min.X, mb.Max.X })
        {
            foreach (float y in new[] { mb.Min.Y, mb.Max.Y })
            {
                foreach (float z in new[] { mb.Min.Z, mb.Max.Z })
                {
                    low = MathF.Min(low, (x * m.Right.Y) + (y * m.Up.Y) + (z * m.Forward.Y));
                }
            }
        }

        // The mesh's lowest point is pos.y + low; lift it onto the floor point given.
        Vec3 p = placed.Position;
        handle.SetPos(p.X, floorY - low, p.Z);
    }

    /// <summary>
    /// Lua: <c>level.player_start{pos=, rot=|yaw=}</c> — the level's single Player Start, created when
    /// absent (one undo step) or moved/rotated when present; <c>level.player_start()</c> just returns it (or nil).
    /// </summary>
    [ScriptDoc("The level's Player Start: with {pos=, rot=|yaw=} creates it (or moves/rotates the existing one); with no table returns it or nil.",
        Example = "level.player_start{pos = {0, 1, -4}, yaw = 0}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.PlayerStart))]
    public ScriptObjectHandle? PlayerStart(ScriptArgs? args = null)
    {
        const string Api = "level.player_start";
        LevelObject? existing = _ctx.Document.Objects.FirstOrDefault(o => o.Kind == LevelObjectKind.PlayerStart);
        if (args is null)
        {
            return existing is null ? null : new ScriptObjectHandle(_ctx, existing);
        }

        args.RejectUnknown(ScriptOptionSets.PlayerStart);
        if (args.Has("rot") && args.Has("yaw"))
        {
            throw new ScriptApiException($"{Api}: give either 'rot' or 'yaw', not both.");
        }

        Vec3? pos = args.VecOrNull(Api, "pos");
        Mat3? rot = args.Has("rot") ? ScriptRotation.FromArgs(Api, "rot", args.Named["rot"])
            : args.NumberOrNull(Api, "yaw") is double yaw ? ScriptRotation.FromEuler(yaw, 0, 0) : null;

        if (existing is null)
        {
            if (pos is null)
            {
                throw new ScriptApiException($"{Api}: this level has no Player Start yet; 'pos' is required to create one.",
                    "For example level.player_start{pos = {0, 1, 0}}.");
            }

            LevelObject created = _ctx.Document.CreatePlayerStart(pos.Value, rot?.Orthonormalize());
            _ctx.NoteCreated(created.Uid);
            _ctx.Changes.Record("placed");
            return new ScriptObjectHandle(_ctx, created);
        }

        var handle = new ScriptObjectHandle(_ctx, existing);
        if (pos is { } p)
        {
            handle.SetPos(p.X, p.Y, p.Z);
        }

        if (rot is { } r)
        {
            handle.SetRot(new ScriptRotation(r));
        }

        return handle;
    }

    private ScriptTerrains? _terrain;

    [ScriptDoc("The level's Alpine terrains: level.terrain.create{…}, all(), get(uid or name). Build Geometry compiles each into detail rooms, one per chunk.",
        Example = "local t = level.terrain.create{nx = 65, nz = 65} t:generate{seed = 3, height_range = 24}", Since = 2)]
    public ScriptTerrains Terrain => _terrain ??= new ScriptTerrains(_ctx);

    private ScriptPropertyBag? _props;
    private ScriptPropertyBag? _alpine;

    [ScriptDoc("The stock level properties (level_info / level_properties): level_name, author, date, multiplayer, geomod_texture, hardness, ambient_color, fog_*, rfl_version.",
        Example = "level.props:set_many{level_name = \"Arena\", ambient_color = {60, 60, 60}}", Since = 2)]
    public ScriptPropertyBag Props => _props ??= new ScriptPropertyBag(_ctx, "level.props", LevelPropertiesSchema.Fields);

    [ScriptDoc("The Alpine level properties: legacy timers/movers, headlamp, RF2-style geomod, payload_* rules, and the Alpine 1.5 lighting, sun_*, minimap_*, flight ceiling and far clip settings.",
        Example = "level.alpine:set(\"starts_with_headlamp\", true)", Since = 2)]
    public ScriptPropertyBag Alpine => _alpine ??= new ScriptPropertyBag(_ctx, "level.alpine", AlpineLevelPropertiesSchema.ScriptFields);

    [ScriptDoc("The Level Flags Outside / Inside buttons: flags every built room and every room effect outside (true) or inside (false), as RED does. Returns how many rooms and room effects changed.",
        Example = "level.set_all_rooms_outside(true)", Since = 2)]
    public int SetAllRoomsOutside(bool outside)
    {
        int changed = Editing.LevelRoomFlags.SetAllRooms(_ctx.Document, outside);
        if (changed > 0)
        {
            _ctx.Changes.Record(outside ? "rooms flagged outside" : "rooms flagged inside");
        }

        return changed;
    }

    private static LevelObjectKind ResolvePlaceKind(string api, string kindName)
    {
        string norm = kindName.Replace("_", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (norm is "playerstart")
        {
            throw new ScriptApiException($"{api}: the Player Start is unique.", "Use level.player_start{…}.");
        }

        if (norm is "mover" or "keyframe")
        {
            throw new ScriptApiException($"{api}: a {kindName} cannot be placed directly.", "Use level.create_mover{…}.");
        }

        LevelObjectKind kind = ScriptCatalog.ParseKind(api, kindName);
        if (!ScriptCatalog.PlaceableKinds().Contains(kind))
        {
            throw new ScriptApiException($"{api}: {kind} objects cannot be placed.",
                $"Placeable kinds: {string.Join(", ", ScriptCatalog.PlaceableKinds())}.");
        }

        return kind;
    }

    /// <summary>
    /// The class to place: required for entity / item / clutter and validated against the game tables
    /// when they are loaded (stored in the table's spelling); accepted with one warning per run when
    /// they are not. Other kinds take no class.
    /// </summary>
    private string? ResolveClass(string api, LevelObjectKind kind, string? className)
    {
        bool needsClass = kind is LevelObjectKind.Entity or LevelObjectKind.Item or LevelObjectKind.Clutter;
        if (!needsClass)
        {
            if (className is not null)
            {
                throw new ScriptApiException($"{api}: 'class' applies only to entity, item, clutter and event (kind is {kind}).", "Drop 'class'.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(className))
        {
            throw new ScriptApiException($"{api}: 'class' is required for a {kind}.",
                $"Use catalog.classes(\"{kind}\") to list valid classes.");
        }

        GameCatalogs catalogs = _ctx.Catalogs;
        if (catalogs.HasTable(kind))
        {
            return catalogs.Canonical(kind, className)
                ?? throw new ScriptApiException($"{api}: unknown {kind} class '{className}'.",
                    ScriptSuggest.Hint(className, catalogs.ClassNames(kind), "class", kind.ToString()));
        }

        if (!_warnedUnvalidatedClasses)
        {
            _warnedUnvalidatedClasses = true;
            _ctx.Log.Warn($"{api}: no game tables are loaded (no RF install mounted), so entity, item and clutter class names are not validated.");
        }

        return className;
    }

    /// <summary>Lua: <c>level.save([path])</c> — saves the document (destructive; see <c>ops.save</c>).</summary>
    [ScriptDoc("Saves the level (destructive: needs confirmation or --@allow-destructive; refused in Dry-Run). Same as ops.save.", Example = "level.save()")]
    public void Save(string? path = null) => _ctx.Ops.Save(path);

    // ---- Shared helpers (used by handles) -------------------------------------

    /// <summary>Deletes the given objects as one undo entry, reusing the document's delete path.</summary>
    internal void DeleteObjects(IReadOnlyCollection<LevelObject> objects)
    {
        var removable = objects.Where(o => o.CanRemove).ToList();
        if (removable.Count == 0)
        {
            return;
        }

        // Reuse EditorDocument.DeleteSelection (one undo entry, index-accurate) by scoping selection
        // to exactly the targets. Selection is a view concern the runner restores conceptually via undo.
        _ctx.Document.SelectMany(removable, additive: false);
        _ctx.Document.DeleteSelection("Script delete");
        _ctx.Changes.Record("deleted", removable.Count);
    }

    /// <summary>
    /// Parses a kind name to <see cref="LevelObjectKind"/> with the same rules as <c>level.place{kind=}</c>
    /// (case-insensitive, underscores/spaces ignored, aliases such as "respawn", "corona"), so a kind that
    /// places an object also finds it; unlike placement, every kind (keyframes, movers, …) is accepted.
    /// </summary>
    internal static LevelObjectKind ParseKind(string kind, string api = "level.objects_of")
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ScriptApiException($"{api}: a kind name is required.",
                $"Kinds: {string.Join(", ", Enum.GetNames<LevelObjectKind>())}.");
        }

        return ScriptCatalog.ParseKind(api, kind, anyKind: true);
    }
}
