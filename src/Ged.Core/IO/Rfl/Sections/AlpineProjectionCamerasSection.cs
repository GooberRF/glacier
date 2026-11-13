using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_projection_camera_objects (0x0AFBAE08). Read as Alpine's <c>projection_camera_deserialize_chunk</c>
/// (editor_patch/projection_camera.cpp:60-110): count capped at 10000, a lenient script name (a cut-short name
/// still loads the record, with Alpine's default name). Written as <c>projection_camera_serialize_chunk</c> (:31-58).
/// </summary>
public sealed class AlpineProjectionCamerasSection : AlpineRecordChunkSection<AlpineProjectionCamera>
{
    public override SectionType Type => SectionType.AlpineProjectionCameras;

    protected override uint MaxRecords => 10000;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineProjectionCamerasSection(), r);

    private protected override AlpineProjectionCamera? ReadRecord(AlpineChunkReader r)
    {
        if (!TryReadHeader(r, out int uid, out Vec3 pos, out Mat3 orient))
        {
            return null;
        }

        return new AlpineProjectionCamera { Uid = uid, Position = pos, Orientation = orient, ScriptName = r.LenientString() };
    }

    protected override void WriteRecord(RfWriter w, AlpineProjectionCamera x)
    {
        WriteHeader(w, x.Uid, x.Position, x.Orientation);
        w.WriteVString(x.ScriptName);
    }
}
