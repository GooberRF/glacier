using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>push_regions (0x1100): force / jump-pad volumes.</summary>
public sealed class PushRegionsSection : IRflSectionContent
{
    public const int ShapeSphere = 1;
    public const int ShapeAxisAlignedBox = 2;
    public const int ShapeOrientedBox = 3;

    public SectionType Type => SectionType.PushRegions;

    public List<PushRegion> Regions { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new PushRegionsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var region = new PushRegion
            {
                Header = ObjectHeader.Read(r),
                Shape = r.ReadI32(),
            };

            if (region.Shape == ShapeSphere)
            {
                region.Radius = r.ReadF32();
            }
            else
            {
                region.Extents = r.ReadVec3();
            }

            region.Strength = r.ReadF32();
            region.Flags = r.ReadU16();
            region.Turbulence = r.ReadU16();
            section.Regions.Add(region);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Regions.Count);
        foreach (PushRegion region in Regions)
        {
            region.Header.Write(w);
            w.WriteI32(region.Shape);

            if (region.Shape == ShapeSphere)
            {
                w.WriteF32(region.Radius ?? 0f);
            }
            else
            {
                w.WriteVec3(region.Extents ?? default);
            }

            w.WriteF32(region.Strength);
            w.WriteU16(region.Flags);
            w.WriteU16(region.Turbulence);
        }
    }
}
