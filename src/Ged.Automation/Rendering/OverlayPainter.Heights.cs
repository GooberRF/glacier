using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ged.Core.Analysis;
using Ged.Core.IO.Tex;
using Ged.Core.Spatial;

namespace Ged.Automation.Rendering;

// The heights overlay's symbols (see HeightsLayer for the floor tint): drop edges (a heavy violet line with ticks on the
// low side, "↓ 3.5 m"), uphill chevrons on ramps and slopes ("y0 → y1 m"), an arrow up each flight of stairs, spot
// heights ("+y") on flat floor, and the legend's colour bar and symbol entries. Each symbol is drawn only where the image
// shows its floor; labels never overlap each other, the panels or object labels.
internal sealed partial class OverlayPainter
{
    private const string BarRow = "\u0001colour bar";
    private const string TickRow = "\u0002colour bar ticks";
    private static readonly Rgba Violet = new(HeightColorMap.Drop.R, HeightColorMap.Drop.G, HeightColorMap.Drop.B);

    /// <summary>The floor-heights layer (plan and top views with the heights overlay), or null.</summary>
    public HeightsLayer? Heights { get; init; }

    /// <summary>What the heights overlay drew (for the JSON result), or null.</summary>
    public RenderHeightsDto? HeightsResult { get; private set; }

    private int BarWidth => 110 * _ts;

    /// <summary>The legend entries of the heights overlay.</summary>
    private static List<RenderLegendDto> HeightsLegend(double min, double max, double step)
    {
        (byte r, byte g, byte b) = HeightColorMap.Color(0.5);
        return new List<RenderLegendDto>
        {
            new() { Glyph = "colorbar", Color = $"#{r:X2}{g:X2}{b:X2}", Meaning = $"floor height, m: {F1(min)} to {F1(max)}" },
            new() { Glyph = "chevron", Color = "#FFFFFF", Meaning = $"ramp: chevrons uphill, contours {F1(step)} m" },
            new() { Glyph = "stairs", Color = "#FFFFFF", Meaning = "stairs: arrow uphill" },
            new() { Glyph = "drop", Color = Hex(Violet), Meaning = "drop: ticks on the low side" },
            new() { Glyph = "spot", Color = "#FFFFFF", Meaning = "flat floor height, m" },
        };
    }

    /// <summary>The colour bar (striped, so a decoder never reads it as floor) or its tick labels, at (x, y).</summary>
    private void DrawColorBar(int x, int y, bool bar)
    {
        if (Heights is not HeightsLayer hl)
        {
            return;
        }

        int bw = BarWidth, bh = BitmapFont.LineHeight(_ts);
        if (bar)
        {
            for (int k = 0; k < bw; k++)
            {
                if (k % 3 == 2)
                {
                    continue; // a dark gap every third column
                }

                (byte r, byte g, byte b) = HeightColorMap.Color(k / (double)(bw - 1));
                _c.FillRect(x + k, y, 1, bh, new Rgba(r, g, b));
            }

            _c.StrokeRect(x - 1, y - 1, bw + 2, bh + 2, new Rgba(150, 150, 160));
            return;
        }

        // Ticks: the ends and round values between them, labelled where they fit.
        double span = hl.Max - hl.Min;
        double[] nice = { 0.5, 1, 2, 2.5, 5, 10, 20, 25, 50, 100 };
        double step = nice.FirstOrDefault(s => span / s <= 4, 200);
        var ticks = new List<double> { hl.Min };
        for (double v = Math.Ceiling((hl.Min + (step * 0.75)) / step) * step; v < hl.Max - (step * 0.75); v += step)
        {
            ticks.Add(v);
        }

        ticks.Add(hl.Max);
        int lastRight = int.MinValue;
        foreach (double v in ticks)
        {
            int px = x + (int)Math.Round((v - hl.Min) / Math.Max(1e-9, span) * (bw - 1));
            string label = F1(v);
            int tw = BitmapFont.Measure(label, _ts);
            int lx = Math.Clamp(px - (tw / 2), x, x + bw - tw);
            _c.FillRect(px, y - (2 * _ts), Math.Max(1, _ts / 2), 2 * _ts, new Rgba(220, 220, 220));
            if (lx > lastRight + (2 * _ts))
            {
                _c.Text(lx, y, label, TextColor, _ts);
                lastRight = lx + tw;
            }
        }
    }

