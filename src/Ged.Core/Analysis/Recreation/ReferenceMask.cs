using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ged.Core.Analysis.Recreation;

/// <summary>One traced outline of a mask: its outer loop and holes in image pixels (corner coordinates), area in px².</summary>
public sealed record RefPolygon(IReadOnlyList<(double X, double Y)> Outer, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Holes, double AreaPx);

/// <summary>How <see cref="RefMask.Segment"/> picks the playable pixels.</summary>
public sealed class RefMaskOptions
{
    /// <summary>Only this rectangle (x, y, w, h source pixels) is segmented; null = the whole picture.</summary>
    public int[]? Crop { get; init; }

    /// <summary>Reference colours: a pixel within <see cref="Tolerance"/> of any of them matches.</summary>
    public IReadOnlyList<(byte R, byte G, byte B)> Colors { get; init; } = Array.Empty<(byte, byte, byte)>();

    /// <summary>RGB distance (0-441) a pixel may be from a reference colour.</summary>
    public double Tolerance { get; init; } = 40;

    /// <summary>True = the playable pixels are those that do NOT match (list the rock / background colours).</summary>
    public bool Invert { get; init; }

    /// <summary>Keep only the matching regions that contain one of these pixels (null = every region).</summary>
    public IReadOnlyList<(int X, int Y)>? Seeds { get; init; }

    /// <summary>Closing radius (px): bridges gaps and grout lines narrower than about twice this.</summary>
    public int Close { get; init; } = 2;

    /// <summary>Opening radius (px): removes specks and threads thinner than about twice this.</summary>
    public int Open { get; init; } = 1;

    /// <summary>Regions smaller than this (px²) are dropped.</summary>
    public int MinArea { get; init; } = 64;

    /// <summary>Holes smaller than this (px²) are filled.</summary>
    public int FillHoles { get; init; } = 64;

    /// <summary>
    /// Match in hue / saturation / value instead of RGB distance: a pixel matches a reference colour when its hue is within
    /// <see cref="HueTol"/> degrees (ignored for a grey reference, saturation under 0.12), its saturation within
    /// <see cref="SatTol"/> and its value within <see cref="ValTol"/>.
    /// </summary>
    public bool Hsv { get; init; }

    public double HueTol { get; init; } = 15;

    public double SatTol { get; init; } = 0.2;

    public double ValTol { get; init; } = 0.3;

    /// <summary>Explicit HSV boxes (hue degrees, wrapping when HueMin &gt; HueMax; saturation and value 0-1), matched after the colours.</summary>
    public IReadOnlyList<HsvRange> Ranges { get; init; } = Array.Empty<HsvRange>();
}

/// <summary>An HSV box: hue in degrees (null = any; wraps when min &gt; max), saturation and value 0-1.</summary>
public sealed record HsvRange(double? HueMin, double? HueMax, double SatMin, double SatMax, double ValMin, double ValMax)
{
    public bool Contains(double h, double s, double v) =>
        s >= SatMin && s <= SatMax && v >= ValMin && v <= ValMax
        && (HueMin is not double h0 || HueMax is not double h1 || (h0 <= h1 ? h >= h0 && h <= h1 : h >= h0 || h <= h1));
}

