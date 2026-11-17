using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// Alpine RED's surface-chart bake, reimplemented from editor_patch/alpine_lightmaps.cpp (layout :2082-2306, shading
/// :706-867 and :2331-2377, edge replication and gutter reconciliation :1062-1157, section body :1479-1601). A chart
/// is its stock fragment's interior refined k times per axis; k comes from the level's lightmap density over the
/// surface's lightmap-axis extent; the charts are tiled and packed exactly as Alpine RED packs them; each chart is
/// lit with the same per-texel kernel, lights, occluders and ambient as the stock pass, on a view of the surface's
/// page scaled by k with the surface's own uv_scale / uv_add (so k = 1 is the stock fragment to the texel); every
/// stock interior texel then becomes the rounded box mean of the k_u × k_v chart texels over it, which is what other
/// renderers draw.
/// </summary>
public static class AlpineChartBaker
{
    /// <summary>A tile view's scratch cap in Alpine RED; charts over it stay on the stock lightmaps (alpine_lightmaps.cpp:59-62).</summary>
    public const long MaxTileViewBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Derives the surface charts of <paramref name="g"/> at <paramref name="density"/>
    /// (alpine_lightmaps.cpp:384-438 <c>derive_surface_charts</c>). Only a surface whose page is
    /// <paramref name="stockPage"/> square, whose fragment is larger than 2×2 and whose lightmap axes are valid gets
    /// one; k per axis from the surface bounding box extent along that axis.
    /// </summary>
    public static AlpineChartGeometry[] DeriveCharts(
        IReadOnlyList<Surface> surfaces, IReadOnlyList<Lightmap> pages, int stockPage, float density, out ushort[] kU, out ushort[] kV,
        Func<int, bool>? eligible = null) =>
        DeriveCharts(surfaces, i => (uint)surfaces[i].LightmapIndex < (uint)pages.Count ? pages[surfaces[i].LightmapIndex] : null,
            stockPage, density, out kU, out kV, eligible);

    /// <summary>
    /// <see cref="DeriveCharts(IReadOnlyList{Surface}, IReadOnlyList{Lightmap}, int, float, out ushort[], out ushort[], Func{int, bool}?)"/>
    /// with each surface's page given by <paramref name="pageOf"/> (a mover's surfaces sit on pages of their own).
    /// </summary>
    public static AlpineChartGeometry[] DeriveCharts(
        IReadOnlyList<Surface> surfaces, Func<int, Lightmap?> pageOf, int stockPage, float density, out ushort[] kU, out ushort[] kV,
        Func<int, bool>? eligible = null)
    {
        int n = surfaces.Count;
        var charts = new AlpineChartGeometry[n];
        kU = new ushort[n];
        kV = new ushort[n];
        if (!AlpineLightmapLayout.StockPageEdgeValid(stockPage))
        {
            return charts;
        }

        for (int i = 0; i < n; i++)
        {
            Surface s = surfaces[i];
            if (eligible is not null && !eligible(i))
            {
                continue;
            }

            if (pageOf(i) is not { } page)
            {
                continue;
            }

            int w = s.W, h = s.H;
            if (w <= 2 || h <= 2 || page.Width != stockPage || page.Height != stockPage
                || s.UCoefficient is < 0 or > 2 || s.VCoefficient is < 0 or > 2)
            {
                continue;
            }

            float extentU = s.BoundingBox.P2.Component(s.UCoefficient) - s.BoundingBox.P1.Component(s.UCoefficient);
            float extentV = s.BoundingBox.P2.Component(s.VCoefficient) - s.BoundingBox.P1.Component(s.VCoefficient);
            ushort ku = AlpineLightmapLayout.KFromDensity(w, extentU, density);
            ushort kv = AlpineLightmapLayout.KFromDensity(h, extentV, density);
            AlpineChartGeometry geom = AlpineLightmapLayout.SurfaceChart(w, h, ku, kv);
            if (geom.IsEmpty || TileViewBytes(geom, ku, page.Width) > MaxTileViewBytes)
            {
                continue;
            }

            charts[i] = geom;
            kU[i] = ku;
            kV[i] = kv;
        }

        return charts;
    }

    /// <summary>The largest tile view a chart needs in Alpine RED (alpine_lightmaps.cpp:350-363).</summary>
    public static long TileViewBytes(in AlpineChartGeometry g, int kU, int lmW)
    {
        long worst = 0;
        long stride = (long)kU * lmW * 3;
        for (int ty = 0; ty < g.Ny; ty++)
        {
            for (int tx = 0; tx < g.Nx; tx++)
            {
                AlpineTileDims td = AlpineLightmapLayout.TileDims(g, tx, ty);
                worst = Math.Max(worst, ((td.StoredH - 1) * stride) + (td.StoredW * 3L));
            }
        }

        return worst;
    }

    /// <summary>The stock page edge of the first surface that has a page (alpine_lightmaps.cpp:372-382).</summary>
    public static int FirstStockPage(IReadOnlyList<Surface> surfaces, IReadOnlyList<Lightmap> pages)
    {
        foreach (Surface s in surfaces)
        {
            if ((uint)s.LightmapIndex < (uint)pages.Count && pages[s.LightmapIndex].Width > 0)
            {
                return pages[s.LightmapIndex].Width;
            }
        }

        return 0;
    }
}

