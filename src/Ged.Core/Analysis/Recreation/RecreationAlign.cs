using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Ged.Core.Analysis.Recreation;

/// <summary>
/// A similarity transform from the candidate's plan frame (a = x, b = −z, metres) into the truth's plan frame (a, b in
/// truth units: metres for a level truth, image pixels for a mockup truth): optional mirror (a → −a), rotation by
/// <see cref="YawDeg"/>, uniform scale, then translation.
/// </summary>
internal readonly record struct PlanPose(double YawDeg, bool Mirror, double Scale, double Ta, double Tb)
{
    public (double A, double B) Forward(double a, double b)
    {
        if (Mirror)
        {
            a = -a;
        }

        double t = YawDeg * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        return ((Scale * ((c * a) - (s * b))) + Ta, (Scale * ((s * a) + (c * b))) + Tb);
    }

    public (double A, double B) Inverse(double a, double b)
    {
        a = (a - Ta) / Scale;
        b = (b - Tb) / Scale;
        double t = YawDeg * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        double ra = (c * a) + (s * b), rb = (-s * a) + (c * b);
        return (Mirror ? -ra : ra, rb);
    }
}

/// <summary>A raster in a plan frame: pixel (u, v) centre = (A0 + (u + ½)·Res, B0 + (v + ½)·Res).</summary>
internal sealed class PlanRaster
{
    public PlanRaster(int w, int h, double a0, double b0, double res)
    {
        W = w;
        H = h;
        A0 = a0;
        B0 = b0;
        Res = res;
        V = new float[w * h];
    }

    public int W { get; }

    public int H { get; }

    public double A0 { get; }

    public double B0 { get; }

    public double Res { get; }

    public float[] V { get; }

    public double Sum()
    {
        double s = 0;
        foreach (float f in V)
        {
            s += f;
        }

        return s;
    }

    /// <summary>The box-filtered raster <paramref name="f"/> times coarser (mean coverage).</summary>
    public PlanRaster Downsample(int f)
    {
        if (f <= 1)
        {
            return this;
        }

        var r = new PlanRaster((W + f - 1) / f, (H + f - 1) / f, A0, B0, Res * f);
        float inv = 1f / (f * f);
        for (int v = 0; v < H; v++)
        {
            for (int u = 0; u < W; u++)
            {
                r.V[((v / f) * r.W) + (u / f)] += V[(v * W) + u] * inv;
            }
        }

        return r;
    }
}

/// <summary>
/// Rigid / similarity alignment of a candidate plan to a truth plan (recreate/SCORING.md §2): brute-force yaw (and
/// scale for a mockup truth) with the best translation for each from an FFT cross-correlation of the coverage
/// rasters, coarse first (≤ 96 px side), refined at ≤ 256 px around the best three coarse poses (yaw, then scale), and the
/// winner's translation found again at the working resolution. The
/// objective is plan IoU = overlap / (truth area + candidate area − overlap).
/// </summary>
internal static class RecreationAlign
{
    /// <summary>The candidate's plan coverage in its own frame, with a pyramid of coarser box-filtered copies.</summary>
    internal sealed class CandidatePlan
    {
        private readonly List<PlanRaster> _levels = new();

        public CandidatePlan(RecreationFloor floor)
        {
            Floor = floor;
            (double minX, double minZ, double maxX, double maxZ) = floor.Bounds;
            // Plan frame: a = x, b = −z; row v = 0 is the largest z.
            var r = new PlanRaster(floor.W, floor.H, minX, -maxZ, floor.Pitch);
            for (int k = 0; k < floor.H; k++)
            {
                for (int i = 0; i < floor.W; i++)
                {
                    double x = (floor.I0 + i + 0.5) * floor.Pitch, z = (floor.K0 + k + 0.5) * floor.Pitch;
                    if (floor.Cell(x, z) >= 0)
                    {
                        r.V[((floor.H - 1 - k) * floor.W) + i] = 1;
                    }
                }
            }

            _levels.Add(r);
            while (_levels[^1].W > 2 || _levels[^1].H > 2)
            {
                _levels.Add(_levels[^1].Downsample(2));
            }

            Corners = new[] { (minX, -maxZ), (maxX, -maxZ), (minX, -minZ), (maxX, -minZ) };
            Area = floor.CellCount * floor.Pitch * floor.Pitch;
        }

