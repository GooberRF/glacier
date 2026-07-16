using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>alpine_bag_objects (0x0AFBAE04).</summary>
public sealed class AlpineBagObjectsSection : IRflSectionContent
{
    public SectionType Type => SectionType.AlpineBagObjects;

    public List<AlpineBagObject> Bags { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new AlpineBagObjectsSection();
        int count = (int)r.ReadU32();
        for (int i = 0; i < count; i++)
        {
            section.Bags.Add(new AlpineBagObject
            {
                Uid = r.ReadI32(),
                Position = r.ReadVec3(),
                Orientation = r.ReadMat3(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteU32((uint)Bags.Count);
        foreach (AlpineBagObject bag in Bags)
        {
            w.WriteI32(bag.Uid);
            w.WriteVec3(bag.Position);
            w.WriteMat3(bag.Orientation);
        }
    }
}
