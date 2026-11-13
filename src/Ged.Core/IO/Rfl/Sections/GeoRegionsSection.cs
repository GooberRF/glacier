using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// geo_regions (0x200): geomod hardness / ice / shallow-geomod volumes.
///
/// <para>
/// Record layout follows the engine's reader (RF.exe level_read_geo_regions @ 0x00461B70) for
/// every version GED loads (&gt;= 0xA3): uid, then a u32 whose low 16 bits are the flags and
/// whose high 16 bits are the hardness, then the shallow-geomod depth when flag 0x20 is set,
/// then a payload chosen by the shape code <c>flags &amp; 7</c> — not by testing the sphere and
/// box bits independently:
/// <list type="bullet">
/// <item>2 (sphere): position, a rotation matrix only when shallow geomods are on, radius;</item>
/// <item>4 (box): position, rotation matrix, width/height/depth;</item>
/// <item>3: two points and a radius (read and discarded by the engine);</item>
/// <item>any other code: nothing more.</item>
/// </list>
/// The shallow-sphere matrix is what the older field-by-field reading missed: a stock
/// single-player level stores one and was rejected before.
/// </para>
/// </summary>
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

            if (region.UsesShallowGeomods)
            {
                region.ShallowGeomodDepth = r.ReadF32();
            }

            switch (region.Shape)
            {
                case GeoRegion.ShapeSphere:
                    region.Position = r.ReadVec3();
                    if (region.HasRotation)
                    {
                        region.Rotation = r.ReadMat3();
                    }

                    region.Radius = r.ReadF32();
                    break;

                case GeoRegion.ShapeBox:
                    region.Position = r.ReadVec3();
                    region.Rotation = r.ReadMat3();
                    region.Width = r.ReadF32();
                    region.Height = r.ReadF32();
                    region.Depth = r.ReadF32();
                    break;

                case GeoRegion.ShapeTwoPoint:
                    region.Position = r.ReadVec3();
                    region.EndPoint = r.ReadVec3();
                    region.Radius = r.ReadF32();
                    break;
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

            if (region.UsesShallowGeomods)
            {
                w.WriteF32(region.ShallowGeomodDepth ?? 0f);
            }

            switch (region.Shape)
            {
                case GeoRegion.ShapeSphere:
                    w.WriteVec3(region.Position);
                    if (region.HasRotation)
                    {
                        w.WriteMat3(region.Rotation ?? Mat3.Identity);
                    }

                    w.WriteF32(region.Radius ?? 0f);
                    break;

                case GeoRegion.ShapeBox:
                    w.WriteVec3(region.Position);
                    w.WriteMat3(region.Rotation ?? Mat3.Identity);
                    w.WriteF32(region.Width ?? 0f);
                    w.WriteF32(region.Height ?? 0f);
                    w.WriteF32(region.Depth ?? 0f);
                    break;

                case GeoRegion.ShapeTwoPoint:
                    w.WriteVec3(region.Position);
                    w.WriteVec3(region.EndPoint ?? region.Position);
                    w.WriteF32(region.Radius ?? 0f);
                    break;
            }
        }
    }
}
