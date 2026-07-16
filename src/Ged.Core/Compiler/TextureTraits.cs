using Ged.Core.IO.Tex;

namespace Ged.Core.Compiler;

/// <summary>
/// Face-flag traits RED's compiler derives from a face's texture content:
/// a fully-transparent texture makes the face invisible; any transparency sets
/// has_alpha; punch-through (fully-transparent texels mixed with opaque) sets
/// has_holes. Verified against dm04: the authored cloud-slab faces carry
/// 0x0100/0x0110 while RED's compiled output carries 0x2108/0x1D8 — the extra
/// bits (IsInvisible / HasAlpha+HasHoles) come from the textures
/// (mtl_invisible02.tga, Sky_Blu01_06_A.vbm).
/// </summary>
public readonly record struct TextureTraits(bool IsInvisible, bool HasAlpha, bool HasHoles)
{
    public static readonly TextureTraits None = new(false, false, false);

    /// <summary>Scans a decoded image's alpha channel for the trait bits.</summary>
    public static TextureTraits FromImage(TextureImage image)
    {
        byte[] px = image.Pixels;
        bool anyZero = false;
        bool anyOpaque = false;
        bool anyTranslucent = false;
        for (int i = 3; i < px.Length; i += 4)
        {
            byte a = px[i];
            if (a == 0)
            {
                anyZero = true;
            }
            else if (a == 255)
            {
                anyOpaque = true;
            }
            else
            {
                anyTranslucent = true;
            }
        }

        bool invisible = anyZero && !anyOpaque && !anyTranslucent;
        bool hasAlpha = !invisible && (anyZero || anyTranslucent);
        bool hasHoles = !invisible && anyZero;
        return new TextureTraits(invisible, hasAlpha, hasHoles);
    }

    /// <summary>
    /// Name-only fallback when the texture content is unavailable (no VFS):
    /// RF's invisible-wall textures follow the *_invisibleNN naming convention.
    /// </summary>
    public static TextureTraits FromName(string textureName) =>
        textureName.Contains("invisible", System.StringComparison.OrdinalIgnoreCase)
            ? new TextureTraits(true, false, false)
            : None;
}
