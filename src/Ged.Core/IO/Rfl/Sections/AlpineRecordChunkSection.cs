using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// Shared shape of Alpine Faction's counted record chunks (weather regions, vehicle factories, projection
/// cameras, rope emitters, directional lights, waypoint helpers, liquid emission): a <c>u32</c> count, then
/// that many records, with no chunk version — each chunk grows by fields appended per record, gated on the
/// RFL version.
///
/// <para>
/// Reading follows Alpine's readers: the count is capped (clamped, or for some chunks rejected) the way
/// Alpine caps it, records are read while they fit, and a record the chunk ends inside is dropped together
/// with everything after it, keeping the records already read. Values are kept exactly as stored (Alpine
/// sanitizes on read; Glacier keeps the stored value so an unmodified chunk is written back byte for byte).
/// </para>
///
/// <para>
/// Round-trip and forward compatibility (ALPINE-COMPAT-SPEC §10 rule 3): when the parse consumes the chunk
/// exactly — every declared record read cleanly and no byte left over — the records are the whole truth and
/// <see cref="Write"/> regenerates the identical bytes from them. Otherwise (a truncated chunk, a count over
/// Alpine's cap, or bytes after the last record, such as fields a newer Alpine appended) the chunk is kept
/// raw: the records Alpine would load are still exposed, but <see cref="IsReadOnly"/> is set and
/// <see cref="Write"/> emits the original bytes, never a guess.
/// </para>
/// </summary>
/// <typeparam name="T">The record model.</typeparam>
public abstract class AlpineRecordChunkSection<T> : IRflSectionContent
    where T : class
{
    private byte[]? _retained;

    public abstract SectionType Type { get; }

    /// <summary>The records Alpine would load from the chunk, in file order.</summary>
    public List<T> Records { get; } = new();

    /// <summary>The record count stored in the chunk (may exceed <see cref="Records"/> for a read-only chunk).</summary>
    public uint StoredCount { get; private set; }

    /// <summary>
    /// True when the chunk did not parse exactly (see the class remarks): it is written back verbatim and its
    /// records must not be edited.
    /// </summary>
    public bool IsReadOnly => _retained is not null;

    /// <summary>Why the chunk is read-only, or null.</summary>
    public string? ReadOnlyReason { get; private set; }

    /// <summary>The most records Alpine reads from one chunk of this kind.</summary>
    protected abstract uint MaxRecords { get; }

    /// <summary>True when Alpine rejects a count over <see cref="MaxRecords"/> instead of clamping it.</summary>
    protected virtual bool RejectsCountOverMax => false;

    public void Write(RfWriter w, RflContext ctx)
    {
        if (_retained is not null)
        {
            w.WriteBytes(_retained);
            return;
        }

        WriteBody(w);
    }

    /// <summary>Writes the count and the records (the inverse of <see cref="ReadBody"/>).</summary>
    protected virtual void WriteBody(RfWriter w)
    {
        w.WriteU32((uint)Records.Count);
        foreach (T record in Records)
        {
            WriteRecord(w, record);
        }
    }

    /// <summary>Writes one record in wire order.</summary>
    protected abstract void WriteRecord(RfWriter w, T record);

    /// <summary>Reads one record, or returns null when the chunk ends inside it (or Alpine gives up on it).</summary>
    private protected abstract T? ReadRecord(AlpineChunkReader r);

    /// <summary>
    /// Reads the chunk body into <see cref="Records"/>. Returns null when every declared record was read,
    /// else the reason the chunk is not a clean record list.
    /// </summary>
    private protected virtual string? ReadBody(AlpineChunkReader r)
    {
        if (!r.TryU32(out uint count))
        {
            return "the chunk is too short to hold its record count";
        }

        StoredCount = count;
        uint load = count;
        if (count > MaxRecords)
        {
            if (RejectsCountOverMax)
            {
                return $"it declares {count} records, more than the {MaxRecords} Alpine Faction accepts";
            }

            load = MaxRecords;
        }

        for (uint i = 0; i < load; i++)
        {
            T? record = ReadRecord(r);
            if (record is null)
            {
                return $"record {i + 1} of {count} is cut short";
            }

            Records.Add(record);
        }

        return load < count ? $"it declares {count} records; Alpine Faction reads the first {load}" : null;
    }

    /// <summary>
    /// Parses a chunk body into <paramref name="section"/>, consuming the whole body (see the class remarks
    /// for when the section becomes read-only).
    /// </summary>
    private protected static TSection Load<TSection>(TSection section, RfReader reader)
        where TSection : AlpineRecordChunkSection<T>
    {
        byte[] body = reader.ReadBytes(reader.Remaining);
        var r = new AlpineChunkReader(body);
        string? problem = section.ReadBody(r);
        if (problem is null && r.Lossy)
        {
            problem = "a record is cut short or stored in a form that would not be written back identically";
        }

        if (problem is null && r.Remaining != 0)
        {
            problem = $"{r.Remaining} bytes follow the last record (fields this version of Glacier does not know)";
        }

        if (problem is not null)
        {
            section._retained = body;
            section.ReadOnlyReason = problem;
        }

        return section;
    }

    /// <summary>
    /// The object header every Alpine object record starts with: uid, position, then the orientation rows
    /// right, up, forward (Alpine writes <c>orient.rvec/uvec/fvec</c>, e.g. editor_patch/weather_region.cpp:61-73).
    /// The rows land in <see cref="Mat3"/>'s named slots, so <see cref="Mat3.Forward"/> is the record's forward.
    /// </summary>
    private protected static bool TryReadHeader(AlpineChunkReader r, out int uid, out Vec3 pos, out Mat3 orient)
    {
        pos = default;
        orient = default;
        if (!r.TryI32(out uid) || !r.TryVec3(out pos) || !r.TryVec3(out Vec3 right) || !r.TryVec3(out Vec3 up)
            || !r.TryVec3(out Vec3 forward))
        {
            return false;
        }

        orient = new Mat3(forward, right, up);
        return true;
    }

    /// <summary>Writes the object header: uid, position, orientation rows right, up, forward.</summary>
    protected static void WriteHeader(RfWriter w, int uid, Vec3 pos, Mat3 orient)
    {
        w.WriteI32(uid);
        w.WriteVec3(pos);
        w.WriteVec3(orient.Right);
        w.WriteVec3(orient.Up);
        w.WriteVec3(orient.Forward);
    }
}
