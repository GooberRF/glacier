using System;
using System.Numerics;

namespace Ged.Automation.Rendering;

/// <summary>An 8-bit RGBA colour for the CPU overlay painter.</summary>
internal readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba Black = new(0, 0, 0);
    public static readonly Rgba White = new(255, 255, 255);

    /// <summary>The same colour at another opacity.</summary>
    public Rgba WithAlpha(byte a) => this with { A = a };
}

/// <summary>
/// Software drawing onto a tightly packed RGBA8 buffer (top-left origin) — the post-render overlay
/// layer of <c>glacier-cli render</c>. Everything is integer/deterministic: lines are rasterized with
/// a fixed sub-pixel walk, discs and rings by distance tests, text from <see cref="BitmapFont"/>.
/// Alpha blending is straight "over" onto an opaque destination.
/// </summary>
internal sealed class RgbaCanvas
{
    public RgbaCanvas(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the canvas size.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Reads one pixel (no bounds check beyond the array's).</summary>
    public Rgba Get(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    /// <summary>Blends one pixel (clipped to the canvas).</summary>
    public void Blend(int x, int y, Rgba c)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || c.A == 0)
        {
            return;
        }

        int i = ((y * Width) + x) * 4;
        if (c.A == 255)
        {
            Pixels[i] = c.R;
            Pixels[i + 1] = c.G;
            Pixels[i + 2] = c.B;
            Pixels[i + 3] = 255;
            return;
        }

        int a = c.A;
        int ia = 255 - a;
        Pixels[i] = (byte)(((c.R * a) + (Pixels[i] * ia) + 127) / 255);
        Pixels[i + 1] = (byte)(((c.G * a) + (Pixels[i + 1] * ia) + 127) / 255);
        Pixels[i + 2] = (byte)(((c.B * a) + (Pixels[i + 2] * ia) + 127) / 255);
        Pixels[i + 3] = 255;
    }

    /// <summary>Fills an axis-aligned rectangle (clipped).</summary>
    public void FillRect(int x, int y, int w, int h, Rgba c)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        for (int py = y0; py < y1; py++)
        {
            for (int px = x0; px < x1; px++)
            {
                Blend(px, py, c);
            }
        }
    }

    /// <summary>Outlines an axis-aligned rectangle with a one-pixel border.</summary>
    public void StrokeRect(int x, int y, int w, int h, Rgba c)
    {
        FillRect(x, y, w, 1, c);
        FillRect(x, y + h - 1, w, 1, c);
        FillRect(x, y + 1, 1, h - 2, c);
        FillRect(x + w - 1, y + 1, 1, h - 2, c);
    }

