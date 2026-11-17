using System;
using System.Collections.Generic;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// A texture's alpha channel and its box-filtered mips, for alpha-tested light occlusion — reimplemented from Alpine
/// RED's behaviour (editor_patch/lightmap_alpha_masks.cpp:15-226): the source is box-reduced to at most 1024 per side,
/// each mip is the rounded mean of 2×2 texels, and <see cref="Coverage"/> averages a square footprint 2^lod texels
/// wide with 4×4 trilinear taps (one bilinear tap at lod 0), wrapping across repeats or clamped to a triangle's
/// texture rect when it maps inside one repeat.
/// </summary>
public sealed class AlpineAlphaMask
{
    private const int MaxMaskDim = 1024;
    private const int FootprintTaps = 4;

    private AlpineAlphaMask(List<(int W, int H, byte[] A)> levels) => Levels = levels;

    /// <summary>The mip chain, finest first.</summary>
    public IReadOnlyList<(int W, int H, byte[] A)> Levels { get; }

    /// <summary>A triangle's texture area within one repeat, in [0, 1].</summary>
    public readonly record struct Rect(float U0, float V0, float U1, float V1);

    /// <summary>Builds the mask of an RGBA8 image (row major), or null for an image without any transparency.</summary>
    public static AlpineAlphaMask? FromRgba(int w, int h, byte[] rgba)
    {
        if (w <= 0 || h <= 0 || rgba.Length < w * h * 4)
        {
            return null;
        }

        bool any = false;
        for (int i = 3; i < w * h * 4; i += 4)
        {
            if (rgba[i] != 255)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return null;
        }

        int factor = 1;
        while (w / factor > MaxMaskDim || h / factor > MaxMaskDim)
        {
            factor *= 2;
        }

        int bw = Math.Max(1, w / factor), bh = Math.Max(1, h / factor);
        var sums = new uint[bw * bh];
        var counts = new uint[bw * bh];
        for (int y = 0; y < h; y++)
        {
            int oy = Math.Min(y / factor, bh - 1);
            for (int x = 0; x < w; x++)
            {
                int o = (oy * bw) + Math.Min(x / factor, bw - 1);
                sums[o] += rgba[(((y * w) + x) * 4) + 3];
                counts[o]++;
            }
        }

        var a0 = new byte[bw * bh];
        for (int i = 0; i < a0.Length; i++)
        {
            a0[i] = (byte)(counts[i] != 0 ? (sums[i] + (counts[i] / 2)) / counts[i] : 255u);
        }

        var levels = new List<(int, int, byte[])> { (bw, bh, a0) };
        while (levels[^1].Item1 > 1 || levels[^1].Item2 > 1)
        {
            (int sw, int sh, byte[] s) = levels[^1];
            int dw = Math.Max(1, sw / 2), dh = Math.Max(1, sh / 2);
            var d = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = Math.Min(y * 2, sh - 1), y1 = Math.Min((y * 2) + 1, sh - 1);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Math.Min(x * 2, sw - 1), x1 = Math.Min((x * 2) + 1, sw - 1);
                    uint sum = (uint)s[(y0 * sw) + x0] + s[(y0 * sw) + x1] + s[(y1 * sw) + x0] + s[(y1 * sw) + x1];
                    d[(y * dw) + x] = (byte)((sum + 2) / 4);
                }
            }