        public RecreationFloor Floor { get; }

        public (double A, double B)[] Corners { get; }

        /// <summary>Plan area (m²).</summary>
        public double Area { get; }

        /// <summary>The coarsest pyramid level whose pixel is no larger than <paramref name="maxPixel"/> metres.</summary>
        public PlanRaster Level(double maxPixel)
        {
            PlanRaster best = _levels[0];
            foreach (PlanRaster l in _levels)
            {
                if (l.Res <= maxPixel + 1e-9)
                {
                    best = l;
                }
            }

            return best;
        }

        /// <summary>Coverage at plan point (a, b) (nearest pixel of <paramref name="level"/>).</summary>
        public static float At(PlanRaster level, double a, double b)
        {
            int u = (int)Math.Floor((a - level.A0) / level.Res), v = (int)Math.Floor((b - level.B0) / level.Res);
            return (uint)u < (uint)level.W && (uint)v < (uint)level.H ? level.V[(v * level.W) + u] : 0f;
        }
    }

    /// <summary>The alignment found and its plan IoU at the working resolution.</summary>
    internal readonly record struct AlignResult(PlanPose Pose, double Iou);

    /// <summary>Search settings.</summary>
    internal sealed record Settings
    {
        public bool AllowMirror { get; init; }

        /// <summary>Scales to search (truth units per metre); one entry for a metric truth.</summary>
        public double[] Scales { get; init; } = { 1.0 };

        public double CoarseYawStep { get; init; } = 2;

        /// <summary>Scale refinement factors tried around each coarse winner.</summary>
        public double[] ScaleRefine { get; init; } = { 1.0 };

        public int Parallelism { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

        /// <summary>Stage timings (seconds), filled by <see cref="Align"/>.</summary>
        public Dictionary<string, double> Timings { get; } = new(StringComparer.Ordinal);
    }

    public static AlignResult Align(PlanRaster truth, CandidatePlan cand, Settings settings, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // ---- coarse ----
        int f = Math.Max(1, (int)Math.Ceiling(Math.Max(truth.W, truth.H) / 96.0));
        PlanRaster tc = truth.Downsample(f);
        var coarse = new List<(double Yaw, bool Mirror, double Scale)>();
        foreach (bool m in settings.AllowMirror ? new[] { false, true } : new[] { false })
        {
            foreach (double s in settings.Scales)
            {
                for (double y = 0; y < 360 - 1e-9; y += settings.CoarseYawStep)
                {
                    coarse.Add((y, m, s));
                }
            }
        }

        // One FFT size per scale: small scales need much smaller transforms than the largest.
        AlignResult[] coarseResults = coarse.GroupBy(p => p.Scale)
            .SelectMany(g => Evaluate(tc, cand, g.ToList(), settings.Parallelism, ct)).ToArray();
        settings.Timings["coarse"] = sw.Elapsed.TotalSeconds;
        settings.Timings["coarsePoses"] = coarse.Count;
        sw.Restart();
        var winners = new List<AlignResult>();
        foreach (AlignResult r in coarseResults.OrderByDescending(r => r.Iou))
        {
            bool near = winners.Any(w => w.Pose.Mirror == r.Pose.Mirror
                && Math.Abs(Math.Log(w.Pose.Scale / r.Pose.Scale)) < 0.2
                && AngleGap(w.Pose.YawDeg, r.Pose.YawDeg) < 3 * settings.CoarseYawStep);
            if (!near)
            {
                winners.Add(r);
            }

            if (winners.Count >= 3)
            {
                break;
            }
        }

        // ---- refine at a middle resolution (≤ 256 px side): yaw in 1° steps across the coarse step, then scale ----
        PlanRaster tm = truth.Downsample(Math.Max(1, (int)Math.Ceiling(Math.Max(truth.W, truth.H) / 256.0)));
        int half = (int)Math.Ceiling(settings.CoarseYawStep);
        AlignResult? best = null;
        foreach (AlignResult w in winners)
        {
            var yaws = Enumerable.Range(-half, (2 * half) + 1)
                .Select(d => ((((w.Pose.YawDeg + d) % 360) + 360) % 360, w.Pose.Mirror, w.Pose.Scale)).ToList();
            AlignResult r = Evaluate(tm, cand, yaws, settings.Parallelism, ct).MaxBy(x => x.Iou);
            if (settings.ScaleRefine.Length > 1)
            {
                var scales = settings.ScaleRefine.Select(f => (r.Pose.YawDeg, r.Pose.Mirror, w.Pose.Scale * f)).ToList();
                r = Evaluate(tm, cand, scales, settings.Parallelism, ct).Append(r).MaxBy(x => x.Iou);
            }

            if (best is null || r.Iou > best.Value.Iou)
            {
                best = r;
            }
        }

        if (best is null)
        {
            return new AlignResult(new PlanPose(0, false, settings.Scales[0], 0, 0), 0);
        }

        settings.Timings["refine"] = sw.Elapsed.TotalSeconds;
        sw.Restart();

        // ---- the translation at the working resolution ----
        PlanPose p = best.Value.Pose;
        AlignResult final = Evaluate(truth, cand, new List<(double, bool, double)> { (p.YawDeg, p.Mirror, p.Scale) }, settings.Parallelism, ct)[0];
        settings.Timings["final"] = sw.Elapsed.TotalSeconds;
        return final;
    }

