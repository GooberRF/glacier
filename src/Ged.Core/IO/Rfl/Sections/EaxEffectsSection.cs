using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>eax_effects (0x8000): EAX reverb zones. effect_type precedes the object header.</summary>
public sealed class EaxEffectsSection : IRflSectionContent
{
    public SectionType Type => SectionType.EaxEffects;

    public List<EaxEffect> Effects { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new EaxEffectsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Effects.Add(new EaxEffect
            {
                EffectType = r.ReadVString(),
                Header = ObjectHeader.Read(r),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Effects.Count);
        foreach (EaxEffect e in Effects)
        {
            w.WriteVString(e.EffectType);
            e.Header.Write(w);
        }
    }
}
