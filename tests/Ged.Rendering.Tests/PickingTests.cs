using System.Numerics;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Exercises the full GPU id-buffer pick path (R32_UINT pass + 1x1 readback +
/// decode) against the synthetic quad, so it needs no external assets. Skips
/// gracefully when no D3D device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class PickingTests
{
    [Fact]
    public void CenterHitsFace_CornerHitsNothing()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        const int size = 64;
        using var renderer = new SceneRenderer(gd);
        using var gpu = new GpuScene(gd, RenderTestSupport.QuadScene(), null);
        using var pick = gd.CreatePickTarget(size, size);

        var camera = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f, AspectRatio = 1f };

        // Center of the view is on the quad -> face 0.
        PickId center = renderer.RenderPick(camera, gpu, pick, size / 2, size / 2);
        Assert.Equal(PickKind.Face, center.Kind);
        Assert.Equal(0, center.Index);

        // A corner sees past the quad edge -> nothing.
        PickId corner = renderer.RenderPick(camera, gpu, pick, 1, 1);
        Assert.True(corner.IsNone, $"Corner pick should be empty but was {corner.Kind} #{corner.Index}.");
    }
}
