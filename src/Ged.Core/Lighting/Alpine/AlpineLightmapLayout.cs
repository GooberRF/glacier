using System;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// The derived geometry of one Alpine lightmap chart: its texel extent and how it is tiled
/// (common/include/common/lightmap/alpine_lightmap.h:325-337). Nothing here is stored on the wire except through
/// the chart's refinement factors; readers derive the same numbers.
/// </summary>
/// <param name="Cw">Chart texel width, (surface.w − 2)·k_u.</param>
/// <param name="Ch">Chart texel height, (surface.h − 2)·k_v.</param>
/// <param name="Nx">Tiles across.</param>
/// <param name="Ny">Tiles down.</param>
/// <param name="PadU">Stored texels of border/overlap on each side horizontally (1 for a single tile, else the gutter).</param>
/// <param name="PadV">The same vertically.</param>
public readonly record struct AlpineChartGeometry(int Cw, int Ch, int Nx, int Ny, int PadU, int PadV)
{
    /// <summary>Tiles this chart owns.</summary>
    public int TileCount => Nx * Ny;

    /// <summary>True for "no chart" (k = 0 on the wire, or a fragment no RFL can store).</summary>
    public bool IsEmpty => Cw == 0 || Ch == 0;
}

/// <summary>One tile's interior and stored size (alpine_lightmap.h:422-442).</summary>
/// <param name="InteriorW">Chart texels this tile owns horizontally.</param>
/// <param name="InteriorH">Chart texels this tile owns vertically.</param>
/// <param name="StoredW">Interior plus padding, rounded up to a 4-texel block.</param>
/// <param name="StoredH">The same vertically.</param>
public readonly record struct AlpineTileDims(int InteriorW, int InteriorH, int StoredW, int StoredH);

/// <summary>A tile's place in the atlas (alpine_lightmap.h:227-232).</summary>
public readonly record struct AlpineTile(ushort Page, ushort X, ushort Y);

/// <summary>
/// The constants and pure derivations of Alpine Faction's lightmap section (RFL chunk 0x0AFBAE09), reimplemented from
/// common/include/common/lightmap/alpine_lightmap.h — the single derivation the RED writer and the game reader share.
/// Every function cites the line range it follows; no Alpine code is copied.
/// </summary>
public static class AlpineLightmapLayout
{
    /// <summary>SectionHeader::version this writer produces and the 1.5 reader accepts (alpine_lightmap.h:22).</summary>
    public const uint SectionVersion = 2;

    /// <summary>LayerDirEntry::layer_version (alpine_lightmap.h:23).</summary>
    public const ushort LayerVersion = 1;

    /// <summary>P, the edge of every page (alpine_lightmap.h:25).</summary>
    public const int PageSize = 256;

    /// <summary>S, the chart texel step between tile origins (alpine_lightmap.h:26).</summary>
    public const int TileStep = 244;

    /// <summary>G, the overlap a tiled chart stores on each side (alpine_lightmap.h:27).</summary>
    public const int Gutter = 4;

    /// <summary>The most pages a section may hold (alpine_lightmap.h:33).</summary>
    public const int MaxPages = 2048;

    /// <summary>Page budget of the surface, mover and terrain charts, in BC7 pages (alpine_lightmap.h:35).</summary>
    public const int StagePageBudget = 1024;

    /// <summary>Bytes of one serialized GSurface record; the surface fingerprint hashes these (alpine_lightmap.h:278).</summary>
    public const int SurfaceRecordSize = 96;

    /// <summary>The largest fragment edge an RFL stores (u8, alpine_lightmap.h:271-275).</summary>
    public const int MaxFragmentDim = 255;

    /// <summary>Charts above this edge are invalid (alpine_lightmap.h:339-340).</summary>
    public const long MaxChartDim = 1L << 20;

    /// <summary>Level property <c>lightmap_density</c> = 0: the default 8 texels per world unit (alpine_lightmap.h:66-73).</summary>
    public const byte DensityDefault = 8;

    public const byte DensityMin = 1;

    public const byte DensityMax = 128;

    /// <summary>Level property <c>lightmap_density</c> = 255: Alpine surface charts off.</summary>
    public const byte DensityOff = 255;

    /// <summary>Bytes of one raw RGB8 page (alpine_lightmap.h:764-775).</summary>
    public const int RawPageBytes = PageSize * PageSize * 3;

    /// <summary>Bytes of one BC7 page.</summary>
    public const int Bc7PageBytes = (PageSize / 4) * (PageSize / 4) * 16;

    /// <summary>The density actually baked for a stored property value (alpine_lightmap.h:76-79).</summary>
    public static byte EffectiveDensity(byte stored) =>
        stored == 0 ? DensityDefault : Math.Clamp(stored, DensityMin, DensityMax);

    /// <summary>The stock page edge (128, or 256 with high-resolution lightmaps) (alpine_lightmap.h:39-56).</summary>
    public static bool StockPageEdgeValid(int edge) => edge is 128 or 256;

    /// <summary>SectionHeader::stock_page_log2 of a stock page edge (alpine_lightmap.h:48-51).</summary>
    public static byte StockPageLog2Of(int edge) => edge == 256 ? (byte)8 : (byte)7;

    /// <summary>
    /// The page budget of the surface stage in pages of the raw codec: as many raw pages as a BC7 atlas of
    /// <see cref="StagePageBudget"/> pages holds in bytes (alpine_lightmap.h:788-795).
    /// </summary>
    public static int StageRawPages =>
        (int)Math.Min(StagePageBudget, (long)StagePageBudget * Bc7PageBytes / RawPageBytes);

    /// <summary>align4 (alpine_lightmap.h:315-318).</summary>
    public static int Align4(int v) => (v + 3) & ~3;

