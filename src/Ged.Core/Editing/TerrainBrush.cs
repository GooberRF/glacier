using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>Alpine RED's Terrain Tools modes (editor_patch/terrain_paint_math.h:29-45; panel order resources.rc:1777-1790).</summary>
public enum TerrainTool
{
    PaintLayer = 0,
    EraseToBase = 1,
    SmoothWeights = 2,
    PaintHoles = 3,
    ClearHoles = 4,
    Raise = 5,
    Lower = 6,
    SmoothHeights = 7,
    Flatten = 8,
    SetHeight = 9,
    Noise = 10,
    GeoableChunks = 11,
    Ramp = 12,
    BridgePoints = 13,
}

/// <summary>Brush falloff (terrain_paint_math.h:22-27).</summary>
public enum TerrainFalloff
{
    Smooth = 0,
    Linear = 1,
    Constant = 2,
}

/// <summary>[X0, X1) x [Z0, Z1) of samples; empty when X0 &gt;= X1 or Z0 &gt;= Z1 (terrain_paint_math.h:188-202).</summary>
public readonly record struct TerrainRect(int X0, int Z0, int X1, int Z1)
{
    public bool IsEmpty => X0 >= X1 || Z0 >= Z1;

    public long Area => IsEmpty ? 0 : (long)(X1 - X0) * (Z1 - Z0);

    public bool Has(int i, int j) => i >= X0 && i < X1 && j >= Z0 && j < Z1;

    public TerrainRect Union(TerrainRect b)
    {
        if (IsEmpty)
        {
            return b;
        }

        return b.IsEmpty ? this : new TerrainRect(Math.Min(X0, b.X0), Math.Min(Z0, b.Z0), Math.Max(X1, b.X1), Math.Max(Z1, b.Z1));
    }
}

/// <summary>A dab in cell units: centre (cells from the grid origin), radius, strength 0..1 and falloff.</summary>
public struct TerrainDab
{
    public float Cx;
    public float Cz;
    public float Radius;
    public float Strength;
    public TerrainFalloff Falloff;
}

/// <summary>Mirror lines: X and/or Z, each at <c>X2 / 2</c> (<c>Z2 / 2</c>) cells from the grid origin (terrain_paint_math.h:234-238).</summary>
public struct TerrainMirror
{
    public bool X;
    public bool Z;
    public int X2;
    public int Z2;

    public readonly bool On => X || Z;
}

/// <summary>
/// The Terrain Tools brush arithmetic: dabs on the weight maps, overlay and decoration coverage, hole mask and heights,
/// mirror images, the height mapping growth of sculpting, the Geoable Chunks segment test. Behaviour follows Alpine RED's
/// <c>terrain_paint_math.h</c> (editor_patch/terrain_paint_math.h:66-1116) so the same strokes give the same bytes; the
/// code is Glacier's own.
/// </summary>
public static class TerrainBrush
{
    /// <summary>Images of a mirrored dab: image k flips x on bit 0 and z on bit 1 (:232).</summary>
    public const int MaxImages = 4;

    /// <summary>Below this a dab leaves a texel alone, so a soft brush's rim does not creep (:467).</summary>
    public const float MinAmount = 0.5f / 255.0f;

    /// <summary>Raise / Lower per dab at the centre, per unit of brush radius at full strength (:658).</summary>
    public const float RaiseRatePerRadius = 0.05f;

    /// <summary>Noise peak per unit of brush radius at full strength (:660).</summary>
    public const float NoiseAmplitudePerRadius = 0.25f;

    public const float MaxRampAngle = 80.0f;

    /// <summary>A shorter Bridge Points drag does nothing (:680).</summary>
    public const float MinSegmentCells = 0.25f;

    /// <summary>Brush radius limits in world units (terrain_paint.cpp:68-69).</summary>
    public const float MinRadius = 0.1f;

    public const float MaxRadius = 4096.0f;

    public static bool EditsHoles(TerrainTool t) => t is TerrainTool.PaintHoles or TerrainTool.ClearHoles;

    public static bool EditsHeights(TerrainTool t) =>
        (t >= TerrainTool.Raise && t <= TerrainTool.Noise) || t is TerrainTool.Ramp or TerrainTool.BridgePoints;

    public static bool EditsWeights(TerrainTool t) => t is TerrainTool.PaintLayer or TerrainTool.EraseToBase or TerrainTool.SmoothWeights;

    /// <summary>Held still, these keep dabbing at the last spot (:61-64).</summary>
    public static bool RepeatsInPlace(TerrainTool t) => EditsHeights(t) && t != TerrainTool.Noise && t != TerrainTool.BridgePoints;

    /// <summary>Brush weight at t = distance / radius: 1 at the centre, 0 at and past the rim (:177-186).</summary>
    public static float FalloffWeight(TerrainFalloff f, float t)
    {
        if (!(t < 1.0f))
        {
            return 0.0f;
        }

        t = MathF.Max(t, 0.0f);
        return f switch
        {
            TerrainFalloff.Linear => 1.0f - t,
            TerrainFalloff.Constant => 1.0f,
            _ => 0.5f + (0.5f * MathF.Cos(t * 3.14159265f)),
        };
    }

    /// <summary>Dab amount at weight sample (i, j) of a lattice with <paramref name="perCell"/> samples per cell (:214-220).</summary>
    public static float DabAmount(in TerrainDab d, int perCell, float i, float j)
    {
        float x = ((i + 0.5f) / perCell) - d.Cx;
        float z = ((j + 0.5f) / perCell) - d.Cz;
        float t = MathF.Sqrt((x * x) + (z * z)) / MathF.Max(d.Radius, 1e-6f);
        return Math.Clamp(d.Strength, 0.0f, 1.0f) * FalloffWeight(d.Falloff, t);
    }

    public static float RaiseStep(float radiusWorld, float strength) => Math.Clamp(strength, 0.0f, 1.0f) * radiusWorld * RaiseRatePerRadius;

    public static float NoiseAmplitude(float radiusWorld, float strength) => Math.Clamp(strength, 0.0f, 1.0f) * radiusWorld * NoiseAmplitudePerRadius;

    public static float NoiseFeatureCells(float radiusCells) => MathF.Max(radiusCells * 0.5f, 1.0f);

