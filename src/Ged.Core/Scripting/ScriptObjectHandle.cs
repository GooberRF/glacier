using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one <see cref="LevelObject"/>. Reads project the underlying
/// model; every mutation routes through the document's undo stack via
/// <see cref="EditorDocument.EditValue{T}"/> so it participates in the run's single transaction —
/// the raw <see cref="LevelObject"/> setters bypass undo and are never used here.
/// </summary>
public sealed class ScriptObjectHandle
{
    private readonly ScriptContext _ctx;

    internal ScriptObjectHandle(ScriptContext ctx, LevelObject obj)
    {
        _ctx = ctx;
        Object = obj;
    }

    internal LevelObject Object { get; }

    /// <summary>Lua: <c>obj.uid</c> — the unique id.</summary>
    [ScriptDoc("The object's unique id (shared UID space with brushes).", Example = "log.info(o.uid)")]
    public int Uid => Object.Uid;

    /// <summary>Lua: <c>obj.kind</c> — the object category ("Light", "Mover", "Entity", …).</summary>
    [ScriptDoc("The object kind: \"Light\", \"Trigger\", \"Event\", \"Entity\", \"Item\", \"Keyframe\", ….", Example = "if o.kind == \"Light\" then log.info(o.name) end")]
    public string Kind => Object.Kind.ToString();

    /// <summary>Lua: <c>obj.name</c> — the script name (empty when unnamed).</summary>
    [ScriptDoc("The script name (empty when unnamed); change it with set_name or set(\"name\", …).", Example = "log.info(o.name)")]
    public string Name => ScriptObjectNames.Get(Object);

    /// <summary>Lua: <c>obj.class_name</c> — the class/type name where applicable.</summary>
    [ScriptDoc("The class name where the kind has one (entity/item/clutter/event class), else the kind.", Example = "log.info(o.class_name)")]
    public string ClassName => Object.ClassName;

    /// <summary>Lua: <c>obj.display_name</c> — name, else class, else "Kind uid".</summary>
    [ScriptDoc("A label for messages: the script name, else the class, else \"Kind uid\".", Example = "log.info(o.display_name)")]
    public string DisplayName => Object.DisplayName;

    /// <summary>Lua: <c>obj.hidden</c> — whether the object is hidden.</summary>
    [ScriptDoc("True when the object is hidden in the editor views.", Example = "if o.hidden then o:set_hidden(false) end")]
    public bool Hidden => Object.Hidden;

    /// <summary>Lua: <c>obj.pos</c> — the position vector (read; use <c>set_pos</c>/<c>move</c> to change).</summary>
    [ScriptDoc("The world position in metres (read-only; use set_pos or move).", Example = "local p = o.pos log.info(p.x .. \",\" .. p.y .. \",\" .. p.z)")]
    public Vec3 Pos => Object.Position;

    /// <summary>Lua: <c>obj.x</c>.</summary>
    [ScriptDoc("The X (right) coordinate of the position, metres.", Example = "log.info(o.x)")]
    public double X => Object.Position.X;

    /// <summary>Lua: <c>obj.y</c>.</summary>
    [ScriptDoc("The Y (up) coordinate of the position, metres.", Example = "log.info(o.y)")]
    public double Y => Object.Position.Y;

    /// <summary>Lua: <c>obj.z</c>.</summary>
    [ScriptDoc("The Z (forward) coordinate of the position, metres.", Example = "log.info(o.z)")]
    public double Z => Object.Position.Z;

    /// <summary>Lua: <c>obj.selected</c> — whether the object is in the current selection.</summary>
    [ScriptDoc("True when the object is in the current selection.", Example = "if o.selected then o:move(0, 1, 0) end")]
    public bool Selected => _ctx.Document.IsSelected(Object);

    /// <summary>Lua: <c>obj:set_pos(x, y, z)</c> — moves the object (undoable).</summary>
    [ScriptDoc("Moves the object to (x, y, z) metres.", Example = "o:set_pos(0, 2, 0)")]
    public void SetPos(double x, double y, double z)
    {
        RequireEditable("object:set_pos");
        Vec3 old = Object.Position;
        var next = new Vec3((float)x, (float)y, (float)z);
        _ctx.Document.EditValue(Object.Section, "Set position", old, next, v => Object.Position = v);
        _ctx.Changes.Record("moved");
    }

