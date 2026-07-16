using Ged.Core.IO.Tex;
using Ged.Rendering.Graphics;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Validates that the full D3D11 pipeline (device, shader compile, buffers,
/// offscreen render, readback) actually runs and produces a non-trivial image.
/// Uses a synthetic scene so it needs no external assets.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class OffscreenSmokeTests
{
    [Fact]
    public void SyntheticQuad_RendersNonTrivialImage()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out string reason);
        if (gd is null)
        {
            // No GPU/WARP in this environment: skip gracefully (passes trivially).
            return;
        }

        var camera = new Camera { Position = new System.Numerics.Vector3(0f, 0f, 0f), Yaw = 0f, Pitch = 0f };
        byte[] pixels = OffscreenRenderer.Render(
            gd!, RenderTestSupport.QuadScene(), vfs: null, camera, RenderMode.JustTextures, 256, 256);

        Assert.Equal(256 * 256 * 4, pixels.Length);
        bool nonTrivial = RenderTestSupport.IsNonTrivial(pixels, out int distinct);
        Assert.True(nonTrivial, $"Rendered image was trivial (distinct colors: {distinct}).");

        File.WriteAllBytes(
            Path.Combine(RenderTestSupport.ArtifactsDir, "synthetic_quad.png"),
            PngWriter.Encode(256, 256, pixels));
    }
}
