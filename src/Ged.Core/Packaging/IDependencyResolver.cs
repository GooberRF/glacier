using System;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;

namespace Ged.Core.Packaging;

/// <summary>
/// Resolves a level dependency reference to a concrete file in a mount, honouring
/// the per-kind lookup rules (texture supercede chain, mesh-extension probing,
/// sound/exact match). A null result means the file is missing from every mount.
/// The scanner is written against this interface so it can be unit-tested with a
/// synthetic resolver and driven live by <see cref="VfsDependencyResolver"/>.
/// </summary>
public interface IDependencyResolver
{
    /// <summary>Resolves a reference, or returns null when nothing matches.</summary>
    DependencyResolution? Resolve(DependencyKind kind, string fileName);
}

/// <summary>
/// The production resolver: resolves dependency references against a mounted
/// <see cref="AssetVfs"/>. Texture-class references use the supercede chain,
/// mesh-class references probe the real mesh extensions, and everything else
/// matches the exact name (sounds additionally probe a <c>.wav</c> sibling).
/// </summary>
public sealed class VfsDependencyResolver : IDependencyResolver
{
    private static readonly string[] MeshExtensions = { ".v3m", ".v3c", ".vcm", ".vfx" };

    private static readonly string[] AnimExtensions = { ".rfa", ".mvf" };

    private readonly AssetVfs _vfs;

    public VfsDependencyResolver(AssetVfs vfs) => _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));

    /// <summary>The broad lookup strategy a dependency kind uses to find its file.</summary>
    internal static RefClass ClassOf(DependencyKind kind) => kind switch
    {
        DependencyKind.FaceTexture => RefClass.Texture,
        DependencyKind.LiquidTexture => RefClass.Texture,
        DependencyKind.DecalTexture => RefClass.Texture,
        DependencyKind.ParticleBitmap => RefClass.Texture,
        DependencyKind.BoltBitmap => RefClass.Texture,
        DependencyKind.CoronaBitmap => RefClass.Texture,
        DependencyKind.EventBitmap => RefClass.Texture,
        DependencyKind.MeshObjectTexture => RefClass.Texture,
        DependencyKind.GeomodTexture => RefClass.Texture,
        DependencyKind.AtxDescriptor => RefClass.Texture,
        DependencyKind.AtxFrame => RefClass.Texture,
        DependencyKind.ClutterSkin => RefClass.Texture,
        DependencyKind.EntitySkin => RefClass.Texture,
        DependencyKind.MeshObject => RefClass.Mesh,
        DependencyKind.EventMesh => RefClass.Mesh,
        DependencyKind.ClutterMesh => RefClass.Mesh,
        DependencyKind.EntityMesh => RefClass.Mesh,
        DependencyKind.ItemMesh => RefClass.Mesh,
        DependencyKind.EventSound => RefClass.Sound,
        DependencyKind.AmbientSound => RefClass.Sound,
        DependencyKind.MoverSound => RefClass.Sound,
        DependencyKind.MeshAnimation => RefClass.Animation,
        DependencyKind.EventAnimation => RefClass.Animation,
        _ => RefClass.Exact,
    };

    public DependencyResolution? Resolve(DependencyKind kind, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string? resolved = ClassOf(kind) switch
        {
            RefClass.Texture => _vfs.ResolveTexture(fileName),
            RefClass.Mesh => ResolveMesh(fileName),
            RefClass.Sound => ResolveSound(fileName),
            RefClass.Animation => ResolveAnimation(fileName),
            _ => _vfs.Exists(fileName) ? fileName : null,
        };

        if (resolved is null)
        {
            return null;
        }

        IAssetSource? src = _vfs.FindSource(resolved);
        if (src is null)
        {
            return null;
        }

        string? loose = src is DirectoryAssetSource d ? d.GetFullPath(resolved) : null;
        long size = src.GetSize(resolved) ?? 0;
        string name = resolved;
        return new DependencyResolution(resolved, src.Kind, src.Description, loose, size,
            () => _vfs.ReadFile(name));
    }

    private string? ResolveMesh(string name)
    {
        if (_vfs.Exists(name) && HasMeshExtension(name))
        {
            return name;
        }

        string baseName = StripExtension(name);
        foreach (string ext in MeshExtensions)
        {
            string candidate = baseName + ext;
            if (_vfs.Exists(candidate))
            {
                return candidate;
            }
        }

        return _vfs.Exists(name) ? name : null;
    }

    private string? ResolveSound(string name)
    {
        if (_vfs.Exists(name))
        {
            return name;
        }

        string wav = StripExtension(name) + ".wav";
        return _vfs.Exists(wav) ? wav : null;
    }

    /// <summary>Resolves an animation reference: exact name, else probes the .rfa/.mvf extensions.</summary>
    private string? ResolveAnimation(string name)
    {
        if (_vfs.Exists(name))
        {
            return name;
        }

        string baseName = StripExtension(name);
        foreach (string ext in AnimExtensions)
        {
            string candidate = baseName + ext;
            if (_vfs.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasMeshExtension(string name)
    {
        string ext = System.IO.Path.GetExtension(name);
        return Array.Exists(MeshExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    internal enum RefClass
    {
        Texture,
        Mesh,
        Sound,
        Animation,
        Exact,
    }
}
