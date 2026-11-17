using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Ged.Core.IO;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// Serializes an Alpine lightmap section (RFL chunk 0x0AFBAE09) exactly as Alpine RED's <c>build_body</c> lays it
/// out (editor_patch/alpine_lightmaps.cpp:1479-1601; wire records common/include/common/lightmap/alpine_lightmap.h:
/// 120-263): a 38-byte header, one chart per geometry surface, the table directory (the mover table when movers got
/// charts; terrain and overflow tables are not written by this bake), the tile table, a one-entry layer directory and
/// the pages as one zlib stream. The layer is BC7 (<c>codec</c> 1) or raw RGB8 (<c>codec</c> 0), both of which the 1.5
/// reader accepts (alpine_lightmap_reader.h:154-168). The fingerprint fields are written zero and stamped at save
/// (<see cref="Stamp(byte[], Geometry, Func{int, Geometry?}?, bool, IReadOnlyList{AlpineMoverCharts}?)"/>), from the
/// surface records as they are written (alpine_lightmaps.cpp:1872-1883, 1900-1924, 2613-2696).
/// </summary>
public static class AlpineLightmapWriter
{
    /// <summary>Byte offset of SectionHeader::src_num_faces (alpine_lightmap.h:124-140).</summary>
    public const int SourceFacesOffset = 26;

    /// <summary>Byte offset of SectionHeader::src_num_surfaces.</summary>
    public const int SourceSurfacesOffset = 30;

    /// <summary>Byte offset of SectionHeader::src_surface_hash.</summary>
    public const int SourceHashOffset = 34;

    /// <summary>Bytes of SectionHeader.</summary>
    public const int HeaderSize = 38;

    /// <summary>Builds a raw-RGB8 section body with surface charts only (fingerprints zero).</summary>
    public static byte[] BuildBody(
        IReadOnlyList<AlpineSurfaceChart> charts, IReadOnlyList<AlpineTile> tiles, IReadOnlyList<byte[]> pages,
        byte baseDensity, byte stockPageLog2) =>
        BuildBody(charts, Array.Empty<AlpineMoverCharts>(), tiles, pages, AlpineLightmapCodec.RawRgb8, baseDensity, stockPageLog2);

    /// <summary>
    /// Builds the section body (fingerprints zero): <paramref name="layer"/> holds each page in
    /// <paramref name="codec"/>'s form (RGB8 or BC7 blocks). A non-empty <paramref name="movers"/> list is written as the
    /// mover table (tag 1, version 1; alpine_lightmaps.cpp:1556-1575); each record's <c>surface_hash</c> offset is stored
    /// on the record for <see cref="Stamp(byte[], Geometry, Func{int, Geometry?}?, bool)"/>.
    /// </summary>
    public static byte[] BuildBody(
        IReadOnlyList<AlpineSurfaceChart> charts, IReadOnlyList<AlpineMoverCharts> movers, IReadOnlyList<AlpineTile> tiles,
        IReadOnlyList<byte[]> layer, AlpineLightmapCodec codec, byte baseDensity, byte stockPageLog2) =>
        BuildBody(charts, movers, Array.Empty<AlpineTerrainChartRecord>(), tiles, layer, codec, baseDensity, stockPageLog2);

