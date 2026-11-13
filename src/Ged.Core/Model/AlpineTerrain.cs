namespace Ged.Core.Model;

/// <summary>
/// One Alpine terrain record as stored in 0x0AFBAE0B (record layout common/include/common/terrain/alpine_terrain_reader.h:76-217;
/// writer editor_patch/terrain.cpp:697-766): the heightfield's header, texture layers, overlays, decorations, the
/// chunk-to-room build mapping and the size of its zlib blob (heights, weights, holes, diagonals, masks). Values are
/// kept as stored; the blob itself stays inside the section's preserved bytes.
/// </summary>
public sealed class AlpineTerrain
{
    public int Uid { get; set; }

    /// <summary>The min-X / min-Z grid corner; terrains are axis-aligned.</summary>
    public Vec3 Origin { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    public float CellSize { get; set; }

    /// <summary>Vertices along X.</summary>
    public ushort Nx { get; set; }

    /// <summary>Vertices along Z.</summary>
    public ushort Nz { get; set; }

    public float HeightMin { get; set; }

    public float HeightRange { get; set; }

    public byte ChunkCells { get; set; }

    public byte WeightResolutionMultiplier { get; set; }

    /// <summary>Lightmap texels per cell.</summary>
    public byte LightmapDensity { get; set; }

    /// <summary>0x01 geoable, 0x02 skirts, 0x04 chunk geo mask, 0x08 overlays, 0x10 decorations, 0x20 fullbright.</summary>
    public byte Flags { get; set; }

    public float Thickness { get; set; }

    public float SkirtDepth { get; set; }

    public string UndersideTexture { get; set; } = string.Empty;

    public string CraterTexture { get; set; } = string.Empty;

    public List<AlpineTerrainLayer> Layers { get; } = new();

    public List<AlpineTerrainLayer> Overlays { get; } = new();

    public List<AlpineTerrainDecoration> Decorations { get; } = new();

    public List<AlpineTerrainChunkMapping> BuildMapping { get; } = new();

    /// <summary>Decompressed blob size the record declares.</summary>
    public uint RawSize { get; set; }

    /// <summary>Stored (zlib) blob size.</summary>
    public uint CompressedSize { get; set; }
}

/// <summary>A terrain texture layer or overlay: texture, UV scale and flags (0x1 triplanar; overlays 0x2 break tiling).</summary>
public sealed record AlpineTerrainLayer(string Texture, float UvScale, byte Flags);

/// <summary>A terrain decoration (scattered mesh) definition.</summary>
public sealed record AlpineTerrainDecoration(
    string Mesh, float Density, float ScaleMin, float ScaleMax, float MaxSlopeDegrees, float DrawDistance,
    float VerticalOffset, byte LinkLayer, byte Flags);

/// <summary>One terrain chunk's build mapping: the compiled room holding it, its distinct vertex count and position hash.</summary>
public sealed record AlpineTerrainChunkMapping(int RoomUid, uint VertexCount, ulong PositionHash);
