namespace Ged.Core.Model;

/// <summary>
/// An Alpine Faction terrain (chunk 0x0AFBAE0B; record layout common/include/common/terrain/alpine_terrain_reader.h:76-217,
/// writer editor_patch/terrain.cpp:697-766): an axis-aligned heightfield whose <see cref="Origin"/> is the min-X / min-Z grid
/// corner, with its texture layers, overlays, decorations, the chunk-to-room build mapping and the grid arrays the record's
/// zlib blob holds (<see cref="Grid"/>). Values are kept as stored so an unedited terrain is written back byte for byte.
/// </summary>
public sealed class AlpineTerrain
{
    public int Uid { get; set; }

    /// <summary>The min-X / min-Z grid corner; terrains are never rotated or scaled.</summary>
    public Vec3 Origin { get; set; }

    public string ScriptName { get; set; } = string.Empty;

    /// <summary>World distance between height samples.</summary>
    public float CellSize { get; set; } = AlpineTerrainRules.DefaultCellSize;

    /// <summary>World height above <c>Origin.Y</c> that height sample 0 maps to.</summary>
    public float HeightMin { get; set; }

    /// <summary>World height span of the 0..65535 height samples.</summary>
    public float HeightRange { get; set; } = AlpineTerrainRules.DefaultHeightRange;

    /// <summary>The chunk edge in cells, as stored (the edge the last build used, <c>effective_chunk_cells</c>).</summary>
    public byte ChunkCells { get; set; } = AlpineTerrainRules.DefaultChunkCells;

    /// <summary>Lightmap texels per cell edge (1..8).</summary>
    public byte LightmapDensity { get; set; } = AlpineTerrainRules.LightmapDensityDefault;

    /// <summary>
    /// The terrain's own flags: 0x01 geoable, 0x02 skirts, 0x20 fullbright. The wire-only bits (0x04 chunk geo mask,
    /// 0x08 overlays, 0x10 decorations) are derived when the record is written (editor_patch/terrain.cpp:435-452).
    /// </summary>
    public byte Flags { get; set; }

    /// <summary>Depth of a geoable terrain's closed solid below <see cref="HeightMin"/>.</summary>
    public float Thickness { get; set; } = AlpineTerrainRules.DefaultThickness;

    /// <summary>How far skirts hang below the edges and hole rims of a non-geoable terrain.</summary>
    public float SkirtDepth { get; set; } = AlpineTerrainRules.DefaultSkirtDepth;

    public string UndersideTexture { get; set; } = AlpineTerrainRules.DefaultLayerTexture;

    /// <summary>Empty = the level's geomod texture.</summary>
    public string CraterTexture { get; set; } = string.Empty;

    /// <summary>Texture layers 1..8 (weights in <see cref="AlpineTerrainGrid.Weights"/>).</summary>
    public List<AlpineTerrainLayer> Layers { get; } = new();

    /// <summary>Overlays 0..4 (coverage in <see cref="AlpineTerrainGrid.OverlayCoverage"/>).</summary>
    public List<AlpineTerrainLayer> Overlays { get; } = new();

    /// <summary>Decorations 0..255 (one coverage plane each in <see cref="AlpineTerrainGrid.DecorationCoverage"/>).</summary>
    public List<AlpineTerrainDecoration> Decorations { get; } = new();

    /// <summary>Per chunk, the compiled room holding it (empty = not built; see <see cref="AlpineTerrainRules.GeometryFingerprint"/>).</summary>
    public List<AlpineTerrainChunkMapping> BuildMapping { get; } = new();

    /// <summary>The heightfield arrays the record's blob holds.</summary>
    public AlpineTerrainGrid Grid { get; set; } = new();

    /// <summary>Vertices along X.</summary>
    public int Nx => Grid.Nx;

    /// <summary>Vertices along Z.</summary>
    public int Nz => Grid.Nz;

    /// <summary>Weight texels per cell edge.</summary>
    public int WeightResolutionMultiplier => Grid.WeightResolution;

    public bool Geoable
    {
        get => (Flags & AlpineTerrainRules.FlagGeoable) != 0;
        set => Flags = Set(Flags, AlpineTerrainRules.FlagGeoable, value);
    }

    public bool Skirts
    {
        get => (Flags & AlpineTerrainRules.FlagSkirts) != 0;
        set => Flags = Set(Flags, AlpineTerrainRules.FlagSkirts, value);
    }

    public bool Fullbright
    {
        get => (Flags & AlpineTerrainRules.FlagFullbright) != 0;
        set => Flags = Set(Flags, AlpineTerrainRules.FlagFullbright, value);
    }

    /// <summary>Decompressed blob size of the record as loaded (0 for a terrain made in Glacier).</summary>
    public uint RawSize { get; internal set; }

    /// <summary>Compressed blob size of the record as loaded (0 for a terrain made in Glacier).</summary>
    public uint CompressedSize { get; internal set; }

