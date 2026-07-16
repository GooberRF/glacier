using System.IO;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

public class TableTests
{
    private static string TablePath(string name)
    {
        Assert.NotNull(TestPaths.Tables);
        return Path.Combine(TestPaths.Tables!, name);
    }

    private static bool TablesAvailable => TestPaths.Tables is not null;

    [Fact]
    public void Parser_Splits_Records_And_Handles_Comments()
    {
        const string tbl =
            "// a comment\n" +
            "#Items\n" +
            "$Class Name: \"Alpha\"  // inline comment\n" +
            "$Count: 3\n" +
            "$Flags: (\"a\" \"b\")\n" +
            "\n" +
            "$Class Name: \"Beta\"\n" +
            "$Count: 1\n" +
            "#End\n";

        TblDocument doc = TblParser.Parse(tbl);
        Assert.Equal(2, doc.Records.Count);
        Assert.Equal("Alpha", doc.Records[0].GetString("Class Name"));
        Assert.Equal(3, doc.Records[0].GetInt("Count"));
        Assert.Equal(new[] { "a", "b" }, doc.Records[0].GetList("Flags"));
        Assert.Equal("Items", doc.Records[0].Section);
        Assert.Equal("Beta", doc.Records[1].GetString("Class Name"));
    }

    [Fact]
    public void Value_Helpers_Handle_Xstr_And_Lists()
    {
        Assert.Equal("Remote Charge", TblValue.AsText("XSTR(293, \"Remote Charge\")"));
        Assert.Equal("plain", TblValue.AsText("\"plain\""));
        Assert.Equal(new[] { "6", "9" }, TblValue.ParseList("{ 6 9 }"));
    }

    [Fact]
    public void Items_Catalog_Resolves_Known_Entry()
    {
        if (!TablesAvailable)
        {
            return;
        }

        ItemCatalog cat = ItemCatalog.Load(File.ReadAllBytes(TablePath("items.tbl")));
        Assert.Equal(45, cat.Items.Count);

        ItemDef? remote = cat.Find("Remote Charge");
        Assert.NotNull(remote);
        Assert.Equal("rmt_explosive.V3D", remote!.V3dFilename);
        Assert.Equal("static", remote.V3dType);
        Assert.Equal(1, remote.Count);
        Assert.Equal(20, remote.RespawnTime);
    }

    [Fact]
    public void Clutter_Catalog_Resolves_Known_Entry()
    {
        if (!TablesAvailable)
        {
            return;
        }

        ClutterCatalog cat = ClutterCatalog.Load(File.ReadAllBytes(TablePath("clutter.tbl")));
        Assert.NotEmpty(cat.Clutters);

        ClutterDef? bookcase = cat.Find("officebookcase");
        Assert.NotNull(bookcase);
        Assert.Equal("officebookcase.V3D", bookcase!.V3dFilename);
        Assert.Equal("wood", bookcase.Material);
        Assert.Equal(40, bookcase.Life);
    }

    [Fact]
    public void Clutter_Palette_Tree_Nests_Rfe_Subcategories_From_Real_Data()
    {
        if (!TablesAvailable)
        {
            return;
        }

        ClutterCatalog cat = ClutterCatalog.Load(File.ReadAllBytes(TablePath("clutter.tbl")));

        // The RFE Level1/Level2 tags are surfaced on the def and form its category path.
        ClutterDef bookcase = cat.Find("officebookcase")!;
        Assert.Equal("Furniture", bookcase.RfeLevel1);
        Assert.Null(bookcase.RfeLevel2);
        Assert.Equal(new[] { "Furniture" }, bookcase.CategoryPath);

        Ged.Core.Editing.PaletteCategoryNode root = cat.BuildPaletteTree();
        var topFolders = root.SubCategories.Select(s => s.Name).ToList();

        // Top-level folders are alpha-sorted and include the known Level1 categories.
        Assert.Equal(topFolders.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList(), topFolders);
        Assert.Contains("Furniture", topFolders);
        Assert.Contains("Natural", topFolders);
        Assert.Contains("Computers", topFolders);

        // officebookcase lands in the Furniture folder.
        Ged.Core.Editing.PaletteCategoryNode furniture = root.SubCategories.First(s => s.Name == "Furniture");
        Assert.Contains("officebookcase", furniture.Classes);

        // Case-insensitive merge: the "Misc"/"misc" casings collapse to a single folder.
        Assert.Single(root.SubCategories, s => string.Equals(s.Name, "Misc", System.StringComparison.OrdinalIgnoreCase));

        // Multi-level nesting: Natural ▸ Plants / Rocks / Water (alpha), each holding classes.
        Ged.Core.Editing.PaletteCategoryNode natural = root.SubCategories.First(s => s.Name == "Natural");
        var sub = natural.SubCategories.Select(s => s.Name).ToList();
        Assert.Equal(sub.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList(), sub);
        Assert.Contains("Plants", sub);
        Assert.Contains("Rocks", sub);
        Assert.Contains("Water", sub);
        Assert.NotEmpty(natural.SubCategories.First(s => s.Name == "Plants").Classes);

        // Every distinct clutter class appears somewhere in the tree.
        var inTree = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        Collect(root, inTree);
        foreach (ClutterDef c in cat.Clutters)
        {
            Assert.Contains(c.ClassName, inTree);
        }
    }

