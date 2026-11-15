using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Automation.Reference;

/// <summary>The cues of one picture (reference or render), measured identically on both.</summary>
public sealed class PictureCues
{
    /// <summary>Share of edge pixels over the whole picture (detail density), 0..1.</summary>
    public double EdgeDensity { get; set; }

    /// <summary>Edge density by region: rows top / middle / bottom, each [left, centre, right].</summary>
    public double[][] EdgeGrid { get; set; } = Array.Empty<double[]>();

    /// <summary>
    /// Share of the frame above the first structure line (the first edge from the top), column by column: open sky, or
    /// a plain surface such as a dark ceiling or a bare wall (0 = structure right at the top edge, 1 = no edge at all).
    /// </summary>
    public double PlainTop { get; set; }

    /// <summary>The first structure line's height in ten column bands (fraction from the top; lower = taller structure).</summary>
    public double[] Skyline { get; set; } = Array.Empty<double>();

    /// <summary>Mean brightness 0..1.</summary>
    public double Brightness { get; set; }

    /// <summary>Brightness by region (rows top / middle / bottom, each [left, centre, right]).</summary>
    public double[][] BrightnessGrid { get; set; } = Array.Empty<double[]>();

    /// <summary>Mean colour [r, g, b] 0..255.</summary>
    public double[] Colour { get; set; } = Array.Empty<double>();

    /// <summary>(mean red - mean blue) / 255: positive = warm (orange, rust, red light), negative = cool (grey-blue).</summary>
    public double Warmth { get; set; }

    /// <summary>The horizon's row (fraction from the top): marked on the reference, from the camera pitch on the render.</summary>
    public double? Horizon { get; set; }
}

/// <summary>Numbers comparing a reference with its matched render, plus plain-language hints.</summary>
public sealed class CompareCues
{
    /// <summary>The analysis size both pictures were measured at.</summary>
    public int[] AnalysisSize { get; set; } = Array.Empty<int>();

    public PictureCues Reference { get; set; } = new();

    public PictureCues Render { get; set; } = new();

    /// <summary>Share of the reference's edge pixels with a render edge within 2 analysis pixels (and the converse).</summary>
    public double EdgeOverlapReference { get; set; }

    /// <summary>Share of the render's edge pixels with a reference edge within 2 analysis pixels.</summary>
    public double EdgeOverlapRender { get; set; }

    /// <summary>Plain-language differences worth checking (heuristic; see the notes).</summary>
    public List<string> Hints { get; set; } = new();
}

/// <summary>
/// Measures pictures for reference matching: a contrast-normalised Sobel edge map, the "plain top" (sky, or a plain ceiling or wall
/// above the first structure line), a ten-band skyline, edge density and brightness by region, colour warmth, and the
/// overlap of two edge maps. Both pictures are resampled to one analysis size first, so the numbers compare like with
/// like. These are hints for an agent's own judgement, not a score: concept art and a game render differ in style.
/// </summary>
internal static class ImageCues
{
    /// <summary>The gradient magnitude (on contrast-normalised luma) above which a pixel is an edge.</summary>
    public const float EdgeThreshold = 0.22f;

    /// <summary>The analysis width for a reference of <paramref name="refWidth"/> pixels.</summary>
    public static int AnalysisWidth(int refWidth) => Math.Clamp(refWidth, 160, 320);

