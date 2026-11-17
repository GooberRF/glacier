using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one mover (a moving group). It holds the <see cref="Group"/>
/// reference and every member except <see cref="Exists"/> first checks the mover is still in
/// <see cref="Editing.MoverService.Movers"/>. Edits route through <see cref="Editing.MoverService"/>
/// (the mover inspector's own write path), so they join the run's single undo step.
/// <para>
/// The member brushes stay ordinary brushes in the level; the build and the game use a COPY of each
/// taken when the mover is created. Editing a member brush afterwards does not update that copy —
/// call <see cref="Sync"/> (the run warns about any mover left stale).
/// </para>
/// </summary>
[ScriptDoc("A mover: its member brushes/objects, keyframes, motion fields (get/set/fields), hold-open and sync. Create with level.create_mover{…}.", Since = 2)]
public sealed class ScriptMoverHandle
{
    private readonly ScriptContext _ctx;

    internal ScriptMoverHandle(ScriptContext ctx, Group group)
    {
        _ctx = ctx;
        Group = group;
    }

    internal Group Group { get; }

    private MovingGroupData Data
    {
        get
        {
            RequireExists();
            return Group.MovingData ?? throw new ScriptApiException($"Mover '{Group.Name}' has no motion data.");
        }
    }

    [ScriptDoc("True while the mover is still in the level (dissolving it, or removing its last keyframe, ends it).", Since = 2)]
    public bool Exists => _ctx.Movers.Movers.Contains(Group);

    [ScriptDoc("The mover's name.", Since = 2)]
    public string Name
    {
        get
        {
            RequireExists();
            return Group.Name;
        }
    }

    [ScriptDoc("The member brushes (brush[]); they stay ordinary, editable brushes.", Example = "for _, b in ipairs(m.brushes) do print(b.uid) end", Since = 2)]
    public ScriptBrushHandle[] Brushes
    {
        get
        {
            RequireExists();
            return Group.Brushes.Where(u => _ctx.Brushes.FindBrush(u) is not null).Select(u => new ScriptBrushHandle(_ctx, u)).ToArray();
        }
    }

    [ScriptDoc("The member objects that ride the mover (object[]).", Since = 2)]
    public ScriptObjectHandle[] Objects
    {
        get
        {
            RequireExists();
            return Group.Objects.Select(u => _ctx.Document.FindByUid(u)).OfType<LevelObject>().Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();
        }
    }

    [ScriptDoc("The keyframes in path order (object[] of kind Keyframe): set_pos/set_rot move them, get/set edit their timing fields.", Example = "m.keyframes[2]:set(\"pause_time\", 3)", Since = 2)]
    public ScriptObjectHandle[] Keyframes =>
        Data.Keyframes.Select(k => _ctx.Document.FindByUid(k.Uid)).OfType<LevelObject>().Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    [ScriptDoc("The start keyframe (object): the one links to the mover are stored as, and that member offsets are relative to.", Example = "print(m.start_keyframe.pos)", Since = 2)]
    public ScriptObjectHandle StartKeyframe
    {
        get
        {
            int uid = StartKeyframeUid("mover.start_keyframe");
            LevelObject o = _ctx.Document.FindByUid(uid) ?? throw new ScriptApiException($"Mover '{Group.Name}': keyframe {uid} is not in the level.");
            return new ScriptObjectHandle(_ctx, o);
        }
    }

    [ScriptDoc("True when Alpine Hold Open is on (the mover stays at its last keyframe).", Since = 2)]
    public bool HoldOpen
    {
        get
        {
            RequireExists();
            return _ctx.Movers.IsHoldOpen(Group);
        }
    }

