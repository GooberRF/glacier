using System;
using System.Collections.Concurrent;
using Ged.Core.Compiler;
using Ged.Core.IO.Tex;

namespace Ged.Core.Assets;

/// <summary>
/// VFS-backed provider of compile-time texture traits: resolves a texture name
/// through the supercede chain, decodes frame 0, and scans its alpha channel for
/// the invisible / has-alpha / has-holes face bits (what RED derives when
/// compiling geometry). Results are cached per name; unresolvable names return
/// null so the compiler applies its name-based fallback. Thread-safe.
/// </summary>
public sealed class TextureTraitsCache
{
    private readonly AssetVfs _vfs;
    private readonly ConcurrentDictionary<string, TextureTraits?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TextureTraitsCache(AssetVfs vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
    }

    /// <summary>Traits for the texture, or null when it cannot be resolved/decoded.</summary>
    public TextureTraits? Get(string textureName)
    {
        if (string.IsNullOrEmpty(textureName))
        {
            return null;
        }

        return _cache.GetOrAdd(textureName, Compute);
    }

    /// <summary>Clears cached traits (after a VFS rescan / Reload Textures).</summary>
    public void Invalidate() => _cache.Clear();

    private TextureTraits? Compute(string name)
    {
        try
        {
            DecodedTexture? tex = _vfs.LoadTexture(name);
            return tex is null ? null : TextureTraits.FromImage(tex.Primary);
        }
        catch
        {
            return null; // corrupt/unsupported file: fall back to name heuristics
        }
    }
}
