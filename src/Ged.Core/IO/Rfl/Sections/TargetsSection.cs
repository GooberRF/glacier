using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>targets (0xF00): each item is a plain object header.</summary>
public sealed class TargetsSection : IRflSectionContent
{
    public SectionType Type => SectionType.Targets;

    public List<ObjectHeader> Targets { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new TargetsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Targets.Add(ObjectHeader.Read(r));
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Targets.Count);
        foreach (ObjectHeader t in Targets)
        {
            t.Write(w);
        }
    }
}
