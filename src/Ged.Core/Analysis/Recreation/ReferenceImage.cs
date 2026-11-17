using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ged.Core.IO.Tex;

namespace Ged.Core.Analysis.Recreation;

/// <summary>A decoded reference picture (PNG, JPEG, BMP, TGA, GIF): RGBA rows, top row first.</summary>
public sealed class RefImage
{
    private RefImage(int width, int height, byte[] rgba, string path)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        Path = path;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Width × height × 4 bytes, rows top to bottom.</summary>
    public byte[] Rgba { get; }

    /// <summary>The file it was read from.</summary>
    public string Path { get; }

    /// <summary>Decodes an image file.</summary>
    public static RefImage Load(string path)
    {
        StbImageSharp.ImageResult r = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return new RefImage(r.Width, r.Height, r.Data, path);
    }

    /// <summary>An image from raw RGBA (tests, generated references).</summary>
    public static RefImage FromRgba(int width, int height, byte[] rgba, string path = "") => new(width, height, rgba, path);

    /// <summary>The colour of pixel (x, y), clamped to the picture.</summary>
    public (byte R, byte G, byte B) At(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int o = ((y * Width) + x) * 4;
        return (Rgba[o], Rgba[o + 1], Rgba[o + 2]);
    }
}

/// <summary>
/// The image → world mapping of an overhead reference: <see cref="Ppm"/> image pixels per metre, the image point
/// <see cref="OriginX"/>, <see cref="OriginY"/> lands on world (<see cref="WorldX"/>, <see cref="WorldZ"/>), and the
/// image's up direction points along world heading <see cref="HeadingDeg"/> (0 = +Z, 90 = +X; the default 0 puts
/// north = +Z up, like render_plan). Image +Y (down) is then world −Z, and image +X (right) world +X.
/// </summary>
public sealed record RefCalibration(double Ppm, double OriginX, double OriginY, double WorldX, double WorldZ, double HeadingDeg)
{
    private double H => HeadingDeg * Math.PI / 180;

    /// <summary>The world point (x, z) under image pixel coordinates (px, py) (pixel centres are at +0.5).</summary>
    public (double X, double Z) ToWorld(double px, double py)
    {
        double r = (px - OriginX) / Ppm, u = -(py - OriginY) / Ppm; // metres right, metres up the image
        double s = Math.Sin(H), c = Math.Cos(H);
        // up = (sin h, cos h), right = (cos h, -sin h)
        return (WorldX + (r * c) + (u * s), WorldZ - (r * s) + (u * c));
    }

    /// <summary>The image point over world (x, z).</summary>
    public (double X, double Y) ToPx(double x, double z)
    {
        double dx = x - WorldX, dz = z - WorldZ;
        double s = Math.Sin(H), c = Math.Cos(H);
        double r = (dx * c) - (dz * s), u = (dx * s) + (dz * c);
        return (OriginX + (r * Ppm), OriginY - (u * Ppm));
    }

    /// <summary>The mapping as a plain table.</summary>
    public Dictionary<string, object?> ToTable() => new(StringComparer.Ordinal)
    {
        ["ppm"] = Math.Round(Ppm, 5),
        ["mpp"] = Math.Round(1 / Ppm, 6),
        ["origin"] = new[] { Math.Round(OriginX, 3), Math.Round(OriginY, 3) },
        ["at"] = new[] { Math.Round(WorldX, 4), Math.Round(WorldZ, 4) },
        ["heading"] = Math.Round(HeadingDeg, 4),
        ["frame"] = "world(x, z) = at + (px - origin.x) / ppm along the image's right + (origin.y - py) / ppm along its up; up points at heading (0 = +Z, 90 = +X)",
    };

    /// <summary>The heading of a named up direction (+z, +x, -z, -x), or null.</summary>
    public static double? HeadingOf(string up) => up.Trim().ToLowerInvariant() switch
    {
        "+z" or "z" or "north" => 0,
        "+x" or "x" or "east" => 90,
        "-z" or "south" => 180,
        "-x" or "west" => 270,
        _ => null,
    };
}