    /// <summary>
    /// Draws a line of <paramref name="thickness"/> pixels from <paramref name="a"/> to
    /// <paramref name="b"/> (continuous screen coordinates; pixel centres at +0.5). Each covered pixel is
    /// blended once.
    /// </summary>
    public void Line(Vector2 a, Vector2 b, Rgba c, float thickness = 1f)
    {
        if (!ClipToCanvas(ref a, ref b, thickness + 2f))
        {
            return;
        }

        float half = MathF.Max(thickness, 1f) * 0.5f;
        int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) - half - 1f);
        int x1 = (int)MathF.Ceiling(MathF.Max(a.X, b.X) + half + 1f);
        int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) - half - 1f);
        int y1 = (int)MathF.Ceiling(MathF.Max(a.Y, b.Y) + half + 1f);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1);
        y1 = Math.Min(Height - 1, y1);
        Vector2 d = b - a;
        float len2 = d.LengthSquared();

        // Long lines would make the bounding box walk quadratic; step along the segment instead.
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096 && len2 > 0f)
        {
            float len = MathF.Sqrt(len2);
            int steps = (int)MathF.Ceiling(len);
            int r = (int)MathF.Ceiling(half);
            var seen = new System.Collections.Generic.HashSet<long>();
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = a + (d * (s / (float)steps));
                int cx = (int)MathF.Floor(p.X), cy = (int)MathF.Floor(p.Y);
                for (int oy = -r; oy <= r; oy++)
                {
                    for (int ox = -r; ox <= r; ox++)
                    {
                        int px = cx + ox, py = cy + oy;
                        if ((uint)px >= (uint)Width || (uint)py >= (uint)Height)
                        {
                            continue;
                        }

                        if (DistanceToSegment(new Vector2(px + 0.5f, py + 0.5f), a, d, len2) <= half && seen.Add(((long)py * Width) + px))
                        {
                            Blend(px, py, c);
                        }
                    }
                }
            }

            return;
        }

        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                if (DistanceToSegment(new Vector2(px + 0.5f, py + 0.5f), a, d, len2) <= half)
                {
                    Blend(px, py, c);
                }
            }
        }
    }

    /// <summary>A filled disc centred at <paramref name="center"/>.</summary>
    public void FillCircle(Vector2 center, float radius, Rgba c)
    {
        int x0 = (int)MathF.Floor(center.X - radius), x1 = (int)MathF.Ceiling(center.X + radius);
        int y0 = (int)MathF.Floor(center.Y - radius), y1 = (int)MathF.Ceiling(center.Y + radius);
        float r2 = radius * radius;
        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                float dx = px + 0.5f - center.X, dy = py + 0.5f - center.Y;
                if ((dx * dx) + (dy * dy) <= r2)
                {
                    Blend(px, py, c);
                }
            }
        }
    }

    /// <summary>A ring (circle outline) of <paramref name="thickness"/> pixels.</summary>
    public void StrokeCircle(Vector2 center, float radius, Rgba c, float thickness = 1f)
    {
        float outer = radius + (thickness * 0.5f), inner = MathF.Max(0f, radius - (thickness * 0.5f));
        int x0 = (int)MathF.Floor(center.X - outer), x1 = (int)MathF.Ceiling(center.X + outer);
        int y0 = (int)MathF.Floor(center.Y - outer), y1 = (int)MathF.Ceiling(center.Y + outer);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1);
        y1 = Math.Min(Height - 1, y1);
        float o2 = outer * outer, i2 = inner * inner;
        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                float dx = px + 0.5f - center.X, dy = py + 0.5f - center.Y;
                float d2 = (dx * dx) + (dy * dy);
                if (d2 <= o2 && d2 >= i2)
                {
                    Blend(px, py, c);
                }
            }
        }
    }

    /// <summary>A filled convex polygon (screen coordinates), by per-pixel half-plane tests.</summary>
    public void FillConvex(ReadOnlySpan<Vector2> pts, Rgba c)
    {
        if (pts.Length < 3)
        {
            return;
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (Vector2 p in pts)
        {
            minX = MathF.Min(minX, p.X);
            minY = MathF.Min(minY, p.Y);
            maxX = MathF.Max(maxX, p.X);
            maxY = MathF.Max(maxY, p.Y);
        }

        // Winding sign, so the inside test works for either orientation.
        float area = 0f;
        for (int i = 0; i < pts.Length; i++)
        {
            Vector2 p = pts[i], q = pts[(i + 1) % pts.Length];
            area += (p.X * q.Y) - (q.X * p.Y);
        }

        float sign = area >= 0f ? 1f : -1f;
        int x0 = Math.Max(0, (int)MathF.Floor(minX)), x1 = Math.Min(Width - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(Height - 1, (int)MathF.Ceiling(maxY));
        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                var s = new Vector2(px + 0.5f, py + 0.5f);
                bool inside = true;
                for (int i = 0; i < pts.Length && inside; i++)
                {
                    Vector2 p = pts[i], q = pts[(i + 1) % pts.Length];
                    float cross = ((q.X - p.X) * (s.Y - p.Y)) - ((q.Y - p.Y) * (s.X - p.X));
                    inside = cross * sign >= -0.0001f;
                }

                if (inside)
                {
                    Blend(px, py, c);
                }
            }
        }
    }

    /// <summary>Draws text with its top-left at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Text(int x, int y, string text, Rgba c, int scale = 1)
    {
        scale = Math.Max(1, scale);
        int cx = x;
        foreach (char ch in text)
        {
            for (int gy = 0; gy < BitmapFont.GlyphHeight; gy++)
            {
                for (int gx = 0; gx < BitmapFont.GlyphWidth; gx++)
                {
                    if (BitmapFont.IsSet(ch, gx, gy))
                    {
                        FillRect(cx + (gx * scale), y + (gy * scale), scale, scale, c);
                    }
                }
            }

            cx += BitmapFont.Advance * scale;
        }
    }

    /// <summary>Text with a one-font-pixel dark halo, readable over any background.</summary>
    public void HaloText(int x, int y, string text, Rgba c, int scale = 1, Rgba? halo = null)
    {
        scale = Math.Max(1, scale);
        Rgba h = halo ?? new Rgba(0, 0, 0, 220);
        int cx = x;
        foreach (char ch in text)
        {
            for (int gy = -1; gy <= BitmapFont.GlyphHeight; gy++)
            {
                for (int gx = -1; gx <= BitmapFont.GlyphWidth; gx++)
                {
                    if (BitmapFont.IsSet(ch, gx, gy))
                    {
                        continue;
                    }

                    bool near = false;
                    for (int oy = -1; oy <= 1 && !near; oy++)
                    {
                        for (int ox = -1; ox <= 1 && !near; ox++)
                        {
                            near = BitmapFont.IsSet(ch, gx + ox, gy + oy);
                        }
                    }

                    if (near)
                    {
                        FillRect(cx + (gx * scale), y + (gy * scale), scale, scale, h);
                    }
                }
            }

            cx += BitmapFont.Advance * scale;
        }

        Text(x, y, text, c, scale);
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 d, float len2)
    {
        if (len2 <= 1e-12f)
        {
            return Vector2.Distance(p, a);
        }

        float t = Math.Clamp(Vector2.Dot(p - a, d) / len2, 0f, 1f);
        return Vector2.Distance(p, a + (d * t));
    }

    /// <summary>Liang-Barsky clip of a segment to the canvas (expanded by <paramref name="pad"/>).</summary>
    private bool ClipToCanvas(ref Vector2 a, ref Vector2 b, float pad)
    {
        float xmin = -pad, ymin = -pad, xmax = Width + pad, ymax = Height + pad;
        float t0 = 0f, t1 = 1f;
        Vector2 d = b - a;
        float[] p = { -d.X, d.X, -d.Y, d.Y };
        float[] q = { a.X - xmin, xmax - a.X, a.Y - ymin, ymax - a.Y };
        for (int i = 0; i < 4; i++)
        {
            if (MathF.Abs(p[i]) < 1e-9f)
            {
                if (q[i] < 0f)
                {
                    return false;
                }

                continue;
            }

            float t = q[i] / p[i];
            if (p[i] < 0f)
            {
                t0 = MathF.Max(t0, t);
            }
            else
            {
                t1 = MathF.Min(t1, t);
            }

            if (t0 > t1)
            {
                return false;
            }
        }

        Vector2 a0 = a;
        a = a0 + (d * t0);
        b = a0 + (d * t1);
        return true;
    }
}
