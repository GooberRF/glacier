using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Analysis.Recreation;

/// <summary>What <see cref="HeightsDecoder"/> reads from a heights image.</summary>
internal sealed record HeightsDecoderOptions
{
    /// <summary>The colour legend's range (m): the heights the map's two ends stand for.</summary>
    public required double Min { get; init; }

    public required double Max { get; init; }

    /// <summary>Picture pixels per metre.</summary>
    public required double Ppm { get; init; }

    /// <summary>Only this window {x, y, w, h} (null: the whole picture).</summary>
    public int[]? Crop { get; init; }

    /// <summary>Rectangles {x, y, w, h} to leave out (a legend panel, a caption).</summary>
    public IReadOnlyList<int[]> Ignore { get; init; } = Array.Empty<int[]>();

    /// <summary>The height grid's cell (px); 0 = about a quarter metre.</summary>
    public int Cell { get; init; }

    /// <summary>The smallest flat region, ramp or flight reported (m²).</summary>
    public double MinArea { get; init; } = 2;

    /// <summary>Outline simplification (px).</summary>
    public double SimplifyPx { get; init; } = 2;
}

/// <summary>One decoded region: a flat floor, a ramp or a flight of stairs.</summary>
internal sealed record DecodedRegion(string Name, string Kind, IReadOnlyList<(double X, double Y)> Poly, double Low, double High,
    (double X, double Y)? Climb, double AreaM2, double Slope);

/// <summary>One decoded drop edge: its polyline (picture pixels) and the floors above and below it.</summary>
internal sealed record DecodedDrop(IReadOnlyList<(double X, double Y)> Points, double High, double Low);

/// <summary>Everything decoded from a heights image.</summary>
internal sealed record HeightsDecoded(int Cell, int Cols, int Rows, int X0, int Y0, double[] Grid, double Quantum,
    IReadOnlyList<DecodedRegion> Regions, IReadOnlyList<DecodedDrop> Drops, int FloorPixels);

/// <summary>
/// Decodes a heights image (<c>glacier-cli render --mode heights</c>, or a plan with the heights overlay; see
/// <see cref="HeightColorMap"/>) back into heights: every floor-coloured pixel's height through the legend's range, a
/// height grid, flat floor regions, ramps (a plane fitted to them: foot, top, uphill direction), stairs (a climb made of
/// level treads and risers) and the violet drop edges with the floors either side. Regions come out as outlines that
/// share their seams (one label image traced per region), so they go straight into <c>kit.trace{units = "px"}</c>.
/// </summary>
internal static class HeightsDecoder
{
    /// <summary>A flat region's neighbouring cells differ by at most this (m), or two quanta of the map.</summary>
    private const double FlatStep = 0.1;

    /// <summary>A slope (rise per run) under this is flat; quantisation noise is added on top.</summary>
    private const double FlatSlope = 0.05;

