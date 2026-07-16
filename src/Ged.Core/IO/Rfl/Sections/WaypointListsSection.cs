using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>waypoint_lists (0x10000): named lists of nav-point indices.</summary>
public sealed class WaypointListsSection : IRflSectionContent
{
    public SectionType Type => SectionType.WaypointLists;

    public List<WaypointList> Lists { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new WaypointListsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var list = new WaypointList { Name = r.ReadVString() };
            int numWaypoints = r.ReadI32();
            for (int j = 0; j < numWaypoints; j++)
            {
                list.WaypointIndices.Add(r.ReadI32());
            }

            section.Lists.Add(list);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Lists.Count);
        foreach (WaypointList list in Lists)
        {
            w.WriteVString(list.Name);
            w.WriteI32(list.WaypointIndices.Count);
            foreach (int index in list.WaypointIndices)
            {
                w.WriteI32(index);
            }
        }
    }
}
