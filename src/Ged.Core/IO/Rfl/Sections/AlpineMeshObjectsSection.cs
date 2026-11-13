using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_mesh_objects (0x0AFBAE01): placed V3M/V3C mesh objects.
///
/// <para>
/// Read the way Alpine reads it (game_patch/object/alpine_mesh.cpp,
/// <c>alpine_mesh_load_chunk</c>): a record's material and is-clutter flag are read only
/// while chunk bytes remain, and so are the four corpse fields of a clutter record —
/// older Alpine editors wrote shorter records, which only work as the chunk's last record.
/// Bytes after the last record (e.g. the per-object flag block newer editors append) are
/// skipped by Alpine; they are kept verbatim in <see cref="UnparsedTail"/>.
/// </para>
///
/// <para>
/// RFL 306 appends two per-mesh blocks after the last record (editor_patch/mesh.cpp:356-366 write, :489-516 read):
/// block A, one no-shadow-cast byte per mesh, read when all records loaded and at least <c>count</c> bytes remain;
/// block B, per mesh a geometry-source byte, a collision mesh name and a draw scale, read when at least
/// <c>count * 7</c> bytes remain. Pre-release 306 files carry A only, or B without the draw scale; which blocks the
/// chunk stored is remembered in <see cref="Blocks"/> so the same layout is written back. Below RFL 306 Alpine
/// ignores anything after the records, and so does Glacier (kept as <see cref="UnparsedTail"/>).
/// </para>
///
/// <para>
/// Round-trip: a short last record remembers how many optional fields it stored
/// (<see cref="AlpineMeshObject.StoredOptionalFields"/>), and <see cref="Write"/> emits the
/// same fields — extended only as far as needed to store a value that differs from Alpine's
/// default for a missing field — so an unmodified chunk re-serializes byte-for-byte. Every
/// record that is not last is written in full, since a short record there would shift the
/// records after it.
/// </para>
/// </summary>
public sealed class AlpineMeshObjectsSection : IRflSectionContent
{
    /// <summary>Optional trailing fields of a record: material, is-clutter, corpse file/anim/collision/material.</summary>
    private const int OptionalFieldCount = 6;

    /// <summary>Alpine clamps the record count to this (alpine_mesh_load_chunk).</summary>
    private const uint MaxRecords = 10000;

    /// <summary>Number of records the chunk held when parsed; -1 for a section created in memory.</summary>
    private int _parsedCount = -1;

    public SectionType Type => SectionType.AlpineMeshObjects;

    public List<AlpineMeshObject> Meshes { get; set; } = new();

    /// <summary>
    /// The RFL 306 trailing blocks the chunk stores. A section created in memory, and every chunk the v306
    /// upgrade rewrites, has <see cref="AlpineMeshTrailingBlocks.Full"/>: what Alpine RED always writes.
    /// </summary>
    public AlpineMeshTrailingBlocks Blocks { get; set; } = AlpineMeshTrailingBlocks.Full;

    /// <summary>Chunk bytes after the last record, preserved verbatim while the record count is unchanged.</summary>
    public byte[] UnparsedTail { get; private set; } = Array.Empty<byte>();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AlpineMeshObjectsSection();
        uint rawCount = r.ReadU32();
        if (rawCount > MaxRecords)
        {
            throw new RflFormatException(
                $"alpine_mesh_objects declares {rawCount} records (more than the {MaxRecords} Alpine reads); " +
                "the chunk is not in a layout Alpine Faction can load.");
        }

        int count = (int)rawCount;
        for (int i = 0; i < count; i++)
        {
            var mesh = new AlpineMeshObject
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Orientation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                MeshFilename = r.ReadVString(),
                StateAnim = r.ReadVString(),
                CollisionMode = r.ReadU8(),
            };

            int numOverrides = r.ReadU8();
            for (int j = 0; j < numOverrides; j++)
            {
                mesh.TextureOverrides.Add(new AlpineMeshTextureOverride
                {
                    SlotId = r.ReadU8(),
                    Filename = r.ReadVString(),
                });
            }

            int stored = ReadOptionalFields(r, mesh);
            if (stored < FullOptionalFields(mesh))
            {
                mesh.StoredOptionalFields = stored;
            }

