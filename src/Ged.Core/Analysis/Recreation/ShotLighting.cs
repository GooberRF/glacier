using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Analysis.Recreation;

/// <summary>
/// A reference shot's lighting, read off its pixels (Lua <c>ref.shot_lighting</c>). Deliberately simple:
/// <list type="bullet">
/// <item>brightness is the mean luminance (0-1) and contrast the 5th-95th percentile spread;</item>
/// <item>the ambient is the mean colour of the darkest quarter of the pixels (what unlit surfaces show);</item>
/// <item>the light colours come from the brightest 8 % of the pixels (lamps, lit patches, sky), grouped by hue into
/// 30-degree bins plus a "white" bin for the unsaturated ones; the up to three heaviest groups are the lights, each
/// with its colour normalised so the brightest channel is 255, its brightness and its share of the highlights;</item>
/// <item>the fog colour is the mean of the picture's upper third (sky or haze far away).</item>
/// </list>
/// The suggestions map these to the level: the main light's colour and an intensity (0.4-2.0, about 2.5 x the mean luminance: a dark shot gets dim lights; from how bright the
/// the shot is overall), an ambient colour on the stock levels' scale (dark interiors carry 20-50 per channel)
/// and the fog colour.
/// </summary>
public static class ShotLighting
{
    /// <summary>One light colour found in the highlights.</summary>
    public sealed record Light(string Name, int[] Color, double Brightness, double Share);

    /// <summary>The estimate.</summary>
    public sealed record Result(double Brightness, double Contrast, string Tint, int[] AmbientSeen, double AmbientLevel, string AmbientName, IReadOnlyList<Light> Lights,
        int[] LightColor, double Intensity, int[] AmbientColor, int[] FogColor, string Summary);

    /// <summary>Estimates the lighting of the picture's rectangle (optionally only the pixels inside).</summary>
    public static Result Estimate(RefImage img, int[]? rect, Func<int, int, bool>? inside)
    {
        int x0 = rect?[0] ?? 0, y0 = rect?[1] ?? 0, w = rect?[2] ?? img.Width, h = rect?[3] ?? img.Height;
        int step = Math.Max(1, (int)Math.Sqrt((double)w * h / 250_000)); // sample about 250k pixels at most
        var px = new List<(double Y, byte R, byte G, byte B)>();
        double fr = 0, fg = 0, fb = 0;
        int fn = 0;
        for (int y = y0; y < y0 + h; y += step)
        {
            for (int x = x0; x < x0 + w; x += step)
            {
                if (inside is not null && !inside(x, y))
                {
                    continue;
                }

                (byte r, byte g, byte b) = img.At(x, y);
                double lum = ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255.0;
                px.Add((lum, r, g, b));
                if (y < y0 + (h / 3))
                {
                    fr += r;
                    fg += g;
                    fb += b;
                    fn++;
                }
            }
        }

        if (px.Count == 0)
        {
            throw new InvalidOperationException("no pixels to read");
        }

        px.Sort((a, b) => a.Y.CompareTo(b.Y));
        double mean = px.Average(p => p.Y);
        double p05 = px[(int)(0.05 * (px.Count - 1))].Y, p95 = px[(int)(0.95 * (px.Count - 1))].Y;

        // Ambient: the darkest quarter.
        int q = Math.Max(1, px.Count / 4);
        double ar = 0, ag = 0, ab = 0, al = 0;
        for (int i = 0; i < q; i++)
        {
            ar += px[i].R;
            ag += px[i].G;
            ab += px[i].B;
            al += px[i].Y;
        }

        int[] ambientSeen = { (int)Math.Round(ar / q), (int)Math.Round(ag / q), (int)Math.Round(ab / q) };
        double ambientLevel = al / q;

        // Highlights: the brightest 8 % (at least a few pixels), grouped by hue.
        int top = Math.Max(4, (int)(px.Count * 0.08));
        var groups = new Dictionary<int, (double R, double G, double B, double Y, int N)>();
        for (int i = px.Count - top; i < px.Count; i++)
        {
            (double lum, byte r, byte g, byte b) = px[i];
            (double hue, double sat) = HueSat(r, g, b);
            int key = sat < 0.18 ? -1 : (int)(hue / 30) % 12;
            groups.TryGetValue(key, out var acc);
            groups[key] = (acc.R + r, acc.G + g, acc.B + b, acc.Y + lum, acc.N + 1);
        }

        var lights = groups.OrderByDescending(kv => kv.Value.N).Take(3)
            .Where(kv => kv.Value.N >= Math.Max(2, top / 20))
            .Select(kv =>
            {
                var v = kv.Value;
                double r = v.R / v.N, g = v.G / v.N, b = v.B / v.N;
                double m = Math.Max(1, Math.Max(r, Math.Max(g, b)));
                int[] col = { (int)Math.Round(r / m * 255), (int)Math.Round(g / m * 255), (int)Math.Round(b / m * 255) };
                return new Light(ColorName(r, g, b), col, Math.Round(v.Y / v.N, 3), Math.Round((double)v.N / top, 3));
            }).ToList();

        int[] lightColor = lights.Count > 0 ? lights[0].Color : new[] { 255, 255, 255 };
        double keyBright = lights.Count > 0 ? lights[0].Brightness : p95;
        // A dark shot wants dim lights, a bright one strong lights: about 2.5 x the mean luminance, 0.4-2.0.
        double intensity = Math.Round(Math.Clamp(2.5 * mean * (0.85 + (0.3 * Math.Clamp(keyBright - mean, 0, 1))), 0.4, 2.0), 2);

        // The level ambient: the seen dark colour on the stock scale (about 0.6 of what the darkest quarter shows,
        // capped at 80 per channel so lights still read).
        int[] ambientColor = ambientSeen.Select(c => Math.Clamp((int)Math.Round(c * 0.6), 0, 80)).ToArray();
        int[] fog = fn > 0 ? new[] { (int)Math.Round(fr / fn), (int)Math.Round(fg / fn), (int)Math.Round(fb / fn) } : ambientSeen;

        string tint = Tint(px);
        string ambientName = $"{Level(ambientLevel)} {ColorName(ambientSeen[0], ambientSeen[1], ambientSeen[2])}";
        var parts = new List<string>();
        for (int i = 0; i < lights.Count; i++)
        {
            Light l = lights[i];
            string role = i == 0 ? "key" : "accents";
            parts.Add($"{Level(l.Brightness)} {l.Name} {role} {RefCanvas.N(l.Brightness)} ({Math.Round(l.Share * 100)} % of highlights)");
        }

        parts.Add($"{ambientName} ambient {RefCanvas.N(Math.Round(ambientLevel, 2))}");
        string summary = $"{tint} overall, mean {RefCanvas.N(Math.Round(mean, 2))}: " + string.Join(", ", parts);
        return new Result(Math.Round(mean, 3), Math.Round(p95 - p05, 3), tint, ambientSeen, Math.Round(ambientLevel, 3), ambientName, lights,
            lightColor, intensity, ambientColor, fog, summary);
    }

