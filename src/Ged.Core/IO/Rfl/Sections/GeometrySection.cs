using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>static_geometry (0x100): the compiled world geometry the game consumes.</summary>
public sealed class GeometrySection : IRflSectionContent
{
    public SectionType Type => SectionType.StaticGeometry;

    public Geometry Geometry { get; set; } = new();

    public static IRflSectionContent ParseStatic(RfReader r, RflContext ctx) =>
        new GeometrySection { Geometry = Geometry.Parse(r, ctx) };

    public void Write(RfWriter w, RflContext ctx) => Geometry.Write(w, ctx);
}
