using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Packaging.Graph;

/// <summary>The top-level grouping a dependency file falls under in the graph.</summary>
public enum DependencyCategory
{
    Textures,
    Meshes,
    Sounds,
    Animations,
    AtxChains,
    Other,
}

/// <summary>The role of a dependency-graph node.</summary>
public enum DependencyNodeKind
{
    /// <summary>The root: the level itself.</summary>
    Level,

    /// <summary>A category grouping node (Textures, Meshes, …).</summary>
    Category,

    /// <summary>A single dependency file.</summary>
    File,
}

/// <summary>
/// One node of the dependency graph: the level root, a category, or a file. File
/// nodes carry their <see cref="PackDependency"/> (status, size, referencers);
/// category nodes carry per-status counts of their direct file children.
/// </summary>
public sealed class DependencyGraphNode
{
    public required int Id { get; init; }

    public required DependencyNodeKind NodeKind { get; init; }

    public required string Label { get; init; }

    /// <summary>The category for Category and File nodes (null for the Level root).</summary>
    public DependencyCategory? Category { get; init; }

    /// <summary>The dependency for File nodes (null otherwise).</summary>
    public PackDependency? Dependency { get; init; }

    public DependencyStatus? Status => Dependency?.Status;

    /// <summary>True for a file that hangs off a parent file (a mesh texture / ATX frame).</summary>
    public bool Nested { get; init; }

    // Category counts (populated for Category nodes).
    public int Total { get; set; }

    public int IncludedCount { get; set; }

    public int SkippedCount { get; set; }

    public int MissingCount { get; set; }
}

/// <summary>
/// A directed edge in the dependency graph. Tree edges connect the level → category
/// → file hierarchy; <see cref="Nested"/> edges connect a parent file to an indirect
/// dependency (mesh → material texture, ATX → frame).
/// </summary>
public readonly record struct DependencyGraphEdge(int FromId, int ToId, bool Nested);

/// <summary>The built dependency graph: its nodes and edges.</summary>
public sealed class DependencyGraph
{
    public DependencyGraph(IReadOnlyList<DependencyGraphNode> nodes, IReadOnlyList<DependencyGraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyList<DependencyGraphNode> Nodes { get; }

    public IReadOnlyList<DependencyGraphEdge> Edges { get; }

    public DependencyGraphNode Root => Nodes.First(n => n.NodeKind == DependencyNodeKind.Level);

    public IEnumerable<DependencyGraphNode> Categories => Nodes.Where(n => n.NodeKind == DependencyNodeKind.Category);

    public IEnumerable<DependencyGraphNode> Files => Nodes.Where(n => n.NodeKind == DependencyNodeKind.File);

    /// <summary>The category node a file was placed under (its tree parent), or null if nested.</summary>
    public DependencyGraphNode? CategoryOf(DependencyGraphNode file)
    {
        if (file.NodeKind != DependencyNodeKind.File || file.Nested)
        {
            return null;
        }

        DependencyGraphEdge edge = Edges.FirstOrDefault(e => e.ToId == file.Id && !e.Nested);
        return Nodes.FirstOrDefault(n => n.Id == edge.FromId && n.NodeKind == DependencyNodeKind.Category);
    }

    /// <summary>
    /// The subset of this graph visible when the given categories are collapsed. A
    /// collapsed category keeps its own node (still carrying its counts / badge) but
    /// hides its entire file subtree — its direct files and every file nested beneath
    /// them (mesh material textures, ATX frames). Edges touching a hidden node drop
    /// out too. Passing an empty set returns this graph unchanged. Framework-free so
    /// the panel's collapse behaviour is unit-tested at the model level.
    /// </summary>
    public DependencyGraph Collapse(IReadOnlySet<DependencyCategory> collapsedCategories)
    {
        ArgumentNullException.ThrowIfNull(collapsedCategories);
        if (collapsedCategories.Count == 0)
        {
            return this;
        }

        var collapsedCategoryIds = Nodes
            .Where(n => n.NodeKind == DependencyNodeKind.Category && n.Category is { } c && collapsedCategories.Contains(c))
            .Select(n => n.Id)
            .ToHashSet();
        if (collapsedCategoryIds.Count == 0)
        {
            return this;
        }

        // Outgoing adjacency (category → file, file → nested file) for the subtree walk.
        var outgoing = new Dictionary<int, List<int>>();
        foreach (DependencyGraphEdge e in Edges)
        {
            if (!outgoing.TryGetValue(e.FromId, out List<int>? list))
            {
                outgoing[e.FromId] = list = new List<int>();
            }

            list.Add(e.ToId);
        }

        // BFS from each collapsed category down through its files and their nested
        // descendants. The category node itself is NOT hidden — only what hangs below it.
        var hidden = new HashSet<int>();
        var queue = new Queue<int>(collapsedCategoryIds);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!outgoing.TryGetValue(cur, out List<int>? kids))
            {
                continue;
            }

            foreach (int kid in kids)
            {
                if (hidden.Add(kid))
                {
                    queue.Enqueue(kid);
                }
            }
        }

        var nodes = Nodes.Where(n => !hidden.Contains(n.Id)).ToList();
        var edges = Edges.Where(e => !hidden.Contains(e.FromId) && !hidden.Contains(e.ToId)).ToList();
        return new DependencyGraph(nodes, edges);
    }
}

