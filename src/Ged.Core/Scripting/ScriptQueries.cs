using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A lazily-materialized set of object handles supporting chained filtering and bulk mutation
///. The predicate is expressed in Lua but the *apply* runs as vectorized C# inside
/// the run's single transaction, so a batch over thousands of objects produces one undo node.
/// </summary>
public sealed class ScriptObjectQuery
{
    private readonly ScriptContext _ctx;
    private readonly List<LevelObject> _objects;

    internal ScriptObjectQuery(ScriptContext ctx, IEnumerable<LevelObject> objects)
    {
        _ctx = ctx;
        _objects = objects.ToList();
    }

    /// <summary>Lua: <c>q.count</c>.</summary>
    [ScriptDoc("The number of objects in the set.", Example = "log.info(q.count)")]
    public int Count => _objects.Count;

    /// <summary>Lua: <c>q.objects</c> — the handles (iterate with <c>ipairs</c>).</summary>
    [ScriptDoc("The objects in the set as object[].", Example = "for _, o in ipairs(q.objects) do log.info(o.name) end")]
    public ScriptObjectHandle[] Objects => _objects.Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    /// <summary>Lua: <c>q:get(i)</c> — the i-th handle (1-based), or nil.</summary>
    [ScriptDoc("The i-th object (1-based), or nil when out of range.", Example = "local first = q:get(1)")]
    public ScriptObjectHandle? Get(int index) =>
        index >= 1 && index <= _objects.Count ? new ScriptObjectHandle(_ctx, _objects[index - 1]) : null;

    /// <summary>Lua: <c>q:where(function(o) … end)</c> — narrows the set.</summary>
    [ScriptDoc("A narrower object_query: the objects for which fn(object) returns true.", Example = "q:where(function(o) return o.y > 2 end)")]
    public ScriptObjectQuery Where(ScriptObjectPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ScriptObjectQuery(_ctx, _objects.Where(o => predicate(new ScriptObjectHandle(_ctx, o))));
    }

    /// <summary>Lua: <c>q:each(function(o) … end)</c> — runs an action per object (mutations undoable).</summary>
    [ScriptDoc("Calls fn(object) for every object (its changes join the run's undo step) and returns the query.", Example = "q:each(function(o) o:move(0, 1, 0) end)")]
    public ScriptObjectQuery Each(ScriptObjectAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        foreach (LevelObject o in _objects)
        {
            action(new ScriptObjectHandle(_ctx, o));
        }

        return this;
    }

    /// <summary>Lua: <c>q:select()</c> — replaces the selection with this set.</summary>
    [ScriptDoc("Replaces the object selection with this set and returns the query.", Example = "selection.of_kind(\"Light\"):select()")]
    public ScriptObjectQuery Select()
    {
        _ctx.Document.SelectMany(_objects, additive: false);
        return this;
    }

    /// <summary>Lua: <c>q:add_to_selection()</c> — adds this set to the selection.</summary>
    [ScriptDoc("Adds this set to the object selection and returns the query.", Example = "q:add_to_selection()")]
    public ScriptObjectQuery AddToSelection()
    {
        _ctx.Document.SelectMany(_objects, additive: true);
        return this;
    }

    /// <summary>Lua: <c>q:move(dx, dy, dz)</c> — translates every object (one undo node).</summary>
    [ScriptDoc("Moves every object by (dx, dy, dz) metres (one undo command) and returns the query.", Example = "q:move(0, 1, 0)")]
    public ScriptObjectQuery Move(double dx, double dy, double dz)
    {
        foreach (LevelObject o in _objects)
        {
            Vec3 old = o.Position;
            var next = new Vec3(old.X + (float)dx, old.Y + (float)dy, old.Z + (float)dz);
            _ctx.Document.EditValue(o.Section, "Move objects", old, next, v => o.Position = v);
        }

        _ctx.Changes.Record("moved", _objects.Count);
        return this;
    }

    /// <summary>Lua: <c>q:delete()</c> — deletes every object (one undo node). Destructive.</summary>
    [ScriptDoc("Deletes every object in the set (destructive: needs confirmation or --@allow-destructive) and returns how many were deleted.", Example = "local n = q:delete()")]
    public int Delete()
    {
        if (_objects.Count == 0)
        {
            return 0;
        }

        _ctx.RequireDestructive($"delete {_objects.Count} object(s)");
        _ctx.Level.DeleteObjects(_objects);
        return _objects.Count;
    }

