using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Editing;

/// <summary>
/// One node in the object palette's category hierarchy: named subcategory folders and the
/// leaf class names that sit directly under this node. Both lists are alphabetically sorted
/// (item 1 — "alphabetical at every level"). The root node (<see cref="IsRoot"/>) has an
/// empty name and holds the top-level folders plus any classes that carry no category tag.
/// </summary>
public sealed class PaletteCategoryNode
{
    public PaletteCategoryNode(string name, IReadOnlyList<PaletteCategoryNode> subCategories, IReadOnlyList<string> classes)
    {
        Name = name;
        SubCategories = subCategories;
        Classes = classes;
    }

    /// <summary>The folder's display name; empty for the synthetic root.</summary>
    public string Name { get; }

    /// <summary>Child subcategory folders, sorted alphabetically (case-insensitive).</summary>
    public IReadOnlyList<PaletteCategoryNode> SubCategories { get; }

    /// <summary>Leaf class names directly under this node, sorted alphabetically (case-insensitive).</summary>
    public IReadOnlyList<string> Classes { get; }

    public bool IsRoot => Name.Length == 0;

    /// <summary>True when the node has neither subcategories nor classes.</summary>
    public bool IsEmpty => SubCategories.Count == 0 && Classes.Count == 0;

    /// <summary>An empty root (no categories, no classes) — the "no catalog mounted" state.</summary>
    public static PaletteCategoryNode Empty { get; } =
        new(string.Empty, Array.Empty<PaletteCategoryNode>(), Array.Empty<string>());
}

/// <summary>
/// Builds a <see cref="PaletteCategoryNode"/> hierarchy from flat <c>(class, category-path)</c>
/// entries. Category folder names are merged case-insensitively (so <c>"Misc"</c> and
/// <c>"misc"</c> are one folder), with the display casing chosen deterministically (the most
/// common casing, ties broken ordinally). Every level — folders and leaf classes alike — is
/// sorted alphabetically. Pure and UI-free, so the clutter subcategory nesting is unit-testable
/// against the real <c>clutter.tbl</c>.
/// </summary>
public static class PaletteCategoryTree
{
    /// <summary>Builds the tree; empty/whitespace class names and blank path segments are ignored.</summary>
    public static PaletteCategoryNode Build(IEnumerable<(string ClassName, IReadOnlyList<string> Path)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var root = new Mutable();
        foreach ((string className, IReadOnlyList<string> path) in entries)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            Mutable node = root;
            if (path is not null)
            {
                foreach (string segment in path)
                {
                    string seg = segment?.Trim() ?? string.Empty;
                    if (seg.Length == 0)
                    {
                        continue;
                    }

                    node = node.Child(seg);
                }
            }

            node.Classes.Add(className.Trim());
        }

        return root.Freeze(string.Empty);
    }

    /// <summary>Mutable builder node: children keyed case-insensitively, casing votes tallied.</summary>
    private sealed class Mutable
    {
        private readonly Dictionary<string, Mutable> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _casingVotes = new(StringComparer.Ordinal);

        public HashSet<string> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Mutable Child(string name)
        {
            _casingVotes[name] = _casingVotes.TryGetValue(name, out int c) ? c + 1 : 1;
            if (!_children.TryGetValue(name, out Mutable? child))
            {
                child = new Mutable();
                _children[name] = child;
            }

            return child;
        }

        public PaletteCategoryNode Freeze(string displayName)
        {
            List<PaletteCategoryNode> subs = _children
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Value.Freeze(Represent(kv.Key)))
                .ToList();

            List<string> classes = Classes
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PaletteCategoryNode(displayName, subs, classes);
        }

        /// <summary>The winning display casing for a case-folded folder key: most votes, then ordinal.</summary>
        private string Represent(string key) =>
            _casingVotes
                .Where(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
    }
}