    public static HeightsDecoded Decode(RefImage img, HeightsDecoderOptions o, System.Threading.CancellationToken ct = default)
    {
        int[] win = o.Crop ?? new[] { 0, 0, img.Width, img.Height };
        int x0 = win[0], y0 = win[1], w = win[2], h = win[3];
        double span = o.Max - o.Min;
        double quantum = Math.Max(span, 1e-6) / 480; // the map's step after overlay blending (about 2 x 240 hue levels)
        double ppm = o.Ppm;

        // 1. Per pixel: height (NaN: not floor colour) and the drop violet.
        var y = new double[w * h];
        var drop = new bool[w * h];
        var ignore = new bool[w * h];
        foreach (int[] r in o.Ignore)
        {
            for (int yy = Math.Max(0, r[1] - y0); yy < Math.Min(h, r[1] + r[3] - y0); yy++)
            {
                for (int xx = Math.Max(0, r[0] - x0); xx < Math.Min(w, r[0] + r[2] - x0); xx++)
                {
                    ignore[(yy * w) + xx] = true;
                }
            }
        }

        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                int p = (py * w) + px;
                (byte r, byte g, byte b) = img.At(x0 + px, y0 + py);
                y[p] = double.NaN;
                if (ignore[p])
                {
                    continue;
                }

                if (HeightColorMap.IsDrop(r, g, b))
                {
                    drop[p] = true;
                }
                else if (HeightColorMap.TOf(r, g, b) is double t)
                {
                    y[p] = HeightColorMap.Y(t, o.Min, o.Max);
                }
            }
        }

        // 2. Keep floor pixels whose 3 x 3 neighbourhood is floor of about the same height: text, glyph rims, the legend's
        //    striped colour bar and thin lines drop out.
        var floor = new double[w * h];
        int floorPixels = 0;
        for (int p = 0; p < floor.Length; p++)
        {
            floor[p] = double.NaN;
            int px = p % w, py = p / w;
            if (double.IsNaN(y[p]) || px == 0 || py == 0 || px == w - 1 || py == h - 1)
            {
                continue;
            }

            bool ok = true;
            for (int dy = -1; dy <= 1 && ok; dy++)
            {
                for (int dx = -1; dx <= 1 && ok; dx++)
                {
                    double v = y[p + (dy * w) + dx];
                    ok = !double.IsNaN(v) && Math.Abs(v - y[p]) <= 0.6;
                }
            }

            if (ok)
            {
                floor[p] = y[p];
                floorPixels++;
            }
        }

        ct.ThrowIfCancellationRequested();

        // 3. The cell grid: the median height of each cell's floor pixels (half the cell at least).
        int cell = o.Cell > 0 ? o.Cell : Math.Max(2, (int)Math.Round(0.25 * ppm));
        int cols = (w + cell - 1) / cell, rows = (h + cell - 1) / cell;
        var grid = new double[cols * rows];
        var dropCell = new bool[cols * rows];
        var buf = new List<double>(cell * cell);
        for (int cy = 0; cy < rows; cy++)
        {
            for (int cx = 0; cx < cols; cx++)
            {
                buf.Clear();
                int total = 0;
                for (int py = cy * cell; py < Math.Min(h, (cy + 1) * cell); py++)
                {
                    for (int px = cx * cell; px < Math.Min(w, (cx + 1) * cell); px++)
                    {
                        total++;
                        int p = (py * w) + px;
                        if (!double.IsNaN(floor[p]))
                        {
                            buf.Add(floor[p]);
                        }

                        dropCell[(cy * cols) + cx] |= drop[p];
                    }
                }

                grid[(cy * cols) + cx] = buf.Count * 2 >= total ? Median(buf) : double.NaN;
            }
        }

        // 4. Local slope per cell (m per m): a plane through the cells within about 0.75 m of about the same height.
        double cellM = cell / ppm;
        int rad = Math.Max(1, (int)Math.Round(0.75 / cellM));
        var slope = new double[cols * rows];
        for (int c = 0; c < grid.Length; c++)
        {
            slope[c] = double.NaN;
            if (double.IsNaN(grid[c]))
            {
                continue;
            }

            int cx = c % cols, cy = c / cols;
            double sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, n = 0, mx = 0, my = 0, mz = 0;
            var pts = new List<(double X, double Y, double Z)>();
            for (int dy = -rad; dy <= rad; dy++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if ((uint)nx >= (uint)cols || (uint)ny >= (uint)rows)
                    {
                        continue;
                    }

                    double v = grid[(ny * cols) + nx];
                    if (!double.IsNaN(v) && Math.Abs(v - grid[c]) <= 0.6)
                    {
                        pts.Add((dx * cellM, dy * cellM, v));
                    }
                }
            }

            if (pts.Count < 5)
            {
                continue;
            }

            foreach ((double px, double py, double pz) in pts)
            {
                mx += px;
                my += py;
                mz += pz;
                n++;
            }

            mx /= n;
            my /= n;
            mz /= n;
            foreach ((double px, double py, double pz) in pts)
            {
                double a = px - mx, b = py - my, z = pz - mz;
                sxx += a * a;
                syy += b * b;
                sxy += a * b;
                sxz += a * z;
                syz += b * z;
            }

            double det = (sxx * syy) - (sxy * sxy);
            if (det < 1e-12)
            {
                continue;
            }

            double gx = ((sxz * syy) - (syz * sxy)) / det, gy = ((syz * sxx) - (sxz * sxy)) / det;
            slope[c] = Math.Sqrt((gx * gx) + (gy * gy));
        }

        double flatSlope = FlatSlope + (1.5 * quantum / (2 * rad * cellM));
        double flatStep = Math.Max(FlatStep, 2 * quantum);
        bool IsFlat(int c) => !double.IsNaN(slope[c]) && slope[c] < flatSlope;
        bool IsSloped(int c) => !double.IsNaN(slope[c]) && slope[c] >= flatSlope && slope[c] <= 1.6;

        ct.ThrowIfCancellationRequested();

        // 5. Regions: flat cells within flatStep of their neighbours; sloped cells within a step (0.6 m).
        var label = new int[cols * rows];
        Array.Fill(label, -1);
        var kinds = new List<string>();
        int[] dxs = { 1, -1, 0, 0 }, dys = { 0, 0, 1, -1 };
        List<int> Flood(int seed, Func<int, bool> member, double step, int id)
        {
            var list = new List<int> { seed };
            label[seed] = id;
            var st = new Stack<int>();
            st.Push(seed);
            while (st.Count > 0)
            {
                int c = st.Pop();
                int cx = c % cols, cy = c / cols;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dxs[d], ny = cy + dys[d];
                    if ((uint)nx >= (uint)cols || (uint)ny >= (uint)rows)
                    {
                        continue;
                    }

                    int q = (ny * cols) + nx;
                    if (label[q] < 0 && member(q) && Math.Abs(grid[q] - grid[c]) <= step)
                    {
                        label[q] = id;
                        list.Add(q);
                        st.Push(q);
                    }
                }
            }

            return list;
        }

        double cellArea = cellM * cellM;
        var members = new List<List<int>>();
        for (int c = 0; c < grid.Length; c++)
        {
            if (label[c] >= 0)
            {
                continue;
            }

            if (IsSloped(c))
            {
                List<int> m = Flood(c, IsSloped, 0.6, members.Count);
                members.Add(m);
                kinds.Add("sloped");
            }
        }

        for (int c = 0; c < grid.Length; c++)
        {
            if (label[c] < 0 && IsFlat(c))
            {
                List<int> m = Flood(c, IsFlat, flatStep, members.Count);
                members.Add(m);
                kinds.Add("flat");
            }
        }

        // Small flats give their cells back (they join a neighbour below).
        var regionInfo = new Dictionary<int, (string Kind, double Low, double High, (double X, double Y)? Climb, double Slope)>();
        for (int id = 0; id < members.Count; id++)
        {
            if (kinds[id] != "flat")
            {
                continue;
            }

            List<int> m = members[id];
            if (m.Count * cellArea >= o.MinArea)
            {
                double[] hs = m.Select(c => grid[c]).OrderBy(v => v).ToArray();
                double med = Percentile(hs, 0.5);
                regionInfo[id] = ("flat", med, med, null, 0);
            }
            else
            {
                foreach (int c in m)
                {
                    label[c] = -1;
                }
            }
        }

        // The slope window reads a flat's cells next to a ramp as sloped: cells at a flat's own height go back to it.
        for (bool more = true; more;)
        {
            more = false;
            for (int c = 0; c < grid.Length; c++)
            {
                int own = label[c];
                if (own >= 0 && kinds[own] == "flat")
                {
                    continue;
                }

                if (double.IsNaN(grid[c]))
                {
                    continue;
                }

                int cx = c % cols, cy = c / cols;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dxs[d], ny = cy + dys[d];
                    if ((uint)nx >= (uint)cols || (uint)ny >= (uint)rows)
                    {
                        continue;
                    }

                    int nb = label[(ny * cols) + nx];
                    if (nb >= 0 && regionInfo.TryGetValue(nb, out var fi) && fi.Kind == "flat" && Math.Abs(grid[c] - fi.Low) <= 1.5 * flatStep)
                    {
                        label[c] = nb;
                        more = true;
                        break;
                    }
                }
            }
        }

        // Sloped pieces: what is left of each; too small or climbing too little gives the cells back. Their low and high
        // floors are the flats they run into (within 0.35 m), else their own extremes.
        var sloped = new Dictionary<int, List<int>>();
        for (int c = 0; c < grid.Length; c++)
        {
            if (label[c] >= 0 && kinds[label[c]] == "sloped")
            {
                if (!sloped.TryGetValue(label[c], out List<int>? l))
                {
                    sloped[label[c]] = l = new List<int>();
                }

                l.Add(c);
            }
        }

        foreach ((int id, List<int> m) in sloped.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)))
        {
            double[] hs = m.Select(c => grid[c]).OrderBy(v => v).ToArray();
            double lo = Percentile(hs, 0.03), hi = Percentile(hs, 0.97);
            if (m.Count * cellArea < o.MinArea || hi - lo < 0.5)
            {
                foreach (int c in m)
                {
                    label[c] = -1;
                }

                continue;
            }

            var touching = new HashSet<int>();
            foreach (int c in m)
            {
                int cx = c % cols, cy = c / cols;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dxs[d], ny = cy + dys[d];
                    if ((uint)nx < (uint)cols && (uint)ny < (uint)rows && label[(ny * cols) + nx] is int nb && nb >= 0 && regionInfo.TryGetValue(nb, out var fi) && fi.Kind == "flat")
                    {
                        touching.Add(nb);
                    }
                }
            }

            double Snap(double v) => touching.Select(t => regionInfo[t].Low).Where(fy => Math.Abs(fy - v) <= 0.35).OrderBy(fy => Math.Abs(fy - v)).Select(fy => (double?)fy).FirstOrDefault() ?? v;

            // The plane y = a u + b v + c over the cells (u, v: cell centres in picture pixels).
            (double a, double b) = Gradient(m, c => (((c % cols) + 0.5) * cell, ((c / cols) + 0.5) * cell), c => grid[c]);
            double g = Math.Sqrt((a * a) + (b * b));
            string kind = Stepped(floor, w, h, m, cols, cell, a, b, quantum) ? "stairs" : "ramp";
            regionInfo[id] = (kind, Snap(lo), Snap(hi), g > 1e-12 ? (a / g, b / g) : null, g * ppm);
        }

        // 6. Grow the regions over the cells left (floor between them, the drop lines) a cell per pass, so neighbours
        //    meet: floor joins a region within 0.35 m of it, a drop line (with its dark rim and ticks) either side.
        for (int pass = 0; pass < 2; pass++)
        {
            var grown = (bool[])dropCell.Clone();
            for (int c = 0; c < grid.Length; c++)
            {
                if (dropCell[c] || !double.IsNaN(grid[c]))
                {
                    continue;
                }

                int cx = c % cols, cy = c / cols;
                for (int dy = -1; dy <= 1 && !grown[c]; dy++)
                {
                    for (int dx = -1; dx <= 1 && !grown[c]; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        grown[c] = (uint)nx < (uint)cols && (uint)ny < (uint)rows && dropCell[(ny * cols) + nx];
                    }
                }
            }

            dropCell = grown;
        }

        int growPasses = Math.Max(2, (int)Math.Ceiling(1.0 / cellM));
        for (int pass = 0; pass < growPasses; pass++)
        {
            var next = (int[])label.Clone();
            bool changed = false;
            for (int c = 0; c < grid.Length; c++)
            {
                if (label[c] >= 0 || (double.IsNaN(grid[c]) && !dropCell[c]))
                {
                    continue;
                }

                int cx = c % cols, cy = c / cols, best = -1;
                double bestD = double.MaxValue;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dxs[d], ny = cy + dys[d];
                    if ((uint)nx >= (uint)cols || (uint)ny >= (uint)rows)
                    {
                        continue;
                    }

                    int q = (ny * cols) + nx;
                    if (label[q] < 0)
                    {
                        continue;
                    }

                    double dd = double.IsNaN(grid[c]) || double.IsNaN(grid[q]) ? 0 : Math.Abs(grid[c] - grid[q]);
                    if (!double.IsNaN(grid[c]) && dd > 0.35)
                    {
                        continue;
                    }

                    if (dd < bestD || (dd == bestD && label[q] < best))
                    {
                        bestD = dd;
                        best = label[q];
                    }
                }

                if (best >= 0)
                {
                    next[c] = best;
                    changed = true;
                }
            }

            label = next;
            if (!changed)
            {
                break;
            }
        }

        // 7. Pixel labels; holes a symbol or label left inside one region are filled with it.
        var zone = new int[w * h];
        for (int p = 0; p < zone.Length; p++)
        {
            int c = ((p / w / cell) * cols) + (p % w / cell);
            zone[p] = label[c];
        }

        FillHoles(zone, w, h, (int)Math.Ceiling(4 * ppm * ppm));
        Unpinch(zone, w, h);
        var mask = new bool[w * h];
        for (int p = 0; p < zone.Length; p++)
        {
            mask[p] = zone[p] >= 0;
        }

        ct.ThrowIfCancellationRequested();
        var rm = new RefMask(w, h, mask);
        List<(int Zone, RefPolygon Poly)> pieces = rm.ZonePolygons(zone, o.SimplifyPx, 8, cutHoles: true);
        var regions = new List<DecodedRegion>();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        double px2 = 1 / (ppm * ppm);
        foreach (IGrouping<int, (int Zone, RefPolygon Poly)> gz in pieces.GroupBy(q => q.Zone).OrderBy(gz => gz.Key))
        {
            if (!regionInfo.TryGetValue(gz.Key, out var info))
            {
                continue;
            }

            var polys = gz.Select(q => q.Poly).Where(pp => pp.AreaPx * px2 >= Math.Max(1, o.MinArea / 2) && pp.Outer.Count >= 3).ToList();
            counters[info.Kind] = counters.GetValueOrDefault(info.Kind) + 1;
            int k = 0;
            foreach (RefPolygon pp in polys)
            {
                k++;
                string name = $"{info.Kind}{counters[info.Kind]}" + (polys.Count > 1 ? $"_{k}" : string.Empty);
                List<(double X, double Y)> outer = Clean(pp.Outer.Select(q => (q.X + x0, q.Y + y0)).ToList());
                if (outer.Count < 3)
                {
                    continue;
                }

                regions.Add(new DecodedRegion(name, info.Kind, outer, Round(info.Low), Round(info.High), info.Climb, pp.AreaPx * px2, info.Slope));
            }
        }

        // 8. Drop edges: the violet lines, as polylines, with the floor either side.
        List<DecodedDrop> drops = Drops(drop, floor, w, h, ppm, x0, y0);

        var outGrid = new double[grid.Length];
        for (int c = 0; c < grid.Length; c++)
        {
            outGrid[c] = double.IsNaN(grid[c]) ? double.NaN : Math.Round(grid[c], 3);
        }

        return new HeightsDecoded(cell, cols, rows, x0, y0, outGrid, quantum, regions, drops, floorPixels);
    }

    private static double Round(double v) => Math.Round(v * 20) / 20;

    /// <summary>
    /// An outline kit.trace takes: corners closer than half a pixel merged, and corners where the outline runs straight
    /// on or doubles back (a spike along a seam) dropped, until none is left.
    /// </summary>
    private static List<(double X, double Y)> Clean(List<(double X, double Y)> p)
    {
        for (bool more = true; more && p.Count >= 3;)
        {
            more = false;
            for (int i = 0; i < p.Count && p.Count >= 3; i++)
            {
                (double ax, double ay) = p[(i + p.Count - 1) % p.Count];
                (double bx, double by) = p[i];
                (double cx, double cy) = p[(i + 1) % p.Count];
                double abx = bx - ax, aby = by - ay, bcx = cx - bx, bcy = cy - by;
                double lab = Math.Sqrt((abx * abx) + (aby * aby)), lbc = Math.Sqrt((bcx * bcx) + (bcy * bcy));
                double cross = (abx * bcy) - (aby * bcx);
                if (lab < 0.5 || lbc < 0.5 || Math.Abs(cross) <= 0.02 * lab * lbc)
                {
                    p.RemoveAt(i);
                    more = true;
                    i--;
                }
            }
        }

        return p;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0)
        {
            return double.NaN;
        }

        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[(v.Count / 2) - 1] + v[v.Count / 2]) / 2;
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        double pos = q * (sorted.Length - 1);
        int i = (int)Math.Floor(pos);
        return i + 1 < sorted.Length ? sorted[i] + ((sorted[i + 1] - sorted[i]) * (pos - i)) : sorted[i];
    }

    /// <summary>The least-squares gradient (dy/du, dy/dv) of a set of cells.</summary>
    private static (double A, double B) Gradient(List<int> cells, Func<int, (double U, double V)> at, Func<int, double> val)
    {
        double mu = 0, mv = 0, my = 0;
        foreach (int c in cells)
        {
            (double u, double v) = at(c);
            mu += u;
            mv += v;
            my += val(c);
        }

        mu /= cells.Count;
        mv /= cells.Count;
        my /= cells.Count;
        double suu = 0, svv = 0, suv = 0, suy = 0, svy = 0;
        foreach (int c in cells)
        {
            (double u, double v) = at(c);
            double a = u - mu, b = v - mv, z = val(c) - my;
            suu += a * a;
            svv += b * b;
            suv += a * b;
            suy += a * z;
            svy += b * z;
        }

        double det = (suu * svv) - (suv * suv);
        return det < 1e-12 ? (0, 0) : (((suy * svv) - (svy * suv)) / det, ((svy * suu) - (suy * suv)) / det);
    }

    /// <summary>
    /// True when a climbing region is stairs: walking its pixels along the climb, most of the rise comes in jumps (risers)
    /// between level runs, where a ramp rises a little at every pixel.
    /// </summary>
    private static bool Stepped(double[] floor, int w, int h, List<int> cells, int cols, int cell, double a, double b, double quantum)
    {
        double g = Math.Sqrt((a * a) + (b * b));
        if (g < 1e-12)
        {
            return false;
        }

        double ux = a / g, uy = b / g;
        var inRegion = new HashSet<int>(cells);
        double jumpT = Math.Max(0.1, Math.Max(2.5 * quantum, 3 * g)); // a ramp climbs g a pixel; a riser all at once
        double jumps = 0, total = 0;
        int jumpCount = 0;
        foreach (int c in cells)
        {
            int cx = c % cols, cy = c / cols;
            for (int py = cy * cell; py < Math.Min(h, (cy + 1) * cell); py++)
            {
                for (int px = cx * cell; px < Math.Min(w, (cx + 1) * cell); px++)
                {
                    double v0 = floor[(py * w) + px];
                    int qx = (int)Math.Round(px + ux), qy = (int)Math.Round(py + uy);
                    if (double.IsNaN(v0) || (uint)qx >= (uint)w || (uint)qy >= (uint)h || !inRegion.Contains(((qy / cell) * cols) + (qx / cell)))
                    {
                        continue;
                    }

                    double v1 = floor[(qy * w) + qx];
                    if (double.IsNaN(v1))
                    {
                        continue;
                    }

                    double d = v1 - v0;
                    if (d <= 0)
                    {
                        continue;
                    }

                    total += d;
                    if (d >= jumpT)
                    {
                        jumps += d;
                        jumpCount++;
                    }
                }
            }
        }

        return jumpCount >= 4 && total > 0 && jumps >= 0.5 * total;
    }

    /// <summary>
    /// Removes diagonal-only touches: where a label holds two diagonal pixels of a 2 x 2 block and neither of the other
    /// two, its outline would pass the shared corner twice (a polygon kit.trace refuses as touching itself). The first
    /// off-diagonal pixel joins it, so the corner is crossed once; repeated until no such block is left.
    /// </summary>
    private static void Unpinch(int[] zone, int w, int h)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            for (int y = 0; y + 1 < h; y++)
            {
                for (int x = 0; x + 1 < w; x++)
                {
                    int p = (y * w) + x;
                    int a = zone[p], b = zone[p + 1], c = zone[p + w], d = zone[p + w + 1];
                    if (a >= 0 && a == d && b != a && c != a)
                    {
                        zone[p + 1] = a;
                        changed = true;
                    }
                    else if (b >= 0 && b == c && a != b && d != b)
                    {
                        zone[p] = b;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>Fills the unlabelled pockets (under <paramref name="maxArea"/> px, off the border) that one label surrounds.</summary>
    private static void FillHoles(int[] zone, int w, int h, int maxArea)
    {
        var empty = new bool[zone.Length];
        for (int p = 0; p < zone.Length; p++)
        {
            empty[p] = zone[p] < 0;
        }

        (int[] comp, List<int> sizes) = RefMask.Components(empty, w, h, true);
        var owner = new int[sizes.Count];
        Array.Fill(owner, -2); // -2: none seen yet, -1: border or several labels
        for (int p = 0; p < zone.Length; p++)
        {
            int k = comp[p];
            if (k < 0)
            {
                continue;
            }

            int x = p % w, yy = p / w;
            if (x == 0 || yy == 0 || x == w - 1 || yy == h - 1)
            {
                owner[k] = -1;
                continue;
            }

            foreach (int q in new[] { p - 1, p + 1, p - w, p + w })
            {
                int z = zone[q];
                if (z < 0 || owner[k] == -1)
                {
                    continue;
                }

                owner[k] = owner[k] == -2 ? z : owner[k] == z ? z : -1;
            }
        }

        for (int p = 0; p < zone.Length; p++)
        {
            int k = comp[p];
            if (k >= 0 && owner[k] >= 0 && sizes[k] <= maxArea)
            {
                zone[p] = owner[k];
            }
        }
    }

    /// <summary>The drop lines: each violet component's longest path through a coarse grid, simplified, with the floor either side.</summary>
    private static List<DecodedDrop> Drops(bool[] drop, double[] floor, int w, int h, double ppm, int x0, int y0)
    {
        var result = new List<DecodedDrop>();
        (int[] comp, List<int> sizes) = RefMask.Components(drop, w, h, true);
        const int S = 6;
        int gw = (w + S - 1) / S;
        var byComp = new Dictionary<int, Dictionary<int, (double X, double Y, int N)>>();
        for (int p = 0; p < drop.Length; p++)
        {
            int k = comp[p];
            if (k < 0 || sizes[k] < 12)
            {
                continue;
            }

            if (!byComp.TryGetValue(k, out var cells))
            {
                byComp[k] = cells = new Dictionary<int, (double X, double Y, int N)>();
            }

            int x = p % w, y = p / w, key = ((y / S) * gw) + (x / S);
            (double sx, double sy, int n) = cells.GetValueOrDefault(key);
            cells[key] = (sx + x + 0.5, sy + y + 0.5, n + 1);
        }

        int reach = (int)Math.Max(10, Math.Round(1.2 * ppm));
        foreach (KeyValuePair<int, Dictionary<int, (double X, double Y, int N)>> entry in byComp.OrderBy(kv => kv.Key))
        {
            Dictionary<int, (double X, double Y, int N)> cells = entry.Value;
            List<int> keys = cells.Keys.OrderBy(k => k).ToList();
            var at = keys.ToDictionary(k => k, k => (cells[k].X / cells[k].N, cells[k].Y / cells[k].N));
            List<int> Neighbours(int k)
            {
                var list = new List<int>();
                int gx = k % gw, gy = k / gw;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int q = ((gy + dy) * gw) + gx + dx;
                        if ((dx != 0 || dy != 0) && gx + dx >= 0 && gx + dx < gw && cells.ContainsKey(q))
                        {
                            list.Add(q);
                        }
                    }
                }

                return list;
            }

            (int Far, Dictionary<int, int> Prev) Bfs(int start)
            {
                var prev = new Dictionary<int, int> { [start] = start };
                var q = new Queue<int>();
                q.Enqueue(start);
                int last = start;
                while (q.Count > 0)
                {
                    last = q.Dequeue();
                    foreach (int nb in Neighbours(last))
                    {
                        if (prev.TryAdd(nb, last))
                        {
                            q.Enqueue(nb);
                        }
                    }
                }

                return (last, prev);
            }

            (int a, _) = Bfs(keys[0]);
            (int b, Dictionary<int, int> back) = Bfs(a);
            var path = new List<(double X, double Y)>();
            for (int k = b; ; k = back[k])
            {
                path.Add(at[k]);
                if (back[k] == k)
                {
                    break;
                }
            }

            if (path.Count < 2)
            {
                continue;
            }

            List<(double X, double Y)> simple = RefMask.Simplify(path, Math.Max(3, 0.25 * ppm)); // the ticks wiggle the path

            // The floor either side: march out from points along the line to the first floor pixel.
            var sideA = new List<double>();
            var sideB = new List<double>();
            for (int s = 0; s + 1 < simple.Count; s++)
            {
                (double ax, double ay) = simple[s];
                (double bx, double by) = simple[s + 1];
                double len = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
                if (len < 1e-6)
                {
                    continue;
                }

                double nx = -(by - ay) / len, ny = (bx - ax) / len;
                for (double t = 0.2; t < 0.85; t += 0.15)
                {
                    double mx = ax + ((bx - ax) * t), my = ay + ((by - ay) * t);
                    foreach ((List<double> side, double sgn) in new[] { (sideA, 1.0), (sideB, -1.0) })
                    {
                        for (int d = 2; d <= reach; d++)
                        {
                            int qx = (int)(mx + (nx * sgn * d)), qy = (int)(my + (ny * sgn * d));
                            if ((uint)qx >= (uint)w || (uint)qy >= (uint)h)
                            {
                                break;
                            }

                            int q = (qy * w) + qx;
                            if (!double.IsNaN(floor[q]))
                            {
                                side.Add(floor[q]);
                                break;
                            }
                        }
                    }
                }
            }

            // A drop edge has floor on both sides (a violet swatch in a legend panel has none).
            double ma = sideA.Count > 0 ? Median(sideA) : double.NaN, mb = sideB.Count > 0 ? Median(sideB) : double.NaN;
            if (double.IsNaN(ma) || double.IsNaN(mb) || Math.Abs(ma - mb) < 0.5)
            {
                continue;
            }

            result.Add(new DecodedDrop(simple.Select(q => (Math.Round(q.X + x0, 1), Math.Round(q.Y + y0, 1))).ToList(), Round(Math.Max(ma, mb)), Round(Math.Min(ma, mb))));
        }

        return result;
    }
}