    private static string Tint(List<(double Y, byte R, byte G, byte B)> px)
    {
        double a = 0, b = 0;
        int step = Math.Max(1, px.Count / 20000);
        int n = 0;
        for (int i = 0; i < px.Count; i += step)
        {
            (_, double la, double lb) = ShotMaterials.ToLab(ShotMaterials.Lin(px[i].R), ShotMaterials.Lin(px[i].G), ShotMaterials.Lin(px[i].B));
            a += la;
            b += lb;
            n++;
        }

        a /= n;
        b /= n;
        double c = Math.Sqrt((a * a) + (b * b));
        if (c < 4)
        {
            return "neutral";
        }

        double hue = Math.Atan2(b, a) * 180 / Math.PI;
        // Lab hue: red ~ 30, orange/yellow 60-100, green ~ 140, blue ~ 260-300, purple ~ 320.
        return hue is > -30 and < 110 ? (c > 12 ? "strongly warm" : "warm") : hue is >= 110 and < 180 ? "greenish" : (c > 12 ? "strongly cool" : "cool");
    }

    private static string Level(double y) => y switch
    {
        < 0.12 => "dark",
        < 0.3 => "dim",
        < 0.55 => "medium",
        < 0.8 => "bright",
        _ => "very bright",
    };

    private static (double Hue, double Sat) HueSat(double r, double g, double b)
    {
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double d = mx - mn;
        double sat = mx <= 0 ? 0 : d / mx;
        if (d < 1e-9)
        {
            return (0, 0);
        }

        double h = mx == r ? 60 * (((g - b) / d) % 6) : mx == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
        return (h < 0 ? h + 360 : h, sat);
    }

    /// <summary>A plain colour name for an sRGB colour (white / grey for unsaturated ones).</summary>
    public static string ColorName(double r, double g, double b)
    {
        (double h, double s) = HueSat(r, g, b);
        double v = Math.Max(r, Math.Max(g, b)) / 255.0;
        if (s < 0.18)
        {
            return v > 0.75 ? "white" : v < 0.2 ? "black" : "grey";
        }

        string name = h switch
        {
            < 15 => "red",
            < 40 => "orange",
            < 65 => "yellow",
            < 160 => "green",
            < 200 => "cyan",
            < 255 => "blue",
            < 290 => "purple",
            < 340 => "magenta",
            _ => "red",
        };
        return s < 0.4 ? "pale " + name : name;
    }
}
