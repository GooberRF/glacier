using System.Numerics;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Rhi.Gl;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Exercises the no-clip-control depth fallback: when ARB_clip_control is absent
/// the GL backend maps the D3D [0,1]-depth projection into GL's default [-1,1] NDC
/// with the in-shader GED_EMIT_CLIP fixup (z:[0,w] -&gt; [-w,w]). This test forces
/// that path on (via the internal test hook) even on hardware that has the
/// extension, then verifies the fixed-up GL frame still matches the D3D11 reference
/// — proving depth ordering and the rendered image survive the fallback.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BackendDepthFixupTests
{
    private const int Size = 384;
    private const int ChannelTolerance = 12;

    private readonly ITestOutputHelper _out;

    public BackendDepthFixupTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DepthFixupPath_Matches_D3D11()
    {
        using GraphicsDevice? d3d = RenderTestSupport.TryCreateDevice(GraphicsBackend.Direct3D11, out string dxReason);
        if (d3d is null)
        {
            _out.WriteLine($"Skipping (D3D11: {dxReason})");
            return;
        }

        // Overlapping geometry so the depth mapping is actually load-bearing.
        RenderScene scene = RenderTestSupport.QuadScene();
        GridBuilder.Append(scene, Vector3.Zero, 20f, 1f, 0.9f, 0f);

        var camera = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
        camera.LookAt(new Vector3(3f, 3f, -6f), new Vector3(0f, 0f, 5f));

        byte[] reference = OffscreenRenderer.Render(d3d, scene, null, camera, RenderMode.JustTextures, Size, Size);

        GlRenderDevice.ForceProjectionDepthFixup = true;
        try
        {
            using GraphicsDevice? gl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
            if (gl is null)
            {
                _out.WriteLine($"Skipping (OpenGL: {glReason})");
                return;
            }

            byte[] fixup = OffscreenRenderer.Render(gl, scene, null, camera, RenderMode.JustTextures, Size, Size);
            double diff = DiffFraction(reference, fixup);
            _out.WriteLine($"depth-fixup vs D3D11: {diff * 100:F3}% differing pixels");
            Assert.True(diff <= 0.01, $"depth-fixup path diverged: {diff * 100:F3}% of pixels differ");
        }
        finally
        {
            GlRenderDevice.ForceProjectionDepthFixup = false;
        }
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
}