    /// <summary>
    /// The section body with the terrain table too (tag 2, version 1, after the mover table: the tables run in the order
    /// their tiles follow the surface tiles — alpine_lightmaps.cpp:1553-1585). Terrain fingerprints are written as given.
    /// </summary>
    public static byte[] BuildBody(
        IReadOnlyList<AlpineSurfaceChart> charts, IReadOnlyList<AlpineMoverCharts> movers, IReadOnlyList<AlpineTerrainChartRecord> terrains,
        IReadOnlyList<AlpineTile> tiles, IReadOnlyList<byte[]> layer, AlpineLightmapCodec codec, byte baseDensity, byte stockPageLog2)
    {
        long raw = (long)layer.Count * AlpineLayerEncoder.PageBytes(codec);
        if (raw > uint.MaxValue)
        {
            throw new InvalidOperationException("The Alpine lightmap layer does not fit its size field.");
        }

        long moverSurfaces = 0, moverTiles = 0;
        foreach (AlpineMoverCharts m in movers)
        {
            moverSurfaces += m.Charts.Length;
            foreach (AlpineSurfaceChart c in m.Charts)
            {
                moverTiles += c.Geometry.IsEmpty ? 0 : c.Geometry.TileCount;
            }
        }

        byte[] payload = Deflate(layer);
        var w = new RfWriter(HeaderSize + (charts.Count * 4) + 4 + 16 + (int)(moverSurfaces * 8) + (movers.Count * 12) + (tiles.Count * 6) + 4 + 16 + payload.Length);
        w.WriteU32(AlpineLightmapLayout.SectionVersion);
        w.WriteU32(0); // flags
        w.WriteU16(AlpineLightmapLayout.PageSize);
        w.WriteU16(AlpineLightmapLayout.TileStep);
        w.WriteU8(AlpineLightmapLayout.Gutter);
        w.WriteU8(baseDensity);
        w.WriteU8(stockPageLog2);
        w.WriteU8(0); // reserved
        w.WriteU16((ushort)layer.Count);
        w.WriteU32((uint)charts.Count);
        w.WriteU32((uint)tiles.Count);
        w.WriteU32(0); // src_num_faces, stamped at save
        w.WriteU32(0); // src_num_surfaces
        w.WriteU32(0); // src_surface_hash
        foreach (AlpineSurfaceChart c in charts)
        {
            bool charted = !c.Geometry.IsEmpty;
            w.WriteU16(charted ? c.KU : (ushort)0);
            w.WriteU16(charted ? c.KV : (ushort)0);
        }

        w.WriteU32((movers.Count > 0 ? 1u : 0u) + (terrains.Count > 0 ? 1u : 0u)); // num_tables
        if (movers.Count > 0)
        {
            w.WriteU32(MoverTableTag);
            w.WriteU16(1); // mover_table_version
            w.WriteU16(0);
            w.WriteU32((uint)(4 + (movers.Count * 12) + (moverSurfaces * 8)));
            w.WriteU32((uint)moverTiles);
            w.WriteU32((uint)movers.Count);
            foreach (AlpineMoverCharts m in movers)
            {
                w.WriteI32(m.Uid);
                w.WriteU32((uint)m.Charts.Length);
                m.HashOffset = w.Length;
                w.WriteU32(0); // surface_hash, stamped at save
                for (int s = 0; s < m.Charts.Length; s++)
                {
                    bool charted = !m.Charts[s].Geometry.IsEmpty;
                    w.WriteU16(charted ? m.Charts[s].KU : (ushort)0);
                    w.WriteU16(charted ? m.Charts[s].KV : (ushort)0);
                    w.WriteU16(m.Fragments[s].W);
                    w.WriteU16(m.Fragments[s].H);
                }
            }
        }

        if (terrains.Count > 0)
        {
            long terrainTiles = 0;
            foreach (AlpineTerrainChartRecord c in terrains)
            {
                terrainTiles += c.Geometry.TileCount;
            }

            w.WriteU32(AlpineTerrainCharts.TableTag);
            w.WriteU16(AlpineTerrainCharts.TableVersion);
            w.WriteU16(0);
            w.WriteU32((uint)(4 + (terrains.Count * AlpineTerrainChartRecord.WireSize)));
            w.WriteU32((uint)terrainTiles);
            w.WriteU32((uint)terrains.Count);
            foreach (AlpineTerrainChartRecord c in terrains)
            {
                w.WriteI32(c.TerrainUid);
                w.WriteU16(c.W);
                w.WriteU16(c.H);
                w.WriteF32(c.OriginX);
                w.WriteF32(c.OriginZ);
                w.WriteF32(c.TexelSize);
                w.WriteU32((uint)c.Fingerprint);
                w.WriteU32((uint)(c.Fingerprint >> 32));
            }
        }

        foreach (AlpineTile t in tiles)
        {
            w.WriteU16(t.Page);
            w.WriteU16(t.X);
            w.WriteU16(t.Y);
        }

        w.WriteU8(1); // num_layers
        w.WriteU8(0);
        w.WriteU8(0);
        w.WriteU8(0);
        w.WriteU16(0); // semantic: radiance_ldr
        w.WriteU16((ushort)codec);
        w.WriteU16(AlpineLightmapLayout.LayerVersion);
        w.WriteU8(0); // colorspace: rf_lightmap_x2
        w.WriteU8(1); // compression: zlib
        w.WriteU32((uint)raw);
        w.WriteU32((uint)payload.Length);
        w.WriteBytes(payload);
        return w.ToArray();
    }

    /// <summary>TableTag::movers (alpine_lightmap.h).</summary>
    public const uint MoverTableTag = 1;

