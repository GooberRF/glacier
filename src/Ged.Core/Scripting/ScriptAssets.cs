using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Core.Packaging;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>assets</c> global (plan §5.4): asset existence/enumeration via the mounted
/// <c>AssetVfs</c>, texture-usage lookup via <c>WhereUsed</c>, and the headline vectorized
/// batch op — <c>replace_texture</c> — that expresses the predicate implicitly and runs the
/// mutation as one C# loop under a single undo node (§5.9). Asset enumeration degrades
/// gracefully (empty results) when no VFS is mounted.
/// </summary>
public sealed class ScriptAssets
{
    private readonly ScriptContext _ctx;

    internal ScriptAssets(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>assets.available</c> — whether an asset library is mounted.</summary>
    public bool Available => _ctx.Assets is not null;

    /// <summary>Lua: <c>assets.exists("name.tga")</c> — true when the asset resolves (false if no VFS).</summary>
    public bool Exists(string name) => _ctx.Assets?.Exists(name) ?? false;

    /// <summary>Lua: <c>assets.textures()</c> — every texture name in the library (empty if no VFS).</summary>
    public string[] Textures() => _ctx.Assets?.EnumerateTextures().ToArray() ?? Array.Empty<string>();

    /// <summary>Lua: <c>assets.used_textures()</c> — base names of textures the level references.</summary>
    public string[] UsedTextures() => WhereUsed.UsedTextureBaseNames(_ctx.Document.Rfl).ToArray();

    /// <summary>Lua: <c>assets.is_used("tex")</c> — whether the level references an asset.</summary>
    public bool IsUsed(string name) => WhereUsed.IsUsed(_ctx.Document.Rfl, name);

    /// <summary>Lua: <c>assets.where_used("tex")</c> — every place the asset is referenced.</summary>
    public ScriptAssetUsage[] WhereUsedBy(string name) =>
        WhereUsed.Find(_ctx.Document.Rfl, name)
            .Select(u => new ScriptAssetUsage(u.Kind.ToString(), u.Description, u.ReferencedAs, u.Uid ?? -1))
            .ToArray();

    /// <summary>Lua: <c>assets.brushes_using("tex")</c> — a brush query of brushes with a face on that texture.</summary>
    public ScriptBrushQuery BrushesUsing(string texture)
    {
        var uids = new List<int>();
        foreach (Brush b in _ctx.Brushes.Brushes)
        {
            if (BrushUsesTexture(b, texture))
            {
                uids.Add(b.Uid);
            }
        }

        return new ScriptBrushQuery(_ctx, uids);
    }

    /// <summary>
    /// Lua: <c>assets.replace_texture("old", "new")</c> — remaps every face on <c>old</c> to
    /// <c>new</c> across the whole level as ONE undo node (the plan's 2.1 mass-replace / §5.9
    /// perf path). Returns the number of faces changed.
    /// </summary>
    public int ReplaceTexture(string oldTexture, string newTexture)
    {
        if (string.IsNullOrWhiteSpace(oldTexture) || string.IsNullOrWhiteSpace(newTexture))
        {
            throw new ScriptApiException("replace_texture needs a non-empty old and new texture name.");
        }

        var targets = _ctx.Brushes.Brushes.Where(b => BrushUsesTexture(b, oldTexture)).Select(b => b.Uid).ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        int changed = 0;
        _ctx.Brushes.EditBrushes(targets, $"Replace texture {oldTexture} → {newTexture}", b =>
        {
            Geometry g = b.Geometry;
            int newIdx = GeometryUtil.EnsureTexture(g, newTexture);
            foreach (Face f in g.Faces)
            {
                if (f.Texture >= 0 && f.Texture < g.Textures.Count &&
                    string.Equals(g.Textures[f.Texture], oldTexture, StringComparison.OrdinalIgnoreCase))
                {
                    f.Texture = newIdx;
                    changed++;
                }
            }

            return OpResult.Ok();
        });

        _ctx.Changes.Record("retextured faces", changed);
        return changed;
    }

    private static bool BrushUsesTexture(Brush b, string texture)
    {
        Geometry g = b.Geometry;
        foreach (Face f in g.Faces)
        {
            if (f.Texture >= 0 && f.Texture < g.Textures.Count &&
                string.Equals(g.Textures[f.Texture], texture, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A single asset-usage record surfaced by <c>assets.where_used</c> (plan §5.4).</summary>
public sealed class ScriptAssetUsage
{
    internal ScriptAssetUsage(string kind, string description, string referencedAs, int uid)
    {
        Kind = kind;
        Description = description;
        ReferencedAs = referencedAs;
        Uid = uid;
    }

    /// <summary>Lua: <c>usage.kind</c> — the dependency kind ("Texture", "Mesh", …).</summary>
    public string Kind { get; }

    /// <summary>Lua: <c>usage.description</c> — a human-readable location.</summary>
    public string Description { get; }

    /// <summary>Lua: <c>usage.referenced_as</c> — the exact reference string.</summary>
    public string ReferencedAs { get; }

    /// <summary>Lua: <c>usage.uid</c> — the referencing object's UID, or -1.</summary>
    public int Uid { get; }

    public override string ToString() => $"{Kind}: {Description}";
}
