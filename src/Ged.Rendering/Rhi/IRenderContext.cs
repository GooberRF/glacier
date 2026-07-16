using System.Numerics;

namespace Ged.Rendering.Rhi;

/// <summary>
/// The immediate rendering context: the ordered command stream a frame issues
/// (bind target, clear, set pipeline state, bind resources, draw). The D3D11
/// backend forwards each call to the <c>ID3D11DeviceContext</c>; an OpenGL
/// backend (L2) forwards to the GL state machine. Every method maps 1:1 to a
/// single backend state or draw operation so behaviour is identical regardless of
/// backend.
/// </summary>
public interface IRenderContext
{
    /// <summary>Binds the target's color+depth surfaces and sets the viewport to its full extent.</summary>
    void SetRenderTarget(IRenderTarget target);

    /// <summary>Clears the bound target's color surface to <paramref name="color"/> (RGBA, straight).</summary>
    void ClearColor(IRenderTarget target, Vector4 color);

    /// <summary>Clears the bound target's depth surface to 1.0 (far).</summary>
    void ClearDepth(IRenderTarget target);

    /// <summary>Sets the rasterizer state (fill/cull).</summary>
    void SetRasterizerState(IRasterizerState state);

    /// <summary>Sets the depth-stencil state (depth test/write).</summary>
    void SetDepthStencilState(IDepthStencilState state);

    /// <summary>Sets the color-blend state.</summary>
    void SetBlendState(IBlendState state);

    /// <summary>Binds a program; <paramref name="pick"/> selects its id-buffer pixel stage.</summary>
    void SetProgram(IShaderProgram program, bool pick);

    /// <summary>Binds a sampler at a pixel-shader sampler slot.</summary>
    void SetSampler(int slot, IGpuSampler sampler);

    /// <summary>Binds a constant buffer at <paramref name="slot"/> for BOTH the vertex and pixel stages.</summary>
    void SetConstantBuffer(int slot, IGpuBuffer buffer);

    /// <summary>Uploads <paramref name="value"/> into a dynamic constant buffer (write-discard map).</summary>
    void UpdateConstantBuffer<T>(IGpuBuffer buffer, in T value)
        where T : unmanaged;

    /// <summary>Binds a texture as a pixel-shader resource at <paramref name="slot"/>.</summary>
    void SetTexture(int slot, IGpuTexture texture);

    /// <summary>Sets the primitive assembly topology.</summary>
    void SetPrimitiveTopology(PrimitiveTopology topology);

    /// <summary>Binds a vertex buffer at slot 0 with the given per-vertex stride.</summary>
    void SetVertexBuffer(IGpuBuffer buffer, int stride);

    /// <summary>Binds a 32-bit index buffer.</summary>
    void SetIndexBuffer(IGpuBuffer buffer);

    /// <summary>Draws <paramref name="indexCount"/> indexed primitives.</summary>
    void DrawIndexed(int indexCount);

    /// <summary>Draws <paramref name="vertexCount"/> non-indexed vertices.</summary>
    void Draw(int vertexCount);
}
