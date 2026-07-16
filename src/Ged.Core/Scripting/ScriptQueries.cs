using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A lazily-materialized set of object handles supporting chained filtering and bulk mutation
/// (plan §5.9). The predicate is expressed in Lua but the *apply* runs as vectorized C# inside
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
    public int Count => _objects.Count;

    /// <summary>Lua: <c>q.objects</c> — the handles (iterate with <c>ipairs</c>).</summary>
    public ScriptObjectHandle[] Objects => _objects.Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    /// <summary>Lua: <c>q:get(i)</c> — the i-th handle (1-based), or nil.</summary>
    public ScriptObjectHandle? Get(int index) =>
        index >= 1 && index <= _objects.Count ? new ScriptObjectHandle(_ctx, _objects[index - 1]) : null;

    /// <summary>Lua: <c>q:where(function(o) … end)</c> — narrows the set.</summary>
    public ScriptObjectQuery Where(ScriptObjectPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ScriptObjectQuery(_ctx, _objects.Where(o => predicate(new ScriptObjectHandle(_ctx, o))));
    }

    /// <summary>Lua: <c>q:each(function(o) … end)</c> — runs an action per object (mutations undoable).</summary>
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
    public ScriptObjectQuery Select()
    {
        _ctx.Document.SelectMany(_objects, additive: false);
        return this;
    }

    /// <summary>Lua: <c>q:add_to_selection()</c> — adds this set to the selection.</summary>
    public ScriptObjectQuery AddToSelection()
    {
        _ctx.Document.SelectMany(_objects, additive: true);
        return this;
    }

    /// <summary>Lua: <c>q:move(dx, dy, dz)</c> — translates every object (one undo node).</summary>
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
    public int Count => _uids.Count;

    /// <summary>Lua: <c>q.brushes</c> — the handles.</summary>
    public ScriptBrushHandle[] Brushes => _uids.Select(u => new ScriptBrushHandle(_ctx, u)).ToArray();

    /// <summary>Lua: <c>q:where(function(b) … end)</c>.</summary>
    public ScriptBrushQuery Where(ScriptBrushPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ScriptBrushQuery(_ctx, _uids.Where(u => predicate(new ScriptBrushHandle(_ctx, u))));
    }

    /// <summary>Lua: <c>q:each(function(b) … end)</c>.</summary>
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
    public ScriptBrushQuery Move(double dx, double dy, double dz)
    {
        var delta = new Vec3((float)dx, (float)dy, (float)dz);
        _ctx.Brushes.EditBrushes(_uids, "Move brushes", b =>
        {
            Editing.BrushTransform.Move(b, delta);
            return Editing.OpResult.Ok();
        });
        _ctx.Changes.Record("moved", _uids.Count);
        return this;
    }

    /// <summary>Lua: <c>q:set_texture("name")</c> — sets every face on every brush (one undo node).</summary>
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
        _ctx.Changes.Record("retextured", _uids.Count);
        return this;
    }

    /// <summary>Lua: <c>q:delete()</c> — deletes every brush (one undo node). Destructive.</summary>
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
}
