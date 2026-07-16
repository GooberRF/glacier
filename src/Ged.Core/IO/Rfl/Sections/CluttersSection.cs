using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>clutters (0x50000).</summary>
public sealed class CluttersSection : IRflSectionContent
{
    public SectionType Type => SectionType.Clutters;

    public List<Clutter> Clutters { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new CluttersSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Clutters.Add(new Clutter
            {
                Header = ObjectHeader.Read(r),
                Unknown = r.ReadI32(),
                Skin = r.ReadVString(),
                Links = r.ReadUidList(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Clutters.Count);
        foreach (Clutter clutter in Clutters)
        {
            clutter.Header.Write(w);
            w.WriteI32(clutter.Unknown);
            w.WriteVString(clutter.Skin);
            w.WriteUidList(clutter.Links);
        }
    }
}
