using System;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// A greyscale projection mask ("gobo") a light multiplies its contribution by during the bake
/// (item 4 — light cookies). Stored as a tightly-packed, top-origin, row-major luminance grid in
/// [0,1]. Built from a decoded RGBA image via Rec.601 luminance, sampled bilinearly with
/// clamp-to-edge addressing. Pure — no VFS/GPU dependency, so it is unit-testable and thread-safe.
/// </summary>
public sealed class LightCookie
{
    /// <summary>Blur levels above the raw image in the pre-blurred chain (item 6). Level 0 is raw.</summary>
    public const int BlurLevels = 4;

    // Pre-blurred mip-like chain: [0] = raw luminance, [1..BlurLevels] progressively blurred
    // (separable gaussian). Sharpness picks/interpolates a level at sample time. Built once at
    // construction ("at cookie load"); the resolver caches one LightCookie per file.
    private readonly float[][] _levels;

    public LightCookie(int width, int height, float[] luminance)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Cookie dimensions must be positive.");
        }

        if (luminance.Length < width * height)
        {
            throw new ArgumentException("Luminance buffer is smaller than width*height.");
        }

        Width = width;
        Height = height;

        _levels = new float[BlurLevels + 1][];
        float[] level0 = new float[width * height];
        Array.Copy(luminance, level0, level0.Length);
        _levels[0] = level0;
        for (int k = 1; k <= BlurLevels; k++)
        {
            _levels[k] = GaussianBlur(_levels[k - 1], width, height);
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Rec.601 luminance coefficient for red (0.299).</summary>
    public const float LumR = 0.299f;

    /// <summary>Rec.601 luminance coefficient for green (0.587).</summary>
    public const float LumG = 0.587f;

    /// <summary>Rec.601 luminance coefficient for blue (0.114).</summary>
    public const float LumB = 0.114f;

    /// <summary>
    /// Builds a cookie from a tightly-packed top-origin RGBA8 image (4 bytes/pixel), converting each
    /// pixel to a [0,1] greyscale value via Rec.601 luminance (already-grey images convert to their
    /// own value). The alpha channel is ignored — a cookie is a brightness mask.
    /// </summary>
    public static LightCookie FromRgba(int width, int height, byte[] rgba)
    {
        var lum = new float[width * height];
        int n = Math.Min(lum.Length, rgba.Length / 4);
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            lum[i] = ((LumR * rgba[o]) + (LumG * rgba[o + 1]) + (LumB * rgba[o + 2])) / 255f;
        }

        return new LightCookie(width, height, lum);
    }

    /// <summary>Bilinear sample of the RAW cookie at UV (top-left origin), clamp-to-edge; returns [0,1].</summary>
    public float SampleBilinearClamp(float u, float v) => SampleLevel(0, u, v);

    /// <summary>
    /// Sharpness at (and below) which sampling is raw bilinear — the pre-slider look (item 6,
    /// amendment 2). Above it, sampling blends bilinear → NEAREST for edges crisper than bilinear;
    /// below it, it blends bilinear → the blur chain.
    /// </summary>
    public const float BilinearSharpness = 0.75f;

    /// <summary>
    /// Sharpness-aware sample (item 6, re-tuned): <paramref name="sharpness"/> 1.0 = NEAREST (hard
    /// texel edges, sharper than the old raw bilinear), ~0.75 = the old raw bilinear look, below that
    /// = progressively blurred (the pre-blurred chain). Clamp-to-edge, returns [0,1].
    /// </summary>
    public float Sample(float u, float v, float sharpness)
    {
        float s = Math.Clamp(sharpness, 0f, 1f);
        if (s >= BilinearSharpness)
        {
            // Top band: raw bilinear (0.75) → nearest (1.0) for maximum crispness at this density.
            float t = (s - BilinearSharpness) / (1f - BilinearSharpness);
            float bilinear = SampleLevel(0, u, v);
            return t <= 0f ? bilinear : Lerp(bilinear, SampleNearest(0, u, v), t);
        }

        // Lower band: raw bilinear (0.75) → blurriest (0.0), through the pre-blurred chain.
        float lvl = ((BilinearSharpness - s) / BilinearSharpness) * BlurLevels;
        int lo = (int)MathF.Floor(lvl);
        if (lo >= BlurLevels)
        {
            return SampleLevel(BlurLevels, u, v);
        }

        float frac = lvl - lo;
        float a = SampleLevel(lo, u, v);
        return frac <= 0f ? a : Lerp(a, SampleLevel(lo + 1, u, v), frac);
    }

    private float SampleNearest(int level, float u, float v)
    {
        float[] lum = _levels[level];
        int x = (int)MathF.Round(Math.Clamp(u, 0f, 1f) * (Width - 1));
        int y = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * (Height - 1));
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return lum[(y * Width) + x];
    }

    private float SampleLevel(int level, float u, float v)
    {
        float[] lum = _levels[level];
        float fx = Math.Clamp(u, 0f, 1f) * (Width - 1);
        float fy = Math.Clamp(v, 0f, 1f) * (Height - 1);
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        x0 = Math.Clamp(x0, 0, Width - 1);
        y0 = Math.Clamp(y0, 0, Height - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        float top = Lerp(lum[(y0 * Width) + x0], lum[(y0 * Width) + x1], tx);
        float bot = Lerp(lum[(y1 * Width) + x0], lum[(y1 * Width) + x1], tx);
        return Lerp(top, bot, ty);
    }

    /// <summary>
    /// One pass of a separable 5-tap gaussian ([1,4,6,4,1]/16), clamp-to-edge addressing. Cumulative
    /// passes build progressively blurrier levels; deterministic (fixed kernel, pure float ops).
    /// </summary>
    private static float[] GaussianBlur(float[] src, int width, int height)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];

        // Horizontal.
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float s =
                    (src[row + ClampI(x - 2, width)] * 1f) +
                    (src[row + ClampI(x - 1, width)] * 4f) +
                    (src[row + x] * 6f) +
                    (src[row + ClampI(x + 1, width)] * 4f) +
                    (src[row + ClampI(x + 2, width)] * 1f);
                tmp[row + x] = s / 16f;
            }
        }

        // Vertical.
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                float s =
                    (tmp[(ClampI(y - 2, height) * width) + x] * 1f) +
                    (tmp[(ClampI(y - 1, height) * width) + x] * 4f) +
                    (tmp[(y * width) + x] * 6f) +
                    (tmp[(ClampI(y + 1, height) * width) + x] * 4f) +
                    (tmp[(ClampI(y + 2, height) * width) + x] * 1f);
                dst[(y * width) + x] = s / 16f;
            }
        }

        return dst;
    }

    private static int ClampI(int i, int n) => i < 0 ? 0 : (i >= n ? n - 1 : i);

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}

