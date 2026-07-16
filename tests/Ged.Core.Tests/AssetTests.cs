using System.IO;
using System.Text;
using Ged.Core.Assets;
using Ged.Core.IO.Tex;
using Ged.Core.IO.Vpp;
using Xunit;

namespace Ged.Core.Tests;

public class AssetTests : IDisposable
{
    private readonly string _temp;

    public AssetTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_asset_tests_" + Guid.NewGuid().ToString("N"));
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

    // ─── Precedence ─────────────────────────────────────────────────────────

    [Fact]
    public void Loose_Directory_Overrides_Packfile()
    {
        // A VPP with foo.txt = "packed".
        string vppPath = Path.Combine(_temp, "base.vpp");
        new VppBuilder().Add("foo.txt", Encoding.ASCII.GetBytes("packed")).Write(vppPath);

        // A directory with foo.txt = "loose".
        string looseDir = Path.Combine(_temp, "loose");
        Directory.CreateDirectory(looseDir);
        File.WriteAllText(Path.Combine(looseDir, "foo.txt"), "loose");

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(looseDir, extensions: new[] { ".txt" }),
            VppAssetSource.Open(vppPath),
        });

        Assert.Equal("loose", Encoding.ASCII.GetString(vfs.ReadFile("foo.txt")!));

        // Reverse the priority: the packfile now wins.
        using var vfs2 = new AssetVfs(new IAssetSource[]
        {
            VppAssetSource.Open(vppPath),
            new DirectoryAssetSource(looseDir, extensions: new[] { ".txt" }),
        });
        Assert.Equal("packed", Encoding.ASCII.GetString(vfs2.ReadFile("foo.txt")!));
    }

    [Fact]
    public void ResolveTexture_Applies_Supercede_Chain_Across_Mounts()
    {
        string dir = Path.Combine(_temp, "tex");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "wall.tga"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "wall.dds"), new byte[] { 2 });

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(dir, extensions: SupercedeChain.Extensions),
        });

        Assert.Equal("wall.dds", vfs.ResolveTexture("wall.tga"));
        Assert.Equal("wall.dds", vfs.ResolveTexture("wall"));
        Assert.Null(vfs.ResolveTexture("missing"));
    }

    [Fact]
    public void Custom_Subdir_Categories_Are_Enumerated()
    {
        // Simulate user_maps/textures/<sub> layout.
        string install = Path.Combine(_temp, "install");
        string sub = Path.Combine(install, "user_maps", "textures", "mypack");
        Directory.CreateDirectory(sub);
        // Real fixtures so the files have valid texture extensions.
        string? texFixture = TestPaths.FixtureFile("tex", "mtl_bluefiller01.tga");
        if (texFixture is null)
        {
            return; // retail-derived fixture not present
        }

        File.Copy(texFixture, Path.Combine(sub, "custom1.tga"));
        File.WriteAllBytes(Path.Combine(install, "dummy.vpp"),
            new VppBuilder().Add("stock.tga", new byte[] { 0 }).ToArray());

        using AssetVfs vfs = GameMount.Mount(install);
        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();

        AssetCategory? custom = cats.FirstOrDefault(c => c.Name == "Custom - mypack");
        Assert.NotNull(custom);
        Assert.Contains("custom1.tga", custom!.Files);
        Assert.Contains(cats, c => c.Name == "All");
    }

    [Fact]
    public void Rescan_Picks_Up_New_Loose_Files()
    {
        string dir = Path.Combine(_temp, "rescan");
        Directory.CreateDirectory(dir);
        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(dir, extensions: new[] { ".tga" }),
        });

        Assert.False(vfs.Exists("new.tga"));
        File.WriteAllBytes(Path.Combine(dir, "new.tga"), new byte[] { 1 });
        Assert.False(vfs.Exists("new.tga")); // not visible until rescan
        vfs.Rescan();
        Assert.True(vfs.Exists("new.tga"));
    }

    // ─── PNG writer & downscale ─────────────────────────────────────────────

    [Fact]
    public void PngWriter_Produces_Decodable_Png()
    {
        byte[] rgba =
        {
            255, 0, 0, 255,   0, 255, 0, 128,
            0, 0, 255, 255,   255, 255, 255, 0,
        };
        var image = new TextureImage(2, 2, rgba);
        byte[] png = PngWriter.Encode(image);

        Assert.True(StbTextureDecoder.IsPng(png));
        DecodedTexture decoded = StbTextureDecoder.Decode(png);
        Assert.Equal((255, 0, 0, 255), decoded.Primary.GetPixel(0, 0));
        Assert.Equal((0, 255, 0, 128), decoded.Primary.GetPixel(1, 0));
        Assert.Equal((255, 255, 255, 0), decoded.Primary.GetPixel(1, 1));
    }

    [Fact]
    public void BoxDownscale_Averages_Blocks()
    {
        // 2x2 image, one colour per pixel -> downscale to 1x1 = the average.
        byte[] rgba =
        {
            0, 0, 0, 255,     100, 100, 100, 255,
            100, 100, 100, 255, 200, 200, 200, 255,
        };
        TextureImage one = ImageOps.BoxDownscale(new TextureImage(2, 2, rgba), 1, 1);
        Assert.Equal((100, 100, 100, 255), one.GetPixel(0, 0)); // (0+100+100+200)/4 = 100
    }

    [Fact]
    public void DownscaleToFit_Caps_Larger_Side()
    {
        var big = new TextureImage(300, 150, new byte[300 * 150 * 4]);
        TextureImage thumb = ImageOps.DownscaleToFit(big, 128);
        Assert.Equal(128, thumb.Width);
        Assert.Equal(64, thumb.Height);
    }

    // ─── Thumbnail cache ────────────────────────────────────────────────────

    [Fact]
    public void ThumbnailCache_Builds_Caches_And_Reuses()
    {
        string cacheDir = Path.Combine(_temp, "thumbs");
        var cache = new ThumbnailCache(cacheDir, maxSize: 128);

        // Use a real fixture texture as the source file.
        string? texFixture = TestPaths.FixtureFile("tex", "mtl_bluefiller01.tga");
        if (texFixture is null)
        {
            return; // retail-derived fixture not present
        }

        string src = Path.Combine(_temp, "src.tga");
        File.Copy(texFixture, src);

        byte[] png = cache.GetThumbnailForFile(src);
        Assert.True(StbTextureDecoder.IsPng(png));
        DecodedTexture thumb = StbTextureDecoder.Decode(png);
        Assert.True(thumb.Width <= 128 && thumb.Height <= 128);

        // The cache file exists and a second call returns identical bytes (cache hit).
        string cachePath = cache.GetCacheFilePath(
            Path.GetFullPath(src),
            $"{new FileInfo(src).LastWriteTimeUtc.Ticks}-{new FileInfo(src).Length}");
        Assert.True(File.Exists(cachePath));
        Assert.Equal(png, cache.GetThumbnailForFile(src));
    }

    // ─── Real install (read-only, skipped when absent) ──────────────────────

    [Fact]
    public void Mounts_Real_Install_And_Resolves_Assets()
    {
        if (!TestPaths.HasRfInstall)
        {
            return;
        }

        using AssetVfs vfs = GameMount.Mount(TestPaths.RfInstall!);

        // A stock texture packed in maps1.vpp resolves via the supercede chain.
        string? wall = vfs.ResolveTexture("mtl_bluefiller01");
        Assert.NotNull(wall);
        DecodedTexture? tex = vfs.LoadTexture("mtl_bluefiller01");
        Assert.NotNull(tex);
        Assert.True(tex!.Width > 0);

        // A stock mesh loads by bare name (probes .v3m/.v3c).
        var mesh = vfs.LoadMesh("LightOfficeCan01");
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh!.Submeshes);
    }

    [Fact]
    public void Real_Install_Exposes_Custom_Texture_Categories()
    {
        if (!TestPaths.HasRfInstall)
        {
            return;
        }

        string customRoot = Path.Combine(TestPaths.RfInstall!, "user_maps", "textures");
        if (!Directory.Exists(customRoot) || !Directory.EnumerateDirectories(customRoot).Any())
        {
            return;
        }

        using AssetVfs vfs = GameMount.Mount(TestPaths.RfInstall!);
        IReadOnlyList<AssetCategory> cats = vfs.GetTextureCategories();
        Assert.Contains(cats, c => c.Name.StartsWith("Custom - ", StringComparison.Ordinal));
    }
}
