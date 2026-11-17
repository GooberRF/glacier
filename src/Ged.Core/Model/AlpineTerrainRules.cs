using System.Buffers.Binary;

namespace Ged.Core.Model;

/// <summary>
/// Every size, index, range and fingerprint derivation of Alpine Faction's terrain data model, reimplemented from the
/// behaviour of <c>common/include/common/terrain/alpine_terrain.h</c> (Alpine master 8db7d90b; line numbers below are
/// that file's unless another file is named). Pure: no editor state. Float arithmetic follows the header's evaluation
/// order exactly, so positions, heights and fingerprints come out bit-identical to Alpine RED's.
/// </summary>
public static class AlpineTerrainRules
{
    // ---- Limits (:38-73) ------------------------------------------------------------------------------------------

    public const int MinVerts = 2;
    public const int MaxVerts = 257;
    public const int MaxLayers = 8;
    public const int MaxOverlays = 4;
    public const int MaxTerrains = 64;
    public const int MaxChunks = 256;
    public const int MaxScriptNameLength = 64;
    public const int MaxTextureNameLength = 31;
    public const int MaxTextureExtensionLength = 14;
    public const byte LightmapDensityMin = 1;
    public const byte LightmapDensityMax = 8;
    public const byte LightmapDensityDefault = 2;
    public const float MaxCoord = 65536f;
    public const float MinCellSize = 0.125f;
    public const float MaxCellSize = 64f;
    public const float MinHeightRange = 0.01f;
    public const float MaxHeightRange = 16384f;
    public const float MinThickness = 0.5f;
    public const float MaxThickness = 4096f;
    public const float MaxSkirtDepth = 4096f;
    public const float MinUvScale = 0.001f;
    public const float MaxUvScale = 1000f;
    public const int MaxRoomVertices = 32768;
    public const int LegacyChunkVertexBudget = 6000;
    public const int LegacyChunkFaceBudget = 12288;
    public const int LevelTriangleBudget = 250000;

    /// <summary>Decompressed terrain bytes a level may hold over every terrain chunk (:160).</summary>
    public const long MaxLevelRawBytes = 128L * 1024 * 1024;

    /// <summary>The chunk edges a build may use (:46).</summary>
    public static readonly int[] ChunkEdgeOptions = { 16, 32, 64, 128 };

    /// <summary>Weight texels per cell edge (:47).</summary>
    public static readonly int[] WeightResolutionOptions = { 1, 2, 4 };

    // ---- Flags (:75-129) ------------------------------------------------------------------------------------------

    public const byte FlagGeoable = 0x01;
    public const byte FlagSkirts = 0x02;

    /// <summary>A terrain's own shape flags; the emitter and every fingerprint see only these (:81).</summary>
    public const byte FlagMask = FlagGeoable | FlagSkirts;

    /// <summary>Wire only: the blob ends with a chunk geo mask (geoable terrains, :84).</summary>
    public const byte FlagChunkGeoMask = 0x04;

    /// <summary>Wire only: overlays follow the layers (:87).</summary>
    public const byte FlagOverlays = 0x08;

    /// <summary>Wire only: decorations follow the overlays (:90).</summary>
    public const byte FlagDecorations = 0x10;

    /// <summary>Drawn at full brightness, no baked light (:94).</summary>
    public const byte FlagFullbright = 0x20;

    public const byte WireFlagMask = 0x3F;
    public const byte LayerFlagTriplanar = 0x01;
    public const byte OverlayFlagBreakTiling = 0x02;
    public const byte OverlayFlagMask = 0x03;

    public const int MaxDecorations = 255;
    public const byte DecorationLinkNone = 0xFF;
    public const byte DecorationFlagMask = 0x3F;
    public const float MaxDecorationDensity = 16f;
    public const float MinDecorationScale = 0.01f;
    public const float MaxDecorationScale = 16f;
    public const float MaxDecorationSlope = 90f;
    public const float MinDecorationDrawDistance = 1f;
    public const float MaxDecorationDrawDistance = 1024f;
    public const float MaxDecorationOffset = 16f;

    // ---- Defaults (:162-179; editor_patch/mfc_types.h:978-1004) -----------------------------------------------------

