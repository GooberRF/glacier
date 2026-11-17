using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Analysis.Recreation;

/// <summary>
/// An eye-level camera: eye position (world metres), yaw (heading in degrees: 0 = +Z, 90 = +X), pitch (degrees, up
/// positive) and the horizontal field of view (degrees).
/// </summary>
public readonly record struct ShotPose(double X, double Y, double Z, double Yaw, double Pitch, double Hfov)
{
    /// <summary>The unit view direction.</summary>
    public Vec3d Forward => Dir(Yaw, Pitch);

    /// <summary>The unit right axis (left-handed: yaw 0 looks +Z with +X to the right).</summary>
    public Vec3d Right
    {
        get
        {
            double y = Yaw * Math.PI / 180;
            return new Vec3d(Math.Cos(y), 0, -Math.Sin(y));
        }
    }

    /// <summary>The unit up axis of the camera.</summary>
    public Vec3d Up => Right.Cross(Forward).Scale(-1);

    /// <summary>The unit direction for a heading and an elevation in degrees.</summary>
    public static Vec3d Dir(double yawDeg, double pitchDeg)
    {
        double y = yawDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180;
        return new Vec3d(Math.Cos(p) * Math.Sin(y), Math.Sin(p), Math.Cos(p) * Math.Cos(y));
    }

    /// <summary>The unit ray through picture point (u, v) (pixels, origin top-left) of a w x h picture.</summary>
    public Vec3d Ray(double u, double v, int w, int h)
    {
        double f = (w / 2.0) / Math.Tan(Hfov * Math.PI / 360);
        return Forward.Scale(f).Add(Right.Scale(u - (w / 2.0))).Add(Up.Scale((h / 2.0) - v)).Normalized();
    }

    /// <summary>Projects a world direction to picture coordinates; false when it is behind the camera.</summary>
    public bool Project(Vec3d d, int w, int h, out double u, out double v)
    {
        double z = d.Dot(Forward);
        u = v = 0;
        if (z <= 1e-6)
        {
            return false;
        }

        double f = (w / 2.0) / Math.Tan(Hfov * Math.PI / 360);
        u = (w / 2.0) + (f * d.Dot(Right) / z);
        v = (h / 2.0) - (f * d.Dot(Up) / z);
        return true;
    }
}

/// <summary>
/// Matches eye-level screenshots against the current build: the compiled geometry is ray-cast (no GPU) into depth +
/// face maps, whose crease and silhouette edges are compared with the screenshot's luminance edges by normalised
/// cross-correlation of blurred edge maps. <see cref="Locate"/> searches walkable floor positions coarse to fine with
/// a 360-degree panorama per position (every yaw is a column shift), <see cref="Render"/> renders one pose, and
/// <see cref="Measure"/> casts the ray through one picture point.
/// </summary>
public sealed class ShotScene
{
    private readonly SpaceSampler _s;

    private ShotScene(SpaceSampler s) => _s = s;

    /// <summary>The faces the scene casts against.</summary>
    public int FaceCount => _s.FaceCount;

    /// <summary>Builds the ray scene of compiled geometry.</summary>
    public static ShotScene Of(Geometry geo, CancellationToken ct) => new(SpaceSampler.Build(geo, ct));

    /// <summary>The nearest hit along a unit ray: distance, face normal and whether a floor/ceiling/wall.</summary>
    public (double T, Vec3d Normal)? Cast(Vec3d o, Vec3d d, double max = 400)
    {
        double? t = _s.Cast(o, d, max, SpaceSampler.HitFilter.Any, out int face);
        if (t is not double tt || face < 0)
        {
            return null;
        }

        Vec3d n = _s.Faces[face].N;
        if (n.Dot(d) > 0)
        {
            n = n.Scale(-1);
        }

        return (tt, n);
    }

