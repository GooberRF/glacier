using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>movers (0x2000): brushes that move; identical layout to the brushes section.</summary>
public sealed class MoversSection : IRflSectionContent
{
    public SectionType Type => SectionType.Movers;

    public List<Brush> Movers { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new MoversSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            Brush b = Brush.Read(r, ctx);
            // RF builds a mover's collision hull from these stored face planes (verbatim), so heal any
            // sign-inverted planes an earlier build wrote — otherwise the mover animates but collides wrong.
            PlaneSignRepair.RepairBrushGeometry(b.Geometry);
            section.Movers.Add(b);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Movers.Count);
        foreach (Brush b in Movers)
        {
            b.Write(w, ctx);
        }
    }
}