    public const int DefaultVerts = 129;
    public const float DefaultCellSize = 2f;
    public const float DefaultHeightRange = 64f;
    public const byte DefaultChunkCells = 32;
    public const byte DefaultGeoableChunkCells = 16;
    public const byte DefaultWeightResolution = 2;
    public const float DefaultThickness = 16f;
    public const float DefaultSkirtDepth = 8f;
    public const float DefaultUvScale = 4f;

    /// <summary>RED's own default face texture (RED.exe string at 0x005781C8, :175).</summary>
    public const string DefaultLayerTexture = "Rck_default.tga";

    /// <summary>The room uid of a chunk that emits no faces (:210).</summary>
    public const int NoRoomUid = -1;

    /// <summary>Skirts shallower than this are not emitted (:1033).</summary>
    public const float MinSkirtDepth = 0.01f;

    /// <summary>The chunk material index of walls and bottoms (the underside texture, :1022).</summary>
    public const int MaterialUnderside = MaxLayers;

    // ---- Grid derivations (:212-345) -------------------------------------------------------------------------------

    public static int Cells(int verts) => verts > 0 ? verts - 1 : 0;

    public static bool IsAllowedChunkCells(int v) => Array.IndexOf(ChunkEdgeOptions, v) >= 0;

    public static bool IsAllowedWeightResolution(int v) => Array.IndexOf(WeightResolutionOptions, v) >= 0;

    public static int ChunkCount(int cellsX, int cellsZ, int edge) =>
        edge <= 0 ? 0 : ((cellsX + edge - 1) / edge) * ((cellsZ + edge - 1) / edge);

    public static int ChunksAlong(int cellsAxis, int edge) => edge <= 0 ? 0 : (cellsAxis + edge - 1) / edge;

    /// <summary>Distinct vertices an edge x edge chunk can reference (:245-249).</summary>
    public static int ChunkMaxVertices(int edge, int flags)
    {
        int grid = (edge + 1) * (edge + 1);
        return (flags & FlagMask) != 0 ? 2 * grid : grid;
    }

    /// <summary>Faces an edge x edge chunk can emit (:251-256).</summary>
    public static int ChunkMaxFaces(int edge, int flags)
    {
        int top = 2 * edge * edge, walls = 2 * edge * (edge + 1);
        if ((flags & FlagGeoable) != 0)
        {
            return top + walls + (edge * edge);
        }

        return (flags & FlagSkirts) != 0 ? top + walls : top;
    }

    public static bool ChunkEdgeFits(int edge, int flags)
    {
        int verts = ChunkMaxVertices(edge, flags);
        return verts < MaxRoomVertices && verts <= LegacyChunkVertexBudget && ChunkMaxFaces(edge, flags) <= LegacyChunkFaceBudget;
    }

    /// <summary>The largest edge whose every chunk fits a room and a legacy room cache: 64 bare, 32 with skirts or closed (:267-274).</summary>
    public static int MaxChunkCells(int flags)
    {
        int best = ChunkEdgeOptions[0];
        foreach (int e in ChunkEdgeOptions)
        {
            if (ChunkEdgeFits(e, flags))
            {
                best = e;
            }
        }

        return best;
    }

    /// <summary>
    /// The edge a build uses: the smallest allowed edge at or above <paramref name="requested"/> keeping the terrain within
    /// 256 chunks, held at or below <see cref="MaxChunkCells"/> (:283-292).
    /// </summary>
    public static int EffectiveChunkCells(int cellsX, int cellsZ, int requested, int flags)
    {
        int cap = MaxChunkCells(flags);
        int want = requested < cap ? requested : cap;
        foreach (int e in ChunkEdgeOptions)
        {
            if (e >= want && e <= cap && ChunkCount(cellsX, cellsZ, e) <= MaxChunks)
            {
                return e;
            }
        }

        return cap;
    }

    /// <summary>Cells [X0, X1) x [Z0, Z1) of chunk <paramref name="index"/>, chunks numbered row-major z then x (:305-312).</summary>
    public static TerrainChunkRect ChunkRect(int cellsX, int cellsZ, int edge, int index)
    {
        int across = ChunksAlong(cellsX, edge);
        int x0 = (index % across) * edge;
        int z0 = (index / across) * edge;
        return new TerrainChunkRect(x0, z0, Math.Min(x0 + edge, cellsX), Math.Min(z0 + edge, cellsZ));
    }

