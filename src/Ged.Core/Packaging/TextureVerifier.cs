using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;

namespace Ged.Core.Packaging;

/// <summary>A problem the "Verify All Textures" tool found with one referenced texture.</summary>
public enum TextureIssue
{
    /// <summary>The texture does not resolve from any mount.</summary>
    Missing,

    /// <summary>A dimension is not a power of two (the engine rescales, wasting VRAM / blurring).</summary>
    NonPowerOfTwo,

    /// <summary>A dimension exceeds the configured maximum.</summary>
    Oversize,
}

/// <summary>One "Verify All Textures" finding, with the level usages for jump-to.</summary>
public sealed record TextureVerifyResult(
    string TextureName,
    TextureIssue Issue,
    string Detail,
    IReadOnlyList<AssetUsage> Usages);

/// <summary>
/// The stock "Verify All Textures" tool, modernized: scans the level's texture
/// references, resolves each against the VFS, and reports missing files plus
/// non-power-of-two and oversize dimension warnings, each with the referencing
/// objects for jump-to. Runs on demand (decodes each distinct texture once).
/// </summary>
public static class TextureVerifier
{
    /// <summary>Verifies every distinct texture referenced by <paramref name="rfl"/>.</summary>
    public static IReadOnlyList<TextureVerifyResult> Verify(RflFile rfl, AssetVfs vfs, int maxDimension = 1024)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(vfs);

        var results = new List<TextureVerifyResult>();
        var byBase = DependencyScanner.Gather(rfl)
            .Where(r => IsTexture(r.Kind))
            .GroupBy(r => SupercedeChain.GetBaseName(r.FileName), StringComparer.OrdinalIgnoreCase);

        foreach (var group in byBase)
        {
            string name = group.Key;
            var usages = group
                .Select(r => new AssetUsage(r.Kind, r.Origin, r.FileName, r.Uid))
                .ToList();

            string? resolved = vfs.ResolveTexture(name);
            if (resolved is null)
            {
                results.Add(new TextureVerifyResult(name, TextureIssue.Missing, "not found in any mount", usages));
                continue;
            }

            // ATX descriptors are text; their frames are verified as their own references.
            if (resolved.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DecodedTexture? tex;
            try
            {
                tex = vfs.LoadTexture(name);
            }
            catch (Exception)
            {
                results.Add(new TextureVerifyResult(name, TextureIssue.Missing, $"'{resolved}' failed to decode", usages));
                continue;
            }

            if (tex is null)
            {
                results.Add(new TextureVerifyResult(name, TextureIssue.Missing, $"'{resolved}' failed to decode", usages));
                continue;
            }

            int w = tex.Primary.Width;
            int h = tex.Primary.Height;
            if (!IsPowerOfTwo(w) || !IsPowerOfTwo(h))
            {
                results.Add(new TextureVerifyResult(name, TextureIssue.NonPowerOfTwo, $"{w}x{h} ('{resolved}')", usages));
            }

            if (w > maxDimension || h > maxDimension)
            {
                results.Add(new TextureVerifyResult(name, TextureIssue.Oversize, $"{w}x{h} exceeds {maxDimension} ('{resolved}')", usages));
            }
        }

        return results;
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    private static bool IsTexture(DependencyKind kind) => kind is
        DependencyKind.FaceTexture or DependencyKind.LiquidTexture or DependencyKind.DecalTexture or
        DependencyKind.ParticleBitmap or DependencyKind.BoltBitmap or DependencyKind.CoronaBitmap or
        DependencyKind.EventBitmap or DependencyKind.MeshObjectTexture or DependencyKind.GeomodTexture;
}
