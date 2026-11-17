using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;

namespace Ged.Core.Analysis.Recreation;

/// <summary>
/// Matches a patch of a reference shot (a wall, floor, ceiling or trim in a screenshot or mockup) against the mounted
/// texture library (Lua <c>ref.match_textures</c>). Every picture is reduced to the same cheap descriptor on a
/// 64 × 64 grid: the CIE Lab mean and spread, a soft 8 × 8 a/b (chroma) histogram, the shape of the lightness
/// distribution (z-scores, so overall brightness from lighting matters less than pattern), contrast-normalised edge
/// density, a 4-bin edge-orientation histogram, and a fine/coarse gradient ratio (the pattern's scale). Each texture
/// is described twice, once as one repeat and once tiled 2 × 2, because a shot shows a surface at an unknown number of
/// repeats; the better of the two counts. The library's descriptors are computed once per mounted library and cached
/// on disk beside the thumbnails, so later calls take well under a second.
/// </summary>
public static class ShotMaterials
{
    /// <summary>Grid side the descriptor is computed on.</summary>
    public const int Grid = 64;

    private const int AbBins = 8;
    private const int LzBins = 8;
    private const int OrientBins = 4;
    private const int Scalars = 6; // meanL, meanA, meanB, stdL, edge, fine

    /// <summary>Floats in one descriptor.</summary>
    public const int Length = (AbBins * AbBins) + LzBins + OrientBins + Scalars;

    private const int CacheVersion = 3;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, Library> Loaded = new(StringComparer.Ordinal);

    /// <summary>One ranked texture.</summary>
    public sealed record Match(string Name, double Score, double Distance, double Repeats, double[] MeanRgb);

    /// <summary>The descriptor library of a mounted texture set.</summary>
    public sealed class Library
    {
        internal Library(string[] names, bool[] stock, float[] features, double[][] meanRgb)
        {
            Names = names;
            Stock = stock;
            Features = features;
            MeanRgb = meanRgb;
        }

        /// <summary>Texture names (the reference name a face uses, e.g. <c>mtl_floor01.tga</c>).</summary>
        public string[] Names { get; }

        /// <summary>True for textures a retail packfile carries.</summary>
        public bool[] Stock { get; }

        /// <summary>Two descriptors per texture (one repeat, 2 × 2 tiled), <see cref="Length"/> floats each.</summary>
        public float[] Features { get; }

        /// <summary>Mean sRGB colour per texture (0-255).</summary>
        public double[][] MeanRgb { get; }

        /// <summary>True when the library was read from the disk cache rather than built.</summary>
        public bool FromCache { get; internal set; }
    }

    /// <summary>
    /// The descriptor library of every texture in <paramref name="vfs"/> (stock and not; filtered at query time),
    /// from memory, the disk cache in <paramref name="cacheDirectory"/>, or built (decoding every texture once).
    /// </summary>
    public static Library LoadLibrary(AssetVfs vfs, string cacheDirectory, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        List<(string Name, string Stem)> entries = TextureEntries(vfs);
        string key = LibraryKey(vfs, entries);
        lock (CacheGate)
        {
            if (Loaded.TryGetValue(key, out Library? hit))
            {
                return hit;
            }
        }

        string file = Path.Combine(cacheDirectory, $"shotmat-v{CacheVersion}-{key}.bin");
        Library? lib = TryRead(file);
        if (lib is not null)
        {
            lib.FromCache = true;
        }
        else
        {
            lib = Build(vfs, entries, cancel);
            TryWrite(file, lib);
        }

        lock (CacheGate)
        {
            Loaded[key] = lib;
        }

        return lib;
    }

