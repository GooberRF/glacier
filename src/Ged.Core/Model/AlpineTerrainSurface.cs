using Ged.Core.Compiler;

namespace Ged.Core.Model;

/// <summary>One placed decoration mesh: its base on the surface, the surface normal there, its origin and orientation.</summary>
public readonly record struct AlpineDecorationInstance(int Decoration, int Chunk, Vec3 Base, Vec3 Normal, Vec3 Position, Vec3 Right, Vec3 Up, Vec3 Forward, float Scale);

/// <summary>
/// What the terrain shader, the terrain light bake and the decoration placement read from a terrain's grid, reimplemented
/// from <c>common/include/common/terrain/alpine_terrain.h</c> (Alpine master 8db7d90b; line numbers are that file's):
/// the shading normal (<c>heightmap_normal</c>, :1329-1371), the decoration placement (<c>for_each_decoration_instance</c> /
/// <c>for_each_terrain_decoration</c>, :1623-1837) and the fingerprint a baked terrain chart stores
/// (<c>decoration_lighting_hash</c> / <c>chart_fingerprint</c>, :1891-1968). Float arithmetic follows the header's order.
/// </summary>
public static class AlpineTerrainSurface
{
    public const byte DecorationFlagAlignToSlope = 0x1;
    public const byte DecorationFlagRandomYaw = 0x2;
    public const byte DecorationFlagCastsShadows = 0x4;
    public const byte DecorationFlagDitherFade = 0x8;
    public const byte DecorationFlagEdges = 0x30;

    /// <summary>Flags that only change drawing; lighting hashes ignore them (:129).</summary>
    public const byte DecorationDrawOnlyFlags = DecorationFlagDitherFade | DecorationFlagEdges;

    /// <summary>Instances one weight texel may expect (:150).</summary>
    public const int MaxDecorationInstancesPerTexel = 256;

    /// <summary>The level's placement budget: instances placed, and candidates tried (:151-154).</summary>
    public const int MaxLevelDecorationInstances = 250000;

    public const int MaxLevelDecorationCandidates = 4 * MaxLevelDecorationInstances;

    /// <summary>Distinct decoration meshes a level loads (:155-157).</summary>
    public const int MaxLevelDecorationMeshes = 128;

    // ---- heightmap normal (:1329-1371) -----------------------------------------------------------------------------

    /// <summary>The normalised height at heightmap texel coordinates (u, v), vertex (x, z) at texel centre (x + 0.5, z + 0.5).</summary>
    public static float Height01Bilinear(AlpineTerrainGrid g, float u, float v)
    {
        static void Axis(float c, int n, out int i0, out int i1, out float t)
        {
            float f = float.IsFinite(c) ? Math.Clamp(c - 0.5f, -1.0f, n) : 0.0f;
            float fl = MathF.Floor(f);
            t = f - fl;
            long i = (long)fl;
            long hi = n - 1;
            i0 = (int)Math.Clamp(i, 0, hi);
            i1 = (int)Math.Clamp(i + 1, 0, hi);
        }

        Axis(u, g.Nx, out int x0, out int x1, out float tx);
        Axis(v, g.Nz, out int z0, out int z1, out float tz);
        float H(int x, int z) => g.Heights[(z * g.Nx) + x] / 65535.0f;
        float a = H(x0, z0) + ((H(x1, z0) - H(x0, z0)) * tx);
        float b = H(x0, z1) + ((H(x1, z1) - H(x0, z1)) * tx);
        return a + ((b - a) * tz);
    }

    /// <summary>The shading normal at world (x, z): central differences one cell either side, one-sided at the edge.</summary>
    public static Vec3 HeightmapNormal(AlpineTerrain t, float worldX, float worldZ)
    {
        AlpineTerrainGrid g = t.Grid;
        float tx = ((worldX - t.Origin.X) / t.CellSize) + 0.5f;
        float tz = ((worldZ - t.Origin.Z) / t.CellSize) + 0.5f;
        float loX = MathF.Max(tx - 1.0f, 0.5f), hiX = MathF.Min(tx + 1.0f, g.Nx - 0.5f);
        float loZ = MathF.Max(tz - 1.0f, 0.5f), hiZ = MathF.Min(tz + 1.0f, g.Nz - 0.5f);
        float hl = Height01Bilinear(g, loX, tz), hr = Height01Bilinear(g, hiX, tz);
        float hd = Height01Bilinear(g, tx, loZ), hu = Height01Bilinear(g, tx, hiZ);
        float kx = t.HeightRange / (MathF.Max(hiX - loX, 1e-3f) * t.CellSize);
        float kz = t.HeightRange / (MathF.Max(hiZ - loZ, 1e-3f) * t.CellSize);
        float nx = (hl - hr) * kx, ny = 1.0f, nz = (hd - hu) * kz;
        float len = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return new Vec3(nx / len, ny / len, nz / len);
    }

