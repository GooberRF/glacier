using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Editing.Graph;

/// <summary>Tunable spacing for the layered auto-layout.</summary>
public sealed class GraphLayoutMetrics
{
    public double OriginX { get; init; } = 40;

    public double OriginY { get; init; } = 40;

    /// <summary>Nominal column pitch (node width + inter-column gap).</summary>
    public double ColumnSpacing { get; init; } = 260;

    /// <summary>Nominal row pitch (node height + inter-row gap).</summary>
    public double RowSpacing { get; init; } = 64;

    /// <summary>The canvas node width used for overlap-free coordinate assignment.</summary>
    public double NodeWidth { get; init; } = 168;

    /// <summary>The canvas node height used for overlap-free coordinate assignment.</summary>
    public double NodeHeight { get; init; } = 46;

    /// <summary>Horizontal gap between layers derived from the column pitch.</summary>
    public double ColumnGap => Math.Max(24, ColumnSpacing - NodeWidth);

    /// <summary>Vertical gap between stacked nodes derived from the row pitch.</summary>
    public double RowGap => Math.Max(8, RowSpacing - NodeHeight);
}

/// <summary>
/// Layered auto-layout for the link graph, built on the shared
/// <see cref="GraphLayoutEngine"/>: the kind column (Trigger → Event → Mover →
/// Target → Other) seeds each node's layer, longest-path distance pushes chains
/// left → right, barycenter sweeps order rows to reduce crossings, and coordinates
/// come from real node sizes with minimum gaps so nodes never overlap.
/// <see cref="Apply"/> either repositions every node (re-layout all) or only nodes
/// that have no saved position — in the latter case new nodes are placed in their
/// kind column below the occupied region, overlap-checked against the real rects of
/// everything already arranged, so additions never land on top of an arranged graph.
/// </summary>
public static class GraphAutoLayout
{
    private static readonly GraphNodeCategory[] ColumnOrder =
    {
        GraphNodeCategory.Trigger,
        GraphNodeCategory.Event,
        GraphNodeCategory.Mover,
        GraphNodeCategory.Target,
        GraphNodeCategory.Other,
    };

    /// <summary>
    /// Places graph nodes into <paramref name="layout"/>. When
    /// <paramref name="relayoutAll"/> is false, nodes already present in the layout
    /// keep their positions and only unplaced nodes are arranged.
    /// </summary>
    public static void Apply(LinkGraph graph, GraphLayout layout, bool relayoutAll, GraphLayoutMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(layout);
        metrics ??= new GraphLayoutMetrics();

        // No saved position for any visible node (fresh graph / absent sidecar):
        // additive mode has nothing to preserve, so run the full engine layout.
        if (relayoutAll || graph.Nodes.All(n => !layout.Has(n.Uid)))
        {
            LayoutAll(graph, layout, metrics);
        }
        else
        {
            PlaceNewNodes(graph, layout, metrics);
        }
    }

    /// <summary>Builds a fresh layout for the graph (used for the offscreen artifact and tests).</summary>
    public static GraphLayout Build(LinkGraph graph, GraphLayoutMetrics? metrics = null)
    {
        var layout = new GraphLayout();
        Apply(graph, layout, relayoutAll: true, metrics);
        return layout;
    }

    private static int ColumnOf(GraphNodeCategory c) => Array.IndexOf(ColumnOrder, c);

    private static void LayoutAll(LinkGraph graph, GraphLayout layout, GraphLayoutMetrics metrics)
    {
        var nodes = graph.Nodes
            .Select(n => new GraphLayoutNode(n.Uid, metrics.NodeWidth, metrics.NodeHeight, ColumnOf(n.Category)))
            .ToList();
        var edges = graph.Edges.Select(e => new GraphLayoutEdge(e.From, e.To)).ToList();
        var options = new GraphLayoutEngineOptions
        {
            OriginX = metrics.OriginX,
            OriginY = metrics.OriginY,
            ColumnGap = metrics.ColumnGap,
            RowGap = metrics.RowGap,
        };

        foreach (KeyValuePair<int, GraphNodePos> kv in GraphLayoutEngine.Layout(nodes, edges, options))
        {
            layout.Set(kv.Key, kv.Value.X, kv.Value.Y);
        }
    }

    /// <summary>
    /// Additive placement: saved positions stay; each new node goes into its kind
    /// column below the column's occupied region, then slides further down while its
    /// real rect intersects any already-placed rect.
    /// </summary>
    private static void PlaceNewNodes(LinkGraph graph, GraphLayout layout, GraphLayoutMetrics metrics)
    {
        double w = metrics.NodeWidth;
        double h = metrics.NodeHeight;

        // Real rects of everything already arranged (saved nodes in this graph).
        var occupied = new List<(double X, double Y)>();
        var cursor = new double[ColumnOrder.Length];
        for (int i = 0; i < cursor.Length; i++)
        {
            cursor[i] = metrics.OriginY;
        }

        foreach (LinkGraphNode n in graph.Nodes)
        {
            if (layout.TryGet(n.Uid, out double x, out double y))
            {
                occupied.Add((x, y));
                int col = ColumnOf(n.Category);
                cursor[col] = Math.Max(cursor[col], y + h + metrics.RowGap);
            }
        }

        bool Overlaps(double x, double y) =>
            occupied.Any(r => x < r.X + w && r.X < x + w && y < r.Y + h && r.Y < y + h);

        foreach (LinkGraphNode n in graph.Nodes.OrderBy(n => ColumnOf(n.Category)).ThenBy(n => n.Uid))
        {
            if (layout.Has(n.Uid))
            {
                continue;
            }

            int col = ColumnOf(n.Category);
            double x = metrics.OriginX + (col * metrics.ColumnSpacing);
            double y = cursor[col];
            while (Overlaps(x, y))
            {
                y += h + metrics.RowGap;
            }

            layout.Set(n.Uid, x, y);
            occupied.Add((x, y));
            cursor[col] = y + h + metrics.RowGap;
        }
    }
}
