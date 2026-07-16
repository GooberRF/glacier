using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>alpine_corona_objects (0x0AFBAE03): corona / volumetric light sprites.</summary>
public sealed class AlpineCoronaObjectsSection : IRflSectionContent
{
    public SectionType Type => SectionType.AlpineCoronaObjects;

    public List<AlpineCoronaObject> Coronas { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AlpineCoronaObjectsSection();
        int count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            var corona = new AlpineCoronaObject
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Orientation = r.ReadMat3(),
                ScriptName = r.ReadVString(),
                ColorR = r.ReadU8(),
                ColorG = r.ReadU8(),
                ColorB = r.ReadU8(),
                ColorA = r.ReadU8(),
                CoronaBitmap = r.ReadVString(),
                ConeAngle = r.ReadF32(),
                Intensity = r.ReadF32(),
                RadiusDistance = r.ReadF32(),
                RadiusScale = r.ReadF32(),
                DiminishDistance = r.ReadF32(),
                VolumetricBitmap = r.ReadVString(),
            };

            if (corona.VolumetricBitmap.Length != 0)
            {
                corona.VolumetricHeight = r.ReadF32();
                corona.VolumetricLength = r.ReadF32();
            }

            section.Coronas.Add(corona);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32((uint)Coronas.Count);
        foreach (AlpineCoronaObject corona in Coronas)
        {
            w.WriteI32(corona.Uid);
            w.WriteVec3(corona.Position);
            w.WriteMat3(corona.Orientation);
            w.WriteVString(corona.ScriptName);
            w.WriteU8(corona.ColorR);
            w.WriteU8(corona.ColorG);
            w.WriteU8(corona.ColorB);
            w.WriteU8(corona.ColorA);
            w.WriteVString(corona.CoronaBitmap);
            w.WriteF32(corona.ConeAngle);
            w.WriteF32(corona.Intensity);
            w.WriteF32(corona.RadiusDistance);
            w.WriteF32(corona.RadiusScale);
            w.WriteF32(corona.DiminishDistance);
            w.WriteVString(corona.VolumetricBitmap);

            if (corona.VolumetricBitmap.Length != 0)
            {
                w.WriteF32(corona.VolumetricHeight ?? 0f);
                w.WriteF32(corona.VolumetricLength ?? 0f);
            }
        }
    }
}