    [ScriptDoc("Adds a keyframe at pos (world, metres) or offset (from the start keyframe) — exactly one — with optional rot and props {field = value}. Returns the keyframe object.",
        Example = "m:add_keyframe{offset = {0, 3, 0}, props = {pause_time = 2}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.AddKeyframe))]
    public ScriptObjectHandle AddKeyframe(ScriptArgs args)
    {
        const string Api = "mover:add_keyframe";
        MovingGroupData data = Data;
        if (args is null)
        {
            throw new ScriptApiException($"{Api}: a table {{pos=}} or {{offset=}} is required.");
        }

        args.RejectUnknown(ScriptOptionSets.AddKeyframe);
        Vec3? pos = args.VecOrNull(Api, "pos");
        Vec3? offset = args.VecOrNull(Api, "offset");
        if (pos.HasValue == offset.HasValue)
        {
            throw new ScriptApiException($"{Api}: give exactly one of 'pos' (world point) or 'offset' (from the start keyframe).",
                "For example m:add_keyframe{offset = {0, 3, 0}}.");
        }

        Vec3 start = data.Keyframes.Count > 0
            ? data.Keyframes[Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1)].Position
            : _ctx.Movers.MemberBoundsCenter(Group);
        Vec3 point = pos ?? start.Add(offset!.Value);
        Mat3 rot = args.Has("rot") ? ScriptRotation.FromArgs(Api, "rot", args.Named["rot"]).Orthonormalize() : Mat3.Identity;
        ScriptArgs? props = args.Table(Api, "props");

        Keyframe kf = _ctx.Movers.AddKeyframe(Group, point, rot);
        _ctx.NoteCreated(kf.Uid);
        _ctx.Changes.Record("keyframe");
        LevelObject obj = _ctx.Document.FindByUid(kf.Uid)
            ?? throw new ScriptApiException($"{Api}: keyframe {kf.Uid} could not be resolved.");
        var handle = new ScriptObjectHandle(_ctx, obj);
        if (props is not null)
        {
            handle.FieldSet().SetMany($"{Api} props", props);
        }

        return handle;
    }

    [ScriptDoc("Removes one of this mover's keyframes. Removing the LAST keyframe dissolves the mover (its brushes become static). Returns the mover.", Example = "m:remove_keyframe(m.keyframes[2])", Since = 2)]
    public ScriptMoverHandle RemoveKeyframe(ScriptObjectHandle keyframe)
    {
        const string Api = "mover:remove_keyframe";
        ArgumentNullException.ThrowIfNull(keyframe);
        MovingGroupData data = Data;
        Keyframe kf = data.Keyframes.FirstOrDefault(k => k.Uid == keyframe.Uid)
            ?? throw new ScriptApiException($"{Api}: {keyframe.Kind} {keyframe.Uid} is not a keyframe of mover '{Group.Name}'.",
                "Use one of m.keyframes.");
        _ctx.Movers.RemoveKeyframe(Group, kf);
        if (!Exists)
        {
            _ctx.Brushes.NotifyExternalBrushChange(); // dissolved: its brushes rejoin the static world
        }

        _ctx.Changes.Record("keyframe removed");
        return this;
    }

    [ScriptDoc("Turns Alpine Hold Open on or off (stored in the Alpine level properties; the section is created on first use). Returns the mover.", Example = "m:set_hold_open(true)", Since = 2)]
    public ScriptMoverHandle SetHoldOpen(bool holdOpen)
    {
        RequireExists();
        _ctx.Movers.SetHoldOpen(Group, holdOpen);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Refreshes the copies the build and game use from the member brushes (after editing a member) and recomputes member offsets; unchanged members are left as they are. Returns the mover.", Example = "door:set_pos(0, 1, 0) m:sync()", Since = 2)]
    public ScriptMoverHandle Sync()
    {
        RequireExists();
        if (_ctx.Movers.SyncMemberCopies(Group))
        {
            _ctx.Changes.Record("synced");
            _ctx.Brushes.NotifyExternalBrushChange();
        }

        return this;
    }

    [ScriptDoc("Dissolves the mover: its brushes revert to static world brushes and its keyframes are removed (undoable).", Example = "m:dissolve()", Since = 2)]
    public void Dissolve()
    {
        RequireExists();
        _ctx.Movers.DissolveMover(Group);
        _ctx.Brushes.NotifyExternalBrushChange();
        _ctx.Changes.Record("dissolved");
    }

    [ScriptDoc("RED's Propagate Sounds: copies this mover's sounds and volumes to every mover with a keyframe named like keyframe (one of its keyframes, not named \"Keyframe\"). Returns the movers written.",
        Example = "m:propagate_sounds(m.keyframes[1])", Since = 2)]
    public int PropagateSounds(ScriptObjectHandle keyframe) =>
        Propagate("mover:propagate_sounds", keyframe, Editing.MoverPropagation.PropagateSounds).Movers;

    [ScriptDoc("RED's Propagate Properties: this mover's flags, type and start go to movers with a keyframe named like keyframe; its timing, UIDs and rotation to keyframes of that name. Returns keyframes written.",
        Example = "m:propagate_properties(m.keyframes[1])", Since = 2)]
    public int PropagateProperties(ScriptObjectHandle keyframe) =>
        Propagate("mover:propagate_properties", keyframe, Editing.MoverPropagation.PropagateProperties).Keyframes;

    private Editing.MoverPropagationResult Propagate(string api, ScriptObjectHandle keyframe, Func<Editing.MoverService, Keyframe, Editing.MoverPropagationResult> run)
    {
        ArgumentNullException.ThrowIfNull(keyframe);
        Keyframe kf = Data.Keyframes.FirstOrDefault(k => k.Uid == keyframe.Uid)
            ?? throw new ScriptApiException($"{api}: {keyframe.Kind} {keyframe.Uid} is not a keyframe of mover '{Group.Name}'.", "Use one of m.keyframes.");
        Editing.MoverPropagationResult result = run(_ctx.Movers, kf);
        if (result.Refusal is { } why)
        {
            throw new ScriptApiException($"{api}: {why}", "Give the keyframe a script name of its own first, e.g. kf:set(\"script_name\", \"door_a\").");
        }

        _ctx.Changes.Record("propagated");
        return result;
    }

    // ---- generic fields ----------------------------------------------------

    internal ScriptFieldSet FieldSet()
    {
        RequireExists();
        return ScriptFieldCatalog.ForMover(_ctx, Group);
    }

    [ScriptDoc("A mover field by key or label: movement_type (option name), is_door, starting_keyframe, sounds/volumes, hold_open, …", Example = "print(m:get(\"movement_type\"))", Since = 2)]
    public object? Get(string name) => FieldSet().Get("mover:get", name);

    [ScriptDoc("Sets one mover field exactly as the mover inspector does. Returns the mover.", Example = "m:set(\"movement_type\", \"Ping Pong Once\")", Since = 2)]
    public ScriptMoverHandle Set(string name, object? value)
    {
        FieldSet().Set("mover:set", name, value);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Sets several mover fields from {key = value, …}, applied in schema order. Returns the mover.", Example = "m:set_many{movement_type = \"Ping Pong Once\", is_door = true}", Since = 2)]
    public ScriptMoverHandle SetMany(ScriptArgs values)
    {
        FieldSet().SetMany("mover:set_many", values);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Every mover field (field_info[]), in inspector order.", Example = "for _, f in ipairs(m:fields()) do print(f.name) end", Since = 2)]
    public ScriptFieldInfo[] Fields() => FieldSet().Infos();

    [ScriptDoc("A table {key = value} of every mover field.", Example = "print(m:props().is_door)", Since = 2)]
    public Dictionary<string, object?> Props() => FieldSet().Props();

    public override string ToString() => Exists ? $"mover '{Group.Name}'" : $"mover '{Group.Name}' (dissolved)";

    /// <summary>The start keyframe's UID — what a link to this mover stores (the UID the game follows).</summary>
    internal int StartKeyframeUid(string api)
    {
        MovingGroupData data = Data;
        if (data.Keyframes.Count == 0)
        {
            throw new ScriptApiException($"{api}: mover '{Group.Name}' has no keyframes.");
        }

        return data.Keyframes[Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1)].Uid;
    }

    private void RequireExists()
    {
        if (!Exists)
        {
            throw new ScriptApiException("This mover no longer exists.", "It was dissolved (or lost its last keyframe) earlier in the run.");
        }
    }
}

/// <summary>
/// A script handle onto one user group: a named set of brushes and objects that the
/// editor selects as a unit. Edits route through <see cref="Editing.GroupService"/>.
/// </summary>
[ScriptDoc("A user group: a named set of brushes and objects the editor selects together. Create with level.create_group{…}.", Since = 2)]
public sealed class ScriptGroupHandle
{
    private readonly ScriptContext _ctx;

    internal ScriptGroupHandle(ScriptContext ctx, Group group)
    {
        _ctx = ctx;
        Group = group;
    }

    internal Group Group { get; }

    [ScriptDoc("True while the group is still in the level.", Since = 2)]
    public bool Exists => _ctx.Groups.Groups.Contains(Group);

    [ScriptDoc("The group's name.", Since = 2)]
    public string Name
    {
        get
        {
            RequireExists();
            return Group.Name;
        }
    }

    [ScriptDoc("The member brushes (brush[]).", Since = 2)]
    public ScriptBrushHandle[] Brushes
    {
        get
        {
            RequireExists();
            return Group.Brushes.Where(u => _ctx.Brushes.FindBrush(u) is not null).Select(u => new ScriptBrushHandle(_ctx, u)).ToArray();
        }
    }

    [ScriptDoc("The member objects (object[]).", Since = 2)]
    public ScriptObjectHandle[] Objects
    {
        get
        {
            RequireExists();
            return Group.Objects.Select(u => _ctx.Document.FindByUid(u)).OfType<LevelObject>().Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();
        }
    }

    [ScriptDoc("Adds members {brushes = {…}, objects = {…}} (handles or UIDs; existing members are skipped). Returns the group.", Example = "g:add{brushes = {b1, b2}}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GroupMembers))]
    public ScriptGroupHandle Add(ScriptArgs members)
    {
        const string Api = "group:add";
        RequireExists();
        if (members is null)
        {
            throw new ScriptApiException($"{Api}: a table {{brushes = {{…}}, objects = {{…}}}} is required.");
        }

        members.RejectUnknown(ScriptOptionSets.GroupMembers);
        List<int> brushes = ScriptMembers.Brushes(_ctx, Api, "brushes", members.Table(Api, "brushes"));
        List<int> objects = ScriptMembers.Objects(_ctx, Api, "objects", members.Table(Api, "objects"));
        _ctx.Groups.AddMembers(Group, brushes, objects);
        _ctx.Changes.Record("grouped");
        return this;
    }

    [ScriptDoc("Removes the given members (a list of brush/object handles or UIDs; non-members are ignored). Returns the group.", Example = "g:remove{b1, 42}", Since = 2)]
    public ScriptGroupHandle Remove(ScriptArgs members)
    {
        const string Api = "group:remove";
        RequireExists();
        (List<int> brushes, List<int> objects) = ScriptMembers.Mixed(_ctx, Api, members);
        _ctx.Groups.RemoveMembers(Group, brushes.Concat(objects));
        _ctx.Changes.Record("ungrouped");
        return this;
    }

    [ScriptDoc("Renames the group (a non-empty name). Returns the group.", Example = "g:rename(\"stairs_east\")", Since = 2)]
    public ScriptGroupHandle Rename(string name)
    {
        RequireExists();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ScriptApiException("group:rename: the name must not be empty.");
        }

        _ctx.Groups.Rename(Group, name);
        _ctx.Changes.Record("renamed");
        return this;
    }

    [ScriptDoc("Dissolves the group; its members stay in the level (undoable).", Example = "g:dissolve()", Since = 2)]
    public void Dissolve()
    {
        RequireExists();
        _ctx.Groups.Dissolve(Group);
        _ctx.Changes.Record("dissolved");
    }

    public override string ToString() => Exists ? $"group '{Group.Name}'" : $"group '{Group.Name}' (dissolved)";

    private void RequireExists()
    {
        if (!Exists)
        {
            throw new ScriptApiException("This group no longer exists.", "It was dissolved earlier in the run.");
        }
    }
}

/// <summary>Resolves member lists (handles or UIDs) for movers and groups, validating that each exists.</summary>
internal static class ScriptMembers
{
    /// <summary>A list of brush handles / brush UIDs → distinct brush UIDs in list order.</summary>
    public static List<int> Brushes(ScriptContext ctx, string api, string key, ScriptArgs? list)
    {
        var uids = new List<int>();
        foreach (object? item in Items(api, key, list))
        {
            int uid = item switch
            {
                ScriptBrushHandle b => b.Uid,
                double d => ScriptArgs.ToInt(api, key, d),
                ScriptObjectHandle o => throw new ScriptApiException($"{api}: '{key}' takes brushes, but {o.Kind} {o.Uid} is an object.", "Pass objects in 'objects'."),
                _ => throw new ScriptApiException($"{api}: '{key}' items must be brush handles or UIDs (got {ScriptArgs.LuaTypeOf(item)})."),
            };
            if (ctx.Brushes.FindBrush(uid) is null)
            {
                throw new ScriptApiException($"{api}: there is no brush with UID {uid}.");
            }

            if (!uids.Contains(uid))
            {
                uids.Add(uid);
            }
        }

        return uids;
    }

    /// <summary>A list of object handles / object UIDs → distinct object UIDs in list order.</summary>
    public static List<int> Objects(ScriptContext ctx, string api, string key, ScriptArgs? list)
    {
        var uids = new List<int>();
        foreach (object? item in Items(api, key, list))
        {
            int uid = item switch
            {
                ScriptObjectHandle o => o.Uid,
                double d => ScriptArgs.ToInt(api, key, d),
                ScriptBrushHandle b => throw new ScriptApiException($"{api}: '{key}' takes objects, but {b.Uid} is a brush.", "Pass brushes in 'brushes'."),
                _ => throw new ScriptApiException($"{api}: '{key}' items must be object handles or UIDs (got {ScriptArgs.LuaTypeOf(item)})."),
            };
            if (ctx.Document.FindByUid(uid) is null || ctx.Brushes.FindBrush(uid) is not null)
            {
                throw new ScriptApiException($"{api}: there is no object with UID {uid}.");
            }

            if (!uids.Contains(uid))
            {
                uids.Add(uid);
            }
        }

        return uids;
    }

    /// <summary>A mixed list of brush/object handles or UIDs → (brush UIDs, object UIDs).</summary>
    public static (List<int> Brushes, List<int> Objects) Mixed(ScriptContext ctx, string api, ScriptArgs? list)
    {
        var brushes = new List<int>();
        var objects = new List<int>();
        foreach (object? item in Items(api, "members", list))
        {
            switch (item)
            {
                case ScriptBrushHandle b:
                    brushes.Add(b.Uid);
                    break;
                case ScriptObjectHandle o:
                    objects.Add(o.Uid);
                    break;
                case double d:
                    int uid = ScriptArgs.ToInt(api, "members", d);
                    (ctx.Brushes.FindBrush(uid) is not null ? brushes : objects).Add(uid);
                    break;
                default:
                    throw new ScriptApiException($"{api}: members must be brush/object handles or UIDs (got {ScriptArgs.LuaTypeOf(item)}).");
            }
        }

        return (brushes.Distinct().ToList(), objects.Distinct().ToList());
    }

    private static IReadOnlyList<object?> Items(string api, string key, ScriptArgs? list)
    {
        if (list is null)
        {
            return Array.Empty<object?>();
        }

        // Named fields beside the items (a kit list's report) are ignored; named fields alone are a mistake.
        if (list.Named.Count > 0 && list.Items.Count == 0)
        {
            throw new ScriptApiException($"{api}: '{key}' must be a list of handles or UIDs, not named options.", "For example {b1, b2, 42}.");
        }

        return list.Items;
    }
}
