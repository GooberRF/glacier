using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>selection</c> global. Because scripts run outside a viewport mode, the
/// runner uses the document's select primitives directly (a documented, deliberate exception to
/// the interactive <c>SelectionRouter</c> gating), never a bypass of round-trip safety.
/// </summary>
public sealed class ScriptSelection
{
    private readonly ScriptContext _ctx;

    internal ScriptSelection(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>selection.count</c> — number of selected objects.</summary>
    [ScriptDoc("The number of selected level objects (brushes: selection.brushes).", Example = "if selection.count == 0 then return end")]
    public int Count => _ctx.Document.Selection.Count;

    /// <summary>Lua: <c>selection.objects</c> — handles for the current selection.</summary>
    [ScriptDoc("The selected level objects as object[].", Example = "for _, o in ipairs(selection.objects) do log.info(o.name) end")]
    public ScriptObjectHandle[] Objects =>
        _ctx.Document.Selection.Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    /// <summary>Lua: <c>selection.all()</c> — a query over the current selection (chainable).</summary>
    [ScriptDoc("A chainable object_query over the current object selection.", Example = "selection.all():move(0, 1, 0)")]
    public ScriptObjectQuery All() => new(_ctx, _ctx.Document.Selection.ToList());

    /// <summary>Lua: <c>selection.where(function(o) … end)</c> — a query over ALL objects filtered by a predicate.</summary>
    [ScriptDoc("An object_query over ALL level objects for which fn(object) returns true (not limited to the selection).", Example = "selection.where(function(o) return o.kind == \"Light\" end):select()")]
    public ScriptObjectQuery Where(ScriptObjectPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ScriptObjectQuery(_ctx, _ctx.Document.Objects.Where(o => predicate(new ScriptObjectHandle(_ctx, o))));
    }

    /// <summary>Lua: <c>selection.of_kind("Light")</c> — a query over all objects of one kind.</summary>
    [ScriptDoc("An object_query over all level objects of one kind (e.g. \"Light\").", Example = "selection.of_kind(\"Trigger\"):select()")]
    public ScriptObjectQuery OfKind(string kind)
    {
        LevelObjectKind k = ScriptLevel.ParseKind(kind, "selection.of_kind");
        return new ScriptObjectQuery(_ctx, _ctx.Document.Objects.Where(o => o.Kind == k).ToList());
    }

    /// <summary>Lua: <c>selection.clear()</c>.</summary>
    [ScriptDoc("Deselects every object.", Example = "selection.clear()")]
    public void Clear() => _ctx.Document.ClearSelection();

    /// <summary>Lua: <c>selection.invert()</c>.</summary>
    [ScriptDoc("Inverts the object selection.", Example = "selection.invert()")]
    public void Invert() => _ctx.Document.InvertSelection();

    /// <summary>Lua: <c>selection.select_all()</c>.</summary>
    [ScriptDoc("Selects every level object.", Example = "selection.select_all()")]
    public void SelectAll() => _ctx.Document.SelectAll();

    /// <summary>Lua: <c>selection.by_uid(uid)</c> — selects one object; returns its handle or nil.</summary>
    [ScriptDoc("Selects the object with this UID (replacing the selection) and returns it, or nil when there is none.", Example = "selection.by_uid(42)")]
    public ScriptObjectHandle? ByUid(int uid)
    {
        LevelObject? o = _ctx.Document.SelectByUid(uid);
        return o is null ? null : new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>Lua: <c>selection.set(objects)</c> — replaces selection with the given handle array.</summary>
    [ScriptDoc("Replaces the object selection with the given object[].", Example = "selection.set(level.objects_of(\"Light\"))")]
    public void Set(ScriptObjectHandle[]? objects) => Apply(objects, additive: false);

    /// <summary>Lua: <c>selection.add(objects)</c> — adds the given handles to the selection.</summary>
    [ScriptDoc("Adds the given object[] to the object selection.", Example = "selection.add(level.objects_of(\"Light\"))")]
    public void Add(ScriptObjectHandle[]? objects) => Apply(objects, additive: true);

    private void Apply(ScriptObjectHandle[]? objects, bool additive)
    {
        IEnumerable<LevelObject> models = (objects ?? Array.Empty<ScriptObjectHandle>()).Select(h => h.Object);
        _ctx.Document.SelectMany(models, additive);
    }

    [ScriptDoc("The selected brushes in CSG time order (brush[]).", Example = "for _, b in ipairs(selection.brushes) do b:set_detail(true) end", Since = 2)]
    public ScriptBrushHandle[] Brushes
    {
        get
        {
            var selected = new HashSet<int>(_ctx.Brushes.SelectedBrushes);
            return _ctx.Brushes.Brushes.Where(b => selected.Contains(b.Uid))
                .Select(b => new ScriptBrushHandle(_ctx, b.Uid)).ToArray();
        }
    }

    [ScriptDoc("The selected faces, ordered by brush time order then face index (face[]).", Since = 2)]
    public ScriptFaceHandle[] Faces
    {
        get
        {
            var order = new Dictionary<int, int>();
            for (int i = 0; i < _ctx.Brushes.Brushes.Count; i++)
            {
                order[_ctx.Brushes.Brushes[i].Uid] = i;
            }

            return _ctx.Brushes.SelectedFaces
                .Where(f => order.ContainsKey(f.Brush))
                .OrderBy(f => order[f.Brush]).ThenBy(f => f.Face)
                .Select(f => new ScriptFaceHandle(_ctx, f.Brush, f.Face)).ToArray();
        }
    }

    [ScriptDoc("Replaces the brush selection with the given brushes (an empty list clears it).", Example = "selection.set_brushes(level.all_brushes().brushes)", Since = 2)]
    public void SetBrushes(ScriptBrushHandle[]? brushes)
    {
        var uids = (brushes ?? Array.Empty<ScriptBrushHandle>()).Select(b => b.Uid)
            .Where(u => _ctx.Brushes.FindBrush(u) is not null).ToList();
        _ctx.Brushes.SelectBrushes(uids, additive: false);
    }

    [ScriptDoc("Replaces the face selection with the given faces (an empty list clears it).", Example = "selection.set_faces(b:faces_facing(\"up\"))", Since = 2)]
    public void SetFaces(ScriptFaceHandle[]? faces)
    {
        var list = (faces ?? Array.Empty<ScriptFaceHandle>())
            .Where(f => _ctx.Brushes.FindBrush(f.BrushUid) is { } b && f.Index >= 0 && f.Index < b.Geometry.Faces.Count)
            .ToList();
        if (list.Count == 0)
        {
            _ctx.Brushes.RetainSelectionKinds(brushes: true, faces: false, vertices: true, edges: true);
            return;
        }

        bool additive = false;
        foreach (ScriptFaceHandle f in list)
        {
            _ctx.Brushes.SelectFace(f.BrushUid, f.Index, additive);
            additive = true;
        }
    }

    /// <summary>Lua: <c>selection.delete()</c> — deletes the selected objects (one undo node). Destructive.</summary>
    [ScriptDoc("Deletes the selected objects (destructive: needs confirmation or --@allow-destructive) and returns how many were deleted.", Example = "local n = selection.delete()")]
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
