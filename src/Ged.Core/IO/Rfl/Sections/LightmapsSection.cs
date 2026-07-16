using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>lightmaps (0x1200): 24bpp lightmap atlas pages.</summary>
public sealed class LightmapsSection : IRflSectionContent
{
    public SectionType Type => SectionType.Lightmaps;

    public List<Lightmap> Lightmaps { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new LightmapsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            int w = r.ReadI32();
            int h = r.ReadI32();
            section.Lightmaps.Add(new Lightmap
            {
                Width = w,
                Height = h,
                Pixels = r.ReadBytes(w * h * 3),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Lightmaps.Count);
        foreach (Lightmap lm in Lightmaps)
        {
            w.WriteI32(lm.Width);
            w.WriteI32(lm.Height);
            w.WriteBytes(lm.Pixels);
        }
    }
}
