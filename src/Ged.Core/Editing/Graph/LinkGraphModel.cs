using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;

namespace Ged.Core.Editing.Graph;

/// <summary>The kind-filter bucket a link-graph node falls into (the panel's checkbox row).</summary>
public enum GraphNodeCategory
{
    Trigger,
    Event,
    Mover,
    Target,
    Other,
}

/// <summary>
/// One node of the interactive link graph: a level object (or a dangling target
/// UID with no object). Carries the display fields the canvas draws (kind, uid,
/// script, class) plus the underlying <see cref="LevelObject"/> for selection sync
/// and camera jumps.
/// </summary>
public sealed class LinkGraphNode
{
    public required int Uid { get; init; }

    /// <summary>The object kind, or null when the link points at a UID that no longer exists.</summary>
    public LevelObjectKind? Kind { get; init; }

    public string Script { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;

    /// <summary>The resolved level object, or null for a dangling (missing) link target.</summary>
    public LevelObject? Object { get; init; }

    /// <summary>True when this node is a link target whose UID has no object (a broken link).</summary>
    public bool Missing => Object is null;

    /// <summary>True when this node can originate links (Trigger / Event / Clutter / Nav Point).</summary>
    public bool CanOriginate { get; init; }

    public GraphNodeCategory Category => LinkGraphModel.CategoryOf(Kind);

    /// <summary>A short label: script name, else class name, else "kind uid".</summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(Script) ? Script
        : !string.IsNullOrEmpty(ClassName) ? ClassName
        : Missing ? $"missing {Uid}" : $"{Kind} {Uid}";
}

/// <summary>A directed link edge from one node's UID to another (origin → target).</summary>
public readonly record struct LinkGraphEdge(int From, int To);

/// <summary>The built graph: the filtered node and edge lists.</summary>
public sealed class LinkGraph
{
    public LinkGraph(IReadOnlyList<LinkGraphNode> nodes, IReadOnlyList<LinkGraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyList<LinkGraphNode> Nodes { get; }

    public IReadOnlyList<LinkGraphEdge> Edges { get; }

    public LinkGraphNode? Node(int uid) => Nodes.FirstOrDefault(n => n.Uid == uid);
}

/// <summary>The filter applied when building the graph (the panel's toolbar state).</summary>
public sealed class LinkGraphFilter
{
    /// <summary>Show the whole graph rather than the selection's connected component.</summary>
    public bool ShowAll { get; set; }

    /// <summary>Enabled kind buckets; empty means all kinds are shown.</summary>
    public HashSet<GraphNodeCategory> Categories { get; } = new();

    /// <summary>Case-insensitive substring over UID / script / class; null or empty matches all.</summary>
    public string? Search { get; set; }

    /// <summary>UIDs seeding the connected-component filter (the current editor selection).</summary>
    public IReadOnlyCollection<int> SelectionUids { get; set; } = Array.Empty<int>();

    public bool CategoryEnabled(GraphNodeCategory c) => Categories.Count == 0 || Categories.Contains(c);
}

/// <summary>
/// Builds the interactive link graph from a document's originator links, honouring
/// the connected-component-of-selection default (or Show All), the kind-filter
/// buckets, and the UID/script/class search box. Framework-free so the panel view
/// logic is fully unit-tested.
/// </summary>
public static class LinkGraphModel
{
    /// <summary>Maps an object kind onto the kind-filter bucket the panel groups by.</summary>
    public static GraphNodeCategory CategoryOf(LevelObjectKind? kind) => kind switch
    {
        LevelObjectKind.Trigger => GraphNodeCategory.Trigger,
        LevelObjectKind.Event => GraphNodeCategory.Event,
        LevelObjectKind.Mover => GraphNodeCategory.Mover,
        LevelObjectKind.Target => GraphNodeCategory.Target,
        _ => GraphNodeCategory.Other,
    };

    /// <summary>Builds the filtered graph for the given document and filter.</summary>
    public static LinkGraph Build(EditorDocument doc, LinkGraphFilter filter)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(filter);

        // All directed edges: originator object links plus the moving-group structural edges
        // (member mover → start keyframe, keyframe sequence). Enumerated through DocumentLinks
        // so the panel shows exactly the same links as the viewport overlay. Keyframes are
        // level objects (resolvable by UID) so they and their movers become graph nodes just
        // like event/trigger link targets.
        var allEdges = DocumentLinks.AllEdges(doc).Select(e => new LinkGraphEdge(e.From, e.To)).ToList();

        if (allEdges.Count == 0)
        {
            return new LinkGraph(Array.Empty<LinkGraphNode>(), Array.Empty<LinkGraphEdge>());
        }

        var nodeUids = allEdges.SelectMany(e => new[] { e.From, e.To }).Distinct().ToHashSet();

        // 2. Default view = the connected component(s) of the current selection.
        if (!filter.ShowAll)
        {
            var seeds = filter.SelectionUids.Where(nodeUids.Contains).ToHashSet();
            if (seeds.Count > 0)
            {
                nodeUids = ConnectedComponent(seeds, allEdges);
            }
        }

        // 3. Kind + search filters.
        var visible = new HashSet<int>();
        foreach (int uid in nodeUids)
        {
            LinkGraphNode node = MakeNode(doc, uid);
            if (filter.CategoryEnabled(node.Category) && MatchesSearch(node, filter.Search))
            {
                visible.Add(uid);
            }
        }

        var nodes = visible.Select(uid => MakeNode(doc, uid)).OrderBy(n => n.Uid).ToList();
        var edges = allEdges
            .Where(e => visible.Contains(e.From) && visible.Contains(e.To))
            .Distinct()
            .ToList();
        return new LinkGraph(nodes, edges);
    }

    private static LinkGraphNode MakeNode(EditorDocument doc, int uid)
    {
        LevelObject? o = doc.FindByUid(uid);
        return new LinkGraphNode
        {
            Uid = uid,
            Kind = o?.Kind,
            Script = o?.ScriptName ?? string.Empty,
            ClassName = o?.ClassName ?? string.Empty,
            Object = o,
            CanOriginate = o is not null && LinkModel.CanOriginate(o),
        };
    }

    private static bool MatchesSearch(LinkGraphNode node, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string s = search.Trim();
        return node.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(s, StringComparison.OrdinalIgnoreCase)
            || node.Script.Contains(s, StringComparison.OrdinalIgnoreCase)
            || node.ClassName.Contains(s, StringComparison.OrdinalIgnoreCase)
            || (node.Kind?.ToString().Contains(s, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static HashSet<int> ConnectedComponent(HashSet<int> seeds, List<LinkGraphEdge> edges)
    {
        var adj = new Dictionary<int, List<int>>();

        void Add(int a, int b)
        {
            if (!adj.TryGetValue(a, out List<int>? l))
            {
                l = new List<int>();
                adj[a] = l;
            }

            l.Add(b);
        }

        foreach (LinkGraphEdge e in edges)
        {
            Add(e.From, e.To);
            Add(e.To, e.From);
        }

        var visited = new HashSet<int>(seeds);
        var queue = new Queue<int>(seeds);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (adj.TryGetValue(cur, out List<int>? neigh))
            {
                foreach (int n in neigh)
                {
                    if (visited.Add(n))
                    {
                        queue.Enqueue(n);
                    }
                }
            }
        }

        return visited;
    }
}
