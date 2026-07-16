using System.IO;
using System.Text;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;
using Ged.Core.IO.Vpp;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Tests for <see cref="TextureCategoryCatalog"/>: RED-style stock categories
/// built from maps*.txt texture lists (see docs/research/red-texture-categories.md),
/// the maps_af.txt merge, the supercede chain, and the fallback buckets.
/// </summary>
public class TextureCategoryTests : IDisposable
{
    private readonly string _temp;

    public TextureCategoryTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_texcat_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static byte[] Text(params string[] lines) =>
        Encoding.ASCII.GetBytes(string.Join("\r\n", lines));

    private string WriteVpp(string name, params (string Name, byte[] Data)[] files)
    {
        string path = Path.Combine(_temp, name);
        var builder = new VppBuilder();
        foreach ((string n, byte[] d) in files)
        {
            builder.Add(n, d);
        }

        builder.Write(path);
        return path;
    }

    // ─── Stock categories from list files ───────────────────────────────────

    [Fact]
    public void Stock_Categories_Built_From_List_Files_In_Red_Order()
    {
        // Two list files; entries exercise comments, blank lines, whitespace,
        // mixed case, and forward slashes.
        string tables = WriteVpp("tables.vpp",
            ("maps.txt", Text(
                "// comment line",
                "# another comment",
                string.Empty,
                @"  data\maps\textures\crates\crate01.tga  ",
                @"DATA\MAPS\TEXTURES\CRATES\CRATE02.TGA")),
            ("maps1.txt", Text(
                "data/maps/textures/pipes/pipe01.tga",
                @"data\maps\textures\crates\crate01.tga"))); // duplicate entry is deduplicated

        string maps = WriteVpp("maps.vpp",
            ("crate01.tga", new byte[] { 1 }),
            ("crate02.tga", new byte[] { 2 }),
            ("pipe01.tga", new byte[] { 3 }));

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            VppAssetSource.Open(tables),
            VppAssetSource.Open(maps),
        });

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();

        // Every texture is listed, so there is no "Uncategorized" bucket and the
        // order is exactly: stock categories in RED's order, then "All".
        Assert.Equal(new[] { "Crates", "Pipes", "All" }, cats.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "crate01.tga", "crate02.tga" }, cats[0].Files);
        Assert.Equal(new[] { "pipe01.tga" }, cats[1].Files);
    }

    [Fact]
    public void List_Entries_In_Unmapped_Directories_Are_Ignored()
    {
        // data\maps\skins is not a stock browser folder in RED; its entries are
        // reachable only via "All" (GED additionally surfaces them in Uncategorized).
        string tables = WriteVpp("tables.vpp",
            ("maps.txt", Text(@"data\maps\skins\skin01.tga")));
        string maps = WriteVpp("maps.vpp", ("skin01.tga", new byte[] { 1 }));

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            VppAssetSource.Open(tables),
            VppAssetSource.Open(maps),
        });

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        Assert.Equal(
            new[] { TextureCategoryCatalog.UncategorizedName, TextureCategoryCatalog.AllName },
            cats.Select(c => c.Name).ToArray());
        Assert.Contains("skin01.tga", cats[0].Files);
    }

    // ─── maps_af.txt merge ───────────────────────────────────────────────────

    [Fact]
    public void MapsAf_Entries_Join_The_Stock_Categories()
    {
        string tables = WriteVpp("tables.vpp",
            ("maps.txt", Text(@"data\maps\textures\crates\crate01.tga")));
        string alpine = WriteVpp("alpinefaction.vpp",
            ("maps_af.txt", Text(
                @"data\maps\textures\crates\crate02.tga",
                @"data\maps\textures\doors\door01.tga")));
        string maps = WriteVpp("maps.vpp",
            ("crate01.tga", new byte[] { 1 }),
            ("crate02.tga", new byte[] { 2 }),
            ("door01.tga", new byte[] { 3 }));

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            VppAssetSource.Open(alpine),
            VppAssetSource.Open(tables),
            VppAssetSource.Open(maps),
        });

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        AssetCategory crates = Assert.Single(cats, c => c.Name == "Crates");
        AssetCategory doors = Assert.Single(cats, c => c.Name == "Doors");
        Assert.Equal(new[] { "crate01.tga", "crate02.tga" }, crates.Files);
        Assert.Equal(new[] { "door01.tga" }, doors.Files);

        // Without maps_af.txt the extra textures are not part of the stock set.
        using var vfsBase = new AssetVfs(new IAssetSource[]
        {
            VppAssetSource.Open(tables),
            VppAssetSource.Open(maps),
        });
        IReadOnlyList<AssetCategory> baseCats = vfsBase.GetTextureCategories();
        Assert.Equal(new[] { "crate01.tga" }, Assert.Single(baseCats, c => c.Name == "Crates").Files);
        Assert.DoesNotContain(baseCats, c => c.Name == "Doors");
    }

    // ─── Uncategorized fallback ──────────────────────────────────────────────

    [Fact]
    public void Unlisted_Texture_Falls_Into_Uncategorized()
    {
        string tables = WriteVpp("tables.vpp",
            ("maps.txt", Text(@"data\maps\textures\crates\crate01.tga")));
        string maps = WriteVpp("maps.vpp",
            ("crate01.tga", new byte[] { 1 }),
            ("rogue.tga", new byte[] { 2 }));

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            VppAssetSource.Open(tables),
            VppAssetSource.Open(maps),
        });

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        AssetCategory other = Assert.Single(cats, c => c.Name == TextureCategoryCatalog.UncategorizedName);
        Assert.Equal(new[] { "rogue.tga" }, other.Files);
        Assert.Equal(new[] { "crate01.tga" }, Assert.Single(cats, c => c.Name == "Crates").Files);
    }

    // ─── Supercede chain ─────────────────────────────────────────────────────

    [Fact]
    public void List_Entry_Resolves_Through_The_Supercede_Chain()
    {
        // maps.txt names the .tga; a higher-priority mount supplies a superseding
        // .dds. The category lists the winning file and the shadowed sibling does
        // not leak into Uncategorized.
        string tables = WriteVpp("tables.vpp",
            ("maps.txt", Text(@"data\maps\textures\crates\crate01.tga")));
        string maps = WriteVpp("maps.vpp", ("crate01.tga", new byte[] { 1 }));

        string overrideDir = Path.Combine(_temp, "override");
        Directory.CreateDirectory(overrideDir);
        File.WriteAllBytes(Path.Combine(overrideDir, "crate01.dds"), new byte[] { 2 });

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(overrideDir, extensions: SupercedeChain.Extensions),
            VppAssetSource.Open(tables),
            VppAssetSource.Open(maps),
        });

        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        Assert.Equal(new[] { "crate01.dds" }, Assert.Single(cats, c => c.Name == "Crates").Files);
        Assert.DoesNotContain(cats, c => c.Name == TextureCategoryCatalog.UncategorizedName);
    }

    // ─── Custom mount categories are preserved ──────────────────────────────

    [Fact]
    public void Custom_Subdir_Categories_Follow_The_Stock_Categories()
    {
        string install = Path.Combine(_temp, "install");
        string sub = Path.Combine(install, "user_maps", "textures", "mypack");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "custom1.tga"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(install, "tables.vpp"),
            new VppBuilder()
                .Add("maps.txt", Text(@"data\maps\textures\crates\crate01.tga"))
                .Add("crate01.tga", new byte[] { 2 })
                .ToArray());

        using AssetVfs vfs = GameMount.Mount(install);
        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();

        int crates = cats.ToList().FindIndex(c => c.Name == "Crates");
        int custom = cats.ToList().FindIndex(c => c.Name == "Custom - mypack");
        int all = cats.ToList().FindIndex(c => c.Name == TextureCategoryCatalog.AllName);
        Assert.True(crates >= 0 && custom > crates && all > custom,
            $"expected Crates < Custom - mypack < All, got: {string.Join(", ", cats.Select(c => c.Name))}");

        Assert.Contains("custom1.tga", cats[custom].Files);

        // The custom texture is claimed by its mount category, not Uncategorized.
        AssetCategory? other = cats.FirstOrDefault(c => c.Name == TextureCategoryCatalog.UncategorizedName);
        if (other is not null)
        {
            Assert.DoesNotContain("custom1.tga", other.Files);
        }
    }

    // ─── Cache & rescan ──────────────────────────────────────────────────────

    [Fact]
    public void Categories_Refresh_After_Rescan()
    {
        string tables = WriteVpp("tables.vpp",
            ("maps.txt", Text(@"data\maps\textures\crates\crate01.tga")));

        string looseDir = Path.Combine(_temp, "loose");
        Directory.CreateDirectory(looseDir);

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(looseDir, extensions: SupercedeChain.Extensions),
            VppAssetSource.Open(tables),
        });

        Assert.DoesNotContain(vfs.GetTextureCategories(), c => c.Name == "Crates");

        // The listed texture appears on disk; a rescan rebuilds the categories.
        File.WriteAllBytes(Path.Combine(looseDir, "crate01.tga"), new byte[] { 1 });
        vfs.Rescan();
        Assert.Contains(vfs.GetTextureCategories(), c => c.Name == "Crates");
    }

    // ─── List-file parsing ───────────────────────────────────────────────────

    [Fact]
    public void ParseListEntries_Is_Tolerant_And_Skips_Mip_Variants()
    {
        byte[] data = Text(
            "// comment",
            "; also a comment",
            "# and this",
            string.Empty,
            "   ",
            @"data\maps\textures\crates\crate01.tga",
            @"data\maps\skins\enviro_guard_face_h-mip1.tga", // "-mip" LOD variant: skipped
            @"data\maps\skins\enviro_sci_chest_b_mip1.tga",  // "_mip" is NOT skipped (matches RED)
            "data/maps/textures/pipes/pipe01.tga",
            "barename.tga");

        var entries = TextureCategoryCatalog.ParseListEntries(data).ToList();

        Assert.Equal(4, entries.Count);
        Assert.Equal((@"data\maps\textures\crates", "crate01.tga"), entries[0]);
        Assert.Equal((@"data\maps\skins", "enviro_sci_chest_b_mip1.tga"), entries[1]);
        Assert.Equal((@"data\maps\textures\pipes", "pipe01.tga"), entries[2]);
        Assert.Equal((string.Empty, "barename.tga"), entries[3]);
    }

    // ─── Real install (read-only, skipped when absent) ──────────────────────

    [Fact]
    public void Real_Install_Exposes_Stock_Categories_From_Tables_Vpp()
    {
        if (!TestPaths.HasRfInstall || TestPaths.RfVpp("tables.vpp") is null)
        {
            return;
        }

        using AssetVfs vfs = GameMount.Mount(TestPaths.RfInstall!);
        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();

        // tables.vpp ships maps.txt..maps4.txt; the classic stock folders exist.
        AssetCategory crates = Assert.Single(cats, c => c.Name == "Crates");
        Assert.NotEmpty(crates.Files);
        Assert.Contains(cats, c => c.Name == "Wall - Rock");
        Assert.Contains(cats, c => c.Name == "Effects");

        // Order: stock first, "All" last.
        Assert.Equal(TextureCategoryCatalog.AllName, cats[^1].Name);
    }
}
