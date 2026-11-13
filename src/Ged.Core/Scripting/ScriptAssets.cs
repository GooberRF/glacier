using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Core.Packaging;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>assets</c> global: asset existence/enumeration via the mounted
/// <c>AssetVfs</c>, texture-usage lookup via <c>WhereUsed</c>, and the headline vectorized
/// batch op — <c>replace_texture</c> — that expresses the predicate implicitly and runs the
/// mutation as one C# loop under a single undo node. Asset enumeration degrades
/// gracefully (empty results) when no VFS is mounted.
/// </summary>
public sealed class ScriptAssets
{
    private readonly ScriptContext _ctx;

    internal ScriptAssets(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>assets.available</c> — whether an asset library is mounted.</summary>
    [ScriptDoc("True when a game asset library (an RF install) is mounted; asset lookups find nothing without one.", Example = "if not assets.available then log.warn(\"no install mounted\") end")]
    public bool Available => _ctx.Assets is not null;

    /// <summary>Lua: <c>assets.exists("name.tga")</c> — true when the asset resolves (false if no VFS).</summary>
    [ScriptDoc("True when the mounted asset library has a file with this name (e.g. \"rock02.tga\").", Example = "assets.exists(\"rock02.tga\")")]
    public bool Exists(string name) => _ctx.Assets?.Exists(name) ?? false;

    /// <summary>Lua: <c>assets.textures()</c> — every texture name in the library (empty if no VFS).</summary>
    [ScriptDoc("Every texture name in the mounted asset library as string[] (empty without one); catalog.textures() filters and limits.", Example = "log.info(#assets.textures() .. \" textures\")")]
    public string[] Textures() => _ctx.Assets?.EnumerateTextures().ToArray() ?? Array.Empty<string>();

    /// <summary>Lua: <c>assets.used_textures()</c> — base names of textures the level references.</summary>
    [ScriptDoc("The base names of the textures the level references, as string[].", Example = "for _, t in ipairs(assets.used_textures()) do log.info(t) end")]
    public string[] UsedTextures() => WhereUsed.UsedTextureBaseNames(_ctx.Document.Rfl).ToArray();

    [ScriptDoc("Every texture the level uses as texture_info[], sorted by name: stock = every player has it; unresolved ones have exists = false. The portability check.",
        Example = "for _, t in ipairs(assets.texture_sources()) do if not t.stock then log.warn(t.name .. \" from \" .. tostring(t.source)) end end", Since = 2)]
    public ScriptTextureInfo[] TextureSources()
    {
        // The texture references WhereUsed.UsedTextureBaseNames counts, one per base name (the lowest
        // ordinal-ignore-case spelling the level uses wins, so the result is deterministic).
        var byBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DependencyRef r in DependencyScanner.Gather(_ctx.Document.Rfl))
        {
            if (!WhereUsed.IsTextureKind(r.Kind) || string.IsNullOrWhiteSpace(r.FileName))
            {
                continue;
            }

            string baseName = IO.Tex.SupercedeChain.GetBaseName(r.FileName);

            if (!byBase.TryGetValue(baseName, out string? seen) || Order(r.FileName, seen) < 0)
            {
                byBase[baseName] = r.FileName;
            }
        }

        AssetVfs? vfs = _ctx.Assets;
        return byBase.Values
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)
            .Select(n => new ScriptTextureInfo(n, vfs, vfs is null ? null : StockContent.Describe(vfs, n)))
            .ToArray();

        static int Order(string a, string b)
        {
            int c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
        }
    }

    /// <summary>Lua: <c>assets.is_used("tex")</c> — whether the level references an asset.</summary>
    [ScriptDoc("True when the level references this asset (texture, mesh, sound…) anywhere.", Example = "assets.is_used(\"rock02.tga\")")]
    public bool IsUsed(string name) => WhereUsed.IsUsed(_ctx.Document.Rfl, name);

    /// <summary>Lua: <c>assets.where_used_by("tex")</c> — every place the asset is referenced.</summary>
    [ScriptDoc("Every place the level references this asset, as asset_usage[] (kind, description, referenced_as, uid).", Example = "for _, u in ipairs(assets.where_used_by(\"rock02.tga\")) do log.info(u.description) end")]
    public ScriptAssetUsage[] WhereUsedBy(string name) =>
        WhereUsed.Find(_ctx.Document.Rfl, name)
            .Select(u => new ScriptAssetUsage(u.Kind.ToString(), u.Description, u.ReferencedAs, u.Uid ?? -1))
            .ToArray();

    /// <summary>Lua: <c>assets.brushes_using("tex")</c> — a brush query of brushes with a face on that texture.</summary>
    [ScriptDoc("A brush_query over the brushes with at least one face using this texture.", Example = "assets.brushes_using(\"rock02.tga\"):select()")]
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
    /// <c>new</c> across the whole level as ONE undo node (the vectorized mass-replace path).
    /// Returns the number of faces changed.
    /// </summary>
    [ScriptDoc("Replaces texture old with new on every brush face in the level (one undo step) and returns how many faces changed.", Example = "assets.replace_texture(\"rock02.tga\", \"rock03.tga\")")]
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

        foreach (int uid in targets)
        {
            _ctx.NoteBrushEdited(uid);
        }

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

/// <summary>A single asset-usage record surfaced by <c>assets.where_used_by</c>.</summary>
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
    [ScriptDoc("The dependency kind of the asset (\"Texture\", \"Mesh\", \"Sound\", …).")]
    public string Kind { get; }

    /// <summary>Lua: <c>usage.description</c> — a human-readable location.</summary>
    [ScriptDoc("A human-readable description of where the asset is referenced.")]
    public string Description { get; }

    /// <summary>Lua: <c>usage.referenced_as</c> — the exact reference string.</summary>
    [ScriptDoc("The reference string exactly as the level spells it.")]
    public string ReferencedAs { get; }

    /// <summary>Lua: <c>usage.uid</c> — the referencing object's UID, or -1.</summary>
    [ScriptDoc("The UID of the referencing brush or object, or -1 when the reference has no UID.")]
    public int Uid { get; }

    public override string ToString() => $"{Kind}: {Description}";
}