    /// <summary>The legend swatch of a heights symbol.</summary>
    private void DrawHeightsSwatch(string glyph, int x, int y, int w, int h)
    {
        var c = new Vector2(x + (w / 2f), y + (h / 2f));
        switch (glyph)
        {
            case "chevron":
                _c.FillRect(x, y, w, h, new Rgba(90, 200, 90));
                Chevron(c, new Vector2(0, -1), h * 0.55f);
                break;
            case "stairs":
                _c.FillRect(x, y, w, h, new Rgba(240, 200, 60));
                for (int k = 1; k < 4; k++)
                {
                    _c.FillRect(x, y + (k * h / 4), w, 1, new Rgba(255, 255, 255));
                }

                Arrow(new Vector2(c.X, y + h), new Vector2(c.X, y), MathF.Max(2f, h * 0.35f));
                break;
            case "drop":
                DropLine(new Vector2(x, y + 2), new Vector2(x + w, y + 2), new Vector2(0, 1), h - 3);
                break;
            default:
                _c.Text(x, y + ((h - BitmapFont.LineHeight(Math.Max(1, _ts - 1))) / 2), "+2", TextColor, Math.Max(1, _ts - 1));
                break;
        }
    }

    // ---- the symbols -------------------------------------------------------------------------------------------

    private float SymbolWidth => MathF.Max(1f, _u * 0.9f);

    /// <summary>A white chevron pointing along <paramref name="dir"/> (screen), dark-outlined.</summary>
    private void Chevron(Vector2 c, Vector2 dir, float size)
    {
        var n = new Vector2(-dir.Y, dir.X);
        Vector2 tip = c + (dir * size * 0.45f);
        Vector2 l = c - (dir * size * 0.45f) + (n * size * 0.7f), r = c - (dir * size * 0.45f) - (n * size * 0.7f);
        float t = SymbolWidth;
        _c.Line(l, tip, Rgba.Black, t + 2.5f);
        _c.Line(r, tip, Rgba.Black, t + 2.5f);
        _c.Line(l, tip, Rgba.White, t + 0.5f);
        _c.Line(r, tip, Rgba.White, t + 0.5f);
    }