    /// <summary>
    /// The compressed blob as loaded and the hash of what it decompressed to: re-emitted as long as the grid still
    /// serialises to the same bytes (zlib output differs between implementations).
    /// </summary>
    internal byte[]? LoadedCompressedBlob { get; set; }

    internal byte[]? LoadedBlobHash { get; set; }

    /// <summary>
    /// <see cref="AlpineTerrainRules.GeometryFingerprint"/> of the terrain the current <see cref="BuildMapping"/> was made
    /// for (Alpine RED's <c>built_geometry_fingerprint</c>, terrain_build.cpp:441-447, 1172-1188); null when the mapping
    /// belongs to no build of this terrain (a copy, or a terrain made since). A save drops a mapping that no longer
    /// matches (the game would draw the chunks as plain geometry).
    /// </summary>
    internal ulong? BuiltGeometryFingerprint { get; set; }

    /// <summary>True when the build mapping still describes this terrain's compiled geometry.</summary>
    public bool BuildIsCurrent =>
        BuildMapping.Count > 0 && BuiltGeometryFingerprint is { } fp && fp == AlpineTerrainRules.GeometryFingerprint(this);

    /// <summary>Records that the current build mapping belongs to this terrain as it is now.</summary>
    public void NoteBuilt() => BuiltGeometryFingerprint = AlpineTerrainRules.GeometryFingerprint(this);

    /// <summary>Forgets the build (a copy, or a terrain whose compiled geometry is gone).</summary>
    public void ResetBuiltState()
    {
        BuildMapping.Clear();
        BuiltGeometryFingerprint = null;
    }

    /// <summary>A deep copy, the loaded blob and build state included (for undo and for builds on a worker thread).</summary>
    public AlpineTerrain DeepCopy()
    {
        var copy = new AlpineTerrain();
        copy.Assign(this);
        copy.Grid = Grid.Clone();
        return copy;
    }

    /// <summary>
    /// Makes this terrain a copy of <paramref name="from"/> (lists copied element-wise; the grid shared, as every edit
    /// installs a new grid instead of changing one in place).
    /// </summary>
    public void Assign(AlpineTerrain from)
    {
        Uid = from.Uid;
        Origin = from.Origin;
        ScriptName = from.ScriptName;
        CellSize = from.CellSize;
        HeightMin = from.HeightMin;
        HeightRange = from.HeightRange;
        ChunkCells = from.ChunkCells;
        LightmapDensity = from.LightmapDensity;
        Flags = from.Flags;
        Thickness = from.Thickness;
        SkirtDepth = from.SkirtDepth;
        UndersideTexture = from.UndersideTexture;
        CraterTexture = from.CraterTexture;
        Copy(Layers, from.Layers, l => new AlpineTerrainLayer(l.Texture, l.UvScale, l.Flags));
        Copy(Overlays, from.Overlays, l => new AlpineTerrainLayer(l.Texture, l.UvScale, l.Flags));
        Copy(Decorations, from.Decorations, d => new AlpineTerrainDecoration(d.Mesh, d.Density, d.ScaleMin, d.ScaleMax,
            d.MaxSlopeDegrees, d.DrawDistance, d.VerticalOffset, d.LinkLayer, d.Flags));
        Copy(BuildMapping, from.BuildMapping, m => m);
        Grid = from.Grid;
        RawSize = from.RawSize;
        CompressedSize = from.CompressedSize;
        LoadedCompressedBlob = from.LoadedCompressedBlob;
        LoadedBlobHash = from.LoadedBlobHash;
        BuiltGeometryFingerprint = from.BuiltGeometryFingerprint;
    }

    private static void Copy<T>(List<T> into, List<T> from, Func<T, T> clone)
    {
        if (ReferenceEquals(into, from))
        {
            return;
        }

        into.Clear();
        foreach (T item in from)
        {
            into.Add(clone(item));
        }
    }

    private static byte Set(byte flags, byte bit, bool on) => (byte)(on ? flags | bit : flags & ~bit);
}

/// <summary>
/// The heavy arrays of a terrain, laid out as the record's blob stores them (alpine_terrain.h:674-761): heights
/// <c>heights[z * Nx + x]</c>, two RGBA8 weight maps (layers 0-3 then 4-7), the holes and diagonal bitmasks (bit
/// <c>z * cellsX + x</c>, LSB first), and the optional chunk geo mask, overlay coverage and decoration planes.
/// </summary>
public sealed class AlpineTerrainGrid
{
    public int Nx { get; set; } = 2;

    public int Nz { get; set; } = 2;

    /// <summary>Weight texels per cell edge (1, 2 or 4).</summary>
    public int WeightResolution { get; set; } = 1;

    public ushort[] Heights { get; set; } = new ushort[4];

    /// <summary>Weight map 0 then weight map 1, each <c>WeightMapBytes</c> long; every texel's 8 weights sum to 255.</summary>
    public byte[] Weights { get; set; } = new byte[8] { 255, 0, 0, 0, 0, 0, 0, 0 };