    /// <summary>The descriptor of an RGBA picture's rectangle (inside = optional per-pixel mask over the whole picture).</summary>
    public static float[] Describe(byte[] rgba, int width, int height, int x0, int y0, int w, int h, Func<int, int, bool>? inside = null)
    {
        // Square windows keep the pattern's proportions (a long thin crop squeezed onto a square grid would not match
        // anything): a crop longer than it is wide is read as overlapping squares along it, descriptors averaged.
        int side = Math.Min(w, h);
        int along = Math.Max(w, h) - side;
        int count = along <= side / 4 ? 1 : Math.Min(6, 1 + (int)Math.Ceiling(along / (side / 2.0)));
        var sum = new float[2 * Length];
        int used = 0;
        for (int k = 0; k < count; k++)
        {
            int off = count == 1 ? along / 2 : (int)Math.Round((double)along * k / (count - 1));
            int wx = w >= h ? x0 + off : x0, wy = w >= h ? y0 : y0 + off;
            float[]? rgb = Resample(rgba, width, height, wx, wy, count == 1 ? w : side, count == 1 ? h : side, inside, out bool[] valid, out _);
            if (rgb is null)
            {
                continue;
            }

            float[] d = Features(ToLabGrid(rgb, valid, false), valid).Concat(Features(ToLabGrid(rgb, valid, true), valid)).ToArray();
            for (int i = 0; i < d.Length; i++)
            {
                sum[i] += d[i];
            }

            used++;
        }

        if (used > 1)
        {
            for (int i = 0; i < sum.Length; i++)
            {
                sum[i] /= used;
            }
        }

        return sum;
    }

    /// <summary>The repeat counts a texture is described at (how many times it repeats across the crop).</summary>
    private static readonly double[] Repeats = { 0.5, 1, 2, 4 };

    /// <summary>Descriptors per texture: each repeat count as seen and white-balanced.</summary>
    private static int PerTexture => Repeats.Length * 2;

    /// <summary>
    /// Ranks the library's textures against a shot descriptor pair (as seen, white-balanced; best first). Each texture
    /// scores its best repeat count; the white-balanced comparison (pattern and relative colour, robust to a coloured
    /// light) pays a small penalty for the as-seen chroma difference so a grey wall under orange light can still match
    /// grey concrete while a truly orange texture under white light wins when one exists.
    /// </summary>
    public static List<Match> Rank(Library lib, float[] shot, int n, Func<int, bool> candidate)
    {
        var scored = new List<(int I, double D, double Repeat)>();
        int sb = (AbBins * AbBins) + LzBins + OrientBins;
        for (int i = 0; i < lib.Names.Length; i++)
        {
            if (!candidate(i))
            {
                continue;
            }

            double best = double.MaxValue;
            double rep = 1;
            for (int s = 0; s < Repeats.Length; s++)
            {
                int raw = ((i * PerTexture) + (2 * s)) * Length, neu = raw + Length;
                double dRaw = Distance(shot, 0, lib.Features, raw);
                double chroma = Math.Sqrt(Math.Pow(shot[sb + 1] - lib.Features[raw + sb + 1], 2) + Math.Pow(shot[sb + 2] - lib.Features[raw + sb + 2], 2));
                double dNeu = Distance(shot, Length, lib.Features, neu) + (0.006 * chroma) + 0.01;
                double d = Math.Min(dRaw, dNeu) + (0.02 * s); // a slight preference for fewer repeats on a tie
                if (d < best)
                {
                    best = d;
                    rep = Repeats[s];
                }
            }

            scored.Add((i, best, rep));
        }

        return scored.OrderBy(s => s.D).ThenBy(s => lib.Names[s.I], StringComparer.OrdinalIgnoreCase).Take(n)
            .Select(s => new Match(lib.Names[s.I], Math.Round(1 / (1 + (2 * s.D)), 4), Math.Round(s.D, 4), s.Repeat, lib.MeanRgb[s.I]))
            .ToList();
    }

