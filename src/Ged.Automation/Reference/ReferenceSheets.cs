using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ged.Automation.Rendering;
using Ged.Core.IO.Tex;

namespace Ged.Automation.Reference;

/// <summary>
/// The images of reference matching: the comparison sheet (reference | render over blend | edge overlay, on one
/// fraction grid, with both horizons and the skylines), the marked-reference view, and the coordinate-grid views of a
/// source image used to pick crop rectangles. Software-drawn and deterministic.
/// </summary>
internal static class ReferenceSheets
{
    public static readonly Rgba RefColour = new(255, 150, 30);
    public static readonly Rgba RenderColour = new(40, 200, 255);
    public static readonly Rgba MarkColour = new(255, 230, 40);
    public static readonly Rgba MeasureColour = new(90, 255, 120);

    private const int Gap = 8;
    private const int Text2 = 2;

    private static readonly Rgba Background = new(24, 24, 28);
    private static readonly Rgba Ink = new(235, 235, 235);
    private static readonly Rgba Dim = new(170, 170, 175);

    /// <summary>
    /// The comparison sheet. <paramref name="reference"/> and <paramref name="render"/> are already the panel size;
    /// <paramref name="refEdges"/> / <paramref name="renderEdges"/> and the first-edge rows are at the analysis size.
    /// </summary>
    public static RasterImage CompareSheet(
        RasterImage reference,
        RasterImage render,
        (bool[] Edges, int[] FirstRows) refA,
        (bool[] Edges, int[] FirstRows) renA,
        int aw,
        int ah,
        ReferenceMarks? marks,
        double? renderHorizon,
        IReadOnlyList<string> header,
        IReadOnlyList<string> footer)
    {
        int w = reference.Width, h = reference.Height;
        int ts = w >= 420 ? Text2 : 1; // text scale: large on large panels, and every line wrapped to the sheet's width
        int sw = (2 * w) + (3 * Gap);
        int maxChars = Math.Max(10, (sw - (2 * Gap)) / (BitmapFont.Advance * ts));
        List<(string Text, bool Dim)> head = Wrap(header, maxChars, i => i > 0);
        List<(string Text, bool Dim)> foot = Wrap(footer, maxChars, i => i == footer.Count - 1);
        int line = BitmapFont.LineHeight(ts) + (3 * ts);
        int headH = (head.Count * line) + Gap;
        int footH = (foot.Count * line) + Gap;
        int sh = headH + (2 * h) + (3 * Gap) + footH;
        RasterImage sheet = RasterImage.Filled(sw, sh, Background.R, Background.G, Background.B);
        var canvas = new RgbaCanvas(sw, sh, sheet.Pixels);
        for (int i = 0; i < head.Count; i++)
        {
            canvas.Text(Gap, Gap + (i * line), head[i].Text, head[i].Dim ? Dim : Ink, ts);
        }

        int x0 = Gap, x1 = (2 * Gap) + w, y0 = headH + Gap, y1 = headH + (2 * Gap) + h;

        // Blend and edge panels.
        var blend = new RasterImage(w, h, new byte[w * h * 4]);
        for (int i = 0; i < blend.Pixels.Length; i++)
        {
            blend.Pixels[i] = (byte)((reference.Pixels[i] + render.Pixels[i] + 1) / 2);
        }

        RasterImage edges = RasterImage.Filled(w, h, 8, 8, 10);
        for (int y = 0; y < h; y++)
        {
            int ay = Math.Min(ah - 1, y * ah / h);
            for (int x = 0; x < w; x++)
            {
                int ax = Math.Min(aw - 1, x * aw / w);
                bool a = refA.Edges[(ay * aw) + ax], b = renA.Edges[(ay * aw) + ax];
                if (!a && !b)
                {
                    continue;
                }

                Rgba c = a && b ? Rgba.White : a ? RefColour : RenderColour;
                int p = ((y * w) + x) * 4;
                edges.Pixels[p] = c.R;
                edges.Pixels[p + 1] = c.G;
                edges.Pixels[p + 2] = c.B;
            }
        }

        sheet.Paste(reference, x0, y0);
        sheet.Paste(render, x1, y0);
        sheet.Paste(blend, x0, y1);
        sheet.Paste(edges, x1, y1);

        foreach ((int px, int py) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
        {
            FractionGrid(canvas, px, py, w, h, labels: px == x0 && py == y0, scale: ts);
        }

        // Horizons: the reference's (marked) on reference / blend / edges, the render's (from its pitch) on render / blend / edges.
        double? refHorizon = HorizonOf(marks);
        foreach ((int px, int py, bool showRef, bool showRender) in new[] { (x0, y0, true, false), (x1, y0, false, true), (x0, y1, true, true), (x1, y1, true, true) })
        {
            if (showRef && refHorizon is double rh)
            {
                Dashed(canvas, px, py + (float)(rh * h), px + w, py + (float)(rh * h), MarkColour, 2f);
            }

            if (showRender && renderHorizon is double nh && nh is > 0 and < 1)
            {
                Dashed(canvas, px, py + (float)(nh * h), px + w, py + (float)(nh * h), RenderColour, 2f);
            }
        }

        if (marks is not null)
        {
            DrawMarks(canvas, x0, y0, w, h, marks);
        }

        // Skylines (the first structure line from the top) on the edge panel.
        Skyline(canvas, x1, y1, w, h, refA.FirstRows, aw, ah, RefColour);
        Skyline(canvas, x1, y1, w, h, renA.FirstRows, aw, ah, RenderColour);

        PanelLabel(canvas, x0, y0, "REFERENCE", RefColour, ts);
        PanelLabel(canvas, x1, y0, "RENDER", RenderColour, ts);
        PanelLabel(canvas, x0, y1, "BLEND 50/50", Ink, ts);
        PanelLabel(canvas, x1, y1, "EDGES + SKYLINES", Ink, ts);

        int fy = y1 + h + Gap;
        for (int i = 0; i < foot.Count; i++)
        {
            canvas.Text(Gap, fy + (i * line), foot[i].Text, foot[i].Dim ? Dim : Ink, ts);
        }

        return sheet;
    }

    /// <summary>A stored reference enlarged to <paramref name="width"/>, with the fraction grid and its marks.</summary>
    public static RasterImage MarkedReference(RasterImage picture, int width, ReferenceMarks? marks, string title)
    {
        int h = Math.Max(16, (int)Math.Round(width * (double)picture.Height / picture.Width));
        RasterImage big = picture.Resize(width, h);
        int line = BitmapFont.LineHeight(Text2) + 6;
        int sw = width + (2 * Gap) + 34, sh = h + (2 * Gap) + line + 22;
        RasterImage sheet = RasterImage.Filled(sw, sh, Background.R, Background.G, Background.B);
        var canvas = new RgbaCanvas(sw, sh, sheet.Pixels);
        canvas.Text(Gap, Gap, title, Ink, Text2);
        int px = Gap + 34, py = Gap + line + 18;
        sheet.Paste(big, px, py);
        FractionGrid(canvas, px, py, width, h, labels: true, outside: true);
        if (marks is not null)
        {
            DrawMarks(canvas, px, py, width, h, marks);
        }

        return sheet;
    }

    /// <summary>
    /// A source image fitted into <paramref name="maxSide"/> with a labelled grid in the SOURCE image's pixels (for
    /// reading crop rectangles), the given crop outlined (and the snapped crop, when different).
    /// </summary>
    public static RasterImage SourceGrid(RasterImage source, int maxSide, int[]? crop, int[]? snapped, string title)
    {
        // A crop is shown zoomed with a margin; otherwise the whole image.
        int vx = 0, vy = 0, vw = source.Width, vh = source.Height;
        if (crop is not null)
        {
            int mx = Math.Max(8, crop[2] / 6), my = Math.Max(8, crop[3] / 6);
            vx = Math.Max(0, crop[0] - mx);
            vy = Math.Max(0, crop[1] - my);
            vw = Math.Min(source.Width, crop[0] + crop[2] + mx) - vx;
            vh = Math.Min(source.Height, crop[1] + crop[3] + my) - vy;
        }

        double scale = Math.Min((double)maxSide / vw, (double)maxSide / vh);
        if (crop is null)
        {
            scale = Math.Min(scale, 1.0);
        }

        int w = Math.Max(16, (int)Math.Round(vw * scale)), h = Math.Max(16, (int)Math.Round(vh * scale));
        RasterImage view = source.Crop(vx, vy, vw, vh).Resize(w, h);
        int line = BitmapFont.LineHeight(Text2) + 6;
        int left = 46, top = Gap + line + 20;
        int sw = left + w + Gap, sh = top + h + Gap;
        RasterImage sheet = RasterImage.Filled(sw, sh, Background.R, Background.G, Background.B);
        var canvas = new RgbaCanvas(sw, sh, sheet.Pixels);
        canvas.Text(Gap, Gap, title, Ink, Text2);
        sheet.Paste(view, left, top);

        // Grid step in source pixels: about 60-120 screen pixels apart.
        int[] steps = { 5, 10, 20, 25, 50, 100, 200, 250, 500 };
        int step = steps.FirstOrDefault(s => s * scale >= 60, 500);
        var minor = new Rgba(255, 255, 255, 60);
        var major = new Rgba(255, 255, 255, 120);
        for (int sx = ((vx / step) + 1) * step; sx < vx + vw; sx += step)
        {
            float x = left + (float)((sx - vx) * scale);
            canvas.Line(new Vector2(x, top), new Vector2(x, top + h), major);
            string label = sx.ToString(CultureInfo.InvariantCulture);
            canvas.Text((int)x - (BitmapFont.Measure(label, 1) / 2), top - 10, label, Ink, 1);
            float half = x + (float)(step * scale / 2);
            if (half < left + w)
            {
                canvas.Line(new Vector2(half, top), new Vector2(half, top + h), minor);
            }
        }

        for (int sy = ((vy / step) + 1) * step; sy < vy + vh; sy += step)
        {
            float y = top + (float)((sy - vy) * scale);
            canvas.Line(new Vector2(left, y), new Vector2(left + w, y), major);
            string label = sy.ToString(CultureInfo.InvariantCulture);
            canvas.Text(left - 4 - BitmapFont.Measure(label, 1), (int)y - 3, label, Ink, 1);
            float half = y + (float)(step * scale / 2);
            if (half < top + h)
            {
                canvas.Line(new Vector2(left, half), new Vector2(left + w, half), minor);
            }
        }

        void Box(int[] r, Rgba c)
        {
            float bx = left + (float)((r[0] - vx) * scale), by = top + (float)((r[1] - vy) * scale);
            float bw = (float)(r[2] * scale), bh = (float)(r[3] * scale);
            canvas.Line(new Vector2(bx, by), new Vector2(bx + bw, by), c, 2f);
            canvas.Line(new Vector2(bx + bw, by), new Vector2(bx + bw, by + bh), c, 2f);
            canvas.Line(new Vector2(bx + bw, by + bh), new Vector2(bx, by + bh), c, 2f);
            canvas.Line(new Vector2(bx, by + bh), new Vector2(bx, by), c, 2f);
        }

        if (crop is not null)
        {
            Box(crop, MarkColour);
        }

        if (snapped is not null && crop is not null && !snapped.SequenceEqual(crop))
        {
            Box(snapped, MeasureColour);
        }

        return sheet;
    }

    /// <summary>The reference's horizon row (fraction from the top), from its marks.</summary>
    public static double? HorizonOf(ReferenceMarks? marks) => marks?.Horizon switch
    {
        { Length: 1 } h => h[0],
        { Length: 4 } h => (h[1] + h[3]) / 2,
        _ => marks?.Vp is { Length: 2 } vp ? vp[1] : null,
    };

    private static void DrawMarks(RgbaCanvas canvas, int px, int py, int w, int h, ReferenceMarks marks)
    {
        Vector2 P(double fx, double fy) => new(px + (float)(fx * w), py + (float)(fy * h));
        if (marks.Horizon is { Length: 4 } hl)
        {
            Dashed(canvas, P(hl[0], hl[1]).X, P(hl[0], hl[1]).Y, P(hl[2], hl[3]).X, P(hl[2], hl[3]).Y, MarkColour, 2f);
        }
        else if (marks.Horizon is { Length: 1 } hy)
        {
            Dashed(canvas, px, P(0, hy[0]).Y, px + w, P(0, hy[0]).Y, MarkColour, 2f);
        }

        if (marks.Vp is { Length: 2 } vp)
        {
            canvas.StrokeCircle(P(vp[0], vp[1]), 7f, MarkColour, 2f);
            canvas.HaloText((int)P(vp[0], vp[1]).X + 10, (int)P(vp[0], vp[1]).Y - 7, "VP", MarkColour, Text2);
        }

        if (marks.Object is { Length: 4 } o)
        {
            canvas.Line(P(o[0], o[1]), P(o[0], o[2]), MeasureColour, 3f);
            canvas.HaloText((int)P(o[0], o[2]).X + 6, (int)P(o[0], o[2]).Y, $"{o[3].ToString("0.##", CultureInfo.InvariantCulture)} m", MeasureColour, Text2);
        }

        if (marks.Measure is { } m)
        {
            for (int i = 0; i + 2 < m.Length; i += 3)
            {
                canvas.Line(P(m[i], m[i + 1]), P(m[i], m[i + 2]), MeasureColour, 2f);
                canvas.HaloText((int)P(m[i], m[i + 2]).X + 6, (int)P(m[i], m[i + 2]).Y, $"M{(i / 3) + 1}", MeasureColour, Text2);
            }
        }

        if (marks.Ignore is { } ig)
        {
            for (int i = 0; i + 3 < ig.Length; i += 4)
            {
                Vector2 a = P(ig[i], ig[i + 1]), b = P(ig[i] + ig[i + 2], ig[i + 1] + ig[i + 3]);
                var c = new Rgba(200, 200, 200, 200);
                Dashed(canvas, a.X, a.Y, b.X, a.Y, c, 1f);
                Dashed(canvas, b.X, a.Y, b.X, b.Y, c, 1f);
                Dashed(canvas, b.X, b.Y, a.X, b.Y, c, 1f);
                Dashed(canvas, a.X, b.Y, a.X, a.Y, c, 1f);
                canvas.HaloText((int)a.X + 3, (int)a.Y + 3, "ignored", c, 1);
            }
        }
    }

    /// <summary>Word-wraps <paramref name="lines"/> to <paramref name="maxChars"/>; each piece keeps its line's dim flag.</summary>
    private static List<(string Text, bool Dim)> Wrap(IReadOnlyList<string> lines, int maxChars, Func<int, bool> dim)
    {
        var result = new List<(string, bool)>();
        for (int i = 0; i < lines.Count; i++)
        {
            string rest = lines[i];
            while (rest.Length > maxChars)
            {
                int cut = rest.LastIndexOf(' ', maxChars);
                cut = cut <= 0 ? maxChars : cut;
                result.Add((rest[..cut].TrimEnd(), dim(i)));
                rest = "  " + rest[cut..].TrimStart();
            }

            result.Add((rest, dim(i)));
        }

        return result;
    }

    private static void FractionGrid(RgbaCanvas canvas, int px, int py, int w, int h, bool labels, bool outside = false, int scale = Text2)
    {
        var line = new Rgba(255, 255, 255, 45);
        var mid = new Rgba(255, 255, 255, 90);
        for (int i = 1; i < 10; i++)
        {
            float x = px + (w * i / 10f), y = py + (h * i / 10f);
            canvas.Line(new Vector2(x, py), new Vector2(x, py + h), i == 5 ? mid : line);
            canvas.Line(new Vector2(px, y), new Vector2(px + w, y), i == 5 ? mid : line);
            if (!labels || i % 2 != 0 && !outside)
            {
                continue;
            }

            string t = "." + i.ToString(CultureInfo.InvariantCulture);
            if (outside)
            {
                canvas.Text((int)x - (BitmapFont.Measure(t, scale) / 2), py - 18, t, Dim, scale);
                canvas.Text(px - 6 - BitmapFont.Measure(t, scale), (int)y - 7, t, Dim, scale);
            }
            else
            {
                canvas.HaloText((int)x + 3, py + h - (9 * scale), t, Ink, scale);
                canvas.HaloText(px + 3, (int)y + 3, t, Ink, scale);
            }
        }
    }

    private static void PanelLabel(RgbaCanvas canvas, int px, int py, string text, Rgba colour, int scale)
    {
        int tw = BitmapFont.Measure(text, scale);
        canvas.FillRect(px, py, tw + (5 * scale), BitmapFont.LineHeight(scale) + (4 * scale), new Rgba(0, 0, 0, 170));
        canvas.Text(px + (2 * scale) + 1, py + (2 * scale), text, colour, scale);
    }

    private static void Skyline(RgbaCanvas canvas, int px, int py, int w, int h, int[] rows, int aw, int ah, Rgba colour)
    {
        Vector2? prev = null;
        for (int ax = 0; ax < aw; ax++)
        {
            var p = new Vector2(px + ((ax + 0.5f) * w / aw), py + MathF.Min(h - 1, rows[ax] * (float)h / ah));
            if (prev is Vector2 q)
            {
                canvas.Line(q, p, colour, 2f);
            }

            prev = p;
        }
    }

    private static void Dashed(RgbaCanvas canvas, float ax, float ay, float bx, float by, Rgba c, float thickness)
    {
        var a = new Vector2(ax, ay);
        var b = new Vector2(bx, by);
        float len = Vector2.Distance(a, b);
        if (len < 1f)
        {
            return;
        }

        Vector2 dir = (b - a) / len;
        for (float t = 0; t < len; t += 14f)
        {
            canvas.Line(a + (dir * t), a + (dir * MathF.Min(len, t + 8f)), c, thickness);
        }
    }
}
