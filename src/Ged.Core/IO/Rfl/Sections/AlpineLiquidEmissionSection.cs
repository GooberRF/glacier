using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_liquid_emission (0x0AFBAE0F, RFL 306; editor-only bake data). Read as Alpine's <c>read_chunk</c>
/// (editor_patch/liquid_emission.cpp:122-142): a count over 65536 (:29) is rejected outright, 16-byte records,
/// the first record that does not fit ends the load. Written as <c>write_chunk_body</c> (:106-118).
/// </summary>
public sealed class AlpineLiquidEmissionSection : AlpineRecordChunkSection<AlpineLiquidEmission>
{
    public override SectionType Type => SectionType.AlpineLiquidEmission;

    protected override uint MaxRecords => 65536;

    protected override bool RejectsCountOverMax => true;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineLiquidEmissionSection(), r);

    private protected override AlpineLiquidEmission? ReadRecord(AlpineChunkReader r)
    {
        if (!r.TryI32(out int key) || !r.TryU8(out byte mode) || !r.TryU8(out byte red) || !r.TryU8(out byte green)
            || !r.TryU8(out byte blue) || !r.TryF32(out float intensity) || !r.TryF32(out float distance))
        {
            return null;
        }

        return new AlpineLiquidEmission
        {
            RoomEffectUid = key,
            Mode = mode,
            ColorR = red,
            ColorG = green,
            ColorB = blue,
            Intensity = intensity,
            Distance = distance,
        };
    }

    protected override void WriteRecord(RfWriter w, AlpineLiquidEmission x)
    {
        w.WriteI32(x.RoomEffectUid);
        w.WriteU8(x.Mode);
        w.WriteU8(x.ColorR);
        w.WriteU8(x.ColorG);
        w.WriteU8(x.ColorB);
        w.WriteF32(x.Intensity);
        w.WriteF32(x.Distance);
    }
}
