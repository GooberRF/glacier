using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;

namespace Ged.Core.Packaging;

/// <summary>One place a texture/asset is referenced in the open level.</summary>
public sealed record AssetUsage(DependencyKind Kind, string Description, string ReferencedAs, int? Uid);

/// <summary>
/// "Where used" lookup: scans the open level's brush + compiled faces, decals,
/// liquid surfaces, particle/bolt emitters, coronas, event bitmap references and
/// mesh-object texture overrides for uses of a given asset, matching by supercede
/// base name (so <c>metal01</c>, <c>metal01.tga</c> and <c>metal01.dds</c> all
/// match). Each hit carries the referencing object's UID for jump-to.
/// </summary>
public static class WhereUsed
{
    /// <summary>All usages of <paramref name="assetName"/> in <paramref name="rfl"/>.</summary>
    public static IReadOnlyList<AssetUsage> Find(RflFile rfl, string assetName, DependencyScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentException.ThrowIfNullOrEmpty(assetName);

        string wanted = SupercedeChain.GetBaseName(assetName);
        return DependencyScanner.Gather(rfl, options)
            .Where(r => string.Equals(SupercedeChain.GetBaseName(r.FileName), wanted, StringComparison.OrdinalIgnoreCase))
            .Select(r => new AssetUsage(r.Kind, r.Origin, r.FileName, r.Uid))
            .ToList();
    }

    /// <summary>True if <paramref name="assetName"/> is referenced anywhere in the level.</summary>
    public static bool IsUsed(RflFile rfl, string assetName, DependencyScanOptions? options = null) =>
        Find(rfl, assetName, options).Count > 0;

    /// <summary>The set of distinct texture base-names actually used by the level (for "Show Only Used").</summary>
    public static IReadOnlyCollection<string> UsedTextureBaseNames(RflFile rfl)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DependencyRef r in DependencyScanner.Gather(rfl))
        {
            if (IsTextureKind(r.Kind))
            {
                set.Add(SupercedeChain.GetBaseName(r.FileName));
            }
        }

        return set;
    }

    private static bool IsTextureKind(DependencyKind kind) => kind is
        DependencyKind.FaceTexture or DependencyKind.LiquidTexture or DependencyKind.DecalTexture or
        DependencyKind.ParticleBitmap or DependencyKind.BoltBitmap or DependencyKind.CoronaBitmap or
        DependencyKind.EventBitmap or DependencyKind.MeshObjectTexture or DependencyKind.GeomodTexture or
        DependencyKind.AtxDescriptor or DependencyKind.AtxFrame or DependencyKind.ClutterSkin or
        DependencyKind.EntitySkin;
}
