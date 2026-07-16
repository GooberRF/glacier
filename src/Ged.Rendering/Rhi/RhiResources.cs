namespace Ged.Rendering.Rhi;

/// <summary>
/// A GPU buffer (vertex, index, or constant). Opaque handle owned by the caller;
/// disposed to release the underlying GPU allocation.
/// </summary>
public interface IGpuBuffer : IDisposable
{
}

/// <summary>A sampleable 2D texture (RGBA8, top-left origin) plus its bind view.</summary>
public interface IGpuTexture : IDisposable
{
}

/// <summary>A texture sampler state.</summary>
public interface IGpuSampler : IDisposable
{
}

/// <summary>
/// A compiled shader program: a vertex + shading pixel stage, an optional
/// id-buffer (pick) pixel stage, and the vertex input layout. Bound via
/// <see cref="IRenderContext.SetProgram"/>, which selects the shading or pick
/// stage.
/// </summary>
public interface IShaderProgram : IDisposable
{
    /// <summary>True when this program has a pick (id-buffer) pixel stage.</summary>
    bool HasPick { get; }
}

/// <summary>Fixed-function rasterizer state (fill mode + cull mode).</summary>
public interface IRasterizerState : IDisposable
{
}

/// <summary>Depth-stencil state (depth test/write function).</summary>
public interface IDepthStencilState : IDisposable
{
}

/// <summary>Color-blend state.</summary>
public interface IBlendState : IDisposable
{
}

/// <summary>A bindable render target with a color surface and a depth surface, sized in pixels.</summary>
public interface IRenderTarget
{
    /// <summary>Target width in pixels.</summary>
    int Width { get; }

    /// <summary>Target height in pixels.</summary>
    int Height { get; }
}

/// <summary>A swapchain bound to a native window: renders then presents to the screen.</summary>
public interface ISwapChainTarget : IRenderTarget, IDisposable
{
    /// <summary>Presents the current back buffer. <paramref name="vsync"/> gates on vblank.</summary>
    void Present(bool vsync);

    /// <summary>Resizes the swapchain and depth buffers to a new client size (no-op if unchanged).</summary>
    void Resize(int width, int height);
}

/// <summary>An offscreen color+depth target whose color surface can be read back to the CPU.</summary>
public interface IReadbackTarget : IRenderTarget, IDisposable
{
    /// <summary>Copies the rendered color buffer to CPU memory as tightly packed RGBA8 (top-left origin).</summary>
    byte[] ReadPixels();
}

/// <summary>
/// A GPU id-buffer: an R32_UINT color target plus depth. The scene is re-rendered
/// with the pick pixel shaders, then a single pixel is read back on the CPU.
/// </summary>
public interface IPickTarget : IRenderTarget, IDisposable
{
    /// <summary>Resizes the id-buffer and depth to a new size (no-op if unchanged).</summary>
    void Resize(int width, int height);

    /// <summary>Reads the raw encoded pick id at a pixel; out-of-range returns 0.</summary>
    uint ReadPick(int x, int y);
}