    private static double AngleGap(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    private static AlignResult[] Evaluate(PlanRaster truth, CandidatePlan cand, List<(double Yaw, bool Mirror, double Scale)> poses, int parallelism, CancellationToken ct)
    {
        // The candidate's largest rendered extent fixes one FFT size for every pose.
        double maxScale = poses.Count == 0 ? 1 : poses.Max(p => p.Scale);
        (double a0, double b0) = cand.Corners[0];
        (double a1, double b1) = cand.Corners[3];
        double diag = Math.Sqrt(((a1 - a0) * (a1 - a0)) + ((b1 - b0) * (b1 - b0))) * maxScale / truth.Res;
        int need = (int)Math.Ceiling(Math.Max(truth.W, truth.H) + diag + 2);
        int n = 1;
        while (n < need)
        {
            n <<= 1;
        }

        Complex[] truthSpec = new Complex[n * n];
        for (int v = 0; v < truth.H; v++)
        {
            for (int u = 0; u < truth.W; u++)
            {
                truthSpec[(v * n) + u] = truth.V[(v * truth.W) + u];
            }
        }

        Fft2(truthSpec, n, inverse: false);
        double truthArea = truth.Sum();
        var results = new AlignResult[poses.Count];
        Parallel.For(0, poses.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, () => new Complex[n * n], (i, _, buf) =>
        {
            (double yaw, bool mirror, double scale) = poses[i];
            results[i] = AlignOne(truth, truthSpec, truthArea, n, cand, new PlanPose(yaw, mirror, scale, 0, 0), buf);
            return buf;
        }, _ => { });
        return results;
    }

    /// <summary>Renders the candidate at <paramref name="pose"/> (translation ignored) and finds the best translation.</summary>
    private static AlignResult AlignOne(PlanRaster truth, Complex[] truthSpec, double truthArea, int n, CandidatePlan cand, PlanPose pose, Complex[] buf)
    {
        (float[] img, int w, int h, double oa, double ob) = Render(cand, pose, truth.Res, n);
        Array.Clear(buf);
        double candArea = 0;
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                float c = img[(v * w) + u];
                buf[(v * n) + u] = c;
                candArea += c;
            }
        }

