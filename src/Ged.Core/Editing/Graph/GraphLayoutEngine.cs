using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Editing.Graph;

/// <summary>A node fed to the shared layout engine: a stable key, its real size, and a base layer hint.</summary>
public readonly record struct GraphLayoutNode(int Key, double Width, double Height, int LayerHint);

/// <summary>A directed edge fed to the shared layout engine (by node key).</summary>
public readonly record struct GraphLayoutEdge(int From, int To);

/// <summary>Tunable spacing and ordering behaviour for <see cref="GraphLayoutEngine"/>.</summary>
public sealed class GraphLayoutEngineOptions
{
    /// <summary>Left edge of the first layer.</summary>
    public double OriginX { get; init; } = 40;

    /// <summary>Top edge of the tallest layer.</summary>
    public double OriginY { get; init; } = 40;

    /// <summary>Horizontal gap between a layer's widest node and the next layer.</summary>
    public double ColumnGap { get; init; } = 92;

    /// <summary>Vertical gap between stacked nodes within a layer.</summary>
    public double RowGap { get; init; } = 18;

    /// <summary>
    /// Number of alternating barycenter sweeps used to order nodes within layers
    /// (crossing reduction). Zero keeps the naive key order (used by tests as the
    /// baseline the barycenter ordering is compared against).
    /// </summary>
    public int OrderingSweeps { get; init; } = 3;
}

/// <summary>
/// The shared, framework-free layered ("Sugiyama-lite") layout engine behind both
/// the Link Graph and the Dependency Graph. Layering starts from each node's layer
/// hint (kind column / tree depth) and is refined by longest-path graph distance so
/// chains read left → right; within-layer order is chosen by alternating barycenter
/// sweeps to reduce edge crossings; coordinates are assigned from real node sizes
/// with minimum gaps, so two nodes can never overlap by construction. The result is
/// deterministic: ties always break on the node key.
/// </summary>
public static class GraphLayoutEngine
{
    /// <summary>Computes top-left positions for every node, keyed by node key.</summary>
    public static Dictionary<int, GraphNodePos> Layout(
        IReadOnlyList<GraphLayoutNode> nodes,
        IReadOnlyList<GraphLayoutEdge> edges,
        GraphLayoutEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        options ??= new GraphLayoutEngineOptions();

        var result = new Dictionary<int, GraphNodePos>();
        if (nodes.Count == 0)
        {
            return result;
        }

        // Deduplicate keys defensively (last wins) and order deterministically.
        var byKey = new Dictionary<int, GraphLayoutNode>(nodes.Count);
        foreach (GraphLayoutNode n in nodes)
        {
            byKey[n.Key] = n;
        }

        List<int> keys = byKey.Keys.OrderBy(k => k).ToList();

        // Usable edges: both ends known, no self-loops, deduplicated.
        var cleanEdges = edges
            .Where(e => e.From != e.To && byKey.ContainsKey(e.From) && byKey.ContainsKey(e.To))
            .Distinct()
            .OrderBy(e => e.From).ThenBy(e => e.To)
            .ToList();

        Dictionary<int, int> layer = AssignLayers(byKey, keys, cleanEdges);
        List<List<int>> layers = BuildLayerLists(keys, layer);
        OrderWithinLayers(layers, layer, cleanEdges, options.OrderingSweeps);
        AssignCoordinates(byKey, layers, options, result);
        return result;
    }

    // ─── Layering ────────────────────────────────────────────────────────────

    /// <summary>
    /// Layer = the node's hint, pushed right by longest-path distance along the
    /// (cycle-broken) edge DAG so every kept edge points to a strictly later layer.
    /// </summary>
    private static Dictionary<int, int> AssignLayers(
        Dictionary<int, GraphLayoutNode> byKey, List<int> keys, List<GraphLayoutEdge> edges)
    {
        // Adjacency in deterministic order.
        var outEdges = new Dictionary<int, List<int>>();
        foreach (GraphLayoutEdge e in edges)
        {
            if (!outEdges.TryGetValue(e.From, out List<int>? list))
            {
                list = new List<int>();
                outEdges[e.From] = list;
            }

            list.Add(e.To);
        }

        // Break cycles: iterative DFS marking back-edges (target currently on the stack).
        var kept = new List<(int From, int To)>();
        var state = new Dictionary<int, int>(); // 0/absent = white, 1 = on stack, 2 = done
        foreach (int root in keys)
        {
            if (state.ContainsKey(root))
            {
                continue;
            }

            var stack = new Stack<(int Key, int NextChild)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                (int cur, int idx) = stack.Pop();
                List<int>? kids = outEdges.TryGetValue(cur, out List<int>? k) ? k : null;
                if (kids is null || idx >= kids.Count)
                {
                    state[cur] = 2;
                    continue;
                }

                stack.Push((cur, idx + 1));
                int child = kids[idx];
                if (!state.TryGetValue(child, out int s))
                {
                    kept.Add((cur, child));
                    state[child] = 1;
                    stack.Push((child, 0));
                }
                else if (s == 2)
                {
                    kept.Add((cur, child)); // forward/cross edge — safe for layering
                }

                // s == 1: back edge closing a cycle — dropped for layering only.
            }
        }

        // Longest-path relaxation over the DAG in topological order, seeded by hints.
        var layer = new Dictionary<int, int>(keys.Count);
        var indeg = new Dictionary<int, int>(keys.Count);
        var dagOut = new Dictionary<int, List<int>>();
        foreach (int k in keys)
        {
            layer[k] = Math.Max(0, byKey[k].LayerHint);
            indeg[k] = 0;
        }