    public static int WeightWidth(int nx, int mul) => Cells(nx) * mul;

    public static int WeightHeight(int nz, int mul) => Cells(nz) * mul;

    public static int WeightTexelCount(int nx, int nz, int mul) => WeightWidth(nx, mul) * WeightHeight(nz, mul);

    /// <summary>One RGBA8 weight map (:608-611).</summary>
    public static int WeightMapBytes(int nx, int nz, int mul) => WeightTexelCount(nx, nz, mul) * 4;

    public static int BitmaskBytes(int cellsX, int cellsZ) => ((cellsX * cellsZ) + 7) / 8;

    public static int ChunkMaskBytes(int chunkCount) => (chunkCount + 7) / 8;

    /// <summary>World size along one axis (:335-338).</summary>
    public static float Extent(int verts, float cellSize) => Cells(verts) * cellSize;

    /// <summary>Heightmap image row of grid row z: image row 0 is the terrain's +Z edge (:342-345).</summary>
    public static int ImageRow(int z, int rows) => rows - 1 - z;

    // ---- Blob layout (:674-761) ------------------------------------------------------------------------------------

    /// <summary>heights | weight maps 0+1 | holes | diagonals: the blob without its optional parts (:710-713).</summary>
    public static int BlobBaseSize(int nx, int nz, int mul) =>
        (nx * nz * 2) + (WeightMapBytes(nx, nz, mul) * 2) + (2 * BitmaskBytes(Cells(nx), Cells(nz)));

    /// <summary>The decompressed size a record with these wire values declares (:939-950).</summary>
    public static long WireRawSize(int nx, int nz, int mul, int chunkCells, int wireFlags, int decorationCount)
    {
        long size = BlobBaseSize(nx, nz, mul);
        if ((wireFlags & FlagChunkGeoMask) != 0)
        {
            size += ChunkMaskBytes(ChunkCount(Cells(nx), Cells(nz), chunkCells));
        }

        if ((wireFlags & FlagOverlays) != 0)
        {
            size += WeightMapBytes(nx, nz, mul);
        }

        if ((wireFlags & FlagDecorations) != 0)
        {
            size += (long)WeightTexelCount(nx, nz, mul) * decorationCount;
        }

        return size;
    }

    /// <summary>zlib's <c>compressBound</c> (the reader's ceiling on a record's compressed size, alpine_terrain_reader.h:176).</summary>
    public static long CompressBound(long sourceLength) =>
        sourceLength + (sourceLength >> 12) + (sourceLength >> 14) + (sourceLength >> 25) + 13;

    // ---- Heights (:347-366) ----------------------------------------------------------------------------------------

    /// <summary>Height above origin.y: 0 maps to height_min, 65535 to height_min + height_range.</summary>
    public static float HeightOffset(ushort h, float heightMin, float heightRange) => heightMin + ((h / 65535.0f) * heightRange);

    /// <summary>A stored height as an offset above the origin, in double (<c>height_offset_exact</c>, :387-390).</summary>
    public static double HeightOffsetExact(ushort h, float heightMin, float heightRange) => heightMin + (h / 65535.0 * heightRange);

    public static float WorldY(float originY, ushort h, float heightMin, float heightRange) =>
        originY + HeightOffset(h, heightMin, heightRange);

    /// <summary>A normalised 0..1 height to storage, rounded half away from zero and clamped (:361-366).</summary>
    public static ushort EncodeHeight01(float v)
    {
        if (!(v > 0f))
        {
            return 0;
        }

        if (v >= 1f)
        {
            return 65535;
        }

        return (ushort)MathF.Round(v * 65535.0f, MidpointRounding.AwayFromZero);
    }

    /// <summary>An offset above origin.y to storage under the mapping, rounded and clamped (:379-385).</summary>
    public static ushort EncodeHeight(double offset, float heightMin, float heightRange)
    {
        double v = (offset - heightMin) / heightRange;
        if (!(v > 0.0))
        {
            return 0;
        }

        if (v >= 1.0)
        {
            return 65535;
        }

        return (ushort)Math.Round(v * 65535.0, MidpointRounding.AwayFromZero);
    }

    // ---- Bitmasks (:474-510) ---------------------------------------------------------------------------------------

    public static bool GetBit(byte[] mask, int i) => ((mask[i >> 3] >> (i & 7)) & 1) != 0;