    /// <summary>Lua: <c>obj:move(dx, dy, dz)</c> — translates the object (undoable).</summary>
    [ScriptDoc("Moves the object by (dx, dy, dz) metres.", Example = "o:move(0, 1, 0)")]
    public void Move(double dx, double dy, double dz)
    {
        Vec3 old = Object.Position;
        SetPos(old.X + dx, old.Y + dy, old.Z + dz);
    }

    /// <summary>Lua: <c>obj:set_name("…")</c> — renames the object (undoable).</summary>
    [ScriptDoc("Sets the script name (\"\" clears it).", Example = "o:set_name(\"door_trigger\")")]
    public void SetName(string name)
    {
        RequireEditable("object:set_name");
        string old = Object.ScriptName;
        string next = ScriptObjectNames.Stored(Object, name ?? string.Empty);
        _ctx.Document.EditValue(Object.Section, "Rename object", old, next, v => Object.ScriptName = v);
        _ctx.Changes.Record("renamed");
    }

    /// <summary>Lua: <c>obj:set_hidden(true|false)</c> — toggles visibility (undoable).</summary>
    [ScriptDoc("Hides or shows the object in the editor views (saved with the level; no effect in game).", Example = "o:set_hidden(true)")]
    public void SetHidden(bool hidden)
    {
        bool old = Object.Hidden;
        if (old == hidden)
        {
            return;
        }

        _ctx.Document.EditValue(Object.Section, hidden ? "Hide object" : "Show object", old, hidden, v => Object.Hidden = v);
        _ctx.Changes.Record("visibility");
    }

    /// <summary>Lua: <c>obj:select(additive?)</c> — adds the object to the selection.</summary>
    [ScriptDoc("Selects the object; additive = true keeps the existing selection (default replaces it).", Example = "o:select(true)")]
    public void Select(bool additive = false) => _ctx.Document.Select(Object, additive);

    /// <summary>Lua: <c>obj:links()</c> — the UIDs this object links to (empty when it has no link list).</summary>
    [ScriptDoc("The UIDs this object links to, as number[] (empty when it has no links or cannot link).", Example = "for _, uid in ipairs(t:links()) do log.info(uid) end")]
    public int[] Links() => LinkModel.LinksOf(Object)?.ToArray() ?? Array.Empty<int>();

    /// <summary>Lua: <c>obj:link_to(target_uid)</c> — adds a link to another object by UID (undoable).</summary>
    [ScriptDoc("Adds a link from this object to the object with this UID; returns false (and logs why) when the link is not allowed.", Example = "t:link_to(42)")]
    public bool LinkTo(int targetUid)
    {
        LinkResult r = _ctx.Links.AddLink(Object, targetUid);
        if (r.Ok)
        {
            _ctx.Changes.Record("linked");
        }
        else
        {
            _ctx.Log.Warn($"link {Object.Uid} → {targetUid}: {r.Message}");
        }

        return r.Ok;
    }

    /// <summary>Lua: <c>obj:unlink(target_uid)</c> — removes a link (undoable).</summary>
    [ScriptDoc("Removes the link to this UID; returns true when a link was removed.", Example = "t:unlink(42)")]
    public bool Unlink(int targetUid)
    {
        bool ok = _ctx.Links.RemoveLink(Object, targetUid);
        if (ok)
        {
            _ctx.Changes.Record("unlinked");
        }

        return ok;
    }

    /// <summary>Lua: <c>obj:delete()</c> — deletes the object (undoable, one entry). Destructive.</summary>
    [ScriptDoc("Deletes the object (destructive: needs confirmation or --@allow-destructive; refused in Dry-Run).", Example = "o:delete()")]
    public void Delete()
    {
        RequireEditable("object:delete");
        _ctx.RequireDestructive($"delete object {Object.Uid} ({Object.DisplayName})");
        _ctx.Level.DeleteObjects(new[] { Object });
    }