/// <summary>
/// A playable mask over a reference picture (Lua <c>ref.mask</c>): pixels picked by colour (or by colour around seed
/// points), cleaned by closing / opening, small specks and holes removed, and its outlines traced along pixel edges
/// and simplified (Douglas-Peucker) to a tolerance. Also loads a mask PNG an agent painted (white = playable).
/// </summary>
public sealed class RefMask
{
    public RefMask(int width, int height, bool[] mask)
    {
        Width = width;
        Height = height;
        M = mask;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major playable flags, the picture's size.</summary>
    public bool[] M { get; }

    public int Count => M.Count(b => b);

    /// <summary>Per pixel, the reference colour (then range) it matched, before cleaning; −1 for none. Set by <see cref="Segment"/>.</summary>
    public int[]? Class { get; private init; }

    /// <summary>Per reference colour (then range): the pixels of the segmented window it matched, and those on the window's border.</summary>
    public long[] Matches { get; private init; } = Array.Empty<long>();

    public long[] BorderMatches { get; private init; } = Array.Empty<long>();

    /// <summary>The segmented window's pixel count and border pixel count.</summary>
    public long WindowPixels { get; private init; }

    public long BorderPixels { get; private init; }

    /// <summary>Hue (degrees), saturation and value (0-1) of an RGB colour.</summary>
    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            h = max == rf ? 60 * (((gf - bf) / d) % 6) : max == gf ? 60 * (((bf - rf) / d) + 2) : 60 * (((rf - gf) / d) + 4);
            if (h < 0)
            {
                h += 360;
            }
        }