    /// <summary>The floor under (x, z) at or below y (null when none within 50 m).</summary>
    public double? FloorBelow(double x, double y, double z)
    {
        double? t = _s.Cast(new Vec3d(x, y, z), new Vec3d(0, -1, 0), 50, SpaceSampler.HitFilter.UpFacing, out _);
        return t is double tt ? y - tt : null;
    }

    /// <summary>The clear air above (x, y, z) (null = more than 60 m or open sky).</summary>
    public double? Headroom(double x, double y, double z) =>
        _s.Cast(new Vec3d(x, y, z), new Vec3d(0, 1, 0), 60, SpaceSampler.HitFilter.DownFacing, out _);

    // ---- per-pose depth + face maps ----------------------------------------------------------------------------

    /// <summary>A ray-cast view: depth (+inf = nothing hit) and the hit face's normal per cell.</summary>
    internal sealed class View
    {
        public View(int w, int h)
        {
            W = w;
            H = h;
            Depth = new float[w * h];
            Nx = new float[w * h];
            Ny = new float[w * h];
            Nz = new float[w * h];
            Pd = new float[w * h];
        }

        public int W { get; }

        public int H { get; }

        public float[] Depth { get; }

        public float[] Nx { get; }

        public float[] Ny { get; }

        public float[] Nz { get; }

        /// <summary>The hit plane's offset n·p (two cells on one plane share normal and offset).</summary>
        public float[] Pd { get; }

        public void Set(int i, (double T, Vec3d N)? hit, Vec3d o, Vec3d d)
        {
            if (hit is { } h)
            {
                Depth[i] = (float)h.T;
                Nx[i] = (float)h.N.X;
                Ny[i] = (float)h.N.Y;
                Nz[i] = (float)h.N.Z;
                Pd[i] = (float)h.N.Dot(o.Add(d.Scale(h.T)));
            }
            else
            {
                Depth[i] = float.PositiveInfinity;
            }
        }

        /// <summary>True when cells a and b see different surfaces (crease, depth jump or silhouette).</summary>
        public bool Break(int a, int b)
        {
            float da = Depth[a], db = Depth[b];
            bool ia = float.IsPositiveInfinity(da), ib = float.IsPositiveInfinity(db);
            if (ia || ib)
            {
                return ia != ib;
            }

            if (Math.Abs(Pd[a] - Pd[b]) > 0.04f + (0.004f * Math.Min(da, db)))
            {
                return true;
            }

            return (Nx[a] * Nx[b]) + (Ny[a] * Ny[b]) + (Nz[a] * Nz[b]) < 0.94f;
        }

        /// <summary>The edge map: 1 where a cell differs from its right or lower neighbour (columns wrap when asked).</summary>
        public float[] Edges(bool wrap)
        {
            var e = new float[W * H];
            for (int r = 0; r < H; r++)
            {
                for (int c = 0; c < W; c++)
                {
                    int i = (r * W) + c;
                    int right = c + 1 < W ? i + 1 : (wrap ? r * W : -1);
                    if ((right >= 0 && Break(i, right)) || (r + 1 < H && Break(i, i + W)))
                    {
                        e[i] = 1;
                    }
                }
            }

            return e;
        }
    }

