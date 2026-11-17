using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// The viewport draws Alpine-mode lightmaps (ALPINE-COMPAT-SPEC C6): after an Alpine bake the static faces sample
/// their surface charts from display pages appended after the stock pages; with the option off, or after a
/// geometry-only rebuild, they sample the stock lightmaps. Renders dm01 both ways to
/// tests/artifacts/alpine_lightmaps/ (skips without GPU / corpus / RF install).
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class AlpineLightmapRenderTests
{
    private const int Size = 640;
    private readonly ITestOutputHelper _out;

    public AlpineLightmapRenderTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Charted_Faces_Sample_Display_Pages_And_The_Option_Turns_Them_Off()
    {
        string? orig = RenderTestSupport.CorpusFile("dm13.rfl");
        if (orig is null)
        {
            return;
        }

        RflFile rfl = Baked(orig, alpine: true);
        int stockPages = rfl.Sections.Select(s => s.Content).OfType<Ged.Core.IO.Rfl.Sections.LightmapsSection>().Single().Lightmaps.Count;

        RenderScene alpine = SceneBuilder.Build(rfl, Plain(alpineLightmaps: true));
        Assert.True(alpine.Lightmaps.Count > stockPages);
        Assert.Contains(alpine.Batches, b => b.LightmapIndex >= stockPages);
        Assert.All(alpine.Lightmaps.Skip(stockPages), p => Assert.Equal(AlpineChartDisplay.PageEdge, p.Width));

        RenderScene stock = SceneBuilder.Build(rfl, Plain(alpineLightmaps: false));
        Assert.Equal(stockPages, stock.Lightmaps.Count);
        Assert.DoesNotContain(stock.Batches, b => b.LightmapIndex >= stockPages);

        // A display chart's texels average to its stock fragment (the fragment is their box mean): sample the
        // display UV of a face vertex and compare luminance with the stock page at its stock UV.
        AlpineChartDisplay display = AlpineChartDisplay.For(rfl)!;
        Assert.Same(display, AlpineChartDisplay.For(rfl)); // cached per bake
    }

    [Fact]
    public void Renders_Dm01_With_Alpine_Charts_And_Stock_Lightmaps()
    {
        string? orig = RenderTestSupport.CorpusFile("dm01.rfl");
        if (orig is null || RenderTestSupport.RfInstall is null)
        {
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "alpine_lightmaps");
            Directory.CreateDirectory(outDir);
            RflFile rfl = Baked(orig, alpine: true, new TextureTraitsCache(vfs).Get);
            byte[]? first = null;
            foreach ((string tag, bool charts, RenderMode mode) in new[]
            {
                ("alpine_lit", true, RenderMode.TexturesAndLightmaps),
                ("stock_lit", false, RenderMode.TexturesAndLightmaps),
                ("alpine_lightmaps", true, RenderMode.JustLightmaps),
                ("stock_lightmaps", false, RenderMode.JustLightmaps),
            })
            {
                RenderScene scene = SceneBuilder.Build(rfl, Plain(charts));
                // From one light toward its nearest neighbour more than 4 units away: both inside the level.
                List<Light> lights = rfl.Sections.Select(s => s.Content).OfType<Ged.Core.IO.Rfl.Sections.LightsSection>()
                    .First(l => l.Lights.Count > 1).Lights;
                Vec3 from = lights[0].Position;
                Vec3 to = lights.Skip(1).Where(l => Distance(l.Position, from) > 4f).OrderBy(l => Distance(l.Position, from)).First().Position;
                var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
                cam.LookAt(ToVec(from) - new Vector3(0f, 0.5f, 0f), ToVec(to) - new Vector3(0f, 1.5f, 0f));
                byte[] px = OffscreenRenderer.Render(gd, scene, vfs, cam, mode, Size, Size);
                File.WriteAllBytes(Path.Combine(outDir, $"dm01_{tag}.png"), PngWriter.Encode(Size, Size, px));
                double lum = MeanLuminance(px);
                _out.WriteLine($"dm01 {tag}: mean luminance {lum:0.00}");
                Assert.True(lum > 1, $"{tag} renders lit");
                if (tag == "alpine_lightmaps")
                {
                    first = px;
                }
                else if (tag == "stock_lightmaps")
                {
                    // Same overall light (the stock fragments are the charts' box means), finer detail.
                    double a = MeanLuminance(first!), b = lum;
                    Assert.InRange(a / b, 0.85, 1.15);
                    Assert.True(DiffFraction(first!, px) > 0.01, "the charts must change the picture");
                }
            }
        }
        finally
        {
            vfs.Dispose();
        }
    }

    /// <summary>
    /// A level Alpine RED baked to BC7 draws its Alpine lightmaps (ALPINE-COMPAT-SPEC C7): the viewport decodes the BC7
    /// layer, and the picture matches Alpine RED's raw-RGB8 bake of the same level to within BC7's error. Renders both
    /// to tests/artifacts/alpine_lightmaps/dm14_alpinered_{bc7,raw}_lightmaps.png (skips without the references / GPU).
    /// </summary>
    [Fact]
    public void An_Alpine_RED_Bc7_Bake_Draws_Its_Alpine_Lightmaps()
    {
        string? root = RenderTestSupport.ResearchFixtures is { } fx ? Path.Combine(fx, "alpine-ref", "8db7d90b") : null;
        string? bc7 = root is null ? null : Path.Combine(root, "bake", "dm14.rfl");
        string? raw = root is null ? null : Path.Combine(root, "bake-raw", "dm14.rfl");
        if (bc7 is null || raw is null || !File.Exists(bc7) || !File.Exists(raw))
        {
            return;
        }

        RflFile bcLevel = RflFile.Load(bc7);
        bcLevel.ParseAllKnownSections();
        RflFile rawLevel = RflFile.Load(raw);
        rawLevel.ParseAllKnownSections();
        AlpineLightmapAtlas bcAtlas = Assert.IsType<AlpineLightmapAtlas>(AlpineLightmapAtlas.FromLevel(bcLevel));
        AlpineLightmapAtlas rawAtlas = Assert.IsType<AlpineLightmapAtlas>(AlpineLightmapAtlas.FromLevel(rawLevel));
        Assert.Equal(rawAtlas.Pages.Length, bcAtlas.Pages.Length);
        long diff = 0, n = 0;
        for (int p = 0; p < bcAtlas.Pages.Length; p++)
        {
            for (int k = 0; k < bcAtlas.Pages[p].Length; k++)
            {
                diff += Math.Abs(bcAtlas.Pages[p][k] - rawAtlas.Pages[p][k]);
                n++;
            }
        }

        _out.WriteLine($"dm14 Alpine RED BC7 vs raw atlas: mean |d| {diff / (double)n:0.000}");
        Assert.True(diff / (double)n < 0.25);

        RenderScene scene = SceneBuilder.Build(bcLevel, Plain(alpineLightmaps: true));
        int stockPages = bcLevel.Sections.Select(s => s.Content).OfType<Ged.Core.IO.Rfl.Sections.LightmapsSection>().Single().Lightmaps.Count;
        Assert.Contains(scene.Batches, b => b.LightmapIndex >= stockPages);

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null || RenderTestSupport.RfInstall is null)
        {
            return;
        }

        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "alpine_lightmaps");
            Directory.CreateDirectory(outDir);
            List<Light> lights = bcLevel.Sections.Select(s => s.Content).OfType<Ged.Core.IO.Rfl.Sections.LightsSection>()
                .First(l => l.Lights.Count > 1).Lights;
            Vec3 from = lights[0].Position;
            Vec3 to = lights.Skip(1).Where(l => Distance(l.Position, from) > 4f).OrderBy(l => Distance(l.Position, from)).First().Position;
            var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            cam.LookAt(ToVec(from) - new Vector3(0f, 0.5f, 0f), ToVec(to) - new Vector3(0f, 1.5f, 0f));
            byte[] a = OffscreenRenderer.Render(gd, scene, vfs, cam, RenderMode.JustLightmaps, Size, Size);
            byte[] b = OffscreenRenderer.Render(gd, SceneBuilder.Build(rawLevel, Plain(alpineLightmaps: true)), vfs, cam, RenderMode.JustLightmaps, Size, Size);
            File.WriteAllBytes(Path.Combine(outDir, "dm14_alpinered_bc7_lightmaps.png"), PngWriter.Encode(Size, Size, a));
            File.WriteAllBytes(Path.Combine(outDir, "dm14_alpinered_raw_lightmaps.png"), PngWriter.Encode(Size, Size, b));
            double la = MeanLuminance(a), lb = MeanLuminance(b);
            _out.WriteLine($"dm14 Alpine RED bake rendered: BC7 {la:0.00}, raw {lb:0.00}, pixels differing {DiffFraction(a, b):0.0000}");
            Assert.True(la > 1);
            Assert.InRange(la / lb, 0.98, 1.02);
        }
        finally
        {
            vfs.Dispose();
        }
    }

    private static RflFile Baked(string path, bool alpine, Func<string, TextureTraits?>? traits = null)
    {
        RflFile rfl = RflFile.Load(path);
        var opts = new CompileOptions { TextureTraits = traits, BakeLighting = true };
        opts.Lighting.AlpineCharts = alpine ? AlpineLightmapSettings.FromLevel(rfl) : null;
        GeometryBuildService.BuildAndApply(rfl, opts);
        Assert.Equal(alpine, rfl.AlpineLightmaps is not null);
        return rfl;
    }

    private static SceneBuildOptions Plain(bool alpineLightmaps) => new()
    {
        IncludeObjects = false,
        IncludeLinks = false,
        IncludeLightRanges = false,
        IncludeRegionOutlines = false,
        AlpineLightmaps = alpineLightmaps,
    };

    private static double MeanLuminance(byte[] rgba)
    {
        double sum = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            sum += (0.299 * rgba[i]) + (0.587 * rgba[i + 1]) + (0.114 * rgba[i + 2]);
        }

        return sum / (rgba.Length / 4);
    }

    private static double DiffFraction(byte[] a, byte[] b)
    {
        int pixels = Math.Min(a.Length, b.Length) / 4, differing = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (Math.Abs(a[o] - b[o]) > 6 || Math.Abs(a[o + 1] - b[o + 1]) > 6 || Math.Abs(a[o + 2] - b[o + 2]) > 6)
            {
                differing++;
            }
        }

        return pixels == 0 ? 0 : differing / (double)pixels;
    }

    private static Vector3 ToVec(Vec3 v) => new(v.X, v.Y, v.Z);

    private static float Distance(Vec3 a, Vec3 b) => Vector3.Distance(ToVec(a), ToVec(b));
}
