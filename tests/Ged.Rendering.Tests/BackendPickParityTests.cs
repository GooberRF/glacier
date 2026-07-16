using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Pick-readback equivalence between the backends: the R32_UINT id-buffer pass and
/// the single-pixel readback must decode the SAME <see cref="PickId"/> at the same
/// pixels on D3D11 and OpenGL. Covers the top-left-origin Y-flip on the GL
/// glReadPixels path and the integer-target clear/format handling. Uses the
/// synthetic quad (no assets) plus a corpus level for face-id variety when present.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BackendPickParityTests
{
    private readonly ITestOutputHelper _out;

    public BackendPickParityTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SyntheticQuad_Pick_Matches_AcrossBackends()
    {
        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        if (d3d is null || gl is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason}; OpenGL: {glReason})");
            return;
        }

        const int size = 96;
        var camera = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f, AspectRatio = 1f };
        RenderScene scene = RenderTestSupport.QuadScene();

        int mismatches = 0;
        int sampled = 0;
        using (var rD = new SceneRenderer(d3d))
        using (var rG = new SceneRenderer(gl))
        using (var gpuD = new GpuScene(d3d, scene, null))
        using (var gpuG = new GpuScene(gl, scene, null))
        using (var pickD = d3d.CreatePickTarget(size, size))
        using (var pickG = gl.CreatePickTarget(size, size))
        {
            // GL is bottom-left origin; this grid sweep verifies the top-left-origin
            // Y-flip lands the same ids as D3D at every sampled pixel (incl. edges/misses).
            for (int y = 8; y < size; y += 12)
            {
                for (int x = 8; x < size; x += 12)
                {
                    PickId a = rD.RenderPick(camera, gpuD, pickD, x, y);
                    PickId b = rG.RenderPick(camera, gpuG, pickG, x, y);
                    sampled++;
                    if (a.Encode() != b.Encode())
                    {
                        mismatches++;
                        _out.WriteLine($"pixel ({x},{y}): D3D={a.Kind}#{a.Index} GL={b.Kind}#{b.Index}");
                    }
                }
            }
        }

        _out.WriteLine($"synthetic pick: {sampled - mismatches}/{sampled} pixels matched");
        Assert.Equal(0, mismatches);
    }

    [Theory]
    [InlineData("dm01.rfl")]
    public void CorpusLevel_Pick_Matches_AcrossBackends(string fileName)
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
            const int size = 256;
            RflFile file = RflFile.Load(path);
            var options = new SceneBuildOptions
            {
                IncludeObjects = false,
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            };
            RenderScene scene = SceneBuilder.Build(file, options);
            var camera = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
            camera.Frame(scene.Bounds);

            int mismatches = 0;
            int hits = 0;
            int sampled = 0;
            using (var rD = new SceneRenderer(d3d))
            using (var rG = new SceneRenderer(gl))
            using (var gpuD = new GpuScene(d3d, scene, vfs))
            using (var gpuG = new GpuScene(gl, scene, vfs))
            using (var pickD = d3d.CreatePickTarget(size, size))
            using (var pickG = gl.CreatePickTarget(size, size))
            {
                for (int y = 16; y < size; y += 16)
                {
                    for (int x = 16; x < size; x += 16)
                    {
                        PickId a = rD.RenderPick(camera, gpuD, pickD, x, y);
                        PickId b = rG.RenderPick(camera, gpuG, pickG, x, y);
                        sampled++;
                        if (!a.IsNone)
                        {
                            hits++;
                        }

                        if (a.Encode() != b.Encode())
                        {
                            mismatches++;
                        }
                    }
                }
            }

            _out.WriteLine($"{fileName} pick: {sampled - mismatches}/{sampled} matched ({hits} D3D hits sampled)");
            Assert.True(hits > 0, "expected at least some face hits in the overview pick");
            Assert.Equal(0, mismatches);
        }
        finally
        {
            vfs?.Dispose();
        }
    }
}