    public byte[] Holes { get; set; } = new byte[1];

    /// <summary>Diagonal bit 0 splits a cell along (x, z)-(x+1, z+1); 1 along (x+1, z)-(x, z+1).</summary>
    public byte[] Diagonals { get; set; } = new byte[1];

    /// <summary>The stored chunk geo mask (bit k = chunk k geoable), or null = every chunk geoable.</summary>
    public byte[]? GeoMask { get; set; }

    /// <summary>Overlay coverage (RGBA8 laid out as a weight map), present while the terrain has overlays.</summary>
    public byte[]? OverlayCoverage { get; set; }

    /// <summary>One coverage byte per weight texel per decoration, in decoration order.</summary>
    public byte[]? DecorationCoverage { get; set; }

    public int CellsX => AlpineTerrainRules.Cells(Nx);

    public int CellsZ => AlpineTerrainRules.Cells(Nz);

    public ushort Height(int x, int z) => Heights[(z * Nx) + x];

    public bool IsHole(int x, int z) => AlpineTerrainRules.GetBit(Holes, (z * CellsX) + x);

    public bool Diagonal(int x, int z) => AlpineTerrainRules.GetBit(Diagonals, (z * CellsX) + x);

    /// <summary>A flat grid painted entirely with layer 0, Alpine RED's new terrain (editor_patch/terrain.cpp:69-85).</summary>
    public static AlpineTerrainGrid Flat(int nx, int nz, int weightResolution)
    {
        int map = AlpineTerrainRules.WeightMapBytes(nx, nz, weightResolution);
        var weights = new byte[map * 2];
        for (int i = 0; i < map; i += 4)
        {
            weights[i] = 255;
        }

        int mask = AlpineTerrainRules.BitmaskBytes(AlpineTerrainRules.Cells(nx), AlpineTerrainRules.Cells(nz));
        return new AlpineTerrainGrid
        {
            Nx = nx,
            Nz = nz,
            WeightResolution = weightResolution,
            Heights = new ushort[nx * nz],
            Weights = weights,
            Holes = new byte[mask],
            Diagonals = new byte[mask],
        };
    }

    /// <summary>A deep copy.</summary>
    public AlpineTerrainGrid Clone() => new()
    {
        Nx = Nx,
        Nz = Nz,
        WeightResolution = WeightResolution,
        Heights = (ushort[])Heights.Clone(),
        Weights = (byte[])Weights.Clone(),
        Holes = (byte[])Holes.Clone(),
        Diagonals = (byte[])Diagonals.Clone(),
        GeoMask = (byte[]?)GeoMask?.Clone(),
        OverlayCoverage = (byte[]?)OverlayCoverage?.Clone(),
        DecorationCoverage = (byte[]?)DecorationCoverage?.Clone(),
    };
}

/// <summary>A terrain texture layer or overlay: texture, world units per repeat, flags (0x1 triplanar; overlays 0x2 break tiling).</summary>
public sealed class AlpineTerrainLayer
{
    public AlpineTerrainLayer()
    {
    }

    public AlpineTerrainLayer(string texture, float uvScale, byte flags)
    {
        Texture = texture;
        UvScale = uvScale;
        Flags = flags;
    }

    public string Texture { get; set; } = AlpineTerrainRules.DefaultLayerTexture;

    public float UvScale { get; set; } = AlpineTerrainRules.DefaultUvScale;

    public byte Flags { get; set; }
}

/// <summary>A terrain decoration (a mesh scattered over the surface); kept as stored, edited in a later release.</summary>
public sealed class AlpineTerrainDecoration
{
    public AlpineTerrainDecoration()
    {
    }

    public AlpineTerrainDecoration(
        string mesh, float density, float scaleMin, float scaleMax, float maxSlopeDegrees, float drawDistance,
        float verticalOffset, byte linkLayer, byte flags)
    {
        Mesh = mesh;
        Density = density;
        ScaleMin = scaleMin;
        ScaleMax = scaleMax;
        MaxSlopeDegrees = maxSlopeDegrees;
        DrawDistance = drawDistance;
        VerticalOffset = verticalOffset;
        LinkLayer = linkLayer;
        Flags = flags;
    }

    public string Mesh { get; set; } = string.Empty;

    public float Density { get; set; }

    public float ScaleMin { get; set; }

    public float ScaleMax { get; set; }

    public float MaxSlopeDegrees { get; set; }

    public float DrawDistance { get; set; }

    public float VerticalOffset { get; set; }

    public byte LinkLayer { get; set; } = AlpineTerrainRules.DecorationLinkNone;

    public byte Flags { get; set; }
}

/// <summary>One terrain chunk's build mapping: the compiled room holding it, its distinct vertex count and position hash.</summary>
public readonly record struct AlpineTerrainChunkMapping(int RoomUid, uint VertexCount, ulong PositionHash);
