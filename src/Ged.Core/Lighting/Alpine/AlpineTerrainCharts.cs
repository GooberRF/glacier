using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// One record of an Alpine lightmap section's terrain table (common/include/common/lightmap/alpine_lightmap.h:150-163):
/// a <see cref="W"/> × <see cref="H"/> texel chart over the terrain's XZ footprint, texel i spanning world
/// <c>[origin + i·texel_size, origin + (i + 1)·texel_size)</c>, and the chart fingerprint of the terrain it was baked for.
/// </summary>
public sealed record AlpineTerrainChartRecord(int TerrainUid, ushort W, ushort H, float OriginX, float OriginZ, float TexelSize, ulong Fingerprint)
{
    /// <summary>Bytes of one record on the wire.</summary>
    public const int WireSize = 28;

    /// <summary>The chart's tiling.</summary>
    public AlpineChartGeometry Geometry => AlpineTerrainCharts.ChartGeometry(W, H);

    /// <summary>Texels per cell the chart was baked at (<c>terrain_chart_density</c>, alpine_lightmap.h:621-624).</summary>
    public int DensityFor(int cellsX) => cellsX > 0 ? W / cellsX : 0;
}

/// <summary>
/// Alpine Faction's terrain lighting charts, reimplemented from Alpine RED's bake (editor_patch/alpine_lightmaps.cpp:
/// 570-622 <c>terrain_chart</c> / <c>collect_terrains</c>, 1159-1276 <c>shade_terrain_chart</c>, 2202-2242 the fit, 1576-1585
/// the table) and the shared derivations (alpine_lightmap.h:285-295, 604-647): one XZ-planar chart per lit terrain, cells ×
/// density texels per axis, each texel lit at its centre on the heightfield with the heightmap normal, converted the
/// fixed pipeline's way (a renormalised 3 × 3 box, round half up, hue-preserving rescale; lightmap.cpp:3107-3176).
/// </summary>
public static class AlpineTerrainCharts
{
    /// <summary>TableTag::terrain (alpine_lightmap.h:116).</summary>
    public const uint TableTag = 2;

    public const ushort TableVersion = 1;

    /// <summary>alpine_lightmap.h:287-290.</summary>
    public const int MaxCharts = 64;

    public const int MaxChartDim = 2048;

    /// <summary>The texels a section's terrain charts may declare: the stage budget's worth of tiles.</summary>
    public const long MaxChartTexels = (long)AlpineLightmapLayout.StagePageBudget * AlpineLightmapLayout.TileStep * AlpineLightmapLayout.TileStep;

    /// <summary>Shadow rays leave a terrain texel this far along its normal (alpine_lightmaps.cpp:1159-1164).</summary>
    public static float RayLift(float texelSize) => 0.02f + (0.05f * texelSize);

    /// <summary>A terrain chart's tiling; empty past the reader's 2048-texel edge (alpine_lightmap.h:386-394).</summary>
    public static AlpineChartGeometry ChartGeometry(int w, int h) =>
        w > MaxChartDim || h > MaxChartDim ? default : AlpineLightmapLayout.FromExtent(w, h);

    public static float TexelCenter(float origin, float texelSize, int i) => origin + ((i + 0.5f) * texelSize);

    /// <summary>A terrain the bake lights, at the density it got.</summary>
    public sealed class Entry
    {
        public required AlpineTerrain Terrain { get; init; }

        public required int CellsX { get; init; }

        public required int CellsZ { get; init; }

        public int Density { get; set; }

        public ulong Fingerprint { get; set; }

        public int W => CellsX * Density;

        public int H => CellsZ * Density;

        public float TexelSize => Terrain.CellSize / Density;

        public AlpineTerrainChartRecord Record => new(Terrain.Uid, (ushort)W, (ushort)H, Terrain.Origin.X, Terrain.Origin.Z, TexelSize, Fingerprint);
    }

    /// <summary>
    /// The terrains a bake lights (alpine_lightmaps.cpp:580-622): every built, non-fullbright terrain, one per uid, at
    /// most 64, at its own lightmap density. The rest keep no baked lighting; <paramref name="log"/> says why.
    /// </summary>
    public static List<Entry> Collect(IEnumerable<AlpineTerrain> terrains, List<string> log)
    {
        var outp = new List<Entry>();
        foreach (AlpineTerrain t in terrains)
        {
            string label = Compiler.TerrainChunkEmitter.Label(t);
            if (t.Fullbright)
            {
                log.Add($"{label} is fullbright, its lighting is not baked.");
                continue;
            }

            if (outp.Exists(e => e.Terrain.Uid == t.Uid))
            {
                log.Add($"{label} shares its uid with another terrain, its lighting is not baked.");
                continue;
            }

            if (outp.Count >= MaxCharts)
            {
                break;
            }

            int cx = t.Grid.CellsX, cz = t.Grid.CellsZ;
            if (cx == 0 || cz == 0)
            {
                continue;
            }

            outp.Add(new Entry
            {
                Terrain = t,
                CellsX = cx,
                CellsZ = cz,
                Density = Math.Clamp((int)t.LightmapDensity, AlpineTerrainRules.LightmapDensityMin, AlpineTerrainRules.LightmapDensityMax),
                Fingerprint = AlpineTerrainSurface.ChartFingerprint(t),
            });
        }

        return outp;
    }