    /// <summary>Height change per cell along x and z of a ramp at <paramref name="angleDeg"/> rising along (dirX, dirZ) (:684-690).</summary>
    public static (float SlopeX, float SlopeZ) RampSlope(float angleDeg, float dirX, float dirZ, float cellSize)
    {
        float a = Math.Clamp(angleDeg, -MaxRampAngle, MaxRampAngle) * (3.14159265f / 180.0f);
        float rise = MathF.Tan(a) * cellSize;
        return (rise * dirX, rise * dirZ);
    }

    /// <summary>A Ramp stroke's direction locks once the cursor is this many cells from its start (:694-697).</summary>
    public static float RampLockCells(float radiusCells) => Math.Clamp(radiusCells * 0.5f, 1.0f, 8.0f);

    /// <summary>Half-width in vertices of Smooth Heights' box average (:712-715).</summary>
    public static int SmoothKernel(float radiusCells) => (int)Math.Clamp(Round(radiusCells / 8.0f), 1L, 3L);

    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    // ---- Geoable Chunks --------------------------------------------------------------------------------------------

    /// <summary>The chunk under (x, z) cells from the grid origin, clamped to the grid (:69-78).</summary>
    public static int ChunkAt(int cellsX, int cellsZ, int edge, float x, float z)
    {
        int across = AlpineTerrainRules.ChunksAlong(cellsX, edge), down = AlpineTerrainRules.ChunksAlong(cellsZ, edge);
        if (across == 0 || down == 0)
        {
            return 0;
        }

        int Axis(float c, int n, int count)
        {
            float f = float.IsFinite(c) ? Math.Clamp(c, 0.0f, n) : 0.0f;
            return Math.Min((int)f / edge, count - 1);
        }

        return (Axis(z, cellsZ, down) * across) + Axis(x, cellsX, across);
    }

    /// <summary>Clips the segment to [0, w] x [0, h] (Liang-Barsky); false when none of it is inside (:81-100).</summary>
    public static bool ClipSegment(ref float x0, ref float z0, ref float x1, ref float z1, float w, float h)
    {
        float dx = x1 - x0, dz = z1 - z0;
        float t0 = 0.0f, t1 = 1.0f;
        bool Edge(float p, float q)
        {
            if (p == 0.0f)
            {
                return q >= 0.0f;
            }

            float t = q / p;
            if (p < 0.0f)
            {
                t0 = MathF.Max(t0, t);
            }
            else
            {
                t1 = MathF.Min(t1, t);
            }

            return t0 <= t1;
        }

        if (!(Edge(-dx, x0) && Edge(dx, w - x0) && Edge(-dz, z0) && Edge(dz, h - z0)))
        {
            return false;
        }

        float ax = x0, az = z0;
        x0 = ax + (t0 * dx);
        z0 = az + (t0 * dz);
        x1 = ax + (t1 * dx);
        z1 = az + (t1 * dz);
        return true;
    }

    /// <summary>Every chunk whose closed cell rect the segment touches (:103-132).</summary>
    public static void ChunksOnSegment(int cellsX, int cellsZ, int edge, float x0, float z0, float x1, float z1, Action<int> visit)
    {
        static float ClampC(float c, int n) => float.IsFinite(c) ? Math.Clamp(c, 0.0f, n) : 0.0f;
        x0 = ClampC(x0, cellsX);
        x1 = ClampC(x1, cellsX);
        z0 = ClampC(z0, cellsZ);
        z1 = ClampC(z1, cellsZ);
        int n = AlpineTerrainRules.ChunkCount(cellsX, cellsZ, edge);
        for (int k = 0; k < n; k++)
        {
            TerrainChunkRect r = AlpineTerrainRules.ChunkRect(cellsX, cellsZ, edge, k);
            float t0 = 0.0f, t1 = 1.0f;
            bool Slab(float a, float b, float lo, float hi)
            {
                float d = b - a;
                if (d == 0.0f)
                {
                    return a >= lo && a <= hi;
                }

                float ta = (lo - a) / d, tb = (hi - a) / d;
                if (ta > tb)
                {
                    (ta, tb) = (tb, ta);
                }

                t0 = MathF.Max(t0, ta);
                t1 = MathF.Min(t1, tb);
                return t0 <= t1;
            }

            if (Slab(x0, x1, r.X0, r.X1) && Slab(z0, z1, r.Z0, r.Z1))
            {
                visit(k);
            }
        }
    }

    // ---- Mirror images (:227-413) ----------------------------------------------------------------------------------

    public static bool ImageOn(in TerrainMirror? m, int k) =>
        k == 0 || (m is { On: true } mm && ((k & 1) == 0 || mm.X) && ((k & 2) == 0 || mm.Z));

    private readonly record struct Lattice(int PerCell, bool Centred);

    private static long LatticeFold(int line2, Lattice l) => ((long)line2 * l.PerCell) - (l.Centred ? 1 : 0);

    private readonly record struct Span(long X0, long Z0, long X1, long Z1)
    {
        public bool Has(long i, long j) => i >= X0 && i < X1 && j >= Z0 && j < Z1;
    }

