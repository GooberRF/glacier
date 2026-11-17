using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_mover_path (0x0AFBAE0E, RFL 306): curve settings for moving groups and their keyframes, keyed by UID
/// (stock keyframe and mover sections are untouched). Body (common/include/common/mover_path.h:209-246):
/// <c>u32</c> mover count, {i32 first keyframe uid, u8 orient mode, u8 rider flags, u8 pad[2]}, <c>u32</c>
/// keyframe count, {i32 keyframe uid, u8 segment mode, u8 tangent mode, u8 ease, u8 segment orient, f32×3 in
/// handle, f32×3 out handle}. A count over 65536 is rejected and a record the chunk ends inside ends the load,
/// keeping the records before it (<c>read_chunk</c>, :212-246; editor reader editor_patch/mover_path.cpp:1444-1458).
///
/// <para>
/// Round-trip as for <see cref="AlpineRecordChunkSection{T}"/>: an exactly consumed chunk is regenerated from the
/// records, anything else is kept raw and read-only.
/// </para>
/// </summary>
public sealed class AlpineMoverPathsSection : IRflSectionContent, IRetainedChunk
{
    private const uint MaxRecords = 65536;

    private byte[]? _retained;

    public SectionType Type => SectionType.AlpineMoverPaths;

    public List<AlpineMoverPathMover> Movers { get; } = new();

    public List<AlpineMoverPathKeyframe> Keyframes { get; } = new();

    /// <summary>True when the chunk did not parse exactly; it is written back verbatim.</summary>
    public bool IsReadOnly => _retained is not null;

    public string? ReadOnlyReason { get; private set; }

    public static IRflSectionContent Parse(RfReader reader, RflContext ctx)
    {
        byte[] body = reader.ReadBytes(reader.Remaining);
        var section = new AlpineMoverPathsSection();
        var r = new AlpineChunkReader(body);
        string? problem = section.ReadBody(r);
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

    public void Write(RfWriter w, RflContext ctx)
    {
        if (_retained is not null)
        {
            w.WriteBytes(_retained);
            return;
        }

        w.WriteU32((uint)Movers.Count);
        foreach (AlpineMoverPathMover m in Movers)
        {
            w.WriteI32(m.FirstKeyframeUid);
            w.WriteU8(m.OrientMode);
            w.WriteU8(m.RiderFlags);
            w.WriteU16(m.Padding);
        }

        w.WriteU32((uint)Keyframes.Count);
        foreach (AlpineMoverPathKeyframe k in Keyframes)
        {
            w.WriteI32(k.KeyframeUid);
            w.WriteU8(k.SegmentMode);
            w.WriteU8(k.TangentMode);
            w.WriteU8(k.Ease);
            w.WriteU8(k.SegmentOrient);
            w.WriteVec3(k.InHandle);
            w.WriteVec3(k.OutHandle);
        }
    }

    private string? ReadBody(AlpineChunkReader r)
    {
        if (!r.TryU32(out uint movers))
        {
            return "the chunk is too short to hold its mover count";
        }

        if (movers > MaxRecords)
        {
            return $"it declares {movers} movers, more than the {MaxRecords} Alpine Faction accepts";
        }

        for (uint i = 0; i < movers; i++)
        {
            if (!r.TryI32(out int first) || !r.TryU8(out byte orient) || !r.TryU8(out byte riders) || !r.TryU16(out ushort pad))
            {
                return $"mover record {i + 1} of {movers} is cut short";
            }

            Movers.Add(new AlpineMoverPathMover { FirstKeyframeUid = first, OrientMode = orient, RiderFlags = riders, Padding = pad });
        }

        if (!r.TryU32(out uint keyframes))
        {
            return "the chunk ends before its keyframe count";
        }

        if (keyframes > MaxRecords)
        {
            return $"it declares {keyframes} keyframes, more than the {MaxRecords} Alpine Faction accepts";
        }

        for (uint i = 0; i < keyframes; i++)
        {
            if (!r.TryI32(out int uid) || !r.TryU8(out byte segment) || !r.TryU8(out byte tangent) || !r.TryU8(out byte ease)
                || !r.TryU8(out byte orient) || !r.TryVec3(out Vec3 inHandle) || !r.TryVec3(out Vec3 outHandle))
            {
                return $"keyframe record {i + 1} of {keyframes} is cut short";
            }

            Keyframes.Add(new AlpineMoverPathKeyframe
            {
                KeyframeUid = uid,
                SegmentMode = segment,
                TangentMode = tangent,
                Ease = ease,
                SegmentOrient = orient,
                InHandle = inHandle,
                OutHandle = outHandle,
            });
        }

        return null;
    }
}
