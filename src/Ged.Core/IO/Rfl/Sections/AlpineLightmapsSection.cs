namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_lightmaps (0x0AFBAE09): Alpine Faction's D3D11 lightmap atlas — per-surface refinement charts, optional
/// mover / terrain / overflow tables, a tile table and one compressed page payload. Wire spec
/// common/include/common/lightmap/alpine_lightmap.h:17-301 (all structs packed, little-endian).
///
/// <para>
/// Glacier parses the structure (header, chart table, table directory, tile table, layer directory) for
/// inspection and keeps the section's bytes verbatim: Alpine RED re-emits a retained section byte for byte
/// (alpine_lightmap.h:18-20, editor_patch/alpine_lightmaps.cpp:2613-2696), and so does Glacier until a re-bake
/// replaces it. A section whose <see cref="SectionVersion"/> is not 2 (the pre-release version 1 files) is kept
/// raw and treated as "no Alpine lightmaps" — the 1.5 reader rejects it and the game falls back to the stock
/// lightmaps.
/// </para>
/// </summary>
public sealed class AlpineLightmapsSection : IRflSectionContent
{
    /// <summary>The section version this parser understands (alpine_lightmap.h:22).</summary>
    public const uint CurrentSectionVersion = 2;

    /// <summary>Table tags (alpine_lightmap.h:118-123).</summary>
    public const uint TagMovers = 1;
    public const uint TagTerrain = 2;
    public const uint TagOverflowFaces = 3;

    private const int HeaderSize = 38;

    private byte[] _raw = Array.Empty<byte>();

    public SectionType Type => SectionType.AlpineLightmaps;

    /// <summary>The section bytes, written back verbatim.</summary>
    public ReadOnlyMemory<byte> RawBytes => _raw;

    /// <summary>SectionHeader::version, or 0 when the section is too short for a header.</summary>
    public uint SectionVersion { get; private set; }

    public ushort PageSize { get; private set; }

    public ushort TileStep { get; private set; }

    public byte Gutter { get; private set; }

    public byte BaseDensity { get; private set; }

    /// <summary>log2 of the stock page edge the surfaces were packed at (7 = 128, 8 = 256).</summary>
    public byte StockPageLog2 { get; private set; }

    public ushort PageCount { get; private set; }

    public uint ChartCount { get; private set; }

    public uint TileCount { get; private set; }

    public uint SourceFaceCount { get; private set; }

    public uint SourceSurfaceCount { get; private set; }

    /// <summary>XXH32 of the static geometry's surface records the section was baked for.</summary>
    public uint SourceSurfaceHash { get; private set; }

    /// <summary>Surface charts that refine their stock fragment (k_u and k_v both non-zero).</summary>
    public int ChartedSurfaceCount { get; private set; }

    /// <summary>The table directory, in file order.</summary>
    public List<AlpineLightmapTable> Tables { get; } = new();

    /// <summary>The layer directory, in file order.</summary>
    public List<AlpineLightmapLayer> Layers { get; } = new();

    /// <summary>Bytes of the compressed page payload after the layer directory.</summary>
    public int PayloadBytes { get; private set; }

    /// <summary>Why the structure does not parse, or null when every part is where the header says.</summary>
    public string? StructureProblem { get; private set; }

    /// <summary>
    /// True when this is a version-2 section whose structure parses: the Alpine lightmaps Glacier recognises.
    /// A version-1 (pre-release) or damaged section is preserved but counts as "no Alpine lightmaps".
    /// </summary>
    public bool IsUsable => SectionVersion == CurrentSectionVersion && StructureProblem is null;

    public static IRflSectionContent Parse(RfReader reader, RflContext ctx)
    {
        var s = new AlpineLightmapsSection { _raw = reader.ReadBytes(reader.Remaining) };
        s.StructureProblem = s.ParseStructure();
        return s;
    }

    public void Write(RfWriter w, RflContext ctx) => w.WriteBytes(_raw);

