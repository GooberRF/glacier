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
/// Visual acceptance gate for the geometry compiler: recompile corpus levels
/// with GED and render them next to the originals from identical cameras. The
/// JustTextures renders must agree pixel-wise (≤3% differing pixels, per-channel
/// delta &gt;12) for an overview and an interior camera; RoomColors renders are
/// emitted as inspection artifacts only (room color assignment legitimately
/// differs). Skips gracefully without a GPU, corpus, or RF install.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class CompiledParityRenderTests
{
    private const int Size = 640;
    private const int ChannelTolerance = 12;
    private const double MaxDiffFraction = 0.03;

    private readonly ITestOutputHelper _out;

    public CompiledParityRenderTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm04.rfl")]
    [InlineData("glass_house.rfl")]
    [InlineData("dmabruptdecayrc2a27.rfl")] // item 5: community level (881 brushes, Alpine v0x130); measured 0.36% / 0.00%
    public void Recompiled_Level_Renders_Like_The_Original(string fileName)
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
            // Recompile the level's brushes with GED (texture traits from the install).
            RflFile recompiled = RflFile.Load(orig);
            var traits = new TextureTraitsCache(vfs);
            GeometryBuildService.BuildAndApply(recompiled, new CompileOptions
            {
                TextureTraits = traits.Get,
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

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "parity");
            Directory.CreateDirectory(outDir);

            foreach ((string tag, Camera cam) in new[] { ("overview", overview), ("interior", interior) })
            {
                byte[] a = OffscreenRenderer.Render(gd, sceneA, vfs, cam, RenderMode.JustTextures, Size, Size);
                byte[] b = OffscreenRenderer.Render(gd, sceneB, vfs, cam, RenderMode.JustTextures, Size, Size);
                File.WriteAllBytes(Path.Combine(outDir, $"{baseName}_orig_{tag}_JustTextures.png"), PngWriter.Encode(Size, Size, a));
                File.WriteAllBytes(Path.Combine(outDir, $"{baseName}_ged_{tag}_JustTextures.png"), PngWriter.Encode(Size, Size, b));

                double diff = DiffFraction(a, b);
                _out.WriteLine($"{fileName} {tag}: {diff * 100:F2}% differing pixels");
                Assert.True(diff <= MaxDiffFraction,
                    $"{fileName} {tag}: {diff * 100:F2}% of pixels differ (gate {MaxDiffFraction * 100:F0}%)");

                // RoomColors: artifact-only (room count/color assignment differs legitimately).
                byte[] ra = OffscreenRenderer.Render(gd, sceneA, vfs, cam, RenderMode.RoomColors, Size, Size);
                byte[] rb = OffscreenRenderer.Render(gd, sceneB, vfs, cam, RenderMode.RoomColors, Size, Size);
                File.WriteAllBytes(Path.Combine(outDir, $"{baseName}_orig_{tag}_RoomColors.png"), PngWriter.Encode(Size, Size, ra));
                File.WriteAllBytes(Path.Combine(outDir, $"{baseName}_ged_{tag}_RoomColors.png"), PngWriter.Encode(Size, Size, rb));
            }
        }
        finally
        {
            vfs.Dispose();
        }
    }

    /// <summary>Fraction of pixels whose any RGB channel differs by more than the tolerance.</summary>
    private static double DiffFraction(byte[] a, byte[] b)
    {
        int pixels = Math.Min(a.Length, b.Length) / 4;
        int differing = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (Math.Abs(a[o] - b[o]) > ChannelTolerance ||
                Math.Abs(a[o + 1] - b[o + 1]) > ChannelTolerance ||
                Math.Abs(a[o + 2] - b[o + 2]) > ChannelTolerance)
            {
                differing++;
            }
        }

        return pixels == 0 ? 1.0 : differing / (double)pixels;
    }

    private static Vector3 ToVec(Ged.Core.Model.Vec3 v) => new(v.X, v.Y, v.Z);
}
