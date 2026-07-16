using System.Diagnostics;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Core.Lighting;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Feature 1: renders dm01's interior camera baked with each lightmap method (RED Classic,
/// +AO, Bounced) to tests/artifacts/lighting_methods/ and reports the bake timings. Skips
/// without GPU / corpus / RF install.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class LightingMethodArtifactTests
{
    private const int Size = 640;
    private readonly ITestOutputHelper _out;

    public LightingMethodArtifactTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Renders_Dm01_With_Each_Method_And_Reports_Timings()
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
            var traits = new TextureTraitsCache(vfs);
            string outDir = System.IO.Path.Combine(RenderTestSupport.ArtifactsDir, "lighting_methods");
            System.IO.Directory.CreateDirectory(outDir);

            var methods = new (string Tag, LightingOptions Make)[]
            {
                ("red_classic", new LightingOptions()),
                ("ao", new LightingOptions { AmbientOcclusion = true }),
                ("bounced", new LightingOptions { LightBounces = 1 }),
            };

            byte[]? redClassicPx = null;
            long redClassicAtlas = 0;
            foreach ((string tag, LightingOptions method) in methods)
            {
                RflFile recompiled = RflFile.Load(orig);
                var opts = new CompileOptions { TextureTraits = traits.Get, BakeLighting = true };
                opts.Lighting.AmbientOcclusion = method.AmbientOcclusion;
                opts.Lighting.LightBounces = method.LightBounces;
                opts.Lighting.SoftShadows = method.SoftShadows;

                var sw = Stopwatch.StartNew();
                GeometryBuildService.BuildAndApply(recompiled, opts);
                sw.Stop();

                long atlas = AtlasSum(recompiled);

                RenderScene scene = SceneBuilder.Build(recompiled, new SceneBuildOptions
                {
                    IncludeObjects = false,
                    IncludeLinks = false,
                    IncludeLightRanges = false,
                    IncludeRegionOutlines = false,
                });

                Vector3 c = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
                var interior = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
                interior.LookAt(c + new Vector3(3f, 1.5f, 0f), c + new Vector3(0f, 1f, 4f));

                byte[] px = OffscreenRenderer.Render(gd, scene, vfs, interior, RenderMode.TexturesAndLightmaps, Size, Size);
                System.IO.File.WriteAllBytes(
                    System.IO.Path.Combine(outDir, $"dm01_{tag}_interior.png"), PngWriter.Encode(Size, Size, px));

                long lum = TotalLuminance(px);
                double diff = redClassicPx is null ? 0 : DiffFraction(redClassicPx, px);
                _out.WriteLine($"dm01 {tag}: bake {sw.Elapsed.TotalMilliseconds:F0} ms, atlas-sum {atlas}, mean-luminance {lum / (double)(Size * Size):F2}, diff-vs-red-classic {diff * 100:F2}%");
                if (tag == "red_classic")
                {
                    redClassicPx = px;
                    redClassicAtlas = atlas;
                }
                else
                {
                    // The modifier must actually change the baked atlas vs stock RED Classic.
                    Assert.True(atlas != redClassicAtlas, $"{tag} must change the baked lightmap atlas");
                }

                Assert.True(lum > 0, $"{tag} render must be non-empty");
            }

            Assert.NotNull(redClassicPx);
        }
        finally
        {
            vfs.Dispose();
        }
    }

    private static long AtlasSum(RflFile rfl)
    {
        long sum = 0;
        foreach (Ged.Core.IO.Rfl.RflSection s in rfl.Sections)
        {
            if (s.Content is Ged.Core.IO.Rfl.Sections.LightmapsSection lm)
            {
                foreach (Ged.Core.Model.Lightmap page in lm.Lightmaps)
                {
                    foreach (byte px in page.Pixels)
                    {
                        sum += px;
                    }
                }
            }
        }

        return sum;
    }

    private static double DiffFraction(byte[] a, byte[] b)
    {
        int pixels = System.Math.Min(a.Length, b.Length) / 4;
        int differing = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (System.Math.Abs(a[o] - b[o]) > 6 || System.Math.Abs(a[o + 1] - b[o + 1]) > 6 || System.Math.Abs(a[o + 2] - b[o + 2]) > 6)
            {
                differing++;
            }
        }

        return pixels == 0 ? 0 : differing / (double)pixels;
    }

    private static long TotalLuminance(byte[] rgba)
    {
        long sum = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            sum += (long)((0.299 * rgba[i]) + (0.587 * rgba[i + 1]) + (0.114 * rgba[i + 2]));
        }

        return sum;
    }

    private static Vector3 ToVec(Ged.Core.Model.Vec3 v) => new(v.X, v.Y, v.Z);
}
