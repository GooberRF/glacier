using System;
using System.Collections.Concurrent;

namespace Ged.Core.Assets;

/// <summary>
/// The pixel size of textures by name, read through the mounted texture library (the VFS supercede chain,
/// frame 0 of an animated texture), cached per name. It is the lookup texel-density work needs: mapping by
/// pixels per metre (a 128 px texture at 64 px/m spans 2 m, a 256 px one 4 m), and measuring the px/m of
/// existing faces. Unresolvable or undecodable names report null, so callers choose their own fallback
/// (<see cref="Editing.UvOps.DefaultTextureSize"/> for mapping). Thread-safe.
/// </summary>
public sealed class TextureSizeLookup
{
    private readonly AssetVfs? _vfs;
    private readonly ConcurrentDictionary<string, (int W, int H)?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A lookup over <paramref name="vfs"/>; with no library mounted every size is unknown.</summary>
    public TextureSizeLookup(AssetVfs? vfs)
    {
        _vfs = vfs;
    }

    /// <summary>True when a texture library is mounted (sizes can be known).</summary>
    public bool Available => _vfs is not null;

    /// <summary>The (width, height) in pixels of <paramref name="textureName"/>, or null when it does not resolve or decode.</summary>
    public (int W, int H)? Get(string? textureName)
    {
        if (_vfs is null || string.IsNullOrEmpty(textureName))
        {
            return null;
        }

        return _cache.GetOrAdd(textureName, Decode);
    }

    /// <summary>The size of <paramref name="textureName"/>, or <paramref name="fallback"/> when it is unknown.</summary>
    public (int W, int H) GetOrDefault(string? textureName, (int W, int H) fallback) => Get(textureName) ?? fallback;

    private (int W, int H)? Decode(string name)
    {
        try
        {
            return _vfs!.LoadTexture(name) is { Width: > 0, Height: > 0 } d ? (d.Width, d.Height) : null;
        }
        catch (Exception)
        {
            return null; // undecodable: unknown size
        }
    }
}