    /// <summary>Ray-casts the perspective view of a pose at w x h.</summary>
    internal View Perspective(ShotPose pose, int w, int h)
    {
        var v = new View(w, h);
        var o = new Vec3d(pose.X, pose.Y, pose.Z);
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                Vec3d d = pose.Ray(c + 0.5, r + 0.5, w, h);
                v.Set((r * w) + c, Cast(o, d), o, d);
            }
        });
        return v;
    }

    /// <summary>A cylindrical panorama: column c looks along heading c·step, row r at elevation top − (r + 0.5)·step.</summary>
    internal View Panorama(Vec3d o, double step, double top)
    {
        int w = (int)Math.Round(360 / step), h = (int)Math.Round(2 * top / step);
        var v = new View(w, h);
        for (int r = 0; r < h; r++)
        {
            double el = top - ((r + 0.5) * step);
            for (int c = 0; c < w; c++)
            {
                Vec3d d = ShotPose.Dir(c * step, el);
                v.Set((r * w) + c, Cast(o, d), o, d);
            }
        }

        return v;
    }

    // ---- screenshot edges ------------------------------------------------------------------------------------

    /// <summary>A screenshot's luminance-gradient magnitude, blurred, normalised to its 95th percentile.</summary>
    public sealed class ShotEdges
    {
        private ShotEdges(int w, int h, float[] m, int srcW, int srcH)
        {
            W = w;
            H = h;
            M = m;
            SrcW = srcW;
            SrcH = srcH;
        }

        public int W { get; }

        public int H { get; }

        public float[] M { get; }

        public int SrcW { get; }

        public int SrcH { get; }

        /// <summary>Gradient map of the picture scaled to width <paramref name="w"/> (keeping its aspect).</summary>
        public static ShotEdges Of(RefImage img, int w)
        {
            int h = Math.Max(8, (int)Math.Round(w * (double)img.Height / img.Width));
            var lum = new float[w * h];
            double sx = (double)img.Width / w, sy = (double)img.Height / h;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // box-average the source pixels this cell covers
                    int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)((x + 1) * sx)), y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)((y + 1) * sy));
                    double sum = 0;
                    int n = 0;
                    for (int yy = y0; yy < Math.Min(y1, img.Height); yy++)
                    {
                        for (int xx = x0; xx < Math.Min(x1, img.Width); xx++)
                        {
                            int o = ((yy * img.Width) + xx) * 4;
                            sum += (0.299 * img.Rgba[o]) + (0.587 * img.Rgba[o + 1]) + (0.114 * img.Rgba[o + 2]);
                            n++;
                        }
                    }

                    lum[(y * w) + x] = (float)(n > 0 ? sum / n : 0);
                }
            }

            var g = new float[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    float L(int dx, int dy) => lum[((y + dy) * w) + x + dx];
                    float gx = (L(1, -1) + (2 * L(1, 0)) + L(1, 1)) - (L(-1, -1) + (2 * L(-1, 0)) + L(-1, 1));
                    float gy = (L(-1, 1) + (2 * L(0, 1)) + L(1, 1)) - (L(-1, -1) + (2 * L(0, -1)) + L(1, -1));
                    g[(y * w) + x] = MathF.Sqrt((gx * gx) + (gy * gy));
                }
            }

            float[] sorted = g.Where(v => v > 0).OrderBy(v => v).ToArray();
            float p95 = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.95)] : 1;
            for (int i = 0; i < g.Length; i++)
            {
                g[i] = Math.Min(1f, g[i] / Math.Max(1e-3f, p95));
            }

            return new ShotEdges(w, h, g, img.Width, img.Height);
        }

        /// <summary>Bilinear sample at (u, v) in this map's pixels; null outside.</summary>
        public float? At(double u, double v)
        {
            u -= 0.5;
            v -= 0.5;
            if (u < 0 || v < 0 || u > W - 1 || v > H - 1)
            {
                return null;
            }

            int x = Math.Min(W - 2, (int)u), y = Math.Min(H - 2, (int)v);
            double fx = u - x, fy = v - y;
            double a = M[(y * W) + x], b = M[(y * W) + x + 1], c = M[((y + 1) * W) + x], d = M[((y + 1) * W) + x + 1];
            return (float)((a * (1 - fx) * (1 - fy)) + (b * fx * (1 - fy)) + (c * (1 - fx) * fy) + (d * fx * fy));
        }
    }

    /// <summary>3x3 box blur (columns wrap when asked).</summary>
    internal static float[] Blur(float[] m, int w, int h, bool wrap)
    {
        var o = new float[m.Length];
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                float s = 0;
                int n = 0;
                for (int dr = -1; dr <= 1; dr++)
                {
                    int rr = r + dr;
                    if (rr < 0 || rr >= h)
                    {
                        continue;
                    }

                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int cc = c + dc;
                        if (cc < 0 || cc >= w)
                        {
                            if (!wrap)
                            {
                                continue;
                            }

                            cc = (cc + w) % w;
                        }

                        s += m[(rr * w) + cc];
                        n++;
                    }
                }

                o[(r * w) + c] = s / n;
            }
        }

        return o;
    }

    /// <summary>Normalised cross-correlation of two maps over the cells where both are defined (NaN = undefined).</summary>
    internal static double Ncc(float[] a, float[] b)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float x = a[i], y = b[i];
            if (float.IsNaN(x) || float.IsNaN(y))
            {
                continue;
            }

            sa += x;
            sb += y;
            saa += x * x;
            sbb += y * y;
            sab += x * y;
            n++;
        }

        if (n < 16)
        {
            return 0;
        }

        double cov = sab - (sa * sb / n), va = saa - (sa * sa / n), vb = sbb - (sb * sb / n);
        return va <= 1e-9 || vb <= 1e-9 ? 0 : cov / Math.Sqrt(va * vb);
    }

    // ---- one pose --------------------------------------------------------------------------------------------

    /// <summary>The edge score of a pose against a screenshot at working width w (default 160).</summary>
    internal double Score(ShotPose pose, ShotEdges shot, out float[] renderEdges, out View view)
    {
        view = Perspective(pose, shot.W, shot.H);
        renderEdges = view.Edges(wrap: false);
        return Ncc(Blur(renderEdges, shot.W, shot.H, false), Blur(shot.M, shot.W, shot.H, false));
    }

    /// <summary>The ray through picture pixel (u, v) of a w x h shot and what it hits.</summary>
    public (Vec3d Dir, (double T, Vec3d Normal)? Hit) Measure(ShotPose pose, double u, double v, int w, int h)
    {
        Vec3d d = pose.Ray(u, v, w, h);
        return (d, Cast(new Vec3d(pose.X, pose.Y, pose.Z), d));
    }

    // ---- search ----------------------------------------------------------------------------------------------

    /// <summary>One located pose and its score.</summary>
    public readonly record struct Candidate(ShotPose Pose, double Score, double Floor);

    /// <summary>The search space: an x/z box (null = the whole floor), position step, eye height and pitch range.</summary>
    public sealed record SearchOptions(
        (double X0, double Z0, double X1, double Z1)? Box,
        double Step,
        double Eye,
        double Hfov,
        double PitchRange,
        int Keep,
        double? YawHint,
        double YawRange,
        double? FloorY);

    /// <summary>Statistics of a search.</summary>
    public sealed record SearchStats(int Positions, int Refined, double Seconds, string Note);

    /// <summary>
    /// Finds the poses whose ray-cast edges best match the screenshot. Coarse: every floor position of the box at
    /// <c>Step</c> (eye above the floor), a 3-degree panorama matched at every yaw and three pitches. Fine: around the
    /// best coarse poses, a third-step position grid, a 1.5-degree view and finer yaw and pitch.
    /// </summary>
    public (List<Candidate> Best, SearchStats Stats) Locate(RefImage shotImg, SearchOptions o, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const double CoarseStep = 2;
        const double Top = 51;

        // walkable positions
        _s.MeasureHeadroom(o.Step, 400_000, Math.Max(1, Environment.ProcessorCount / 2));
        var pos = new List<Vec3d>();
        foreach (SpaceSampler.Sample s in _s.Samples)
        {
            if (!s.Valid || (s.Headroom < o.Eye + 0.3 && !s.OpenSky))
            {
                continue;
            }

            if (o.Box is { } b && (s.P.X < b.X0 || s.P.X > b.X1 || s.P.Z < b.Z0 || s.P.Z > b.Z1))
            {
                continue;
            }

            if (o.FloorY is double fy && Math.Abs(s.P.Y - fy) > 0.75)
            {
                continue;
            }

            pos.Add(s.P);
        }

        string note = string.Empty;
        const int MaxCoarse = 1500;
        if (pos.Count > MaxCoarse)
        {
            // thin evenly: one position per (coarser) cell and floor height
            int all = pos.Count;
            double g = o.Step * Math.Sqrt((double)all / MaxCoarse);
            pos = pos.GroupBy(p => (Math.Floor(p.X / g), Math.Floor(p.Z / g), Math.Round(p.Y / 2))).Select(k => k.First()).ToList();
            note = $"{all} floor positions at {o.Step:0.##} m: searched {pos.Count} at about {g:0.##} m (narrow search.box for a finer search).";
        }

        // the shot's edges, warped into panorama cells for each coarse pitch
        int coarseW = (int)Math.Round(o.Hfov / CoarseStep * 2);
        ShotEdges coarseShot = ShotEdges.Of(shotImg, Math.Max(24, coarseW));
        double[] pitches = o.PitchRange <= 0 ? new[] { 0.0 } : new[] { -o.PitchRange * 0.6, 0, o.PitchRange * 0.6 };
        var warps = pitches.Select(p => Warp(coarseShot, o.Hfov, p, CoarseStep, Top)).ToArray();
        int cols = (int)Math.Round(360 / CoarseStep);

        var coarse = new Candidate[pos.Count];
        Parallel.For(0, pos.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            Vec3d eye = new(pos[i].X, pos[i].Y + o.Eye, pos[i].Z);
            View pano = Panorama(eye, CoarseStep, Top);
            float[] e = Blur(pano.Edges(wrap: true), pano.W, pano.H, true);
            Candidate best = new(default, double.NegativeInfinity, pos[i].Y);
            for (int k = 0; k < warps.Length; k++)
            {
                Warped wp = warps[k];
                for (int shift = 0; shift < cols; shift++)
                {
                    double yaw = shift * CoarseStep;
                    if (o.YawHint is double hint && Math.Abs(Wrap180(yaw - hint)) > o.YawRange)
                    {
                        continue;
                    }

                    double sc = wp.Score(e, cols, shift);
                    if (sc > best.Score)
                    {
                        best = new Candidate(new ShotPose(eye.X, eye.Y, eye.Z, yaw, pitches[k], o.Hfov), sc, pos[i].Y);
                    }
                }
            }

            coarse[i] = best;
        });

        // the best distinct coarse poses
        var seeds = new List<Candidate>();
        foreach (Candidate c in coarse.Where(c => !double.IsNegativeInfinity(c.Score)).OrderByDescending(c => c.Score))
        {
            if (seeds.All(s => Dist2(s.Pose, c.Pose) > o.Step * o.Step * 2.1 || Math.Abs(Wrap180(s.Pose.Yaw - c.Pose.Yaw)) > 30))
            {
                seeds.Add(c);
            }

            if (seeds.Count >= 8)
            {
                break;
            }
        }

        // refine: around each seed, a 3 x 3 position grid (halving) with a 1.5-degree panorama per position, every
        // yaw within the window and pitches every 2.5 degrees; the result is scored on the perspective view
        const double FineStep = 1.5;
        int fineCols = (int)Math.Round(360 / FineStep);
        ShotEdges fineShotP = ShotEdges.Of(shotImg, Math.Max(32, (int)Math.Round(o.Hfov / FineStep * 2)));
        var finePitches = new List<double>();
        for (double p = -o.PitchRange; p <= o.PitchRange + 1e-9; p += 2.5)
        {
            finePitches.Add(p);
        }

        if (finePitches.Count == 0)
        {
            finePitches.Add(0);
        }

        Warped[] fineWarps = finePitches.Select(p => Warp(fineShotP, o.Hfov, p, FineStep, Top)).ToArray();
        ShotEdges fineShot = ShotEdges.Of(shotImg, 96);
        var refined = new List<Candidate>();
        int refinedCount = 0;
        var cur = seeds.Select(s => s with { Score = double.NegativeInfinity }).ToArray();
        double dStep = o.Step / 2, yawWin = 15;
        for (int round = 0; round < 2; round++)
        {
            ct.ThrowIfCancellationRequested();
            var trials = new List<(int Seed, Vec3d Eye, double Floor)>();
            for (int si = 0; si < cur.Length; si++)
            {
                ShotPose c = cur[si].Pose.Hfov > 0 ? cur[si].Pose : seeds[si].Pose;
                double cf = cur[si].Pose.Hfov > 0 ? cur[si].Floor : seeds[si].Floor;
                for (int a = -1; a <= 1; a++)
                {
                    for (int b = -1; b <= 1; b++)
                    {
                        double x = c.X + (a * dStep), z = c.Z + (b * dStep);
                        double? f = FloorBelow(x, cf + 1.0, z);
                        if (f is double ff && Math.Abs(ff - cf) < 1.5)
                        {
                            trials.Add((si, new Vec3d(x, ff + o.Eye, z), ff));
                        }
                    }
                }
            }

            var scored = new Candidate[trials.Count];
            Parallel.For(0, trials.Count, new ParallelOptions { CancellationToken = ct }, t =>
            {
                (int si, Vec3d eye, double floor) = trials[t];
                View pano = Panorama(eye, FineStep, Top);
                float[] e = Blur(pano.Edges(wrap: true), pano.W, pano.H, true);
                double centre = (cur[si].Pose.Hfov > 0 ? cur[si].Pose : seeds[si].Pose).Yaw;
                Candidate best = new(default, double.NegativeInfinity, floor);
                int half = (int)Math.Ceiling(yawWin / FineStep);
                int c0 = (int)Math.Round(centre / FineStep);
                for (int k = 0; k < fineWarps.Length; k++)
                {
                    for (int s = c0 - half; s <= c0 + half; s++)
                    {
                        int shift = ((s % fineCols) + fineCols) % fineCols;
                        double sc = fineWarps[k].Score(e, fineCols, shift);
                        if (sc > best.Score)
                        {
                            best = new Candidate(new ShotPose(eye.X, eye.Y, eye.Z, shift * FineStep, finePitches[k], o.Hfov), sc, floor);
                        }
                    }
                }

                scored[t] = best;
            });
            refinedCount += trials.Count;
            for (int t = 0; t < trials.Count; t++)
            {
                int si = trials[t].Seed;
                if (scored[t].Score > cur[si].Score)
                {
                    cur[si] = scored[t];
                }
            }

            dStep /= 2;
            yawWin = 6;
        }

        for (int si = 0; si < cur.Length; si++)
        {
            Candidate c = double.IsNegativeInfinity(cur[si].Score) ? seeds[si] : cur[si];
            double s = Score(c.Pose, fineShot, out _, out _);
            refined.Add(c with { Score = s });
        }

        var result = new List<Candidate>();
        foreach (Candidate c in refined.OrderByDescending(c => c.Score))
        {
            if (result.Count < Math.Max(1, o.Keep) && result.All(r => Dist2(r.Pose, c.Pose) > 0.25 || Math.Abs(Wrap180(r.Pose.Yaw - c.Pose.Yaw)) > 8))
            {
                result.Add(c);
            }
        }

        return (result, new SearchStats(pos.Count, refinedCount, clock.Elapsed.TotalSeconds, note));
    }

    private static double Dist2(ShotPose a, ShotPose b) => ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));

    private static double Wrap180(double a)
    {
        a %= 360;
        return a > 180 ? a - 360 : a < -180 ? a + 360 : a;
    }

    private static double Norm360(double a) => ((a % 360) + 360) % 360;

    /// <summary>The shot's edges resampled into panorama cells (relative columns) for one pitch.</summary>
    private sealed class Warped
    {
        public int[] Row = Array.Empty<int>();
        public int[] Col = Array.Empty<int>();
        public float[] Val = Array.Empty<float>();
        public double Mean;
        public double Norm;

        public double Score(float[] pano, int cols, int shift)
        {
            double se = 0, see = 0, sxe = 0;
            int n = Val.Length;
            for (int i = 0; i < n; i++)
            {
                int c = Col[i] + shift;
                c = ((c % cols) + cols) % cols;
                float e = pano[(Row[i] * cols) + c];
                se += e;
                see += e * e;
                sxe += Val[i] * e;
            }

            double ve = see - (se * se / n);
            return ve <= 1e-9 || Norm <= 1e-9 ? 0 : sxe / Math.Sqrt(ve * Norm);
        }
    }

    private static Warped Warp(ShotEdges shot, double hfov, double pitch, double step, double top)
    {
        int rows = (int)Math.Round(2 * top / step);
        int half = (int)Math.Ceiling((hfov / 2 / step) + 1);
        var pose = new ShotPose(0, 0, 0, 0, pitch, hfov);
        var rr = new List<int>();
        var cc = new List<int>();
        var vv = new List<float>();
        var map = new float[shot.W * shot.H];
        Array.Copy(Blur(shot.M, shot.W, shot.H, false), map, map.Length);
        for (int r = 0; r < rows; r++)
        {
            double el = top - ((r + 0.5) * step);
            for (int c = -half; c <= half; c++)
            {
                if (!pose.Project(ShotPose.Dir(c * step, el), shot.W, shot.H, out double u, out double v))
                {
                    continue;
                }

                if (u < 0.5 || v < 0.5 || u > shot.W - 0.5 || v > shot.H - 0.5)
                {
                    continue;
                }

                int x = Math.Clamp((int)u, 0, shot.W - 1), y = Math.Clamp((int)v, 0, shot.H - 1);
                rr.Add(r);
                cc.Add(c);
                vv.Add(map[(y * shot.W) + x]);
            }
        }

        var w = new Warped { Row = rr.ToArray(), Col = cc.ToArray(), Val = vv.ToArray() };
        double mean = w.Val.Length > 0 ? w.Val.Average(v => (double)v) : 0;
        double norm = 0;
        for (int i = 0; i < w.Val.Length; i++)
        {
            w.Val[i] = (float)(w.Val[i] - mean);
            norm += w.Val[i] * w.Val[i];
        }

        w.Mean = mean;
        w.Norm = norm;
        return w;
    }

    // ---- pictures --------------------------------------------------------------------------------------------

    /// <summary>A grey ray-cast shading of a view (floors warm, walls grey, ceilings dark; edges black).</summary>
    internal static byte[] Shade(View v, float[]? edges)
    {
        var px = new byte[v.W * v.H * 4];
        for (int i = 0; i < v.W * v.H; i++)
        {
            float d = v.Depth[i];
            double r, g, b;
            if (float.IsPositiveInfinity(d))
            {
                (r, g, b) = (70, 90, 120);
            }
            else
            {
                double ny = v.Ny[i];
                double lam = 0.45 + (0.55 * Math.Abs((0.4 * v.Nx[i]) + (0.8 * ny) + (0.45 * v.Nz[i])));
                double fog = Math.Exp(-d / 60.0);
                (double br, double bg, double bb) = ny > 0.7 ? (190.0, 170, 140) : ny < -0.7 ? (110.0, 110, 120) : (165.0, 165, 170);
                r = (br * lam * fog) + (40 * (1 - fog));
                g = (bg * lam * fog) + (40 * (1 - fog));
                b = (bb * lam * fog) + (46 * (1 - fog));
            }

            if (edges is not null && edges[i] > 0)
            {
                (r, g, b) = (r * 0.25, g * 0.25, b * 0.25);
            }

            px[(i * 4) + 0] = (byte)Math.Clamp(r, 0, 255);
            px[(i * 4) + 1] = (byte)Math.Clamp(g, 0, 255);
            px[(i * 4) + 2] = (byte)Math.Clamp(b, 0, 255);
            px[(i * 4) + 3] = 255;
        }

        return px;
    }
}