    // ---- V2: generic fields ----------------------------------------------

    /// <summary>The bound generic fields of this object (pseudo-fields, then the kind's schema).</summary>
    internal ScriptFieldSet FieldSet()
    {
        RequireLive("object");
        return ScriptFieldCatalog.ForObject(_ctx, Object);
    }

    [ScriptDoc("The value of a field by key, label or model path: enums are option names, colours {r,g,b,a}, vectors vec; nil when it does not apply.", Example = "print(t:get(\"sphere_radius\"))", Since = 2)]
    public object? Get(string name) => FieldSet().Get("object:get", name);

    [ScriptDoc("Sets one field (validated first; wrong types, ranges and read-only fields are errors; equal values change nothing). Returns the object.", Example = "t:set(\"resets_after\", 2)", Since = 2)]
    public ScriptObjectHandle Set(string name, object? value)
    {
        FieldSet().Set("object:set", name, value);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Sets several fields from {key = value, …}, applied in schema order (so shape is set before box_width). Returns the object.", Example = "t:set_many{shape = \"Box\", box_width = 4, box_height = 3, box_depth = 4}", Since = 2)]
    public ScriptObjectHandle SetMany(ScriptArgs values)
    {
        FieldSet().SetMany("object:set_many", values);
        _ctx.Changes.Record("edited");
        return this;
    }

    [ScriptDoc("Every field of this object (field_info[]): name, hidden, pos, class, uid, kind, then the kind's own fields.", Example = "for _, f in ipairs(o:fields()) do print(f.name, f.type) end", Since = 2)]
    public ScriptFieldInfo[] Fields() => FieldSet().Infos();

    [ScriptDoc("A table {key = value} of every field that currently has a value.", Example = "local p = o:props() print(p.pos.x)", Since = 2)]
    public Dictionary<string, object?> Props() => FieldSet().Props();

    // ---- V2: rotation -----------------------------------------------------

    [ScriptDoc("The orientation (rotation record, degrees), or nil when this object has none (sphere trigger, ambient sound, …).", Example = "if o.rot then print(o.rot.yaw) end", Since = 2)]
    public ScriptRotation? Rot
    {
        get
        {
            RequireLive("object.rot");
            return ObjectRotation.TryGetOrientation(Object.Model, out Mat3 m, out _) ? new ScriptRotation(m) : null;
        }
    }

