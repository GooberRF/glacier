using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one <see cref="LevelObject"/> (plan §5.3). Reads project the underlying
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
    public int Uid => Object.Uid;

    /// <summary>Lua: <c>obj.kind</c> — the object category ("Light", "Mover", "Entity", …).</summary>
    public string Kind => Object.Kind.ToString();

    /// <summary>Lua: <c>obj.name</c> — the script name (empty when unnamed).</summary>
    public string Name => Object.ScriptName;

    /// <summary>Lua: <c>obj.class_name</c> — the class/type name where applicable.</summary>
    public string ClassName => Object.ClassName;

    /// <summary>Lua: <c>obj.display_name</c> — name, else class, else "Kind uid".</summary>
    public string DisplayName => Object.DisplayName;

    /// <summary>Lua: <c>obj.hidden</c> — whether the object is hidden.</summary>
    public bool Hidden => Object.Hidden;

    /// <summary>Lua: <c>obj.pos</c> — the position vector (read; use <c>set_pos</c>/<c>move</c> to change).</summary>
    public Vec3 Pos => Object.Position;

    /// <summary>Lua: <c>obj.x</c>.</summary>
    public double X => Object.Position.X;

    /// <summary>Lua: <c>obj.y</c>.</summary>
    public double Y => Object.Position.Y;

    /// <summary>Lua: <c>obj.z</c>.</summary>
    public double Z => Object.Position.Z;

    /// <summary>Lua: <c>obj.selected</c> — whether the object is in the current selection.</summary>
    public bool Selected => _ctx.Document.IsSelected(Object);

    /// <summary>Lua: <c>obj:set_pos(x, y, z)</c> — moves the object (undoable).</summary>
    public void SetPos(double x, double y, double z)
    {
        Vec3 old = Object.Position;
        var next = new Vec3((float)x, (float)y, (float)z);
        _ctx.Document.EditValue(Object.Section, "Set position", old, next, v => Object.Position = v);
        _ctx.Changes.Record("moved");
    }

    /// <summary>Lua: <c>obj:move(dx, dy, dz)</c> — translates the object (undoable).</summary>
    public void Move(double dx, double dy, double dz)
    {
        Vec3 old = Object.Position;
        SetPos(old.X + dx, old.Y + dy, old.Z + dz);
    }

    /// <summary>Lua: <c>obj:set_name("…")</c> — renames the object (undoable).</summary>
    public void SetName(string name)
    {
        string old = Object.ScriptName;
        string next = name ?? string.Empty;
        _ctx.Document.EditValue(Object.Section, "Rename object", old, next, v => Object.ScriptName = v);
        _ctx.Changes.Record("renamed");
    }

    /// <summary>Lua: <c>obj:set_hidden(true|false)</c> — toggles visibility (undoable).</summary>
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
    public void Select(bool additive = false) => _ctx.Document.Select(Object, additive);

    /// <summary>Lua: <c>obj:links()</c> — the UIDs this object links to (empty when it has no link list).</summary>
    public int[] Links() => LinkModel.LinksOf(Object)?.ToArray() ?? Array.Empty<int>();

    /// <summary>Lua: <c>obj:link_to(target_uid)</c> — adds a link to another object by UID (undoable).</summary>
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
    public void Delete()
    {
        _ctx.RequireDestructive($"delete object {Object.Uid} ({Object.DisplayName})");
        _ctx.Level.DeleteObjects(new[] { Object });
    }

    public override string ToString() => $"{Object.Kind}#{Object.Uid} {Object.DisplayName}";
}