/// <summary>A small RGBA drawing surface for the reference tools' images (grids, masks, comparisons).</summary>
internal sealed class RefCanvas
{
    public RefCanvas(int w, int h, byte r = 18, byte g = 18, byte b = 22)
    {
        W = w;
        H = h;
        Px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            Px[i * 4] = r;
            Px[(i * 4) + 1] = g;
            Px[(i * 4) + 2] = b;
            Px[(i * 4) + 3] = 255;
        }
    }

    public int W { get; }

    public int H { get; }

    public byte[] Px { get; }

    /// <summary>When set, <see cref="Blend"/> (and everything drawn through it) only touches pixels inside this box.</summary>
    public (int X0, int Y0, int X1, int Y1)? Clip { get; set; }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x < (uint)W && (uint)y < (uint)H)
        {
            int o = ((y * W) + x) * 4;
            Px[o] = r;
            Px[o + 1] = g;
            Px[o + 2] = b;
        }
    }

    /// <summary>Mixes a colour over pixel (x, y) with opacity <paramref name="a"/> (0-1).</summary>
    public void Blend(int x, int y, byte r, byte g, byte b, double a)
    {
        if ((uint)x < (uint)W && (uint)y < (uint)H && (Clip is not { } k || (x >= k.X0 && x < k.X1 && y >= k.Y0 && y < k.Y1)))
        {
            int o = ((y * W) + x) * 4;
            Px[o] = (byte)((Px[o] * (1 - a)) + (r * a));
            Px[o + 1] = (byte)((Px[o + 1] * (1 - a)) + (g * a));
            Px[o + 2] = (byte)((Px[o + 2] * (1 - a)) + (b * a));
        }
    }

    public void Fill(int x0, int y0, int x1, int y1, byte r, byte g, byte b, double a = 1)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
            {
                Blend(x, y, r, g, b, a);
            }
        }
    }

    public void Line(double ax, double ay, double bx, double by, byte r, byte g, byte b, double a = 1, int thick = 1)
    {
        double len = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
        int n = Math.Max(1, (int)Math.Ceiling(len));
        int lo = -(thick - 1) / 2, hi = thick / 2;
        var done = new HashSet<long>();
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            int x = (int)Math.Floor(ax + ((bx - ax) * t)), y = (int)Math.Floor(ay + ((by - ay) * t));
            for (int dy = lo; dy <= hi; dy++)
            {
                for (int dx = lo; dx <= hi; dx++)
                {
                    if (done.Add(((long)(y + dy) << 32) | (uint)(x + dx)))
                    {
                        Blend(x + dx, y + dy, r, g, b, a);
                    }
                }
            }
        }
    }

    public void Rect(int x0, int y0, int x1, int y1, byte r, byte g, byte b, int thick = 1)
    {
        Line(x0, y0, x1, y0, r, g, b, 1, thick);
        Line(x1, y0, x1, y1, r, g, b, 1, thick);
        Line(x1, y1, x0, y1, r, g, b, 1, thick);
        Line(x0, y1, x0, y0, r, g, b, 1, thick);
    }

    /// <summary>Text in the 5×7 font at <paramref name="scale"/>, optionally on a dark box; returns the drawn width.</summary>
    public int Text(int x, int y, string s, int scale, byte r, byte g, byte b, bool box = false)
    {
        int w = BitmapFont.Measure(s, scale), h = BitmapFont.LineHeight(scale);
        if (box)
        {
            Fill(x - 2, y - 2, x + w + 2, y + h + 2, 0, 0, 0, 0.72);
        }

        for (int i = 0; i < s.Length; i++)
        {
            for (int gx = 0; gx < BitmapFont.GlyphWidth; gx++)
            {
                for (int gy = 0; gy < BitmapFont.GlyphHeight; gy++)
                {
                    if (BitmapFont.IsSet(s[i], gx, gy))
                    {
                        Fill(x + (((i * BitmapFont.Advance) + gx) * scale), y + (gy * scale), x + (((i * BitmapFont.Advance) + gx + 1) * scale), y + ((gy + 1) * scale), r, g, b);
                    }
                }
            }
        }

        return w;
    }

    public byte[] Png() => PngWriter.Encode(W, H, Px);

    public static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// The coordinate grid of a reference picture (Lua <c>ref.grid</c>, MCP <c>reference_view</c> with step / zoom): the