    private static int CeilDiv(int a, int b) => b == 0 ? 0 : (a + b - 1) / b;

    /// <summary>The tiling of a cw × ch chart (alpine_lightmap.h:342-359).</summary>
    public static AlpineChartGeometry FromExtent(long cw, long ch)
    {
        if (cw <= 0 || ch <= 0 || cw > MaxChartDim || ch > MaxChartDim)
        {
            return default;
        }

        int w = (int)cw, h = (int)ch;
        int nx = CeilDiv(w, TileStep);
        int ny = CeilDiv(h, TileStep);
        return new AlpineChartGeometry(w, h, nx, ny, nx == 1 ? 1 : Gutter, ny == 1 ? 1 : Gutter);
    }

    /// <summary>
    /// A surface chart: the stock fragment interior refined k times per axis (alpine_lightmap.h:361-373). Empty for
    /// no chart (k = 0) and for a fragment of two texels or fewer.
    /// </summary>
    public static AlpineChartGeometry SurfaceChart(int surfaceW, int surfaceH, int kU, int kV)
    {
        if (surfaceW <= 2 || surfaceH <= 2 || kU <= 0 || kV <= 0 || surfaceW > MaxFragmentDim || surfaceH > MaxFragmentDim)
        {
            return default;
        }

        return FromExtent((long)(surfaceW - 2) * kU, (long)(surfaceH - 2) * kV);
    }

    /// <summary>Chart coordinate of a tile's stored texel (0, 0), padding included (alpine_lightmap.h:415-420).</summary>
    public static (int U, int V) TileOrigin(in AlpineChartGeometry g, int tx, int ty) =>
        ((tx * TileStep) - g.PadU, (ty * TileStep) - g.PadV);

    /// <summary>A tile's interior and stored size (alpine_lightmap.h:430-442).</summary>
    public static AlpineTileDims TileDims(in AlpineChartGeometry g, int tx, int ty)
    {
        if (tx >= g.Nx || ty >= g.Ny)
        {
            return default;
        }

        int iw = Math.Min(TileStep, g.Cw - (tx * TileStep));
        int ih = Math.Min(TileStep, g.Ch - (ty * TileStep));
        return new AlpineTileDims(iw, ih, Align4(iw + (2 * g.PadU)), Align4(ih + (2 * g.PadV)));
    }

    /// <summary>A chart's tile index of tile (tx, ty): row major from the chart's base (alpine_lightmap.h:460-464).</summary>
    public static int TileIndex(int baseIndex, in AlpineChartGeometry g, int tx, int ty) => baseIndex + (ty * g.Nx) + tx;

    /// <summary>
    /// Which tile owns integer chart texel (cu, cv), padding included, and where that tile stores it: the owner is
    /// chosen on the coordinate clamped into the chart (alpine_lightmap.h:544-574).
    /// </summary>
    public static (int Index, int Sx, int Sy, bool Valid) TileSlot(in AlpineChartGeometry g, int baseIndex, int cu, int cv)
    {
        if (g.Nx == 0 || g.Ny == 0)
        {
            return default;
        }

        int ccu = Math.Clamp(cu, 0, g.Cw - 1);
        int ccv = Math.Clamp(cv, 0, g.Ch - 1);
        int tx = Math.Min(ccu / TileStep, g.Nx - 1);
        int ty = Math.Min(ccv / TileStep, g.Ny - 1);
        AlpineTileDims td = TileDims(g, tx, ty);
        (int ou, int ov) = TileOrigin(g, tx, ty);
        int sx = cu - ou, sy = cv - ov;
        return (TileIndex(baseIndex, g, tx, ty), sx, sy, sx >= 0 && sy >= 0 && sx < td.StoredW && sy < td.StoredH);
    }

    /// <summary>
    /// Tile selection for a chart texel coordinate, clamped in the float domain (NaN lands in tile 0)
    /// (alpine_lightmap.h:493-515).
    /// </summary>
    public static (int Tx, int Ty) SelectTile(in AlpineChartGeometry g, float u, float v)
    {
        if (g.Nx == 0 || g.Ny == 0)
        {
            return (0, 0);
        }

        static int Axis(float c, int n)
        {
            const float fs = TileStep;
            if (!(c >= fs))
            {
                return 0;
            }

            float t = MathF.Floor(c / fs);
            return !(t < n - 1) ? n - 1 : (int)t;
        }

        return (Axis(u, g.Nx), Axis(v, g.Ny));
    }

    /// <summary>
    /// The refinement of one stock axis — <paramref name="surfaceDim"/> texels including the border over
    /// <paramref name="extent"/> world units — that reaches <paramref name="density"/> texels per unit; never 0
    /// (alpine_lightmap.h:732-745). Rounds half away from zero like <c>std::lround</c>.
    /// </summary>
    public static ushort KFromDensity(int surfaceDim, float extent, float density)
    {
        if (surfaceDim <= 2 || !(extent > 0f) || !(density > 0f))
        {
            return 1;
        }

        double dStock = (surfaceDim - 2) / (double)extent;
        if (!(dStock > 0.0))
        {
            return 1;
        }

        double k = Math.Round(density / dStock, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(k, 1.0, 65535.0);
    }

    /// <summary>
    /// Stock page UV → chart texel coordinate (alpine_lightmap.h:466-484): <c>(page_u·lm_w − surf_x − 1)·k_u</c>.
    /// </summary>
    public static (float U, float V) ChartTexelFromPageUv(
        float pageU, float pageV, int lmW, int lmH, int surfX, int surfY, int kU, int kV) =>
        (((pageU * lmW) - surfX - 1f) * kU, ((pageV * lmH) - surfY - 1f) * kV);
}