    [ScriptDoc("Sets the orientation from {yaw=, pitch=, roll=} degrees or {forward=, up=}. Returns the object.", Example = "o:set_rot{yaw = 90}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.EulerRotation), nameof(ScriptOptionSets.BasisRotation))]
    public ScriptObjectHandle SetRot(ScriptArgs rot) => ApplyRot("object:set_rot", ScriptRotation.FromArgs("object:set_rot", "rot", rot));

    [ScriptDoc("Sets the orientation from a rotation record. Returns the object.", Example = "o:set_rot(ged.rot(180))", Since = 2)]
    public ScriptObjectHandle SetRot(ScriptRotation rot) => ApplyRot("object:set_rot", ScriptRotation.FromArgs("object:set_rot", "rot", rot));

    [ScriptDoc("Aims the object at a world point (metres), roll 0: its Forward axis, or its Up axis for a corona. Returns the object.", Example = "spawn:look_at(0, 1, 0)", Since = 2)]
    public ScriptObjectHandle LookAt(double x, double y, double z)
    {
        const string Api = "object:look_at";
        ScriptBrushHandle.RequireFinite(Api, x, y, z);
        RequireOrientation(Api);
        Vec3 dir = new Vec3((float)x, (float)y, (float)z).Sub(Object.Position);
        if (dir.LengthSquared() < 1e-12f)
        {
            throw new ScriptApiException($"{Api}: the target is the object's own position.", "Aim at a different point.");
        }

        return ApplyRot(Api, AimAt(dir.Normalized(), aimUp: Object.Model is AlpineCoronaObject));
    }

    // ---- V2: links ----------------------------------------------------------

    [ScriptDoc("Links to another object (undoable); false + a log warning when refused. Returns boolean.", Example = "trigger:link_to(door_event)", Since = 2)]
    public bool LinkTo(ScriptObjectHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireLive("object:link_to");
        return LinkTo(target.Uid);
    }

    [ScriptDoc("Links to a mover brush, stored as the mover's start keyframe (the UID the game follows); false + a warning when refused.", Example = "trigger:link_to(door_brush)", Since = 2)]
    public bool LinkTo(ScriptBrushHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireLive("object:link_to");
        (LevelObject? obj, string? why) = ResolveLinkTarget(target);
        if (obj is null)
        {
            _ctx.Log.Warn($"link {Object.Uid} → {target.Uid}: {why}");
            return false;
        }

        return LinkTo(obj.Uid);
    }

    [ScriptDoc("Links to a mover, stored as its start keyframe (the UID the game follows); false + a warning when refused.", Example = "trigger:link_to(level.find_mover(\"door1\"))", Since = 2)]
    public bool LinkTo(ScriptMoverHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireLive("object:link_to");
        return LinkTo(target.StartKeyframeUid("object:link_to"));
    }

    // ---- V2: provenance tags ------------------------------------------------

    [ScriptDoc("Sets (or replaces) a provenance tag; value must be a string. Returns the object.", Example = "o:tag(\"kit.name\", \"room1\")", Since = 2)]
    public ScriptObjectHandle Tag(string key, object? value)
    {
        const string Api = "object:tag";
        RequireLive(Api);
        string k = ScriptTags.Key(Api, key);
        ScriptTags.Merge(_ctx, Api, new[] { Uid }, new Dictionary<string, string> { [k] = ScriptTags.Value(Api, k, value) });
        _ctx.Changes.Record("tagged");
        return this;
    }

    [ScriptDoc("Removes a provenance tag (no-op when absent). Returns the object.", Example = "o:untag(\"kit.name\")", Since = 2)]
    public ScriptObjectHandle Untag(string key)
    {
        RequireLive("object:untag");
        ScriptTags.Remove(_ctx, new[] { Uid }, ScriptTags.Key("object:untag", key));
        return this;
    }

    [ScriptDoc("The object's provenance tags as a table {key = value} (empty when none).", Example = "print(o:tags()[\"ai.session\"])", Since = 2)]
    public Dictionary<string, object?> Tags()
    {
        RequireLive("object:tags");
        return ScriptTags.ToLua(_ctx.Metadata.Tags(Uid));
    }

    [ScriptDoc("Links to each target (handles or UIDs; a list a kit function returns, such as kit.spawn_points', works as it is) and reports what was stored and what was refused. Returns link_report.", Example = "local r = trigger:link_many{door, light1, 42}", Since = 2)]
    public ScriptLinkReport LinkMany(ScriptArgs targets)
    {
        const string Api = "object:link_many";
        RequireLive(Api);
        RequireOriginator(Api);
        var linked = new List<int>();
        var rejected = new List<ScriptLinkRejection>();
        foreach (object? item in TargetList(Api, targets))
        {
            (int uid, LevelObject? target, string? why) = ResolveItem(Api, item);
            if (target is null)
            {
                rejected.Add(new ScriptLinkRejection(uid, why!));
                continue;
            }

            int stored = MoverLinkResolver.ResolveTarget(_ctx.Document, target.Uid);
            LinkResult r = _ctx.Links.LinkOneToMany(Object, new[] { target });
            if (r.Ok)
            {
                linked.Add(stored);
            }
            else
            {
                rejected.Add(new ScriptLinkRejection(uid, r.Message));
            }
        }

        if (linked.Count > 0)
        {
            _ctx.Changes.Record("linked", linked.Count);
        }

        return new ScriptLinkReport(linked.ToArray(), rejected.ToArray());
    }

    [ScriptDoc("Makes each source (trigger/event/clutter/nav point handles or UIDs) link to this object. Returns link_report (linked = source UIDs).", Example = "door:back_link{trigger1, trigger2}", Since = 2)]
    public ScriptLinkReport BackLink(ScriptArgs sources)
    {
        const string Api = "object:back_link";
        RequireLive(Api);
        var linked = new List<int>();
        var rejected = new List<ScriptLinkRejection>();
        foreach (object? item in TargetList(Api, sources))
        {
            (int uid, LevelObject? source, string? why) = ResolveItem(Api, item);
            if (source is null)
            {
                rejected.Add(new ScriptLinkRejection(uid, why!));
                continue;
            }

            LinkResult r = _ctx.Links.BackLink(Object, new[] { source });
            if (r.Ok)
            {
                linked.Add(source.Uid);
            }
            else
            {
                rejected.Add(new ScriptLinkRejection(uid, r.Message));
            }
        }

        if (linked.Count > 0)
        {
            _ctx.Changes.Record("linked", linked.Count);
        }

        return new ScriptLinkReport(linked.ToArray(), rejected.ToArray());
    }

    [ScriptDoc("Removes every link from and to this object (undoable). Returns true when anything changed.", Example = "o:break_links()", Since = 2)]
    public bool BreakLinks()
    {
        RequireLive("object:break_links");
        bool changed = _ctx.Links.BreakAllLinks(new[] { Object });
        if (changed)
        {
            _ctx.Changes.Record("unlinked");
        }

        return changed;
    }

    [ScriptDoc("The UIDs of the objects that link to this one (number[]), in level order.", Example = "for _, uid in ipairs(door:links_in()) do print(uid) end", Since = 2)]
    public int[] LinksIn()
    {
        RequireLive("object:links_in");
        int uid = Object.Uid;
        return _ctx.Document.Objects
            .Where(o => o.Uid != uid && LinkModel.LinksOf(o)?.Contains(uid) == true)
            .Select(o => o.Uid)
            .ToArray();
    }

    public override string ToString() => $"{Object.Kind}#{Object.Uid} {Object.DisplayName}";

    // ---- Helpers ----------------------------------------------------------------------

    /// <summary>True while the object this handle names is still in the level (the same model, not a later object reusing its UID).</summary>
    internal bool IsLive => (_ctx.Document.FindByUid(Object.Uid) is { } cur && ReferenceEquals(cur.Model, Object.Model))
        || _ctx.Document.Objects.Any(o => ReferenceEquals(o.Model, Object.Model));

    /// <summary>Refuses an edit of a read-only object (a terrain, or an object of an Alpine chunk kept raw; see <see cref="LevelObject.IsReadOnly"/>).</summary>
    private void RequireEditable(string api)
    {
        if (Object.IsReadOnly)
        {
            throw new ScriptApiException(
                $"{api}: {Object.Kind} {Object.Uid} is read-only — its chunk is in a layout Glacier keeps exactly as loaded.",
                "Read it with get/props; edit it in Alpine RED.");
        }
    }

    /// <summary>Refuses to act on a deleted object (its handle would otherwise edit a detached model).</summary>
    private void RequireLive(string api)
    {
        if (!IsLive)
        {
            throw new ScriptApiException($"{api}: object {Object.Uid} no longer exists.", "It was deleted earlier in the run.");
        }
    }


    private void RequireOrientation(string api)
    {
        RequireLive(api);
        if (!ObjectRotation.TryGetOrientation(Object.Model, out _, out string? hint))
        {
            throw new ScriptApiException($"{api}: {Object.Kind} objects have no orientation.", hint);
        }
    }

    private ScriptObjectHandle ApplyRot(string api, Mat3 rotation)
    {
        RequireEditable(api);
        RequireOrientation(api);
        Mat3 next = rotation.Orthonormalize();
        object model = Object.Model;
        object? oldRaw = ObjectRotation.GetRaw(model);
        if (oldRaw is Mat3 cur && cur.Equals(next))
        {
            return this;
        }

        RflSection section = Object.Section;
        _ctx.Document.Undo.Execute(new RelayCommand("Rotate object",
            () =>
            {
                ObjectRotation.Set(model, next);
                section.Dirty = true;
            },
            () =>
            {
                ObjectRotation.SetRaw(model, oldRaw);
                section.Dirty = true;
            }));
        _ctx.Changes.Record("rotated");
        return this;
    }

    /// <summary>
    /// The orientation aiming the kind's aim axis along <paramref name="dir"/> with zero roll
    ///: Forward aim — F = dir, R = normalize(+Y × F) (fallback +X when vertical),
    /// U = F × R; Up aim (corona) — U = dir, F = normalize(+Z − U·U.z) (fallback +X), R = U × F.
    /// </summary>
    internal static Mat3 AimAt(Vec3 dir, bool aimUp)
    {
        if (!aimUp)
        {
            Vec3 f = dir;
            Vec3 r = new Vec3(0, 1, 0).Cross(f);
            r = r.LengthSquared() < 1e-10f ? new Vec3(1, 0, 0) : r.Normalized();
            Vec3 u = f.Cross(r).Normalized();
            return new Mat3(f, r, u);
        }

        Vec3 up = dir;
        Vec3 fw = new Vec3(0, 0, 1).Sub(up.Scale(up.Z));
        fw = fw.LengthSquared() < 1e-10f ? new Vec3(1, 0, 0) : fw.Normalized();
        Vec3 right = up.Cross(fw).Normalized();
        return new Mat3(fw, right, up);
    }

    private void RequireOriginator(string api)
    {
        if (LinkModel.LinksOf(Object) is null)
        {
            throw new ScriptApiException($"{api}: a {Object.Kind} cannot originate links.", LinkRules.OriginatorMessage);
        }
    }

    private static IReadOnlyList<object?> TargetList(string api, ScriptArgs? list)
    {
        if (list is null)
        {
            throw new ScriptApiException($"{api}: a list of handles or UIDs is required.", "For example {door, 42}.");
        }

        // A list the kit returns may carry named fields beside its items (kit.spawn_points' report): the items are
        // the list. Named fields alone are a mistake (options where a list belongs).
        if (list.Named.Count > 0 && list.Items.Count == 0)
        {
            throw new ScriptApiException($"{api}: expected a list of handles or UIDs, not named options.", "For example {door, 42}.");
        }

        return list.Items;
    }

    /// <summary>Resolves a link list item (UID, object handle, brush handle) to a level object.</summary>
    private (int Uid, LevelObject? Target, string? Why) ResolveItem(string api, object? item)
    {
        switch (item)
        {
            case ScriptObjectHandle o:
                return (o.Uid, o.Object, null);
            case ScriptBrushHandle b:
            {
                (LevelObject? obj, string? why) = ResolveLinkTarget(b);
                return (b.Uid, obj, why);
            }

            case ScriptMoverHandle m:
            {
                int start = m.StartKeyframeUid(api);
                LevelObject? kf = _ctx.Document.FindByUid(start);
                return (start, kf, kf is null ? $"No object with UID {start}." : null);
            }

            case double d:
            {
                int uid = ScriptArgs.ToInt(api, "uid", d);
                int stored = MoverLinkResolver.ResolveTarget(_ctx.Document, uid);
                LevelObject? obj = _ctx.Document.FindByUid(stored) ?? _ctx.Document.FindByUid(uid);
                return (uid, obj, obj is null ? $"No object with UID {uid}." : null);
            }

            default:
                throw new ScriptApiException($"{api}: list items must be handles or UIDs (got {ScriptArgs.LuaTypeOf(item)}).");
        }
    }

    /// <summary>A brush is a link target only as a mover member: resolves to the mover's start keyframe object.</summary>
    private (LevelObject? Target, string? Why) ResolveLinkTarget(ScriptBrushHandle brush)
    {
        int stored = MoverLinkResolver.ResolveTarget(_ctx.Document, brush.Uid);
        if (stored == brush.Uid)
        {
            return (null, $"Brush {brush.Uid} is not part of a mover; only objects and mover brushes can be linked.");
        }

        return _ctx.Document.FindByUid(stored) is { } kf
            ? (kf, null)
            : (null, $"No object with UID {stored}.");
    }
}
