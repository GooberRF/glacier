using System;
using System.Collections.Generic;

namespace Ged.Core.Lighting.Alpine;

/// <summary>The outcome of packing a chart table (editor_patch/alpine_lightmaps.cpp:365-370).</summary>
public enum AlpinePackResult
{
    Ok,
    NoCharts,
    OverBudget,
}

/// <summary>
/// Alpine RED's deterministic atlas packer, reimplemented from editor_patch/alpine_lightmaps.cpp:236-348 (the skyline
/// packer) and :440-504 (<c>pack_charts</c>): one page is open at a time, a tile that does not fit closes it; tiles are
/// placed tallest first, then widest, then in tile-table order, so the placement is a pure function of the chart
/// table. Glacier packs exactly as Alpine RED does, so a chart table yields the same tile table in both.
/// </summary>
public static class AlpineAtlasPacker
{
    /// <summary>
    /// Lays every chart's tiles out in pages. <paramref name="bases"/> receives each chart's first tile index (the
    /// running sum of tile counts in chart order, as the reader derives it); <paramref name="tiles"/> the tile table.
    /// </summary>
    public static AlpinePackResult Pack(
        IReadOnlyList<AlpineChartGeometry> charts, int pageBudget, out int[] bases, out AlpineTile[] tiles, out int pages)
    {
        bases = new int[charts.Count];
        int total = 0;
        for (int i = 0; i < charts.Count; i++)
        {
            bases[i] = total;
            total += charts[i].TileCount;
        }

        pages = 0;
        tiles = new AlpineTile[total];
        if (total == 0)
        {
            return AlpinePackResult.NoCharts;
        }

        var order = new List<(int Index, int W, int H)>(total);
        for (int i = 0; i < charts.Count; i++)
        {
            AlpineChartGeometry g = charts[i];
            for (int ty = 0; ty < g.Ny; ty++)
            {
                for (int tx = 0; tx < g.Nx; tx++)
                {
                    AlpineTileDims td = AlpineLightmapLayout.TileDims(g, tx, ty);
                    order.Add((AlpineLightmapLayout.TileIndex(bases[i], g, tx, ty), td.StoredW, td.StoredH));
                }
            }
        }

        // Height descending, width descending, tile index ascending: a total order, so the sort is stable by
        // construction (alpine_lightmaps.cpp:480-488).
        order.Sort((a, b) => a.H != b.H ? b.H.CompareTo(a.H) : a.W != b.W ? b.W.CompareTo(a.W) : a.Index.CompareTo(b.Index));

        var packer = new SkylinePacker();
        foreach ((int index, int w, int h) in order)
        {
            if (!packer.Place(w, h, out ushort x, out ushort y, out ushort page))
            {
                return AlpinePackResult.OverBudget;
            }

            tiles[index] = new AlpineTile(page, x, y);
        }

        pages = packer.Pages;
        return pages > pageBudget || pages > 0xFFFF ? AlpinePackResult.OverBudget : AlpinePackResult.Ok;
    }

    /// <summary>The skyline over one open page (alpine_lightmaps.cpp:240-348).</summary>
    private sealed class SkylinePacker
    {
        private readonly List<(int X, int Y, int W)> _nodes = new();

        public SkylinePacker() => NewPage();

        public int Pages { get; private set; }

        public bool Place(int w, int h, out ushort x, out ushort y, out ushort page)
        {
            x = y = page = 0;
            if (w <= 0 || h <= 0 || w > AlpineLightmapLayout.PageSize || h > AlpineLightmapLayout.PageSize)
            {
                return false;
            }

            if (!TryPlace(w, h, out x, out y))
            {
                NewPage();
                if (!TryPlace(w, h, out x, out y))
                {
                    return false;
                }
            }

            page = (ushort)(Pages - 1);
            return true;
        }

        private void NewPage()
        {
            _nodes.Clear();
            _nodes.Add((0, 0, AlpineLightmapLayout.PageSize));
            Pages++;
        }

        // Lowest y wins, then leftmost x (alpine_lightmaps.cpp:280-303).
        private bool TryPlace(int w, int h, out ushort outX, out ushort outY)
        {
            outX = outY = 0;
            int best = _nodes.Count;
            int bestY = AlpineLightmapLayout.PageSize + 1;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!Fits(i, w, out int y) || y + h > AlpineLightmapLayout.PageSize)
                {
                    continue;
                }

                if (y < bestY)
                {
                    best = i;
                    bestY = y;
                }
            }

            if (best == _nodes.Count)
            {
                return false;
            }

            outX = (ushort)_nodes[best].X;
            outY = (ushort)bestY;
            AddNode(best, _nodes[best].X, bestY + h, w);
            return true;
        }

        private bool Fits(int index, int w, out int y)
        {
            y = _nodes[index].Y;
            if (_nodes[index].X + w > AlpineLightmapLayout.PageSize)
            {
                return false;
            }

            int left = w;
            for (int i = index; i < _nodes.Count && left > 0; i++)
            {
                y = Math.Max(y, _nodes[i].Y);
                left = _nodes[i].W >= left ? 0 : left - _nodes[i].W;
            }

            return left == 0;
        }

        // Inserts the new segment, trims the ones it covers, merges equal heights (alpine_lightmaps.cpp:319-344).
        private void AddNode(int index, int x, int y, int w)
        {
            _nodes.Insert(index, (x, y, w));
            for (int i = index + 1; i < _nodes.Count;)
            {
                int prevEnd = _nodes[i - 1].X + _nodes[i - 1].W;
                if (_nodes[i].X >= prevEnd)
                {
                    break;
                }

                int shrink = prevEnd - _nodes[i].X;
                if (_nodes[i].W <= shrink)
                {
                    _nodes.RemoveAt(i);
                    continue;
                }

                _nodes[i] = (_nodes[i].X + shrink, _nodes[i].Y, _nodes[i].W - shrink);
                break;
            }

            for (int i = 0; i + 1 < _nodes.Count;)
            {
                if (_nodes[i].Y == _nodes[i + 1].Y)
                {
                    _nodes[i] = (_nodes[i].X, _nodes[i].Y, _nodes[i].W + _nodes[i + 1].W);
                    _nodes.RemoveAt(i + 1);
                    continue;
                }

                i++;
            }
        }
    }
}
