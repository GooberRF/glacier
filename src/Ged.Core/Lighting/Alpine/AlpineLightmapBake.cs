using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>One surface's chart in a bake: its refinement and where its tiles start.</summary>
/// <param name="KU">Refinement along the surface's lightmap U axis (0 = no chart).</param>
/// <param name="KV">Refinement along V (0 = no chart).</param>
/// <param name="Geometry">The derived chart geometry (empty for no chart).</param>
/// <param name="TileBase">The chart's first tile in the tile table.</param>
public readonly record struct AlpineSurfaceChart(ushort KU, ushort KV, AlpineChartGeometry Geometry, int TileBase);

/// <summary>
/// The result of one Alpine chart bake: the 0x0AFBAE09 section body Glacier writes for the geometry it was baked for,
/// with its fingerprint fields still zero (they are stamped at save, from the surface records as written —
/// editor_patch/alpine_lightmaps.cpp:1872-1883), plus what the viewport and the reports need.
/// </summary>
public sealed class AlpineLightmapBake
{
    /// <summary>
    /// The static geometry the charts are positional over (a compile binds it once the geometry is assembled, after
    /// the bake; until then null).
    /// </summary>
    public Geometry? Geometry { get; internal set; }

    /// <summary>A token over the surfaces' fragment rects and pages at bake time (<see cref="SurfacesSignature"/>).</summary>
    public required uint Signature { get; init; }

    /// <summary>The section body (header, charts, tables, tiles, layer directory, payload), fingerprints zero.</summary>
    public required byte[] Body { get; init; }

    /// <summary>Per geometry surface, positionally.</summary>
    public required AlpineSurfaceChart[] Charts { get; init; }

    /// <summary>The tile table.</summary>
    public required AlpineTile[] Tiles { get; init; }

    /// <summary>The decoded pages, RGB8, <see cref="AlpineLightmapLayout.PageSize"/>² each.</summary>
    public required byte[][] Pages { get; init; }

    /// <summary>The stock page edge the surfaces were packed at (128 or 256).</summary>
    public required int StockPage { get; init; }

    /// <summary>The density (texels per world unit) the charts were baked at.</summary>
    public required int Density { get; init; }

    /// <summary>The density the bake started from (the level's setting).</summary>
    public required int RequestedDensity { get; init; }

    /// <summary>How many times the density was halved to fit the page budget.</summary>
    public int DensityHalvings { get; init; }

    /// <summary>Surfaces that got a chart.</summary>
    public int ChartedSurfaces { get; init; }

    /// <summary>Charts spread over more than one tile.</summary>
    public int TiledCharts { get; init; }

    /// <summary>Chart texels (interiors) over all charts.</summary>
    public long ChartTexels { get; init; }

    /// <summary>The mover table's records, in table order (empty when no mover got charts).</summary>
    public IReadOnlyList<AlpineMoverCharts> Movers { get; init; } = Array.Empty<AlpineMoverCharts>();

    /// <summary>The density the mover charts were baked at (0 without movers).</summary>
    public int MoverDensity { get; init; }

    /// <summary>The terrain table's records, in table order (empty when no terrain was lit).</summary>
    public IReadOnlyList<AlpineTerrainChartRecord> Terrains { get; init; } = Array.Empty<AlpineTerrainChartRecord>();

    /// <summary>The first terrain chart's tile; the terrain charts' tiles follow each other in record order.</summary>
    public int FirstTerrainTile { get; init; }

    /// <summary>The tile base of each terrain record, in table order.</summary>
    public IEnumerable<(AlpineTerrainChartRecord Chart, int TileBase)> TerrainTiles()
    {
        int at = FirstTerrainTile;
        foreach (AlpineTerrainChartRecord c in Terrains)
        {
            yield return (c, at);
            at += c.Geometry.TileCount;
        }
    }

    /// <summary>The layer codec the section was written with.</summary>
    public AlpineLightmapCodec Codec { get; init; } = AlpineLightmapCodec.Bc7;

    /// <summary>The level's compression mode the BC7 layer was encoded at.</summary>
    public AlpineLightmapCompression Compression { get; init; }

    /// <summary>BC7 blocks the rate-distortion pass changed.</summary>
    public int RdoModifiedBlocks { get; init; }

    /// <summary>Lines for the bake log.</summary>
    public List<string> Log { get; } = new();

    /// <summary>
    /// Not a wire value: a token saying the surfaces the charts were derived from are still the ones being saved —
    /// each surface's fragment rect and its page's size, as Alpine RED's <c>surfaces_signature</c>
    /// (alpine_lightmaps.cpp:209-234) — plus the page index, which Glacier's repack may also change.
    /// </summary>
    public static uint SurfacesSignature(IReadOnlyList<Surface> surfaces, IReadOnlyList<Lightmap>? pages)
    {
        var token = new byte[surfaces.Count * 4 * 7];
        int o = 0;
        void Put(int v)
        {
            BitConverter.TryWriteBytes(token.AsSpan(o, 4), v);
            o += 4;
        }

        foreach (Surface s in surfaces)
        {
            Lightmap? page = pages is not null && (uint)s.LightmapIndex < (uint)pages.Count ? pages[s.LightmapIndex] : null;
            Put(s.X);
            Put(s.Y);
            Put(s.W);
            Put(s.H);
            Put(page?.Width ?? 0);
            Put(page?.Height ?? 0);
            Put(s.LightmapIndex);
        }

        return Xxh32.Hash(token);
    }
}