    /// <summary>A white arrow from <paramref name="a"/> to <paramref name="b"/>, dark-outlined.</summary>
    private void Arrow(Vector2 a, Vector2 b, float head)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 2f)
        {
            return;
        }

        d /= len;
        var n = new Vector2(-d.Y, d.X);
        float t = SymbolWidth;
        Vector2 h1 = b - (d * head) + (n * head * 0.6f), h2 = b - (d * head) - (n * head * 0.6f);
        foreach ((Rgba col, float w) in new[] { (Rgba.Black, t + 2.5f), (Rgba.White, t + 0.5f) })
        {
            _c.Line(a, b, col, w);
            _c.Line(b, h1, col, w);
            _c.Line(b, h2, col, w);
        }
    }

    /// <summary>A drop edge from a to b: a heavy violet line with ticks toward <paramref name="low"/> (screen unit vector).</summary>
    private void DropLine(Vector2 a, Vector2 b, Vector2 low, float tick)
    {
        float t = MathF.Max(3f, (_u * 2.2f) + 1f);
        Vector2 d = b - a;
        float len = d.Length();
        _c.Line(a, b, Rgba.Black, t + 3.5f);
        var ticks = new List<Vector2>();
        float spacing = MathF.Max(7f, 6f * _u);
        if (len > 1f)
        {
            Vector2 u = d / len;
            for (float s = spacing * 0.5f; s < len; s += spacing)
            {
                ticks.Add(a + (u * s));
            }
        }

        float tw = MathF.Max(1.5f, _u * 0.9f);
        foreach (Vector2 p in ticks)
        {
            _c.Line(p, p + (low * tick), Rgba.Black, tw + 2.5f);
        }

        _c.Line(a, b, Violet, t);
        foreach (Vector2 p in ticks)
        {
            _c.Line(p, p + (low * tick), Violet, tw);
        }
    }

    /// <summary>Keeps labels off a drawn line: small squares along it (half-width <paramref name="half"/>).</summary>
    private void Occupy(Vector2 a, Vector2 b, float half)
    {
        float len = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)MathF.Ceiling(len / half));
        for (int k = 0; k <= steps; k++)
        {
            Vector2 p = Vector2.Lerp(a, b, k / (float)steps);
            _occupied.Add(((int)(p.X - half), (int)(p.Y - half), (int)(2 * half) + 1, (int)(2 * half) + 1));
        }
    }

    /// <summary>Places a label (white, dark halo) at the first free candidate; returns where, or null.</summary>
    private Vector2? PlaceHeightLabel(string text, Vector2 at, IEnumerable<Vector2> offsets)
    {
        int tw = BitmapFont.Measure(text, _ts), th = BitmapFont.LineHeight(_ts);
        foreach (Vector2 o in offsets)
        {
            int x = (int)MathF.Round(at.X + o.X - (tw / 2f)), y = (int)MathF.Round(at.Y + o.Y - (th / 2f));
            var rect = (x - (2 * _ts), y - (2 * _ts), tw + (4 * _ts), th + (4 * _ts));
            if (x < 1 || y < 1 || x + tw >= W - 1 || y + th >= H - 1 || _occupied.Any(r => Overlaps(r, rect)))
            {
                continue;
            }

            _c.HaloText(x, y, text, TextColor, _ts, new Rgba(0, 0, 0, 235));
            _occupied.Add(rect);
            return new Vector2(x + (tw / 2f), y + (th / 2f));
        }

        return null;
    }

    private static string F1(double v) => Math.Abs(v) < 0.05 ? "0" : (Math.Round(v * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Rise(double a, double b) => $"{F1(a)} → {F1(b)} m";

    /// <summary>True when the image shows floor at about height <paramref name="y"/> near pixel <paramref name="p"/>.</summary>
    private static bool FloorNear(HeightsLayer hl, Vector2 p, double y, double tol, int r = 2)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                float v = hl.At((int)p.X + dx, (int)p.Y + dy);
                if (!float.IsNaN(v) && Math.Abs(v - y) <= tol)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when a pixel near <paramref name="p"/> shows the feature.</summary>
    private static bool FeatureNear(HeightsLayer hl, Vector2 p, int kind, int index, int r = 1)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int x = (int)p.X + dx, y = (int)p.Y + dy;
                if ((uint)x < (uint)hl.W && (uint)y < (uint)hl.H && hl.Kind[(y * hl.W) + x] == kind && hl.Feature[(y * hl.W) + x] == index)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Vector2? Px(double x, double y, double z) => Project(new Vector3((float)x, (float)y, (float)z));

    /// <summary>Draws the heights symbols and fills <see cref="HeightsResult"/>.</summary>
    private void DrawHeights(HeightsLayer hl)
    {
        HeightsReading r = hl.Reading;
        float wpp = Wpp;
        var result = new RenderHeightsDto
        {
            Range = new[] { hl.Min, hl.Max },
            Colormap = HeightsLayer.ColormapText,
            ContourStep = hl.ContourStep,
            Lowest = HeightsLayer.Point(r.Lowest),
            Highest = HeightsLayer.Point(r.Highest),
        };

        // 1. Drop edges: each segment where the image shows its high floor.
        var dropLabels = new List<(HeightsDrop D, Vector2 At, Vector2 Low, float Len)>();
        foreach (HeightsDrop d in r.Drops)
        {
            float shownLen = 0;
            (Vector2 At, Vector2 Low, float Len) best = (default, default, 0);
            for (int k = 0; k + 1 < d.Points.Count; k++)
            {
                (double ax, double az) = d.Points[k];
                (double bx, double bz) = d.Points[k + 1];
                (double nx, double nz) = d.LowSide[k];
                if (Px(ax, d.High, az) is not Vector2 pa || Px(bx, d.High, bz) is not Vector2 pb
                    || Px(ax + nx, d.High, az + nz) is not Vector2 pn)
                {
                    continue;
                }

                Vector2 low = pn - pa;
                if (low.LengthSquared() < 1e-6f)
                {
                    continue;
                }

                low = Vector2.Normalize(low);

                // The high side must show this floor: probe a few pixels in, at the quarter points.
                float probe = MathF.Max(3f, (0.3f / wpp) + 1f);
                int seen = 0;
                foreach (float f in new[] { 0.25f, 0.5f, 0.75f })
                {
                    Vector2 q = Vector2.Lerp(pa, pb, f) - (low * probe);
                    seen += FloorNear(hl, q, d.High, 0.35) ? 1 : 0;
                }

                if (seen < 2)
                {
                    continue;
                }

                DropLine(pa, pb, low, MathF.Max(6f, 5f * _u));
                Occupy(pa, pb, (MathF.Max(3f, (_u * 2.2f) + 1f) + 3.5f) / 2f); // the line itself; labels may sit over its ticks
                float len = Vector2.Distance(pa, pb);
                shownLen += len;
                if (len > best.Len)
                {
                    best = ((pa + pb) * 0.5f, low, len);
                }
            }

            if (shownLen > 0)
            {
                dropLabels.Add((d, best.At, best.Low, shownLen));
            }
        }

        // 2. Ramps and slopes: chevrons uphill at their marks.
        var flightLabels = new List<(HeightsFlight F, Vector2 At, Vector2 Across)>();
        float chevron = Math.Clamp(4.5f * _u, 7f, 16f);
        foreach (HeightsFlight f in r.Ramps)
        {
            if (!hl.Shows(HeightsReading.KindRamp, f.Index, 20))
            {
                continue;
            }

            var drawn = new List<Vector2>();
            foreach (HeightsMark m in f.Marks)
            {
                if (Px(m.X, m.Y, m.Z) is not Vector2 c || Px(m.X + m.DirX, m.Y, m.Z + m.DirZ) is not Vector2 c2
                    || !FeatureNear(hl, c, HeightsReading.KindRamp, f.Index) || drawn.Any(o => Vector2.Distance(o, c) < chevron * 2.2f))
                {
                    continue;
                }

                Vector2 dir = c2 - c;
                if (dir.LengthSquared() < 1e-6f)
                {
                    continue;
                }

                Chevron(c, Vector2.Normalize(dir), chevron);
                drawn.Add(c);
                float rr = chevron;
                _occupied.Add(((int)(c.X - rr), (int)(c.Y - rr), (int)(2 * rr) + 1, (int)(2 * rr) + 1));
            }

            if (Px(f.X, (f.Low + f.High) / 2, f.Z) is Vector2 at && Px(f.X - f.DirZ, (f.Low + f.High) / 2, f.Z + f.DirX) is Vector2 at2)
            {
                Vector2 across = at2 - at;
                Vector2 anchor = hl.Anchor.TryGetValue((HeightsReading.KindRamp, f.Index), out Vector2 shown) ? shown : at;
                flightLabels.Add((f, anchor, across.LengthSquared() > 1e-6f ? Vector2.Normalize(across) : new Vector2(1, 0)));
            }
        }

        // 3. Stairs: an arrow up the flight's centre line.
        foreach (HeightsFlight f in r.Stairs)
        {
            if (!hl.Shows(HeightsReading.KindStairs, f.Index, 20) || Px(f.Foot.X, f.Foot.Y, f.Foot.Z) is not Vector2 a || Px(f.Top.X, f.Top.Y, f.Top.Z) is not Vector2 b)
            {
                continue;
            }

            Vector2 mid = (a + b) * 0.5f;
            if (!FeatureNear(hl, mid, HeightsReading.KindStairs, f.Index, 3))
            {
                continue;
            }

            Vector2 from = Vector2.Lerp(a, b, 0.12f), to = Vector2.Lerp(a, b, 0.88f);
            Arrow(from, to, Math.Clamp(3.5f * _u, 6f, 14f));
            _occupied.Add(((int)MathF.Min(from.X, to.X) - 2, (int)MathF.Min(from.Y, to.Y) - 2, (int)MathF.Abs(to.X - from.X) + 4, (int)MathF.Abs(to.Y - from.Y) + 4));
            if (Px(f.X, (f.Low + f.High) / 2, f.Z) is Vector2 at && Px(f.X - f.DirZ, (f.Low + f.High) / 2, f.Z + f.DirX) is Vector2 at2)
            {
                Vector2 across = at2 - at;
                flightLabels.Add((f, mid, across.LengthSquared() > 1e-6f ? Vector2.Normalize(across) : new Vector2(1, 0)));
            }
        }

        // 4. Labels, most important first: ramps and stairs, then drops, then spot heights (largest floors first).
        int th = BitmapFont.LineHeight(_ts);
        foreach ((HeightsFlight f, Vector2 at, Vector2 across) in flightLabels.OrderByDescending(x => x.F.High - x.F.Low))
        {
            // Beside the chevrons or arrow on the centre line, either side, then further out, then on it.
            string text = Rise(f.Low, f.High);
            float half = (MathF.Abs(across.X) * BitmapFont.Measure(text, _ts) / 2f) + (MathF.Abs(across.Y) * th / 2f);
            float o = half + chevron + (3 * _u);
            Vector2 along = new(-across.Y, across.X);
            Vector2? placed = PlaceHeightLabel(text, at,
                new[] { across * o, -across * o, across * (o + th), -across * (o + th), Vector2.Zero, (across * o) + (along * th * 1.6f), (-across * o) - (along * th * 1.6f) });
            AddFlight(result, f, placed);
        }

        foreach ((HeightsDrop d, Vector2 at, Vector2 low, float len) in dropLabels.OrderByDescending(x => x.Len))
        {
            string text = double.IsNaN(d.Low) ? "↓ void" : $"↓ {F1(d.Drop)} m";
            float half = (MathF.Abs(low.X) * BitmapFont.Measure(text, _ts) / 2f) + (MathF.Abs(low.Y) * th / 2f);
            float o = half + MathF.Max(6f, 5f * _u) + (2 * _u);
            Vector2 along = new(-low.Y, low.X);
            Vector2? placed = len >= 3f * th
                ? PlaceHeightLabel(text, at, new[] { low * o, (low * o) + (along * th * 2), (low * o) - (along * th * 2), -low * (half + (3 * _u) + 4), low * (o + th) })
                : null;
            result.Drops.Add(new RenderHeightsDropDto
            {
                Points = d.Points.Select(p => new[] { Dto.R(p.X), Dto.R(p.Z) }).ToList(),
                High = Dto.R(d.High),
                Low = double.IsNaN(d.Low) ? null : Dto.R(d.Low),
                Drop = double.IsNaN(d.Low) ? null : Dto.R(d.Drop),
                Length = Dto.R(d.Length),
                Label = placed is null ? null : text,
            });
        }

        foreach (HeightsFlat f in r.Flats.OrderByDescending(f => f.Area))
        {
            if (!hl.Shows(HeightsReading.KindFlat, f.Index, 12) || Px(f.X, f.Y, f.Z) is not Vector2 at0)
            {
                continue;
            }

            // On its own floor: its label point, the middle of what the image shows of it, then spots spread over it —
            // the first where the whole label lies on this floor and is free.
            (int, int) key = (HeightsReading.KindFlat, f.Index);
            string text = (f.Y >= -0.05 ? "+" : string.Empty) + F1(f.Y);
            int tw = BitmapFont.Measure(text, _ts);
            bool room = f.Area >= 4 && hl.Shown.GetValueOrDefault(key) >= tw * th * 2;
            Vector2 anchor = hl.Anchor[key];
            var tries = new List<Vector2> { at0, anchor };
            tries.AddRange(hl.Spots.GetValueOrDefault(key, new List<Vector2>()).OrderBy(q => Vector2.DistanceSquared(q, anchor)));
            Vector2? placed = null;
            foreach (Vector2 c in room ? tries : new List<Vector2>())
            {
                float hx = (tw / 2f) + 1, hy = (th / 2f) + 1;
                if (FeatureNear(hl, c + new Vector2(-hx, -hy), HeightsReading.KindFlat, f.Index, 0) && FeatureNear(hl, c + new Vector2(hx, -hy), HeightsReading.KindFlat, f.Index, 0)
                    && FeatureNear(hl, c + new Vector2(-hx, hy), HeightsReading.KindFlat, f.Index, 0) && FeatureNear(hl, c + new Vector2(hx, hy), HeightsReading.KindFlat, f.Index, 0)
                    && FeatureNear(hl, c, HeightsReading.KindFlat, f.Index, 0)
                    && (placed = PlaceHeightLabel(text, c, new[] { Vector2.Zero })) is not null)
                {
                    break;
                }
            }
            if (result.Flats.Count < RenderHeightsDto.ListLimit)
            {
                result.Flats.Add(new RenderHeightsFlatDto { Y = Dto.R(f.Y), Area = Dto.R(f.Area), At = new[] { Dto.R(f.X), Dto.R(f.Z) }, Label = placed is null ? null : text });
            }
        }

        result.Drawn = new RenderHeightsCountsDto
        {
            Ramps = result.Ramps.Count,
            Stairs = result.Stairs.Count,
            Drops = result.Drops.Count,
            Spots = result.Flats.Count(f => f.Label is not null),
        };
        HeightsResult = result;
    }

    private static void AddFlight(RenderHeightsDto result, HeightsFlight f, Vector2? label)
    {
        var dto = new RenderHeightsFlightDto
        {
            Low = Dto.R(f.Low),
            High = Dto.R(f.High),
            Rise = Dto.R(f.High - f.Low),
            Foot = new[] { Dto.R(f.Foot.X), Dto.R(f.Foot.Y), Dto.R(f.Foot.Z) },
            Top = new[] { Dto.R(f.Top.X), Dto.R(f.Top.Y), Dto.R(f.Top.Z) },
            Climb = new[] { Dto.R(f.DirX), Dto.R(f.DirZ) },
            Run = Dto.R(f.Run),
            Width = Dto.R(f.Width),
            Area = Dto.R(f.Area),
            At = new[] { Dto.R(f.X), Dto.R(f.Z) },
            Label = label is null ? null : Rise(f.Low, f.High),
        };
        (f.Kind == "stairs" ? result.Stairs : result.Ramps).Add(dto);
    }
}