            section.Meshes.Add(mesh);
        }

        section._parsedCount = count;
        section.Blocks = AlpineMeshTrailingBlocks.None;
        if (ctx.Version >= 306 && count > 0 && section.ReadTrailingBlocks(r))
        {
            if (r.Remaining > 0)
            {
                // Bytes past the blocks this version knows (a later layout): kept verbatim.
                section.UnparsedTail = r.ReadBytes(r.Remaining);
            }

            return section;
        }

        // Bytes after the last record that Alpine skips: the per-object flag block newer
        // editors append (one byte per mesh), or fewer bytes than the material field a short
        // last record stops before (Alpine reads the material only when 4 bytes remain).
        // Anything else left over is not a known layout and stays unconsumed, so the registry
        // reports it rather than hiding a misread.
        bool lastStoppedBeforeMaterial = count > 0 && section.Meshes[^1].StoredOptionalFields == 0;
        if (r.Remaining == count || (lastStoppedBeforeMaterial && r.Remaining < 4) ||
            (ctx.Version >= 306 && (IsV306TrailingBlocks(r, count, withDrawScale: true) ||
                                    IsV306TrailingBlocks(r, count, withDrawScale: false))))
        {
            section.UnparsedTail = r.ReadBytes(r.Remaining);
        }

        return section;
    }

    /// <summary>
    /// Re-targets the chunk at the RFL 306 layout Alpine RED writes: both trailing blocks (meshes keep their
    /// block values, at the defaults for a chunk read below 306) and no tail. Used by the v306 upgrade.
    /// </summary>
    public void RewriteWithTrailingBlocks()
    {
        Blocks = AlpineMeshTrailingBlocks.Full;
        UnparsedTail = Array.Empty<byte>();
        _parsedCount = -1;
    }

    /// <summary>
    /// Reads blocks A and B the way Alpine does (mesh.cpp:489-516), remembering which were present: A when every
    /// record is complete and at least one byte per mesh remains, B when exactly the rest of the chunk is block B
    /// with or without the draw scale (the earlier 306 layout). Returns false when block A is absent.
    /// </summary>
    private bool ReadTrailingBlocks(RfReader r)
    {
        int count = Meshes.Count;
        if (Meshes.Any(m => m.StoredOptionalFields is not null) || r.Remaining < count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            Meshes[i].NoShadowCast = r.ReadU8();
        }

        Blocks = AlpineMeshTrailingBlocks.Flags;
        foreach (bool withScale in new[] { true, false })
        {
            if (r.Remaining == 0 || !IsGeometryBlock(r, count, withScale))
            {
                continue;
            }

            foreach (AlpineMeshObject mesh in Meshes)
            {
                mesh.BrushGeoSource = r.ReadU8();
                mesh.CollisionMeshFilename = r.ReadVString();
                if (withScale)
                {
                    mesh.DrawScale = r.ReadF32();
                }
            }

            Blocks = withScale ? AlpineMeshTrailingBlocks.Full : AlpineMeshTrailingBlocks.FlagsAndGeometryWithoutScale;
            break;
        }

        return true;
    }

    /// <summary>True when exactly the rest of the chunk is block B (with or without the draw scale).</summary>
    private static bool IsGeometryBlock(RfReader r, int count, bool withScale)
    {
        int start = r.Position;
        try
        {
            for (int i = 0; i < count; i++)
            {
                r.ReadU8();
                r.ReadVString();
                if (withScale)
                {
                    r.ReadF32();
                }
            }

            return r.Remaining == 0;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        finally
        {
            r.Position = start;
        }
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32((uint)Meshes.Count);
        bool lastAsParsed = true;
        for (int i = 0; i < Meshes.Count; i++)
        {
            AlpineMeshObject mesh = Meshes[i];
            w.WriteI32(mesh.Uid);
            w.WriteVec3(mesh.Position);
            w.WriteMat3(mesh.Orientation);
            w.WriteVString(mesh.ScriptName);
            w.WriteVString(mesh.MeshFilename);
            w.WriteVString(mesh.StateAnim);
            w.WriteU8(mesh.CollisionMode);

            w.WriteU8((byte)mesh.TextureOverrides.Count);
            foreach (AlpineMeshTextureOverride o in mesh.TextureOverrides)
            {
                w.WriteU8(o.SlotId);
                w.WriteVString(o.Filename);
            }

            bool isLast = i == Meshes.Count - 1;
            int full = FullOptionalFields(mesh);
            int emit = isLast && mesh.StoredOptionalFields is int stored
                ? Math.Min(full, Math.Max(stored, NeededOptionalFields(mesh)))
                : full;
            WriteOptionalFields(w, mesh, emit);
            if (isLast)
            {
                lastAsParsed = emit == (mesh.StoredOptionalFields ?? full);
            }
        }

        // RFL 306 trailing blocks, in the layout the chunk had (Alpine writes both; mesh.cpp:356-366).
        if (ctx.Version >= 306 && Meshes.Count > 0 && Blocks != AlpineMeshTrailingBlocks.None)
        {
            foreach (AlpineMeshObject mesh in Meshes)
            {
                w.WriteU8(mesh.NoShadowCast);
            }

            if (Blocks != AlpineMeshTrailingBlocks.Flags)
            {
                foreach (AlpineMeshObject mesh in Meshes)
                {
                    w.WriteU8(mesh.BrushGeoSource);
                    w.WriteVString(mesh.CollisionMeshFilename);
                    if (Blocks == AlpineMeshTrailingBlocks.Full)
                    {
                        w.WriteF32(mesh.DrawScale);
                    }
                }
            }
        }

        // The tail only lines up behind the records (and blocks) it followed on disk.
        if (Meshes.Count == _parsedCount && lastAsParsed)
        {
            w.WriteBytes(UnparsedTail);
        }
    }

    /// <summary>
    /// True when the rest of the chunk is exactly the blocks rfl v306 appends after the records
    /// (alpine_mesh.cpp): one flag byte per mesh, then optionally per mesh a geometry-source byte,
    /// a collision mesh name and — in the later v306 layout — a draw scale. Leaves the reader
    /// where it was.
    /// </summary>
    private static bool IsV306TrailingBlocks(RfReader r, int count, bool withDrawScale)
    {
        int start = r.Position;
        try
        {
            if (r.Remaining < count)
            {
                return false;
            }

            r.Position += count;
            for (int i = 0; i < count && r.Remaining > 0; i++)
            {
                r.ReadU8();
                r.ReadVString();
                if (withDrawScale)
                {
                    r.ReadF32();
                }
            }

            return r.Remaining == 0;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        finally
        {
            r.Position = start;
        }
    }

    /// <summary>Optional fields a record carries in the full layout (corpse fields only for clutter).</summary>
    private static int FullOptionalFields(AlpineMeshObject mesh) => mesh.IsClutter != 0 ? OptionalFieldCount : 2;

    /// <summary>The shortest optional-field prefix that still stores every non-default value.</summary>
    private static int NeededOptionalFields(AlpineMeshObject mesh)
    {
        AlpineMeshClutterInfo? c = mesh.IsClutter != 0 ? mesh.Clutter ?? new AlpineMeshClutterInfo() : null;
        if (c is not null)
        {
            var defaults = new AlpineMeshClutterInfo();
            if (c.CorpseMaterial != defaults.CorpseMaterial) return 6;
            if (c.CorpseCollision != defaults.CorpseCollision) return 5;
            if (c.CorpseStateAnim.Length > 0) return 4;
            if (c.CorpseFilename.Length > 0) return 3;
            return 2;
        }

        return mesh.Material != 0 ? 1 : 0;
    }

    /// <summary>Reads the optional tail of a record while chunk bytes remain; returns how many fields were present.</summary>
    private static int ReadOptionalFields(RfReader r, AlpineMeshObject mesh)
    {
        if (r.Remaining < 4)
        {
            return 0;
        }

        mesh.Material = r.ReadI32();
        if (r.Remaining < 1)
        {
            return 1;
        }

        mesh.IsClutter = r.ReadU8();
        if (mesh.IsClutter == 0)
        {
            return 2;
        }

        var clutter = new AlpineMeshClutterInfo
        {
            Life = r.ReadF32(),
            DebrisFilename = r.ReadVString(),
            ExplosionVclip = r.ReadVString(),
            ExplosionRadius = r.ReadF32(),
            DebrisVelocity = r.ReadF32(),
        };
        for (int k = 0; k < 11; k++)
        {
            clutter.DamageTypeFactors[k] = r.ReadF32();
        }

        mesh.Clutter = clutter;
        int present = 2;
        if (r.Remaining > 0)
        {
            clutter.CorpseFilename = r.ReadVString();
            present++;
        }

        if (present == 3 && r.Remaining > 0)
        {
            clutter.CorpseStateAnim = r.ReadVString();
            present++;
        }

        if (present == 4 && r.Remaining >= 1)
        {
            clutter.CorpseCollision = r.ReadU8();
            present++;
        }

        if (present == 5 && r.Remaining >= 1)
        {
            clutter.CorpseMaterial = r.ReadI8();
            present++;
        }

        return present;
    }

    private static void WriteOptionalFields(RfWriter w, AlpineMeshObject mesh, int count)
    {
        if (count < 1)
        {
            return;
        }

        w.WriteI32(mesh.Material);
        if (count < 2)
        {
            return;
        }

        w.WriteU8(mesh.IsClutter);
        if (mesh.IsClutter == 0)
        {
            return;
        }

        AlpineMeshClutterInfo c = mesh.Clutter ?? new AlpineMeshClutterInfo();
        w.WriteF32(c.Life);
        w.WriteVString(c.DebrisFilename);
        w.WriteVString(c.ExplosionVclip);
        w.WriteF32(c.ExplosionRadius);
        w.WriteF32(c.DebrisVelocity);
        for (int k = 0; k < 11; k++)
        {
            w.WriteF32(c.DamageTypeFactors[k]);
        }

        if (count > 2) w.WriteVString(c.CorpseFilename);
        if (count > 3) w.WriteVString(c.CorpseStateAnim);
        if (count > 4) w.WriteU8(c.CorpseCollision);
        if (count > 5) w.WriteI8(c.CorpseMaterial);
    }
}

/// <summary>Which RFL 306 trailing blocks an Alpine mesh chunk stores.</summary>
public enum AlpineMeshTrailingBlocks
{
    /// <summary>No blocks (below RFL 306, or a pre-release 306 chunk).</summary>
    None,

    /// <summary>Block A only: one no-shadow-cast byte per mesh.</summary>
    Flags,

    /// <summary>Block A, then block B without the draw scale (an earlier 306 layout).</summary>
    FlagsAndGeometryWithoutScale,

    /// <summary>Block A, then block B with the draw scale: what Alpine RED writes.</summary>
    Full,
}
