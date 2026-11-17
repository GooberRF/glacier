using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one brush, identified by UID so it survives edits that rebuild the
/// brush list. Every mutation routes through <see cref="BrushEditor.EditBrushes"/>
/// (undoable, snapshot-rollback-on-failure), so brush edits join the run's single transaction.
/// V2 setters return the handle so edits chain; no member changes the selection as a side effect
/// except <see cref="Select"/> (and <see cref="SetLocked"/>, which drops a locked brush from the
/// selection exactly as the editor does).
/// </summary>
public sealed partial class ScriptBrushHandle
{
    private readonly ScriptContext _ctx;

    internal ScriptBrushHandle(ScriptContext ctx, int uid)
    {
        _ctx = ctx;
        Uid = uid;
    }

    /// <summary>Lua: <c>brush.uid</c>.</summary>
    [ScriptDoc("The brush's unique id (shared UID space with objects).", Example = "log.info(b.uid)")]
    public int Uid { get; }

    internal Brush Model => _ctx.Brushes.FindBrush(Uid)
        ?? throw new ScriptApiException($"Brush {Uid} no longer exists.");

    /// <summary>Lua: <c>brush.pos</c> — the brush origin.</summary>
    [ScriptDoc("The brush position in metres: the centre of its local geometry (read-only; use set_pos or move).", Example = "log.info(b.pos.y)")]
    public Vec3 Pos => Model.Position;

    /// <summary>Lua: <c>brush.face_count</c>.</summary>
    [ScriptDoc("The number of faces.", Example = "log.info(b.face_count)")]
    public int FaceCount => Model.Geometry.Faces.Count;

    /// <summary>Lua: <c>brush.air</c> — true when the brush is an air (subtractive) brush.</summary>
    [ScriptDoc("True for an air brush (carves space; the world starts solid).", Example = "if b.air then log.info(\"room\") end")]
    public bool Air => HasFlag(BrushFlags.Air);

    /// <summary>Lua: <c>brush.solid</c> — the inverse of <c>air</c>.</summary>
    [ScriptDoc("True for a solid brush (fills space) — the inverse of air.", Example = "if b.solid then log.info(\"solid\") end")]
    public bool Solid => !HasFlag(BrushFlags.Air);

    /// <summary>Lua: <c>brush.portal</c>.</summary>
    [ScriptDoc("True for a portal brush (splits the level into rooms for visibility: an air portal opens the solid it covers and its opened walls become portals).", Example = "log.info(tostring(b.portal))")]
    public bool Portal => HasFlag(BrushFlags.Portal);

    /// <summary>Lua: <c>brush.detail</c>.</summary>
    [ScriptDoc("True for a detail brush (does not split rooms; breakable when it has finite life).", Example = "log.info(tostring(b.detail))")]
    public bool Detail => HasFlag(BrushFlags.Detail);

    /// <summary>Lua: <c>brush.geoable</c> — Alpine geo-modifiable.</summary>
    [ScriptDoc("True when the brush is Alpine geo-modifiable (implies detail).", Example = "log.info(tostring(b.geoable))")]
    public bool Geoable => HasFlag(BrushFlags.Geoable);

    /// <summary>Lua: <c>brush.emits_steam</c>.</summary>
    [ScriptDoc("True when the brush emits steam.", Example = "log.info(tostring(b.emits_steam))")]
    public bool EmitsSteam => HasFlag(BrushFlags.EmitsSteam);

    /// <summary>Lua: <c>brush.faces</c> — handles for each face (iterate with <c>ipairs</c>).</summary>
    [ScriptDoc("Every face of the brush as face[].", Example = "for _, f in ipairs(b.faces) do f:set_texture(\"rock02.tga\") end")]
    public ScriptFaceHandle[] Faces
    {
        get
        {
            int n = Model.Geometry.Faces.Count;
            var faces = new ScriptFaceHandle[n];
            for (int i = 0; i < n; i++)
            {
                faces[i] = new ScriptFaceHandle(_ctx, Uid, i);
            }

            return faces;
        }
    }

    // ---- V2 reads ---------------------------------------------------------------

    [ScriptDoc("The brush orientation as a rotation record (degrees).", Example = "print(b.rot.yaw)", Since = 2)]
    public ScriptRotation Rot => new(Model.Rotation);

    [ScriptDoc("Local bounding-box extents {w, h, d} in metres along the brush's own X, Y, Z axes.", Since = 2)]
    public Vec3 Size => BrushTransform.Dimensions(Model);

    [ScriptDoc("The world-space axis-aligned bounds of every vertex (aabb).", Example = "local top = b.bounds.max.y", Since = 2)]
    public ScriptAabb Bounds => _ctx.BrushIndex.BoundsOf(Model);

    [ScriptDoc("0-based CSG time order (list position); later brushes win over earlier ones.", Since = 2)]
    public int TimeIndex => _ctx.Brushes.TimeIndex(Uid);

    [ScriptDoc("True when the brush is locked (saved state; unselectable in the editor).", Since = 2)]
    public bool Locked => _ctx.Brushes.IsBrushLocked(Uid);

