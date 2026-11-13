using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_directional_light_objects (0x0AFBAE0C). Read as Alpine's <c>directional_light_deserialize_chunk</c>
/// (editor_patch/dir_light.cpp:528-565) with the record reader <c>alpine_dir_light::read_record</c>
/// (common/include/common/alpine_dir_light.h:93-103): count capped at 1024, strict strings, the first record that
/// does not fit ends the load. Written as <c>directional_light_serialize_chunk</c> (dir_light.cpp:478-526).
/// </summary>
public sealed class AlpineDirectionalLightsSection : AlpineRecordChunkSection<AlpineDirectionalLight>
{
    public override SectionType Type => SectionType.AlpineDirectionalLights;

    protected override uint MaxRecords => 1024;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineDirectionalLightsSection(), r);

    private protected override AlpineDirectionalLight? ReadRecord(AlpineChunkReader r)
    {
        if (!TryReadHeader(r, out int uid, out Vec3 pos, out Mat3 orient)
            || !r.TryString(out string script) || !r.TryColor(out RfColor color) || !r.TryF32(out float intensity)
            || !r.TryU8(out byte on) || !r.TryU8(out byte shape)
            || !r.TryF32(out float ex) || !r.TryF32(out float ey) || !r.TryF32(out float ez)
            || !r.TryF32(out float yaw) || !r.TryF32(out float feather) || !r.TryF32(out float spread)
            || !r.TryU8(out byte cast) || !r.TryU8(out byte liquid) || !r.TryU8(out byte sky) || !r.TryU8(out byte outside)
            || !r.TryU8(out byte meshes) || !r.TryU8(out byte meshMode) || !r.TryU8(out byte showRange))
        {
            return null;
        }

        return new AlpineDirectionalLight
        {
            Uid = uid,
            Position = pos,
            Orientation = orient,
            ScriptName = script,
            Color = color,
            Intensity = intensity,
            InitiallyOn = on,
            Shape = shape,
            ExtentX = ex,
            ExtentY = ey,
            ExtentZ = ez,
            BoxYaw = yaw,
            Feather = feather,
            Spread = spread,
            CastBakedShadows = cast,
            LiquidOccludes = liquid,
            SkyPasses = sky,
            OutsideCasts = outside,
            AffectsMeshes = meshes,
            MeshMode = meshMode,
            AlwaysShowRange = showRange,
        };
    }

    protected override void WriteRecord(RfWriter w, AlpineDirectionalLight x)
    {
        WriteHeader(w, x.Uid, x.Position, x.Orientation);
        w.WriteVString(x.ScriptName);
        w.WriteColor(x.Color);
        w.WriteF32(x.Intensity);
        w.WriteU8(x.InitiallyOn);
        w.WriteU8(x.Shape);
        w.WriteF32(x.ExtentX);
        w.WriteF32(x.ExtentY);
        w.WriteF32(x.ExtentZ);
        w.WriteF32(x.BoxYaw);
        w.WriteF32(x.Feather);
        w.WriteF32(x.Spread);
        w.WriteU8(x.CastBakedShadows);
        w.WriteU8(x.LiquidOccludes);
        w.WriteU8(x.SkyPasses);
        w.WriteU8(x.OutsideCasts);
        w.WriteU8(x.AffectsMeshes);
        w.WriteU8(x.MeshMode);
        w.WriteU8(x.AlwaysShowRange);
    }
}
