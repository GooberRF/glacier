using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>geo_regions (0x200): geomod hardness / ice / shallow-geomod volumes.</summary>
public sealed class GeoRegionsSection : IRflSectionContent
{
    public SectionType Type => SectionType.GeoRegions;

    public List<GeoRegion> Regions { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new GeoRegionsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var region = new GeoRegion
            {
                Uid = r.ReadI32(),
                Flags = r.ReadU16(),
                Hardness = r.ReadU16(),
            };

            if ((region.Flags & GeoRegion.FlagUseShallowGeomods) != 0)
            {
                region.ShallowGeomodDepth = r.ReadF32();
            }

            region.Position = r.ReadVec3();

            if ((region.Flags & GeoRegion.FlagIsBox) != 0)
            {
                region.Rotation = r.ReadMat3();
                region.Width = r.ReadF32();
                region.Height = r.ReadF32();
                region.Depth = r.ReadF32();
            }

            if ((region.Flags & GeoRegion.FlagIsSphere) != 0)
            {
                region.Radius = r.ReadF32();
            }

            section.Regions.Add(region);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Regions.Count);
        foreach (GeoRegion region in Regions)
        {
            w.WriteI32(region.Uid);
            w.WriteU16(region.Flags);
            w.WriteU16(region.Hardness);

            if ((region.Flags & GeoRegion.FlagUseShallowGeomods) != 0)
            {
                w.WriteF32(region.ShallowGeomodDepth ?? 0f);
            }

            w.WriteVec3(region.Position);

            if ((region.Flags & GeoRegion.FlagIsBox) != 0)
            {
                w.WriteMat3(region.Rotation ?? Mat3.Identity);
                w.WriteF32(region.Width ?? 0f);
                w.WriteF32(region.Height ?? 0f);
                w.WriteF32(region.Depth ?? 0f);
            }

            if ((region.Flags & GeoRegion.FlagIsSphere) != 0)
            {
                w.WriteF32(region.Radius ?? 0f);
            }
        }
    }
}
