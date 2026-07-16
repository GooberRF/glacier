using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;
using Ged.Core.Packaging.Graph;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the shared Sugiyama-lite layout engine: zero overlapping node rects on
/// dense seeded graphs (engine-level with mixed sizes, link-graph level with real
/// canvas sizes, dependency-graph level on a representative tree), left → right
/// layer structure, determinism, and barycenter crossing reduction vs naive order.
/// </summary>
public sealed class GraphLayoutEngineTests
{
    private readonly record struct SizedRect(double X, double Y, double W, double H);

    private static bool Overlap(SizedRect a, SizedRect b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private static void AssertNoOverlaps(IReadOnlyList<SizedRect> rects, IReadOnlyList<int> keys)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                Assert.False(
                    Overlap(rects[i], rects[j]),
                    $"nodes {keys[i]} and {keys[j]} overlap: {rects[i]} vs {rects[j]}");
            }
        }
    }

    // ─── Engine: dense graph, mixed sizes ────────────────────────────────────

    [Fact]
    public void Engine_Dense_Mixed_Size_Graph_Has_Zero_Overlapping_Rects()
    {
        var rnd = new Random(20260708);
        var nodes = new List<GraphLayoutNode>();
        for (int i = 1; i <= 60; i++)
        {
            nodes.Add(new GraphLayoutNode(
                Key: i,
                Width: 100 + rnd.Next(120),
                Height: 30 + rnd.Next(40),
                LayerHint: rnd.Next(5)));
        }

        var edges = new List<GraphLayoutEdge>();
        for (int i = 2; i <= 60; i++)
        {
            edges.Add(new GraphLayoutEdge(1 + rnd.Next(i - 1), i)); // connected-ish
        }

        for (int i = 0; i < 40; i++)
        {
            edges.Add(new GraphLayoutEdge(1 + rnd.Next(60), 1 + rnd.Next(60))); // extra chaos
        }

        // A deliberate cycle — layering must still terminate and stay sane.
        edges.Add(new GraphLayoutEdge(5, 6));
        edges.Add(new GraphLayoutEdge(6, 7));
        edges.Add(new GraphLayoutEdge(7, 5));

        Dictionary<int, GraphNodePos> pos = GraphLayoutEngine.Layout(nodes, edges);
        Assert.Equal(nodes.Count, pos.Count);

        var keys = nodes.Select(n => n.Key).ToList();
        var rects = nodes.Select(n => new SizedRect(pos[n.Key].X, pos[n.Key].Y, n.Width, n.Height)).ToList();
        AssertNoOverlaps(rects, keys);

        // Deterministic: a second run yields identical positions.
        Dictionary<int, GraphNodePos> again = GraphLayoutEngine.Layout(nodes, edges);
        foreach (GraphLayoutNode n in nodes)
        {
            Assert.Equal(pos[n.Key], again[n.Key]);
        }
    }

    // ─── Link graph: dense, real canvas sizes ────────────────────────────────

    [Fact]
    public void AutoLayout_Dense_50_Node_LinkGraph_Has_Zero_Overlapping_Rects()
    {
        var kinds = new[]
        {
            LevelObjectKind.Trigger,
            LevelObjectKind.Event,
            LevelObjectKind.Mover,
            LevelObjectKind.Target,
            LevelObjectKind.Entity, // → Other bucket
        };

        var nodes = new List<LinkGraphNode>();
        for (int i = 1; i <= 50; i++)
        {
            nodes.Add(new LinkGraphNode { Uid = 100 + i, Kind = kinds[i % kinds.Length] });
        }

        var rnd = new Random(1234);
        var edges = new List<LinkGraphEdge>();
        for (int i = 1; i < 50; i++)
        {
            edges.Add(new LinkGraphEdge(nodes[rnd.Next(i)].Uid, nodes[i].Uid));
        }

        for (int i = 0; i < 30; i++)
        {
            int a = rnd.Next(50);
            int b = rnd.Next(50);
            if (a != b)
            {
                edges.Add(new LinkGraphEdge(nodes[a].Uid, nodes[b].Uid));
            }
        }

        var graph = new LinkGraph(nodes, edges);
        var metrics = new GraphLayoutMetrics();
        GraphLayout layout = GraphAutoLayout.Build(graph, metrics);
        Assert.Equal(nodes.Count, layout.Count);

        var keys = nodes.Select(n => n.Uid).ToList();
        var rects = keys.Select(uid =>
        {
            Assert.True(layout.TryGet(uid, out double x, out double y));
            return new SizedRect(x, y, metrics.NodeWidth, metrics.NodeHeight);
        }).ToList();
        AssertNoOverlaps(rects, keys);
    }

    [Fact]
    public void AutoLayout_Additive_New_Nodes_Never_Overlap_Saved_Rects()
    {
        var nodes = new List<LinkGraphNode>();
        for (int i = 1; i <= 12; i++)
        {
            nodes.Add(new LinkGraphNode { Uid = i, Kind = LevelObjectKind.Trigger });
        }

        var graph = new LinkGraph(nodes, Array.Empty<LinkGraphEdge>());
        var metrics = new GraphLayoutMetrics();
        var layout = new GraphLayout();

        // Six user-arranged nodes scattered right where the trigger column would place
        // newcomers — additive layout must dodge every one of them.
        for (int i = 1; i <= 6; i++)
        {
            layout.Set(i, metrics.OriginX + ((i % 2) * 30), metrics.OriginY + ((i - 1) * 55));
        }

        GraphAutoLayout.Apply(graph, layout, relayoutAll: false, metrics);
        Assert.Equal(12, layout.Count);

        // Saved positions untouched.
        layout.TryGet(1, out double x1, out double y1);
        Assert.Equal(metrics.OriginX + 30, x1, 6);
        Assert.Equal(metrics.OriginY, y1, 6);

        var keys = nodes.Select(n => n.Uid).ToList();
        var rects = keys.Select(uid =>
        {
            layout.TryGet(uid, out double x, out double y);
            return new SizedRect(x, y, metrics.NodeWidth, metrics.NodeHeight);
        }).ToList();
        AssertNoOverlaps(rects, keys);
    }

    // ─── Dependency graph: representative tree ───────────────────────────────

    [Fact]
    public void DependencyLayout_Tree_Has_Zero_Overlaps_And_Reads_Left_To_Right()
    {
        var nodes = new List<DependencyGraphNode>
        {
            new() { Id = 0, NodeKind = DependencyNodeKind.Level, Label = "level.rfl" },
        };
        var edges = new List<DependencyGraphEdge>();

        // Three categories with six direct files each; every third file carries a
        // nested child (mesh texture / ATX frame analogue).
        int id = 1;
        foreach (DependencyCategory cat in new[] { DependencyCategory.Textures, DependencyCategory.Meshes, DependencyCategory.Sounds })
        {
            var cnode = new DependencyGraphNode { Id = id++, NodeKind = DependencyNodeKind.Category, Label = cat.ToString(), Category = cat };
            nodes.Add(cnode);
            edges.Add(new DependencyGraphEdge(0, cnode.Id, Nested: false));
            for (int f = 0; f < 6; f++)
            {
                var file = new DependencyGraphNode { Id = id++, NodeKind = DependencyNodeKind.File, Label = $"{cat}_{f}.dat", Category = cat };
                nodes.Add(file);
                edges.Add(new DependencyGraphEdge(cnode.Id, file.Id, Nested: false));
                if (f % 3 == 0)
                {
                    var nested = new DependencyGraphNode { Id = id++, NodeKind = DependencyNodeKind.File, Label = $"{cat}_{f}_child.dat", Category = cat, Nested = true };
                    nodes.Add(nested);
                    edges.Add(new DependencyGraphEdge(file.Id, nested.Id, Nested: true));
                }
            }
        }

        var graph = new DependencyGraph(nodes, edges);
        var metrics = new DependencyGraphLayoutMetrics();
        IReadOnlyDictionary<int, GraphNodePos> pos = DependencyGraphLayout.Build(graph, metrics);
        Assert.Equal(nodes.Count, pos.Count);

        var keys = nodes.Select(n => n.Id).ToList();
        var rects = nodes.Select(n => new SizedRect(
            pos[n.Id].X,
            pos[n.Id].Y,
            n.NodeKind == DependencyNodeKind.File ? metrics.FileNodeWidth : metrics.NodeWidth,
            metrics.NodeHeight)).ToList();
        AssertNoOverlaps(rects, keys);

        // Level → Category → File → nested file, strictly left to right.
        double rootX = pos[0].X;
        double catX = graph.Categories.Min(c => pos[c.Id].X);
        double fileX = graph.Files.Where(f => !f.Nested).Min(f => pos[f.Id].X);
        double nestedX = graph.Files.Where(f => f.Nested).Min(f => pos[f.Id].X);
        Assert.True(rootX < catX, $"root {rootX} !< category {catX}");
        Assert.True(catX < fileX, $"category {catX} !< file {fileX}");
        Assert.True(fileX < nestedX, $"file {fileX} !< nested {nestedX}");
    }

    // ─── Crossing reduction ──────────────────────────────────────────────────

    [Fact]
    public void Barycenter_Ordering_Removes_Crossings_Naive_Order_Keeps()
    {
        // Bipartite reversal: 1→6, 2→5, 3→4. Naive key order has all three edges
        // crossing pairwise; barycenter ordering untangles it completely.
        var nodes = new List<GraphLayoutNode>();
        for (int k = 1; k <= 3; k++)
        {
            nodes.Add(new GraphLayoutNode(k, 168, 46, 0));
        }

        for (int k = 4; k <= 6; k++)
        {
            nodes.Add(new GraphLayoutNode(k, 168, 46, 1));
        }

        var edges = new List<GraphLayoutEdge> { new(1, 6), new(2, 5), new(3, 4) };

        Dictionary<int, GraphNodePos> naive = GraphLayoutEngine.Layout(
            nodes, edges, new GraphLayoutEngineOptions { OrderingSweeps = 0 });
        Dictionary<int, GraphNodePos> tuned = GraphLayoutEngine.Layout(nodes, edges);

        Assert.Equal(3, CountCrossings(edges, naive));
        Assert.Equal(0, CountCrossings(edges, tuned));
    }

    private static int CountCrossings(IReadOnlyList<GraphLayoutEdge> edges, Dictionary<int, GraphNodePos> pos)
    {
        int crossings = 0;
        for (int i = 0; i < edges.Count; i++)
        {
            for (int j = i + 1; j < edges.Count; j++)
            {
                GraphLayoutEdge a = edges[i];
                GraphLayoutEdge b = edges[j];
                bool sameSpan = Math.Abs(pos[a.From].X - pos[b.From].X) < 0.01
                    && Math.Abs(pos[a.To].X - pos[b.To].X) < 0.01;
                if (sameSpan &&
                    (pos[a.From].Y - pos[b.From].Y) * (pos[a.To].Y - pos[b.To].Y) < 0)
                {
                    crossings++;
                }
            }
        }

        return crossings;
    }
}