    private static (long Lo, long Hi) SignedSpan(float a, float b)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b))
        {
            return (0, 0);
        }

        const float lim = 1073741824.0f;
        return ((long)Math.Clamp(MathF.Ceiling(a), -lim, lim), (long)Math.Clamp(MathF.Floor(b) + 1.0f, -lim, lim));
    }

    private static Span DabReach(in TerrainDab d, int perCell)
    {
        float pc = perCell;
        (long x0, long x1) = SignedSpan(((d.Cx - d.Radius) * pc) - 0.5f, ((d.Cx + d.Radius) * pc) - 0.5f);
        (long z0, long z1) = SignedSpan(((d.Cz - d.Radius) * pc) - 0.5f, ((d.Cz + d.Radius) * pc) - 0.5f);
        return new Span(x0, z0, x1, z1);
    }

    private static Span VertexSpan(float x0, float z0, float x1, float z1)
    {
        if (!float.IsFinite(x0) || !float.IsFinite(z0) || !float.IsFinite(x1) || !float.IsFinite(z1))
        {
            return default;
        }

        (long a0, long a1) = SignedSpan(x0, x1);
        (long b0, long b1) = SignedSpan(z0, z1);
        return new Span(a0, b0, a1, b1);
    }

    private sealed class ImageSet
    {
        public TerrainMirror? Mirror;
        public long FoldX;
        public long FoldZ;
        public Span Reach;
        public readonly TerrainRect[] Rects = new TerrainRect[MaxImages];

        public bool Empty => Rects.All(r => r.IsEmpty);
    }

    private static ImageSet MakeImageSet(TerrainMirror? m, Lattice l, Span reach, int w, int h)
    {
        var s = new ImageSet { Mirror = m is { On: true } ? m : null, Reach = reach };
        if (s.Mirror is { } mm)
        {
            s.FoldX = LatticeFold(mm.X2, l);
            s.FoldZ = LatticeFold(mm.Z2, l);
        }

        static int Clip(long v, int n) => (int)Math.Clamp(v, 0, n);
        for (int k = 0; k < MaxImages; k++)
        {
            if (!ImageOn(s.Mirror, k))
            {
                continue;
            }

            long x0 = reach.X0, x1 = reach.X1, z0 = reach.Z0, z1 = reach.Z1;
            if ((k & 1) != 0)
            {
                x0 = s.FoldX - reach.X1 + 1;
                x1 = s.FoldX - reach.X0 + 1;
            }

            if ((k & 2) != 0)
            {
                z0 = s.FoldZ - reach.Z1 + 1;
                z1 = s.FoldZ - reach.Z0 + 1;
            }

            s.Rects[k] = new TerrainRect(Clip(x0, w), Clip(z0, h), Clip(x1, w), Clip(z1, h));
        }

        return s;
    }

    private struct Fold
    {
        public long I;
        public long J;
        public bool Reached;
    }

    private struct Contrib
    {
        public float W;
        public double Target;
        public long I;
        public long J;
        public int Image;
    }

    private static bool ContribBefore(in Contrib a, in Contrib b)
    {
        if (a.W != b.W)
        {
            return a.W > b.W;
        }

        if (a.I != b.I)
        {
            return a.I < b.I;
        }

        return a.J != b.J ? a.J < b.J : a.Target < b.Target;
    }

    private static void OrderContribs(Contrib[] c, int n)
    {
        for (int a = 1; a < n; a++)
        {
            for (int b = a; b > 0 && ContribBefore(c[b], c[b - 1]); b--)
            {
                (c[b], c[b - 1]) = (c[b - 1], c[b]);
            }
        }
    }

    private delegate float Weigh(in Fold f, int image, ref double target);

    private static int GatherContribs(ImageSet s, Fold[] f, Contrib[] outc, Weigh weigh)
    {
        int n = 0;
        for (int e = 0; e < MaxImages; e++)
        {
            if (!ImageOn(s.Mirror, e) || !f[e].Reached)
            {
                continue;
            }

            var c = new Contrib { I = f[e].I, J = f[e].J, Image = e };
            c.W = weigh(f[e], e, ref c.Target);
            outc[n++] = c;
        }

        OrderContribs(outc, n);
        return n;
    }

    private delegate bool Visit(int i, int j, int k, Fold[] f);

    /// <summary>Visits once every sample some image reaches, k being the first image whose rect holds it (:346-371).</summary>
    private static TerrainRect VisitImages(ImageSet s, TerrainRect[]? perImage, Visit visit)
    {
        TerrainRect changed = default;
        var f = new Fold[MaxImages];
        for (int k = 0; k < MaxImages; k++)
        {
            TerrainRect r = s.Rects[k];
            for (int j = r.Z0; j < r.Z1; j++)
            {
                for (int i = r.X0; i < r.X1; i++)
                {
                    bool seen = false;
                    for (int e = 0; e < k && !seen; e++)
                    {
                        seen = s.Rects[e].Has(i, j);
                    }

                    if (seen)
                    {
                        continue;
                    }

                    for (int e = 0; e < MaxImages; e++)
                    {
                        if (!ImageOn(s.Mirror, e))
                        {
                            f[e] = default;
                            continue;
                        }

                        f[e].I = (e & 1) != 0 ? s.FoldX - i : i;
                        f[e].J = (e & 2) != 0 ? s.FoldZ - j : j;
                        f[e].Reached = s.Reach.Has(f[e].I, f[e].J);
                    }

                    if (!visit(i, j, k, f))
                    {
                        continue;
                    }

                    var one = new TerrainRect(i, j, i + 1, j + 1);
                    changed = changed.Union(one);
                    if (perImage is not null)
                    {
                        perImage[k] = perImage[k].Union(one);
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>Per image, the samples a dab reached padded by <paramref name="pad"/>, as they were before it (:416-443).</summary>
    private sealed class Snapshots<T>
    {
        public readonly TerrainRect[] Box = new TerrainRect[MaxImages];
        public readonly T[][] Data = new T[MaxImages][];

        public void Take(ImageSet s, int pad, int w, int h, int stride, Action<int, int, T[], int> read)
        {
            for (int k = 0; k < MaxImages; k++)
            {
                TerrainRect r = s.Rects[k];
                Box[k] = default;
                if (r.IsEmpty)
                {
                    continue;
                }

                Box[k] = new TerrainRect(Math.Max(r.X0 - pad, 0), Math.Max(r.Z0 - pad, 0), Math.Min(r.X1 + pad, w), Math.Min(r.Z1 + pad, h));
                Data[k] = new T[Box[k].Area * stride];
                for (int j = Box[k].Z0; j < Box[k].Z1; j++)
                {
                    for (int i = Box[k].X0; i < Box[k].X1; i++)
                    {
                        read(i, j, Data[k], At(k, i, j, stride));
                    }
                }
            }
        }

        public int At(int k, int i, int j, int stride)
        {
            TerrainRect b = Box[k];
            return (((j - b.Z0) * (b.X1 - b.X0)) + (i - b.X0)) * stride;
        }
    }

    // ---- Weight maps (:445-553) ------------------------------------------------------------------------------------

    private static void GetWeights(byte[] weights, int map, int texel, byte[] v)
    {
        for (int c = 0; c < 4; c++)
        {
            v[c] = weights[(texel * 4) + c];
            v[4 + c] = weights[map + (texel * 4) + c];
        }
    }

    private static void SetWeights(byte[] weights, int map, int texel, byte[] v)
    {
        for (int c = 0; c < 4; c++)
        {
            weights[(texel * 4) + c] = v[c];
            weights[map + (texel * 4) + c] = v[4 + c];
        }
    }

    private static bool Same(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Moves each weight texel toward pure <paramref name="layer"/> by the dab amount: every other channel is scaled down
    /// and rounded down, the layer takes the rest (:472-505). Returns the texels changed.
    /// </summary>
    public static TerrainRect PaintLayer(AlpineTerrainGrid g, int layerCount, in TerrainDab d, int layer, TerrainMirror? mirror = null, TerrainRect[]? perImage = null)
    {
        if (layer < 0 || layer >= AlpineTerrainRules.MaxLayers || layer >= layerCount)
        {
            return default;
        }

        int mul = g.WeightResolution;
        int w = AlpineTerrainRules.WeightWidth(g.Nx, mul), h = AlpineTerrainRules.WeightHeight(g.Nz, mul);
        int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, mul);
        TerrainDab dab = d;
        ImageSet s = MakeImageSet(mirror, new Lattice(mul, true), DabReach(dab, mul), w, h);
        var c = new Contrib[MaxImages];
        var v = new byte[8];
        var cur = new byte[8];
        var outw = new byte[8];
        byte[] weights = g.Weights;
        return VisitImages(s, perImage, (i, j, _, f) =>
        {
            int n = GatherContribs(s, f, c, (in Fold p, int _, ref double _) => DabAmount(dab, mul, p.I, p.J));
            int t = (j * w) + i;
            GetWeights(weights, map, t, v);
            Array.Copy(v, cur, 8);
            for (int q = 0; q < n; q++)
            {
                float a = c[q].W;
                if (a < MinAmount)
                {
                    continue;
                }

                uint rest = 0;
                for (int ch = 0; ch < 8; ch++)
                {
                    if (ch == layer)
                    {
                        continue;
                    }

                    outw[ch] = (byte)MathF.Floor(cur[ch] * (1.0f - a));
                    rest += outw[ch];
                }

                outw[layer] = (byte)(255u - Math.Min(rest, 255u));
                TerrainOps.NormalizeWeights(outw);
                Array.Copy(outw, cur, 8);
            }

            if (Same(cur, v))
            {
                return false;
            }

            SetWeights(weights, map, t, cur);
            return true;
        });
    }

    /// <summary>Blends each weight texel toward the 3 x 3 average around it as it was before the dab (:509-553).</summary>
    public static TerrainRect SmoothWeights(AlpineTerrainGrid g, in TerrainDab d, TerrainMirror? mirror = null, TerrainRect[]? perImage = null)
    {
        int mul = g.WeightResolution;
        int w = AlpineTerrainRules.WeightWidth(g.Nx, mul), h = AlpineTerrainRules.WeightHeight(g.Nz, mul);
        int map = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, mul);
        TerrainDab dab = d;
        ImageSet s = MakeImageSet(mirror, new Lattice(mul, true), DabReach(dab, mul), w, h);
        if (s.Empty)
        {
            return default;
        }

        byte[] weights = g.Weights;
        var src = new Snapshots<byte>();
        var tmp = new byte[8];
        src.Take(s, 1, w, h, 8, (i, j, data, at) =>
        {
            GetWeights(weights, map, (j * w) + i, tmp);
            Array.Copy(tmp, 0, data, at, 8);
        });
        var c = new Contrib[MaxImages];
        var avg = new float[8];
        var cur = new byte[8];
        var was = new byte[8];
        var outw = new byte[8];
        return VisitImages(s, perImage, (i, j, k, f) =>
        {
            int n = GatherContribs(s, f, c, (in Fold p, int _, ref double _) => DabAmount(dab, mul, p.I, p.J));
            TerrainRect b = src.Box[k];
            Array.Clear(avg);
            int count = 0;
            for (int z = j > b.Z0 ? j - 1 : j; z <= Math.Min(j + 1, b.Z1 - 1); z++)
            {
                for (int x = i > b.X0 ? i - 1 : i; x <= Math.Min(i + 1, b.X1 - 1); x++)
                {
                    int o = src.At(k, x, z, 8);
                    for (int ch = 0; ch < 8; ch++)
                    {
                        avg[ch] += src.Data[k][o + ch];
                    }

                    count++;
                }
            }

            Array.Copy(src.Data[k], src.At(k, i, j, 8), was, 0, 8);
            Array.Copy(was, cur, 8);
            for (int q = 0; q < n; q++)
            {
                float a = c[q].W;
                if (a < MinAmount)
                {
                    continue;
                }

                for (int ch = 0; ch < 8; ch++)
                {
                    float target = avg[ch] / count;
                    float val = cur[ch] + ((target - cur[ch]) * a);
                    outw[ch] = (byte)Math.Clamp(Round(val), 0L, 255L);
                }

                TerrainOps.NormalizeWeights(outw);
                Array.Copy(outw, cur, 8);
            }

            if (Same(cur, was))
            {
                return false;
            }

            SetWeights(weights, map, (j * w) + i, cur);
            return true;
        });
    }

    /// <summary>
    /// Moves one coverage channel (an overlay's in the RGBA coverage map, stride 4, or a decoration plane, stride 1) toward
    /// 255 or 0 by the dab amount, rounding in favour of the move (:569-592).
    /// </summary>
    public static TerrainRect PaintCoverage(byte[] data, int offset, int stride, int channel, AlpineTerrainGrid g, in TerrainDab d, bool raise, TerrainMirror? mirror = null, TerrainRect[]? perImage = null)
    {
        if (channel >= stride)
        {
            return default;
        }

        int mul = g.WeightResolution;
        int w = AlpineTerrainRules.WeightWidth(g.Nx, mul), h = AlpineTerrainRules.WeightHeight(g.Nz, mul);
        TerrainDab dab = d;
        ImageSet s = MakeImageSet(mirror, new Lattice(mul, true), DabReach(dab, mul), w, h);
        var c = new Contrib[MaxImages];
        return VisitImages(s, perImage, (i, j, _, f) =>
        {
            int n = GatherContribs(s, f, c, (in Fold p, int _, ref double _) => DabAmount(dab, mul, p.I, p.J));
            int at = offset + ((((j * w) + i) * stride) + channel);
            byte v = data[at];
            byte cur = v;
            for (int q = 0; q < n; q++)
            {
                float a = c[q].W;
                if (a < MinAmount)
                {
                    continue;
                }

                float rest = raise ? 255.0f - cur : cur;
                byte left = (byte)MathF.Floor(rest * (1.0f - a));
                cur = raise ? (byte)(255 - left) : left;
            }

            if (cur == v)
            {
                return false;
            }

            data[at] = cur;
            return true;
        });
    }

    /// <summary>Blends one coverage channel toward the 3 x 3 average around it as it was before the dab (:596-632).</summary>
    public static TerrainRect SmoothCoverage(byte[] data, int offset, int stride, int channel, AlpineTerrainGrid g, in TerrainDab d, TerrainMirror? mirror = null, TerrainRect[]? perImage = null)
    {
        if (channel >= stride)
        {
            return default;
        }

        int mul = g.WeightResolution;
        int w = AlpineTerrainRules.WeightWidth(g.Nx, mul), h = AlpineTerrainRules.WeightHeight(g.Nz, mul);
        TerrainDab dab = d;
        ImageSet s = MakeImageSet(mirror, new Lattice(mul, true), DabReach(dab, mul), w, h);
        if (s.Empty)
        {
            return default;
        }

        int At(int i, int j) => offset + ((((j * w) + i) * stride) + channel);
        var src = new Snapshots<byte>();
        src.Take(s, 1, w, h, 1, (i, j, buf, o) => buf[o] = data[At(i, j)]);
        var c = new Contrib[MaxImages];
        return VisitImages(s, perImage, (i, j, k, f) =>
        {
            int n = GatherContribs(s, f, c, (in Fold p, int _, ref double _) => DabAmount(dab, mul, p.I, p.J));
            TerrainRect b = src.Box[k];
            float sum = 0.0f;
            int count = 0;
            for (int z = j > b.Z0 ? j - 1 : j; z <= Math.Min(j + 1, b.Z1 - 1); z++)
            {
                for (int x = i > b.X0 ? i - 1 : i; x <= Math.Min(i + 1, b.X1 - 1); x++)
                {
                    sum += src.Data[k][src.At(k, x, z, 1)];
                    count++;
                }
            }

            byte outv = src.Data[k][src.At(k, i, j, 1)];
            for (int q = 0; q < n; q++)
            {
                float a = c[q].W;
                if (a < MinAmount)
                {
                    continue;
                }

                float cur = outv;
                float v = cur + (((sum / count) - cur) * a);
                outv = (byte)Math.Clamp(Round(v), 0L, 255L);
            }

            if (outv == data[At(i, j)])
            {
                return false;
            }

            data[At(i, j)] = outv;
            return true;
        });
    }

    /// <summary>Sets or clears the hole bit of every cell whose centre is inside the dab (strength and falloff do not apply; :636-651).</summary>
    public static TerrainRect PaintHoles(byte[] holes, int cellsX, int cellsZ, in TerrainDab d, bool hole, TerrainMirror? mirror = null, TerrainRect[]? perImage = null)
    {
        TerrainDab dab = d;
        ImageSet s = MakeImageSet(mirror, new Lattice(1, true), DabReach(dab, 1), cellsX, cellsZ);
        return VisitImages(s, perImage, (x, z, _, f) =>
        {
            bool inside = false;
            for (int e = 0; e < MaxImages && !inside; e++)
            {
                if (!ImageOn(s.Mirror, e) || !f[e].Reached)
                {
                    continue;
                }

                float dx = f[e].I + 0.5f - dab.Cx, dz = f[e].J + 0.5f - dab.Cz;
                inside = (dx * dx) + (dz * dz) < dab.Radius * dab.Radius;
            }

            int bit = (z * cellsX) + x;
            if (!inside || AlpineTerrainRules.GetBit(holes, bit) == hole)
            {
                return false;
            }

            AlpineTerrainRules.SetBit(holes, bit, hole);
            return true;
        });
    }

    // ---- Sculpting (:653-1039) -------------------------------------------------------------------------------------

    /// <summary>Heights and mapping a sculpt dab edits in place.</summary>
    public sealed class HeightGrid
    {
        public required ushort[] Heights { get; init; }

        public required int Nx { get; init; }

        public required int Nz { get; init; }

        public float HeightMin { get; set; }

        public float HeightRange { get; set; }

        public double HeadroomFloor { get; init; } = double.NegativeInfinity;

        public double Offset(int i) => AlpineTerrainRules.HeightOffsetExact(Heights[i], HeightMin, HeightRange);
    }

    public readonly record struct HeightTarget(int Index, double Offset, int Image = 0);

    /// <summary>What a height write changed.</summary>
    public sealed class HeightWrite
    {
        public TerrainRect Verts { get; set; }

        public TerrainRect[] ImageVerts { get; } = new TerrainRect[MaxImages];

        /// <summary>The mapping grew: every height was re-encoded.</summary>
        public bool Requantized { get; set; }

        /// <summary>An offset did not fit the mapping limits.</summary>
        public bool Clamped { get; set; }
    }

    private static double SculptOffset(HeightGrid g, double[]? exact, int i) => exact is not null && !double.IsNaN(exact[i]) ? exact[i] : g.Offset(i);

    private static double HeightStep(float range) => range / 65535.0;

    /// <summary>
    /// Writes offsets to their vertices; an offset outside the mapping by more than half a step grows it (with 25%
    /// headroom) and re-quantizes every height from its exact offset first (:805-842).
    /// </summary>
    public static HeightWrite WriteHeights(HeightGrid g, IReadOnlyList<HeightTarget> targets, double[]? exact = null)
    {
        var outw = new HeightWrite();
        if (targets.Count == 0)
        {
            return outw;
        }

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (HeightTarget t in targets)
        {
            lo = Math.Min(lo, t.Offset);
            hi = Math.Max(hi, t.Offset);
        }

        double half = HeightStep(g.HeightRange) * 0.5;
        if (lo < g.HeightMin - half || hi > (double)g.HeightMin + g.HeightRange + half)
        {
            (float min, float range, bool clamped) = TerrainOps.GrowHeightMapping(g.HeightMin, g.HeightRange, (float)lo, (float)hi, g.HeadroomFloor);
            outw.Clamped = clamped;
            if (min != g.HeightMin || range != g.HeightRange)
            {
                for (int i = 0; i < g.Heights.Length; i++)
                {
                    double off = SculptOffset(g, exact, i);
                    if (exact is not null)
                    {
                        exact[i] = off;
                    }

                    g.Heights[i] = AlpineTerrainRules.EncodeHeight(off, min, range);
                }

                g.HeightMin = min;
                g.HeightRange = range;
                outw.Requantized = true;
                outw.Verts = new TerrainRect(0, 0, g.Nx, g.Nz);
            }
        }

        foreach (HeightTarget t in targets)
        {
            ushort v = AlpineTerrainRules.EncodeHeight(t.Offset, g.HeightMin, g.HeightRange);
            if (g.Heights[t.Index] == v)
            {
                continue;
            }

            g.Heights[t.Index] = v;
            int x = t.Index % g.Nx, z = t.Index / g.Nx;
            var one = new TerrainRect(x, z, x + 1, z + 1);
            outw.Verts = outw.Verts.Union(one);
            int im = t.Image < MaxImages ? t.Image : 0;
            outw.ImageVerts[im] = outw.ImageVerts[im].Union(one);
        }

        return outw;
    }

    /// <summary>One sculpt dab: the tool, the dab and every per-tool input (:844-873).</summary>
    public sealed class SculptDab
    {
        public TerrainTool Tool { get; set; } = TerrainTool.Raise;

        public TerrainDab Dab;

        /// <summary>Raise / Lower: <see cref="RaiseStep"/>; Noise: <see cref="NoiseAmplitude"/>.</summary>
        public float Amount { get; set; }

        /// <summary>Flatten, Set Height, Ramp: the plane through offset Target at (PlaneX, PlaneZ) cells, rising SlopeX / SlopeZ per cell.</summary>
        public float Target { get; set; }

        public float PlaneX { get; set; }

        public float PlaneZ { get; set; }

        public float SlopeX { get; set; }

        public float SlopeZ { get; set; }

        /// <summary>Bridge Points: the segment from the dab centre to (SegX, SegZ) cells past it, Target to TargetEnd along it.</summary>
        public float SegX { get; set; }

        public float SegZ { get; set; }

        public float TargetEnd { get; set; }

        /// <summary>Noise: heights and mapping at the stroke's start, per-vertex coverage (one plane per image when mirrored).</summary>
        public ushort[]? Base { get; set; }

        public float BaseMin { get; set; }

        public float BaseRange { get; set; } = 1.0f;

        public float[]? Coverage { get; set; }

        public uint Seed { get; set; }

        public float FeatureCells { get; set; } = 1.0f;

        /// <summary>Per vertex, the offset the stroke has built up (NaN where it has not touched).</summary>
        public double[]? Exact { get; set; }

        public TerrainMirror? Mirror { get; set; }

        public float[] ImageTarget { get; } = new float[MaxImages];

        public float[] ImageTargetEnd { get; } = new float[MaxImages];
    }

    private static double PlaneTarget(SculptDab s, float target, double i, double j) =>
        target + (s.SlopeX * (i - s.PlaneX)) + (s.SlopeZ * (j - s.PlaneZ));

    private static float SegmentT(float ax, float az, float sx, float sz, float x, float z, out float dist)
    {
        float px = x - ax, pz = z - az;
        float len2 = (sx * sx) + (sz * sz);
        float t = len2 > 0.0f ? Math.Clamp(((px * sx) + (pz * sz)) / len2, 0.0f, 1.0f) : 0.0f;
        float dx = px - (sx * t), dz = pz - (sz * t);
        dist = MathF.Sqrt((dx * dx) + (dz * dz));
        return t;
    }

    private static float SegmentFalloff(SculptDab s, float from, float to, float i, float j, ref double target)
    {
        float t = SegmentT(s.Dab.Cx, s.Dab.Cz, s.SegX, s.SegZ, i, j, out float dist);
        target = from + (((double)to - from) * t);
        return FalloffWeight(s.Dab.Falloff, dist / MathF.Max(s.Dab.Radius, 1e-6f));
    }

    private static float VertexFalloff(in TerrainDab d, float i, float j)
    {
        float x = i - d.Cx, z = j - d.Cz;
        return FalloffWeight(d.Falloff, MathF.Sqrt((x * x) + (z * z)) / MathF.Max(d.Radius, 1e-6f));
    }

    private static Span SculptVertexSpan(SculptDab s)
    {
        TerrainDab d = s.Dab;
        if (s.Tool != TerrainTool.BridgePoints)
        {
            if (!float.IsFinite(d.Cx) || !float.IsFinite(d.Cz) || !float.IsFinite(d.Radius))
            {
                return default;
            }

            return VertexSpan(d.Cx - d.Radius, d.Cz - d.Radius, d.Cx + d.Radius, d.Cz + d.Radius);
        }

        if (MathF.Sqrt((s.SegX * s.SegX) + (s.SegZ * s.SegZ)) < MinSegmentCells || !float.IsFinite(d.Radius))
        {
            return default;
        }

        float bx = d.Cx + s.SegX, bz = d.Cz + s.SegZ, r = d.Radius;
        return VertexSpan(MathF.Min(d.Cx, bx) - r, MathF.Min(d.Cz, bz) - r, MathF.Max(d.Cx, bx) + r, MathF.Max(d.Cz, bz) + r);
    }

    // Lattice value noise (:752-784).
    private static float LatticeNoise(long x, long z, uint seed)
    {
        ulong key = (ulong)(uint)x | ((ulong)(uint)z << 32);
        ulong h = AlpineTerrainRules.SplitMix64(AlpineTerrainRules.SplitMix64(key) ^ seed);
        return ((float)(h >> 40) / (1u << 23)) - 1.0f;
    }

    private static float ValueNoise(float x, float z, uint seed)
    {
        float fx = MathF.Floor(x), fz = MathF.Floor(z);
        long ix = (long)fx, iz = (long)fz;
        static float Fade(float t) => t * t * (3.0f - (2.0f * t));
        float u = Fade(x - fx), v = Fade(z - fz);
        float a = LatticeNoise(ix, iz, seed), b = LatticeNoise(ix + 1, iz, seed);
        float c = LatticeNoise(ix, iz + 1, seed), e = LatticeNoise(ix + 1, iz + 1, seed);
        float top = a + ((b - a) * u), bottom = c + ((e - c) * u);
        return top + ((bottom - top) * v);
    }

    /// <summary>Three octaves of value noise at a vertex, in [-1, 1].</summary>
    public static float SculptNoise(float i, float j, float featureCells, uint seed)
    {
        float x = i / featureCells, z = j / featureCells;
        float sum = 0.0f, amp = 1.0f, freq = 1.0f;
        for (uint o = 0; o < 3; o++)
        {
            sum += amp * ValueNoise(x * freq, z * freq, unchecked(seed + (o * 0x9E3779B9u)));
            amp *= 0.5f;
            freq *= 2.0f;
        }

        return Math.Clamp(sum / 1.75f, -1.0f, 1.0f);
    }

    /// <summary>Applies one sculpt dab to the heights (:916-1039). Returns what it wrote.</summary>
    public static HeightWrite Sculpt(HeightGrid g, SculptDab s)
    {
        var targets = new List<HeightTarget>();
        if (!EditsHeights(s.Tool))
        {
            return new HeightWrite();
        }

        ImageSet set = MakeImageSet(s.Mirror, new Lattice(1, false), SculptVertexSpan(s), g.Nx, g.Nz);
        if (set.Empty)
        {
            return new HeightWrite();
        }

        float strength = Math.Clamp(s.Dab.Strength, 0.0f, 1.0f);
        int count = g.Nx * g.Nz;
        int kernel = 0;
        var src = new Snapshots<double>();
        if (s.Tool == TerrainTool.SmoothHeights)
        {
            kernel = SmoothKernel(s.Dab.Radius);
            src.Take(set, kernel, g.Nx, g.Nz, 1, (i, j, buf, o) => buf[o] = SculptOffset(g, s.Exact, (j * g.Nx) + i));
        }

        double BoxAverage(int image, int i, int j)
        {
            TerrainRect b = src.Box[image];
            int z0 = j > b.Z0 + kernel ? j - kernel : b.Z0, z1 = Math.Min(j + kernel, b.Z1 - 1);
            int x0 = i > b.X0 + kernel ? i - kernel : b.X0, x1 = Math.Min(i + kernel, b.X1 - 1);
            double At(int x, int z) => src.Data[image][src.At(image, x, z, 1)];
            double sum = 0.0;
            if (set.Mirror is null)
            {
                int n0 = 0;
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        sum += At(x, z);
                        n0++;
                    }
                }

                return sum / n0;
            }

            double Row(int z)
            {
                double r = At(i, z);
                for (int d = 1; d <= kernel; d++)
                {
                    r += (i >= x0 + d ? At(i - d, z) : 0.0) + (i + d <= x1 ? At(i + d, z) : 0.0);
                }

                return r;
            }

            sum = Row(j);
            for (int d = 1; d <= kernel; d++)
            {
                sum += (j >= z0 + d ? Row(j - d) : 0.0) + (j + d <= z1 ? Row(j + d) : 0.0);
            }

            return sum / ((x1 - x0 + 1) * (z1 - z0 + 1));
        }

        float ImageTarget(int e) => e == 0 ? s.Target : s.ImageTarget[e];
        float ImageTargetEnd(int e) => e == 0 ? s.TargetEnd : s.ImageTargetEnd[e];

        var c = new Contrib[MaxImages];
        var parts = new Contrib[MaxImages];
        VisitImages(set, null, (i, j, image, f) =>
        {
            int n = GatherContribs(set, f, c, (in Fold p, int e, ref double target) =>
            {
                float fi = p.I, fj = p.J;
                switch (s.Tool)
                {
                    case TerrainTool.BridgePoints:
                        return SegmentFalloff(s, ImageTarget(e), ImageTargetEnd(e), fi, fj, ref target);
                    case TerrainTool.Flatten:
                    case TerrainTool.Ramp:
                        target = PlaneTarget(s, ImageTarget(e), p.I, p.J);
                        break;
                    case TerrainTool.SetHeight:
                        target = PlaneTarget(s, s.Target, p.I, p.J);
                        break;
                }

                return VertexFalloff(s.Dab, fi, fj);
            });
            int idx = (j * g.Nx) + i;
            double cur = SculptOffset(g, s.Exact, idx);
            double next = cur;
            if (s.Tool == TerrainTool.Noise)
            {
                if (s.Base is null || s.Coverage is null)
                {
                    return false;
                }

                bool raised = false;
                for (int q = 0; q < n; q++)
                {
                    int ci = (c[q].Image * count) + idx;
                    if (!(c[q].W > 0.0f) || !(c[q].W > s.Coverage[ci]))
                    {
                        continue;
                    }

                    s.Coverage[ci] = c[q].W;
                    raised = true;
                }

                if (!raised)
                {
                    return false;
                }

                int m = 0;
                for (int e = 0; e < MaxImages; e++)
                {
                    if (!ImageOn(set.Mirror, e) || !(s.Coverage[(e * count) + idx] > 0.0f))
                    {
                        continue;
                    }

                    parts[m++] = new Contrib { W = s.Coverage[(e * count) + idx], I = f[e].I, J = f[e].J, Image = e };
                }

                OrderContribs(parts, m);
                next = AlpineTerrainRules.HeightOffsetExact(s.Base[idx], s.BaseMin, s.BaseRange);
                for (int q = 0; q < m; q++)
                {
                    next += s.Amount * (double)SculptNoise(parts[q].I, parts[q].J, s.FeatureCells, s.Seed) * parts[q].W;
                }
            }
            else
            {
                double avg = s.Tool == TerrainTool.SmoothHeights ? BoxAverage(image, i, j) : 0.0;
                for (int q = 0; q < n; q++)
                {
                    float w = c[q].W;
                    if (!(w > 0.0f))
                    {
                        continue;
                    }

                    next = s.Tool switch
                    {
                        TerrainTool.Raise => next + ((double)s.Amount * w),
                        TerrainTool.Lower => next - ((double)s.Amount * w),
                        TerrainTool.SmoothHeights => next + ((avg - next) * strength * w),
                        _ => next + ((c[q].Target - next) * strength * w),
                    };
                }
            }

            if (!double.IsFinite(next) || next == cur)
            {
                return false;
            }

            targets.Add(new HeightTarget(idx, next, image));
            if (s.Exact is not null)
            {
                s.Exact[idx] = next;
            }

            return true;
        });
        HeightWrite wr = WriteHeights(g, targets, s.Exact);
        if (s.Exact is not null)
        {
            // Past the mapping's limits an offset stops within half a step of them (:1031-1037).
            double half = HeightStep(g.HeightRange) * 0.5;
            double lo = g.HeightMin - half, hi = (double)g.HeightMin + g.HeightRange + half;
            foreach (HeightTarget t in targets)
            {
                s.Exact[t.Index] = Math.Clamp(s.Exact[t.Index], lo, hi);
            }
        }

        return wr;
    }

    /// <summary>
    /// Mirrored sculpting keeps cell splits mirrored: each cell next to <paramref name="verts"/> off the dab's side takes
    /// the diagonal of its mirror, flipped once per line between them (:1044-1084). Returns the cells changed.
    /// </summary>
    public static TerrainRect MirrorDiagonals(byte[] diag, int cellsX, int cellsZ, TerrainMirror m, float cx, float cz, TerrainRect verts)
    {
        if (!m.On || verts.IsEmpty || cellsX == 0 || cellsZ == 0)
        {
            return default;
        }

        int x0 = verts.X0 > 0 ? verts.X0 - 1 : 0, z0 = verts.Z0 > 0 ? verts.Z0 - 1 : 0;
        int x1 = Math.Min(verts.X1, cellsX), z1 = Math.Min(verts.Z1, cellsZ);
        int sideX = 2.0f * cx > m.X2 ? 1 : -1;
        int sideZ = 2.0f * cz > m.Z2 ? 1 : -1;
        static int Side(long cell, int line2)
        {
            long d = (2 * cell) + 1 - line2;
            return d > 0 ? 1 : d < 0 ? -1 : 0;
        }

        TerrainRect changed = default;
        for (int z = z0; z < z1; z++)
        {
            for (int x = x0; x < x1; x++)
            {
                int sx = Side(x, m.X2), sz = Side(z, m.Z2);
                bool onX = m.X && sx != 0, onZ = m.Z && sz != 0;
                bool dx = onX && sx != sideX, dz = onZ && sz != sideZ;
                if (!dx && !dz)
                {
                    continue;
                }

                long ax = dx ? (long)m.X2 - 1 - x : x;
                long az = dz ? (long)m.Z2 - 1 - z : z;
                for (int u = 0; u < MaxImages; u++)
                {
                    if (((u & 1) != 0 && !onX) || ((u & 2) != 0 && !onZ))
                    {
                        continue;
                    }

                    long px = (u & 1) != 0 ? (long)m.X2 - 1 - ax : ax;
                    long pz = (u & 2) != 0 ? (long)m.Z2 - 1 - az : az;
                    if (px < 0 || pz < 0 || px >= cellsX || pz >= cellsZ)
                    {
                        continue;
                    }

                    int flips = (((u & 1) != 0) != dx ? 1 : 0) + (((u & 2) != 0) != dz ? 1 : 0);
                    if (flips == 0)
                    {
                        break;
                    }

                    bool want = AlpineTerrainRules.GetBit(diag, (int)((pz * cellsX) + px)) != (flips == 1);
                    int bit = (z * cellsX) + x;
                    if (AlpineTerrainRules.GetBit(diag, bit) != want)
                    {
                        AlpineTerrainRules.SetBit(diag, bit, want);
                        changed = changed.Union(new TerrainRect(x, z, x + 1, z + 1));
                    }

                    break;
                }
            }
        }

        return changed;
    }

    /// <summary>The surface height at image k's mirror of world (x, z), worked in (x, z)'s frame (:1088-1116).</summary>
    public static float MirroredHeightAt(AlpineTerrain t, TerrainMirror m, int k, float worldX, float worldZ)
    {
        AlpineTerrainGrid g = t.Grid;
        int cx = g.CellsX, cz = g.CellsZ;
        float fx = Math.Clamp((worldX - t.Origin.X) / t.CellSize, 0.0f, cx);
        float fz = Math.Clamp((worldZ - t.Origin.Z) / t.CellSize, 0.0f, cz);
        if (float.IsNaN(fx) || float.IsNaN(fz))
        {
            return float.NaN;
        }

        const float coordUlps = 4.8e-7f;
        float ulps = coordUlps / t.CellSize;
        float tolX = Math.Clamp((MathF.Abs(worldX) + MathF.Abs(t.Origin.X)) * ulps, 1e-4f, 0.1f);
        float tolZ = Math.Clamp((MathF.Abs(worldZ) + MathF.Abs(t.Origin.Z)) * ulps, 1e-4f, 0.1f);
        if (MathF.Abs(fx - MathF.Round(fx)) < tolX)
        {
            fx = MathF.Round(fx);
        }

        if (MathF.Abs(fz - MathF.Round(fz)) < tolZ)
        {
            fz = MathF.Round(fz);
        }

        int x = Math.Min((int)fx, cx - 1);
        int z = Math.Min((int)fz, cz - 1);
        float u = fx - x, v = fz - z;
        static int FoldI(bool flip, long line, long i, int n) => (int)Math.Clamp(flip ? line - i : i, 0, n - 1);
        float Y(int a, int b) => Compiler.TerrainChunkEmitter.TopY(t, FoldI((k & 1) != 0, m.X2, a, g.Nx), FoldI((k & 2) != 0, m.Z2, b, g.Nz));
        float h00 = Y(x, z), h10 = Y(x + 1, z), h11 = Y(x + 1, z + 1), h01 = Y(x, z + 1);
        int dxc = FoldI((k & 1) != 0, m.X2 - 1, x, cx), dzc = FoldI((k & 2) != 0, m.Z2 - 1, z, cz);
        bool diag = AlpineTerrainRules.GetBit(g.Diagonals, (dzc * cx) + dxc) != (k == 1 || k == 2);
        if (!diag)
        {
            return u >= v ? h00 + (u * (h10 - h00)) + (v * (h11 - h10)) : h00 + (v * (h01 - h00)) + (u * (h11 - h01));
        }

        return u + v <= 1.0f ? h00 + (u * (h10 - h00)) + (v * (h01 - h00))
            : h11 + ((1.0f - u) * (h01 - h11)) + ((1.0f - v) * (h10 - h11));
    }
}
