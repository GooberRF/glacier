using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_terrain (0x0AFBAE0B): Alpine Faction heightfield terrains. Chunk: <c>u32</c> count (at most 64,
/// editor_patch/terrain.cpp:856-860), then per terrain the record <c>alpine_terrain::read_record</c> reads
/// (common/include/common/terrain/alpine_terrain_reader.h:76-217) ending in a zlib blob.
///
/// <para>
/// Glacier parses each record's structure (header, layers, overlays, decorations, build mapping, blob sizes) for
/// inspection without decompressing the blob, and keeps the section's bytes verbatim: zlib output is not stable
/// across implementations, so an unmodified terrain must re-emit its original bytes (ALPINE-COMPAT-SPEC §4.3).
/// Alpine loads a terrain chunk all-or-nothing; a record this structural pass cannot follow is reported in
/// <see cref="StructureProblem"/> and the terrains before it are still listed.
/// </para>
/// </summary>
public sealed class AlpineTerrainSection : IRflSectionContent
{
    /// <summary>The most terrains a level holds (common/include/common/terrain/alpine_terrain.h:44).</summary>
    public const int MaxTerrains = 64;

    private const byte FlagOverlays = 0x08;
    private const byte FlagDecorations = 0x10;

    /// <summary>Every flag bit Alpine knows (alpine_terrain.h:74-100); a record with another bit is rejected.</summary>
    private const byte KnownFlags = 0x3F;

    private byte[] _raw = Array.Empty<byte>();

    public SectionType Type => SectionType.AlpineTerrain;

    /// <summary>The section bytes, written back verbatim.</summary>
    public ReadOnlyMemory<byte> RawBytes => _raw;

    /// <summary>The terrain count stored in the chunk.</summary>
    public uint StoredCount { get; private set; }

    /// <summary>The terrains whose records parsed, in file order.</summary>
    public List<AlpineTerrain> Terrains { get; } = new();

    /// <summary>Why the structure does not parse completely, or null.</summary>
    public string? StructureProblem { get; private set; }

    public static IRflSectionContent Parse(RfReader reader, RflContext ctx)
    {
        var s = new AlpineTerrainSection { _raw = reader.ReadBytes(reader.Remaining) };
        s.StructureProblem = s.ParseStructure();
        return s;
    }

    public void Write(RfWriter w, RflContext ctx) => w.WriteBytes(_raw);

    private string? ParseStructure()
    {
        var r = new AlpineChunkReader(_raw);
        if (!r.TryU32(out uint count))
        {
            return "the chunk is too short to hold its terrain count";
        }

        StoredCount = count;
        if (count > MaxTerrains)
        {
            return $"it declares {count} terrains, more than the {MaxTerrains} a level holds";
        }

        string? flagProblem = null;
        for (uint i = 0; i < count; i++)
        {
            if (ReadRecord(r) is not { } terrain)
            {
                return $"terrain {i + 1} of {count} is cut short";
            }

            if ((terrain.Flags & ~KnownFlags) != 0)
            {
                // Alpine validates the header and drops the whole chunk (alpine_terrain_reader.h:103, terrain.cpp:862-896).
                flagProblem ??= $"terrain {i + 1} uses flag bits 0x{terrain.Flags & ~KnownFlags:X2} Alpine Faction does not know, so it rejects the chunk";
            }

            Terrains.Add(terrain);
        }

        return r.Remaining != 0 ? $"{r.Remaining} bytes follow the last terrain" : flagProblem;
    }

    private static AlpineTerrain? ReadRecord(AlpineChunkReader r)
    {
        if (!r.TryI32(out int uid) || !r.TryVec3(out Vec3 origin) || !r.TryString(out string script)
            || !r.TryF32(out float cell) || !r.TryU16(out ushort nx) || !r.TryU16(out ushort nz)
            || !r.TryF32(out float heightMin) || !r.TryF32(out float heightRange)
            || !r.TryU8(out byte chunkCells) || !r.TryU8(out byte mul) || !r.TryU8(out byte density) || !r.TryU8(out byte flags)
            || !r.TryF32(out float thickness) || !r.TryF32(out float skirt)
            || !r.TryString(out string underside) || !r.TryString(out string crater) || !r.TryU8(out byte layerCount))
        {
            return null;
        }

        var t = new AlpineTerrain
        {
            Uid = uid,
            Origin = origin,
            ScriptName = script,
            CellSize = cell,
            Nx = nx,
            Nz = nz,
            HeightMin = heightMin,
            HeightRange = heightRange,
            ChunkCells = chunkCells,
            WeightResolutionMultiplier = mul,
            LightmapDensity = density,
            Flags = flags,
            Thickness = thickness,
            SkirtDepth = skirt,
            UndersideTexture = underside,
            CraterTexture = crater,
        };

        if (!ReadLayers(r, layerCount, t.Layers))
        {
            return null;
        }

        if ((flags & FlagOverlays) != 0 && (!r.TryU8(out byte overlays) || !ReadLayers(r, overlays, t.Overlays)))
        {
            return null;
        }

        if ((flags & FlagDecorations) != 0)
        {
            if (!r.TryU8(out byte decorations))
            {
                return null;
            }

            for (int i = 0; i < decorations; i++)
            {
                if (!r.TryString(out string mesh) || !r.TryF32(out float dens) || !r.TryF32(out float scaleMin)
                    || !r.TryF32(out float scaleMax) || !r.TryF32(out float slope) || !r.TryF32(out float draw)
                    || !r.TryF32(out float offset) || !r.TryU8(out byte link) || !r.TryU8(out byte decoFlags))
                {
                    return null;
                }

                t.Decorations.Add(new AlpineTerrainDecoration(mesh, dens, scaleMin, scaleMax, slope, draw, offset, link, decoFlags));
            }
        }

        if (!r.TryU32(out uint mappings))
        {
            return null;
        }

        for (uint i = 0; i < mappings; i++)
        {
            if (!r.TryI32(out int room) || !r.TryU32(out uint vertices) || !r.TryU64(out ulong hash))
            {
                return null;
            }

            t.BuildMapping.Add(new AlpineTerrainChunkMapping(room, vertices, hash));
        }

        if (!r.TryU32(out uint rawSize) || !r.TryU32(out uint compSize) || compSize > (uint)r.Remaining)
        {
            return null;
        }

        t.RawSize = rawSize;
        t.CompressedSize = compSize;
        r.Position += (int)compSize;
        return t;
    }

    private static bool ReadLayers(AlpineChunkReader r, int count, List<AlpineTerrainLayer> into)
    {
        for (int i = 0; i < count; i++)
        {
            if (!r.TryString(out string texture) || !r.TryF32(out float uv) || !r.TryU8(out byte flags))
            {
                return false;
            }

            into.Add(new AlpineTerrainLayer(texture, uv, flags));
        }

        return true;
    }
}