    private static void Collect(Ged.Core.Editing.PaletteCategoryNode node, System.Collections.Generic.HashSet<string> into)
    {
        foreach (string c in node.Classes)
        {
            into.Add(c);
        }

        foreach (Ged.Core.Editing.PaletteCategoryNode sub in node.SubCategories)
        {
            Collect(sub, into);
        }
    }

    [Fact]
    public void Entity_Catalog_Resolves_V3d_And_Skins()
    {
        if (!TablesAvailable)
        {
            return;
        }

        EntityCatalog cat = EntityCatalog.Load(File.ReadAllBytes(TablePath("entity.tbl")));
        Assert.NotEmpty(cat.Entities);

        EntityDef? tech = cat.Find("comp_tech");
        Assert.NotNull(tech);
        Assert.Equal("Hendrix.vcm", tech!.V3dFilename);
        Assert.Equal("flesh", tech.Material);
        Assert.Contains("walk", tech.Flags);
        Assert.Equal(new[] { 6f, 9f }, tech.LodDistances);

        // comp_tech declares multiple named skins, each a list of texture files.
        Assert.NotEmpty(tech.Skins);
        EntitySkin? skinB = tech.Skins.FirstOrDefault(s => s.Name == "b");
        Assert.NotNull(skinB);
        Assert.Contains("comp_tech_1a.tga", skinB!.Textures);
    }

    [Fact]
    public void Entity_Palette_Tree_Nests_Rfe_Subcategories_And_Excludes_Ignore()
    {
        if (!TablesAvailable)
        {
            return;
        }

        EntityCatalog cat = EntityCatalog.Load(File.ReadAllBytes(TablePath("entity.tbl")));

        // The RFE Level1 tag is surfaced on the def and forms its (single-level) category path.
        EntityDef tech = cat.Find("comp_tech")!;
        Assert.Equal("Ultor", tech.RfeLevel1);
        Assert.Null(tech.RfeLevel2);
        Assert.Equal(new[] { "Ultor" }, tech.CategoryPath);
        Assert.False(tech.HideFromPalette);

        // Editor-internal entities ($RFE Level1 "Ignore") are hidden from the palette (RED parity).
        EntityDef freelook = cat.Find("Freelook camera")!;
        Assert.Equal("Ignore", freelook.RfeLevel1);
        Assert.True(freelook.HideFromPalette);

        Ged.Core.Editing.PaletteCategoryNode root = cat.BuildPaletteTree();
        var topFolders = root.SubCategories.Select(s => s.Name).ToList();

        // Top-level folders are alpha-sorted and include the known Level1 categories.
        Assert.Equal(topFolders.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList(), topFolders);
        Assert.Contains("Ultor", topFolders);
        Assert.Contains("Robots", topFolders);
        Assert.Contains("Creatures", topFolders);

        // comp_tech lands in the Ultor folder.
        Ged.Core.Editing.PaletteCategoryNode ultor = root.SubCategories.First(s => s.Name == "Ultor");
        Assert.Contains("comp_tech", ultor.Classes);

        // The "Ignore" folder never appears, and the Freelook camera is nowhere in the tree.
        var inTree = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        Collect(root, inTree);
        Assert.DoesNotContain("Freelook camera", inTree);
        Assert.DoesNotContain(root.SubCategories, s => s.Name.Equals("Ignore", System.StringComparison.OrdinalIgnoreCase));

        // Every non-Ignore entity appears somewhere in the tree.
        foreach (EntityDef e in cat.Entities.Where(e => !e.HideFromPalette))
        {
            Assert.Contains(e.Name, inTree);
        }
    }

    [Fact]
    public void Event_Catalog_Builds_Categorized_Tree()
    {
        if (!TablesAvailable)
        {
            return;
        }

        EventCatalog cat = EventCatalog.Load(File.ReadAllBytes(TablePath("events.tbl")));
        Assert.NotEmpty(cat.Events);

        EventDef? teleport = cat.Find("Teleport");
        Assert.NotNull(teleport);

        // Categories come from RFE Level1 / enclosing sections; AI_Actions is a known one.
        Assert.Contains(cat.Categories, c => c.Name.Equals("AI_Actions", StringComparison.OrdinalIgnoreCase));
        EventCategory ai = cat.Categories.First(c => c.Name.Equals("AI_Actions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ai.Events, e => e.Name == "Attack");

        // Every event ends up in exactly one category.
        Assert.Equal(cat.Events.Count, cat.Categories.Sum(c => c.Events.Count));
    }
}