        foreach ((int from, int to) in kept)
        {
            if (!dagOut.TryGetValue(from, out List<int>? list))
            {
                list = new List<int>();
                dagOut[from] = list;
            }

            list.Add(to);
            indeg[to]++;
        }

        var ready = new SortedSet<int>(keys.Where(k => indeg[k] == 0));
        while (ready.Count > 0)
        {
            int cur = ready.Min;
            ready.Remove(cur);
            if (dagOut.TryGetValue(cur, out List<int>? outs))
            {
                foreach (int to in outs)
                {
                    layer[to] = Math.Max(layer[to], layer[cur] + 1);
                    if (--indeg[to] == 0)
                    {
                        ready.Add(to);
                    }
                }
            }
        }

        return layer;
    }

    /// <summary>Groups keys into compacted layers (empty hint layers removed), naive key order.</summary>
    private static List<List<int>> BuildLayerLists(List<int> keys, Dictionary<int, int> layer)
    {
        List<int> distinct = layer.Values.Distinct().OrderBy(v => v).ToList();
        var remap = new Dictionary<int, int>(distinct.Count);
        for (int i = 0; i < distinct.Count; i++)
        {
            remap[distinct[i]] = i;
        }

        var layers = new List<List<int>>(distinct.Count);
        for (int i = 0; i < distinct.Count; i++)
        {
            layers.Add(new List<int>());
        }

        foreach (int k in keys)
        {
            int compact = remap[layer[k]];
            layer[k] = compact;
            layers[compact].Add(k);
        }

        return layers;
    }

    // ─── Crossing reduction ──────────────────────────────────────────────────

    /// <summary>
    /// Alternating down/up barycenter sweeps: each node is re-sorted by the mean
    /// row index of its neighbours on the already-fixed side (ties break on key;
    /// nodes with no neighbours keep their current row).
    /// </summary>
    private static void OrderWithinLayers(
        List<List<int>> layers, Dictionary<int, int> layer, List<GraphLayoutEdge> edges, int sweeps)
    {
        if (layers.Count < 2 || sweeps <= 0)
        {
            return;
        }

        // Undirected adjacency (ordering cares about connection, not direction).
        var neighbors = new Dictionary<int, List<int>>();
        foreach (GraphLayoutEdge e in edges)
        {
            AddNeighbor(neighbors, e.From, e.To);
            AddNeighbor(neighbors, e.To, e.From);
        }

        var row = new Dictionary<int, int>(); // key → index within its layer
        void RefreshRows(List<int> l)
        {
            for (int i = 0; i < l.Count; i++)
            {
                row[l[i]] = i;
            }
        }

        foreach (List<int> l in layers)
        {
            RefreshRows(l);
        }

        for (int s = 0; s < sweeps; s++)
        {
            bool down = (s % 2) == 0;
            int first = down ? 1 : layers.Count - 2;
            int last = down ? layers.Count : -1;
            int step = down ? 1 : -1;
            for (int i = first; i != last; i += step)
            {
                List<int> current = layers[i];
                List<(int Key, double Bary)> scored = current.Select(k =>
                {
                    double sum = 0;
                    int count = 0;
                    if (neighbors.TryGetValue(k, out List<int>? adj))
                    {
                        foreach (int n in adj)
                        {
                            int nl = layer[n];
                            bool fixedSide = down ? nl < i : nl > i;
                            if (fixedSide)
                            {
                                sum += row[n];
                                count++;
                            }
                        }
                    }

                    return (k, count > 0 ? sum / count : row[k]);
                }).ToList();

                List<int> reordered = scored.OrderBy(t => t.Bary).ThenBy(t => t.Key).Select(t => t.Key).ToList();
                current.Clear();
                current.AddRange(reordered);
                RefreshRows(current);
            }
        }
    }

    private static void AddNeighbor(Dictionary<int, List<int>> map, int a, int b)
    {
        if (!map.TryGetValue(a, out List<int>? list))
        {
            list = new List<int>();
            map[a] = list;
        }

        list.Add(b);
    }

    // ─── Coordinate assignment ───────────────────────────────────────────────

    /// <summary>
    /// X per layer = running max of the previous layer's right edge plus the column
    /// gap; Y within a layer = running cursor (height + row gap), with shorter
    /// layers centred against the tallest one. No two rects can overlap.
    /// </summary>
    private static void AssignCoordinates(
        Dictionary<int, GraphLayoutNode> byKey,
        List<List<int>> layers,
        GraphLayoutEngineOptions options,
        Dictionary<int, GraphNodePos> result)
    {
        double[] heights = layers
            .Select(l => l.Sum(k => byKey[k].Height) + (Math.Max(0, l.Count - 1) * options.RowGap))
            .ToArray();
        double tallest = heights.Length > 0 ? heights.Max() : 0;

        double x = options.OriginX;
        for (int i = 0; i < layers.Count; i++)
        {
            double y = options.OriginY + ((tallest - heights[i]) / 2);
            double maxWidth = 0;
            foreach (int k in layers[i])
            {
                GraphLayoutNode n = byKey[k];
                result[k] = new GraphNodePos(x, y);
                y += n.Height + options.RowGap;
                maxWidth = Math.Max(maxWidth, n.Width);
            }

            x += maxWidth + options.ColumnGap;
        }
    }
}