    [ScriptDoc("True when the brush is hidden in this editor session (not saved).", Since = 2)]
    public bool Hidden => _ctx.Brushes.IsBrushHidden(Uid);

    [ScriptDoc("True when the brush is in the brush selection.", Since = 2)]
    public bool Selected => _ctx.Brushes.IsBrushSelected(Uid);

    [ScriptDoc("Hit points; -1 = infinite. Finite life on a detail brush compiles as breakable.", Since = 2)]
    public int Life => Model.Life;

    [ScriptDoc("True when the world point (metres) lies inside the brush volume or on its surface (within the CSG epsilon, so a point on a face counts).", Example = "if b:contains(0, 1, 0) then log.info('inside') end", Since = 2)]
    public bool Contains(double x, double y, double z)
    {
        BrushVolume? v = _ctx.BrushIndex.VolumeOf(Model);
        return v is not null && v.Contains(new Vec3((float)x, (float)y, (float)z));
    }

    [ScriptDoc("Faces whose world outward normal points \"up\" (y >= 0.7), \"down\" (y <= -0.7) or \"side\" (otherwise). Returns face[].", Example = "for _, f in ipairs(b:faces_facing(\"up\")) do f:set_texture(\"floor01.tga\") end", Since = 2)]
    public ScriptFaceHandle[] FacesFacing(string dir)
    {
        string d = (dir ?? string.Empty).Trim().ToLowerInvariant();
        if (d is not ("up" or "down" or "side"))
        {
            throw new ScriptApiException($"brush:faces_facing: unknown direction '{dir}'.", "Use \"up\", \"down\" or \"side\".");
        }

        Brush b = Model;
        float sign = BrushOrientation.OutwardSign(b);
        var result = new List<ScriptFaceHandle>();
        for (int i = 0; i < b.Geometry.Faces.Count; i++)
        {
            float ny = WorldNormal(b, b.Geometry.Faces[i], sign).Y;
            string role = ny >= HorizontalDot ? "up" : (ny <= -HorizontalDot ? "down" : "side");
            if (role == d)
            {
                result.Add(new ScriptFaceHandle(_ctx, Uid, i));
            }
        }

        return result.ToArray();
    }

    // ---- V1 mutations ---------------------------------------------------------

    /// <summary>Lua: <c>brush:move(dx, dy, dz)</c> — translates the brush (undoable).</summary>
    [ScriptDoc("Moves the brush by (dx, dy, dz) metres.", Example = "b:move(0, 1, 0)")]
    public void Move(double dx, double dy, double dz)
    {
        var delta = new Vec3((float)dx, (float)dy, (float)dz);
        Edit("Move brush", b => BrushTransform.Move(b, delta));
        _ctx.Changes.Record("moved");
    }

    /// <summary>Lua: <c>brush:rotate(axis, degrees)</c> — rotates about its origin; axis = "x"|"y"|"z" (undoable).</summary>
    [ScriptDoc("Rotates the brush about its own position by degrees around axis \"x\", \"y\" or \"z\".", Example = "b:rotate(\"y\", 90)")]
    public void Rotate(string axis, double degrees)
    {
        Mat3 rot = RotationFor(axis, degrees);
        Edit("Rotate brush", b => BrushTransform.Rotate(b, rot));
        _ctx.Changes.Record("rotated");
    }

    /// <summary>Lua: <c>brush:set_texture("name")</c> — sets every face's texture (undoable).</summary>
    [ScriptDoc("Sets every face to this texture (by role: set_textures; per face: face:set_texture).", Example = "b:set_texture(\"rock02.tga\")")]
    public void SetTexture(string texture)
    {
        Edit($"Set brush texture {texture}", b =>
        {
            int tex = GeometryUtil.EnsureTexture(b.Geometry, texture);
            foreach (Face f in b.Geometry.Faces)
            {
                if (!f.IsPortalFace)
                {
                    f.Texture = tex;
                }
            }
        });
        _ctx.Changes.Record("retextured");
    }

    /// <summary>Lua: <c>brush:set_detail(true|false)</c> — toggles the detail flag (undoable).</summary>
    [ScriptDoc("Sets or clears the detail flag.", Example = "b:set_detail(true)")]
    public void SetDetail(bool value) => SetFlag(BrushFlags.Detail, value, "detail");

    /// <summary>Lua: <c>brush:set_portal(true|false)</c>.</summary>
    [ScriptDoc("Sets or clears the portal flag (a portal brush is normally air too: it then opens the solid it covers, and the walls it opens become portals).", Example = "b:set_portal(true)")]
    public void SetPortal(bool value) => SetFlag(BrushFlags.Portal, value, "portal");

    /// <summary>Lua: <c>brush:set_air(true|false)</c>.</summary>
    [ScriptDoc("Makes the brush air (true, carves) or solid (false, fills).", Example = "b:set_air(true)")]
    public void SetAir(bool value) => SetFlag(BrushFlags.Air, value, "air");

    /// <summary>Lua: <c>brush:select(additive?)</c>.</summary>
    [ScriptDoc("Selects the brush; additive = true keeps the existing brush selection (default replaces it).", Example = "b:select(true)")]
    public void Select(bool additive = false) => _ctx.Brushes.SelectBrush(Uid, additive);

