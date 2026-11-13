using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_vehicle_factory_objects (0x0AFBAE07, RFL 306). Read as Alpine's
/// <c>vehicle_factory_deserialize_chunk</c> (editor_patch/vehicle_factory.cpp:586-663): count capped at the
/// level-wide 1000 (common/include/common/vehicle_meshes.h:6), strict strings, a record the chunk ends inside is
/// dropped with the rest. Written as <c>vehicle_factory_serialize_chunk</c> (:548-584).
/// </summary>
public sealed class AlpineVehicleFactoriesSection : AlpineRecordChunkSection<AlpineVehicleFactory>
{
    public override SectionType Type => SectionType.AlpineVehicleFactories;

    protected override uint MaxRecords => 1000;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineVehicleFactoriesSection(), r);

    private protected override AlpineVehicleFactory? ReadRecord(AlpineChunkReader r)
    {
        if (!TryReadHeader(r, out int uid, out Vec3 pos, out Mat3 orient)
            || !r.TryString(out string script) || !r.TryString(out string cls) || !r.TryF32(out float delay)
            || !r.TryU8(out byte team) || !r.TryU8(out byte yaw) || !r.TryU8(out byte reserved)
            || !r.TryU8(out byte locked) || !r.TryU8(out byte active))
        {
            return null;
        }

        return new AlpineVehicleFactory
        {
            Uid = uid,
            Position = pos,
            Orientation = orient,
            ScriptName = script,
            VehicleClass = cls,
            RespawnDelaySeconds = delay,
            Team = team,
            TurretYawLimitDegrees = yaw,
            Reserved = reserved,
            LockToTeam = locked,
            ActiveByDefault = active,
        };
    }

    protected override void WriteRecord(RfWriter w, AlpineVehicleFactory x)
    {
        WriteHeader(w, x.Uid, x.Position, x.Orientation);
        w.WriteVString(x.ScriptName);
        w.WriteVString(x.VehicleClass);
        w.WriteF32(x.RespawnDelaySeconds);
        w.WriteU8(x.Team);
        w.WriteU8(x.TurretYawLimitDegrees);
        w.WriteU8(x.Reserved);
        w.WriteU8(x.LockToTeam);
        w.WriteU8(x.ActiveByDefault);
    }
}