    /// <summary>
    /// The edge map of <paramref name="img"/>: luma normalised to its 2nd-98th percentile range, a 3x3 box blur, then
    /// the Sobel magnitude over <see cref="EdgeThreshold"/>. The one-pixel border is never an edge.
    /// </summary>
    public static bool[] Edges(RasterImage img)
    {
        int w = img.Width, h = img.Height;
        float[] l = img.Luma();
        float[] sorted = (float[])l.Clone();
        Array.Sort(sorted);
        float lo = sorted[(int)(sorted.Length * 0.02)], hi = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.98))];
        float range = MathF.Max(hi - lo, 0.05f);
        var n = new float[l.Length];
        for (int i = 0; i < l.Length; i++)
        {
            n[i] = Math.Clamp((l[i] - lo) / range, 0f, 1f);
        }

        var b = new float[l.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                int c = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((uint)xx < (uint)w && (uint)yy < (uint)h)
                        {
                            s += n[(yy * w) + xx];
                            c++;
                        }
                    }
                }

                b[(y * w) + x] = s / c;
            }
        }

        var e = new bool[l.Length];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                float P(int dx, int dy) => b[((y + dy) * w) + x + dx];
                float gx = (P(1, -1) + (2 * P(1, 0)) + P(1, 1)) - (P(-1, -1) + (2 * P(-1, 0)) + P(-1, 1));
                float gy = (P(-1, 1) + (2 * P(0, 1)) + P(1, 1)) - (P(-1, -1) + (2 * P(0, -1)) + P(1, -1));
                e[(y * w) + x] = MathF.Sqrt((gx * gx) + (gy * gy)) * 0.25f > EdgeThreshold;
            }
        }

        return e;
    }

    /// <summary>The mask of pixels to ignore (rectangles [x, y, w, h] in picture fractions, flattened).</summary>
    public static bool[] IgnoreMask(int w, int h, double[]? ignore)
    {
        var m = new bool[w * h];
        if (ignore is null)
        {
            return m;
        }

        for (int i = 0; i + 3 < ignore.Length; i += 4)
        {
            int x0 = (int)Math.Floor(ignore[i] * w), y0 = (int)Math.Floor(ignore[i + 1] * h);
            int x1 = (int)Math.Ceiling((ignore[i] + ignore[i + 2]) * w), y1 = (int)Math.Ceiling((ignore[i + 1] + ignore[i + 3]) * h);
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    m[(y * w) + x] = true;
                }
            }
        }

        return m;
    }

    /// <summary>The first edge row of each column from the top (h when the column has none), skipping ignored pixels.</summary>
    public static int[] FirstEdgeRows(bool[] edges, bool[] ignore, int w, int h)
    {
        var rows = new int[w];
        for (int x = 0; x < w; x++)
        {
            rows[x] = h;
            for (int y = 1; y < h; y++)
            {
                int i = (y * w) + x;
                if (edges[i] && !ignore[i])
                {
                    rows[x] = y;
                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>Measures one picture (already at the analysis size).</summary>
    public static PictureCues Measure(RasterImage img, bool[] edges, bool[] ignore, double? horizon)
    {
        int w = img.Width, h = img.Height;
        float[] l = img.Luma();
        var cues = new PictureCues { Horizon = horizon is double hz ? R(hz, 3) : null };

        int[] first = FirstEdgeRows(edges, ignore, w, h);
        cues.PlainTop = R(first.Average(r => (double)r) / h, 3);
        cues.Skyline = Enumerable.Range(0, 10).Select(band =>
        {
            int x0 = band * w / 10, x1 = Math.Max(x0 + 1, (band + 1) * w / 10);
            return R(first.Skip(x0).Take(x1 - x0).Average(r => (double)r) / h, 3);
        }).ToArray();

        double edgeCount = 0, kept = 0, lum = 0, rs = 0, gs = 0, bs = 0;
        var eg = new double[3, 3];
        var ek = new double[3, 3];
        var lg = new double[3, 3];
        var lk = new double[3, 3];
        for (int y = 0; y < h; y++)
        {
            int gyCell = Math.Min(2, y * 3 / h);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x, gxCell = Math.Min(2, x * 3 / w);
                if (ignore[i])
                {
                    continue;
                }

                kept++;
                lum += l[i];
                rs += img.Pixels[i * 4];
                gs += img.Pixels[(i * 4) + 1];
                bs += img.Pixels[(i * 4) + 2];
                lg[gyCell, gxCell] += l[i];
                lk[gyCell, gxCell]++;
                ek[gyCell, gxCell]++;
                if (edges[i])
                {
                    edgeCount++;
                    eg[gyCell, gxCell]++;
                }
            }
        }

        kept = Math.Max(kept, 1);
        cues.EdgeDensity = R(edgeCount / kept, 3);
        cues.Brightness = R(lum / kept, 3);
        cues.Colour = new[] { R(rs / kept, 1), R(gs / kept, 1), R(bs / kept, 1) };
        cues.Warmth = R(((rs / kept) - (bs / kept)) / 255, 3);
        cues.EdgeGrid = Grid(eg, ek);
        cues.BrightnessGrid = Grid(lg, lk);
        return cues;
    }

    /// <summary>Share of <paramref name="a"/>'s edge pixels with an edge of <paramref name="b"/> within <paramref name="radius"/> pixels.</summary>
    public static double Overlap(bool[] a, bool[] b, bool[] ignore, int w, int h, int radius = 2)
    {
        int total = 0, near = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                if (!a[i] || ignore[i])
                {
                    continue;
                }

                total++;
                bool found = false;
                for (int dy = -radius; dy <= radius && !found; dy++)
                {
                    int yy = y + dy;
                    if ((uint)yy >= (uint)h)
                    {
                        continue;
                    }

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int xx = x + dx;
                        if ((uint)xx < (uint)w && b[(yy * w) + xx])
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                {
                    near++;
                }
            }
        }

        return total == 0 ? 0 : R((double)near / total, 3);
    }

    private static readonly string[] RowNames = { "top", "middle", "bottom" };

    private static readonly string[] ColumnNames = { "left", "centre", "right" };

    /// <summary>
    /// Plain-language differences between the reference and the render cues that pass a size threshold (heuristic,
    /// FEATURES approximation 45): detail density overall and by region, the plain top and the skyline (the latter only under a sky-bright reference top), brightness,
    /// colour warmth, and the horizon (with the pitch change that would align it).
    /// </summary>
    public static List<string> Hints(PictureCues reference, PictureCues render, double hfov, double aspect, double? renderPitchDeg)
    {
        var hints = new List<string>();
        string F(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        double er = reference.EdgeDensity, en = render.EdgeDensity;
        if (er > 0.01 && en < er * 0.65)
        {
            (int gy, int gx) = WorstCell(reference.EdgeGrid, render.EdgeGrid, wantRenderLower: true);
            hints.Add($"DETAIL: the render is plainer than the reference (edge density {F(en)} vs {F(er)}); the gap is largest {RowNames[gy]}-{ColumnNames[gx]} " +
                $"({F(render.EdgeGrid[gy][gx])} vs {F(reference.EdgeGrid[gy][gx])}). Add the structure the reference shows there: wall panels and pilasters, beams, railings, pipes, crates, trim.");
        }
        else if (en > 0.01 && en > er * 1.6)
        {
            hints.Add($"DETAIL: the render is busier than the reference (edge density {F(en)} vs {F(er)}): fewer, larger forms read closer to it.");
        }

        // Above the first structure line is either sky or a plain surface (a dark ceiling, a bare wall): the reference's
        // top-band brightness tells them apart well enough to word the hint, and the skyline hints only make sense under sky.
        bool refSky = reference.BrightnessGrid.Length == 3 && reference.BrightnessGrid[0].Average() >= 0.2;
        double open = reference.PlainTop - render.PlainTop;
        if (open > 0.1)
        {
            hints.Add(refSky
                ? $"MASSING: more of the reference's frame is open sky above its structure (plain top {F(reference.PlainTop)} vs {F(render.PlainTop)}): in the render, walls or a roof rise too high or stand too close, or the space is roofed where the reference is open."
                : $"MASSING: the top of the reference is plainer and darker (plain top {F(reference.PlainTop)} vs {F(render.PlainTop)}): a dark, shadowed ceiling where the render's upper frame is busier or brighter; a lower, darker ceiling with fewer lit surfaces reads closer.");
        }
        else if (open < -0.1)
        {
            hints.Add($"MASSING: the render's upper frame is plainer than the reference's (plain top {F(render.PlainTop)} vs {F(reference.PlainTop)}): sky, or a bare wall or ceiling, where the reference has structure; raise or add walls, walkways, beams and lamps up there, or bring a wall closer.");
        }

        if (refSky && reference.Skyline.Length == 10 && render.Skyline.Length == 10)
        {
            string[] thirds = { "left", "centre", "right" };
            for (int t = 0; t < 3; t++)
            {
                int b0 = t * 10 / 3, b1 = (t + 1) * 10 / 3;
                double rs = reference.Skyline.Skip(b0).Take(b1 - b0).Average(), ns = render.Skyline.Skip(b0).Take(b1 - b0).Average();
                if (ns - rs > 0.15)
                {
                    hints.Add($"HEIGHT: on the {thirds[t]} the reference's structure reaches higher in the frame (skyline {F(rs)} vs {F(ns)} from the top): build taller there.");
                }
                else if (rs - ns > 0.15)
                {
                    hints.Add($"HEIGHT: on the {thirds[t]} the render's structure reaches higher in the frame than the reference's (skyline {F(ns)} vs {F(rs)}): lower it or move it back.");
                }
            }
        }

        if (Math.Abs(reference.Brightness - render.Brightness) > 0.1)
        {
            hints.Add(reference.Brightness > render.Brightness
                ? $"LIGHT: the render is darker than the reference (brightness {F(render.Brightness)} vs {F(reference.Brightness)}): more or stronger lights, or a lighter palette (or bake lighting if the level is unlit)."
                : $"LIGHT: the render is brighter than the reference (brightness {F(render.Brightness)} vs {F(reference.Brightness)}).");
        }

        if (reference.Warmth - render.Warmth > 0.08)
        {
            hints.Add($"MATERIALS: the reference is warmer (red-minus-blue {F(reference.Warmth)} vs {F(render.Warmth)}): orange/red light, rust, red rock or hazard trim where the render is grey.");
        }
        else if (render.Warmth - reference.Warmth > 0.08)
        {
            hints.Add($"MATERIALS: the render is warmer than the reference (red-minus-blue {F(render.Warmth)} vs {F(reference.Warmth)}).");
        }

        if (reference.Horizon is double hr && render.Horizon is double hn && Math.Abs(hr - hn) > 0.04 && renderPitchDeg is double pitch)
        {
            double fy = ReferenceCamera.FocalHeights(hfov, aspect);
            double want = Math.Atan((hr - 0.5) / fy) * 180 / Math.PI;
            hints.Add($"CAMERA: the horizons differ (reference {F(hr)}, render {F(hn)} from the top): pitch the camera {(want > pitch ? "up" : "down")} by about {Math.Abs(want - pitch):0.#} degrees (turn 0,{want - pitch:0.#}).");
        }

        return hints;
    }

    private static (int Y, int X) WorstCell(double[][] reference, double[][] render, bool wantRenderLower)
    {
        (int, int) best = (1, 1);
        double gap = double.MinValue;
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                double g = wantRenderLower ? reference[y][x] - render[y][x] : render[y][x] - reference[y][x];
                if (g > gap)
                {
                    gap = g;
                    best = (y, x);
                }
            }
        }

        return best;
    }

    private static double[][] Grid(double[,] sum, double[,] count) =>
        Enumerable.Range(0, 3).Select(y => Enumerable.Range(0, 3).Select(x => count[y, x] > 0 ? R(sum[y, x] / count[y, x], 3) : 0).ToArray()).ToArray();

    private static double R(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
}