            levels.Add((dw, dh, d));
        }

        return new AlpineAlphaMask(levels);
    }

    /// <summary>
    /// Opacity in [0, 1] averaged over a square around (u, v) — in texture repeats — 2^lod finest texels wide;
    /// taps read only texels inside <paramref name="rect"/> when given, else wrap.
    /// </summary>
    public float Coverage(float u, float v, float lod, Rect? rect)
    {
        float top = Levels.Count - 1;
        lod = lod > 0f ? MathF.Min(lod, top) : 0f;
        int taps = lod > 0f ? FootprintTaps : 1;
        float finest = top;
        if (rect is Rect r)
        {
            float extent = MathF.Min((r.U1 - r.U0) * Levels[0].W, (r.V1 - r.V0) * Levels[0].H);
            finest = MathF.Log2(MathF.Max(extent, 1f));
        }

        float tapLod = MathF.Min(MathF.Max(0f, lod - MathF.Log2(taps)), finest);
        int l0 = (int)tapLod;
        int l1 = l0 + 1 <= finest ? l0 + 1 : l0;
        l1 = Math.Min(l1, Levels.Count - 1);
        float frac = tapLod - l0;
        (int X0, int X1, int Y0, int Y1) r0 = rect is Rect a ? TexelsIn(Levels[l0], a) : default;
        (int X0, int X1, int Y0, int Y1) r1 = rect is Rect b ? TexelsIn(Levels[l1], b) : default;
        float spanU = MathF.Pow(2f, lod) / Levels[0].W;
        float spanV = MathF.Pow(2f, lod) / Levels[0].H;
        float sum = 0f;
        for (int j = 0; j < taps; j++)
        {
            float tv = v + ((((j + 0.5f) / taps) - 0.5f) * spanV);
            for (int i = 0; i < taps; i++)
            {
                float tu = u + ((((i + 0.5f) / taps) - 0.5f) * spanU);
                float s = rect is null ? Bilinear(Levels[l0], tu, tv) : BilinearIn(Levels[l0], tu, tv, r0);
                if (frac > 0f && l1 != l0)
                {
                    float s1 = rect is null ? Bilinear(Levels[l1], tu, tv) : BilinearIn(Levels[l1], tu, tv, r1);
                    s += (s1 - s) * frac;
                }

                sum += s;
            }
        }

        return sum / (taps * taps) / 255f;
    }

    private static (int, int, int, int) TexelsIn((int W, int H, byte[] A) l, Rect r)
    {
        static void Axis(float a, float b, int n, out int lo, out int hi)
        {
            lo = Math.Clamp((int)MathF.Ceiling((a * n) - 0.5f), 0, n - 1);
            hi = Math.Clamp((int)MathF.Floor((b * n) - 0.5f), 0, n - 1);
            if (lo > hi)
            {
                lo = hi = Math.Clamp((int)((a + b) * 0.5f * n), 0, n - 1);
            }
        }

        Axis(r.U0, r.U1, l.W, out int x0, out int x1);
        Axis(r.V0, r.V1, l.H, out int y0, out int y1);
        return (x0, x1, y0, y1);
    }

    private static float BilinearIn((int W, int H, byte[] A) l, float u, float v, (int X0, int X1, int Y0, int Y1) t)
    {
        static void Axis(float c, int n, int lo, int hi, out int i0, out int i1, out float f)
        {
            float x = (c * n) - 0.5f;
            x = float.IsFinite(x) ? Math.Clamp(x, lo, hi) : lo;
            i0 = (int)x;
            i1 = Math.Min(i0 + 1, hi);
            f = x - i0;
        }

        Axis(u, l.W, t.X0, t.X1, out int x0, out int x1, out float fx);
        Axis(v, l.H, t.Y0, t.Y1, out int y0, out int y1, out float fy);
        return Mix(l, x0, x1, y0, y1, fx, fy);
    }

    private static float Bilinear((int W, int H, byte[] A) l, float u, float v)
    {
        float x = (Wrap(u) * l.W) - 0.5f, y = (Wrap(v) * l.H) - 0.5f;
        float fx0 = MathF.Floor(x), fy0 = MathF.Floor(y);
        int x0 = (((int)fx0 % l.W) + l.W) % l.W, y0 = (((int)fy0 % l.H) + l.H) % l.H;
        return Mix(l, x0, (x0 + 1) % l.W, y0, (y0 + 1) % l.H, x - fx0, y - fy0);
    }

    private static float Mix((int W, int H, byte[] A) l, int x0, int x1, int y0, int y1, float fx, float fy)
    {
        float a00 = l.A[(y0 * l.W) + x0], a01 = l.A[(y0 * l.W) + x1];
        float a10 = l.A[(y1 * l.W) + x0], a11 = l.A[(y1 * l.W) + x1];
        float top = a00 + ((a01 - a00) * fx);
        float bottom = a10 + ((a11 - a10) * fx);
        return top + ((bottom - top) * fy);
    }

    private static float Wrap(float f) => float.IsFinite(f) ? f - MathF.Floor(f) : 0f;
}
