using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing.Graph;

namespace Ged.Core.Packaging.Graph;

/// <summary>Node sizes and spacing for the dependency-graph layout (mirrors the panel's canvas sizes).</summary>
public sealed class DependencyGraphLayoutMetrics
{
    public double OriginX { get; init; } = 40;

    public double OriginY { get; init; } = 40;

    /// <summary>Width of Level and Category nodes on the canvas.</summary>
    public double NodeWidth { get; init; } = 168;

    /// <summary>Width of File nodes on the canvas (wider for file names).</summary>
    public double FileNodeWidth { get; init; } = 190;

    public double NodeHeight { get; init; } = 46;

    /// <summary>Horizontal gap between a layer's widest node and the next layer.</summary>
    public double ColumnGap { get; init; } = 62;

    /// <summary>Vertical gap between stacked nodes within a layer.</summary>
    public double RowGap { get; init; } = 12;
}

/// <summary>
/// Layout for the Dependency Graph, built on the shared <see cref="GraphLayoutEngine"/>
/// so it is core-testable: tree depth from the level root seeds each node's layer
/// (Level → Category → File → nested file, left to right), barycenter sweeps order
/// files near their category/parent, and coordinates come from real node sizes with
/// minimum gaps — no two nodes can overlap.
/// </summary>
public static class DependencyGraphLayout
{
    /// <summary>Computes top-left positions for every graph node, keyed by node id.</summary>
    public static IReadOnlyDictionary<int, GraphNodePos> Build(
        DependencyGraph graph, DependencyGraphLayoutMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        metrics ??= new DependencyGraphLayoutMetrics();

        if (graph.Nodes.Count == 0)
        {
            return new Dictionary<int, GraphNodePos>();
        }

        Dictionary<int, int> depth = TreeDepths(graph);
        var nodes = graph.Nodes
            .Select(n => new GraphLayoutNode(
                n.Id,
                n.NodeKind == DependencyNodeKind.File ? metrics.FileNodeWidth : metrics.NodeWidth,
                metrics.NodeHeight,
                depth[n.Id]))
            .ToList();
        var edges = graph.Edges.Select(e => new GraphLayoutEdge(e.FromId, e.ToId)).ToList();
        var options = new GraphLayoutEngineOptions
        {
            OriginX = metrics.OriginX,
            OriginY = metrics.OriginY,
            ColumnGap = metrics.ColumnGap,
            RowGap = metrics.RowGap,
        };

        return GraphLayoutEngine.Layout(nodes, edges, options);
    }

    /// <summary>
    /// BFS depth from the level root(s) over all edges (tree + nested). Nodes not
    /// reached from a root (defensive) fall back to a kind-based depth.
    /// </summary>
    private static Dictionary<int, int> TreeDepths(DependencyGraph graph)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (DependencyGraphEdge e in graph.Edges)
        {
            if (!children.TryGetValue(e.FromId, out List<int>? list))
            {
                list = new List<int>();
                children[e.FromId] = list;
            }

            list.Add(e.ToId);
        }

        var depth = new Dictionary<int, int>(graph.Nodes.Count);
        var queue = new Queue<int>();
        foreach (DependencyGraphNode n in graph.Nodes.Where(n => n.NodeKind == DependencyNodeKind.Level))
        {
            depth[n.Id] = 0;
            queue.Enqueue(n.Id);
        }

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!children.TryGetValue(cur, out List<int>? kids))
            {
                continue;
            }

            foreach (int kid in kids)
            {
                if (!depth.ContainsKey(kid))
                {
                    depth[kid] = depth[cur] + 1;
                    queue.Enqueue(kid);
                }
            }
        }

        foreach (DependencyGraphNode n in graph.Nodes)
        {
            if (!depth.ContainsKey(n.Id))
            {
                depth[n.Id] = n.NodeKind switch
                {
                    DependencyNodeKind.Level => 0,
                    DependencyNodeKind.Category => 1,
                    _ => 2,
                };
            }
        }

        return depth;
    }
}
