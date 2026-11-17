using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Ged.Core.Analysis;
using Ged.Core.Spatial;
using Ged.Rendering;

namespace Ged.Automation.Rendering;

/// <summary>
/// The floor-heights layer of a plan or top render (<see cref="RenderOverlays.Heights"/>, mode <c>heights</c>): every
/// pixel over walkable floor takes the height colour (<see cref="HeightColorMap"/>) of the floor under it, found from
/// the cut plane down (the top of the level in a top view), as the image shows it. Overlay mode blends the colour over
/// the lit pixel's luminance so the texture still reads; the heights mode draws the colour alone and greys every other
/// surface, a clean image <c>ref.heights</c> decodes. Sloped floor gets contour lines (the colour darkened), stairs
/// their tread lines (the colour lightened) — both keep the hue, so they decode as floor. The symbols (chevrons, stair
/// arrows, drop edges, labels) are drawn later by <see cref="OverlayPainter"/>; this keeps what they need: the floor
/// height, feature and walkability of every pixel.
/// </summary>
internal sealed class HeightsLayer
{
    /// <summary>How heights map to colours (the JSON result's <c>colormap</c>).</summary>
    public const string ColormapText = "hue = 240 * (1 - t) degrees (HSV, full saturation; t = (y - min) / (max - min)): blue low, cyan, green, yellow, red high. Overlay mode blends it over the texture's grey; contours darken and stair treads lighten it, keeping the hue.";

    /// <summary>The colour's weight over the texture's luminance in overlay mode.</summary>
    private const double Alpha = 0.55;

    /// <summary>Floors seen through an opening below the slice are drawn this much darker.</summary>
    private const double BelowDim = 0.6;

    /// <summary>Floor further than this (m) outside the colour range is left untinted (grey in the heights mode).</summary>
    private const double OutOfRange = 0.25;

    private HeightsLayer(HeightsReading reading, int w, int h, double min, double max, double step, bool pure)
    {
        Reading = reading;
        W = w;
        H = h;
        Min = min;
        Max = max;
        ContourStep = step;
        Pure = pure;
        Y = new float[w * h];
        Array.Fill(Y, float.NaN);
        Kind = new byte[w * h];
        Feature = new int[w * h];
        Array.Fill(Feature, -1);
    }

    public HeightsReading Reading { get; }

    public int W { get; }

    public int H { get; }

    /// <summary>The colour map's range (m): the walkable floor's range, widened to whole half metres.</summary>
    public double Min { get; }

    public double Max { get; }

    /// <summary>The contour interval on sloped floor (m).</summary>
    public double ContourStep { get; }

    /// <summary>The heights mode: colours alone, other surfaces grey.</summary>
    public bool Pure { get; }

    /// <summary>Per pixel: the walkable floor's height (NaN where none shows).</summary>
    public float[] Y { get; }

    /// <summary>Per pixel: the walkable floor's feature kind (<see cref="HeightsReading.KindFlat"/>...).</summary>
    public byte[] Kind { get; }

    /// <summary>Per pixel: the feature's index (−1 for none).</summary>
    public int[] Feature { get; }

    /// <summary>Pixels showing each feature (kind, index): what the image shows of it.</summary>
    public Dictionary<(int Kind, int Index), int> Shown { get; } = new();

    /// <summary>The colour of a height.</summary>
    public Rgba ColorOf(double y)
    {
        (byte r, byte g, byte b) = HeightColorMap.ColorOf(y, Min, Max);
        return new Rgba(r, g, b);
    }

    /// <summary>The colour map's range for a reading: whole half metres around the floor's range (at least 1 m).</summary>
    public static (double Min, double Max) RangeOf(HeightsReading reading)
    {
        double lo = Math.Floor(reading.RangeMin * 2) / 2, hi = Math.Ceiling(reading.RangeMax * 2) / 2;
        if (hi - lo < 1)
        {
            double mid = (lo + hi) / 2;
            lo = Math.Floor((mid - 0.5) * 2) / 2;
            hi = lo + 1;
        }

        return (lo, hi);
    }