    [ScriptDoc("The UIDs of the objects in the set, in set order (number[]).", Example = "local ids = q.uids", Since = 2)]
    public int[] Uids => _objects.Select(o => o.Uid).ToArray();

    [ScriptDoc("Sets one field on every object (one edit per object, all inside the run's single undo step); fails before changing anything if any object lacks the field. Returns the query.", Example = "level.objects_in{min={-8,0,-8}, max={8,6,8}}:where(function(o) return o.kind == 'Light' end):set(\"size_range\", 12)", Since = 2)]
    public ScriptObjectQuery Set(string name, object? value)
    {
        const string Api = "object_query:set";
        var sets = _objects.Select(o => ScriptFieldCatalog.ForObject(_ctx, o)).ToList();
        foreach (ScriptFieldSet s in sets)
        {
            s.Resolve(Api, name);
        }

        foreach (ScriptFieldSet s in sets)
        {
            s.Set(Api, name, value);
        }

        if (_objects.Count > 0)
        {
            _ctx.Changes.Record("edited", _objects.Count);
        }

        return this;
    }

    [ScriptDoc("Sets (or replaces) a provenance tag on every object in the set as one undo step; value must be a string. Returns the query.", Example = "level.objects_tagged(\"kit.name\", \"room1\"):tag(\"lit.pass\", \"2\")", Since = 2)]
    public ScriptObjectQuery Tag(string key, object? value)
    {
        const string Api = "object_query:tag";
        string k = ScriptTags.Key(Api, key);
        ScriptTags.Merge(_ctx, Api, _objects.Select(o => o.Uid).ToList(), new Dictionary<string, string> { [k] = ScriptTags.Value(Api, k, value) });
        if (_objects.Count > 0)
        {
            _ctx.Changes.Record("tagged", _objects.Count);
        }

        return this;
    }

    [ScriptDoc("Removes a provenance tag from every object in the set that has it, as one undo step. Returns the query.", Example = "level.objects_tagged(\"draft\"):untag(\"draft\")", Since = 2)]
    public ScriptObjectQuery Untag(string key)
    {
        ScriptTags.Remove(_ctx, _objects.Select(o => o.Uid).ToList(), ScriptTags.Key("object_query:untag", key));
        return this;
    }
}

/// <summary>
/// A set of brush handles with chained filtering and bulk mutation (the brush analogue of
/// <see cref="ScriptObjectQuery"/>). Bulk edits fold into the run's single transaction.
/// </summary>
public sealed class ScriptBrushQuery
{
    private readonly ScriptContext _ctx;
    private readonly List<int> _uids;

    internal ScriptBrushQuery(ScriptContext ctx, IEnumerable<int> uids)
    {
        _ctx = ctx;
        _uids = uids.ToList();
    }

    /// <summary>Lua: <c>q.count</c>.</summary>
    [ScriptDoc("The number of brushes in the set.", Example = "log.info(q.count)")]
    public int Count => _uids.Count;

    /// <summary>Lua: <c>q.brushes</c> — the handles.</summary>
    [ScriptDoc("The brushes in the set as brush[].", Example = "for _, b in ipairs(q.brushes) do log.info(b.uid) end")]
    public ScriptBrushHandle[] Brushes => _uids.Select(u => new ScriptBrushHandle(_ctx, u)).ToArray();

    /// <summary>Lua: <c>q:where(function(b) … end)</c>.</summary>
    [ScriptDoc("A narrower brush_query: the brushes for which fn(brush) returns true.", Example = "q:where(function(b) return b.air end)")]
    public ScriptBrushQuery Where(ScriptBrushPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ScriptBrushQuery(_ctx, _uids.Where(u => predicate(new ScriptBrushHandle(_ctx, u))));
    }

    /// <summary>Lua: <c>q:each(function(b) … end)</c>.</summary>
    [ScriptDoc("Calls fn(brush) for every brush (its changes join the run's undo step) and returns the query.", Example = "q:each(function(b) b:move(0, 1, 0) end)")]
    public ScriptBrushQuery Each(ScriptBrushAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        foreach (int u in _uids)
        {
            action(new ScriptBrushHandle(_ctx, u));
        }

        return this;
    }