/// <summary>
/// Projects a light's <see cref="LightCookie"/> onto a texel world position, returning the [0,1]
/// brightness multiplier for that texel (item 4). SPOT lights sample the cookie as a 2D gobo across
/// the cone cross-section — U,V from the light's Right/Up axes (perpendicular to the spot
/// direction), scaled so the cookie exactly spans the OUTER cone at each distance. POINT lights use
/// a spherical (lat/long) mapping in the light's own frame. TUBE lights are skipped (return 1).
/// Lights with no cookie return 1 (no modulation).
/// </summary>
public static class CookieProjection
{
    public static float Mask(in EngineLight light, Vec3 p)
    {
        LightCookie? cookie = light.Cookie;
        if (cookie is null)
        {
            return 1f;
        }

        return light.Type switch
        {
            EngineLightType.Spot => SpotMask(light, cookie, p),
            EngineLightType.Point => PointMask(light, cookie, p),
            _ => 1f, // tube: skipped (documented)
        };
    }

    private static float SpotMask(in EngineLight light, LightCookie cookie, Vec3 p)
    {
        Vec3 fromLight = p.Sub(light.Position);
        float axial = fromLight.Dot(light.SpotAxis); // distance along the cone axis
        if (axial <= 1e-4f)
        {
            return 1f; // at/behind the light plane (outside the projected cone)
        }

        float coneRadius = axial * light.CookieConeTan; // half-width of the cone at this distance
        if (coneRadius <= 1e-6f)
        {
            return 1f;
        }

        float u = fromLight.Dot(light.CookieRight) / coneRadius; // -1..1 across the cone
        float vv = fromLight.Dot(light.CookieUp) / coneRadius;
        float cu = 0.5f + (0.5f * u);      // +Right → right of the cookie
        float cv = 0.5f - (0.5f * vv);     // +Up → top of the cookie (top-origin image)
        return cookie.Sample(cu, cv, light.CookieSharpness);
    }

    private static float PointMask(in EngineLight light, LightCookie cookie, Vec3 p)
    {
        Vec3 dir = p.Sub(light.Position);
        float len2 = dir.LengthSquared();
        if (len2 < 1e-12f)
        {
            return 1f;
        }

        dir = dir.Scale(1f / MathF.Sqrt(len2));
        float x = dir.Dot(light.CookieRight);
        float y = dir.Dot(light.CookieUp);
        float z = dir.Dot(light.SpotAxis);
        float lon = MathF.Atan2(x, z);                       // -π..π (longitude)
        float lat = MathF.Acos(Math.Clamp(y, -1f, 1f));      // 0..π (latitude from +Up)
        float cu = 0.5f + (lon / (2f * MathF.PI));
        float cv = lat / MathF.PI;
        return cookie.SampleBilinearClamp(cu, cv);
    }
}
