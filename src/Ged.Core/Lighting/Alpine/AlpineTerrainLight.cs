using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>One terrain's baked light, decoded for display: its chart record and texels (RGB8, W × H).</summary>
public sealed record AlpineTerrainChartImage(AlpineTerrainChartRecord Chart, byte[] Rgb);

/// <summary>
/// The terrain charts a level carries, decoded: from Glacier's last bake (<see cref="RflFile.AlpineLightmaps"/>), else
/// from the level's own 0x0AFBAE09 chunk while Glacier has not rebuilt or relit the level (a rebuilt level drops the chunk
/// on save). A chart lights a terrain only while it matches it (<see cref="AlpineLightmapValidator.TerrainChartMatches"/>,
/// alpine_lightmap_reader.h:1014-1041), as the game and Alpine RED's preview check (alpine_lightmaps.cpp:1634-1641).
/// Decoding is cached per source.
/// </summary>
public static class AlpineTerrainLight
{
    private static readonly ConditionalWeakTable<object, IReadOnlyList<AlpineTerrainChartImage>> Cache = new();

    /// <summary>Every usable terrain chart of the level (unmatched ones included; callers match them to terrains).</summary>
    public static IReadOnlyList<AlpineTerrainChartImage> Charts(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        if (rfl.AlpineLightmaps is { Terrains.Count: > 0 } bake)
        {
            return Cache.GetValue(bake, _ => FromBake(bake));
        }

        if (rfl.GlacierLightingChanges != GlacierLightingChange.None)
        {
            return Array.Empty<AlpineTerrainChartImage>();
        }

        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.AlpineLightmaps)
            {
                object key = (object?)s.Content ?? s;
                return Cache.GetValue(key, _ => FromSection(s, rfl.Context));
            }
        }

        return Array.Empty<AlpineTerrainChartImage>();
    }

    /// <summary>The chart that lights <paramref name="t"/> as it is now, or null.</summary>
    public static AlpineTerrainChartImage? For(RflFile rfl, AlpineTerrain t)
    {
        if (t.Fullbright)
        {
            return null;
        }

        foreach (AlpineTerrainChartImage c in Charts(rfl))
        {
            if (c.Chart.TerrainUid == t.Uid && AlpineLightmapValidator.TerrainChartMatches(c.Chart, t))
            {
                return c;
            }
        }

        return null;
    }

    private static IReadOnlyList<AlpineTerrainChartImage> FromBake(AlpineLightmapBake bake)
    {
        var list = new List<AlpineTerrainChartImage>();
        foreach ((AlpineTerrainChartRecord c, int tileBase) in bake.TerrainTiles())
        {
            if (AlpineTerrainCharts.Gather(c, tileBase, bake.Tiles, bake.Pages) is { } rgb)
            {
                list.Add(new AlpineTerrainChartImage(c, rgb));
            }
        }

        return list;
    }

    private static IReadOnlyList<AlpineTerrainChartImage> FromSection(RflSection chunk, RflContext context)
    {
        try
        {
            byte[] body = chunk.GetBodyBytes(context);
            AlpineLightmapReadResult r = AlpineLightmapValidator.Read(body, null);
            if (!r.Ok || r.Terrains.Count == 0 || r.LayerCodec > 1 || AlpineLightmapValidator.DecodeLayer(body, r) is not { } layer)
            {
                return Array.Empty<AlpineTerrainChartImage>();
            }

            // Only the pages the terrain charts' tiles sit on are decoded.
            var used = new HashSet<int>();
            for (int i = 0; i < r.Terrains.Count; i++)
            {
                for (int k = 0; k < r.Terrains[i].Geometry.TileCount && r.TerrainBases[i] + k < r.Tiles.Count; k++)
                {
                    used.Add(r.Tiles[r.TerrainBases[i] + k].Page);
                }
            }

            var pages = new byte[r.PageCount][];
            for (int p = 0; p < pages.Length; p++)
            {
                pages[p] = !used.Contains(p) ? Array.Empty<byte>() : r.LayerCodec == 1
                    ? Bc7Decoder.DecodePageRgb(layer.AsSpan(p * AlpineLightmapLayout.Bc7PageBytes, AlpineLightmapLayout.Bc7PageBytes), AlpineLightmapLayout.PageSize)
                    : layer.AsSpan(p * AlpineLightmapLayout.RawPageBytes, AlpineLightmapLayout.RawPageBytes).ToArray();
            }

            var list = new List<AlpineTerrainChartImage>();
            for (int i = 0; i < r.Terrains.Count; i++)
            {
                if (r.TerrainOk[i] && AlpineTerrainCharts.Gather(r.Terrains[i], r.TerrainBases[i], r.Tiles, pages) is { } rgb)
                {
                    list.Add(new AlpineTerrainChartImage(r.Terrains[i], rgb));
                }
            }

            return list;
        }
        catch (Exception)
        {
            return Array.Empty<AlpineTerrainChartImage>();
        }
    }
}