        return (h, max > 1e-9 ? d / max : 0, max);
    }

    /// <summary>The reference colour or range pixel (r, g, b) matches best (−1 = none), by RGB distance or HSV closeness.</summary>
    private static int Match(byte r, byte g, byte b, RefMaskOptions o, (double H, double S, double V)[] refHsv, double tol2)
    {
        int best = -1;
        double bestScore = double.PositiveInfinity;
        if (!o.Hsv)
        {
            for (int i = 0; i < o.Colors.Count; i++)
            {
                (byte cr, byte cg, byte cb) = o.Colors[i];
                double dr = r - cr, dg = g - cg, db = b - cb, d = (dr * dr) + (dg * dg) + (db * db);
                if (d <= tol2 && d < bestScore)
                {
                    bestScore = d;
                    best = i;
                }
            }
        }
        else
        {
            (double h, double s, double v) = ToHsv(r, g, b);
            for (int i = 0; i < refHsv.Length; i++)
            {
                (double rh, double rs, double rv) = refHsv[i];
                double ds = Math.Abs(s - rs), dv = Math.Abs(v - rv);
                if (ds > o.SatTol || dv > o.ValTol)
                {
                    continue;
                }

                double score = (ds / Math.Max(1e-6, o.SatTol)) + (dv / Math.Max(1e-6, o.ValTol));
                if (rs >= 0.12)
                {
                    double dh = Math.Abs(h - rh);
                    dh = Math.Min(dh, 360 - dh);
                    if (dh > o.HueTol || s < 0.06)
                    {
                        continue;
                    }

                    score += dh / Math.Max(1e-6, o.HueTol);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
            {
                for (int k = 0; k < o.Ranges.Count; k++)
                {
                    if (o.Ranges[k].Contains(h, s, v))
                    {
                        return o.Colors.Count + k;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Picks the playable pixels of <paramref name="img"/>.</summary>
    public static RefMask Segment(RefImage img, RefMaskOptions o)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(o);
        int w = img.Width, h = img.Height;
        int x0 = 0, y0 = 0, x1 = w, y1 = h;
        if (o.Crop is { } c)
        {
            x0 = Math.Clamp(c[0], 0, w);
            y0 = Math.Clamp(c[1], 0, h);
            x1 = Math.Clamp(c[0] + c[2], x0, w);
            y1 = Math.Clamp(c[1] + c[3], y0, h);
        }

        var m = new bool[w * h];
        var cls = new int[w * h];
        Array.Fill(cls, -1);
        int nref = o.Colors.Count + o.Ranges.Count;
        var matches = new long[nref];
        var borderHits = new long[nref];
        double tol2 = o.Tolerance * o.Tolerance;
        (double H, double S, double V)[] refHsv = o.Colors.Select(c => ToHsv(c.R, c.G, c.B)).ToArray();
        long borderPixels = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int p = (y * w) + x;
                bool edge = x == x0 || y == y0 || x == x1 - 1 || y == y1 - 1;
                borderPixels += edge ? 1 : 0;
                int k = Match(img.Rgba[p * 4], img.Rgba[(p * 4) + 1], img.Rgba[(p * 4) + 2], o, refHsv, tol2);
                if (k >= 0)
                {
                    matches[k]++;
                    borderHits[k] += edge ? 1 : 0;
                    cls[p] = k;
                }

                m[p] = (k >= 0) != o.Invert;
            }
        }

        if (o.Close > 0)
        {
            m = Erode(Dilate(m, w, h, o.Close), w, h, o.Close);
        }

        if (o.Open > 0)
        {
            m = Dilate(Erode(m, w, h, o.Open), w, h, o.Open);
        }

        // Clip again to the crop (closing can grow past it), then drop small regions and fill small holes.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x < x0 || x >= x1 || y < y0 || y >= y1)
                {
                    m[(y * w) + x] = false;
                }
            }
        }

        (int[] label, List<int> sizes) = Components(m, w, h, true);
        HashSet<int>? keep = null;
        if (o.Seeds is { Count: > 0 } seeds)
        {
            keep = new HashSet<int>();
            foreach ((int sx, int sy) in seeds)
            {
                if ((uint)sx < (uint)w && (uint)sy < (uint)h && label[(sy * w) + sx] >= 0)
                {
                    keep.Add(label[(sy * w) + sx]);
                }
            }
        }

        for (int p = 0; p < m.Length; p++)
        {
            if (m[p] && (sizes[label[p]] < o.MinArea || (keep is not null && !keep.Contains(label[p]))))
            {
                m[p] = false;
            }
        }

        if (o.FillHoles > 0)
        {
            (int[] bl, List<int> bsizes) = Components(m, w, h, false);
            var border = new HashSet<int>();
            for (int x = 0; x < w; x++)
            {
                border.Add(bl[x]);
                border.Add(bl[((h - 1) * w) + x]);
            }

            for (int y = 0; y < h; y++)
            {
                border.Add(bl[y * w]);
                border.Add(bl[(y * w) + w - 1]);
            }

            for (int p = 0; p < m.Length; p++)
            {
                if (!m[p] && bl[p] >= 0 && !border.Contains(bl[p]) && bsizes[bl[p]] < o.FillHoles)
                {
                    m[p] = true;
                }
            }
        }

        return new RefMask(w, h, m)
        {
            Class = o.Invert ? null : cls,
            Matches = matches,
            BorderMatches = borderHits,
            WindowPixels = (long)(x1 - x0) * (y1 - y0),
            BorderPixels = borderPixels,
        };
    }

    /// <summary>A mask with the same flags (and none of the segmentation's colour statistics).</summary>
    public RefMask Copy() => new(Width, Height, (bool[])M.Clone());

    /// <summary>
    /// The pixels of each colour class (the reference colour or range each matched pixel took), with pixels the cleaning
    /// added given the class of the nearest classed pixel; −1 outside the mask. Null when the mask has no classes.
    /// </summary>
    public int[]? ClassLabels()
    {
        if (Class is null)
        {
            return null;
        }

        var lab = new int[M.Length];
        var queue = new Queue<int>();
        for (int p = 0; p < M.Length; p++)
        {
            lab[p] = M[p] ? Class[p] : -1;
            if (lab[p] >= 0)
            {
                queue.Enqueue(p);
            }
        }

        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            int x = p % Width, y = p / Width;
            foreach (int q in new[] { x > 0 ? p - 1 : -1, x < Width - 1 ? p + 1 : -1, y > 0 ? p - Width : -1, y < Height - 1 ? p + Width : -1 })
            {
                if (q >= 0 && M[q] && lab[q] < 0)
                {
                    lab[q] = lab[p];
                    queue.Enqueue(q);
                }
            }
        }

        return lab;
    }

    /// <summary>
    /// Outlines per zone: <paramref name="zone"/> gives each pixel's zone (−1 = none); the mask's pixels of each zone are
    /// traced separately, so neighbouring zones share their seam exactly (seam ends are kept through simplification).
    /// With <paramref name="cutHoles"/>, every piece that would enclose a hole is cut along a pixel row through the middle
    /// of each hole into pieces without holes (they share the cut edges). <paramref name="snap"/>: a zone's own outline
    /// (polygon zones) that piece corners within 1.5 px of are moved onto, so seams follow the zone's straight edges.
    /// </summary>
    public List<(int Zone, RefPolygon Poly)> ZonePolygons(int[] zone, double tolerancePx, int maxPerZone, bool cutHoles, IReadOnlyDictionary<int, IReadOnlyList<(double X, double Y)>>? snap = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        int n = M.Length, w = Width, h = Height;
        var lab = new int[n];
        for (int p = 0; p < n; p++)
        {
            lab[p] = M[p] && zone[p] >= 0 ? zone[p] : -1;
        }

        var zoneOf = new Dictionary<int, int>();
        foreach (int z in lab.Where(v => v >= 0).Distinct())
        {
            zoneOf[z] = z;
        }

        if (cutHoles)
        {
            int next = zoneOf.Count == 0 ? 0 : zoneOf.Keys.Max() + 1;
            foreach (int z in zoneOf.Keys.ToList())
            {
                var sub = new bool[n];
                for (int p = 0; p < n; p++)
                {
                    sub[p] = lab[p] == z;
                }

                (int[] comp, _) = Components(sub, w, h, true);
                (int[] bg, List<int> bgSizes) = Components(sub, w, h, false);
                var touches = new bool[bgSizes.Count];
                var top = new int[bgSizes.Count];
                var bottom = new int[bgSizes.Count];
                var topPixel = new int[bgSizes.Count];
                Array.Fill(top, int.MaxValue);
                Array.Fill(bottom, -1);
                for (int p = 0; p < n; p++)
                {
                    int b = bg[p];
                    if (b < 0)
                    {
                        continue;
                    }

                    int x = p % w, y = p / w;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                    {
                        touches[b] = true;
                    }

                    if (y < top[b])
                    {
                        top[b] = y;
                        topPixel[b] = p;
                    }

                    bottom[b] = Math.Max(bottom[b], y);
                }

                var cuts = new Dictionary<int, List<int>>();
                for (int b = 0; b < bgSizes.Count; b++)
                {
                    if (touches[b] || top[b] == 0)
                    {
                        continue;
                    }

                    int owner = comp[topPixel[b] - w];
                    if (owner < 0)
                    {
                        continue;
                    }

                    if (!cuts.TryGetValue(owner, out List<int>? list))
                    {
                        cuts[owner] = list = new List<int>();
                    }

                    list.Add(top[b] + ((bottom[b] - top[b] + 1) / 2));
                }

                var ids = new Dictionary<(int C, int Band), int>();
                for (int p = 0; p < n; p++)
                {
                    if (!sub[p] || !cuts.TryGetValue(comp[p], out List<int>? rows))
                    {
                        continue;
                    }

                    int y = p / w, band = rows.Count(r => r <= y);
                    if (band == 0)
                    {
                        continue;
                    }

                    if (!ids.TryGetValue((comp[p], band), out int id))
                    {
                        ids[(comp[p], band)] = id = next++;
                        zoneOf[id] = z;
                    }

                    lab[p] = id;
                }
            }
        }

        int L(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? lab[(y * w) + x] : -1;
        var result = new List<(int Zone, RefPolygon Poly)>();
        foreach (IGrouping<int, int> group in zoneOf.Keys.GroupBy(id => zoneOf[id]).OrderBy(g => g.Key))
        {
            var polys = new List<RefPolygon>();
            foreach (int id in group)
            {
                var sub = new bool[n];
                for (int p = 0; p < n; p++)
                {
                    sub[p] = lab[p] == id;
                }

                polys.AddRange(PolygonsOf(sub, tolerancePx, int.MaxValue, (x, y) =>
                {
                    // A seam end: the four pixels around the corner hold at least three different labels (two pieces
                    // and the outside, or three pieces).
                    int a = L(x - 1, y - 1), b = L(x, y - 1), c = L(x - 1, y), d = L(x, y);
                    int distinct = 1 + (b != a ? 1 : 0) + (c != a && c != b ? 1 : 0) + (d != a && d != b && d != c ? 1 : 0);
                    return distinct >= 3;
                }));
            }

            if (snap is not null && snap.TryGetValue(group.Key, out IReadOnlyList<(double X, double Y)>? ring))
            {
                polys = polys.Select(pp => SnapTo(pp, ring)).ToList();
            }

            foreach (RefPolygon pp in polys.OrderByDescending(pp => pp.AreaPx).Take(maxPerZone))
            {
                result.Add((group.Key, pp));
            }
        }

        return result;
    }

    private static RefPolygon SnapTo(RefPolygon poly, IReadOnlyList<(double X, double Y)> ring)
    {
        List<(double X, double Y)> Snap(IReadOnlyList<(double X, double Y)> loop) => loop.Select(q =>
        {
            double best = 1.5;
            (double X, double Y) to = q;
            for (int i = 0; i < ring.Count; i++)
            {
                (double ax, double ay) = ring[i];
                (double bx, double by) = ring[(i + 1) % ring.Count];
                double d = SegDist(q.X, q.Y, ax, ay, bx, by);
                if (d < best)
                {
                    double vx = bx - ax, vy = by - ay, l2 = (vx * vx) + (vy * vy);
                    double t = l2 > 0 ? Math.Clamp((((q.X - ax) * vx) + ((q.Y - ay) * vy)) / l2, 0, 1) : 0;
                    best = d;
                    to = (Math.Round(ax + (t * vx), 3), Math.Round(ay + (t * vy), 3));
                }
            }

            return to;
        }).ToList();

        List<(double X, double Y)> outer = Snap(poly.Outer);
        var holes = poly.Holes.Select(hh => (IReadOnlyList<(double X, double Y)>)Snap(hh)).ToList();
        return new RefPolygon(outer, holes, Area(outer) + holes.Sum(hh => Area(hh)));
    }

    /// <summary>A mask from a PNG the size of the picture: playable = bright (luma ≥ 128) and opaque.</summary>
    public static RefMask Load(string path, int width, int height)
    {
        RefImage img = RefImage.Load(path);
        if (img.Width != width || img.Height != height)
        {
            throw new InvalidDataException($"The mask is {img.Width}x{img.Height}; the reference picture is {width}x{height}.");
        }

        var m = new bool[width * height];
        for (int p = 0; p < m.Length; p++)
        {
            int o = p * 4;
            m[p] = img.Rgba[o + 3] >= 128 && ((img.Rgba[o] * 299) + (img.Rgba[o + 1] * 587) + (img.Rgba[o + 2] * 114)) / 1000 >= 128;
        }

        return new RefMask(width, height, m);
    }

    /// <summary>The mask as a black / white PNG.</summary>
    public byte[] Png()
    {
        var px = new byte[Width * Height * 4];
        for (int p = 0; p < M.Length; p++)
        {
            byte v = M[p] ? (byte)255 : (byte)0;
            px[p * 4] = v;
            px[(p * 4) + 1] = v;
            px[(p * 4) + 2] = v;
            px[(p * 4) + 3] = 255;
        }

        return IO.Tex.PngWriter.Encode(Width, Height, px);
    }

    /// <summary>The outlines, largest first, simplified to <paramref name="tolerancePx"/> (holes attached to their outline).</summary>
    public List<RefPolygon> Polygons(double tolerancePx, int max = 64) => PolygonsOf(M, tolerancePx, max, null);

    /// <summary>The outlines of <paramref name="m"/>; corners where <paramref name="pin"/> is true survive simplification.</summary>
    private List<RefPolygon> PolygonsOf(bool[] m, double tolerancePx, int max, Func<int, int, bool>? pin)
    {
        var loops = TraceLoops(m, Width, Height);
        var outers = new List<(List<(double X, double Y)> P, double A)>();
        var holes = new List<List<(double X, double Y)>>();
        foreach (List<(int X, int Y)> loop in loops)
        {
            var raw = loop.Select(q => ((double)q.X, (double)q.Y)).ToList();
            bool[]? pins = pin is null ? null : loop.Select(q => pin(q.X, q.Y)).ToArray();
            var pts = pins is not null && pins.Any(b => b) ? SimplifyPinned(raw, tolerancePx, pins) : Simplify(raw, tolerancePx);
            if (pts.Count < 3)
            {
                continue;
            }

            double a = Area(pts);
            if (a > 0)
            {
                outers.Add((pts, a));
            }
            else if (a < 0)
            {
                holes.Add(pts);
            }
        }

        outers.Sort((a, b) => b.A.CompareTo(a.A));
        var result = new List<RefPolygon>();
        foreach ((List<(double X, double Y)> p, double a) in outers.Take(max))
        {
            var mine = holes.Where(hp => Inside(p, hp[0].X + 0.01, hp[0].Y + 0.01) || Inside(p, hp[hp.Count / 2].X, hp[hp.Count / 2].Y)).ToList();
            foreach (var hp in mine)
            {
                holes.Remove(hp);
            }

            result.Add(new RefPolygon(p, mine.Select(hp => (IReadOnlyList<(double X, double Y)>)hp).ToList(), a - mine.Sum(hp => -Area(hp))));
        }

        return result;
    }

    /// <summary>The picture at half brightness with the mask tinted green and each outline drawn with its corners.</summary>
    public byte[] Overlay(RefImage img, IReadOnlyList<RefPolygon> polygons)
    {
        var c = new RefCanvas(Width, Height);
        for (int p = 0; p < M.Length; p++)
        {
            int x = p % Width, y = p / Width;
            (byte r, byte g, byte b) = img.At(x, y);
            c.Set(x, y, (byte)(r / 2), (byte)(g / 2), (byte)(b / 2));
            if (M[p])
            {
                c.Blend(x, y, 40, 230, 90, 0.35);
            }
        }

        int k = 0;
        foreach (RefPolygon poly in polygons)
        {
            k++;
            foreach (IReadOnlyList<(double X, double Y)> loop in new[] { poly.Outer }.Concat(poly.Holes))
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    (double ax, double ay) = loop[i];
                    (double bx, double by) = loop[(i + 1) % loop.Count];
                    c.Line(ax, ay, bx, by, 255, 230, 0, 1, 2);
                }

                foreach ((double x, double y) in loop)
                {
                    c.Fill((int)x - 2, (int)y - 2, (int)x + 3, (int)y + 3, 255, 60, 60);
                }
            }

            (double sx, double sy) = poly.Outer[0];
            c.Text((int)sx + 4, (int)sy + 4, $"#{k}", 2, 255, 255, 255, box: true);
        }

        return c.Png();
    }

    // ---- morphology and components ----

    internal static bool[] Dilate(bool[] m, int w, int h, int r)
    {
        double[] d = RecreationScorer.Distance(m, w, h);
        var o = new bool[m.Length];
        for (int p = 0; p < m.Length; p++)
        {
            o[p] = d[p] <= r + 0.01;
        }

        return o;
    }

    internal static bool[] Erode(bool[] m, int w, int h, int r)
    {
        var inv = new bool[m.Length];
        for (int p = 0; p < m.Length; p++)
        {
            inv[p] = !m[p];
        }

        bool[] grown = Dilate(inv, w, h, r);
        var o = new bool[m.Length];
        for (int p = 0; p < m.Length; p++)
        {
            o[p] = !grown[p];
        }

        return o;
    }

    /// <summary>4-connected components of the pixels equal to <paramref name="value"/>: labels (−1 elsewhere) and sizes.</summary>
    internal static (int[] Label, List<int> Sizes) Components(bool[] m, int w, int h, bool value)
    {
        var label = new int[m.Length];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (int s = 0; s < m.Length; s++)
        {
            if (m[s] != value || label[s] >= 0)
            {
                continue;
            }

            int id = sizes.Count, n = 0;
            label[s] = id;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                n++;
                int x = p % w, y = p / w;
                void Try(int q)
                {
                    if (m[q] == value && label[q] < 0)
                    {
                        label[q] = id;
                        stack.Push(q);
                    }
                }

                if (x > 0) Try(p - 1);
                if (x < w - 1) Try(p + 1);
                if (y > 0) Try(p - w);
                if (y < h - 1) Try(p + w);
            }

            sizes.Add(n);
        }

        return (label, sizes);
    }

    // ---- outline tracing ----

    /// <summary>
    /// The mask's boundary loops along pixel edges (corner coordinates): outlines have positive shoelace area in image
    /// coordinates (clockwise on screen), holes negative. Diagonal touches stay separate (4-connected regions).
    /// </summary>
    internal static List<List<(int X, int Y)>> TraceLoops(bool[] m, int w, int h)
    {
        bool F(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && m[(y * w) + x];
        var edges = new List<(int Ax, int Ay, int Bx, int By)>();
        var from = new Dictionary<long, List<int>>();
        void Add(int ax, int ay, int bx, int by)
        {
            long key = ((long)ay << 32) | (uint)ax;
            if (!from.TryGetValue(key, out List<int>? list))
            {
                from[key] = list = new List<int>(1);
            }

            list.Add(edges.Count);
            edges.Add((ax, ay, bx, by));
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!m[(y * w) + x])
                {
                    continue;
                }

                if (!F(x, y - 1)) Add(x, y, x + 1, y);
                if (!F(x + 1, y)) Add(x + 1, y, x + 1, y + 1);
                if (!F(x, y + 1)) Add(x + 1, y + 1, x, y + 1);
                if (!F(x - 1, y)) Add(x, y + 1, x, y);
            }
        }

        var used = new bool[edges.Count];
        var loops = new List<List<(int X, int Y)>>();
        for (int s = 0; s < edges.Count; s++)
        {
            if (used[s])
            {
                continue;
            }

            var loop = new List<(int X, int Y)>();
            int e = s;
            while (!used[e])
            {
                used[e] = true;
                (int ax, int ay, int bx, int by) = edges[e];
                loop.Add((ax, ay));
                int dx = bx - ax, dy = by - ay;
                List<int> next = from[((long)by << 32) | (uint)bx];
                int pick = -1, best = int.MaxValue;
                foreach (int n in next)
                {
                    if (used[n] && n != s)
                    {
                        continue;
                    }

                    (int nax, int nay, int nbx, int nby) = edges[n];
                    int ndx = nbx - nax, ndy = nby - nay;
                    // Prefer a right turn (keeps to the current pixel), then straight on, then left.
                    int rank = (ndx == -dy && ndy == dx) ? 0 : (ndx == dx && ndy == dy) ? 1 : 2;
                    if (rank < best)
                    {
                        best = rank;
                        pick = n;
                    }
                }

                if (pick < 0)
                {
                    break;
                }

                e = pick;
            }

            // Drop collinear corners.
            var clean = new List<(int X, int Y)>();
            for (int i = 0; i < loop.Count; i++)
            {
                (int px, int py) = loop[(i + loop.Count - 1) % loop.Count];
                (int cx, int cy) = loop[i];
                (int nx, int ny) = loop[(i + 1) % loop.Count];
                if (((cx - px) * (ny - cy)) - ((cy - py) * (nx - cx)) != 0)
                {
                    clean.Add((cx, cy));
                }
            }

            if (clean.Count >= 3)
            {
                loops.Add(clean);
            }
        }

        return loops;
    }

    /// <summary>Douglas-Peucker on a closed loop (split at the two points farthest apart).</summary>
    internal static List<(double X, double Y)> Simplify(List<(double X, double Y)> p, double tol)
    {
        if (p.Count <= 4 || tol <= 0)
        {
            return p;
        }

        int far = 0;
        double best = -1;
        for (int i = 1; i < p.Count; i++)
        {
            double d = Sq(p[i].X - p[0].X) + Sq(p[i].Y - p[0].Y);
            if (d > best)
            {
                best = d;
                far = i;
            }
        }

        var keep = new bool[p.Count];
        keep[0] = keep[far] = true;
        Dp(p, 0, far, tol, keep);
        Dp(p, far, p.Count, tol, keep);
        var o = new List<(double X, double Y)>();
        for (int i = 0; i < p.Count; i++)
        {
            if (keep[i])
            {
                o.Add(p[i]);
            }
        }

        return o;
    }

    /// <summary>Douglas-Peucker on a closed loop between consecutive pinned corners (pins always kept).</summary>
    internal static List<(double X, double Y)> SimplifyPinned(List<(double X, double Y)> p, double tol, bool[] pins)
    {
        int n = p.Count;
        var keep = (bool[])pins.Clone();
        var idx = Enumerable.Range(0, n).Where(i => pins[i]).ToList();
        if (tol > 0)
        {
            for (int k = 0; k < idx.Count; k++)
            {
                int a = idx[k], b = k + 1 < idx.Count ? idx[k + 1] : idx[0] + n;
                DpMod(p, a, b, tol, keep);
            }
        }
        else
        {
            Array.Fill(keep, true);
        }

        return Enumerable.Range(0, n).Where(i => keep[i]).Select(i => p[i]).ToList();
    }

    private static void DpMod(List<(double X, double Y)> p, int a, int b, double tol, bool[] keep)
    {
        int n = p.Count;
        (double ax, double ay) = p[a % n];
        (double bx, double by) = p[b % n];
        int idx = -1;
        double best = tol;
        for (int i = a + 1; i < b; i++)
        {
            double d = SegDist(p[i % n].X, p[i % n].Y, ax, ay, bx, by);
            if (d > best)
            {
                best = d;
                idx = i;
            }
        }

        if (idx >= 0)
        {
            keep[idx % n] = true;
            DpMod(p, a, idx, tol, keep);
            DpMod(p, idx, b, tol, keep);
        }
    }

    private static void Dp(List<(double X, double Y)> p, int a, int b, double tol, bool[] keep)
    {
        // b may equal p.Count (wraps to point 0).
        (double ax, double ay) = p[a];
        (double bx, double by) = p[b % p.Count];
        int idx = -1;
        double best = tol;
        for (int i = a + 1; i < b; i++)
        {
            double d = SegDist(p[i].X, p[i].Y, ax, ay, bx, by);
            if (d > best)
            {
                best = d;
                idx = i;
            }
        }

        if (idx >= 0)
        {
            keep[idx] = true;
            Dp(p, a, idx, tol, keep);
            Dp(p, idx, b, tol, keep);
        }
    }

    internal static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double l2 = (vx * vx) + (vy * vy);
        double t = l2 > 0 ? Math.Clamp((((px - ax) * vx) + ((py - ay) * vy)) / l2, 0, 1) : 0;
        return Math.Sqrt(Sq(px - (ax + (t * vx))) + Sq(py - (ay + (t * vy))));
    }

    internal static double Area(IReadOnlyList<(double X, double Y)> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            (double ax, double ay) = p[i];
            (double bx, double by) = p[(i + 1) % p.Count];
            s += (ax * by) - (bx * ay);
        }

        return s / 2;
    }

    internal static bool Inside(IReadOnlyList<(double X, double Y)> p, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            if ((p[i].Y > y) != (p[j].Y > y) && x < ((p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y)) + p[i].X)
            {
                c = !c;
            }
        }

        return c;
    }

    private static double Sq(double v) => v * v;
}
