using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>climbing_regions (0xD00): ladder / chain-fence climb volumes.</summary>
public sealed class ClimbingRegionsSection : IRflSectionContent
{
    public SectionType Type => SectionType.ClimbingRegions;

    public List<ClimbingRegion> Regions { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new ClimbingRegionsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Regions.Add(new ClimbingRegion
            {
                Header = ObjectHeader.Read(r),
                RegionType = r.ReadI32(),
                Extents = r.ReadVec3(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Regions.Count);
        foreach (ClimbingRegion region in Regions)
        {
            region.Header.Write(w);
            w.WriteI32(region.RegionType);
            w.WriteVec3(region.Extents);
        }
    }
}
