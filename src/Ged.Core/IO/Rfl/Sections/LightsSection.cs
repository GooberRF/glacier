using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>lights (0x300) and editor_only_lights (0x4000000): point/spot/tube lights.</summary>
public sealed class LightsSection : IRflSectionContent
{
    public LightsSection(SectionType type)
    {
        Type = type;
    }

    public SectionType Type { get; }

    public List<Light> Lights { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) =>
        ParseInto(new LightsSection(SectionType.Lights), r);

    public static IRflSectionContent ParseEditorOnly(RfReader r, RflContext ctx) =>
        ParseInto(new LightsSection(SectionType.EditorOnlyLights), r);

    private static LightsSection ParseInto(LightsSection section, RfReader r)
    {
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Lights.Add(new Light
            {
                Uid = r.ReadI32(),
                ClassName = r.ReadVString(),
                Position = r.ReadVec3(),
                Rotation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                HiddenInEditor = r.ReadU8(),
                Flags = r.ReadU32(),
                Color = r.ReadColor(),
                Range = r.ReadF32(),
                Fov = r.ReadF32(),
                FovDropoff = r.ReadF32(),
                IntensityAtMaxRange = r.ReadF32(),
                DropoffType = r.ReadI32(),
                TubeLightWidth = r.ReadF32(),
                OnIntensity = r.ReadF32(),
                OnTime = r.ReadF32(),
                OnTimeVariation = r.ReadF32(),
                OffIntensity = r.ReadF32(),
                OffTime = r.ReadF32(),
                OffTimeVariation = r.ReadF32(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Lights.Count);
        foreach (Light l in Lights)
        {
            w.WriteI32(l.Uid);
            w.WriteVString(l.ClassName);
            w.WriteVec3(l.Position);
            w.WriteMat3(l.Rotation);
            w.WriteVString(l.ScriptName);
            w.WriteU8(l.HiddenInEditor);
            w.WriteU32(l.Flags);
            w.WriteColor(l.Color);
            w.WriteF32(l.Range);
            w.WriteF32(l.Fov);
            w.WriteF32(l.FovDropoff);
            w.WriteF32(l.IntensityAtMaxRange);
            w.WriteI32(l.DropoffType);
            w.WriteF32(l.TubeLightWidth);
            w.WriteF32(l.OnIntensity);
            w.WriteF32(l.OnTime);
            w.WriteF32(l.OnTimeVariation);
            w.WriteF32(l.OffIntensity);
            w.WriteF32(l.OffTime);
            w.WriteF32(l.OffTimeVariation);
        }
    }
}
