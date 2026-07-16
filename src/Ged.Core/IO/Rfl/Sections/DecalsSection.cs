using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>decals (0x1000).</summary>
public sealed class DecalsSection : IRflSectionContent
{
    public SectionType Type => SectionType.Decals;

    public List<Decal> Decals { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new DecalsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Decals.Add(new Decal
            {
                Header = ObjectHeader.Read(r),
                Extents = r.ReadVec3(),
                Texture = r.ReadVString(),
                Alpha = r.ReadI32(),
                SelfIlluminated = r.ReadU8(),
                Tiling = r.ReadI32(),
                Scale = r.ReadF32(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Decals.Count);
        foreach (Decal d in Decals)
        {
            d.Header.Write(w);
            w.WriteVec3(d.Extents);
            w.WriteVString(d.Texture);
            w.WriteI32(d.Alpha);
            w.WriteU8(d.SelfIlluminated);
            w.WriteI32(d.Tiling);
            w.WriteF32(d.Scale);
        }
    }
}
