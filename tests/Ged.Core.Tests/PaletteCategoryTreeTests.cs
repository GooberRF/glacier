using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The pure palette category-tree builder (item 1): folders merge case-insensitively with a
/// deterministic display casing, every level is alphabetically sorted, multi-level nesting is
/// preserved, and classes with no category path sit at the root.
/// </summary>
public sealed class PaletteCategoryTreeTests
{
    private static (string, IReadOnlyList<string>) E(string cls, params string[] path) => (cls, path);

    [Fact]
    public void Sorts_Folders_And_Classes_Alphabetically_At_Every_Level()
    {
        PaletteCategoryNode root = PaletteCategoryTree.Build(new[]
        {
            E("zeta", "Storage"),
            E("alpha", "Storage"),
            E("fern", "Natural", "Plants"),
            E("cactus", "Natural", "Plants"),
            E("boulder", "Natural", "Rocks"),
            E("rootB"),
            E("rootA"),
        });

        // Folders alpha at the root.
        Assert.Equal(new[] { "Natural", "Storage" }, root.SubCategories.Select(s => s.Name));
        // Root-level (uncategorized) classes alpha.
        Assert.Equal(new[] { "rootA", "rootB" }, root.Classes);

        PaletteCategoryNode storage = root.SubCategories.First(s => s.Name == "Storage");
        Assert.Equal(new[] { "alpha", "zeta" }, storage.Classes);

        // Second-level folders alpha, and their classes alpha.
        PaletteCategoryNode natural = root.SubCategories.First(s => s.Name == "Natural");
        Assert.Equal(new[] { "Plants", "Rocks" }, natural.SubCategories.Select(s => s.Name));
        Assert.Equal(new[] { "cactus", "fern" }, natural.SubCategories.First(s => s.Name == "Plants").Classes);
    }

    [Fact]
    public void Merges_Folder_Names_Case_Insensitively_With_The_Most_Common_Casing()
    {
        PaletteCategoryNode root = PaletteCategoryTree.Build(new[]
        {
            E("a", "Misc"),
            E("b", "Misc"),
            E("c", "misc"), // fewer votes → the "Misc" casing wins the display name
        });

        Assert.Single(root.SubCategories);
        PaletteCategoryNode misc = root.SubCategories[0];
        Assert.Equal("Misc", misc.Name);
        Assert.Equal(new[] { "a", "b", "c" }, misc.Classes);
    }

    [Fact]
    public void Blank_Segments_And_Empty_Classes_Are_Ignored()
    {
        PaletteCategoryNode root = PaletteCategoryTree.Build(new[]
        {
            E("keep", "  ", "Real"), // blank first segment collapses; class nests under Real at root
            E(" ", "Ghost"),         // blank class ignored entirely
            E("bare"),
        });

        Assert.Equal(new[] { "Real" }, root.SubCategories.Select(s => s.Name));
        Assert.Equal(new[] { "keep" }, root.SubCategories[0].Classes);
        Assert.Equal(new[] { "bare" }, root.Classes);
    }

    [Fact]
    public void Empty_Input_Yields_An_Empty_Root()
    {
        PaletteCategoryNode root = PaletteCategoryTree.Build(Array.Empty<(string, IReadOnlyList<string>)>());
        Assert.True(root.IsRoot);
        Assert.True(root.IsEmpty);
    }
}