/// <summary>
/// Builds the dependency graph from a <see cref="DependencyScanResult"/>: a level
/// root, a category node per non-empty category (with Included / BaseGameSkipped /
/// Missing counts of its direct files), and a file node per dependency. Files that
/// were expanded from a parent file (mesh material textures, ATX frames) nest under
/// their parent via a child edge rather than appearing directly under a category.
/// Framework-free so the panel's model is fully unit-tested.
/// </summary>
public static class DependencyGraphModel
{
    /// <summary>The categories in display order.</summary>
    public static readonly DependencyCategory[] Order =
    {
        DependencyCategory.Textures,
        DependencyCategory.Meshes,
        DependencyCategory.Sounds,
        DependencyCategory.Animations,
        DependencyCategory.AtxChains,
        DependencyCategory.Other,
    };

    /// <summary>Assigns a dependency to its display category (ATX by file extension, else by kind).</summary>
    public static DependencyCategory CategoryOf(PackDependency dep)
    {
        ArgumentNullException.ThrowIfNull(dep);
        if (dep.Kind == DependencyKind.AtxFrame ||
            dep.FileName.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
        {
            return DependencyCategory.AtxChains;
        }

        return dep.Kind switch
        {
            DependencyKind.FaceTexture or DependencyKind.LiquidTexture or DependencyKind.DecalTexture
                or DependencyKind.ParticleBitmap or DependencyKind.BoltBitmap or DependencyKind.CoronaBitmap
                or DependencyKind.EventBitmap or DependencyKind.MeshObjectTexture or DependencyKind.GeomodTexture
                or DependencyKind.AtxDescriptor or DependencyKind.ClutterSkin or DependencyKind.EntitySkin
                => DependencyCategory.Textures,

            DependencyKind.MeshObject or DependencyKind.EventMesh or DependencyKind.ClutterMesh
                or DependencyKind.EntityMesh or DependencyKind.ItemMesh
                => DependencyCategory.Meshes,

            DependencyKind.EventSound or DependencyKind.AmbientSound or DependencyKind.MoverSound
                => DependencyCategory.Sounds,

            DependencyKind.MeshAnimation or DependencyKind.EventAnimation
                => DependencyCategory.Animations,

            _ => DependencyCategory.Other,
        };
    }

    /// <summary>Builds the graph for a scan result under a level label (usually the .rfl file name).</summary>
    public static DependencyGraph Build(DependencyScanResult scan, string levelLabel)
    {
        ArgumentNullException.ThrowIfNull(scan);
        levelLabel ??= "level";

        var nodes = new List<DependencyGraphNode>();
        var edges = new List<DependencyGraphEdge>();
        int nextId = 0;

        var root = new DependencyGraphNode { Id = nextId++, NodeKind = DependencyNodeKind.Level, Label = levelLabel };
        nodes.Add(root);

        // A file is "nested" when it was expanded from a parent file that is itself
        // in this scan (mesh material texture / ATX frame) — it hangs off the parent
        // rather than being a direct child of a category.
        var byName = scan.All.ToDictionary(d => d.FileName, d => d, StringComparer.OrdinalIgnoreCase);
        bool IsNested(PackDependency d) => d.Parents.Any(p => byName.ContainsKey(p));

        // File nodes first (so parent-file lookups can find their ids), in scan order.
        var fileNode = new Dictionary<PackDependency, DependencyGraphNode>();
        foreach (PackDependency dep in scan.All)
        {
            var node = new DependencyGraphNode
            {
                Id = nextId++,
                NodeKind = DependencyNodeKind.File,
                Label = dep.FileName,
                Category = CategoryOf(dep),
                Dependency = dep,
                Nested = IsNested(dep),
            };
            nodes.Add(node);
            fileNode[dep] = node;
        }

        // Category nodes for every category with at least one direct (non-nested) file.
        var categoryNode = new Dictionary<DependencyCategory, DependencyGraphNode>();
        foreach (DependencyCategory cat in Order)
        {
            var direct = scan.All.Where(d => CategoryOf(d) == cat && !IsNested(d)).ToList();
            if (direct.Count == 0)
            {
                continue;
            }

            var cnode = new DependencyGraphNode
            {
                Id = nextId++,
                NodeKind = DependencyNodeKind.Category,
                Label = cat.ToString(),
                Category = cat,
                Total = direct.Count,
                IncludedCount = direct.Count(d => d.Status == DependencyStatus.Included),
                SkippedCount = direct.Count(d => d.Status == DependencyStatus.BaseGameSkipped),
                MissingCount = direct.Count(d => d.Status == DependencyStatus.Missing),
            };
            nodes.Add(cnode);
            categoryNode[cat] = cnode;
            edges.Add(new DependencyGraphEdge(root.Id, cnode.Id, Nested: false));
        }

        // Tree edges: category → direct file; nested edges: parent file → child file.
        foreach (PackDependency dep in scan.All)
        {
            DependencyGraphNode node = fileNode[dep];
            if (node.Nested)
            {
                foreach (string parent in dep.Parents)
                {
                    if (byName.TryGetValue(parent, out PackDependency? pdep) && fileNode.TryGetValue(pdep, out DependencyGraphNode? pnode))
                    {
                        edges.Add(new DependencyGraphEdge(pnode.Id, node.Id, Nested: true));
                    }
                }
            }
            else if (node.Category is { } cat && categoryNode.TryGetValue(cat, out DependencyGraphNode? cnode))
            {
                edges.Add(new DependencyGraphEdge(cnode.Id, node.Id, Nested: false));
            }
        }

        return new DependencyGraph(nodes, edges);
    }
}
