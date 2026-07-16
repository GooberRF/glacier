using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Ged.Rendering.Rhi.D3D11;

/// <summary>
/// The D3D11 immediate context: forwards every RHI command to the shared
/// <c>ID3D11DeviceContext</c>. The call order and state semantics are exactly
/// those the renderer issued directly before the RHI extraction, so behaviour is
/// bit-identical.
/// </summary>
internal sealed unsafe class D3D11RenderContext : IRenderContext
{
    private readonly ComPtr<ID3D11DeviceContext> _ctx;

    public D3D11RenderContext(ComPtr<ID3D11DeviceContext> context) => _ctx = context;

    private ID3D11DeviceContext* Ctx => _ctx.Handle;

    public void SetRenderTarget(IRenderTarget target)
    {
        var t = (ID3D11Target)target;
        ID3D11RenderTargetView* rtv = t.RtvHandle;
        var vp = new Silk.NET.Direct3D11.Viewport(0f, 0f, t.Width, t.Height, 0f, 1f);
        Ctx->RSSetViewports(1, &vp);
        Ctx->OMSetRenderTargets(1, &rtv, t.DsvHandle);
    }

    public void ClearColor(IRenderTarget target, Vector4 color)
    {
        var t = (ID3D11Target)target;
        float* clear = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
        Ctx->ClearRenderTargetView(t.RtvHandle, clear);
    }

    public void ClearDepth(IRenderTarget target)
    {
        var t = (ID3D11Target)target;
        Ctx->ClearDepthStencilView(t.DsvHandle, (uint)ClearFlag.Depth, 1f, 0);
    }

    public void SetRasterizerState(IRasterizerState state) =>
        Ctx->RSSetState(((D3D11RasterizerState)state).State.Handle);

    public void SetDepthStencilState(IDepthStencilState state) =>
        Ctx->OMSetDepthStencilState(((D3D11DepthStencilState)state).State.Handle, 0);

    public void SetBlendState(IBlendState state)
    {
        float* factor = stackalloc float[4] { 1f, 1f, 1f, 1f };
        Ctx->OMSetBlendState(((D3D11BlendState)state).State.Handle, factor, 0xFFFFFFFF);
    }

    public void SetProgram(IShaderProgram program, bool pick)
    {
        var p = (D3D11ShaderProgram)program;
        Ctx->IASetInputLayout(p.Layout.Handle);
        Ctx->VSSetShader(p.Vertex.Handle, (ID3D11ClassInstance**)null, 0);
        ID3D11PixelShader* ps = pick ? p.Pick.Handle : p.Pixel.Handle;
        Ctx->PSSetShader(ps, (ID3D11ClassInstance**)null, 0);
    }

    public void SetSampler(int slot, IGpuSampler sampler)
    {
        var s = (D3D11Sampler)sampler;
        Ctx->PSSetSamplers((uint)slot, 1, s.Sampler.GetAddressOf());
    }

    public void SetConstantBuffer(int slot, IGpuBuffer buffer)
    {
        var b = (D3D11Buffer)buffer;
        Ctx->VSSetConstantBuffers((uint)slot, 1, b.Buffer.GetAddressOf());
        Ctx->PSSetConstantBuffers((uint)slot, 1, b.Buffer.GetAddressOf());
    }

    public void UpdateConstantBuffer<T>(IGpuBuffer buffer, in T value)
        where T : unmanaged
    {
        var b = (D3D11Buffer)buffer;
        MappedSubresource mapped = default;
        SilkMarshal.ThrowHResult(_ctx.Map(
            (ID3D11Resource*)b.Handle, 0, Map.WriteDiscard, 0, ref mapped));
        fixed (T* p = &value)
        {
            System.Buffer.MemoryCopy(p, mapped.PData, sizeof(T), sizeof(T));
        }

        _ctx.Unmap((ID3D11Resource*)b.Handle, 0);
    }

    public void SetTexture(int slot, IGpuTexture texture)
    {
        var t = (D3D11Texture)texture;
        Ctx->PSSetShaderResources((uint)slot, 1, t.Srv.GetAddressOf());
    }

    public void SetPrimitiveTopology(PrimitiveTopology topology) =>
        Ctx->IASetPrimitiveTopology(topology == PrimitiveTopology.LineList
            ? D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist
            : D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

    public void SetVertexBuffer(IGpuBuffer buffer, int stride)
    {
        var b = (D3D11Buffer)buffer;
        uint s = (uint)stride;
        uint offset = 0;
        Ctx->IASetVertexBuffers(0, 1, b.Buffer.GetAddressOf(), &s, &offset);
    }

    public void SetIndexBuffer(IGpuBuffer buffer)
    {
        var b = (D3D11Buffer)buffer;
        Ctx->IASetIndexBuffer(b.Handle, Format.FormatR32Uint, 0);
    }

    public void DrawIndexed(int indexCount) => Ctx->DrawIndexed((uint)indexCount, 0, 0);

    public void Draw(int vertexCount) => Ctx->Draw((uint)vertexCount, 0);
}
