namespace Ged.Rendering.Rhi;

/// <summary>
/// The render hardware interface: a GPU device that creates buffers, textures,
/// samplers, shader programs, fixed pipeline states, and render targets, and owns
/// the immediate <see cref="IRenderContext"/>. The D3D11 backend
/// (<c>Ged.Rendering.Rhi.D3D11</c>) is the only implementation today; L2 adds an
/// OpenGL 3.3 backend behind this same interface. The scene-building layer
/// (<c>SceneBuilder</c>, <c>BrushEmitter</c>, <c>OverlayBuilder</c>,
/// <c>IconAtlas</c>, pick-id encoding) never touches this — it is GPU-agnostic.
/// </summary>
public interface IRenderDevice : IDisposable
{
    /// <summary>True when the device is a software rasterizer (no feature-level-11 hardware).</summary>
    bool IsSoftware { get; }

    /// <summary>The immediate command context.</summary>
    IRenderContext Context { get; }

    // ---- Fixed pipeline states (created once, owned by the device) ----

    /// <summary>Solid fill, no culling.</summary>
    IRasterizerState RasterizerSolid { get; }

    /// <summary>Solid fill with back-face culling (RED-parity solid world/mesh passes).</summary>
    IRasterizerState RasterizerSolidCull { get; }

    /// <summary>Wireframe fill, no culling.</summary>
    IRasterizerState RasterizerWireframe { get; }

    /// <summary>Depth test Less, depth write on.</summary>
    IDepthStencilState DepthDefault { get; }

    /// <summary>Depth test LessEqual, depth write off (transparent/overlay passes).</summary>
    IDepthStencilState DepthNoWrite { get; }

    /// <summary>Depth test Always, depth write off (on-top gizmo/labels — never occluded).</summary>
    IDepthStencilState DepthNoTest { get; }

    /// <summary>Blending disabled (opaque).</summary>
    IBlendState BlendOpaque { get; }

    /// <summary>Straight-alpha source-over blending.</summary>
    IBlendState BlendAlpha { get; }

    /// <summary>Additive blending (src=ONE, dst=ONE) for glow/effect (VFX) draws.</summary>
    IBlendState BlendAdditive { get; }

    /// <summary>The shared linear-wrap sampler.</summary>
    IGpuSampler LinearWrapSampler { get; }

    // ---- Resource creation ----

    /// <summary>Creates an immutable vertex buffer from CPU data.</summary>
    IGpuBuffer CreateVertexBuffer<T>(ReadOnlySpan<T> data)
        where T : unmanaged;

    /// <summary>Creates an immutable 32-bit index buffer from CPU data.</summary>
    IGpuBuffer CreateIndexBuffer(ReadOnlySpan<uint> data);

    /// <summary>Creates a dynamic (CPU-writable) constant buffer of the given byte width.</summary>
    IGpuBuffer CreateConstantBuffer(int byteWidth);

    /// <summary>Creates an immutable RGBA8 texture (top-left origin) and its bind view.</summary>
    IGpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba);

    /// <summary>Compiles a shader program (vertex + shading + optional pick) and builds its input layout.</summary>
    IShaderProgram CreateShaderProgram(ShaderProgramDesc desc);

    // ---- Render targets ----

    /// <summary>Creates a swapchain target bound to a native window handle.</summary>
    ISwapChainTarget CreateSwapChain(nint windowHandle, int width, int height);

    /// <summary>Creates an offscreen color+depth target with CPU readback (PNG / thumbnail / test path).</summary>
    IReadbackTarget CreateReadbackTarget(int width, int height);

    /// <summary>Creates an R32_UINT id-buffer target for GPU picking.</summary>
    IPickTarget CreatePickTarget(int width, int height);
}
