using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>ambient_sounds (0x500).</summary>
public sealed class AmbientSoundsSection : IRflSectionContent
{
    public SectionType Type => SectionType.AmbientSounds;

    public List<AmbientSound> Sounds { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AmbientSoundsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Sounds.Add(new AmbientSound
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                HiddenInEditor = r.ReadU8(),
                SoundFileName = r.ReadVString(),
                MinDistance = r.ReadF32(),
                VolumeScale = r.ReadF32(),
                Rolloff = r.ReadF32(),
                StartDelayMs = r.ReadI32(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Sounds.Count);
        foreach (AmbientSound s in Sounds)
        {
            w.WriteI32(s.Uid);
            w.WriteVec3(s.Position);
            w.WriteU8(s.HiddenInEditor);
            w.WriteVString(s.SoundFileName);
            w.WriteF32(s.MinDistance);
            w.WriteF32(s.VolumeScale);
            w.WriteF32(s.Rolloff);
            w.WriteI32(s.StartDelayMs);
        }
    }
}
