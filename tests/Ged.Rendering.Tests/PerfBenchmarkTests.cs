using System.Diagnostics;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// A coarse frame-throughput measurement on the largest available corpus level
/// at 1080p, rendering through the offscreen path (build the GPU scene once, then
/// render+sync many frames). Records the measured fps to tests/artifacts. Not a
/// hard perf gate (WARP / CI machines vary), so it only asserts the pipeline ran.
/// </summary>
[Trait("Category", "Perf")] // load-sensitive frame-throughput/wall-clock gates; quarantined (docs/internal/TESTING-PROTOCOL.md)
[Collection(GpuTestCollection.Name)]
public sealed class PerfBenchmarkTests
{
    private static readonly string[] Candidates =
    {
        "ctf07.rfl", "ctf06.rfl", "dmabruptdecayrc2a27.rfl", "ctf01.rfl", "dm01.rfl",
    };

    private readonly ITestOutputHelper _output;

    public PerfBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Dm01_Scene_Build_Under_Ceiling()
    {
        string? path = RenderTestSupport.CorpusFile("dm01.rfl");
        if (path is null)
        {
            return; // corpus unavailable
        }

        RflFile file = RflFile.Load(path);

        // Warm up (JIT + section parse), then take the best of three CPU scene builds.
        _ = SceneBuilder.Build(file, new SceneBuildOptions());
        double bestMs = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());
            sw.Stop();
            _ = scene.Batches.Count;
            bestMs = System.Math.Min(bestMs, sw.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine($"dm01 scene build: {bestMs:F1} ms");
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(RenderTestSupport.ArtifactsDir, "perf.txt"),
            $"dm01 scene build (best of 3): {bestMs:F1} ms (ceiling 2000 ms)\n");

        // < 2s ceiling (generous; dm01 builds in a few ms — this only catches a regression).
        Assert.True(bestMs < 2000, $"dm01 scene build took {bestMs:F1} ms (> 2000 ms ceiling).");
    }

    [Fact]
    public void LargestLevel_FrameThroughput()
    {
        Measure(GraphicsBackend.Direct3D11, gate: false);
    }

    /// <summary>
    /// The CP2/L3 PERF GATE, per backend: the frame-throughput benchmark on the
    /// largest available corpus level at 1080p, run on BOTH the D3D11 and OpenGL
    /// backends so their numbers can be reported side by side. The target is
    /// ≥60 fps on ctf07 at 1080p (the D3D11 bar — CROSSPLATFORM.md documents ~156 fps
    /// D3D11 with per-frame readback), which this measures with the identical
    /// render+readback machinery for a fair comparison. The 60 fps floor is asserted
    /// only on a real hardware device (WARP / a software GL rasterizer / CI vary, so
    /// they record-only), following the existing skip-when-unavailable discipline.
    /// </summary>
    [Theory]
    [InlineData(GraphicsBackend.Direct3D11)]
    [InlineData(GraphicsBackend.OpenGl)]
    public void LargestLevel_FrameThroughput_ByBackend(GraphicsBackend backend)
    {
        Measure(backend, gate: true);
    }

    private void Measure(GraphicsBackend backend, bool gate)
    {
        string? path = null;
        foreach (string c in Candidates)
        {
            path = RenderTestSupport.CorpusFile(c);
            if (path is not null)
            {
                break;
            }
        }

        if (path is null)
        {
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(backend, out string reason);
        if (gd is null)
        {
            _output.WriteLine($"Skipping {backend} ({reason})");
            return;
        }

        AssetVfs? vfs = RenderTestSupport.RfInstall is null ? null : GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            const int width = 1920;
            const int height = 1080;
            RflFile file = RflFile.Load(path);
            RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());

            using var surface = gd.CreateReadbackTarget(width, height);
            using var renderer = new SceneRenderer(gd);
            using var gpu = new GpuScene(gd, scene, vfs);

            var camera = new Camera { AspectRatio = (float)width / height };
            camera.Frame(scene.Bounds);

            // Warm up (shader/state priming, first-use allocations).
            for (int i = 0; i < 5; i++)
            {
                renderer.Render(camera, RenderMode.TexturesAndLightmaps, gpu, surface);
                surface.ReadPixels();
            }

            const int frames = 60;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                renderer.Render(camera, RenderMode.TexturesAndLightmaps, gpu, surface);
                surface.ReadPixels(); // forces GPU completion each frame
            }

            sw.Stop();
            double fps = frames / sw.Elapsed.TotalSeconds;

            string device = gd.IsWarp ? "software" : "hardware";
            string line =
                $"{Path.GetFileName(path)} @ {width}x{height} [{backend}]: {fps:F1} fps " +
                $"({scene.TotalTriangleCount:N0} tris, {scene.Batches.Count} batches, {device}, render+readback)";
            _output.WriteLine(line);
            File.AppendAllText(Path.Combine(RenderTestSupport.ArtifactsDir, "perf.txt"), line + Environment.NewLine);

            Assert.True(fps > 0, "The benchmark should complete at least one frame.");

            // Real hardware must clear the 60 fps bar; software rasterizers only record.
            if (gate && !gd.IsWarp)
            {
                Assert.True(fps >= 60.0,
                    $"{backend} rendered {Path.GetFileName(path)} at {fps:F1} fps (< 60 fps hardware gate).");
            }
        }
        finally
        {
            vfs?.Dispose();
        }
    }
}
