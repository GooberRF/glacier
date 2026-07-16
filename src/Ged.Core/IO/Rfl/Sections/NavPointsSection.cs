using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// nav_points (0x20000): AI navigation points followed by one connection list
/// per nav point. cover/hide flags are preserved exactly (RED clears them on
/// save — a stock bug GED avoids by construction).
/// </summary>
public sealed class NavPointsSection : IRflSectionContent
{
    public SectionType Type => SectionType.NavPoints;

    public List<NavPoint> NavPoints { get; set; } = new();

    /// <summary>One connection list per nav point (parallel to <see cref="NavPoints"/>).</summary>
    public List<List<int>> Connections { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new NavPointsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.NavPoints.Add(NavPoint.Read(r));
        }

        for (int i = 0; i < count; i++)
        {
            int numIndices = r.ReadU8();
            var indices = new List<int>(numIndices);
            for (int j = 0; j < numIndices; j++)
            {
                indices.Add(r.ReadI32());
            }

            section.Connections.Add(indices);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(NavPoints.Count);
        foreach (NavPoint np in NavPoints)
        {
            np.Write(w);
        }

        foreach (List<int> indices in Connections)
        {
            w.WriteU8((byte)indices.Count);
            foreach (int index in indices)
            {
                w.WriteI32(index);
            }
        }
    }
}