    public static void SetBit(byte[] mask, int i, bool value)
    {
        byte bit = (byte)(1 << (i & 7));
        mask[i >> 3] = (byte)(value ? mask[i >> 3] | bit : mask[i >> 3] & ~bit);
    }

    // ---- Validation (:763-911; alpine_terrain_reader.h:76-217) ----------------------------------------------------

    public static bool FiniteIn(float v, float lo, float hi) => float.IsFinite(v) && v >= lo && v <= hi;

    /// <summary><paramref name="v"/> clamped into [lo, hi], or <paramref name="def"/> when not finite (:771-774).</summary>
    public static float ClampFinite(float v, float lo, float hi, float def) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : def;

    /// <summary>A texture name within RED's stem / extension caps (:776-783).</summary>
    public static bool TextureNameValid(string name)
    {
        if (name.Length > MaxTextureNameLength)
        {
            return false;
        }

        int dot = name.LastIndexOf('.');
        return dot < 0 || name.Length - dot <= MaxTextureExtensionLength;
    }

    /// <summary>A decoration mesh: empty, or a path-free .v3m / .vfx name within the caps (:863-872).</summary>
    public static bool DecorationMeshValid(string name)
    {
        if (name.Length == 0)
        {
            return true;
        }

        if (!TextureNameValid(name) || name.Length < 4 || name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0)
        {
            return false;
        }

        string ext = name[^4..].ToLowerInvariant();
        return ext is ".v3m" or ".vfx";
    }

    /// <summary>
    /// Why Alpine's readers would reject <paramref name="t"/>, or null (<c>validate_header</c>, the layer / overlay /
    /// decoration checks and the name caps of <c>read_record</c>). <paramref name="wireFlags"/> are the flags as written.
    /// </summary>
    public static string? Validate(AlpineTerrain t, int wireFlags)
    {
        Vec3 o = t.Origin;
        if (!FiniteIn(o.X, -MaxCoord, MaxCoord) || !FiniteIn(o.Y, -MaxCoord, MaxCoord) || !FiniteIn(o.Z, -MaxCoord, MaxCoord))
        {
            return "origin out of range";
        }

        if (t.ScriptName.Length > MaxScriptNameLength)
        {
            return "script name too long";
        }

        if (!FiniteIn(t.CellSize, MinCellSize, MaxCellSize))
        {
            return "cell size out of range";
        }

        AlpineTerrainGrid g = t.Grid;
        if (g.Nx < MinVerts || g.Nx > MaxVerts || g.Nz < MinVerts || g.Nz > MaxVerts)
        {
            return "vertex count out of range";
        }

        if (!FiniteIn(t.HeightMin, -MaxCoord, MaxCoord))
        {
            return "height min out of range";
        }

        if (!FiniteIn(t.HeightRange, MinHeightRange, MaxHeightRange))
        {
            return "height range out of range";
        }

        if (!IsAllowedChunkCells(t.ChunkCells))
        {
            return "chunk size not allowed";
        }

        if (ChunkCount(Cells(g.Nx), Cells(g.Nz), t.ChunkCells) > MaxChunks)
        {
            return "too many chunks";
        }

        if (!IsAllowedWeightResolution(g.WeightResolution))
        {
            return "weight resolution not allowed";
        }

        if (t.LightmapDensity < LightmapDensityMin || t.LightmapDensity > LightmapDensityMax)
        {
            return "lightmap density out of range";
        }

        if ((wireFlags & ~WireFlagMask) != 0)
        {
            return "unknown flags";
        }

        if ((wireFlags & FlagChunkGeoMask) != 0 && (wireFlags & FlagGeoable) == 0)
        {
            return "chunk geo mask without geoable";
        }

        if (!FiniteIn(t.Thickness, MinThickness, MaxThickness))
        {
            return "thickness out of range";
        }

        if (!FiniteIn(t.SkirtDepth, 0f, MaxSkirtDepth))
        {
            return "skirt depth out of range";
        }

        if (t.Layers.Count < 1 || t.Layers.Count > MaxLayers)
        {
            return "layer count out of range";
        }

        if (!TextureNameValid(t.UndersideTexture) || !TextureNameValid(t.CraterTexture))
        {
            return "texture name too long";
        }

        foreach (AlpineTerrainLayer layer in t.Layers)
        {
            if (!TextureNameValid(layer.Texture))
            {
                return "texture name too long";
            }

            if (!FiniteIn(layer.UvScale, MinUvScale, MaxUvScale))
            {
                return "layer uv scale out of range";
            }

            if ((layer.Flags & ~LayerFlagTriplanar) != 0)
            {
                return "unknown layer flags";
            }
        }

        bool overlays = (wireFlags & FlagOverlays) != 0;
        if (overlays ? t.Overlays.Count < 1 || t.Overlays.Count > MaxOverlays : t.Overlays.Count != 0)
        {
            return "overlay count out of range";
        }

        foreach (AlpineTerrainLayer overlay in t.Overlays)
        {
            if (!TextureNameValid(overlay.Texture))
            {
                return "texture name too long";
            }

            if (!FiniteIn(overlay.UvScale, MinUvScale, MaxUvScale))
            {
                return "overlay uv scale out of range";
            }

            if ((overlay.Flags & ~OverlayFlagMask) != 0)
            {
                return "unknown overlay flags";
            }
        }

        bool decorations = (wireFlags & FlagDecorations) != 0;
        if (decorations ? t.Decorations.Count < 1 || t.Decorations.Count > MaxDecorations : t.Decorations.Count != 0)
        {
            return "decoration count out of range";
        }

        foreach (AlpineTerrainDecoration d in t.Decorations)
        {
            if (!DecorationMeshValid(d.Mesh))
            {
                return "decoration mesh name invalid";
            }

            if (ValidateDecoration(d, t.Layers.Count) is { } err)
            {
                return err;
            }
        }

        return null;
    }