        if (candArea <= 0)
        {
            return new AlignResult(pose, 0);
        }

        Fft2(buf, n, inverse: false);
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] = truthSpec[i] * Complex.Conjugate(buf[i]);
        }

        Fft2(buf, n, inverse: true);
        double best = double.MinValue;
        int bu = 0, bv = 0;
        for (int jv = 0; jv < n; jv++)
        {
            int dv = jv < truth.H ? jv : jv - n;
            if (dv <= -h)
            {
                continue;
            }

            for (int ju = 0; ju < n; ju++)
            {
                int du = ju < truth.W ? ju : ju - n;
                if (du <= -w)
                {
                    continue;
                }

                double r = buf[(jv * n) + ju].Real;
                if (r > best)
                {
                    best = r;
                    bu = du;
                    bv = dv;
                }
            }
        }

        double overlap = Math.Max(0, best / ((double)n * n));
        double iou = overlap / Math.Max(1e-9, truthArea + candArea - overlap);
        // truth point A0 + (p + ½)·res ↔ un-translated candidate point O + (p − d + ½)·res.
        double ta = truth.A0 - oa + (bu * truth.Res), tb = truth.B0 - ob + (bv * truth.Res);
        return new AlignResult(pose with { Ta = ta, Tb = tb }, iou);
    }

    /// <summary>
    /// The candidate's coverage under <paramref name="pose"/> (its translation ignored) on a raster of pixel
    /// <paramref name="res"/>: the image, its size (clamped to <paramref name="maxSide"/>) and its plan origin.
    /// </summary>
    internal static (float[] Img, int W, int H, double Oa, double Ob) Render(CandidatePlan cand, PlanPose pose, double res, int maxSide)
    {
        PlanPose p0 = pose with { Ta = 0, Tb = 0 };
        double minA = double.MaxValue, minB = double.MaxValue, maxA = double.MinValue, maxB = double.MinValue;
        foreach ((double a, double b) in cand.Corners)
        {
            (double ta, double tb) = p0.Forward(a, b);
            minA = Math.Min(minA, ta);
            minB = Math.Min(minB, tb);
            maxA = Math.Max(maxA, ta);
            maxB = Math.Max(maxB, tb);
        }

        int w = Math.Min(maxSide, Math.Max(1, (int)Math.Ceiling((maxA - minA) / res))), h = Math.Min(maxSide, Math.Max(1, (int)Math.Ceiling((maxB - minB) / res)));
        PlanRaster level = cand.Level(res / pose.Scale);
        var img = new float[w * h];
        for (int v = 0; v < h; v++)
        {
            double pb = minB + ((v + 0.5) * res);
            for (int u = 0; u < w; u++)
            {
                (double a, double b) = p0.Inverse(minA + ((u + 0.5) * res), pb);
                img[(v * w) + u] = CandidatePlan.At(level, a, b);
            }
        }

        return (img, w, h, minA, minB);
    }

    // ---- FFT ----

    /// <summary>In-place 2-D FFT of an n×n row-major array (n a power of two); the inverse is unnormalised.</summary>
    internal static void Fft2(Complex[] data, int n, bool inverse)
    {
        var line = new Complex[n];
        for (int r = 0; r < n; r++)
        {
            Array.Copy(data, r * n, line, 0, n);
            Fft(line, inverse);
            Array.Copy(line, 0, data, r * n, n);
        }

        for (int c = 0; c < n; c++)
        {
            for (int r = 0; r < n; r++)
            {
                line[r] = data[(r * n) + c];
            }

            Fft(line, inverse);
            for (int r = 0; r < n; r++)
            {
                data[(r * n) + c] = line[r];
            }
        }
    }

    private static void Fft(Complex[] x, bool inverse)
    {
        int n = x.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (x[i], x[j]) = (x[j], x[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex u = x[i + j], v = x[i + j + (len / 2)] * w;
                    x[i + j] = u + v;
                    x[i + j + (len / 2)] = u - v;
                    w *= wl;
                }
            }
        }
    }
}