    /// <summary>The body as saved for the static geometry only (no mover table to stamp).</summary>
    public static byte[] Stamp(byte[] body, Geometry staticGeometry) => Stamp(body, staticGeometry, null, false, null);

    /// <summary>
    /// The body as saved: a copy with the header's fingerprint fields describing the static geometry being written —
    /// its face count, surface count and the XXH32 of its serialized surface records (alpine_lightmaps.cpp:1872-1883) —
    /// and each mover record's <c>surface_hash</c> set to the hash of that mover's records as the movers section writes
    /// them (alpine_lightmaps.cpp:1900-1924): a mover that changed since the bake, or is not saved, gets a hash no solid
    /// has, so it keeps its stock lightmap in game. <paramref name="stockSuppressed"/> hashes every record with lightmap
    /// index −1, as a D3D11-only save writes them.
    /// </summary>
    public static byte[] Stamp(
        byte[] body, Geometry staticGeometry, Func<int, Geometry?>? moverGeometry, bool stockSuppressed,
        IReadOnlyList<AlpineMoverCharts>? movers = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(staticGeometry);
        byte[] copy = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(SourceFacesOffset), (uint)staticGeometry.Faces.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(SourceSurfacesOffset), (uint)staticGeometry.Surfaces.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(SourceHashOffset), SurfaceHash(staticGeometry, stockSuppressed));
        foreach (AlpineMoverCharts m in movers ?? Array.Empty<AlpineMoverCharts>())
        {
            Geometry? g = moverGeometry?.Invoke(m.Uid);
            uint hash = 0;
            if (g is not null)
            {
                hash = SurfaceHash(g, stockSuppressed);
                if (g.Surfaces.Count != m.Charts.Length || AlpineLightmapBake.SurfacesSignature(g.Surfaces, null) != m.Signature)
                {
                    hash = ~hash;
                }
            }

            if (m.HashOffset > 0 && m.HashOffset + 4 <= copy.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(m.HashOffset), hash);
            }
        }

        return copy;
    }

    /// <summary>
    /// The surface fingerprint: XXH32 (seed 0) of the geometry's surface records as the RFL stores them, lightmap index
    /// included — the last bytes of the static geometry section (game_patch/graphics/af_lightmap.cpp:729-767,
    /// editor_patch/alpine_lightmaps.cpp:1946-2038). XXH32 of the empty message for no surfaces.
    /// </summary>
    public static uint SurfaceHash(Geometry g) => SurfaceHash(g, false);

    /// <summary><see cref="SurfaceHash(Geometry)"/>, optionally as written with every lightmap index −1.</summary>
    public static uint SurfaceHash(Geometry g, bool indexMinusOne)
    {
        ArgumentNullException.ThrowIfNull(g);
        return Xxh32.Hash(SurfaceRecords(g, indexMinusOne));
    }

    /// <summary>The serialized surface records (96 bytes each), in geometry order.</summary>
    public static byte[] SurfaceRecords(Geometry g) => SurfaceRecords(g, false);
    private static byte[] SurfaceRecords(Geometry g, bool indexMinusOne)
    {
        var w = new RfWriter(g.Surfaces.Count * AlpineLightmapLayout.SurfaceRecordSize);
        foreach (Surface s in g.Surfaces)
        {
            w.WriteI32(indexMinusOne ? -1 : s.LightmapIndex);
            w.WriteU8(s.X);
            w.WriteU8(s.Y);
            w.WriteU8(s.W);
            w.WriteU8(s.H);
            w.WriteF32(s.XPixelsPerMeter);
            w.WriteF32(s.YPixelsPerMeter);
            w.WriteAabb(s.BoundingBox);
            w.WritePlane(s.Plane);
            w.WriteI32(s.ShouldSmooth);
            w.WriteI32(s.UnknownZero);
            w.WriteI32(s.DroppedCoefficient);
            w.WriteI32(s.UCoefficient);
            w.WriteI32(s.VCoefficient);
            w.WriteUv(s.UvAdd);
            w.WriteUv(s.UvScale);
            w.WriteI32(s.RoomIndex);
        }

        return w.ToArray();
    }

    /// <summary>The pages as one zlib stream (RFC 1950), which the reader inflates with zlib's <c>uncompress</c>.</summary>
    private static byte[] Deflate(IReadOnlyList<byte[]> pages)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (byte[] page in pages)
            {
                z.Write(page, 0, page.Length);
            }
        }

        return ms.ToArray();
    }
}
