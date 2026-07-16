using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>selection</c> global (plan §5.3). Because scripts run outside a viewport mode, the
/// runner uses the document's select primitives directly (a documented, deliberate exception to
/// the interactive <c>SelectionRouter</c> gating — §5.3 note), never a bypass of round-trip safety.
/// </summary>
public sealed class ScriptSelection
{
    private readonly ScriptContext _ctx;

    internal ScriptSelection(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>selection.count</c> — number of selected objects.</summary>
    public int Count => _ctx.Document.Selection.Count;

    /// <summary>Lua: <c>selection.objects</c> — handles for the current selection.</summary>
    public ScriptObjectHandle[] Objects =>
        _ctx.Document.Selection.Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    /// <summary>Lua: <c>selection.all()</c> — a query over the current selection (chainable).</summary>
    public ScriptObjectQuery All() => new(_ctx, _ctx.Document.Selection.ToList());

    /// <summary>Lua: <c>selection.where(function(o) … end)</c> — a query over ALL objects filtered by a predicate.</summary>
    public ScriptObjectQuery Where(ScriptObjectPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ScriptObjectQuery(_ctx, _ctx.Document.Objects.Where(o => predicate(new ScriptObjectHandle(_ctx, o))));
    }

    /// <summary>Lua: <c>selection.of_kind("Light")</c> — a query over all objects of one kind.</summary>
    public ScriptObjectQuery OfKind(string kind)
    {
        LevelObjectKind k = ScriptLevel.ParseKind(kind);
        return new ScriptObjectQuery(_ctx, _ctx.Document.Objects.Where(o => o.Kind == k).ToList());
    }

    /// <summary>Lua: <c>selection.clear()</c>.</summary>
    public void Clear() => _ctx.Document.ClearSelection();

    /// <summary>Lua: <c>selection.invert()</c>.</summary>
    public void Invert() => _ctx.Document.InvertSelection();

    /// <summary>Lua: <c>selection.select_all()</c>.</summary>
    public void SelectAll() => _ctx.Document.SelectAll();

    /// <summary>Lua: <c>selection.by_uid(uid)</c> — selects one object; returns its handle or nil.</summary>
    public ScriptObjectHandle? ByUid(int uid)
    {
        LevelObject? o = _ctx.Document.SelectByUid(uid);
        return o is null ? null : new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>Lua: <c>selection.set(objects)</c> — replaces selection with the given handle array.</summary>
    public void Set(ScriptObjectHandle[]? objects) => Apply(objects, additive: false);

    /// <summary>Lua: <c>selection.add(objects)</c> — adds the given handles to the selection.</summary>
    public void Add(ScriptObjectHandle[]? objects) => Apply(objects, additive: true);

    private void Apply(ScriptObjectHandle[]? objects, bool additive)
    {
        IEnumerable<LevelObject> models = (objects ?? Array.Empty<ScriptObjectHandle>()).Select(h => h.Object);
        _ctx.Document.SelectMany(models, additive);
    }

    /// <summary>Lua: <c>selection.delete()</c> — deletes the selected objects (one undo node). Destructive.</summary>
    public int Delete()
    {
        var targets = _ctx.Document.Selection.Where(o => o.CanRemove).ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        _ctx.RequireDestructive($"delete {targets.Count} selected object(s)");
        _ctx.Document.DeleteSelection("Script delete selection");
        _ctx.Changes.Record("deleted", targets.Count);
        return targets.Count;
    }
}
