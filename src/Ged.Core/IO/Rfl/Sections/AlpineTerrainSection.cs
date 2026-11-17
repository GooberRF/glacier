using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_terrain (0x0AFBAE0B): Alpine Faction heightfield terrains. Chunk: <c>u32</c> count (at most 64,
/// editor_patch/terrain.cpp:856-860), then per terrain the record <c>alpine_terrain::read_record</c> reads
/// (common/include/common/terrain/alpine_terrain_reader.h:76-217), ending in a zlib blob of the grid arrays.
///
/// <para>
/// Reading mirrors Alpine's reader exactly, blob included (header and name validation, the mapping-count rule, the
/// declared and compressed size checks, the 128 MB level budget, the decompressed length). Alpine loads a terrain chunk
/// all or nothing: when any record fails, Glacier keeps the chunk's bytes verbatim, lists the terrains read before the
/// failure for inspection only, and reports why in <see cref="StructureProblem"/> (<see cref="IsReadOnly"/>).
/// </para>
///
/// <para>
/// Writing follows <c>terrain_serialize_chunk</c> (terrain.cpp:510-766): every field is clamped into the ranges the
/// readers accept first (<see cref="AlpineTerrainEdit.Sanitize"/>; a no-op on any record Alpine accepted), the wire-only
/// flags are derived, and the blob is rebuilt. zlib output is not stable across implementations, so a terrain whose
/// grid still serialises to the bytes it was loaded with re-emits its original compressed blob: an unedited terrain is
/// written back byte for byte (ALPINE-COMPAT-SPEC §4.3).
/// </para>
/// </summary>
public sealed class AlpineTerrainSection : IRflSectionContent, IRetainedChunk
{
    /// <summary>The most terrains a level holds (alpine_terrain.h:44).</summary>
    public const int MaxTerrains = AlpineTerrainRules.MaxTerrains;

    private byte[]? _retained;

    public SectionType Type => SectionType.AlpineTerrain;

    /// <summary>The terrain count stored in the chunk as loaded.</summary>
    public uint StoredCount { get; private set; }

    /// <summary>The terrains, in file order (for a read-only chunk, those read before the failure).</summary>
    public List<AlpineTerrain> Terrains { get; } = new();

    /// <summary>Why Alpine Faction would not load the chunk as loaded, or null.</summary>
    public string? StructureProblem { get; private set; }

    /// <summary>True when the chunk did not load cleanly: it is written back verbatim and its terrains are read-only.</summary>
    public bool IsReadOnly => _retained is not null;

    public string? ReadOnlyReason => StructureProblem;

    public static IRflSectionContent Parse(RfReader reader, RflContext ctx)
    {
        byte[] body = reader.ReadBytes(reader.Remaining);
        var s = new AlpineTerrainSection();
        s.StructureProblem = s.ParseStructure(body);
        if (s.StructureProblem is not null)
        {
            s._retained = body;
        }

        return s;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        if (_retained is not null)
        {
            w.WriteBytes(_retained);
            return;
        }

        int count = Math.Min(Terrains.Count, MaxTerrains);
        w.WriteU32((uint)count);
        for (int i = 0; i < count; i++)
        {
            WriteRecord(w, Terrains[i]);
        }
    }

    /// <summary>The flags a record is written with: the terrain's own, plus the wire-only bits its content implies (terrain.cpp:435-452).</summary>
    public static byte WireFlags(AlpineTerrain t)
    {
        int flags = t.Flags & (AlpineTerrainRules.FlagMask | AlpineTerrainRules.FlagFullbright);
        if (t.Geoable && t.Grid.GeoMask is not null)
        {
            flags |= AlpineTerrainRules.FlagChunkGeoMask;
        }

        if (t.Overlays.Count > 0)
        {
            flags |= AlpineTerrainRules.FlagOverlays;
        }

        if (t.Decorations.Count > 0)
        {
            flags |= AlpineTerrainRules.FlagDecorations;
        }

        return (byte)flags;
    }

    /// <summary>The decompressed blob a record of <paramref name="t"/> stores (alpine_terrain.h:674-761; terrain.cpp:599-625).</summary>
    public static byte[] BuildBlob(AlpineTerrain t)
    {
        AlpineTerrainGrid g = t.Grid;
        byte wire = WireFlags(t);
        long size = AlpineTerrainRules.WireRawSize(g.Nx, g.Nz, g.WeightResolution, t.ChunkCells, wire, t.Decorations.Count);
        var raw = new byte[size];
        int at = 0;
        for (int i = 0; i < g.Nx * g.Nz; i++, at += 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at, 2), g.Heights[i]);
        }

        int mask = AlpineTerrainRules.BitmaskBytes(g.CellsX, g.CellsZ);
        at = Copy(g.Weights, raw, at, AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution) * 2);
        at = Copy(g.Holes, raw, at, mask);
        at = Copy(g.Diagonals, raw, at, mask);
        if ((wire & AlpineTerrainRules.FlagChunkGeoMask) != 0)
        {
            at = Copy(g.GeoMask, raw, at, AlpineTerrainRules.ChunkMaskBytes(
                AlpineTerrainRules.ChunkCount(g.CellsX, g.CellsZ, t.ChunkCells)));
        }

        if ((wire & AlpineTerrainRules.FlagOverlays) != 0)
        {
            at = Copy(g.OverlayCoverage, raw, at, AlpineTerrainRules.WeightMapBytes(g.Nx, g.Nz, g.WeightResolution));
        }

        if ((wire & AlpineTerrainRules.FlagDecorations) != 0)
        {
            Copy(g.DecorationCoverage, raw, at,
                AlpineTerrainRules.WeightTexelCount(g.Nx, g.Nz, g.WeightResolution) * t.Decorations.Count);
        }

        return raw;
    }

    private static int Copy(byte[]? source, byte[] into, int at, int length)
    {
        if (source is not null)
        {
            Array.Copy(source, 0, into, at, Math.Min(source.Length, length));
        }

        return at + length;
    }

    private static void WriteRecord(RfWriter w, AlpineTerrain t)
    {
        AlpineTerrainEdit.Sanitize(t);
        AlpineTerrainGrid g = t.Grid;
        byte wire = WireFlags(t);
        byte[] raw = BuildBlob(t);
        byte[] hash = SHA256.HashData(raw);
        byte[] comp = t.LoadedCompressedBlob is { } loaded && t.LoadedBlobHash is { } loadedHash && loadedHash.AsSpan().SequenceEqual(hash)
            ? loaded
            : Compress(raw);

        w.WriteI32(t.Uid);
        w.WriteVec3(t.Origin);
        w.WriteVString(t.ScriptName.Length > AlpineTerrainRules.MaxScriptNameLength
            ? t.ScriptName[..AlpineTerrainRules.MaxScriptNameLength]
            : t.ScriptName);
        w.WriteF32(t.CellSize);
        w.WriteU16((ushort)g.Nx);
        w.WriteU16((ushort)g.Nz);
        w.WriteF32(t.HeightMin);
        w.WriteF32(t.HeightRange);
        w.WriteU8(t.ChunkCells);
        w.WriteU8((byte)g.WeightResolution);
        w.WriteU8(t.LightmapDensity);
        w.WriteU8(wire);
        w.WriteF32(t.Thickness);
        w.WriteF32(t.SkirtDepth);
        w.WriteVString(t.UndersideTexture);
        w.WriteVString(t.CraterTexture);
        w.WriteU8((byte)t.Layers.Count);
        foreach (AlpineTerrainLayer layer in t.Layers)
        {
            WriteLayer(w, layer);
        }

        if ((wire & AlpineTerrainRules.FlagOverlays) != 0)
        {
            w.WriteU8((byte)t.Overlays.Count);
            foreach (AlpineTerrainLayer overlay in t.Overlays)
            {
                WriteLayer(w, overlay);
            }
        }

        if ((wire & AlpineTerrainRules.FlagDecorations) != 0)
        {
            w.WriteU8((byte)t.Decorations.Count);
            foreach (AlpineTerrainDecoration d in t.Decorations)
            {
                w.WriteVString(d.Mesh);
                w.WriteF32(d.Density);
                w.WriteF32(d.ScaleMin);
                w.WriteF32(d.ScaleMax);
                w.WriteF32(d.MaxSlopeDegrees);
                w.WriteF32(d.DrawDistance);
                w.WriteF32(d.VerticalOffset);
                w.WriteU8(d.LinkLayer);
                w.WriteU8(d.Flags);
            }
        }

        w.WriteU32((uint)t.BuildMapping.Count);
        foreach (AlpineTerrainChunkMapping m in t.BuildMapping)
        {
            w.WriteI32(m.RoomUid);
            w.WriteU32(m.VertexCount);
            w.WriteU32((uint)m.PositionHash);
            w.WriteU32((uint)(m.PositionHash >> 32));
        }

        w.WriteU32((uint)raw.Length);
        w.WriteU32((uint)comp.Length);
        w.WriteBytes(comp);
    }

    private static void WriteLayer(RfWriter w, AlpineTerrainLayer layer)
    {
        w.WriteVString(layer.Texture);
        w.WriteF32(layer.UvScale);
        w.WriteU8(layer.Flags);
    }

    /// <summary>One zlib stream (RFC 1950) at the default level, which Alpine inflates with <c>uncompress</c>.</summary>
    public static byte[] Compress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        return ms.ToArray();
    }

    private string? ParseStructure(byte[] body)
    {
        var r = new AlpineChunkReader(body);
        if (!r.TryU32(out uint count))
        {
            return "the chunk is too short to hold its terrain count";
        }

        StoredCount = count;
        if (count > MaxTerrains)
        {
            return $"it declares {count} terrains, more than the {MaxTerrains} a level holds";
        }

        long totalRaw = 0;
        for (uint i = 0; i < count; i++)
        {
            var t = new AlpineTerrain();
            if (ReadRecord(r, t, ref totalRaw) is { } err)
            {
                if (t.Grid.Heights.Length == t.Grid.Nx * t.Grid.Nz && t.Layers.Count > 0)
                {
                    Terrains.Add(t);
                }

                return $"terrain {i + 1} of {count}: {err}";
            }

            Terrains.Add(t);
        }

        return r.Remaining != 0 ? $"{r.Remaining} bytes follow the last terrain" : null;
    }

    /// <summary><c>read_record</c>: fills <paramref name="t"/>, returns null on success or why the chunk is unusable.</summary>
    private static string? ReadRecord(AlpineChunkReader r, AlpineTerrain t, ref long totalRaw)
    {
        if (!r.TryI32(out int uid) || !r.TryVec3(out Vec3 origin) || !r.TryString(out string script))
        {
            return "truncated";
        }

        t.Uid = uid;
        t.Origin = origin;
        t.ScriptName = script;
        if (script.Length > AlpineTerrainRules.MaxScriptNameLength)
        {
            return "script name too long";
        }

        if (!r.TryF32(out float cell) || !r.TryU16(out ushort nx) || !r.TryU16(out ushort nz)
            || !r.TryF32(out float heightMin) || !r.TryF32(out float heightRange)
            || !r.TryU8(out byte chunkCells) || !r.TryU8(out byte mul) || !r.TryU8(out byte density) || !r.TryU8(out byte flags)
            || !r.TryF32(out float thickness) || !r.TryF32(out float skirt)
            || !r.TryString(out string underside) || !r.TryString(out string crater) || !r.TryU8(out byte layerCount))
        {
            return "truncated";
        }

        t.CellSize = cell;
        t.HeightMin = heightMin;
        t.HeightRange = heightRange;
        t.ChunkCells = chunkCells;
        t.LightmapDensity = density;
        t.Flags = (byte)(flags & (AlpineTerrainRules.FlagMask | AlpineTerrainRules.FlagFullbright));
        t.Thickness = thickness;
        t.SkirtDepth = skirt;
        t.UndersideTexture = underside;
        t.CraterTexture = crater;
        t.Grid = new AlpineTerrainGrid { Nx = nx, Nz = nz, WeightResolution = mul, Heights = Array.Empty<ushort>() };
        if (HeaderProblem(t, flags, layerCount) is { } headerErr)
        {
            return headerErr;
        }

        for (int i = 0; i < layerCount; i++)
        {
            if (!r.TryString(out string texture) || !r.TryF32(out float uv) || !r.TryU8(out byte layerFlags))
            {
                return "truncated";
            }

            t.Layers.Add(new AlpineTerrainLayer(texture, uv, layerFlags));
        }

        if ((flags & AlpineTerrainRules.FlagOverlays) != 0)
        {
            if (!r.TryU8(out byte overlays))
            {
                return "truncated";
            }

            if (overlays < 1 || overlays > AlpineTerrainRules.MaxOverlays)
            {
                return "overlay count out of range";
            }

            for (int i = 0; i < overlays; i++)
            {
                if (!r.TryString(out string texture) || !r.TryF32(out float uv) || !r.TryU8(out byte overlayFlags))
                {
                    return "truncated";
                }

                t.Overlays.Add(new AlpineTerrainLayer(texture, uv, overlayFlags));
            }
        }

        if ((flags & AlpineTerrainRules.FlagDecorations) != 0)
        {
            if (!r.TryU8(out byte decorations))
            {
                return "truncated";
            }

            if (decorations < 1)
            {
                return "decoration count out of range";
            }

            for (int i = 0; i < decorations; i++)
            {
                if (!r.TryString(out string mesh) || !r.TryF32(out float dens) || !r.TryF32(out float scaleMin)
                    || !r.TryF32(out float scaleMax) || !r.TryF32(out float slope) || !r.TryF32(out float draw)
                    || !r.TryF32(out float offset) || !r.TryU8(out byte link) || !r.TryU8(out byte decoFlags))
                {
                    return "truncated";
                }

                t.Decorations.Add(new AlpineTerrainDecoration(mesh, dens, scaleMin, scaleMax, slope, draw, offset, link, decoFlags));
            }
        }

        if (AlpineTerrainRules.Validate(t, flags) is { } err)
        {
            return err;
        }

        if (!r.TryU32(out uint mappings))
        {
            return "truncated";
        }

        if (mappings > AlpineTerrainRules.MaxChunks)
        {
            return "build mapping count out of range";
        }

        for (uint i = 0; i < mappings; i++)
        {
            if (!r.TryI32(out int room) || !r.TryU32(out uint vertices) || !r.TryU64(out ulong hash))
            {
                return "truncated";
            }

            t.BuildMapping.Add(new AlpineTerrainChunkMapping(room, vertices, hash));
        }

        if (!r.TryU32(out uint rawSize) || !r.TryU32(out uint compSize))
        {
            return "truncated";
        }

        long expected = AlpineTerrainRules.WireRawSize(nx, nz, mul, chunkCells, flags, t.Decorations.Count);
        if (rawSize != expected)
        {
            return $"payload size mismatch (it declares {rawSize} bytes; a {nx} x {nz} grid with these parts holds {expected})";
        }

        if (compSize == 0 || compSize > (uint)r.Remaining || compSize > AlpineTerrainRules.CompressBound(expected))
        {
            return "compressed size out of range";
        }

        totalRaw += expected;
        if (totalRaw > AlpineTerrainRules.MaxLevelRawBytes)
        {
            return "terrain data budget exceeded";
        }

        r.TryBytes((int)compSize, out byte[] comp);
        if (Inflate(comp, (int)expected) is not { } raw)
        {
            return "payload does not decompress";
        }

        t.RawSize = rawSize;
        t.CompressedSize = compSize;
        t.LoadedCompressedBlob = comp;
        t.LoadedBlobHash = SHA256.HashData(raw);
        t.Grid = SplitBlob(raw, nx, nz, mul, chunkCells, flags, t.Decorations.Count);
        if (t.BuildMapping.Count > 0)
        {
            // Alpine adopts a loaded mapping as the terrain's built state (terrain_build.cpp:1172-1188).
            t.NoteBuilt();
        }

        return null;
    }

    /// <summary>The header checks <c>validate_header</c> runs before the layers are read (alpine_terrain.h:786-811).</summary>
    private static string? HeaderProblem(AlpineTerrain t, byte flags, byte layerCount)
    {
        AlpineTerrainGrid g = t.Grid;
        Vec3 o = t.Origin;
        float max = AlpineTerrainRules.MaxCoord;
        if (!AlpineTerrainRules.FiniteIn(o.X, -max, max) || !AlpineTerrainRules.FiniteIn(o.Y, -max, max) || !AlpineTerrainRules.FiniteIn(o.Z, -max, max))
        {
            return "origin out of range";
        }

        if (g.Nx < AlpineTerrainRules.MinVerts || g.Nx > AlpineTerrainRules.MaxVerts || g.Nz < AlpineTerrainRules.MinVerts || g.Nz > AlpineTerrainRules.MaxVerts)
        {
            return "vertex count out of range";
        }

        if (!AlpineTerrainRules.IsAllowedChunkCells(t.ChunkCells) || !AlpineTerrainRules.IsAllowedWeightResolution(g.WeightResolution))
        {
            return "chunk size or weight resolution not allowed";
        }

        if (layerCount < 1 || layerCount > AlpineTerrainRules.MaxLayers)
        {
            return "layer count out of range";
        }

        return (flags & ~AlpineTerrainRules.WireFlagMask) != 0 ? "unknown flags" : null;
    }

    /// <summary>zlib-inflates exactly <paramref name="expected"/> bytes, or null (<c>uncompress</c> with <c>out_len == expected</c>).</summary>
    private static byte[]? Inflate(byte[] comp, int expected)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress);
            var raw = new byte[expected];
            int got = 0;
            while (got < expected)
            {
                int n = z.Read(raw, got, expected - got);
                if (n == 0)
                {
                    return null;
                }

                got += n;
            }

            return z.ReadByte() == -1 ? raw : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static AlpineTerrainGrid SplitBlob(byte[] raw, int nx, int nz, int mul, int chunkCells, int flags, int decorations)
    {
        var heights = new ushort[nx * nz];
        int at = 0;
        for (int i = 0; i < heights.Length; i++, at += 2)
        {
            heights[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at, 2));
        }

        int cx = AlpineTerrainRules.Cells(nx), cz = AlpineTerrainRules.Cells(nz);
        int mask = AlpineTerrainRules.BitmaskBytes(cx, cz);
        int map = AlpineTerrainRules.WeightMapBytes(nx, nz, mul);
        byte[] Take(int n)
        {
            byte[] part = raw.AsSpan(at, n).ToArray();
            at += n;
            return part;
        }

        var g = new AlpineTerrainGrid
        {
            Nx = nx,
            Nz = nz,
            WeightResolution = mul,
            Heights = heights,
            Weights = Take(map * 2),
            Holes = Take(mask),
            Diagonals = Take(mask),
        };
        if ((flags & AlpineTerrainRules.FlagChunkGeoMask) != 0)
        {
            g.GeoMask = Take(AlpineTerrainRules.ChunkMaskBytes(AlpineTerrainRules.ChunkCount(cx, cz, chunkCells)));
        }

        if ((flags & AlpineTerrainRules.FlagOverlays) != 0)
        {
            g.OverlayCoverage = Take(map);
        }

        if ((flags & AlpineTerrainRules.FlagDecorations) != 0)
        {
            g.DecorationCoverage = Take(AlpineTerrainRules.WeightTexelCount(nx, nz, mul) * decorations);
        }

        return g;
    }
}
