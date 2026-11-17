using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>The noise a <see cref="TerrainGenerator"/> sums (Alpine RED's generator vocabulary, editor_patch/terrain_generate.h:9-15).</summary>
public enum TerrainNoiseType
{
    /// <summary>Smooth hills (fractal Brownian motion).</summary>
    Fbm,

    /// <summary>Ridged multifractal: sharp mountain ridges.</summary>
    Ridged,

    /// <summary>Rounded, puffy hills.</summary>
    Billow,

    /// <summary>Ridged peaks over fBm lowlands.</summary>
    Hybrid,
}

/// <summary>How the terrain's edges are shaped (terrain_generate.h:17-22).</summary>
public enum TerrainEdgeFalloff
{
    None,

    /// <summary>Edges sink to the lowest height.</summary>
    Island,

    /// <summary>Edges rise to the highest height.</summary>
    Basin,
}

/// <summary>
/// Generator settings, with Alpine RED's parameter names and defaults (terrain_generate.h:24-54). Glacier's generator is
/// its own implementation: the same settings make the same heights in Glacier every time, not the same as Alpine RED's.
/// </summary>
public sealed record TerrainGenSettings
{
    public uint Seed { get; init; } = 1;

    public TerrainNoiseType Type { get; init; } = TerrainNoiseType.Hybrid;

    /// <summary>World units of the largest octave.</summary>
    public float FeatureSize { get; init; } = 128f;

    public int Octaves { get; init; } = 6;

    /// <summary>Amplitude kept per octave.</summary>
    public float Roughness { get; init; } = 0.45f;

    /// <summary>Frequency gained per octave.</summary>
    public float Lacunarity { get; init; } = 2f;

    /// <summary>Domain warp, in feature sizes.</summary>
    public float Warp { get; init; } = 0.25f;

    /// <summary>Above 1 flattens valleys and sharpens peaks.</summary>
    public float Exponent { get; init; } = 1f;

    public int Terraces { get; init; }

    public TerrainEdgeFalloff Falloff { get; init; } = TerrainEdgeFalloff.None;

    /// <summary>Share of the half extent that blends to the edge.</summary>
    public float FalloffWidth { get; init; } = 0.3f;

    /// <summary>Added before shaping; clamping makes flat floors or plateaus.</summary>
    public float Offset { get; init; }

    /// <summary>3 x 3 smoothing passes over the result.</summary>
    public int SmoothPasses { get; init; } = 1;

    /// <summary>The settings forced into their supported ranges (NaN takes the default).</summary>
    public TerrainGenSettings Clamped()
    {
        var d = new TerrainGenSettings();
        return this with
        {
            FeatureSize = AlpineTerrainRules.ClampFinite(FeatureSize, 1f, 65536f, d.FeatureSize),
            Octaves = Math.Clamp(Octaves, 1, 12),
            Roughness = AlpineTerrainRules.ClampFinite(Roughness, 0f, 1f, d.Roughness),
            Lacunarity = AlpineTerrainRules.ClampFinite(Lacunarity, 1f, 4f, d.Lacunarity),
            Warp = AlpineTerrainRules.ClampFinite(Warp, 0f, 2f, d.Warp),
            Exponent = AlpineTerrainRules.ClampFinite(Exponent, 0.1f, 8f, d.Exponent),
            Terraces = Math.Clamp(Terraces, 0, 64),
            FalloffWidth = AlpineTerrainRules.ClampFinite(FalloffWidth, 0.01f, 1f, d.FalloffWidth),
            Offset = AlpineTerrainRules.ClampFinite(Offset, -1f, 1f, d.Offset),
            SmoothPasses = Math.Clamp(SmoothPasses, 0, 16),
        };
    }
}

/// <summary>
/// Procedural heightfields for a terrain grid: seeded gradient noise summed over octaves (fBm, ridged, billow or a
/// ridged/fBm hybrid), domain warp, exponent shaping, terraces, edge falloff and smoothing, normalised to span the full
/// 0..1 height range unless the result is flat. Deterministic: the same settings and grid always give the same heights.
/// </summary>
public static class TerrainGenerator
{
    /// <summary>Heights 0..1 for an <paramref name="nx"/> x <paramref name="nz"/> grid of <paramref name="cellSize"/> cells (index z * nx + x).</summary>
    public static double[] Generate(TerrainGenSettings settings, int nx, int nz, float cellSize)
    {
        TerrainGenSettings s = settings.Clamped();
        var h = new double[nx * nz];
        double freq0 = 1.0 / s.FeatureSize;
        for (int z = 0; z < nz; z++)
        {
            for (int x = 0; x < nx; x++)
            {
                double wx = x * (double)cellSize, wz = z * (double)cellSize;
                if (s.Warp > 0f)
                {
                    double warp = s.Warp * s.FeatureSize;
                    double ox = Fbm(s.Seed ^ 0x51ED27u, wx * freq0, wz * freq0, 3, 0.5, 2.0);
                    double oz = Fbm(s.Seed ^ 0xA2C4F1u, (wx * freq0) + 5.2, (wz * freq0) + 1.3, 3, 0.5, 2.0);
                    wx += warp * ox;
                    wz += warp * oz;
                }

                h[(z * nx) + x] = Sample(s, wx * freq0, wz * freq0);
            }
        }

        Normalize(h);
        for (int i = 0; i < h.Length; i++)
        {
            double v = Math.Clamp(h[i] + s.Offset, 0.0, 1.0);
            v = Math.Pow(v, s.Exponent);
            if (s.Terraces > 0)
            {
                // Soft steps: each band eases into the next so the flats stay walkable.
                double band = v * s.Terraces;
                double k = Math.Floor(band);
                double f = band - k;
                f = f * f * f * ((f * ((f * 6) - 15)) + 10);
                v = (k + f) / s.Terraces;
            }

            h[i] = v;
        }

        if (s.Falloff != TerrainEdgeFalloff.None)
        {
            for (int z = 0; z < nz; z++)
            {
                for (int x = 0; x < nx; x++)
                {
                    double ex = Math.Min(x, nx - 1 - x) / Math.Max(1.0, (nx - 1) * 0.5);
                    double ez = Math.Min(z, nz - 1 - z) / Math.Max(1.0, (nz - 1) * 0.5);
                    double t = Math.Clamp(Math.Min(ex, ez) / s.FalloffWidth, 0.0, 1.0);
                    t = t * t * (3 - (2 * t));
                    double edge = s.Falloff == TerrainEdgeFalloff.Island ? 0.0 : 1.0;
                    int i = (z * nx) + x;
                    h[i] = edge + ((h[i] - edge) * t);
                }
            }
        }

        for (int pass = 0; pass < s.SmoothPasses; pass++)
        {
            h = Smooth(h, nx, nz);
        }

        Normalize(h);
        return h;
    }

    private static double Sample(TerrainGenSettings s, double x, double z) => s.Type switch
    {
        TerrainNoiseType.Fbm => Fbm(s.Seed, x, z, s.Octaves, s.Roughness, s.Lacunarity),
        TerrainNoiseType.Ridged => Ridged(s.Seed, x, z, s.Octaves, s.Roughness, s.Lacunarity),
        TerrainNoiseType.Billow => Billow(s.Seed, x, z, s.Octaves, s.Roughness, s.Lacunarity),
        _ => Hybrid(s, x, z),
    };

    private static double Hybrid(TerrainGenSettings s, double x, double z)
    {
        double low = (Fbm(s.Seed, x, z, s.Octaves, s.Roughness, s.Lacunarity) * 0.5) + 0.5;
        double ridge = Ridged(s.Seed ^ 0x9E3779B9u, x, z, s.Octaves, s.Roughness, s.Lacunarity);
        double t = Math.Clamp((low - 0.35) / 0.4, 0.0, 1.0);
        t = t * t * (3 - (2 * t));
        return (low * (1 - t)) + (((low * 0.5) + (ridge * 0.8)) * t);
    }

    private static double Fbm(uint seed, double x, double z, int octaves, double gain, double lacunarity)
    {
        double sum = 0, amp = 1, norm = 0, f = 1;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Gradient(seed + (uint)(o * 1013), x * f, z * f);
            norm += amp;
            amp *= gain;
            f *= lacunarity;
        }

        return norm > 0 ? sum / norm : 0;
    }

    private static double Ridged(uint seed, double x, double z, int octaves, double gain, double lacunarity)
    {
        double sum = 0, amp = 1, norm = 0, f = 1, weight = 1;
        for (int o = 0; o < octaves; o++)
        {
            double n = 1 - Math.Abs(Gradient(seed + (uint)(o * 1013), x * f, z * f));
            n *= n * weight;
            weight = Math.Clamp(n * 2, 0, 1);
            sum += amp * n;
            norm += amp;
            amp *= gain;
            f *= lacunarity;
        }

        return norm > 0 ? sum / norm : 0;
    }

    private static double Billow(uint seed, double x, double z, int octaves, double gain, double lacunarity)
    {
        double sum = 0, amp = 1, norm = 0, f = 1;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * ((Math.Abs(Gradient(seed + (uint)(o * 1013), x * f, z * f)) * 2) - 1);
            norm += amp;
            amp *= gain;
            f *= lacunarity;
        }

        return norm > 0 ? sum / norm : 0;
    }

    /// <summary>2D gradient noise in about [-1, 1] with quintic interpolation; lattice gradients hashed from the seed.</summary>
    private static double Gradient(uint seed, double x, double z)
    {
        double fx = Math.Floor(x), fz = Math.Floor(z);
        long ix = (long)fx, iz = (long)fz;
        double dx = x - fx, dz = z - fz;
        double u = dx * dx * dx * ((dx * ((dx * 6) - 15)) + 10);
        double v = dz * dz * dz * ((dz * ((dz * 6) - 15)) + 10);
        double n00 = Dot(seed, ix, iz, dx, dz), n10 = Dot(seed, ix + 1, iz, dx - 1, dz);
        double n01 = Dot(seed, ix, iz + 1, dx, dz - 1), n11 = Dot(seed, ix + 1, iz + 1, dx - 1, dz - 1);
        double a = n00 + ((n10 - n00) * u), b = n01 + ((n11 - n01) * u);
        return (a + ((b - a) * v)) * 1.4142;
    }

    private static double Dot(uint seed, long ix, long iz, double dx, double dz)
    {
        ulong h = AlpineTerrainRules.SplitMix64(((ulong)seed << 32) ^ AlpineTerrainRules.SplitMix64(((ulong)(uint)ix << 32) | (uint)iz));
        double angle = (h >> 11) * (1.0 / (1UL << 53)) * 2 * Math.PI;
        return (Math.Cos(angle) * dx) + (Math.Sin(angle) * dz);
    }

    private static void Normalize(double[] h)
    {
        double lo = h.Min(), hi = h.Max();
        if (!(hi - lo > 1e-12))
        {
            Array.Fill(h, Math.Clamp(lo, 0.0, 1.0));
            return;
        }

        for (int i = 0; i < h.Length; i++)
        {
            h[i] = (h[i] - lo) / (hi - lo);
        }
    }

    private static double[] Smooth(double[] h, int nx, int nz)
    {
        var o = new double[h.Length];
        for (int z = 0; z < nz; z++)
        {
            for (int x = 0; x < nx; x++)
            {
                double sum = 0;
                int n = 0;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx >= 0 && zz >= 0 && xx < nx && zz < nz)
                        {
                            sum += h[(zz * nx) + xx];
                            n++;
                        }
                    }
                }

                o[(z * nx) + x] = sum / n;
            }
        }

        return o;
    }
}
