using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Ged.Rendering.Rhi.D3D11;

/// <summary>D3D11 vertex/index/constant buffer.</summary>
internal sealed unsafe class D3D11Buffer : IGpuBuffer
{
    public D3D11Buffer(ComPtr<ID3D11Buffer> buffer) => Buffer = buffer;

    public ComPtr<ID3D11Buffer> Buffer;

    public ID3D11Buffer* Handle => Buffer.Handle;

    public void Dispose() => Buffer.Dispose();
}

/// <summary>D3D11 sampleable texture (holds only the shader-resource view; the texture is transient).</summary>
internal sealed unsafe class D3D11Texture : IGpuTexture
{
    public D3D11Texture(ComPtr<ID3D11ShaderResourceView> srv) => Srv = srv;

    public ComPtr<ID3D11ShaderResourceView> Srv;

    public void Dispose() => Srv.Dispose();
}

/// <summary>D3D11 sampler state.</summary>
internal sealed unsafe class D3D11Sampler : IGpuSampler
{
    public D3D11Sampler(ComPtr<ID3D11SamplerState> sampler) => Sampler = sampler;

    public ComPtr<ID3D11SamplerState> Sampler;

    public void Dispose() => Sampler.Dispose();
}

/// <summary>D3D11 rasterizer state.</summary>
internal sealed unsafe class D3D11RasterizerState : IRasterizerState
{
    public D3D11RasterizerState(ComPtr<ID3D11RasterizerState> state) => State = state;

    public ComPtr<ID3D11RasterizerState> State;

    public void Dispose() => State.Dispose();
}

/// <summary>D3D11 depth-stencil state.</summary>
internal sealed unsafe class D3D11DepthStencilState : IDepthStencilState
{
    public D3D11DepthStencilState(ComPtr<ID3D11DepthStencilState> state) => State = state;

    public ComPtr<ID3D11DepthStencilState> State;

    public void Dispose() => State.Dispose();
}

/// <summary>D3D11 blend state.</summary>
internal sealed unsafe class D3D11BlendState : IBlendState
{
    public D3D11BlendState(ComPtr<ID3D11BlendState> state) => State = state;

    public ComPtr<ID3D11BlendState> State;

    public void Dispose() => State.Dispose();
}

/// <summary>
/// A compiled D3D11 program: vertex shader, shading pixel shader, optional pick
/// (id-buffer) pixel shader, and the input layout. <see cref="IRenderContext.SetProgram"/>
/// binds the layout + vertex shader and selects the shading or pick pixel shader.
/// </summary>
internal sealed unsafe class D3D11ShaderProgram : IShaderProgram
{
    public ComPtr<ID3D11VertexShader> Vertex;
    public ComPtr<ID3D11PixelShader> Pixel;
    public ComPtr<ID3D11PixelShader> Pick;
    public ComPtr<ID3D11InputLayout> Layout;

    public bool HasPick { get; init; }

    public void Dispose()
    {
        Layout.Dispose();
        if (HasPick)
        {
            Pick.Dispose();
        }

        Pixel.Dispose();
        Vertex.Dispose();
    }
}