    /// <summary><c>validate_decoration</c> (:880-902).</summary>
    public static string? ValidateDecoration(AlpineTerrainDecoration d, int layerCount)
    {
        if (!FiniteIn(d.Density, 0f, MaxDecorationDensity))
        {
            return "decoration density out of range";
        }

        if (!FiniteIn(d.ScaleMin, MinDecorationScale, MaxDecorationScale))
        {
            return "decoration scale min out of range";
        }

        if (!FiniteIn(d.ScaleMax, MinDecorationScale, MaxDecorationScale))
        {
            return "decoration scale max out of range";
        }

        if (!FiniteIn(d.MaxSlopeDegrees, 0f, MaxDecorationSlope))
        {
            return "decoration max slope out of range";
        }

        if (!FiniteIn(d.DrawDistance, MinDecorationDrawDistance, MaxDecorationDrawDistance))
        {
            return "decoration draw distance out of range";
        }

        if (!FiniteIn(d.VerticalOffset, -MaxDecorationOffset, MaxDecorationOffset))
        {
            return "decoration vertical offset out of range";
        }

        if (d.ScaleMax < d.ScaleMin)
        {
            return "decoration scale max below scale min";
        }

        if (d.LinkLayer != DecorationLinkNone && d.LinkLayer >= layerCount)
        {
            return "decoration link layer out of range";
        }

        if ((d.Flags & ~DecorationFlagMask) != 0)
        {
            return "unknown decoration flags";
        }

        return ((d.Flags & 0x30) >> 4) >= 3 ? "decoration edges out of range" : null;
    }

    // ---- Build mapping hash (:1564-1621) ---------------------------------------------------------------------------

    /// <summary>A float's bit pattern with -0 read as +0 (:1581-1585).</summary>
    public static uint PositionBits(float f)
    {
        if (f == 0f)
        {
            f = 0f;
        }

        return BitConverter.SingleToUInt32Bits(f);
    }

