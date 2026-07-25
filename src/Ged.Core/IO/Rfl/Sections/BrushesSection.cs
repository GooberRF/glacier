using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>brushes (0x2000000): the editable source brushes of the level.</summary>
public sealed class BrushesSection : IRflSectionContent
{
    public SectionType Type => SectionType.Brushes;

    public List<Brush> Brushes { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new BrushesSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            // NB: no plane-sign repair here. A static brush's stored face planes are inert in-game —
            // the CSG compiler recomputes every static plane from vertices — so an old build's inverted
            // brush plane never reaches the game, and rewriting it here would break byte-identity for
            // levels this editor previously saved. The mover path (which RF DOES read verbatim for
            // collision) is repaired in MoversSection.Parse, and a brush converted to a mover has its
            // planes recomputed at creation (MoverService.CreateMover).
            section.Brushes.Add(Brush.Read(r, ctx));
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Brushes.Count);
        foreach (Brush b in Brushes)
        {
            b.Write(w, ctx);
        }
    }
}
