using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>What Alpine Faction's lightmap reader makes of a section (alpine_lightmap_reader.h:43-95).</summary>
public sealed class AlpineLightmapReadResult
{
    /// <summary>At least one part (surface charts, terrain, movers, overflow) is usable and a supported layer was found.</summary>
    public bool Ok { get; internal set; }

    /// <summary>Why the section is refused, when <see cref="Ok"/> is false.</summary>
    public string Reason { get; internal set; } = string.Empty;

    /// <summary>The surface charts are usable against the geometry they were checked against.</summary>
    public bool SurfacesOk { get; internal set; }

    /// <summary>Why the surface charts are not usable.</summary>
    public string SurfaceReason { get; internal set; } = string.Empty;

    public uint Version { get; internal set; }

    public int PageCount { get; internal set; }

    public byte BaseDensity { get; internal set; }

    public byte StockPageLog2 { get; internal set; }

    public uint SourceFaces { get; internal set; }

    public uint SourceSurfaces { get; internal set; }

    public uint SourceHash { get; internal set; }

    /// <summary>(k_u, k_v) per chart as stored.</summary>
    public List<(ushort KU, ushort KV)> Charts { get; } = new();

    /// <summary>Derived chart geometry (empty unless <see cref="SurfacesOk"/>).</summary>
    public List<AlpineChartGeometry> Geometries { get; } = new();

    /// <summary>First tile of each chart (empty unless <see cref="SurfacesOk"/>).</summary>
    public List<int> Bases { get; } = new();

    public List<AlpineTile> Tiles { get; } = new();

    /// <summary>Tiles the directory's tables own, after the surface tiles.</summary>
    public long TableTiles { get; internal set; }

    /// <summary>Tags of the tables in the directory, in order.</summary>
    public List<uint> TableTags { get; } = new();

    public ushort LayerCodec { get; internal set; }

    public byte LayerCompression { get; internal set; }

    public int LayerOffset { get; internal set; }

    public uint LayerStoredSize { get; internal set; }

    public uint LayerUncompressedSize { get; internal set; }

    /// <summary>The terrain table's records as stored (empty without a usable terrain table; alpine_lightmap_reader.h:65-71).</summary>
    public List<AlpineTerrainChartRecord> Terrains { get; } = new();

    /// <summary>Absolute first tile of each terrain record.</summary>
    public List<int> TerrainBases { get; } = new();

    /// <summary>Per terrain record: usable.</summary>
    public List<bool> TerrainOk { get; } = new();

    /// <summary>Why the terrain table's layout is unknown (empty when it parsed or is absent).</summary>
    public string TerrainReason { get; internal set; } = string.Empty;
}

/// <summary>
/// Glacier's port of the validation Alpine Faction's lightmap reader runs, in the reader's order
/// (common/include/common/lightmap/alpine_lightmap_reader.h:128-188 header and tile checks, :207-260 the surface half,
/// :541-765 <c>read_section</c>). The game and Alpine RED both call it; a section it refuses is ignored and the level
/// draws its stock lightmaps (game_patch/graphics/af_lightmap.cpp:393-550). The mover, terrain and overflow tables are
/// parsed structurally only (their records validate against data Glacier does not bake yet) — a section that carries
/// none, as every section this build writes, is checked in full.
/// </summary>
public static class AlpineLightmapValidator
{
    private const int MaxTables = 16;
    private const long MaxLayerBytes = (long)AlpineLightmapLayout.MaxPages * AlpineLightmapLayout.Bc7PageBytes;

