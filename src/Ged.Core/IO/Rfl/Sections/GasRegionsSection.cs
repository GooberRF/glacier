using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>gas_regions (0xB00).</summary>
public sealed class GasRegionsSection : IRflSectionContent
{
    public const int ShapeSphere = 1;
    public const int ShapeBox = 2;

    public SectionType Type => SectionType.GasRegions;

    public List<GasRegion> Regions { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new GasRegionsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var region = new GasRegion
            {
                Header = ObjectHeader.Read(r),
                Shape = r.ReadI32(),
            };

            if (region.Shape == ShapeSphere)
            {
                region.Radius = r.ReadF32();
            }
            else if (region.Shape == ShapeBox)
            {
                region.Height = r.ReadF32();
                region.Width = r.ReadF32();
                region.Depth = r.ReadF32();
            }

            region.GasColor = r.ReadColor();
            region.GasDensity = r.ReadF32();
            section.Regions.Add(region);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Regions.Count);
        foreach (GasRegion region in Regions)
        {
            region.Header.Write(w);
            w.WriteI32(region.Shape);

            if (region.Shape == ShapeSphere)
            {
                w.WriteF32(region.Radius ?? 0f);
            }
            else if (region.Shape == ShapeBox)
            {
                w.WriteF32(region.Height ?? 0f);
                w.WriteF32(region.Width ?? 0f);
                w.WriteF32(region.Depth ?? 0f);
            }

            w.WriteColor(region.GasColor);
            w.WriteF32(region.GasDensity);
        }
    }
}