    private string? ParseStructure()
    {
        var r = new AlpineChunkReader(_raw);
        if (_raw.Length < HeaderSize)
        {
            return "the section is shorter than its header";
        }

        r.TryU32(out uint version);
        SectionVersion = version;
        r.TryU32(out _); // flags, written 0
        r.TryU16(out ushort pageSize);
        r.TryU16(out ushort tileStep);
        r.TryU8(out byte gutter);
        r.TryU8(out byte density);
        r.TryU8(out byte log2);
        r.TryU8(out _); // reserved
        r.TryU16(out ushort pages);
        r.TryU32(out uint charts);
        r.TryU32(out uint tiles);
        r.TryU32(out uint faces);
        r.TryU32(out uint surfaces);
        r.TryU32(out uint hash);
        PageSize = pageSize;
        TileStep = tileStep;
        Gutter = gutter;
        BaseDensity = density;
        StockPageLog2 = log2;
        PageCount = pages;
        ChartCount = charts;
        TileCount = tiles;
        SourceFaceCount = faces;
        SourceSurfaceCount = surfaces;
        SourceSurfaceHash = hash;

        if (version != CurrentSectionVersion)
        {
            return $"section version {version} (Glacier reads version {CurrentSectionVersion})";
        }

        if ((ulong)charts * 4 > (ulong)r.Remaining)
        {
            return "the chart table runs past the end of the section";
        }

        int charted = 0;
        for (uint i = 0; i < charts; i++)
        {
            r.TryU16(out ushort ku);
            r.TryU16(out ushort kv);
            if (ku != 0 && kv != 0)
            {
                charted++;
            }
        }

        ChartedSurfaceCount = charted;
        if (!r.TryU32(out uint tableCount))
        {
            return "the section ends before its table count";
        }

        for (uint i = 0; i < tableCount; i++)
        {
            if (!r.TryU32(out uint tag) || !r.TryU16(out ushort tableVersion) || !r.TryU16(out _)
                || !r.TryU32(out uint byteLen) || !r.TryU32(out uint tableTiles))
            {
                return $"table {i + 1} of {tableCount} has a cut-short header";
            }

            if (byteLen > (uint)r.Remaining)
            {
                return $"table {i + 1} of {tableCount} runs past the end of the section";
            }

            int bodyStart = r.Position;
            uint records = 0;
            if (byteLen >= 4)
            {
                r.TryU32(out records); // movers: num_movers; terrain: num charts; overflow: num_charts
            }

            r.Position = bodyStart + (int)byteLen;
            Tables.Add(new AlpineLightmapTable(tag, tableVersion, byteLen, tableTiles, records));
        }

        if ((ulong)tiles * 6 > (ulong)r.Remaining)
        {
            return "the tile table runs past the end of the section";
        }

        r.Position += (int)(tiles * 6);
        if (!r.TryU8(out byte layerCount) || !r.TryBytes(3, out _))
        {
            return "the section ends before its layer directory";
        }

        for (int i = 0; i < layerCount; i++)
        {
            if (!r.TryU16(out ushort semantic) || !r.TryU16(out ushort codec) || !r.TryU16(out ushort layerVersion)
                || !r.TryU8(out byte colorspace) || !r.TryU8(out byte compression)
                || !r.TryU32(out uint uncompressed) || !r.TryU32(out uint stored))
            {
                return $"layer {i + 1} of {layerCount} has a cut-short directory entry";
            }

            Layers.Add(new AlpineLightmapLayer(semantic, codec, layerVersion, colorspace, compression, uncompressed, stored));
        }

        PayloadBytes = r.Remaining;
        long declared = Layers.Sum(l => (long)l.StoredSize);
        return declared != PayloadBytes
            ? $"the layer directory declares {declared} payload bytes but {PayloadBytes} follow it"
            : null;
    }
}

/// <summary>One entry of an Alpine lightmap section's table directory (alpine_lightmap.h:194-206).</summary>
/// <param name="Tag">1 movers, 2 terrain, 3 overflow faces; other tags are skipped by readers.</param>
/// <param name="Version">The table's own version.</param>
/// <param name="ByteLength">Bytes of the table body.</param>
/// <param name="TileCount">Tiles this table owns in the tile table.</param>
/// <param name="RecordCount">The body's leading count (movers / terrain charts / overflow charts).</param>
public sealed record AlpineLightmapTable(uint Tag, ushort Version, uint ByteLength, uint TileCount, uint RecordCount);

/// <summary>One entry of an Alpine lightmap section's layer directory (alpine_lightmap.h:240-249).</summary>
/// <param name="Semantic">0 radiance LDR.</param>
/// <param name="Codec">0 raw RGB8, 1 BC7.</param>
/// <param name="LayerVersion">The layer's version.</param>
/// <param name="Colorspace">0 RF lightmap ×2.</param>
/// <param name="Compression">0 none, 1 zlib.</param>
/// <param name="UncompressedSize">Bytes of the decoded pages.</param>
/// <param name="StoredSize">Bytes of the layer in the payload.</param>
public sealed record AlpineLightmapLayer(
    ushort Semantic, ushort Codec, ushort LayerVersion, byte Colorspace, byte Compression, uint UncompressedSize, uint StoredSize);
