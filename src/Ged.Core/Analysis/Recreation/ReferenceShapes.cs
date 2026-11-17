using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.IO.Tex;

namespace Ged.Core.Analysis.Recreation;

/// <summary>
/// A polygon over a reference picture, in source pixels (corner coordinates): a kit.trace region or solid, a ref.mask
/// outline, a plain point list, or one height band of the level's floor. Holes are inner rings.
/// </summary>
public sealed record RefShape(string? Name, IReadOnlyList<(double X, double Y)> Outer, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Holes, string Kind = "polygon");

/// <summary>
/// Polygons over a reference picture (Lua <c>ref.grid{overlay}</c>, <c>ref.mask{polygons}</c>, <c>ref.mask_edit</c>):
/// rasterising them into a mask, drawing them as outlines with names and corner dots, and the level's walkable floor
/// traced in picture pixels per height band.
/// </summary>
public static class ReferenceShapes
{
    private static readonly (byte R, byte G, byte B)[] Palette =
    {
        (255, 70, 200), (60, 230, 255), (255, 170, 30), (120, 255, 90), (255, 90, 90), (170, 140, 255), (255, 240, 60), (40, 255, 190),
    };

    /// <summary>
    /// Sets (or clears, <paramref name="value"/> false) every pixel whose centre lies inside <paramref name="s"/> (even-odd:
    /// the outer ring minus its holes).
    /// </summary>
    public static int Fill(bool[] m, int w, int h, RefShape s, bool value)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(s);
        var rings = new List<IReadOnlyList<(double X, double Y)>> { s.Outer };
        rings.AddRange(s.Holes);
        double minY = s.Outer.Min(p => p.Y), maxY = s.Outer.Max(p => p.Y);
        int changed = 0;
        var xs = new List<double>();
        for (int y = Math.Max(0, (int)Math.Floor(minY)); y < Math.Min(h, (int)Math.Ceiling(maxY) + 1); y++)
        {
            double cy = y + 0.5;
            xs.Clear();
            foreach (IReadOnlyList<(double X, double Y)> ring in rings)
            {
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    (double ax, double ay) = ring[j];
                    (double bx, double by) = ring[i];
                    if ((ay > cy) != (by > cy))
                    {
                        xs.Add(ax + ((cy - ay) * (bx - ax) / (by - ay)));
                    }
                }
            }

            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                // Pixel x is inside when its centre x + 0.5 lies in [xs[k], xs[k + 1]).
                int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5)), x1 = Math.Min(w - 1, (int)Math.Ceiling(xs[k + 1] - 0.5) - 1);
                for (int x = x0; x <= x1; x++)
                {
                    int p = (y * w) + x;
                    if (m[p] != value)
                    {
                        m[p] = value;
                        changed++;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// The level's walkable floor (the scorer's <see cref="RecreationFloor"/>) in picture pixels, one shape set per height
    /// band: each pixel takes its column's highest floor; heights closer than 0.6 m chain into one band (a ramp joins the
    /// floors it links). Shape names read "y 3" or "y 0..3".
    /// </summary>
    internal static List<RefShape> LevelOutline(RecreationFloor floor, RefCalibration cal, int width, int height, int[]? crop)
    {
        int x0 = 0, y0 = 0, x1 = width, y1 = height;
        if (crop is not null)
        {
            (x0, y0, x1, y1) = (crop[0], crop[1], crop[0] + crop[2], crop[1] + crop[3]);
        }

        var top = new double[width * height];
        Array.Fill(top, double.NaN);
        var ys = new List<double>();
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                (double wx, double wz) = cal.ToWorld(x + 0.5, y + 0.5);
                ReadOnlySpan<FloorPoint> pts = floor.In(floor.Cell(wx, wz));
                if (pts.Length > 0)
                {
                    top[(y * width) + x] = pts[^1].Y;
                    ys.Add(pts[^1].Y);
                }
            }
        }

        var shapes = new List<RefShape>();
        if (ys.Count == 0)
        {
            return shapes;
        }

        double[] distinct = ys.Select(v => Math.Round(v, 2)).Distinct().OrderBy(v => v).ToArray();
        var bands = new List<(double Lo, double Hi)>();
        foreach (double v in distinct)
        {
            if (bands.Count > 0 && v - bands[^1].Hi < 0.6)
            {
                bands[^1] = (bands[^1].Lo, v);
            }
            else
            {
                bands.Add((v, v));
            }
        }

        foreach ((double lo, double hi) in bands)
        {
            var m = new bool[width * height];
            for (int p = 0; p < m.Length; p++)
            {
                m[p] = !double.IsNaN(top[p]) && Math.Round(top[p], 2) >= lo && Math.Round(top[p], 2) <= hi;
            }

            var mask = new RefMask(width, height, m);
            string name = hi - lo < 0.01 ? $"y {RefCanvas.N(lo)}" : $"y {RefCanvas.N(lo)}..{RefCanvas.N(hi)}";
            foreach (RefPolygon poly in mask.Polygons(Math.Max(1, 0.5 * cal.Ppm), 64))
            {
                if (poly.AreaPx >= 4)
                {
                    shapes.Add(new RefShape(name, poly.Outer, poly.Holes, "level"));
                }
            }
        }

        return shapes;
    }

    /// <summary>
    /// Draws <paramref name="shapes"/> on <paramref name="c"/>: <paramref name="map"/> takes source pixels to canvas
    /// pixels. Each name gets its own colour; outlines 2 px, corners as dots, the name at the shape's label point. Level
    /// floor bands are drawn dashed-free in one colour per band.
    /// </summary>
    internal static void Draw(RefCanvas c, IReadOnlyList<RefShape> shapes, Func<double, double, (double X, double Y)> map, double zoom)
    {
        var colours = new Dictionary<string, (byte, byte, byte)>(StringComparer.Ordinal);
        int dot = zoom >= 3 ? 3 : 2;
        foreach (RefShape s in shapes)
        {
            string key = s.Name ?? $"#{colours.Count}";
            if (!colours.TryGetValue(key, out (byte R, byte G, byte B) col))
            {
                col = s.Kind == "solid" ? ((byte)255, (byte)255, (byte)255) : Palette[colours.Count % Palette.Length];
                colours[key] = col;
            }

            foreach (IReadOnlyList<(double X, double Y)> ring in new[] { s.Outer }.Concat(s.Holes))
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    (double ax, double ay) = map(ring[i].X, ring[i].Y);
                    (double bx, double by) = map(ring[(i + 1) % ring.Count].X, ring[(i + 1) % ring.Count].Y);
                    c.Line(ax, ay, bx, by, 0, 0, 0, 0.6, 4);
                    c.Line(ax, ay, bx, by, col.R, col.G, col.B, 1, 2);
                }

                foreach ((double x, double y) in ring)
                {
                    (double px, double py) = map(x, y);
                    c.Fill((int)px - dot, (int)py - dot, (int)px + dot + 1, (int)py + dot + 1, 0, 0, 0);
                    c.Fill((int)px - dot + 1, (int)py - dot + 1, (int)px + dot, (int)py + dot, col.R, col.G, col.B);
                }
            }
        }

        // Names last, so outlines never cover them.
        foreach (RefShape s in shapes)
        {
            if (s.Name is not { Length: > 0 } name)
            {
                continue;
            }

            (double lx, double ly) = LabelPoint(s.Outer);
            (double px, double py) = map(lx, ly);
            (byte r, byte g, byte b) = colours[name];
            int tw = BitmapFont.Measure(name, 1);
            c.Text((int)px - (tw / 2), (int)py - 4, name, 1, r, g, b, box: true);
        }
    }

    /// <summary>A point inside the ring for its label: the centroid when inside, else the middle of the widest scanline span through it.</summary>
    internal static (double X, double Y) LabelPoint(IReadOnlyList<(double X, double Y)> ring)
    {
        double cx = ring.Average(p => p.X), cy = ring.Average(p => p.Y);
        if (RefMask.Inside(ring, cx, cy))
        {
            return (cx, cy);
        }

        var xs = new List<double>();
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[j].Y > cy) != (ring[i].Y > cy))
            {
                xs.Add(ring[j].X + ((cy - ring[j].Y) * (ring[i].X - ring[j].X) / (ring[i].Y - ring[j].Y)));
            }
        }

        xs.Sort();
        double best = -1, bx = cx;
        for (int k = 0; k + 1 < xs.Count; k += 2)
        {
            if (xs[k + 1] - xs[k] > best)
            {
                best = xs[k + 1] - xs[k];
                bx = (xs[k] + xs[k + 1]) / 2;
            }
        }

        return (bx, cy);
    }

    /// <summary>A circle as a 24-gon (kit.trace solids given as circle = {x, y, r}).</summary>
    public static List<(double X, double Y)> Circle(double cx, double cy, double r) =>
        Enumerable.Range(0, 24).Select(i => (cx + (r * Math.Cos(i * Math.PI / 12)), cy + (r * Math.Sin(i * Math.PI / 12)))).ToList();

    internal static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