    /// <summary>Lua: <c>q:select()</c> — selects these brushes.</summary>
    [ScriptDoc("Replaces the brush selection with this set and returns the query.", Example = "level.all_brushes():select()")]
    public ScriptBrushQuery Select()
    {
        bool additive = false;
        foreach (int u in _uids)
        {
            _ctx.Brushes.SelectBrush(u, additive);
            additive = true;
        }

        return this;
    }

    /// <summary>Lua: <c>q:move(dx, dy, dz)</c> — translates every brush (one undo node).</summary>
    [ScriptDoc("Moves every brush by (dx, dy, dz) metres (one undo command) and returns the query.", Example = "q:move(0, 1, 0)")]
    public ScriptBrushQuery Move(double dx, double dy, double dz)
    {
        var delta = new Vec3((float)dx, (float)dy, (float)dz);
        _ctx.Brushes.EditBrushes(_uids, "Move brushes", b =>
        {
            Editing.BrushTransform.Move(b, delta);
            return Editing.OpResult.Ok();
        });
        NoteEdited();
        _ctx.Changes.Record("moved", _uids.Count);
        return this;
    }

    /// <summary>Lua: <c>q:set_texture("name")</c> — sets every face on every brush (one undo node).</summary>
    [ScriptDoc("Sets every face of every brush in the set to this texture (one undo command) and returns the query.", Example = "q:set_texture(\"rock02.tga\")")]
    public ScriptBrushQuery SetTexture(string texture)
    {
        _ctx.Brushes.EditBrushes(_uids, $"Set texture {texture}", b =>
        {
            int tex = Editing.GeometryUtil.EnsureTexture(b.Geometry, texture);
            foreach (Face f in b.Geometry.Faces)
            {
                if (!f.IsPortalFace)
                {
                    f.Texture = tex;
                }
            }

            return Editing.OpResult.Ok();
        });
        NoteEdited();
        _ctx.Changes.Record("retextured", _uids.Count);
        return this;
    }

    private void NoteEdited()
    {
        foreach (int uid in _uids)
        {
            _ctx.NoteBrushEdited(uid);
        }
    }

    /// <summary>Lua: <c>q:delete()</c> — deletes every brush (one undo node). Destructive.</summary>
    [ScriptDoc("Deletes every brush in the set (destructive: needs confirmation or --@allow-destructive) and returns how many were deleted.", Example = "local n = q:delete()")]
    public int Delete()
    {
        if (_uids.Count == 0)
        {
            return 0;
        }

        _ctx.RequireDestructive($"delete {_uids.Count} brush(es)");
        _ctx.Brushes.DeleteBrushes(_uids);
        _ctx.Changes.Record("deleted", _uids.Count);
        return _uids.Count;
    }

    // ---- V2 -------------------------------------------------------------------------

    [ScriptDoc("The UIDs of the brushes in the set, in set order (number[]).", Since = 2)]
    public int[] Uids => _uids.ToArray();

    [ScriptDoc("The i-th brush (1-based), or nil when out of range.", Example = "local first = q:get(1)", Since = 2)]
    public ScriptBrushHandle? Get(int index) =>
        index >= 1 && index <= _uids.Count ? new ScriptBrushHandle(_ctx, _uids[index - 1]) : null;

    [ScriptDoc("Box-maps every brush as one undo step: no ppm = one repeat per 4 m (a new brush's tiling); ppm = pixels per metre by texture size (RED: 256). Returns the query.", Example = "level.all_brushes():box_map()", Since = 2)]
    public ScriptBrushQuery BoxMap(double ppm = ScriptBrushHandle.NewBrushTiling)
    {
        BoxMapScale scale = ScriptBrushHandle.ScaleFromPpm("brush_query:box_map", ppm);
        EditAll("Box map brushes", b => ScriptBrushHandle.BoxMapBrush(_ctx, b, scale), "uv");
        return this;
    }