    // ---- decorations (:1623-1837) ----------------------------------------------------------------------------------

    /// <summary>Decoration <paramref name="index"/>'s coverage plane offset, or -1 when the terrain stores none for it.</summary>
    public static int PlaneOffset(AlpineTerrain t, int index)
    {
        AlpineTerrainGrid g = t.Grid;
        int plane = AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution);
        return g.DecorationCoverage is { } c && c.Length >= (index + 1) * plane ? index * plane : -1;
    }

    /// <summary>Places nothing, casts nothing and hashes nothing when inactive (:1660-1663).</summary>
    public static bool DecorationActive(AlpineTerrain t, int index) =>
        index < t.Decorations.Count && PlaneOffset(t, index) >= 0 && !string.IsNullOrEmpty(t.Decorations[index].Mesh)
        && t.Decorations[index].Density > 0.0f;

    public static bool DecorationCasts(AlpineTerrain t, int index) =>
        DecorationActive(t, index) && (t.Decorations[index].Flags & DecorationFlagCastsShadows) != 0;

    public static ulong DecorationSeed(int uid, uint index) =>
        AlpineTerrainRules.SplitMix64(AlpineTerrainRules.SplitMix64((uint)uid) ^ ((0xDEC0UL << 48) | index));

    public static ulong DecorationTexelKey(ulong seed, uint i, uint j) =>
        AlpineTerrainRules.SplitMix64(seed ^ (((ulong)j << 32) | i));

    /// <summary>The top 24 bits of <paramref name="h"/> as a float in [0, 1).</summary>
    public static float HashU01(ulong h) => (h >> 40) * (1.0f / 16777216.0f);

    /// <summary>The level's remaining placement work (:1713-1722).</summary>
    public sealed class Budget
    {
        public int Instances { get; set; } = MaxLevelDecorationInstances;

        public int Candidates { get; set; } = MaxLevelDecorationCandidates;

        public bool Spent => Instances == 0 || Candidates == 0;
    }

    private static bool CellSolid(AlpineTerrainGrid g, long x, long z)
    {
        int cx = g.CellsX, cz = g.CellsZ;
        return x >= 0 && z >= 0 && x < cx && z < cz && !g.IsHole((int)x, (int)z);
    }

    /// <summary>
    /// Decoration <paramref name="d"/>'s instances on weight texels [x0, x1) x [z0, z1), row by row, within
    /// <paramref name="budget"/>; <paramref name="fn"/> returns false to stop (:1730-1808).
    /// </summary>
    public static int ForEachDecorationInstance(
        AlpineTerrain t, int d, ulong seed, int x0, int z0, int x1, int z1, int chunk, Budget budget, Func<AlpineDecorationInstance, bool> fn)
    {
        if (!DecorationActive(t, d) || budget.Spent)
        {
            return 0;
        }

        AlpineTerrainDecoration deco = t.Decorations[d];
        AlpineTerrainGrid g = t.Grid;
        bool linked = deco.LinkLayer != AlpineTerrainRules.DecorationLinkNone;
        if (linked && deco.LinkLayer >= Math.Min(t.Layers.Count, AlpineTerrainRules.MaxLayers))
        {
            return 0;
        }

        int mul = g.WeightResolution;
        int ww = AlpineTerrainRules.WeightWidth(g.Nx, mul), wh = AlpineTerrainRules.WeightHeight(g.Nz, mul);
        x1 = Math.Min(x1, ww);
        z1 = Math.Min(z1, wh);
        if (mul == 0 || x0 >= x1 || z0 >= z1)
        {
            return 0;
        }

        double step = (double)t.CellSize / mul;
        double expected = Math.Min((double)deco.Density * step * step, MaxDecorationInstancesPerTexel);
        ulong expectedQ16 = (ulong)Math.Round(expected * 65536.0, MidpointRounding.AwayFromZero);
        float cosMax = (float)Math.Cos(deco.MaxSlopeDegrees * (3.14159265358979 / 180.0));
        int mapBytes = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, mul);
        int linkBase = linked ? (deco.LinkLayer < 4 ? 0 : mapBytes) + (deco.LinkLayer & 3) : 0;
        bool align = (deco.Flags & DecorationFlagAlignToSlope) != 0;
        bool randomYaw = (deco.Flags & DecorationFlagRandomYaw) != 0;
        byte[] coverage = g.DecorationCoverage!;
        int planeOffset = PlaneOffset(t, d);
        const float twoPi = 6.28318531f;

        int count = 0;
        for (int j = z0; j < z1; j++)
        {
            for (int i = x0; i < x1; i++)
            {
                int texel = (j * ww) + i;
                uint cov = coverage[planeOffset + texel];
                if (cov == 0)
                {
                    continue;
                }

                uint weight = linked ? g.Weights[linkBase + (texel * 4)] : 255u;
                if (weight == 0 || !CellSolid(g, i / mul, j / mul))
                {
                    continue;
                }

                ulong nQ16 = expectedQ16 * cov * weight / 65025u;
                uint n = (uint)(nQ16 >> 16);
                ulong key = DecorationTexelKey(seed, (uint)i, (uint)j);
                if ((AlpineTerrainRules.SplitMix64(key ^ 0xF4AC7105UL) & 0xFFFFUL) < (nQ16 & 0xFFFFUL))
                {
                    n++;
                }

                for (uint k = 0; k < n; k++)
                {
                    if (budget.Candidates == 0)
                    {
                        return count;
                    }

                    budget.Candidates--;
                    ulong s = AlpineTerrainRules.SplitMix64(unchecked(key + ((k + 1UL) * 0x9E3779B97F4A7C15UL)));
                    ulong r1 = AlpineTerrainRules.SplitMix64(s), r2 = AlpineTerrainRules.SplitMix64(r1);
                    ulong r3 = AlpineTerrainRules.SplitMix64(r2), r4 = AlpineTerrainRules.SplitMix64(r3);
                    float bx = t.Origin.X + (float)((i + (double)HashU01(r1)) * step);
                    float bz = t.Origin.Z + (float)((j + (double)HashU01(r2)) * step);
                    float by = Editing.TerrainOps.HeightAt(t, bx, bz);
                    Vec3 normal = HeightmapNormal(t, bx, bz);
                    if (!(normal.Y >= cosMax))
                    {
                        continue;
                    }

                    float yaw = randomYaw ? HashU01(r3) * twoPi : 0.0f;
                    float scale = deco.ScaleMin + ((deco.ScaleMax - deco.ScaleMin) * HashU01(r4));
                    float ux = align ? normal.X : 0.0f, uy = align ? normal.Y : 1.0f, uz = align ? normal.Z : 0.0f;
                    float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
                    float rx = uy * cy, ry = (uz * sy) - (ux * cy), rz = -uy * sy;
                    float len = MathF.Sqrt((rx * rx) + (ry * ry) + (rz * rz));
                    if (!(len >= 1e-6f))
                    {
                        rx = -uy * sy;
                        ry = (uz * cy) + (ux * sy);
                        rz = -uy * cy;
                        len = MathF.Sqrt((rx * rx) + (ry * ry) + (rz * rz));
                    }

                    float lift = deco.VerticalOffset * scale;
                    var right = new Vec3(rx / len, ry / len, rz / len);
                    var up = new Vec3(ux, uy, uz);
                    var fwd = new Vec3(
                        (right.Y * up.Z) - (right.Z * up.Y),
                        (right.Z * up.X) - (right.X * up.Z),
                        (right.X * up.Y) - (right.Y * up.X));
                    var basePos = new Vec3(bx, by, bz);
                    var pos = new Vec3(bx + (ux * lift), by + (uy * lift), bz + (uz * lift));
                    count++;
                    budget.Instances--;
                    if (!fn(new AlpineDecorationInstance(d, chunk, basePos, normal, pos, right, up, fwd, scale)) || budget.Instances == 0)
                    {
                        return count;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Every decoration instance of one terrain in the order the game places them: chunk by chunk, within a chunk
    /// decoration by decoration, within <paramref name="budget"/> (the level's, carried across terrains; :1814-1837).
    /// </summary>
    public static int ForEachTerrainDecoration(AlpineTerrain t, Budget budget, Func<AlpineDecorationInstance, bool> fn)
    {
        int count = Math.Min(t.Decorations.Count, AlpineTerrainRules.MaxDecorations);
        var seeds = new ulong[count];
        for (int k = 0; k < count; k++)
        {
            seeds[k] = DecorationSeed(t.Uid, (uint)k);
        }

        AlpineTerrainGrid g = t.Grid;
        int edge = TerrainChunkEmitter.EffectiveChunkCells(t);
        int chunks = AlpineTerrainRules.ChunkCount(g.CellsX, g.CellsZ, edge);
        int mul = g.WeightResolution;
        int visited = 0;
        bool stop = false;
        for (int c = 0; c < chunks && !stop && !budget.Spent; c++)
        {
            TerrainChunkRect r = AlpineTerrainRules.ChunkRect(g.CellsX, g.CellsZ, edge, c);
            for (int k = 0; k < count && !stop && !budget.Spent; k++)
            {
                visited += ForEachDecorationInstance(t, k, seeds[k], r.X0 * mul, r.Z0 * mul, r.X1 * mul, r.Z1 * mul, c, budget, inst =>
                {
                    stop = !fn(inst);
                    return !stop;
                });
            }
        }

        return visited;
    }

    // ---- chart fingerprint (:1891-1968) ----------------------------------------------------------------------------

    /// <summary>
    /// What a terrain's baked light depends on beyond its <see cref="AlpineTerrainRules.LightingFingerprint(AlpineTerrain)"/>:
    /// the decorations that cast shadows, by everything that places them; 0 when none casts (:1894-1944).
    /// </summary>
    public static ulong DecorationLightingHash(AlpineTerrain t)
    {
        int count = Math.Min(t.Decorations.Count, AlpineTerrainRules.MaxDecorations);
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            any |= DecorationCasts(t, i);
        }

        if (!any)
        {
            return 0;
        }

        ulong h = 0x6A09E667F3BCC908UL;
        void Mix(ulong v) => h = AlpineTerrainRules.SplitMix64(h ^ v);
        void Bytes(int n, Func<int, byte> byteAt)
        {
            Mix((ulong)n);
            ulong w = 0;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                w |= (ulong)byteAt(i) << (8 * k);
                if (++k == 8)
                {
                    Mix(w);
                    w = 0;
                    k = 0;
                }
            }

            Mix(w);
        }

        AlpineTerrainGrid g = t.Grid;
        int texels = AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution);
        int mapBytes = AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution);
        Mix((uint)t.Uid);
        Mix((uint)g.WeightResolution);
        for (int i = 0; i < count; i++)
        {
            if (!DecorationCasts(t, i))
            {
                continue;
            }

            AlpineTerrainDecoration d = t.Decorations[i];
            Mix((uint)i);
            string mesh = d.Mesh;
            Bytes(mesh.Length, k => (byte)char.ToLowerInvariant(mesh[k] < 128 ? mesh[k] : '?'));
            Mix(AlpineTerrainRules.PositionBits(d.Density));
            Mix(AlpineTerrainRules.PositionBits(d.ScaleMin));
            Mix(AlpineTerrainRules.PositionBits(d.ScaleMax));
            Mix(AlpineTerrainRules.PositionBits(d.MaxSlopeDegrees));
            Mix(AlpineTerrainRules.PositionBits(d.VerticalOffset));
            Mix((uint)(d.Flags & ~DecorationDrawOnlyFlags));
            Mix(d.LinkLayer);
            int plane = PlaneOffset(t, i);
            byte[] cov = g.DecorationCoverage!;
            // mix_le_words over the coverage bytes: the length, 8 bytes per word, the zero-padded tail (:1843-1859)
            Mix((ulong)texels);
            int at = 0;
            for (; at + 8 <= texels; at += 8)
            {
                Mix(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(cov.AsSpan(plane + at, 8)));
            }

            ulong tail = 0;
            for (int k = 0; at + k < texels; k++)
            {
                tail |= (ulong)cov[plane + at + k] << (8 * k);
            }

            Mix(tail);
            if (d.LinkLayer != AlpineTerrainRules.DecorationLinkNone && d.LinkLayer < AlpineTerrainRules.MaxLayers)
            {
                int baseAt = (d.LinkLayer < 4 ? 0 : mapBytes) + (d.LinkLayer & 3);
                Bytes(texels, k => g.Weights[baseAt + (k * 4)]);
            }
        }

        return h == 0 ? 1 : h;
    }

    /// <summary>
    /// The fingerprint a baked terrain chart stores: the lighting fingerprint, mixed with the decoration lighting hash when
    /// a decoration casts shadows (<c>chart_fingerprint</c>, :1948-1951). The game drops a chart that does not match.
    /// </summary>
    public static ulong ChartFingerprint(AlpineTerrain t)
    {
        ulong deco = DecorationLightingHash(t);
        ulong light = AlpineTerrainRules.LightingFingerprint(t);
        return deco == 0 ? light : AlpineTerrainRules.SplitMix64(light ^ deco);
    }
}