    /// <summary>
    /// Runs the reader's checks on <paramref name="data"/> against the static geometry the level loads: its surfaces'
    /// fragment sizes and the XXH32 of their records (<paramref name="geometry"/>; null checks the structure only, as
    /// Alpine RED does for a terrain-only read — alpine_lightmaps.cpp:1628-1632).
    /// </summary>
    public static AlpineLightmapReadResult Read(ReadOnlySpan<byte> data, Geometry? geometry)
    {
        var r = new AlpineLightmapReadResult();
        if (data.Length < AlpineLightmapWriter.HeaderSize)
        {
            return Fail(r, "truncated header");
        }

        uint version = U32(data, 0);
        ushort pageSize = U16(data, 8), tileStep = U16(data, 10);
        byte gutter = data[12];
        r.Version = version;
        r.BaseDensity = data[13];
        r.StockPageLog2 = data[14];
        int numPages = U16(data, 16);
        uint numCharts = U32(data, 18), numTiles = U32(data, 22);
        r.PageCount = numPages;
        r.SourceFaces = U32(data, 26);
        r.SourceSurfaces = U32(data, 30);
        r.SourceHash = U32(data, 34);

        // header_invalid_reason (alpine_lightmap_reader.h:130-145)
        if (version != AlpineLightmapLayout.SectionVersion)
        {
            return Fail(r, "unsupported container version");
        }

        if (pageSize != AlpineLightmapLayout.PageSize || tileStep != AlpineLightmapLayout.TileStep || gutter != AlpineLightmapLayout.Gutter)
        {
            return Fail(r, "bad atlas geometry");
        }

        if (numPages == 0 || numPages > AlpineLightmapLayout.MaxPages)
        {
            return Fail(r, "bad page count");
        }

        if (r.StockPageLog2 is < 7 or > 8)
        {
            return Fail(r, "bad stock page size");
        }

        long dirOff = AlpineLightmapWriter.HeaderSize + ((long)numCharts * 4);
        if (dirOff + 4 > data.Length)
        {
            return Fail(r, "truncated chart/tile tables");
        }

        uint numTables = U32(data, (int)dirOff);
        if (numTables > MaxTables)
        {
            return Fail(r, "too many tables");
        }

        long dirEnd = dirOff + 4;
        var tables = new List<(uint Tag, ushort Version, long BodyOff, uint ByteLen, uint Tiles, long FirstTile)>();
        for (uint k = 0; k < numTables; k++)
        {
            if (dirEnd + 16 > data.Length)
            {
                return Fail(r, "truncated table directory");
            }

            uint tag = U32(data, (int)dirEnd);
            ushort tableVersion = U16(data, (int)dirEnd + 4);
            uint byteLen = U32(data, (int)dirEnd + 8), tableTiles = U32(data, (int)dirEnd + 12);
            dirEnd += 16;
            tables.Add((tag, tableVersion, dirEnd, byteLen, tableTiles, r.TableTiles));
            if (byteLen > data.Length - dirEnd)
            {
                return Fail(r, "truncated table directory");
            }

            r.TableTags.Add(tag);
            dirEnd += byteLen;
            r.TableTiles += tableTiles;
            if (r.TableTiles > numTiles)
            {
                return Fail(r, "table tiles exceed the tile table");
            }
        }

        long tablesEnd = dirEnd + ((long)numTiles * 6);
        if (tablesEnd > data.Length)
        {
            return Fail(r, "truncated chart/tile tables");
        }

        long tilesPerPage = (long)(pageSize / 4) * (pageSize / 4);
        if (numTiles > numPages * tilesPerPage)
        {
            return Fail(r, "tile count exceeds what the pages can hold");
        }

        for (int i = 0; i < numCharts; i++)
        {
            int o = AlpineLightmapWriter.HeaderSize + (i * 4);
            r.Charts.Add((U16(data, o), U16(data, o + 2)));
        }

        int off = (int)dirEnd;
        for (int i = 0; i < numTiles; i++)
        {
            r.Tiles.Add(new AlpineTile(U16(data, off), U16(data, off + 2), U16(data, off + 4)));
            off += 6;
        }

        // The terrain table (read_section :653-662, parse_terrain :370-431): exactly one at version 1, its tiles after
        // the surface tiles and the tables before it.
        long surfaceEnd = numTiles - r.TableTiles;
        int terrainTables = tables.Count(t => t.Tag == AlpineTerrainCharts.TableTag);
        if (terrainTables > 1)
        {
            r.TerrainReason = "more than one terrain table";
        }
        else if (terrainTables == 1)
        {
            var t = tables.First(x => x.Tag == AlpineTerrainCharts.TableTag);
            r.TerrainReason = t.Version != AlpineTerrainCharts.TableVersion
                ? "unsupported terrain table version"
                : ParseTerrain(r, data, t.BodyOff, t.ByteLen, t.Tiles, (int)(surfaceEnd + t.FirstTile)) ?? string.Empty;
            if (r.TerrainReason.Length > 0)
            {
                r.Terrains.Clear();
                r.TerrainBases.Clear();
                r.TerrainOk.Clear();
            }
        }

        string? surfaceReason = ValidateSurfaces(r, geometry, numCharts, numTiles);
        r.SurfacesOk = surfaceReason is null;
        r.SurfaceReason = surfaceReason ?? string.Empty;
        if (!r.SurfacesOk)
        {
            r.Geometries.Clear();
            r.Bases.Clear();

            // No table is validated here, so with the surface charts unusable nothing is (read_section :700-714).
            if (r.TableTags.Count == 0)
            {
                return Fail(r, r.SurfaceReason);
            }
        }

        if (off + 4 > data.Length)
        {
            return Fail(r, "truncated layer directory");
        }

        int numLayers = data[off];
        off += 4;
        if (numLayers == 0)
        {
            return Fail(r, "no layers");
        }

        if (off + ((long)numLayers * 16) > data.Length)
        {
            return Fail(r, "truncated layer directory");
        }

        var entries = new (ushort Semantic, ushort Codec, ushort Version, byte Colorspace, byte Compression, uint Uncompressed, uint Stored)[numLayers];
        for (int i = 0; i < numLayers; i++)
        {
            entries[i] = (U16(data, off), U16(data, off + 2), U16(data, off + 4), data[off + 6], data[off + 7],
                U32(data, off + 8), U32(data, off + 12));
            off += 16;
        }

        bool picked = false;
        long pos = off;
        foreach (var e in entries)
        {
            if (pos + e.Stored > data.Length)
            {
                return Fail(r, "truncated layer payload");
            }

            bool supported = e.Semantic == 0 && (e.Codec == 0 || e.Codec == 1) && e.Version == AlpineLightmapLayout.LayerVersion
                && e.Colorspace == 0 && (e.Compression == 0 || e.Compression == 1);
            if (!picked && supported)
            {
                long want = e.Codec == 0
                    ? (long)numPages * AlpineLightmapLayout.RawPageBytes
                    : (long)numPages * AlpineLightmapLayout.Bc7PageBytes;
                if (want == 0 || want > MaxLayerBytes || want != e.Uncompressed)
                {
                    return Fail(r, "layer payload size");
                }

                if (e.Compression == 0 && e.Stored != e.Uncompressed)
                {
                    return Fail(r, "layer payload size");
                }

                r.LayerCodec = e.Codec;
                r.LayerCompression = e.Compression;
                r.LayerOffset = (int)pos;
                r.LayerStoredSize = e.Stored;
                r.LayerUncompressedSize = e.Uncompressed;
                picked = true;
            }

            pos += e.Stored;
        }

        if (!picked)
        {
            return Fail(r, "no supported layer");
        }

        r.Ok = true;
        r.Reason = string.Empty;
        return r;
    }

