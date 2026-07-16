using System.Linq;
using Ged.App.Panels;
using Ged.Core.Packaging.Graph;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 6: the Dependencies panel's filename filter narrows the visible file nodes to those whose
/// name matches (live, case-insensitive substring — the Link Graph search pattern), keeping their
/// category/level ancestors as anchors. Exercised at the model level via the pure filter helper.
/// </summary>
public sealed class DependencyFilterTests
{
    private static DependencyGraph SampleGraph()
    {
        var level = new DependencyGraphNode { Id = 0, NodeKind = DependencyNodeKind.Level, Label = "level.rfl" };
        var textures = new DependencyGraphNode { Id = 1, NodeKind = DependencyNodeKind.Category, Label = "Textures", Category = DependencyCategory.Textures };
        var meshes = new DependencyGraphNode { Id = 2, NodeKind = DependencyNodeKind.Category, Label = "Meshes", Category = DependencyCategory.Meshes };
        var wall01 = new DependencyGraphNode { Id = 3, NodeKind = DependencyNodeKind.File, Label = "wall01.tga", Category = DependencyCategory.Textures };
        var floor = new DependencyGraphNode { Id = 4, NodeKind = DependencyNodeKind.File, Label = "floor.tga", Category = DependencyCategory.Textures };
        var crate = new DependencyGraphNode { Id = 5, NodeKind = DependencyNodeKind.File, Label = "crate.v3m", Category = DependencyCategory.Meshes };

        var nodes = new[] { level, textures, meshes, wall01, floor, crate };
        var edges = new[]
        {
            new DependencyGraphEdge(0, 1, false),
            new DependencyGraphEdge(0, 2, false),
            new DependencyGraphEdge(1, 3, false),
            new DependencyGraphEdge(1, 4, false),
            new DependencyGraphEdge(2, 5, false),
        };
        return new DependencyGraph(nodes, edges);
    }

    private static int FileCount(DependencyGraph g) => g.Nodes.Count(n => n.NodeKind == DependencyNodeKind.File);

    [Fact]
    public void Filter_Narrows_To_Matching_Files_And_Keeps_Ancestors()
    {
        DependencyGraph g = SampleGraph();
        Assert.Equal(3, FileCount(g));

        DependencyGraph filtered = DependencyGraphPanel.FilterByFileName(g, "wall");

        Assert.Equal(1, FileCount(filtered));
        Assert.Contains(filtered.Nodes, n => n.Label == "wall01.tga");
        Assert.DoesNotContain(filtered.Nodes, n => n.Label == "floor.tga");

        // The matching file's category + the level root survive as anchors; the unrelated Meshes
        // category (no match) drops out.
        Assert.Contains(filtered.Nodes, n => n.NodeKind == DependencyNodeKind.Level);
        Assert.Contains(filtered.Nodes, n => n.Label == "Textures");
        Assert.DoesNotContain(filtered.Nodes, n => n.Label == "Meshes");
    }

    [Fact]
    public void Filter_Is_Case_Insensitive()
    {
        DependencyGraph filtered = DependencyGraphPanel.FilterByFileName(SampleGraph(), "CRATE");
        Assert.Equal(1, FileCount(filtered));
        Assert.Contains(filtered.Nodes, n => n.Label == "crate.v3m");
    }

    [Fact]
    public void Blank_Filter_Returns_The_Graph_Unchanged()
    {
        DependencyGraph g = SampleGraph();
        Assert.Same(g, DependencyGraphPanel.FilterByFileName(g, "   "));
        Assert.Same(g, DependencyGraphPanel.FilterByFileName(g, null));
    }
}
