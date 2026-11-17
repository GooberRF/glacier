using System.Numerics;
using System.Runtime.CompilerServices;
using Ged.Core.IO.Rfl;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;

namespace Ged.Rendering.Scene;

/// <summary>
/// The viewport's view of a level's Alpine surface charts (RFL chunk 0x0AFBAE09): each charted surface's chart is laid
/// whole, with a one-texel edge ring, into large display pages appended after the stock lightmap pages, and the
/// surface's faces sample it through their stock lightmap UVs mapped to chart texels exactly as Alpine Faction's
/// Direct3D 11 shader maps them (<c>(page_u·lm_w − x − 1)·k</c>, alpine_lightmap.h:466-484). Whole charts, rather
/// than Alpine's 244-texel tiles, keep the mapping per-vertex affine so the existing lightmap shader draws them.
/// A chart wider than a display page is box-filtered down to fit (display only).
/// </summary>
public sealed class AlpineChartDisplay
{
    /// <summary>Edge of a display page.</summary>
    public const int PageEdge = 2048;

    private static readonly ConditionalWeakTable<object, AlpineChartDisplay> Cache = new();

    private readonly Placement[] _placements;

    private AlpineChartDisplay(AlpineLightmapAtlas atlas, List<Lightmap> pages, Placement[] placements)
    {
        Atlas = atlas;
        Pages = pages;
        _placements = placements;
    }

    /// <summary>The atlas shown.</summary>
    public AlpineLightmapAtlas Atlas { get; }

    /// <summary>The display pages (RGB8, <see cref="PageEdge"/>²).</summary>
    public List<Lightmap> Pages { get; }

    /// <summary>The display of <paramref name="file"/>'s Alpine charts, or null when none apply (cached per source).</summary>
    public static AlpineChartDisplay? For(RflFile file)
    {
        if (AlpineLightmapAtlas.FromLevel(file) is not { } atlas)
        {
            return null;
        }

        if (Cache.TryGetValue(atlas.Source, out AlpineChartDisplay? cached) && ReferenceEquals(cached.Atlas.Geometry, atlas.Geometry))
        {
            return cached;
        }

        AlpineChartDisplay built = Build(atlas);
        Cache.AddOrUpdate(atlas.Source, built);
        return built;
    }

    /// <summary>Lays the atlas's charts out in display pages.</summary>
    public static AlpineChartDisplay Build(AlpineLightmapAtlas atlas)
    {
        int n = atlas.Charts.Length;
        var placements = new Placement[n];
        var order = Enumerable.Range(0, n).Where(i => !atlas.Charts[i].Geometry.IsEmpty)
            .OrderByDescending(i => atlas.Charts[i].Geometry.Ch).ThenBy(i => i).ToList();
        var pages = new List<Lightmap>();
        int x = 0, y = 0, shelf = 0;
        Lightmap? page = null;
        foreach (int i in order)
        {
            AlpineChartGeometry g = atlas.Charts[i].Geometry;
            int scale = 1;
            while (((g.Cw + scale - 1) / scale) + 2 > PageEdge || ((g.Ch + scale - 1) / scale) + 2 > PageEdge)
            {
                scale++;
            }

            int w = ((g.Cw + scale - 1) / scale) + 2, h = ((g.Ch + scale - 1) / scale) + 2;
            if (page is null || x + w > PageEdge)
            {
                x = 0;
                y += shelf;
                shelf = 0;
            }

            if (page is null || y + h > PageEdge)
            {
                page = new Lightmap { Width = PageEdge, Height = PageEdge, Pixels = new byte[PageEdge * PageEdge * 3] };
                pages.Add(page);
                x = y = shelf = 0;
            }

            byte[] image = atlas.ChartImage(i)!;
            Blit(image, g, scale, page, x, y);
            placements[i] = new Placement(pages.Count - 1, x, y, scale, true);
            x += w;
            shelf = Math.Max(shelf, h);
        }

        return new AlpineChartDisplay(atlas, pages, placements);
    }

    /// <summary>
    /// The display page (relative to the display pages) and display UV for a static face vertex of surface
    /// <paramref name="surface"/> whose stock lightmap UV is <paramref name="stockUv"/>; false when the surface has no
    /// chart.
    /// </summary>
    public bool TryMap(int surface, Surface s, Vector2 stockUv, out int page, out Vector2 uv)
    {
        page = -1;
        uv = default;
        if ((uint)surface >= (uint)_placements.Length || !_placements[surface].Used)
        {
            return false;
        }

        Placement p = _placements[surface];
        AlpineSurfaceChart c = Atlas.Charts[surface];
        (float cu, float cv) = AlpineLightmapLayout.ChartTexelFromPageUv(
            stockUv.X, stockUv.Y, Atlas.StockPage, Atlas.StockPage, s.X, s.Y, c.KU, c.KV);
        page = p.Page;
        uv = new Vector2((p.X + 1 + (cu / p.Scale)) / PageEdge, (p.Y + 1 + (cv / p.Scale)) / PageEdge);
        return true;
    }

    /// <summary>True when surface <paramref name="surface"/> is drawn from its chart.</summary>
    public bool HasChart(int surface) => (uint)surface < (uint)_placements.Length && _placements[surface].Used;

    // Writes the chart (box-filtered by `scale`) plus a ring that repeats its edge texels.
    private static void Blit(byte[] image, AlpineChartGeometry g, int scale, Lightmap page, int px, int py)
    {
        int w = (g.Cw + scale - 1) / scale, h = (g.Ch + scale - 1) / scale;
        for (int y = -1; y <= h; y++)
        {
            int sy = Math.Clamp(y, 0, h - 1);
            for (int x = -1; x <= w; x++)
            {
                int sx = Math.Clamp(x, 0, w - 1);
                int r = 0, gg = 0, b = 0, n = 0;
                for (int dy = 0; dy < scale; dy++)
                {
                    int cy = (sy * scale) + dy;
                    if (cy >= g.Ch)
                    {
                        break;
                    }

                    for (int dx = 0; dx < scale; dx++)
                    {
                        int cx = (sx * scale) + dx;
                        if (cx >= g.Cw)
                        {
                            break;
                        }

                        int o = ((cy * g.Cw) + cx) * 3;
                        r += image[o];
                        gg += image[o + 1];
                        b += image[o + 2];
                        n++;
                    }
                }

                int dst = ((((py + 1 + y) * PageEdge) + px + 1 + x) * 3);
                page.Pixels[dst] = (byte)(r / Math.Max(1, n));
                page.Pixels[dst + 1] = (byte)(gg / Math.Max(1, n));
                page.Pixels[dst + 2] = (byte)(b / Math.Max(1, n));
            }
        }
    }

    private readonly record struct Placement(int Page, int X, int Y, int Scale, bool Used);
}
