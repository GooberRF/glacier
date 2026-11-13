using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>static_geometry (0x100): the compiled world geometry the game consumes.</summary>
public sealed class GeometrySection : IRflSectionContent
{
    public SectionType Type => SectionType.StaticGeometry;

    public Geometry Geometry { get; set; } = new();

    /// <summary>
    /// Section parser: the engine's layout, falling back to the wide lightmap-UV variant
    /// (<see cref="Geometry.UsesWideSurfaceLightmapUvs"/>) only when the engine's layout does not
    /// consume the section and the variant does — so the fallback can only affect levels with
    /// surface indices past 32767 that the engine layout rejects.
    /// </summary>
    public static IRflSectionContent ParseStatic(RfReader r, RflContext ctx)
    {
        int start = r.Position;
        Exception? strictError = null;
        Geometry? strict = null;
        try
        {
            strict = Geometry.Parse(r, ctx);
            if (r.Remaining == 0)
            {
                return new GeometrySection { Geometry = strict };
            }
        }
        catch (EndOfStreamException ex)
        {
            strictError = ex;
        }

        int strictEnd = r.Position;
        r.Position = start;
        try
        {
            Geometry wide = Geometry.Parse(r, ctx, wideSurfaceLightmapUvs: true);
            if (r.Remaining == 0)
            {
                return new GeometrySection { Geometry = wide };
            }
        }
        catch (EndOfStreamException)
        {
            // Not the variant either.
        }

        if (strictError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(strictError).Throw();
        }

        r.Position = strictEnd;
        return new GeometrySection { Geometry = strict! };
    }

    public void Write(RfWriter w, RflContext ctx) => Geometry.Write(w, ctx);
}