    /// <summary>
    /// Decompresses the picked layer as <c>decompress_layer</c> does (alpine_lightmap_decode.h:24-54): exactly
    /// <c>uncompressed_size</c> bytes or nothing.
    /// </summary>
    public static byte[]? DecodeLayer(ReadOnlySpan<byte> data, AlpineLightmapReadResult r)
    {
        if (!r.Ok || r.LayerOffset < 0 || r.LayerOffset + r.LayerStoredSize > data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> payload = data.Slice(r.LayerOffset, (int)r.LayerStoredSize);
        if (r.LayerCompression == 0)
        {
            return payload.ToArray();
        }

        try
        {
            using var src = new MemoryStream(payload.ToArray());
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            var outBuf = new byte[r.LayerUncompressedSize];
            int got = 0;
            while (got < outBuf.Length)
            {
                int n = z.Read(outBuf, got, outBuf.Length - got);
                if (n <= 0)
                {
                    return null;
                }

                got += n;
            }

            return z.ReadByte() >= 0 ? null : outBuf;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // parse_terrain (alpine_lightmap_reader.h:370-431): null when the layout is known (each record then usable or not).
    private static string? ParseTerrain(AlpineLightmapReadResult r, ReadOnlySpan<byte> data, long bodyOff, uint byteLen, uint tableTiles, int firstTile)
    {
        if (byteLen < 4)
        {
            return "truncated terrain table";
        }

        uint n = U32(data, (int)bodyOff);
        if (n > AlpineTerrainCharts.MaxCharts)
        {
            return "too many terrain charts";
        }

        if (byteLen != 4 + (n * AlpineTerrainChartRecord.WireSize))
        {
            return "terrain table length";
        }

        long tiles = 0, texels = 0;
        for (int i = 0; i < n; i++)
        {
            int o = (int)bodyOff + 4 + (i * AlpineTerrainChartRecord.WireSize);
            var c = new AlpineTerrainChartRecord(
                BinaryPrimitives.ReadInt32LittleEndian(data[o..]), U16(data, o + 4), U16(data, o + 6),
                BinaryPrimitives.ReadSingleLittleEndian(data[(o + 8)..]), BinaryPrimitives.ReadSingleLittleEndian(data[(o + 12)..]),
                BinaryPrimitives.ReadSingleLittleEndian(data[(o + 16)..]), BinaryPrimitives.ReadUInt64LittleEndian(data[(o + 20)..]));
            AlpineChartGeometry g = c.Geometry;
            if (g.IsEmpty)
            {
                return "terrain chart size";
            }

            r.Terrains.Add(c);
            r.TerrainBases.Add(firstTile + (int)tiles);
            tiles += (long)g.Nx * g.Ny;
            texels += (long)c.W * c.H;
            if (tiles > tableTiles)
            {
                return "terrain tiles do not match their table";
            }
        }

        if (tiles != tableTiles)
        {
            return "terrain tiles do not match their table";
        }

        if (texels > AlpineTerrainCharts.MaxChartTexels || texels > (long)r.PageCount * AlpineLightmapLayout.PageSize * AlpineLightmapLayout.PageSize)
        {
            return "terrain charts exceed the page budget";
        }

        for (int i = 0; i < r.Terrains.Count; i++)
        {
            AlpineTerrainChartRecord c = r.Terrains[i];
            bool usable = float.IsFinite(c.OriginX) && float.IsFinite(c.OriginZ) && float.IsFinite(c.TexelSize) && c.TexelSize > 0f
                && TilesOnPages(r, c.Geometry, r.TerrainBases[i]);
            for (int j = 0; j < r.Terrains.Count; j++)
            {
                usable &= j == i || r.Terrains[j].TerrainUid != c.TerrainUid;
            }

            r.TerrainOk.Add(usable);
        }

        return null;
    }

    /// <summary>
    /// Whether terrain chart <paramref name="c"/> lights <paramref name="t"/> as it is now: it covers the terrain's cells at
    /// a whole density of 1..8 and was baked for its chart fingerprint (alpine_lightmap_reader.h:1014-1041).
    /// </summary>
    public static bool TerrainChartMatches(AlpineTerrainChartRecord c, AlpineTerrain t)
    {
        int cx = t.Grid.CellsX, cz = t.Grid.CellsZ;
        if (cx == 0 || cz == 0 || c.TerrainUid != t.Uid)
        {
            return false;
        }

        int density = c.W / cx;
        return density >= 1 && density <= AlpineTerrainRules.LightmapDensityMax && c.W == cx * density && c.H == cz * density
            && c.Fingerprint == AlpineTerrainSurface.ChartFingerprint(t);
    }

    // validate_surfaces (alpine_lightmap_reader.h:207-260).
    private static string? ValidateSurfaces(AlpineLightmapReadResult r, Geometry? g, uint numCharts, uint numTiles)
    {
        if (g is null)
        {
            return "surface fingerprint unavailable";
        }

        uint numSurfaces = (uint)g.Surfaces.Count;
        if (numCharts != numSurfaces)
        {
            return "chart count does not match the loaded geometry";
        }

        if (r.SourceSurfaces != numSurfaces)
        {
            return "surface count fingerprint";
        }

        if (r.SourceHash != AlpineLightmapWriter.SurfaceHash(g))
        {
            return "surface fingerprint";
        }

        if (r.TableTiles > numTiles)
        {
            return "tile count does not match the derived atlas";
        }

        long surfaceTiles = numTiles - r.TableTiles;
        long derived = 0;
        for (int i = 0; i < numCharts; i++)
        {
            (ushort ku, ushort kv) = r.Charts[i];
            AlpineChartGeometry geom = ku == 0 || kv == 0 ? default : AlpineLightmapLayout.SurfaceChart(g.Surfaces[i].W, g.Surfaces[i].H, ku, kv);
            r.Geometries.Add(geom);
            r.Bases.Add((int)derived);
            derived += (long)geom.Nx * geom.Ny;
            if (derived > surfaceTiles)
            {
                return "tile count does not match the derived atlas";
            }
        }

        if (derived != surfaceTiles)
        {
            return "tile count does not match the derived atlas";
        }

        for (int i = 0; i < numCharts; i++)
        {
            if (!TilesOnPages(r, r.Geometries[i], r.Bases[i]))
            {
                return "tile rect leaves its page";
            }
        }

        return null;
    }

    // chart_tiles_on_pages (alpine_lightmap_reader.h:170-188).
    private static bool TilesOnPages(AlpineLightmapReadResult r, in AlpineChartGeometry g, int baseIndex)
    {
        for (int ty = 0; ty < g.Ny; ty++)
        {
            for (int tx = 0; tx < g.Nx; tx++)
            {
                int idx = AlpineLightmapLayout.TileIndex(baseIndex, g, tx, ty);
                if (idx >= r.Tiles.Count)
                {
                    return false;
                }

                AlpineTile t = r.Tiles[idx];
                AlpineTileDims td = AlpineLightmapLayout.TileDims(g, tx, ty);
                if (t.Page >= r.PageCount || t.X + td.StoredW > AlpineLightmapLayout.PageSize || t.Y + td.StoredH > AlpineLightmapLayout.PageSize)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static AlpineLightmapReadResult Fail(AlpineLightmapReadResult r, string reason)
    {
        r.Ok = false;
        r.Reason = reason;
        return r;
    }

    private static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
}
