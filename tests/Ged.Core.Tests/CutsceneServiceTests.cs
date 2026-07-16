using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Cutscene path gate: build a named path over three placed nodes, reorder, save,
/// reload, and assert both the cutscene_paths (0x6000) and cutscene_path_nodes
/// (0x5000) sections round-trip with the node order preserved.
/// </summary>
public class CutsceneServiceTests
{
    [Fact]
    public void Build_Cutscene_Path_Round_Trips()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "cut.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);
        var cut = new CutsceneService(doc);

        ObjectHeader n0 = cut.AddNode(new Vec3(0, 0, 0), Mat3.Identity);
        ObjectHeader n1 = cut.AddNode(new Vec3(10, 0, 0), Mat3.Identity);
        ObjectHeader n2 = cut.AddNode(new Vec3(20, 5, 0), Mat3.Identity);

        CutscenePath path = cut.CreatePath("FlyBy");
        cut.AppendNode(path, n0.Uid);
        cut.AppendNode(path, n1.Uid);
        cut.AppendNode(path, n2.Uid);

        // Reorder: move the last node to the front.
        cut.ReorderNode(path, 2, 0);
        Assert.Equal(new[] { n2.Uid, n0.Uid, n1.Uid }, path.PathNodes);

        // ---- save + reload -----------------------------------------------------
        byte[] saved = doc.SaveToBytes();
        var reloaded = RflFile.Load(saved);
        reloaded.ParseAllKnownSections();

        CutscenePathsSection paths = reloaded.Sections.Select(s => s.Content).OfType<CutscenePathsSection>().Single();
        CutscenePathNodesSection nodes = reloaded.Sections.Select(s => s.Content).OfType<CutscenePathNodesSection>().Single();

        Assert.Equal(3, nodes.Nodes.Count);
        CutscenePath p = Assert.Single(paths.Paths);
        Assert.Equal("FlyBy", p.Name);
        Assert.Equal(new[] { n2.Uid, n0.Uid, n1.Uid }, p.PathNodes);

        // Path nodes project as selectable level objects.
        Assert.Equal(3, doc.Objects.Count(o => o.Kind == LevelObjectKind.CutscenePathNode));

        // Re-save is byte-stable.
        Assert.Equal(saved, reloaded.Save());
    }
}