/// picture (or a crop of it) enlarged by an integer-friendly zoom with crisp pixels, major lines every
/// <c>step</c> source pixels labelled on all four margins, faint minor lines at a fifth of the step, and the source
/// coordinates printed at alternate interior crossings, so a reader can take a corner's position to a pixel or two.
/// </summary>
public static class ReferenceGrid
{
    private static readonly int[] Steps = { 2, 4, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000 };

    /// <summary>The grid image and what it shows.</summary>
    public sealed record Result(byte[] Png, int Width, int Height, int Step, int[] Crop, double Zoom);

    /// <summary>
    /// Draws the grid. <paramref name="crop"/> = x, y, w, h in source pixels (null = the whole picture); zoom ≤ 0 = fit
    /// about 1400 px. <paramref name="overlay"/> shapes are drawn over the picture, under the labels.
    /// </summary>
    public static Result Render(RefImage img, int[]? crop, double zoom, int step, RefCalibration? cal, string? title = null, IReadOnlyList<RefShape>? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(img);
        int cx = 0, cy = 0, cw = img.Width, ch = img.Height;
        if (crop is not null)
        {
            cx = Math.Clamp(crop[0], 0, img.Width - 1);
            cy = Math.Clamp(crop[1], 0, img.Height - 1);
            cw = Math.Clamp(crop[2], 1, img.Width - cx);
            ch = Math.Clamp(crop[3], 1, img.Height - cy);
        }

        double z = zoom > 0 ? zoom : Math.Clamp(1400.0 / Math.Max(cw, ch), 0.25, 16);
        if (zoom <= 0 && z >= 1)
        {
            z = Math.Floor(z); // whole-pixel enlargement keeps every source pixel the same size
        }

        int iw = Math.Max(1, (int)Math.Round(cw * z)), ih = Math.Max(1, (int)Math.Round(ch * z));
        if (step <= 0)
        {
            step = Steps[^1];
            foreach (int s in Steps)
            {
                if (s * z >= 70)
                {
                    step = s;
                    break;
                }
            }
        }

        const int Label = 2, Margin = 34, Top = 52;
        var c = new RefCanvas(iw + (2 * Margin), ih + Top + Margin);

        // The picture: nearest-neighbour when enlarged (crisp pixels), box average when reduced.
        for (int y = 0; y < ih; y++)
        {
            double sy0 = cy + (y / z), sy1 = cy + ((y + 1) / z);
            for (int x = 0; x < iw; x++)
            {
                double sx0 = cx + (x / z), sx1 = cx + ((x + 1) / z);
                int r = 0, g = 0, b = 0, n = 0;
                for (int sy = (int)sy0; sy < Math.Max((int)sy0 + 1, (int)Math.Ceiling(sy1)); sy++)
                {
                    for (int sx = (int)sx0; sx < Math.Max((int)sx0 + 1, (int)Math.Ceiling(sx1)); sx++)
                    {
                        (byte pr, byte pg, byte pb) = img.At(sx, sy);
                        r += pr;
                        g += pg;
                        b += pb;
                        n++;
                    }
                }

                c.Set(Margin + x, Top + y, (byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }

        double X(double sx) => Margin + ((sx - cx) * z);
        double Y(double sy) => Top + ((sy - cy) * z);

        // Minor lines.
        int minor = step % 5 == 0 ? step / 5 : step % 2 == 0 ? step / 2 : 0;
        if (minor > 0 && minor * z >= 9)
        {
            for (int sx = ((cx / minor) + 1) * minor; sx < cx + cw; sx += minor)
            {
                if (sx % step != 0)
                {
                    c.Line(X(sx), Top, X(sx), Top + ih - 1, 255, 255, 255, 0.22);
                }
            }

            for (int sy = ((cy / minor) + 1) * minor; sy < cy + ch; sy += minor)
            {
                if (sy % step != 0)
                {
                    c.Line(Margin, Y(sy), Margin + iw - 1, Y(sy), 255, 255, 255, 0.22);
                }
            }
        }

        // Major lines with labels on all four margins.
        int fh = BitmapFont.LineHeight(Label);
        var xs = new List<int>();
        var ys = new List<int>();
        for (int sx = (int)Math.Ceiling((double)cx / step) * step; sx <= cx + cw; sx += step)
        {
            xs.Add(sx);
            double px = X(sx);
            c.Line(px, Top, px, Top + ih - 1, 0, 0, 0, 0.5);
            c.Line(px + 1, Top, px + 1, Top + ih - 1, 255, 220, 0, 0.75);
            string s = sx.ToString(CultureInfo.InvariantCulture);
            int tw = BitmapFont.Measure(s, Label);
            c.Text((int)px - (tw / 2), Top - fh - 4, s, Label, 255, 220, 0);
            c.Text((int)px - (tw / 2), Top + ih + 6, s, Label, 255, 220, 0);
        }

        for (int sy = (int)Math.Ceiling((double)cy / step) * step; sy <= cy + ch; sy += step)
        {
            ys.Add(sy);
            double py = Y(sy);
            c.Line(Margin, py, Margin + iw - 1, py, 0, 0, 0, 0.5);
            c.Line(Margin, py + 1, Margin + iw - 1, py + 1, 255, 220, 0, 0.75);
            string s = sy.ToString(CultureInfo.InvariantCulture);
            int tw = BitmapFont.Measure(s, 1);
            c.Text(Margin - tw - 4, (int)py - 3, s, 1, 255, 220, 0);
            c.Text(Margin + iw + 4, (int)py - 3, s, 1, 255, 220, 0);
        }

        // Interior crossings: "x,y" on alternate crossings when there is room for the text.
        if (step * z >= 64)
        {
            for (int i = 0; i < xs.Count; i++)
            {
                for (int j = 0; j < ys.Count; j++)
                {
                    if ((i + j) % 2 == 0)
                    {
                        string s = $"{xs[i]},{ys[j]}";
                        int px = (int)X(xs[i]) + 4, py = (int)Y(ys[j]) + 4;
                        if (px + BitmapFont.Measure(s, 1) < Margin + iw && py + 8 < Top + ih)
                        {
                            c.Text(px, py, s, 1, 255, 255, 255, box: true);
                        }
                    }
                }
            }
        }

        // The trace (or the level's floor) over the picture: outlines, corner dots and names, in source pixels.
        if (overlay is { Count: > 0 })
        {
            c.Clip = (Margin, Top, Margin + iw, Top + ih);
            ReferenceShapes.Draw(c, overlay, (sx, sy) => (X(sx), Y(sy)), z);
            c.Clip = null;
        }

        string head = title ?? $"{System.IO.Path.GetFileName(img.Path)} {img.Width}x{img.Height}";
        head += $"  crop {cx},{cy},{cw},{ch}  zoom {RefCanvas.N(z)}  grid {step} px";
        if (cal is not null)
        {
            head += $"  ({RefCanvas.N(step / cal.Ppm)} m)";
        }

        c.Text(6, 6, head, 1, 230, 230, 230);
        if (cal is not null)
        {
            (double ax, double az) = cal.ToWorld(cx, cy);
            (double bx, double bz) = cal.ToWorld(cx + cw, cy + ch);
            c.Text(6, 18, $"world: top-left ({RefCanvas.N(ax)}, {RefCanvas.N(az)})  bottom-right ({RefCanvas.N(bx)}, {RefCanvas.N(bz)})  {RefCanvas.N(cal.Ppm)} px/m", 1, 160, 220, 255);
        }

        return new Result(c.Png(), c.W, c.H, step, new[] { cx, cy, cw, ch }, z);
    }
}
