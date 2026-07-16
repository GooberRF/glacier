using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>cutscene_paths (0x6000): named ordered lists of path-node UIDs.</summary>
public sealed class CutscenePathsSection : IRflSectionContent
{
    public SectionType Type => SectionType.CutscenePaths;

    public List<CutscenePath> Paths { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new CutscenePathsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var path = new CutscenePath { Name = r.ReadVString() };
            int numNodes = r.ReadI32();
            for (int j = 0; j < numNodes; j++)
            {
                path.PathNodes.Add(r.ReadI32());
            }

            section.Paths.Add(path);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Paths.Count);
        foreach (CutscenePath path in Paths)
        {
            w.WriteVString(path.Name);
            w.WriteI32(path.PathNodes.Count);
            foreach (int node in path.PathNodes)
            {
                w.WriteI32(node);
            }
        }
    }
}
