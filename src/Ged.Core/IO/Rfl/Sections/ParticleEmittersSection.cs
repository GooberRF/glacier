using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>particle_emitters (0xA00).</summary>
public sealed class ParticleEmittersSection : IRflSectionContent
{
    public SectionType Type => SectionType.ParticleEmitters;

    public List<ParticleEmitter> Emitters { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new ParticleEmittersSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Emitters.Add(new ParticleEmitter
            {
                Header = ObjectHeader.Read(r),
                Shape = r.ReadI32(),
                SphereRadius = r.ReadF32(),
                PlaneWidth = r.ReadF32(),
                PlaneDepth = r.ReadF32(),
                Texture = r.ReadVString(),
                SpawnDelay = r.ReadF32(),
                SpawnRandomize = r.ReadF32(),
                Velocity = r.ReadF32(),
                VelocityRandomize = r.ReadF32(),
                Acceleration = r.ReadF32(),
                Decay = r.ReadF32(),
                DecayRandomize = r.ReadF32(),
                ParticleRadius = r.ReadF32(),
                ParticleRadiusRandomize = r.ReadF32(),
                GrowthRate = r.ReadF32(),
                GravityMultiplier = r.ReadF32(),
                RandomDirection = r.ReadF32(),
                ParticleColor = r.ReadColor(),
                FadeToColor = r.ReadColor(),
                EmitterFlags = r.ReadU32(),
                ParticleFlags = r.ReadU16(),
                StickinessBounciness = r.ReadU8(),
                PushSwirliness = r.ReadU8(),
                InitiallyOn = r.ReadU8(),
                TimeOn = r.ReadF32(),
                TimeOnRandomize = r.ReadF32(),
                TimeOff = r.ReadF32(),
                TimeOffRandomize = r.ReadF32(),
                ActiveDistance = r.ReadF32(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Emitters.Count);
        foreach (ParticleEmitter e in Emitters)
        {
            e.Header.Write(w);
            w.WriteI32(e.Shape);
            w.WriteF32(e.SphereRadius);
            w.WriteF32(e.PlaneWidth);
            w.WriteF32(e.PlaneDepth);
            w.WriteVString(e.Texture);
            w.WriteF32(e.SpawnDelay);
            w.WriteF32(e.SpawnRandomize);
            w.WriteF32(e.Velocity);
            w.WriteF32(e.VelocityRandomize);
            w.WriteF32(e.Acceleration);
            w.WriteF32(e.Decay);
            w.WriteF32(e.DecayRandomize);
            w.WriteF32(e.ParticleRadius);
            w.WriteF32(e.ParticleRadiusRandomize);
            w.WriteF32(e.GrowthRate);
            w.WriteF32(e.GravityMultiplier);
            w.WriteF32(e.RandomDirection);
            w.WriteColor(e.ParticleColor);
            w.WriteColor(e.FadeToColor);
            w.WriteU32(e.EmitterFlags);
            w.WriteU16(e.ParticleFlags);
            w.WriteU8(e.StickinessBounciness);
            w.WriteU8(e.PushSwirliness);
            w.WriteU8(e.InitiallyOn);
            w.WriteF32(e.TimeOn);
            w.WriteF32(e.TimeOnRandomize);
            w.WriteF32(e.TimeOff);
            w.WriteF32(e.TimeOffRandomize);
            w.WriteF32(e.ActiveDistance);
        }
    }
}
