using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// The Alpine surface charts that apply to a level's static geometry as it is now, decoded for display: the charts
/// Glacier just baked (<see cref="RflFile.AlpineLightmaps"/>) or, failing that, the level's own 0x0AFBAE09 chunk when
/// the game would use it — it passes the reader's checks against this geometry (<see cref="AlpineLightmapValidator"/>)
/// and every stock page is the size it records (game_patch/graphics/af_lightmap.cpp:441-489). Raw RGB8 layers are used
/// as stored; BC7 layers (Alpine RED's shipping codec) are decoded with <see cref="Bc7Decoder"/>.
/// </summary>
public sealed class AlpineLightmapAtlas
{
    private AlpineLightmapAtlas(
        object source, Geometry geometry, AlpineSurfaceChart[] charts, AlpineTile[] tiles, byte[][] pages, int stockPage)
    {
        Source = source;
        Geometry = geometry;
        Charts = charts;
        Tiles = tiles;
        Pages = pages;
        StockPage = stockPage;
    }

    /// <summary>What the atlas was read from (the bake, or the chunk's section) — a cache key for display builders.</summary>
    public object Source { get; }

    public Geometry Geometry { get; }

    /// <summary>Per geometry surface, positionally (empty geometry = no chart).</summary>
    public AlpineSurfaceChart[] Charts { get; }

    public AlpineTile[] Tiles { get; }

    /// <summary>RGB8 pages, 256² each.</summary>
    public byte[][] Pages { get; }

    /// <summary>The stock page edge the surfaces' lightmap UVs normalize against.</summary>
    public int StockPage { get; }

    /// <summary>The atlas that applies to <paramref name="rfl"/>'s static geometry, or null.</summary>
    public static AlpineLightmapAtlas? FromLevel(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        Geometry? geometry = null;
        IReadOnlyList<Lightmap>? stock = null;
        RflSection? chunk = null;
        foreach (RflSection s in rfl.Sections)
        {
            switch (s.Content)
            {
                case GeometrySection g when geometry is null:
                    geometry = g.Geometry;
                    break;
                case LightmapsSection l when stock is null:
                    stock = l.Lightmaps;
                    break;
            }

            if (s.TypeId == (uint)SectionType.AlpineLightmaps && chunk is null)
            {
                chunk = s;
            }
        }

        if (geometry is null)
        {
            return null;
        }

        if (rfl.AlpineLightmaps is { } bake && ReferenceEquals(bake.Geometry, geometry)
            && bake.Charts.Length == geometry.Surfaces.Count
            && bake.Signature == AlpineLightmapBake.SurfacesSignature(geometry.Surfaces, stock))
        {
            return new AlpineLightmapAtlas(bake, geometry, bake.Charts, bake.Tiles, bake.Pages, bake.StockPage);
        }

        // A level Glacier rebuilt or relit without baking charts keeps no usable chunk (it is dropped on save).
        if (chunk is null || rfl.GlacierLightingChanges != IO.Rfl.GlacierLightingChange.None)
        {
            return null;
        }

        return FromSection(chunk, rfl.Context, geometry, stock);
    }

    /// <summary>Decodes a chunk against <paramref name="geometry"/>, or null when the game would not use its surface charts.</summary>
    public static AlpineLightmapAtlas? FromSection(RflSection chunk, RflContext context, Geometry geometry, IReadOnlyList<Lightmap>? stock)
    {
        byte[] body;
        try
        {
            body = chunk.GetBodyBytes(context);
        }
        catch (Exception)
        {
            return null;
        }

        AlpineLightmapReadResult r = AlpineLightmapValidator.Read(body, geometry);
        if (!r.Ok || !r.SurfacesOk || r.LayerCodec > 1)
        {
            return null;
        }

        int stockPage = 1 << r.StockPageLog2;
        if (stock is not null && stock.Any(p => p.Width != stockPage || p.Height != stockPage))
        {
            return null; // af_lightmap.cpp:441-459: charts baked against other stock pages are dropped
        }

        byte[]? layer = AlpineLightmapValidator.DecodeLayer(body, r);
        if (layer is null)
        {
            return null;
        }

        var pages = new byte[r.PageCount][];
        for (int p = 0; p < pages.Length; p++)
        {
            pages[p] = r.LayerCodec == 1
                ? Bc7Decoder.DecodePageRgb(layer.AsSpan(p * AlpineLightmapLayout.Bc7PageBytes, AlpineLightmapLayout.Bc7PageBytes), AlpineLightmapLayout.PageSize)
                : layer.AsSpan(p * AlpineLightmapLayout.RawPageBytes, AlpineLightmapLayout.RawPageBytes).ToArray();
        }

        var charts = new AlpineSurfaceChart[geometry.Surfaces.Count];
        for (int i = 0; i < charts.Length; i++)
        {
            (ushort ku, ushort kv) = r.Charts[i];
            charts[i] = r.Geometries[i].IsEmpty ? default : new AlpineSurfaceChart(ku, kv, r.Geometries[i], r.Bases[i]);
        }

        return new AlpineLightmapAtlas(chunk, geometry, charts, r.Tiles.ToArray(), pages, stockPage);
    }

    /// <summary>
    /// The interior texels of surface <paramref name="surface"/>'s chart (cw × ch RGB8), each read from the tile that
    /// owns it (alpine_lightmap.h:544-574), or null for a surface without a chart.
    /// </summary>
    public byte[]? ChartImage(int surface)
    {
        if ((uint)surface >= (uint)Charts.Length || Charts[surface].Geometry.IsEmpty)
        {
            return null;
        }

        AlpineSurfaceChart c = Charts[surface];
        AlpineChartGeometry g = c.Geometry;
        var rgb = new byte[g.Cw * g.Ch * 3];
        for (int v = 0; v < g.Ch; v++)
        {
            for (int u = 0; u < g.Cw; u++)
            {
                (int index, int sx, int sy, bool valid) = AlpineLightmapLayout.TileSlot(g, c.TileBase, u, v);
                if (!valid || (uint)index >= (uint)Tiles.Length)
                {
                    continue;
                }

                AlpineTile t = Tiles[index];
                if (t.Page >= Pages.Length)
                {
                    continue;
                }

                int src = (((t.Y + sy) * AlpineLightmapLayout.PageSize) + t.X + sx) * 3;
                Buffer.BlockCopy(Pages[t.Page], src, rgb, ((v * g.Cw) + u) * 3, 3);
            }
        }

        return rgb;
    }
}
