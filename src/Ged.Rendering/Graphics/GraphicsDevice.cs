using Ged.Rendering.Rhi;
using Ged.Rendering.Rhi.D3D11;
using Ged.Rendering.Rhi.Gl;

namespace Ged.Rendering.Graphics;

/// <summary>
/// The shared GPU device for the process: owns one <see cref="IRenderDevice"/>
/// (the D3D11 backend today; L2 adds OpenGL behind the same interface), the
/// runtime-compiled shader programs, and the default textures. Created once and
/// shared by every render target. This class is now a thin facade over the RHI —
/// all Direct3D 11 code lives in <c>Ged.Rendering.Rhi.D3D11</c>.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    private readonly IRenderDevice _device;

    /// <summary>Creates the default Direct3D 11 device (the Windows reference backend).</summary>
    public GraphicsDevice()
        : this(GraphicsBackend.Direct3D11)
    {
    }

    /// <summary>Creates a device on the requested backend (D3D11 default; OpenGL 3.3 for cross-platform/parity).</summary>
    public GraphicsDevice(GraphicsBackend backend)
    {
        Backend = backend;
        _device = CreateBackend(backend);
        IsWarp = _device.IsSoftware;
        Programs = ShaderPrograms.Build(_device);
        Textures = new DefaultTextures(_device);
    }

    private GraphicsDevice(IRenderDevice device)
    {
        Backend = GraphicsBackend.OpenGl;
        _device = device;
        IsWarp = _device.IsSoftware;
        Programs = ShaderPrograms.Build(_device);
        Textures = new DefaultTextures(_device);
    }

    /// <summary>
    /// Creates an OpenGL 3.3-core device that renders through a HOST-OWNED GL context
    /// (<see cref="IExternalGlContext"/>) instead of the RHI's own offscreen context —
    /// the seam for hosting the GL backend inside Avalonia's <c>OpenGlControlBase</c>
    /// (L3) and, later, the Linux window system (L5). The device does NOT tear the
    /// host's GL context down on <see cref="Dispose"/> beyond dropping the managed GL
    /// binding. Must be called on the thread the context is current on (its render/UI
    /// thread).
    /// </summary>
    public static GraphicsDevice CreateOpenGlHosted(IExternalGlContext external)
    {
        ArgumentNullException.ThrowIfNull(external);
        var adapter = new ExternalGlContextAdapter(external);
        return new GraphicsDevice(new GlRenderDevice(adapter, ownsContext: true));
    }

    /// <summary>The backend this device is running on.</summary>
    public GraphicsBackend Backend { get; }

    /// <summary>True when the device is a software rasterizer (D3D11 WARP or a GL software renderer).</summary>
    public bool IsWarp { get; }

    private static IRenderDevice CreateBackend(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Direct3D11 => new D3D11RenderDevice(),
        GraphicsBackend.OpenGl => CreateOpenGl(),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "unknown graphics backend"),
    };

    private static IRenderDevice CreateOpenGl()
    {
        // Platform-selecting headless context: WGL on Windows, EGL (then GLX) on Linux.
        IGlContext? context = OffscreenGlContext.TryCreate(out string reason);
        if (context is null)
        {
            throw new InvalidOperationException($"OpenGL 3.3 core backend unavailable: {reason}");
        }

        return new GlRenderDevice(context, ownsContext: true);
    }

    /// <summary>The render hardware interface (buffers, textures, states, targets).</summary>
    internal IRenderDevice Rhi => _device;

    /// <summary>The immediate command context.</summary>
    internal IRenderContext Context => _device.Context;

    internal ShaderPrograms Programs { get; }

    internal DefaultTextures Textures { get; }

    /// <summary>Swaps the billboard icon atlas (GED-drawn default, or RED-original composited).</summary>
    public void SetIconAtlas(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        Textures.SetIcons(_device, rgba);
    }

    // ---- Pipeline states (delegated to the RHI device) ----

    internal IRasterizerState RasterSolid => _device.RasterizerSolid;

    /// <summary>Solid fill with back-face culling (RED parity for world/mesh solid passes).</summary>
    internal IRasterizerState RasterSolidCull => _device.RasterizerSolidCull;

    internal IRasterizerState RasterWireframe => _device.RasterizerWireframe;

    internal IDepthStencilState DepthDefault => _device.DepthDefault;

    internal IDepthStencilState DepthNoWrite => _device.DepthNoWrite;

    internal IDepthStencilState DepthNoTest => _device.DepthNoTest;

    internal IBlendState BlendOpaque => _device.BlendOpaque;

    internal IBlendState BlendAlpha => _device.BlendAlpha;

    internal IGpuSampler Sampler => _device.LinearWrapSampler;

    // ---- Resource creation (delegated) ----

    internal IGpuBuffer CreateVertexBuffer<T>(ReadOnlySpan<T> data)
        where T : unmanaged => _device.CreateVertexBuffer(data);

    internal IGpuBuffer CreateIndexBuffer(ReadOnlySpan<uint> data) => _device.CreateIndexBuffer(data);

    internal IGpuBuffer CreateConstantBuffer(int byteWidth) => _device.CreateConstantBuffer(byteWidth);

    internal IGpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba) =>
        _device.CreateTexture(width, height, rgba);

    // ---- Render targets (delegated) ----

    internal ISwapChainTarget CreateSwapChain(nint windowHandle, int width, int height) =>
        _device.CreateSwapChain(windowHandle, width, height);

    internal IReadbackTarget CreateReadbackTarget(int width, int height) =>
        _device.CreateReadbackTarget(width, height);

    internal IPickTarget CreatePickTarget(int width, int height) =>
        _device.CreatePickTarget(width, height);

    public void Dispose()
    {
        Programs?.Dispose();
        Textures?.Dispose();
        _device.Dispose();
    }
}
