using System;
using System.IO;
using Ged.Core.IO.Tex;
using StbImageSharp;

namespace Ged.Automation.Reference;

/// <summary>
/// A tightly packed RGBA8 image (top-left origin) for the reference-matching tools: decoding of the concept images an
/// agent registers (PNG, JPEG, BMP, TGA, GIF via StbImageSharp), cropping, resampling and PNG output. Deterministic.
/// </summary>
internal sealed class RasterImage
{
    /// <summary>The image file types a reference may come from.</summary>
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif" };

    public RasterImage(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || pixels.Length != width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the image size.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>A blank (opaque) image of one colour.</summary>
    public static RasterImage Filled(int width, int height, byte r, byte g, byte b)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = 255;
        }

        return new RasterImage(width, height, px);
    }

    /// <summary>Decodes an image file (throws an <c>io</c> / <c>usage</c> <see cref="AutomationException"/>).</summary>
    public static RasterImage Load(string path)
    {
        if (Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) < 0)
        {
            throw new AutomationException(AutomationErrorCodes.Usage,
                $"Not an image file this tool reads ({string.Join(", ", Extensions)}): {path}");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not read '{path}': {ex.Message}", ex);
        }

        return Decode(bytes, path);
    }

    /// <summary>Decodes image bytes (<paramref name="what"/> names them in errors).</summary>
    public static RasterImage Decode(byte[] bytes, string what)
    {
        try
        {
            ImageResult r = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            byte[] px = r.Data;
            for (int i = 3; i < px.Length; i += 4)
            {
                px[i] = 255; // references are opaque pictures
            }

            return new RasterImage(r.Width, r.Height, px);
        }
        catch (Exception ex) when (ex is not AutomationException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not decode the image '{what}': {ex.Message}", ex);
        }
    }

    /// <summary>Writes the image as a PNG (creating the folder).</summary>
    public void SavePng(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, PngWriter.Encode(Width, Height, Pixels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not write '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>The pixels of a rectangle (clipped to the image; at least one pixel).</summary>
    public RasterImage Crop(int x, int y, int w, int h)
    {
        int x0 = Math.Clamp(x, 0, Width - 1), y0 = Math.Clamp(y, 0, Height - 1);
        int x1 = Math.Clamp(x + w, x0 + 1, Width), y1 = Math.Clamp(y + h, y0 + 1, Height);
        int cw = x1 - x0, ch = y1 - y0;
        var px = new byte[cw * ch * 4];
        for (int row = 0; row < ch; row++)
        {
            Buffer.BlockCopy(Pixels, (((y0 + row) * Width) + x0) * 4, px, row * cw * 4, cw * 4);
        }

        return new RasterImage(cw, ch, px);
    }

    /// <summary>
    /// Resamples to <paramref name="w"/> x <paramref name="h"/>: an area average along an axis that shrinks, bilinear
    /// along one that grows (so a small screenshot crop enlarges smoothly and a large render shrinks without aliasing).
    /// </summary>
    public RasterImage Resize(int w, int h)
    {
        if (w == Width && h == Height)
        {
            return new RasterImage(w, h, (byte[])Pixels.Clone());
        }

        float[] horizontal = ResampleAxis(Pixels, Width, Height, w, horizontal: true);
        float[] both = ResampleAxis(horizontal, w, Height, h, horizontal: false);
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = (byte)Math.Clamp((int)MathF.Round(both[i]), 0, 255);
        }

        return new RasterImage(w, h, px);
    }

    private static float[] ResampleAxis(byte[] src, int sw, int sh, int dn, bool horizontal)
    {
        float[] f = new float[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            f[i] = src[i];
        }

        return ResampleAxis(f, sw, sh, dn, horizontal);
    }

    private static float[] ResampleAxis(float[] src, int sw, int sh, int dn, bool horizontal)
    {
        int sn = horizontal ? sw : sh;
        int ow = horizontal ? dn : sw, oh = horizontal ? sh : dn;
        var dst = new float[ow * oh * 4];
        int lines = horizontal ? sh : sw;
        float scale = (float)sn / dn;
        for (int d = 0; d < dn; d++)
        {
            // Source span [s0, s1) of destination sample d.
            float s0 = d * scale, s1 = (d + 1) * scale;
            for (int line = 0; line < lines; line++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float v;
                    if (scale > 1f)
                    {
                        float sum = 0f, weight = 0f;
                        for (int s = (int)MathF.Floor(s0); s < (int)MathF.Ceiling(s1) && s < sn; s++)
                        {
                            float cover = MathF.Min(s1, s + 1) - MathF.Max(s0, s);
                            if (cover <= 0f)
                            {
                                continue;
                            }

                            sum += Sample(src, sw, horizontal, line, s, c) * cover;
                            weight += cover;
                        }

                        v = weight > 0f ? sum / weight : 0f;
                    }
                    else
                    {
                        float centre = ((d + 0.5f) * scale) - 0.5f;
                        int a = (int)MathF.Floor(centre);
                        float t = centre - a;
                        int ia = Math.Clamp(a, 0, sn - 1), ib = Math.Clamp(a + 1, 0, sn - 1);
                        v = (Sample(src, sw, horizontal, line, ia, c) * (1f - t)) + (Sample(src, sw, horizontal, line, ib, c) * t);
                    }

                    int di = horizontal ? (((line * ow) + d) * 4) + c : (((d * ow) + line) * 4) + c;
                    dst[di] = v;
                }
            }
        }

        return dst;
    }

    private static float Sample(float[] src, int sw, bool horizontal, int line, int s, int c) =>
        horizontal ? src[(((line * sw) + s) * 4) + c] : src[(((s * sw) + line) * 4) + c];

    /// <summary>Rec. 601 luma per pixel, 0..1.</summary>
    public float[] Luma()
    {
        var l = new float[Width * Height];
        for (int i = 0; i < l.Length; i++)
        {
            int p = i * 4;
            l[i] = ((0.299f * Pixels[p]) + (0.587f * Pixels[p + 1]) + (0.114f * Pixels[p + 2])) / 255f;
        }

        return l;
    }

    /// <summary>Pastes <paramref name="src"/> with its top-left at (<paramref name="x"/>, <paramref name="y"/>) (clipped).</summary>
    public void Paste(RasterImage src, int x, int y)
    {
        for (int row = 0; row < src.Height; row++)
        {
            int ty = y + row;
            if ((uint)ty >= (uint)Height)
            {
                continue;
            }

            for (int col = 0; col < src.Width; col++)
            {
                int tx = x + col;
                if ((uint)tx >= (uint)Width)
                {
                    continue;
                }

                Buffer.BlockCopy(src.Pixels, ((row * src.Width) + col) * 4, Pixels, ((ty * Width) + tx) * 4, 4);
            }
        }
    }
}