    /// <summary>Lua: <c>brush:delete()</c> — deletes the brush (undoable). Destructive.</summary>
    [ScriptDoc("Deletes the brush (destructive: needs confirmation or --@allow-destructive; refused in Dry-Run).", Example = "b:delete()")]
    public void Delete()
    {
        _ctx.RequireDestructive($"delete brush {Uid}");
        _ctx.Brushes.DeleteBrushes(new[] { Uid });
        _ctx.Changes.Record("deleted");
    }

    // ---- V2 mutations (each returns the handle) --------------------------------

    [ScriptDoc("Moves the brush so its origin is at the world point (metres). Returns the brush.", Example = "b:set_pos(0, 2, 0)", Since = 2)]
    public ScriptBrushHandle SetPos(double x, double y, double z)
    {
        var target = new Vec3((float)x, (float)y, (float)z);
        RequireFinite("brush:set_pos", x, y, z);
        EditExisting("Set brush position", b => BrushTransform.Move(b, target.Sub(b.Position)));
        _ctx.Changes.Record("moved");
        return this;
    }

    [ScriptDoc("Sets the orientation about the brush origin from {yaw=, pitch=, roll=} degrees or {forward=, up=}. Returns the brush.", Example = "b:set_rot{yaw = 45}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.EulerRotation), nameof(ScriptOptionSets.BasisRotation))]
    public ScriptBrushHandle SetRot(ScriptArgs rot) => ApplyRot(ScriptRotation.FromArgs("brush:set_rot", "rot", rot));

    [ScriptDoc("Sets the orientation about the brush origin from a rotation record. Returns the brush.", Example = "b:set_rot(ged.rot(90))", Since = 2)]
    public ScriptBrushHandle SetRot(ScriptRotation rot) => ApplyRot(ScriptRotation.FromArgs("brush:set_rot", "rot", rot));

    [ScriptDoc("Rotates the brush by degrees about world axis \"x\"|\"y\"|\"z\" through the pivot point (metres). Returns the brush.", Example = "b:rotate_about(\"y\", 90, 0, 0, 0)", Since = 2)]
    public ScriptBrushHandle RotateAbout(string axis, double degrees, double px, double py, double pz)
    {
        RequireFinite("brush:rotate_about", degrees, px, py, pz);
        Mat3 rot = RotationFor(axis, degrees);
        var pivot = new Vec3((float)px, (float)py, (float)pz);
        EditExisting("Rotate brush", b => BrushTransform.RotateAboutPivot(b, rot, pivot));
        _ctx.Changes.Record("rotated");
        return this;
    }

    [ScriptDoc("Scales the local geometry by per-axis factors (> 0) about its bounds centre; re-applies default planar UVs. Returns the brush.", Example = "b:scale(2, 1, 1)", Since = 2)]
    public ScriptBrushHandle Scale(double sx, double sy, double sz)
    {
        if (!(sx > 0 && sy > 0 && sz > 0) || !double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz))
        {
            throw new ScriptApiException("brush:scale: factors must be finite and greater than 0.", "Use 1 to leave an axis unchanged.");
        }

        var factor = new Vec3((float)sx, (float)sy, (float)sz);
        EditExisting("Scale brush", b => BrushTransform.ScaleBy(b, factor));
        _ctx.Changes.Record("scaled");
        return this;
    }

    [ScriptDoc("Stretches the local geometry to the given extents in metres (0 leaves an axis unchanged); re-applies default planar UVs. Returns the brush.", Example = "b:stretch_to(8, 0, 8)", Since = 2)]
    public ScriptBrushHandle StretchTo(double w, double h, double d)
    {
        foreach (double v in new[] { w, h, d })
        {
            if (!double.IsFinite(v) || v < 0 || v > MaxExtent)
            {
                throw new ScriptApiException($"brush:stretch_to: extents must be between 0 and {MaxExtent} metres (got {ScriptArgs.Format(v)}).",
                    "Use 0 to leave an axis unchanged.");
            }
        }

        EditExisting("Stretch brush", b => BrushTransform.StretchToDimensions(b, (float)w, (float)h, (float)d));
        _ctx.Changes.Record("scaled");
        return this;
    }

    [ScriptDoc("Mirrors the brush across the world plane through its origin perpendicular to \"x\"|\"y\"|\"z\"; resets UVs to default planar. Returns the brush.", Example = "b:mirror(\"x\")", Since = 2)]
    public ScriptBrushHandle Mirror(string axis)
    {
        int a = AxisIndex(axis, "brush:mirror");
        EditExisting("Mirror brush", b => BrushOps.Mirror(b, a));
        _ctx.Changes.Record("mirrored");
        return this;
    }

    [ScriptDoc("Clips by a world plane {point=, normal=, mode=\"cut\"|\"split\"}: cut discards the side the normal points toward. Returns brush[] (this brush, then any new piece).", Example = "b:clip{point = {0, 0, 0}, normal = {1, 0, 0}, mode = \"split\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.Clip))]
    public ScriptBrushHandle[] Clip(ScriptArgs args)
    {
        const string Api = "brush:clip";
        args ??= new ScriptArgs();
        args.RejectUnknown(ScriptOptionSets.Clip);
        Vec3 point = args.VecOrNull(Api, "point") ?? throw new ScriptApiException($"{Api}: 'point' is required.", "A world point on the clip plane, e.g. point = {0, 2, 0}.");
        Vec3 normal = args.VecOrNull(Api, "normal") ?? throw new ScriptApiException($"{Api}: 'normal' is required.", "The plane normal, e.g. normal = {0, 1, 0}.");
        string modeName = (args.String(Api, "mode", "cut") ?? "cut").Trim().ToLowerInvariant();
        ClipMode mode = modeName switch
        {
            "cut" => ClipMode.Cut,
            "split" => ClipMode.Split,
            _ => throw new ScriptApiException($"{Api}: unknown mode '{modeName}'.", "Use \"cut\" or \"split\"."),
        };

        _ = Model;
        OpResult r = _ctx.Brushes.Clip(new[] { Uid }, point, normal, mode, flipNormal: false, out IReadOnlyList<int> pieces);
        if (!r.Success)
        {
            throw new ScriptApiException($"{Api}: {r.Message}");
        }

        _ctx.NoteBrushEdited(Uid);
        foreach (int uid in pieces.Where(u => u != Uid))
        {
            _ctx.NoteCreated(uid);
        }

        _ctx.Changes.Record("clipped");
        return pieces.Select(u => new ScriptBrushHandle(_ctx, u)).ToArray();
    }

    [ScriptDoc("Uses this brush as a cutter: permanently subtracts it from the given brushes (default: every other brush it touches). The cutter stays. Returns the carved brush_query.", Example = "cutter:carve({wall})", Since = 2)]
    public ScriptBrushQuery Carve(ScriptBrushHandle[]? targets = null)
    {
        _ = Model;
        IReadOnlyCollection<int>? uids = targets?.Select(t => t.Uid).ToList();
        IReadOnlyList<int> carved = _ctx.Brushes.Carve(Uid, uids);
        foreach (int uid in carved)
        {
            _ctx.NoteBrushEdited(uid);
        }

        if (carved.Count > 0)
        {
            _ctx.Changes.Record("carved", carved.Count);
        }

        return new ScriptBrushQuery(_ctx, carved);
    }

    [ScriptDoc("Moves the brush in CSG time order: \"start\" or \"end\". Returns the brush.", Example = "b:reorder(\"start\")", Since = 2)]
    public ScriptBrushHandle Reorder(string where)
    {
        _ = Model;
        switch ((where ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "start":
                _ctx.Brushes.MoveToStartOfTime(new[] { Uid });
                break;
            case "end":
                _ctx.Brushes.MoveToEndOfTime(new[] { Uid });
                break;
            default:
                throw new ScriptApiException($"brush:reorder: unknown position '{where}'.", "Use \"start\", \"end\", or a 0-based index.");
        }

        _ctx.NoteBrushEdited(Uid);
        _ctx.Changes.Record("reordered");
        return this;
    }

    [ScriptDoc("Moves the brush to 0-based CSG time index i (clamped to the brush count). Returns the brush.", Example = "b:reorder(0)", Since = 2)]
    public ScriptBrushHandle Reorder(double index)
    {
        _ = Model;
        int i = ScriptArgs.ToInt("brush:reorder", "index", index);
        var order = _ctx.Brushes.Brushes.Select(b => b.Uid).Where(u => u != Uid).ToList();
        order.Insert(Math.Clamp(i, 0, order.Count), Uid);
        _ctx.Brushes.ReorderTo(order);
        _ctx.NoteBrushEdited(Uid);
        _ctx.Changes.Record("reordered");
        return this;
    }

    [ScriptDoc("Locks or unlocks the brush (saved, undoable). Locking a selected brush also deselects it, as in the editor. Returns the brush.", Example = "b:set_locked(true)", Since = 2)]
    public ScriptBrushHandle SetLocked(bool locked)
    {
        _ = Model;
        _ctx.Brushes.SetBrushLocked(new[] { Uid }, locked);
        _ctx.Changes.Record("locked");
        return this;
    }

    [ScriptDoc("Hides or shows the brush in this editor session only: not undoable and not saved. Returns the brush.", Example = "b:set_hidden(true)", Since = 2)]
    public ScriptBrushHandle SetHidden(bool hidden)
    {
        _ = Model;
        _ctx.Brushes.SetBrushHidden(new[] { Uid }, hidden);
        return this;
    }

    [ScriptDoc("Duplicates the brush (its tags too), offset by (dx, dy, dz) metres (default 0), appended at the end of time. Returns the new brush.", Example = "local b2 = b:copy(8, 0, 0)", Since = 2)]
    public ScriptBrushHandle Copy(double dx = 0, double dy = 0, double dz = 0)
    {
        _ = Model;
        RequireFinite("brush:copy", dx, dy, dz);
        IReadOnlyList<int> uids = _ctx.Brushes.Duplicate(new[] { Uid }, new Vec3((float)dx, (float)dy, (float)dz));
        if (uids.Count == 0)
        {
            throw new ScriptApiException($"brush:copy: brush {Uid} could not be duplicated.");
        }

        _ctx.NoteCreated(uids[0]);
        _ctx.Changes.Record("placed");
        ScriptTags.Merge(_ctx, "brush:copy", new[] { uids[0] }, _ctx.Metadata.Tags(Uid));
        return new ScriptBrushHandle(_ctx, uids[0]);
    }

    // ---- V2: mover membership + provenance tags -------------------------

    [ScriptDoc("The mover this brush belongs to, or nil.", Example = "if b.mover then b.mover:sync() end", Since = 2)]
    public ScriptMoverHandle? Mover
    {
        get
        {
            _ = Model;
            return _ctx.Movers.FindGroupForMember(Uid) is { } g ? new ScriptMoverHandle(_ctx, g) : null;
        }
    }

    [ScriptDoc("Sets (or replaces) a provenance tag; value must be a string. Returns the brush.", Example = "b:tag(\"kit.part\", \"floor\")", Since = 2)]
    public ScriptBrushHandle Tag(string key, object? value)
    {
        const string Api = "brush:tag";
        _ = Model;
        string k = ScriptTags.Key(Api, key);
        ScriptTags.Merge(_ctx, Api, new[] { Uid }, new Dictionary<string, string> { [k] = ScriptTags.Value(Api, k, value) });
        _ctx.Changes.Record("tagged");
        return this;
    }

    [ScriptDoc("Removes a provenance tag (no-op when absent). Returns the brush.", Example = "b:untag(\"kit.part\")", Since = 2)]
    public ScriptBrushHandle Untag(string key)
    {
        _ = Model;
        ScriptTags.Remove(_ctx, new[] { Uid }, ScriptTags.Key("brush:untag", key));
        return this;
    }

    [ScriptDoc("The brush's provenance tags as a table {key = value} (empty when none).", Example = "for k, v in pairs(b:tags()) do print(k, v) end", Since = 2)]
    public Dictionary<string, object?> Tags()
    {
        _ = Model;
        return ScriptTags.ToLua(_ctx.Metadata.Tags(Uid));
    }

    [ScriptDoc("Sets the Alpine geoable flag (a geoable brush is always also detail). Returns the brush.", Example = "b:set_geoable(true)", Since = 2)]
    public ScriptBrushHandle SetGeoable(bool value)
    {
        SetFlag(BrushFlags.Geoable, value, "geoable", normalize: true);
        return this;
    }

    [ScriptDoc("Sets the emits-steam flag. Returns the brush.", Since = 2)]
    public ScriptBrushHandle SetEmitsSteam(bool value)
    {
        SetFlag(BrushFlags.EmitsSteam, value, "emits_steam", normalize: true);
        return this;
    }

    [ScriptDoc("Sets hit points: an integer >= -1 (-1 = infinite; finite life on a detail brush makes it breakable). Returns the brush.", Example = "b:set_detail(true) b:set_life(50)", Since = 2)]
    public ScriptBrushHandle SetLife(double life)
    {
        int n = ScriptArgs.ToInt("brush:set_life", "life", life);
        if (n < -1)
        {
            throw new ScriptApiException($"brush:set_life: life must be -1 (infinite) or more (got {n}).");
        }

        EditExisting("Set brush life", b => b.Life = n);
        _ctx.Changes.Record("life");
        return this;
    }

    [ScriptDoc("Retextures faces by player-relative role {floor=, wall=, ceiling=} (any subset); floor is the surface you stand on, also for air brushes. Returns the brush.", Example = "room:set_textures{floor = \"floor01.tga\", wall = \"wall01.tga\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RoleTextures))]
    public ScriptBrushHandle SetTextures(ScriptArgs textures)
    {
        RoleTextures roles = RoleTextures.From("brush:set_textures", textures, _ctx);
        EditExisting("Set brush textures", b => roles.Apply(b));
        _ctx.Changes.Record("retextured");
        return this;
    }

    [ScriptDoc("Box-maps every textured face in world space: no ppm = one repeat per 4 m (a new brush's tiling); ppm = pixels per metre by texture size (RED: 256). Returns the brush.", Example = "b:box_map() b:box_map(256)", Since = 2)]
    public ScriptBrushHandle BoxMap(double ppm = NewBrushTiling)
    {
        BoxMapScale scale = ScaleFromPpm("brush:box_map", ppm);
        EditExisting("Box map brush", b => BoxMapBrush(_ctx, b, scale));
        _ctx.Changes.Record("uv");
        return this;
    }

    [ScriptDoc("Box-maps every textured face in world space from {tile = metres per texture repeat; 4 matches a newly drawn brush} or {ppm = pixels per metre}. Returns the brush.",
        Example = "b:box_map{tile = 4}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.BoxMap))]
    public ScriptBrushHandle BoxMap(ScriptArgs options)
    {
        BoxMapScale scale = BoxMapScale.From("brush:box_map", options);
        EditExisting("Box map brush", b => BoxMapBrush(_ctx, b, scale));
        _ctx.Changes.Record("uv");
        return this;
    }

    // ---- V2: generic fields (BrushInspectorCatalog) ---------------------------------------

    [ScriptDoc("The value of a Properties-panel brush field by key or label (kind, is_portal, is_detail, emits_steam, is_geoable, life, locked, material, no_debris, uid, time_index).", Example = "print(b:get(\"material\"))", Since = 2)]
    public object? Get(string name) => ScriptFieldCatalog.ForBrush(_ctx, Uid).Get("brush:get", name);

    [ScriptDoc("Sets one brush field exactly as the Properties panel does (geoable implies detail; locking deselects). Returns the brush.", Example = "b:set(\"kind\", \"Air\")", Since = 2)]
    public ScriptBrushHandle Set(string name, object? value)
    {
        ScriptFieldCatalog.ForBrush(_ctx, Uid).Set("brush:set", name, value);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Sets several brush fields from {key = value, …}, applied in schema order. Returns the brush.", Example = "b:set_many{is_detail = true, life = 50}", Since = 2)]
    public ScriptBrushHandle SetMany(ScriptArgs values)
    {
        ScriptFieldCatalog.ForBrush(_ctx, Uid).SetMany("brush:set_many", values);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Every brush field (field_info[]), in Properties-panel order.", Example = "for _, f in ipairs(b:fields()) do print(f.name) end", Since = 2)]
    public ScriptFieldInfo[] Fields() => ScriptFieldCatalog.ForBrush(_ctx, Uid).Infos();

    [ScriptDoc("A table {key = value} of every brush field.", Example = "print(b:props().life)", Since = 2)]
    public Dictionary<string, object?> Props() => ScriptFieldCatalog.ForBrush(_ctx, Uid).Props();

    public override string ToString() => $"brush#{Uid}";

    // ---- Shared helpers ----------------------------------------------------------

    /// <summary>The factory's floor/ceiling threshold on the world normal's Y (BrushFactory.HorizontalDot).</summary>
    internal const float HorizontalDot = 0.7f;

    /// <summary>The largest brush extent the API accepts, in metres.</summary>
    internal const double MaxExtent = 4096;

    /// <summary>
    /// The world outward normal of a face (corner-derived, so it never depends on a stale stored plane), pointing
    /// out of the brush volume whichever way the brush is stored — RED stores an air brush's faces inward
    /// (<see cref="BrushOrientation"/>). Loops over many faces pass the brush's <see cref="BrushOrientation.OutwardSign(Brush)"/>.
    /// </summary>
    internal static Vec3 WorldNormal(Brush b, Face f) => WorldNormal(b, f, BrushOrientation.OutwardSign(b));

    /// <summary><see cref="WorldNormal(Brush, Face)"/> with the brush's outward sign already measured.</summary>
    internal static Vec3 WorldNormal(Brush b, Face f, float outwardSign) =>
        b.Rotation.Transform(GeometryUtil.Normal(GeometryUtil.Corners(b.Geometry, f)).Scale(outwardSign));

    /// <summary>The world AABB over every vertex of a brush.</summary>
    internal static ScriptAabb WorldBounds(Brush b)
    {
        if (b.Geometry.Vertices.Count == 0)
        {
            return new ScriptAabb(b.Position, b.Position);
        }

        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            Vec3 w = BrushTransform.WorldVertex(b, v);
            min = Vec3Math.Min(min, w);
            max = Vec3Math.Max(max, w);
        }

        return new ScriptAabb(min, max);
    }

    /// <summary>True when the face has a texture to map (not a portal face, texture index in range).</summary>
    internal static bool IsMappable(Geometry g, Face f) =>
        !f.IsPortalFace && f.Texture >= 0 && f.Texture < g.Textures.Count;

    /// <summary>Box-maps every mappable face of a brush, grouped by texture so each uses its pixel size.</summary>
    internal static void BoxMapBrush(ScriptContext ctx, Brush b, float ppm) => BoxMapBrush(ctx, b, BoxMapScale.PixelsPerMetre(ppm));

    /// <summary>
    /// Box-maps every mappable face of a brush at <paramref name="scale"/>: pixels per metre (each
    /// texture's pixel size decides how far one tile reaches) or a fixed tile size in metres (the same
    /// tiling a newly drawn brush gets, independent of the texture's pixel size).
    /// </summary>
    internal static void BoxMapBrush(ScriptContext ctx, Brush b, BoxMapScale scale)
    {
        Geometry g = b.Geometry;
        foreach (IGrouping<int, int> group in Enumerable.Range(0, g.Faces.Count)
                     .Where(i => IsMappable(g, g.Faces[i]))
                     .GroupBy(i => g.Faces[i].Texture))
        {
            if (scale.TileMetres is float tile)
            {
                // U = metres × ppm / width: with ppm = 1/tile over a 1×1 "texture" one tile spans `tile` metres on both axes.
                UvOps.BoxMap(g, group.ToList(), b.Rotation, b.Position, 1f / tile, 1, 1);
            }
            else
            {
                (int w, int h) = ctx.TextureSize(g.Textures[group.Key]);
                UvOps.BoxMap(g, group.ToList(), b.Rotation, b.Position, scale.Ppm, w, h);
            }
        }
    }

    /// <summary>
    /// The value of an omitted <c>ppm</c> argument: map at the editor's new-brush tiling (one repeat per
    /// <see cref="Editing.GeometryUtil.DefaultTileMetres"/> metres, the tiling a newly drawn brush gets) instead of RED's
    /// 256 pixels per metre (<see cref="UvOps.DefaultPixelsPerMeter"/>, which the editor's texture tools keep).
    /// </summary>
    internal const double NewBrushTiling = 0;

    /// <summary>A ppm argument as a box-map scale: omitted (<see cref="NewBrushTiling"/>) = the new-brush tile size.</summary>
    internal static BoxMapScale ScaleFromPpm(string api, double ppm) =>
        ppm == NewBrushTiling ? BoxMapScale.NewBrush : BoxMapScale.PixelsPerMetre(ClampPpm(api, ppm));

    /// <summary>Validates and clamps a pixels-per-metre argument.</summary>
    internal static float ClampPpm(string api, double ppm)
    {
        if (!double.IsFinite(ppm) || ppm <= 0)
        {
            throw new ScriptApiException($"{api}: ppm (pixels per metre) must be a positive number (got {ScriptArgs.Format(ppm)}).",
                $"Omit ppm for the editor's new-brush tiling (one repeat per {Editing.GeometryUtil.DefaultTileMetres} m); the editor's texture tools use {UvOps.DefaultPixelsPerMeter}.");
        }

        return UvOps.ClampPpm((float)ppm);
    }

    internal static void RequireFinite(string api, params double[] values)
    {
        foreach (double v in values)
        {
            if (!double.IsFinite(v))
            {
                throw new ScriptApiException($"{api}: arguments must be finite numbers (got {ScriptArgs.Format(v)}).");
            }
        }
    }

    internal static int AxisIndex(string axis, string api) => (axis ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _ => throw new ScriptApiException($"{api}: unknown axis '{axis}'.", "Use \"x\", \"y\", or \"z\"."),
    };

    private ScriptBrushHandle ApplyRot(Mat3 rot)
    {
        Mat3 r = rot.Orthonormalize();
        EditExisting("Set brush rotation", b => b.Rotation = r);
        _ctx.Changes.Record("rotated");
        return this;
    }

    private bool HasFlag(BrushFlags flag) => (Model.Flags & (uint)flag) != 0;

    private void SetFlag(BrushFlags flag, bool value, string label, bool normalize = false)
    {
        if (normalize)
        {
            _ = Model;
        }

        Edit($"Set brush {label}", b =>
        {
            if (value)
            {
                b.Flags |= (uint)flag;
            }
            else
            {
                b.Flags &= ~(uint)flag;
            }

            // V2 flag setters apply the cookie-cutter implication (geoable implies detail); the V1
            // set_air / set_detail / set_portal keep their raw single-bit semantics.
            if (normalize)
            {
                b.Flags = BrushInspectorCatalog.Normalize(b.Flags);
            }
        });
        _ctx.Changes.Record("flag");
        if (flag is BrushFlags.Portal or BrushFlags.Air)
        {
            WarnIfSolidPortal(_ctx, $"brush:set_{label}", new[] { Uid });
        }
    }

    /// <summary>
    /// Logs a warning for each brush that ended up portal-flagged but solid. The setters keep the
    /// Properties panel's single-flag behaviour; a solid portal is RED's membrane form (it carves nothing and its
    /// faces in open space become portal faces), which a script setting one bit usually did not mean.
    /// </summary>
    internal static void WarnIfSolidPortal(ScriptContext ctx, string api, IEnumerable<int> uids)
    {
        foreach (int uid in uids)
        {
            if (ctx.Brushes.FindBrush(uid) is { } b && (b.Flags & (uint)BrushFlags.Portal) != 0 && (b.Flags & (uint)BrushFlags.Air) == 0)
            {
                ctx.Log.Warn($"{api}: brush {uid} is now a solid portal brush: it carves nothing and its faces in open space become a portal membrane; set air = true for a portal that cuts a doorway through the wall.");
            }
        }
    }

    /// <summary>A V2 edit: fails with "Brush N no longer exists." before touching anything.</summary>
    private void EditExisting(string description, Action<Brush> mutate)
    {
        _ = Model;
        Edit(description, mutate);
    }

    private void Edit(string description, Action<Brush> mutate)
    {
        OpResult r = _ctx.Brushes.EditBrushes(new[] { Uid }, description, b =>
        {
            mutate(b);
            return OpResult.Ok();
        });
        if (!r.Success)
        {
            throw new ScriptApiException($"{description} failed: {r.Message}");
        }

        _ctx.NoteBrushEdited(Uid);
    }

    internal static Mat3 RotationFor(string axis, double degrees)
    {
        float rad = (float)(degrees * Math.PI / 180.0);
        return (axis ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "x" => Mat3Math.RotationX(rad),
            "y" => Mat3Math.RotationY(rad),
            "z" => Mat3Math.RotationZ(rad),
            _ => throw new ScriptApiException($"Unknown axis '{axis}'.", "Use \"x\", \"y\", or \"z\"."),
        };
    }
}

/// <summary>
/// Player-relative texture roles: <c>floor</c> is the surface the player stands
/// on — the up-facing outward face of a solid brush, the down-facing (bottom) face of an air brush.
/// Classification is by the face's WORLD outward normal with the factory's 0.7 threshold.
/// </summary>
internal sealed class RoleTextures
{
    private RoleTextures(string? floor, string? wall, string? ceiling)
    {
        Floor = floor;
        Wall = wall;
        Ceiling = ceiling;
    }

    public string? Floor { get; }

    public string? Wall { get; }

    public string? Ceiling { get; }

    /// <summary>Parses <c>{floor=, wall=, ceiling=}</c> (any subset, at least one); warns once per unresolved name.</summary>
    public static RoleTextures From(string api, ScriptArgs? t, ScriptContext ctx)
    {
        if (t is null)
        {
            throw new ScriptApiException($"{api}: a table {{floor=, wall=, ceiling=}} is required.");
        }

        t.RejectUnknown(api, ScriptOptionSets.RoleTextures);
        var roles = new RoleTextures(NonEmpty(api, t, "floor"), NonEmpty(api, t, "wall"), NonEmpty(api, t, "ceiling"));
        if (roles.Floor is null && roles.Wall is null && roles.Ceiling is null)
        {
            throw new ScriptApiException($"{api}: give at least one of floor, wall, ceiling.");
        }

        foreach (string name in new[] { roles.Floor, roles.Wall, roles.Ceiling }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            WarnIfUnresolved(ctx, name);
        }

        return roles;
    }

    /// <summary>Retextures every non-portal face of <paramref name="b"/> whose role has a texture.</summary>
    public void Apply(Brush b)
    {
        bool air = (b.Flags & (uint)BrushFlags.Air) != 0;
        float sign = BrushOrientation.OutwardSign(b);
        foreach (Face f in b.Geometry.Faces)
        {
            if (f.IsPortalFace)
            {
                continue;
            }

            float ny = ScriptBrushHandle.WorldNormal(b, f, sign).Y;
            bool up = ny >= ScriptBrushHandle.HorizontalDot;
            bool down = ny <= -ScriptBrushHandle.HorizontalDot;
            string? tex = up ? (air ? Ceiling : Floor)
                : down ? (air ? Floor : Ceiling)
                : Wall;
            if (tex is not null)
            {
                f.Texture = GeometryUtil.EnsureTexture(b.Geometry, tex);
            }
        }
    }

    /// <summary>Logs the same warning <c>place_box</c> logs for a texture the mounted library lacks.</summary>
    internal static void WarnIfUnresolved(ScriptContext ctx, string texture)
    {
        if (ctx.Assets is { } vfs && vfs.ResolveTexture(texture) is null)
        {
            ctx.Log.Warn($"texture '{texture}' was not found in the mounted library — " +
                         "faces will render white until it exists.");
        }
    }

    private static string? NonEmpty(string api, ScriptArgs t, string key)
    {
        string? v = t.String(api, key);
        if (v is not null && v.Trim().Length == 0)
        {
            throw new ScriptApiException($"{api}: '{key}' must be a texture name (got an empty string).");
        }

        return v;
    }
}

/// <summary>A box-map scale: pixels per metre (texture-size dependent) or a fixed tile size in metres.</summary>
internal readonly record struct BoxMapScale(float Ppm, float? TileMetres)
{
    /// <summary>The largest tile size accepted, in metres.</summary>
    internal const double MaxTileMetres = 1000;

    public static BoxMapScale PixelsPerMetre(float ppm) => new(ppm, null);

    /// <summary>The editor's new-brush tiling: one texture repeat per <see cref="Editing.GeometryUtil.DefaultTileMetres"/> metres.</summary>
    public static BoxMapScale NewBrush => new(0, Editing.GeometryUtil.DefaultTileMetres);

    /// <summary>The (ppm, width, height) a UvOps call takes for this scale and a texture of <paramref name="w"/>×<paramref name="h"/> pixels.</summary>
    public (float Ppm, int W, int H) For(int w, int h) => TileMetres is float tile ? (1f / tile, 1, 1) : (Ppm, w, h);

    /// <summary>Parses <c>{tile = metres}</c> or <c>{ppm = pixels per metre}</c> (neither = the editor's new-brush tiling).</summary>
    public static BoxMapScale From(string api, ScriptArgs? options)
    {
        options ??= new ScriptArgs();
        options.RejectUnknown(api, ScriptOptionSets.BoxMap);
        if (options.Has("tile") && options.Has("ppm"))
        {
            throw new ScriptApiException($"{api}: give either 'tile' (metres per texture tile) or 'ppm' (pixels per metre), not both.");
        }

        if (options.NumberOrNull(api, "tile") is double tile)
        {
            if (tile < 0.01 || tile > MaxTileMetres)
            {
                throw new ScriptApiException($"{api}: 'tile' must be between 0.01 and {MaxTileMetres} metres (got {ScriptArgs.Format(tile)}).",
                    $"A newly drawn brush tiles every {Editing.GeometryUtil.DefaultTileMetres} m.");
            }

            return new BoxMapScale(0, (float)tile);
        }

        return options.NumberOrNull(api, "ppm") is double ppm ? PixelsPerMetre(ScriptBrushHandle.ClampPpm(api, ppm)) : NewBrush;
    }
}
