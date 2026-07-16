using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// End-to-end bake acceptance gate: recompile a stock level's geometry AND bake
/// its lights with GED, then render (TexturesAndLightmaps) from the overview +
/// interior cameras and compare against the ORIGINAL level's RED-baked render.
/// Measured after the item-7 RED-parity smoothing (interpolated vertex normals on
/// should_smooth surfaces, unweighted mean + 90° hemisphere cutoff, on by default):
/// dm01 1.39/2.00% (lum 0.987/0.986), dm04 0.63/4.19% (lum 1.007/1.051),
/// glass_house 1.54/8.70% (lum 0.964/0.994).
/// glass_house's interior residual also exists on IDENTICAL geometry
/// (<see cref="LightingParityRenderTests"/> measures 9.3% for the same view): its
/// shipped atlas deviates from the current light data under RED's own kernel (a
/// model sweep — wrap / half-lambert / atten-only / 2×wrap / √half — shows the
/// implemented kernel is the best fit of every candidate, and shadows, smoothing,
/// normals and state knobs are all measured no-ops), so that one view is gated at
/// its measured envelope. Gate tightened to ≤6% / lum ±7% (from 8%/10%). Renders
/// land in tests/artifacts/lighting/. Skips without GPU/corpus/RF.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BakeParityRenderTests
{
    private const int Size = 640;
    private const int ChannelTolerance = 16;
    private const double MaxDiffFraction = 0.06;
    private const double GlassHouseInteriorMaxDiff = 0.10;
    private const double LumRatioTolerance = 0.07;

    private readonly ITestOutputHelper _out;

    public BakeParityRenderTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm04.rfl")]
    [InlineData("glass_house.rfl")]
    public void Baked_Lighting_Renders_Like_The_Original(string fileName)
    {
        string? orig = RenderTestSupport.CorpusFile(fileName);
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
            RflFile recompiled = RflFile.Load(orig);
            var traits = new TextureTraitsCache(vfs);
            GeometryBuildService.BuildAndApply(recompiled, new CompileOptions
            {
                TextureTraits = traits.Get,
                BakeLighting = true,
                // TRUE shipping default = RED's authentic shared BSP (the owner-approved flip). GED_FORCE_PERBRUSH=1
                // forces the legacy per-brush path for A/B comparison — that needs BOTH the shared-BSP branch off
                // (SharedBsp = false, it is dispatched first) AND the incremental branch off. Unset exercises the
                // real default (SharedBsp = true, the CompileOptions default).
                SharedBsp = !RenderTestSupport.ForcePerBrush,
                IncrementalAccumulator = !RenderTestSupport.ForcePerBrush,
            });

            var options = new SceneBuildOptions
            {
                IncludeObjects = false,
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            };
            RenderScene sceneA = SceneBuilder.Build(RflFile.Load(orig), options);
            RenderScene sceneB = SceneBuilder.Build(recompiled, options);

            var overview = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            overview.Frame(sceneA.Bounds);

            Vector3 c = (ToVec(sceneA.Bounds.P1) + ToVec(sceneA.Bounds.P2)) * 0.5f;
            var interior = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            interior.LookAt(c + new Vector3(3f, 1.5f, 0f), c + new Vector3(0f, 1f, 4f));

            string baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string outDir = System.IO.Path.Combine(RenderTestSupport.ArtifactsDir, "lighting");
            System.IO.Directory.CreateDirectory(outDir);

            foreach ((string tag, Camera cam) in new[] { ("overview", overview), ("interior", interior) })
            {
                byte[] a = OffscreenRenderer.Render(gd, sceneA, vfs, cam, RenderMode.TexturesAndLightmaps, Size, Size);
                byte[] b = OffscreenRenderer.Render(gd, sceneB, vfs, cam, RenderMode.TexturesAndLightmaps, Size, Size);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"{baseName}_orig_{tag}.png"), PngWriter.Encode(Size, Size, a));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"{baseName}_ged_{tag}.png"), PngWriter.Encode(Size, Size, b));

                double diff = DiffFraction(a, b);
                double lumRatio = MeanLuminance(b) / System.Math.Max(1e-6, MeanLuminance(a));
                _out.WriteLine($"{fileName} {tag}: {diff * 100:F2}% differing pixels, luminance ratio {lumRatio:F3}");

                double gate = fileName == "glass_house.rfl" && tag == "interior"
                    ? GlassHouseInteriorMaxDiff : MaxDiffFraction;
                Assert.True(diff <= gate,
                    $"{fileName} {tag}: {diff * 100:F2}% of pixels differ (gate {gate * 100:F0}%)");
                Assert.True(System.Math.Abs(lumRatio - 1.0) <= LumRatioTolerance,
                    $"{fileName} {tag}: mean-luminance ratio {lumRatio:F3} outside ±{LumRatioTolerance * 100:F0}%");
            }
        }
        finally
        {
            vfs.Dispose();
        }
    }

    private static double DiffFraction(byte[] a, byte[] b)
    {
        int pixels = System.Math.Min(a.Length, b.Length) / 4;
        int differing = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (System.Math.Abs(a[o] - b[o]) > ChannelTolerance ||
                System.Math.Abs(a[o + 1] - b[o + 1]) > ChannelTolerance ||
                System.Math.Abs(a[o + 2] - b[o + 2]) > ChannelTolerance)
            {
                differing++;
            }
        }

        return pixels == 0 ? 1.0 : differing / (double)pixels;
    }

    private static double MeanLuminance(byte[] rgba)
    {
        int pixels = rgba.Length / 4;
        double sum = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            sum += (0.299 * rgba[o]) + (0.587 * rgba[o + 1]) + (0.114 * rgba[o + 2]);
        }

        return pixels == 0 ? 0 : sum / pixels;
    }

    private static Vector3 ToVec(Ged.Core.Model.Vec3 v) => new(v.X, v.Y, v.Z);
}
