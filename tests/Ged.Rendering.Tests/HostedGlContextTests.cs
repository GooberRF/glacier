using System.Numerics;
using Ged.Rendering.Graphics;
using Ged.Rendering.Rhi.Gl;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Verifies the PUBLIC host seam <see cref="GraphicsDevice.CreateOpenGlHosted"/> —
/// the exact building block L3 uses to host the GL backend inside Avalonia's
/// <c>OpenGlControlBase</c> and L5 will use over the Linux window system. It drives
/// the seam over a real GL context (the WGL offscreen context, wrapped as an
/// <see cref="IExternalGlContext"/> exactly as an onscreen host would wrap Avalonia's
/// <c>GlInterface</c>) and asserts a hosted-seam render is byte-for-byte identical to
/// the RHI's own internal GL path — proving the adapter loads the same GL 3.3 table,
/// honours the host-reported default framebuffer, and owns no context lifetime it
/// should not. Skips when no GL 3.3 core context is available, like the other GL tests.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class HostedGlContextTests
{
    private const int Size = 256;

    private readonly ITestOutputHelper _out;

    public HostedGlContextTests(ITestOutputHelper output) => _out = output;

    private static readonly RenderMode[] Modes = { RenderMode.JustTextures, RenderMode.RoomColors, RenderMode.Wireframe };

    [Fact]
    public void HostedSeam_Renders_ByteIdentical_To_InternalGlPath()
    {
        WglOffscreenContext? probe = WglOffscreenContext.TryCreate(out string reason);
        if (probe is null)
        {
            _out.WriteLine($"Skipping (no GL 3.3 core context: {reason})");
            return;
        }

        probe.Dispose();

        RenderScene scene = RenderTestSupport.QuadScene();
        GridBuilder.Append(scene, Vector3.Zero, 20f, 1f, 0.9f, 0f);
        var camera = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
        camera.LookAt(new Vector3(4f, 4f, -6f), new Vector3(0f, 0f, 5f));

        // Two GL contexts on one thread must not be driven interleaved (neither device
        // re-makes-current per frame), so render the HOSTED-seam device fully, capture,
        // dispose it, then render the internal GL path and compare. Same GPU + same GL
        // code path => the two must be byte-for-byte identical.
        var hostedFrames = new byte[Modes.Length][];
        WglOffscreenContext hostedWgl = WglOffscreenContext.TryCreate(out _)!;
        var external = new WglExternalContext(hostedWgl);
        using (GraphicsDevice hosted = GraphicsDevice.CreateOpenGlHosted(external))
        {
            Assert.Equal(GraphicsBackend.OpenGl, hosted.Backend);
            for (int i = 0; i < Modes.Length; i++)
            {
                hostedFrames[i] = OffscreenRenderer.Render(hosted, scene, null, camera, Modes[i], Size, Size);
            }
        }

        external.Dispose();

        using GraphicsDevice? internalGl = RenderTestSupport.TryCreateDevice(GraphicsBackend.OpenGl, out string glReason);
        Assert.True(internalGl is not null, $"internal GL device unavailable: {glReason}");
        for (int i = 0; i < Modes.Length; i++)
        {
            byte[] b = OffscreenRenderer.Render(internalGl!, scene, null, camera, Modes[i], Size, Size);
            int diff = CountDiff(hostedFrames[i], b);
            _out.WriteLine($"{Modes[i]}: {diff} differing pixels (hosted seam vs internal GL)");
            Assert.Equal(0, diff);
        }
    }

    private static int CountDiff(byte[] a, byte[] b)
    {
        int pixels = System.Math.Min(a.Length, b.Length) / 4;
        int differing = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (a[o] != b[o] || a[o + 1] != b[o + 1] || a[o + 2] != b[o + 2] || a[o + 3] != b[o + 3])
            {
                differing++;
            }
        }

        return differing;
    }

    /// <summary>
    /// Test double for a host-owned GL context: it delegates to a live
    /// <see cref="WglOffscreenContext"/>, mirroring how the Avalonia host wraps its
    /// <c>GlInterface</c> (GetProcAddress → the loader, fb → the fb Avalonia hands the
    /// callback, MakeCurrent/SwapBuffers → context/present hooks). It does NOT own the
    /// underlying context — that lifetime is the outer test's.
    /// </summary>
    private sealed class WglExternalContext : IExternalGlContext, System.IDisposable
    {
        private readonly WglOffscreenContext _inner;

        public WglExternalContext(WglOffscreenContext inner) => _inner = inner;

        public uint DefaultFramebuffer => 0;

        public nint GetProcAddress(string name) => _inner.GetProcAddress(name);

        public void MakeCurrent() => _inner.MakeCurrent();

        public void SwapBuffers()
        {
            // Offscreen hidden window: no present needed for the readback path.
        }

        public void Dispose() => _inner.Dispose();
    }
}
