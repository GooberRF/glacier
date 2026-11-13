using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_waypoint_helper_objects (0x0AFBAE0D, RFL 306). Read as Alpine's <c>waypoint_helper_deserialize_chunk</c>
/// (editor_patch/waypoint_helper.cpp:298-341) with <c>alpine_waypoint_helper::read_record</c>
/// (common/include/common/alpine_waypoint_helper.h:78-98): count capped at the level-wide 4096, the first record
/// that does not fit ends the load. Every stored link is kept (Alpine loads the first 64 per helper and writes at
/// most 64, :18, :70). Written as <c>write_record</c> (:58-76).
/// </summary>
public sealed class AlpineWaypointHelpersSection : AlpineRecordChunkSection<AlpineWaypointHelper>
{
    public override SectionType Type => SectionType.AlpineWaypointHelpers;

    protected override uint MaxRecords => 4096;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineWaypointHelpersSection(), r);

    private protected override AlpineWaypointHelper? ReadRecord(AlpineChunkReader r)
    {
        if (!r.TryI32(out int uid) || !r.TryVec3(out Vec3 pos) || !r.TryU8(out byte type) || !r.TryU8(out byte flags)
            || !r.TryU8(out byte size) || !r.TryF32(out float radius) || !r.TryU32(out uint count))
        {
            return null;
        }

        var helper = new AlpineWaypointHelper
        {
            Uid = uid,
            Position = pos,
            HelperType = type,
            Flags = flags,
            SizeClass = size,
            Radius = radius,
        };
        for (uint i = 0; i < count; i++)
        {
            if (!r.TryI32(out int link))
            {
                return null;
            }

            helper.Links.Add(link);
        }

        return helper;
    }

    protected override void WriteRecord(RfWriter w, AlpineWaypointHelper x)
    {
        w.WriteI32(x.Uid);
        w.WriteVec3(x.Position);
        w.WriteU8(x.HelperType);
        w.WriteU8(x.Flags);
        w.WriteU8(x.SizeClass);
        w.WriteF32(x.Radius);
        w.WriteU32((uint)x.Links.Count);
        foreach (int link in x.Links)
        {
            w.WriteI32(link);
        }
    }
}