    /// <summary>
    /// The contact sheet: the shot crop (masked pixels dimmed) beside the ranked textures, each labelled with its rank,
    /// name and score.
    /// </summary>
    public static byte[] ContactSheet(RefImage shot, int[] rect, Func<int, int, bool>? inside, IReadOnlyList<Match> matches, Func<string, TextureImage?> load)
    {
        const int Tile = 128, Pad = 8, Label = 26;
        int cols = Math.Min(5, matches.Count + 1);
        int rows = (matches.Count + 1 + cols - 1) / cols;
        var c = new RefCanvas(Pad + (cols * (Tile + Pad)), Pad + (rows * (Tile + Label + Pad)));

        // The crop, fitted (nearest neighbour).
        double s = Math.Min((double)Tile / rect[2], (double)Tile / rect[3]);
        int cw = Math.Max(1, (int)(rect[2] * s)), ch = Math.Max(1, (int)(rect[3] * s));
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int sx = rect[0] + Math.Min(rect[2] - 1, (int)(x / s)), sy = rect[1] + Math.Min(rect[3] - 1, (int)(y / s));
                (byte r, byte g, byte b) = shot.At(sx, sy);
                bool keep = inside is null || inside(sx, sy);
                c.Set(Pad + x, Pad + y, keep ? r : (byte)(r / 4), keep ? g : (byte)(g / 4), keep ? b : (byte)(b / 4));
            }
        }

        c.Rect(Pad - 1, Pad - 1, Pad + cw, Pad + ch, 255, 220, 60);
        c.Text(Pad, Pad + Tile + 4, "SHOT CROP", 1, 255, 220, 60);
        c.Text(Pad, Pad + Tile + 14, $"{rect[2]}x{rect[3]} px", 1, 200, 200, 200);

        for (int k = 0; k < matches.Count; k++)
        {
            int slot = k + 1;
            int ox = Pad + ((slot % cols) * (Tile + Pad)), oy = Pad + ((slot / cols) * (Tile + Label + Pad));
            TextureImage? img = load(matches[k].Name);
            if (img is not null)
            {
                TextureImage t = ImageOps.DownscaleToFit(img, Tile);
                for (int y = 0; y < Tile; y++)
                {
                    for (int x = 0; x < Tile; x++)
                    {
                        // Small textures tile to fill the cell, as they would on a face.
                        (byte r, byte g, byte b, _) = t.GetPixel(x % t.Width, y % t.Height);
                        c.Set(ox + x, oy + y, r, g, b);
                    }
                }
            }
            else
            {
                c.Fill(ox, oy, ox + Tile, oy + Tile, 60, 20, 20);
            }

            string name = matches[k].Name.Length > 20 ? matches[k].Name[..20] : matches[k].Name;
            c.Text(ox, oy + Tile + 4, $"{k + 1} {name}", 1, 235, 235, 235);
            c.Text(ox, oy + Tile + 14, "score " + RefCanvas.N(matches[k].Score), 1, 150, 210, 255);
        }

        return c.Png();
    }

    // ---- descriptors --------------------------------------------------------------------------------------------

    private static double Distance(float[] shot, int so, float[] lib, int o)
    {
        float[] a = so == 0 ? shot : shot[so..(so + Length)];
        int ab = AbBins * AbBins;
        double dAb = 0;
        for (int i = 0; i < ab; i++)
        {
            double p = a[i], q = lib[o + i], sum = p + q;
            if (sum > 1e-9)
            {
                dAb += (p - q) * (p - q) / sum;
            }
        }

        double dLz = 0;
        for (int i = 0; i < LzBins; i++)
        {
            double p = a[ab + i], q = lib[o + ab + i], sum = p + q;
            if (sum > 1e-9)
            {
                dLz += (p - q) * (p - q) / sum;
            }
        }

        double dOr = 0;
        int ob = ab + LzBins;
        for (int i = 0; i < OrientBins; i++)
        {
            dOr += Math.Abs(a[ob + i] - lib[o + ob + i]);
        }

        int sb = ob + OrientBins;
        double dL = Math.Abs(a[sb] - lib[o + sb]);
        double dChroma = Math.Sqrt(Math.Pow(a[sb + 1] - lib[o + sb + 1], 2) + Math.Pow(a[sb + 2] - lib[o + sb + 2], 2));
        double dStd = Math.Abs(a[sb + 3] - lib[o + sb + 3]) / (a[sb + 3] + lib[o + sb + 3] + 2);
        double dEdge = Math.Abs(a[sb + 4] - lib[o + sb + 4]) / (a[sb + 4] + lib[o + sb + 4] + 1e-3);
        double dFine = Math.Abs(a[sb + 5] - lib[o + sb + 5]);

        // Chroma and pattern weigh most; absolute lightness least (a shot's lighting moves it); orientation is light
        // (a floor seen in perspective squeezes it).
        return (0.5 * dAb) + (0.3 * dLz) + (0.2 * dOr) + (0.003 * dL) + (0.015 * dChroma) + (0.4 * dStd) + (0.45 * dEdge) + (0.35 * dFine);
    }

    /// <summary>The picture rectangle averaged onto the <see cref="Grid"/> grid as linear RGB per cell.</summary>
    private static float[]? Resample(byte[] rgba, int width, int height, int x0, int y0, int w, int h, Func<int, int, bool>? inside, out bool[] valid, out double[] meanRgb)
    {
        var sum = new double[Grid * Grid * 3];
        var cnt = new int[Grid * Grid];
        double mr = 0, mg = 0, mb = 0;
        long total = 0;
        // Each grid cell averages the source pixels it covers (at least one: small crops are stretched).
        for (int gy = 0; gy < Grid; gy++)
        {
            int ya = y0 + (gy * h / Grid), yb = Math.Max(ya + 1, y0 + ((gy + 1) * h / Grid));
            for (int gx = 0; gx < Grid; gx++)
            {
                int xa = x0 + (gx * w / Grid), xb = Math.Max(xa + 1, x0 + ((gx + 1) * w / Grid));
                int c = (gy * Grid) + gx;
                for (int y = ya; y < yb && y < height; y++)
                {
                    for (int x = xa; x < xb && x < width; x++)
                    {
                        if (inside is not null && !inside(x, y))
                        {
                            continue;
                        }

                        int o = ((y * width) + x) * 4;
                        if (rgba[o + 3] < 128)
                        {
                            continue;
                        }

                        sum[c * 3] += Lin(rgba[o]);
                        sum[(c * 3) + 1] += Lin(rgba[o + 1]);
                        sum[(c * 3) + 2] += Lin(rgba[o + 2]);
                        cnt[c]++;
                        mr += rgba[o];
                        mg += rgba[o + 1];
                        mb += rgba[o + 2];
                        total++;
                    }
                }
            }
        }

        valid = new bool[Grid * Grid];
        meanRgb = total == 0 ? new double[3] : new[] { Math.Round(mr / total), Math.Round(mg / total), Math.Round(mb / total) };
        if (total < 16)
        {
            return null;
        }

        var rgb = new float[Grid * Grid * 3];
        for (int c = 0; c < Grid * Grid; c++)
        {
            if (cnt[c] == 0)
            {
                continue;
            }

            valid[c] = true;
            rgb[c * 3] = (float)(sum[c * 3] / cnt[c]);
            rgb[(c * 3) + 1] = (float)(sum[(c * 3) + 1] / cnt[c]);
            rgb[(c * 3) + 2] = (float)(sum[(c * 3) + 2] / cnt[c]);
        }

        return rgb;
    }

    /// <summary>
    /// A linear-RGB grid as Lab. <paramref name="neutral"/> first scales each channel so the grid's mean is grey at its
    /// own brightness (a grey-world white balance: removes a light's colour cast, and the surface's own hue with it).
    /// </summary>
    private static float[] ToLabGrid(float[] rgb, bool[] valid, bool neutral)
    {
        double gr = 1, gg = 1, gb = 1;
        if (neutral)
        {
            double r = 0, g = 0, b = 0;
            for (int c = 0; c < Grid * Grid; c++)
            {
                if (valid[c])
                {
                    r += rgb[c * 3];
                    g += rgb[(c * 3) + 1];
                    b += rgb[(c * 3) + 2];
                }
            }

            double m = (r + g + b) / 3;
            gr = r > 1e-9 ? m / r : 1;
            gg = g > 1e-9 ? m / g : 1;
            gb = b > 1e-9 ? m / b : 1;
        }

        var lab = new float[Grid * Grid * 3];
        for (int c = 0; c < Grid * Grid; c++)
        {
            if (!valid[c])
            {
                continue;
            }

            (double L, double A, double B) = ToLab(Math.Min(1, rgb[c * 3] * gr), Math.Min(1, rgb[(c * 3) + 1] * gg), Math.Min(1, rgb[(c * 3) + 2] * gb));
            lab[c * 3] = (float)L;
            lab[(c * 3) + 1] = (float)A;
            lab[(c * 3) + 2] = (float)B;
        }

        return lab;
    }

    /// <summary>The grid showing the surface tiled k × k: each cell pools a k × k block of the one-repeat grid, wrapped.</summary>
    private static (float[] Rgb, bool[] Valid) Tile(float[] rgb, bool[] valid, double repeats)
    {
        if (repeats == 1)
        {
            return (rgb, valid);
        }

        var o = new float[Grid * Grid * 3];
        var v = new bool[Grid * Grid];
        if (repeats < 1)
        {
            // Less than one repeat across the crop: the texture's centre, enlarged.
            int off = (int)(Grid * (1 - repeats) / 2);
            for (int y = 0; y < Grid; y++)
            {
                for (int x = 0; x < Grid; x++)
                {
                    int s = ((off + (int)(y * repeats)) * Grid) + off + (int)(x * repeats), c = (y * Grid) + x;
                    o[c * 3] = rgb[s * 3];
                    o[(c * 3) + 1] = rgb[(s * 3) + 1];
                    o[(c * 3) + 2] = rgb[(s * 3) + 2];
                    v[c] = valid[s];
                }
            }

            return (o, v);
        }

        int k = (int)repeats;
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                double r = 0, g = 0, b = 0;
                int n = 0;
                for (int dy = 0; dy < k; dy++)
                {
                    for (int dx = 0; dx < k; dx++)
                    {
                        int c = ((((k * y) + dy) % Grid) * Grid) + (((k * x) + dx) % Grid);
                        if (valid[c])
                        {
                            r += rgb[c * 3];
                            g += rgb[(c * 3) + 1];
                            b += rgb[(c * 3) + 2];
                            n++;
                        }
                    }
                }

                if (n > 0)
                {
                    int c = (y * Grid) + x;
                    o[c * 3] = (float)(r / n);
                    o[(c * 3) + 1] = (float)(g / n);
                    o[(c * 3) + 2] = (float)(b / n);
                    v[c] = true;
                }
            }
        }

        return (o, v);
    }

    private static float[] Features(float[] labIn, bool[] valid)
    {
        var f = new float[Length];
        float[] lab = HighPass(labIn, valid, out double seenMean);
        int n = 0;
        double sl = 0, sa = 0, sb = 0, sll = 0;
        for (int c = 0; c < Grid * Grid; c++)
        {
            if (!valid[c])
            {
                continue;
            }

            n++;
            sl += lab[c * 3];
            sa += lab[(c * 3) + 1];
            sb += lab[(c * 3) + 2];
            sll += lab[c * 3] * lab[c * 3];
        }

        if (n == 0)
        {
            return f;
        }

        double mL = sl / n, mA = sa / n, mB = sb / n;
        double sd = Math.Sqrt(Math.Max(0, (sll / n) - (mL * mL)));
        int ab = AbBins * AbBins;
        for (int c = 0; c < Grid * Grid; c++)
        {
            if (!valid[c])
            {
                continue;
            }

            // Soft a/b histogram over [-32, 32] (bin width 8).
            Soft2(f, 0, AbBins, (lab[(c * 3) + 1] + 32) / 8.0, (lab[(c * 3) + 2] + 32) / 8.0, 1.0 / n);

            // Lightness z-score histogram over [-2, 2] (bin width 0.5).
            double z = (lab[c * 3] - mL) / Math.Max(sd, 2);
            Soft1(f, ab, LzBins, (z + 2) / 0.5, 1.0 / n);
        }

        // Gradients on L (central differences where both neighbours are inside).
        int ob = ab + LzBins;
        double mag = 0;
        int gn = 0;
        for (int y = 1; y < Grid - 1; y++)
        {
            for (int x = 1; x < Grid - 1; x++)
            {
                int c = (y * Grid) + x;
                if (!valid[c] || !valid[c - 1] || !valid[c + 1] || !valid[c - Grid] || !valid[c + Grid])
                {
                    continue;
                }

                double gx = lab[(c + 1) * 3] - lab[(c - 1) * 3];
                double gy = lab[(c + Grid) * 3] - lab[(c - Grid) * 3];
                double m = Math.Sqrt((gx * gx) + (gy * gy));
                mag += m;
                gn++;
                double ang = Math.Atan2(gy, gx);
                if (ang < 0)
                {
                    ang += Math.PI;
                }

                Soft1Wrap(f, ob, OrientBins, ang / Math.PI * OrientBins, m);
            }
        }

        double orientSum = 0;
        for (int i = 0; i < OrientBins; i++)
        {
            orientSum += f[ob + i];
        }

        for (int i = 0; i < OrientBins; i++)
        {
            f[ob + i] = orientSum > 1e-9 ? (float)(f[ob + i] / orientSum) : 0.25f;
        }

        double fineMag = gn > 0 ? mag / gn : 0;
        double coarseMag = CoarseGradient(lab, valid);
        int s0 = ob + OrientBins;
        f[s0] = (float)seenMean;
        f[s0 + 1] = (float)mA;
        f[s0 + 2] = (float)mB;
        f[s0 + 3] = (float)sd;
        f[s0 + 4] = (float)(fineMag / (sd + 4));
        f[s0 + 5] = (float)(fineMag + coarseMag > 1e-6 ? fineMag / (fineMag + coarseMag) : 0.5);
        return f;
    }

    /// <summary>
    /// Lightness with its slow variation removed (L minus its 21 × 21-cell local mean, plus the overall mean): a
    /// shot's lightmap shading and falloff go, the surface's own pattern stays. Returns the overall mean too.
    /// </summary>
    private static float[] HighPass(float[] lab, bool[] valid, out double mean)
    {
        const int R = 10, S = Grid + 1;
        var sum = new double[S * S];
        var cnt = new int[S * S];
        double total = 0;
        int n = 0;
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                int c = (y * Grid) + x;
                double v = valid[c] ? lab[c * 3] : 0;
                int k = valid[c] ? 1 : 0;
                total += v;
                n += k;
                sum[((y + 1) * S) + x + 1] = v + sum[(y * S) + x + 1] + sum[((y + 1) * S) + x] - sum[(y * S) + x];
                cnt[((y + 1) * S) + x + 1] = k + cnt[(y * S) + x + 1] + cnt[((y + 1) * S) + x] - cnt[(y * S) + x];
            }
        }

        mean = n > 0 ? total / n : 0;
        var o = (float[])lab.Clone();
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                int c = (y * Grid) + x;
                if (!valid[c])
                {
                    continue;
                }

                int ya = Math.Max(0, y - R), yb = Math.Min(Grid, y + R + 1), xa = Math.Max(0, x - R), xb = Math.Min(Grid, x + R + 1);
                double s = sum[(yb * S) + xb] - sum[(ya * S) + xb] - sum[(yb * S) + xa] + sum[(ya * S) + xa];
                int k = cnt[(yb * S) + xb] - cnt[(ya * S) + xb] - cnt[(yb * S) + xa] + cnt[(ya * S) + xa];
                o[c * 3] = (float)(lab[c * 3] - (s / Math.Max(1, k)) + mean);
            }
        }

        return o;
    }

    /// <summary>Mean gradient magnitude on the grid pooled 2 × 2 (the pattern at half the frequency).</summary>
    private static double CoarseGradient(float[] lab, bool[] valid)
    {
        const int H = Grid / 2;
        var l = new double[H * H];
        var ok = new bool[H * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < H; x++)
            {
                double s = 0;
                int k = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int c = (((2 * y) + dy) * Grid) + (2 * x) + dx;
                        if (valid[c])
                        {
                            s += lab[c * 3];
                            k++;
                        }
                    }
                }

                if (k > 0)
                {
                    l[(y * H) + x] = s / k;
                    ok[(y * H) + x] = true;
                }
            }
        }

        double mag = 0;
        int n = 0;
        for (int y = 1; y < H - 1; y++)
        {
            for (int x = 1; x < H - 1; x++)
            {
                int c = (y * H) + x;
                if (ok[c] && ok[c - 1] && ok[c + 1] && ok[c - H] && ok[c + H])
                {
                    double gx = l[c + 1] - l[c - 1], gy = l[c + H] - l[c - H];
                    mag += Math.Sqrt((gx * gx) + (gy * gy));
                    n++;
                }
            }
        }

        return n > 0 ? mag / n : 0;
    }

    private static void Soft1(float[] f, int o, int bins, double pos, double w)
    {
        pos = Math.Clamp(pos - 0.5, 0, bins - 1);
        int i = (int)Math.Floor(pos);
        double t = pos - i;
        f[o + i] += (float)(w * (1 - t));
        if (i + 1 < bins)
        {
            f[o + i + 1] += (float)(w * t);
        }
    }

    private static void Soft1Wrap(float[] f, int o, int bins, double pos, double w)
    {
        pos -= 0.5;
        int i = (int)Math.Floor(pos);
        double t = pos - i;
        f[o + (((i % bins) + bins) % bins)] += (float)(w * (1 - t));
        f[o + ((((i + 1) % bins) + bins) % bins)] += (float)(w * t);
    }

    private static void Soft2(float[] f, int o, int bins, double pa, double pb, double w)
    {
        pa = Math.Clamp(pa - 0.5, 0, bins - 1);
        pb = Math.Clamp(pb - 0.5, 0, bins - 1);
        int ia = (int)Math.Floor(pa), ib = (int)Math.Floor(pb);
        double ta = pa - ia, tb = pb - ib;
        for (int da = 0; da < 2; da++)
        {
            for (int db = 0; db < 2; db++)
            {
                int a = ia + da, b = ib + db;
                if (a < bins && b < bins)
                {
                    f[o + (a * bins) + b] += (float)(w * (da == 0 ? 1 - ta : ta) * (db == 0 ? 1 - tb : tb));
                }
            }
        }
    }

    internal static double Lin(byte v)
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    internal static (double L, double A, double B) ToLab(double r, double g, double b)
    {
        double x = ((0.4124 * r) + (0.3576 * g) + (0.1805 * b)) / 0.95047;
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double z = ((0.0193 * r) + (0.1192 * g) + (0.9505 * b)) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116);
        double fx = F(x), fy = F(y), fz = F(z);
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    // ---- the library --------------------------------------------------------------------------------------------

    private static List<(string Name, string Stem)> TextureEntries(AssetVfs vfs)
    {
        // One entry per texture stem; the reported name prefers .tga (what stock faces reference), else the first.
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in vfs.EnumerateTextures())
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (!byStem.TryGetValue(stem, out string? have) || (f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && !have.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)))
            {
                byStem[stem] = f;
            }
        }

        return byStem.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Value, kv.Key)).ToList();
    }

    private static string LibraryKey(AssetVfs vfs, List<(string Name, string Stem)> entries)
    {
        var sb = new StringBuilder();
        sb.Append(CacheVersion).Append('|').Append(Length).Append('|');
        foreach ((string name, _) in entries)
        {
            IAssetSource? src = vfs.FindSource(name);
            sb.Append(name).Append(':').Append(src?.GetSize(name) ?? 0).Append(':').Append(src?.Description).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    private static Library Build(AssetVfs vfs, List<(string Name, string Stem)> entries, CancellationToken cancel)
    {
        int count = entries.Count;
        var feats = new float[count][];
        var means = new double[count][];
        var stock = new bool[count];
        var gate = new object();
        Parallel.For(0, count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            string name = entries[i].Name;
            TextureImage? img = null;
            try
            {
                string? resolved;
                byte[]? data;
                lock (gate)
                {
                    stock[i] = StockContent.Describe(vfs, name)?.Stock ?? false;
                    resolved = vfs.ResolveTexture(name);
                    data = resolved is null || resolved.EndsWith(".atx", StringComparison.OrdinalIgnoreCase) ? null : vfs.ReadFile(resolved);
                    if (resolved is not null && data is null)
                    {
                        img = vfs.LoadTexture(name)?.Primary;
                    }
                }

                if (img is null && resolved is not null && data is not null)
                {
                    img = TextureDecoder.Decode(resolved, data).Primary;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                img = null; // undecodable: left out of the ranking
            }

            if (img is null || img.Width < 8 || img.Height < 8 || OpaqueShare(img) < 0.5)
            {
                return;
            }

            (feats[i], means[i]) = DescribeTexture(img);
        });

        var names = new List<string>();
        var st = new List<bool>();
        var flat = new List<float>();
        var mr = new List<double[]>();
        for (int i = 0; i < count; i++)
        {
            if (feats[i] is null)
            {
                continue;
            }

            names.Add(entries[i].Name);
            st.Add(stock[i]);
            flat.AddRange(feats[i]);
            mr.Add(means[i]);
        }

        return new Library(names.ToArray(), st.ToArray(), flat.ToArray(), mr.ToArray());
    }

    /// <summary>A texture's descriptors (each repeat count as seen, then white-balanced) and its mean colour.</summary>
    internal static (float[] Features, double[] Mean) DescribeTexture(TextureImage img)
    {
        float[]? one = Resample(img.Pixels, img.Width, img.Height, 0, 0, img.Width, img.Height, null, out bool[] v1, out double[] mean);
        if (one is null)
        {
            return (new float[PerTexture * Length], mean);
        }

        var all = new List<float>(PerTexture * Length);
        foreach (double k in Repeats)
        {
            (float[] rgb, bool[] v) = Tile(one, v1, k);
            all.AddRange(Features(ToLabGrid(rgb, v, false), v));
            all.AddRange(Features(ToLabGrid(rgb, v, true), v));
        }

        return (all.ToArray(), mean);
    }

    private static double OpaqueShare(TextureImage img)
    {
        long opaque = 0, n = 0;
        int step = Math.Max(1, (img.Width * img.Height) / 4096);
        for (int i = 0; i < img.Width * img.Height; i += step)
        {
            n++;
            if (img.Pixels[(i * 4) + 3] >= 128)
            {
                opaque++;
            }
        }

        return n == 0 ? 0 : (double)opaque / n;
    }

    private static Library? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadInt32() != CacheVersion || r.ReadInt32() != Length)
            {
                return null;
            }

            int n = r.ReadInt32();
            var names = new string[n];
            var stock = new bool[n];
            var means = new double[n][];
            var feats = new float[n * PerTexture * Length];
            for (int i = 0; i < n; i++)
            {
                names[i] = r.ReadString();
                stock[i] = r.ReadBoolean();
                means[i] = new double[] { r.ReadByte(), r.ReadByte(), r.ReadByte() };
                for (int k = 0; k < PerTexture * Length; k++)
                {
                    feats[(i * PerTexture * Length) + k] = r.ReadSingle();
                }
            }

            return new Library(names, stock, feats, means);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // The cache is an optimisation: a read-only cache folder only costs the rebuild next time. The write itself lives in
    // Ged.Core.Assets.CacheFile so the analysis code stays free of file-writing APIs (CorpusReadOnlyTests).
    private static void TryWrite(string file, Library lib) =>
        Ged.Core.Assets.CacheFile.TryWriteAtomic(file, w =>
        {
            w.Write(CacheVersion);
            w.Write(Length);
            w.Write(lib.Names.Length);
            for (int i = 0; i < lib.Names.Length; i++)
            {
                w.Write(lib.Names[i]);
                w.Write(lib.Stock[i]);
                w.Write((byte)Math.Clamp(lib.MeanRgb[i][0], 0, 255));
                w.Write((byte)Math.Clamp(lib.MeanRgb[i][1], 0, 255));
                w.Write((byte)Math.Clamp(lib.MeanRgb[i][2], 0, 255));
                for (int k = 0; k < PerTexture * Length; k++)
                {
                    w.Write(lib.Features[(i * PerTexture * Length) + k]);
                }
            }
        });
}
