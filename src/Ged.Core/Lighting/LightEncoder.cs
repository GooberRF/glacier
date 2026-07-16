using System;

namespace Ged.Core.Lighting;

/// <summary>
/// Float RGB → 24bpp byte encoding (RED FUN_004ace10): ×255, clamp ≥0, and the
/// overbright clamp when any channel exceeds 255 — proportional hue-preserving
/// (scale all by 255/max, RED's default) or per-channel (Alpine's no-clamp load
/// behaviour). Uses ftol truncation. 128 = neutral (buffer = white ambient×0.5).
/// </summary>
public static class LightEncoder
{
    /// <summary>Encodes one float RGB triplet to three bytes into <paramref name="dst"/> at <paramref name="offset"/>.</summary>
    public static void Encode(float r, float g, float b, bool proportional, byte[] dst, int offset)
    {
        (byte br, byte bg, byte bb) = Encode(r, g, b, proportional);
        dst[offset] = br;
        dst[offset + 1] = bg;
        dst[offset + 2] = bb;
    }

    /// <summary>Encodes one float RGB triplet (0..1+ range) to a byte triplet.</summary>
    public static (byte R, byte G, byte B) Encode(float r, float g, float b, bool proportional)
    {
        r = r < 0f ? 0f : r * 255f;
        g = g < 0f ? 0f : g * 255f;
        b = b < 0f ? 0f : b * 255f;
        float m = MathF.Max(r, MathF.Max(g, b));
        if (m > 255f)
        {
            if (proportional)
            {
                float sc = 255f / m;
                r *= sc; g *= sc; b *= sc;
            }
            else
            {
                r = MathF.Min(r, 255f); g = MathF.Min(g, 255f); b = MathF.Min(b, 255f);
            }
        }

        return ((byte)(int)r, (byte)(int)g, (byte)(int)b);
    }
}
