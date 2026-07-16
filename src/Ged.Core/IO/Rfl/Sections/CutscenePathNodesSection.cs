using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>cutscene_path_nodes (0x5000): each item is a plain object header.</summary>
public sealed class CutscenePathNodesSection : IRflSectionContent
{
    public SectionType Type => SectionType.CutscenePathNodes;

    public List<ObjectHeader> Nodes { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new CutscenePathNodesSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Nodes.Add(ObjectHeader.Read(r));
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Nodes.Count);
        foreach (ObjectHeader n in Nodes)
        {
            n.Write(w);
        }
    }
}