    /// <summary>
    /// Lights every texel of a terrain chart through <paramref name="shade"/> (positions on the heightfield with the
    /// heightmap normal, the lift, the texel size) in 64 × 64 blocks, and converts the chart as the fixed pipeline converts a
    /// fragment. Returns the chart as RGB8, <c>W × H</c>.
    /// </summary>
    public static byte[] Shade(Entry e, Func<Vec3[], Vec3[], float, float, float[]> shade)
    {
        AlpineTerrain t = e.Terrain;
        int w = e.W, h = e.H;
        float ts = e.TexelSize;
        float lift = RayLift(ts);
        var light = new float[w * h * 3];
        const int block = 64;
        for (int by = 0; by < h; by += block)
        {
            for (int bx = 0; bx < w; bx += block)
            {
                int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
                var pos = new Vec3[bw * bh];
                var nrm = new Vec3[bw * bh];
                for (int j = 0; j < bh; j++)
                {
                    for (int i = 0; i < bw; i++)
                    {
                        float x = TexelCenter(t.Origin.X, ts, bx + i);
                        float z = TexelCenter(t.Origin.Z, ts, by + j);
                        pos[(j * bw) + i] = new Vec3(x, Editing.TerrainOps.HeightAt(t, x, z), z);
                        nrm[(j * bw) + i] = AlpineTerrainSurface.HeightmapNormal(t, x, z);
                    }
                }

                float[] lit = shade(pos, nrm, lift, ts);
                for (int j = 0; j < bh; j++)
                {
                    Array.Copy(lit, j * bw * 3, light, (((by + j) * w) + bx) * 3, bw * 3);
                }
            }
        }

        return Encode(light, w, h);
    }

    /// <summary>
    /// The fixed pipeline's float → RGB8 conversion of a <paramref name="w"/> × <paramref name="h"/> buffer
    /// (editor_patch/lightmap.cpp:3107-3176 <c>lm_encode_filtered</c> / <c>lm_encode_bytes</c>): every texel the mean of
    /// the 3 × 3 box around it, renormalised where the box is clipped, ×255 rounded half up, negatives to 0, and a
    /// hue-preserving rescale when the brightest channel passes 255.
    /// </summary>
    public static byte[] Encode(float[] rgb, int w, int h)
    {
        var outp = new byte[w * h * 3];
        for (int row = 0; row < h; row++)
        {
            int y0 = Math.Max(row - 1, 0), y1 = Math.Min(row + 1, h - 1);
            for (int col = 0; col < w; col++)
            {
                int x0 = Math.Max(col - 1, 0), x1 = Math.Min(col + 1, w - 1);
                double sr = 0, sg = 0, sb = 0;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int o = ((y * w) + x) * 3;
                        sr += rgb[o];
                        sg += rgb[o + 1];
                        sb += rgb[o + 2];
                    }
                }

                double scale = 255.0 / ((x1 - x0 + 1) * (y1 - y0 + 1));
                EncodeBytes(sr * scale, sg * scale, sb * scale, outp, ((row * w) + col) * 3);
            }
        }

        return outp;
    }

    private static void EncodeBytes(double r, double g, double b, byte[] outp, int at)
    {
        const double limit = 8000000.0;
        r = Math.Clamp(r, -limit, limit);
        g = Math.Clamp(g, -limit, limit);
        b = Math.Clamp(b, -limit, limit);
        int ir = Math.Max((int)(r + 0.5), 0), ig = Math.Max((int)(g + 0.5), 0), ib = Math.Max((int)(b + 0.5), 0);
        int max = Math.Max(ir, Math.Max(ig, ib));
        if (max > 255)
        {
            ir = ir * 255 / max;
            ig = ig * 255 / max;
            ib = ib * 255 / max;
        }

        outp[at] = (byte)ir;
        outp[at + 1] = (byte)ig;
        outp[at + 2] = (byte)ib;
    }

    /// <summary>
    /// The chart's texels from a section's decoded pages (RGB8 per page): texel (u, v) from the tile that owns it
    /// (alpine_lightmap.h:544-574). Null when a tile is out of range.
    /// </summary>
    public static byte[]? Gather(AlpineTerrainChartRecord c, int tileBase, IReadOnlyList<AlpineTile> tiles, IReadOnlyList<byte[]> pages)
    {
        AlpineChartGeometry g = c.Geometry;
        if (g.IsEmpty)
        {
            return null;
        }

        var outp = new byte[g.Cw * g.Ch * 3];
        const int p = AlpineLightmapLayout.PageSize;
        for (int v = 0; v < g.Ch; v++)
        {
            for (int u = 0; u < g.Cw; u++)
            {
                (int index, int sx, int sy, bool valid) = AlpineLightmapLayout.TileSlot(g, tileBase, u, v);
                if (!valid || (uint)index >= (uint)tiles.Count || tiles[index].Page >= pages.Count)
                {
                    return null;
                }

                AlpineTile t = tiles[index];
                int src = (((t.Y + sy) * p) + t.X + sx) * 3;
                Buffer.BlockCopy(pages[t.Page], src, outp, ((v * g.Cw) + u) * 3, 3);
            }
        }

        return outp;
    }
}