    /// <summary>
    /// Tints <paramref name="canvas"/> (see the class summary). <paramref name="top"/> is the height the floor search
    /// starts at (the plan's cut, else above the level); <paramref name="solid"/> marks a plan's solid pixels (left
    /// alone) and <paramref name="openBelow"/> its floors seen through an opening (drawn darker).
    /// </summary>
    public static HeightsLayer Paint(RgbaCanvas canvas, Camera cam, HeightsReading reading, double top, bool[]? solid, bool[]? openBelow, bool pure)
    {
        int w = canvas.Width, h = canvas.Height;
        (double min, double max) = RangeOf(reading);
        double step = max - min > 24 ? 1.0 : 0.5;
        var layer = new HeightsLayer(reading, w, h, min, max, step, pure);
        float wpp = cam.WorldPerPixel(Vector3.Zero, h);
        Vector3 right = cam.Right * wpp, down = -cam.Up * wpp;
        float halfH = cam.OrthoZoom, halfW = halfH * cam.AspectRatio;
        Vector3 origin = cam.Position - (cam.Right * halfW) + (cam.Up * halfH);
        byte[] px = canvas.Pixels;
        var sloped = new bool[w * h];

        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)) }, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                if (solid is not null && solid[p])
                {
                    continue;
                }

                Vector3 world = origin + (right * (x + 0.5f)) + (down * (y + 0.5f));
                HeightsHit? hit = reading.FloorAt(world.X, world.Z, top);
                int i = p * 4;
                if (hit is not HeightsHit f || !f.Walkable)
                {
                    if (pure && hit is not null)
                    {
                        // Another surface (a wall top, a prop, unreachable floor): grey, so it never reads as a height.
                        byte l = (byte)(Luma(px[i], px[i + 1], px[i + 2]) * 0.55);
                        px[i] = px[i + 1] = px[i + 2] = (byte)Math.Max((int)l, 48);
                    }

                    continue;
                }

                layer.Y[p] = (float)f.Y;
                layer.Kind[p] = (byte)f.Kind;
                layer.Feature[p] = f.Feature;
                sloped[p] = f.Sloped;
                if (f.Y < min - OutOfRange || f.Y > max + OutOfRange)
                {
                    // Floor beyond the colour range (a kill pit far below the play): no colour, so it never reads as the
                    // bar's end; its spot height says where it is.
                    if (pure)
                    {
                        byte l = (byte)(Luma(px[i], px[i + 1], px[i + 2]) * 0.55);
                        px[i] = px[i + 1] = px[i + 2] = (byte)Math.Max((int)l, 48);
                    }

                    continue;
                }

                (byte r, byte g, byte b) = HeightColorMap.ColorOf(f.Y, min, max);
                double dim = openBelow is not null && openBelow[p] ? BelowDim : 1.0;
                if (pure)
                {
                    px[i] = (byte)Math.Round(r * dim);
                    px[i + 1] = (byte)Math.Round(g * dim);
                    px[i + 2] = (byte)Math.Round(b * dim);
                }
                else
                {
                    double l = Luma(px[i], px[i + 1], px[i + 2]) * (1 - Alpha);
                    px[i] = (byte)Math.Round((r * Alpha * dim) + l);
                    px[i + 1] = (byte)Math.Round((g * Alpha * dim) + l);
                    px[i + 2] = (byte)Math.Round((b * Alpha * dim) + l);
                }

                px[i + 3] = 255;
            }
        });

        // Contours on sloped floor, tread lines on stairs: where the next pixel right or down crosses a contour height or
        // a riser. Both keep the pixel's hue.
        var contour = new bool[w * h];
        var tread = new bool[w * h];
        var sums = new Dictionary<(int Kind, int Index), (double X, double Y)>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                float a = layer.Y[p];
                if (float.IsNaN(a))
                {
                    continue;
                }

                for (int side = 0; side < 2; side++)
                {
                    int q = side == 0 ? (x + 1 < w ? p + 1 : -1) : (y + 1 < h ? p + w : -1);
                    if (q < 0 || float.IsNaN(layer.Y[q]))
                    {
                        continue;
                    }

                    float b = layer.Y[q];
                    if (layer.Kind[p] == HeightsReading.KindStairs && layer.Kind[q] == HeightsReading.KindStairs && Math.Abs(a - b) > 0.04)
                    {
                        tread[a > b ? q : p] = true; // the lower tread's edge, under the riser
                    }
                    else if ((sloped[p] || sloped[q]) && Math.Abs(a - b) < 0.6 && Math.Floor(a / step) != Math.Floor(b / step))
                    {
                        contour[a > b ? q : p] = true;
                    }
                }
            }
        }

        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            if (contour[p])
            {
                px[i] = (byte)(px[i] * 0.45);
                px[i + 1] = (byte)(px[i + 1] * 0.45);
                px[i + 2] = (byte)(px[i + 2] * 0.45);
            }
            else if (tread[p])
            {
                px[i] = (byte)(px[i] + ((255 - px[i]) * 0.4));
                px[i + 1] = (byte)(px[i + 1] + ((255 - px[i + 1]) * 0.4));
                px[i + 2] = (byte)(px[i + 2] + ((255 - px[i + 2]) * 0.4));
            }

            if (!float.IsNaN(layer.Y[p]) && layer.Feature[p] >= 0)
            {
                (int, int) key = (layer.Kind[p], layer.Feature[p]);
                layer.Shown[key] = layer.Shown.GetValueOrDefault(key) + 1;
                (double sx, double sy) = sums.GetValueOrDefault(key);
                sums[key] = (sx + (p % w), sy + (p / w));
            }
        }

        // Each feature's anchor: its shown pixel nearest the middle of what is shown (labels sit by what the image shows),
        // and up to 64 of its shown pixels spread over it (more places to try a label).
        var best = new Dictionary<(int Kind, int Index), double>();
        var seen = new Dictionary<(int Kind, int Index), int>();
        for (int p = 0; p < w * h; p++)
        {
            if (float.IsNaN(layer.Y[p]) || layer.Feature[p] < 0)
            {
                continue;
            }

            (int, int) key = (layer.Kind[p], layer.Feature[p]);
            int n = layer.Shown[key];
            int k = seen.GetValueOrDefault(key);
            seen[key] = k + 1;
            if (k % Math.Max(1, n / 64) == 0)
            {
                if (!layer.Spots.TryGetValue(key, out List<Vector2>? spots))
                {
                    layer.Spots[key] = spots = new List<Vector2>();
                }

                spots.Add(new Vector2((p % w) + 0.5f, (p / w) + 0.5f));
            }

            (double sx, double sy) = sums[key];
            double dx = (p % w) - (sx / n), dy = (p / w) - (sy / n), d2 = (dx * dx) + (dy * dy);
            if (!best.TryGetValue(key, out double b) || d2 < b)
            {
                best[key] = d2;
                layer.Anchor[key] = new Vector2((p % w) + 0.5f, (p / w) + 0.5f);
            }
        }

        return layer;
    }

    /// <summary>Per feature: the shown pixel nearest the middle of its shown pixels.</summary>
    public Dictionary<(int Kind, int Index), Vector2> Anchor { get; } = new();

    /// <summary>Per feature: up to about 64 of its shown pixels, spread over it.</summary>
    public Dictionary<(int Kind, int Index), List<Vector2>> Spots { get; } = new();

    /// <summary>A world point as JSON [x, y, z] (centimetres).</summary>
    public static double[] Point(Ged.Core.Editing.Organic.Vec3d p) => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2) };

    /// <summary>The walkable floor height at a pixel (NaN when none or off the image).</summary>
    public float At(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? Y[(y * W) + x] : float.NaN;

    /// <summary>True when the image shows at least <paramref name="pixels"/> of a feature.</summary>
    public bool Shows(int kind, int index, int pixels = 12) => Shown.GetValueOrDefault((kind, index)) >= pixels;

    private static double Luma(byte r, byte g, byte b) => (0.299 * r) + (0.587 * g) + (0.114 * b);
}
