using Ged.Rendering.Rhi;

namespace Ged.Rendering.Graphics;

/// <summary>
/// Small procedurally generated fallback textures: a white diffuse (for
/// untextured faces), a neutral 128-grey lightmap (so the 2x combine is a
/// no-op), and a soft-disc glyph used for every billboard until real icon art
/// exists.
/// </summary>
internal sealed class DefaultTextures : IDisposable
{
    private readonly IGpuTexture _white;
    private readonly IGpuTexture _neutralLightmap;
    private readonly IGpuTexture _glyph;
    private IGpuTexture _icons;

    public DefaultTextures(IRenderDevice device)
    {
        _white = device.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 });
        _neutralLightmap = device.CreateTexture(1, 1, new byte[] { 128, 128, 128, 255 });
        _glyph = device.CreateTexture(GlyphSize, GlyphSize, BuildGlyph());
        _icons = device.CreateTexture(IconAtlas.Width, IconAtlas.Height, IconAtlas.Build());
    }

    /// <summary>Opaque white — the default diffuse for faces with no resolved texture.</summary>
    public IGpuTexture White => _white;

    /// <summary>Neutral 128-grey — bound as the lightmap for faces that have none.</summary>
    public IGpuTexture NeutralLightmap => _neutralLightmap;

    /// <summary>Soft filled disc used as the billboard mask.</summary>
    public IGpuTexture Glyph => _glyph;

    /// <summary>The original per-object-type icon atlas (white core + dark rim, coverage in alpha).</summary>
    public IGpuTexture Icons => _icons;

    /// <summary>Replaces the icon atlas texture (GED-drawn ⇄ RED-original composited).</summary>
    public void SetIcons(IRenderDevice device, byte[] rgba)
    {
        IGpuTexture next = device.CreateTexture(IconAtlas.Width, IconAtlas.Height, rgba);
        _icons.Dispose();
        _icons = next;
    }

    private const int GlyphSize = 32;

    private static byte[] BuildGlyph()
    {
        var pixels = new byte[GlyphSize * GlyphSize * 4];
        const float center = (GlyphSize - 1) / 2f;
        const float radius = GlyphSize / 2f;
        for (int y = 0; y < GlyphSize; y++)
        {
            for (int x = 0; x < GlyphSize; x++)
            {
                float dx = (x - center) / radius;
                float dy = (y - center) / radius;
                float d = MathF.Sqrt((dx * dx) + (dy * dy));
                // Solid inside 0.72, soft to the rim, plus a slightly brighter ring.
                float a = 1f - SmoothStep(0.72f, 1f, d);
                int i = ((y * GlyphSize) + x) * 4;
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Math.Clamp(a * 255f, 0f, 255f);
            }
        }

        return pixels;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    public void Dispose()
    {
        _icons.Dispose();
        _glyph.Dispose();
        _neutralLightmap.Dispose();
        _white.Dispose();
    }
}
