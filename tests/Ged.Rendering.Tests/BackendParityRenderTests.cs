using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Core.Tables;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// The backend-parity gate (the CP2 crown jewel): render the SAME scene, camera
/// and mode offscreen on both the D3D11 and OpenGL backends and assert the frames
/// are near-identical. Both backends run the identical scene-building/rendering
/// code above the RHI, so any difference is a backend deviation. Target: ≤1%
/// differing pixels per view (per-channel delta &gt; 12); the residual is
/// edge-only rasterization difference. Per-view numbers are logged and PNGs are
/// written to tests/artifacts/backend-parity for inspection. Skips gracefully
/// when either backend (or the corpus/RF install) is unavailable.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BackendParityRenderTests
{
    private const int Size = 512;
    private const int ChannelTolerance = 12;
    private const double MaxDiffFraction = 0.01;

    private readonly ITestOutputHelper _out;

    public BackendParityRenderTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SyntheticScene_Matches_AcrossBackends()
    {
        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        // World geometry (the quad) plus a line grid — always runs, no assets needed.
        RenderScene scene = RenderTestSupport.QuadScene();
        GridBuilder.Append(scene, Vector3.Zero, 20f, 1f, 0.9f, 0f);

        var camera = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
        camera.LookAt(new Vector3(4f, 4f, -6f), new Vector3(0f, 0f, 5f));

        foreach (RenderMode mode in new[] { RenderMode.JustTextures, RenderMode.RoomColors, RenderMode.Wireframe })
        {
            CompareView(d3d, gl, scene, null, camera, mode, "synthetic", mode.ToString());
        }
    }

    [Theory]
    [InlineData("dm01.rfl")]
    [InlineData("dm04.rfl")]
    public void CorpusLevel_Matches_AcrossBackends(string fileName)
    {
        string? path = RenderTestSupport.CorpusFile(fileName);
        if (path is null)
        {
            return;
        }

        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        AssetVfs? vfs = RenderTestSupport.RfInstall is null ? null : GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            RflFile file = RflFile.Load(path);
            // Objects on -> billboards (icon atlas) exercised; grid -> line overlay exercised.
            var options = new SceneBuildOptions
            {
                Entities = vfs is null ? null : TryLoad(vfs, "entity.tbl", EntityCatalog.Load),
                Clutter = vfs is null ? null : TryLoad(vfs, "clutter.tbl", ClutterCatalog.Load),
                Items = vfs is null ? null : TryLoad(vfs, "items.tbl", ItemCatalog.Load),
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            };
            RenderScene scene = SceneBuilder.Build(file, options);
            Vector3 center = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
            GridBuilder.Append(scene, center, 40f, 2f, 0.8f, scene.Bounds.P1.Y);

            var overview = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            overview.Frame(scene.Bounds);

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            RenderMode[] modes =
            {
                RenderMode.JustTextures,
                RenderMode.TexturesAndLightmaps,
                RenderMode.RoomColors,
                RenderMode.Wireframe,
            };

            foreach (RenderMode mode in modes)
            {
                CompareView(d3d, gl, scene, vfs, overview, mode, baseName, mode.ToString());
            }
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    private void CompareView(
        GraphicsDevice d3d,
        GraphicsDevice gl,
        RenderScene scene,
        AssetVfs? vfs,
        Camera camera,
        RenderMode mode,
        string label,
        string tag)
    {
        byte[] a = OffscreenRenderer.Render(d3d, scene, vfs, camera, mode, Size, Size);
        byte[] b = OffscreenRenderer.Render(gl, scene, vfs, camera, mode, Size, Size);

        string outDir = Path.Combine(RenderTestSupport.ArtifactsDir, "backend-parity");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, $"{label}_{tag}_d3d11.png"), PngWriter.Encode(Size, Size, a));
        File.WriteAllBytes(Path.Combine(outDir, $"{label}_{tag}_opengl.png"), PngWriter.Encode(Size, Size, b));

        double diff = DiffFraction(a, b);
        _out.WriteLine($"{label} {tag}: {diff * 100:F3}% differing pixels (D3D11 <-> OpenGL)");
        Assert.True(
            diff <= MaxDiffFraction,
            $"{label} {tag}: {diff * 100:F3}% of pixels differ (gate {MaxDiffFraction * 100:F0}%)");
    }

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

    private static T? TryLoad<T>(AssetVfs vfs, string name, Func<byte[], T> parse)
        where T : class
    {
        try
        {
            byte[]? data = vfs.ReadFile(name);
            return data is null ? null : parse(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Vector3 ToVec(Ged.Core.Model.Vec3 v) => new(v.X, v.Y, v.Z);
}
