using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>level</c> global: enumeration + lookup + placement over the open
/// <see cref="EditorDocument"/> (plan §5.3). Placement routes through the document's undoable
/// <c>PlaceObject</c>/<c>PlaceEvent</c>; deletion collapses into one undo entry.
/// </summary>
public sealed class ScriptLevel
{
    private readonly ScriptContext _ctx;

    internal ScriptLevel(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>level.count</c> — number of level objects.</summary>
    public int Count => _ctx.Document.Objects.Count;

    /// <summary>Lua: <c>level.brush_count</c> — number of brushes.</summary>
    public int BrushCount => _ctx.Brushes.Brushes.Count;

    /// <summary>Lua: <c>level.objects</c> — handles for every level object (iterate with <c>ipairs</c>).</summary>
    public ScriptObjectHandle[] Objects =>
        _ctx.Document.Objects.Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();

    /// <summary>Lua: <c>level.brushes</c> — handles for every brush.</summary>
    public ScriptBrushHandle[] Brushes =>
        _ctx.Brushes.Brushes.Select(b => new ScriptBrushHandle(_ctx, b.Uid)).ToArray();

    /// <summary>Lua: <c>level.objects_of("Light")</c> — objects of one kind.</summary>
    public ScriptObjectHandle[] ObjectsOf(string kind)
    {
        LevelObjectKind k = ParseKind(kind);
        return _ctx.Document.Objects.Where(o => o.Kind == k)
            .Select(o => new ScriptObjectHandle(_ctx, o)).ToArray();
    }

    /// <summary>Lua: <c>level.find_uid(n)</c> — the object with UID <paramref name="uid"/>, or nil.</summary>
    public ScriptObjectHandle? FindUid(int uid)
    {
        LevelObject? o = _ctx.Document.FindByUid(uid);
        return o is null ? null : new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>Lua: <c>level.find_brush(uid)</c> — the brush with UID <paramref name="uid"/>, or nil.</summary>
    public ScriptBrushHandle? FindBrush(int uid) =>
        _ctx.Brushes.FindBrush(uid) is null ? null : new ScriptBrushHandle(_ctx, uid);

    /// <summary>Lua: <c>level.place("light", x, y, z [, class_name])</c> — places a new object (undoable).</summary>
    public ScriptObjectHandle Place(string kind, double x, double y, double z, string? className = null)
    {
        LevelObjectKind k = ParseKind(kind);
        LevelObject? o = _ctx.Document.PlaceObject(k, new Vec3((float)x, (float)y, (float)z), className)
            ?? throw new ScriptApiException($"Could not place a '{kind}'.", "This kind may not be placeable via scripts.");
        _ctx.Changes.Record("placed");
        return new ScriptObjectHandle(_ctx, o);
    }

    /// <summary>Lua: <c>level.place_event("class", x, y, z)</c> — places a new event (undoable).</summary>
    public ScriptObjectHandle PlaceEvent(string className, double x, double y, double z)
    {
        EventSchema schema = EventSchemaCatalog.Find(className)
            ?? throw new ScriptApiException($"Unknown event class '{className}'.", "Check the class name against the event catalog.");
        LevelObject? o = _ctx.Document.PlaceEvent(schema, new Vec3((float)x, (float)y, (float)z))
            ?? throw new ScriptApiException($"Could not place event '{className}'.");
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
        _ctx.Changes.Record("placed");
        return new ScriptBrushHandle(_ctx, uid);
    }

    /// <summary>Lua: <c>level.save([path])</c> — saves the document (destructive; see <c>ops.save</c>).</summary>
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

    /// <summary>Parses a kind name (case-insensitive) to <see cref="LevelObjectKind"/>.</summary>
    internal static LevelObjectKind ParseKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ScriptApiException("A kind name is required.");
        }

        foreach (LevelObjectKind k in Enum.GetValues<LevelObjectKind>())
        {
            if (string.Equals(k.ToString(), kind, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }

        throw new ScriptApiException($"Unknown object kind '{kind}'.",
            $"Valid kinds: {string.Join(", ", Enum.GetNames<LevelObjectKind>())}");
    }
}