    [ScriptDoc("Box-maps every brush from {tile = metres per texture repeat; 4 matches a newly drawn brush} or {ppm = pixels per metre} as one undo step. Returns the query.",
        Example = "level.brushes_tagged(\"kit\"):box_map{tile = 4}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.BoxMap))]
    public ScriptBrushQuery BoxMap(ScriptArgs options)
    {
        BoxMapScale scale = BoxMapScale.From("brush_query:box_map", options);
        EditAll("Box map brushes", b => ScriptBrushHandle.BoxMapBrush(_ctx, b, scale), "uv");
        return this;
    }

    [ScriptDoc("Lines the set's textures up as level.uv_align does (groups stay inside the set) as one undo step. Returns the query.",
        Example = "level.brushes_tagged(\"kit\"):uv_align{}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.UvAlignQuery))]
    public ScriptBrushQuery UvAlign(ScriptArgs? options = null)
    {
        const string Api = "brush_query:uv_align";
        options ??= new ScriptArgs();
        options.RejectUnknown(Api, ScriptOptionSets.UvAlignQuery);
        ScriptLevel.AlignBrushes(_ctx, Api, _uids, options);
        return this;
    }

    [ScriptDoc("Scales the UVs of every textured face about each face's UV centroid by (su, sv), non-zero, as one undo step. Returns the query.", Since = 2)]
    public ScriptBrushQuery ScaleUv(double su, double sv)
    {
        ScriptFaceHandle.RequireScale("brush_query:scale_uv", su, sv);
        EditAll("Scale brush UVs", b => ForMappableFaces(b, f => UvOps.Scale(f, (float)su, (float)sv)), "uv");
        return this;
    }

    [ScriptDoc("Offsets the UVs of every textured face by (du, dv) tile units as one undo step. Returns the query.", Since = 2)]
    public ScriptBrushQuery OffsetUv(double du, double dv)
    {
        ScriptBrushHandle.RequireFinite("brush_query:offset_uv", du, dv);
        EditAll("Offset brush UVs", b => ForMappableFaces(b, f => UvOps.Offset(f, (float)du, (float)dv)), "uv");
        return this;
    }

    [ScriptDoc("Retextures every brush by player-relative role {floor=, wall=, ceiling=} (any subset) as one undo step. Returns the query.", Example = "q:set_textures{floor = \"floor01.tga\"}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.RoleTextures))]
    public ScriptBrushQuery SetTextures(ScriptArgs textures)
    {
        RoleTextures roles = RoleTextures.From("brush_query:set_textures", textures, _ctx);
        EditAll("Set brush textures", roles.Apply, "retextured");
        return this;
    }

    [ScriptDoc("Sets brush flags {air=, detail=, portal=, geoable=, emits_steam=} (any subset; geoable implies detail) as one undo step. Returns the query.", Example = "q:set_flags{detail = true}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.Flags))]
    public ScriptBrushQuery SetFlags(ScriptArgs flags)
    {
        const string Api = "brush_query:set_flags";
        if (flags is null)
        {
            throw new ScriptApiException($"{Api}: a table of flags is required.");
        }

        flags.RejectUnknown(ScriptOptionSets.Flags);
        var changes = new List<(BrushFlags Flag, bool Value)>();
        foreach ((string key, BrushFlags flag) in new[]
                 {
                     ("air", BrushFlags.Air), ("detail", BrushFlags.Detail), ("portal", BrushFlags.Portal),
                     ("geoable", BrushFlags.Geoable), ("emits_steam", BrushFlags.EmitsSteam),
                 })
        {
            if (flags.Has(key))
            {
                changes.Add((flag, flags.Bool(Api, key, false)));
            }
        }

        if (changes.Count == 0)
        {
            throw new ScriptApiException($"{Api}: give at least one flag.", $"Valid options: {string.Join(", ", ScriptOptionSets.Flags.Keys)}.");
        }

        EditAll("Set brush flags", b =>
        {
            foreach ((BrushFlags flag, bool value) in changes)
            {
                b.Flags = value ? b.Flags | (uint)flag : b.Flags & ~(uint)flag;
            }

            b.Flags = Tables.BrushInspectorCatalog.Normalize(b.Flags);
        }, "flag");
        ScriptBrushHandle.WarnIfSolidPortal(_ctx, Api, _uids);
        return this;
    }

    [ScriptDoc("Sets a face flag (e.g. \"show_sky\") on every face whose world normal points \"up\" (y >= 0.7), \"down\", \"side\" or \"all\", on every brush, as one undo step. Returns the query.",
        Example = "canyon.brushes:set_face_flag(\"up\", \"show_sky\", true)", Since = 2)]
    public ScriptBrushQuery SetFaceFlag(string facing, string flag, bool value)
    {
        const string Api = "brush_query:set_face_flag";
        string dir = (facing ?? string.Empty).Trim().ToLowerInvariant();
        if (dir is not ("up" or "down" or "side" or "all"))
        {
            throw new ScriptApiException($"{Api}: unknown direction '{facing}'.", ScriptSuggest.Hint(dir, new[] { "up", "down", "side", "all" }, "direction"));
        }

        FaceFlags f;
        try
        {
            f = ScriptFaceHandle.ParseFlag(flag);
        }
        catch (ScriptApiException e)
        {
            throw new ScriptApiException($"{Api}: {e.Message}", e.Hint);
        }

        EditAll($"Set face {flag}", b =>
        {
            float sign = Editing.BrushOrientation.OutwardSign(b);
            foreach (Face face in b.Geometry.Faces)
            {
                if (face.Vertices.Count < 3)
                {
                    continue;
                }

                float ny = ScriptBrushHandle.WorldNormal(b, face, sign).Y;
                string role = ny >= ScriptBrushHandle.HorizontalDot ? "up" : ny <= -ScriptBrushHandle.HorizontalDot ? "down" : "side";
                if (dir == "all" || role == dir)
                {
                    FaceProps.Set(face, f, value);
                }
            }
        }, "flag");
        return this;
    }

    [ScriptDoc("Moves every brush in the set to the \"start\" or \"end\" of CSG time, keeping their relative order. Returns the query.", Example = "q:reorder(\"end\")", Since = 2)]
    public ScriptBrushQuery Reorder(string where)
    {
        switch ((where ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "start":
                _ctx.Brushes.MoveToStartOfTime(_uids);
                break;
            case "end":
                _ctx.Brushes.MoveToEndOfTime(_uids);
                break;
            default:
                throw new ScriptApiException($"brush_query:reorder: unknown position '{where}'.", "Use \"start\" or \"end\".");
        }

        foreach (int uid in _uids)
        {
            _ctx.NoteBrushEdited(uid);
        }

        _ctx.Changes.Record("reordered", _uids.Count);
        return this;
    }

    [ScriptDoc("Sets (or replaces) a provenance tag on every brush in the set as one undo step; value must be a string. Returns the query.", Example = "level.all_brushes():tag(\"pass\", \"blockout\")", Since = 2)]
    public ScriptBrushQuery Tag(string key, object? value)
    {
        const string Api = "brush_query:tag";
        string k = ScriptTags.Key(Api, key);
        ScriptTags.Merge(_ctx, Api, Live(), new Dictionary<string, string> { [k] = ScriptTags.Value(Api, k, value) });
        if (_uids.Count > 0)
        {
            _ctx.Changes.Record("tagged", _uids.Count);
        }

        return this;
    }

    [ScriptDoc("Removes a provenance tag from every brush in the set that has it, as one undo step. Returns the query.", Example = "level.brushes_tagged(\"pass\"):untag(\"pass\")", Since = 2)]
    public ScriptBrushQuery Untag(string key)
    {
        ScriptTags.Remove(_ctx, Live(), ScriptTags.Key("brush_query:untag", key));
        return this;
    }

    /// <summary>The UIDs of the set that are still brushes (a deleted brush is never tagged).</summary>
    private List<int> Live() => _uids.Where(u => _ctx.Brushes.FindBrush(u) is not null).ToList();

    private static void ForMappableFaces(Brush b, Action<Face> action)
    {
        foreach (Face f in b.Geometry.Faces)
        {
            if (ScriptBrushHandle.IsMappable(b.Geometry, f))
            {
                action(f);
            }
        }
    }

    /// <summary>One <see cref="BrushEditor.EditBrushes"/> over every UID in the set (no-op when empty).</summary>
    private void EditAll(string description, Action<Brush> mutate, string changeKind)
    {
        if (_uids.Count == 0)
        {
            return;
        }

        OpResult r = _ctx.Brushes.EditBrushes(_uids, description, b =>
        {
            mutate(b);
            return OpResult.Ok();
        });
        if (!r.Success)
        {
            throw new ScriptApiException($"{description} failed: {r.Message}");
        }

        foreach (int uid in _uids)
        {
            _ctx.NoteBrushEdited(uid);
        }

        _ctx.Changes.Record(changeKind, _uids.Count);
    }
}