    public static ulong SplitMix64(ulong v)
    {
        unchecked
        {
            v += 0x9E3779B97F4A7C15UL;
            v = (v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL;
            v = (v ^ (v >> 27)) * 0x94D049BB133111EBUL;
            return v ^ (v >> 31);
        }
    }

    /// <summary><c>splitmix64(splitmix64(x | y &lt;&lt; 32) ^ z)</c> over the position's bit patterns (:1601-1605).</summary>
    public static ulong PositionHashTerm(uint x, uint y, uint z) => SplitMix64(SplitMix64(x | ((ulong)y << 32)) ^ z);

    /// <summary>
    /// A room's distinct vertex count and order-independent position hash (sum mod 2^64 of the terms) from the
    /// positions of all its face vertices, duplicates included (<c>position_set_hash</c>, :1612-1621).
    /// </summary>
    public static (uint Count, ulong Hash) PositionSetHash(IEnumerable<Vec3> positions)
    {
        var keys = new HashSet<(uint, uint, uint)>();
        foreach (Vec3 p in positions)
        {
            keys.Add((PositionBits(p.X), PositionBits(p.Y), PositionBits(p.Z)));
        }

        ulong hash = 0;
        foreach ((uint x, uint y, uint z) in keys)
        {
            hash = unchecked(hash + PositionHashTerm(x, y, z));
        }

        return ((uint)keys.Count, hash);
    }

    // ---- Fingerprints (:1839-1979) ---------------------------------------------------------------------------------

    /// <summary>
    /// What a terrain's compiled positions depend on: placement, grid, heights, holes, diagonals and shape flags
    /// (<c>lighting_fingerprint</c>, :1862-1880; pinned vector :1883-1889).
    /// </summary>
    public static ulong LightingFingerprint(
        Vec3 origin, float cellSize, float heightMin, float heightRange, int nx, int nz, int shapeFlags,
        float thickness, float skirtDepth, ushort[] heights, byte[] holes, byte[] diagonals)
    {
        ulong h = 0xcbf29ce484222325UL;
        void Mix(ulong v) => h = SplitMix64(h ^ v);
        Mix(PositionBits(origin.X));
        Mix(PositionBits(origin.Y));
        Mix(PositionBits(origin.Z));
        Mix(PositionBits(cellSize));
        Mix(PositionBits(heightMin));
        Mix(PositionBits(heightRange));
        Mix((uint)nx);
        Mix((uint)nz);
        Mix((uint)(shapeFlags & FlagMask));
        Mix(PositionBits(thickness));
        Mix(PositionBits(skirtDepth));
        int mask = BitmaskBytes(Cells(nx), Cells(nz));
        MixWords(Mix, heights, nx * nz);
        MixBytes(Mix, holes, mask);
        MixBytes(Mix, diagonals, mask);
        return h;
    }

    /// <summary>The terrain's <see cref="LightingFingerprint"/> as it is now.</summary>
    public static ulong LightingFingerprint(AlpineTerrain t)
    {
        AlpineTerrainGrid g = t.Grid;
        return LightingFingerprint(t.Origin, t.CellSize, t.HeightMin, t.HeightRange, g.Nx, g.Nz, t.Flags, t.Thickness,
            t.SkirtDepth, g.Heights, g.Holes, g.Diagonals);
    }

    /// <summary>
    /// Every input of the compiled positions and chunk layout: the build mapping is valid while this matches
    /// (<c>geometry_fingerprint</c>, :1975-1979).
    /// </summary>
    public static ulong GeometryFingerprint(AlpineTerrain t)
    {
        AlpineTerrainGrid g = t.Grid;
        return SplitMix64(LightingFingerprint(t)
            ^ (uint)EffectiveChunkCells(Cells(g.Nx), Cells(g.Nz), t.ChunkCells, t.Flags & FlagMask));
    }

    /// <summary><c>mix_le_words</c> over u16 values: the byte length, then 4 values per word, then the zero-padded tail (:1845-1859).</summary>
    private static void MixWords(Action<ulong> mix, ushort[] p, int count)
    {
        mix((ulong)count * 2);
        int i = 0;
        for (; i + 4 <= count; i += 4)
        {
            ulong w = 0;
            for (int k = 0; k < 4; k++)
            {
                w |= (ulong)p[i + k] << (16 * k);
            }

            mix(w);
        }

        ulong tail = 0;
        for (int k = 0; i + k < count; k++)
        {
            tail |= (ulong)p[i + k] << (16 * k);
        }

        mix(tail);
    }

    private static void MixBytes(Action<ulong> mix, byte[] p, int count)
    {
        mix((ulong)count);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            mix(BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(i, 8)));
        }

        ulong tail = 0;
        for (int k = 0; i + k < count; k++)
        {
            tail |= (ulong)p[i + k] << (8 * k);
        }

        mix(tail);
    }
}

/// <summary>Cells [X0, X1) x [Z0, Z1) of one terrain chunk.</summary>
public readonly record struct TerrainChunkRect(int X0, int Z0, int X1, int Z1);
