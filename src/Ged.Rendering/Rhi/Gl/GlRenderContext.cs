using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// The OpenGL immediate context: forwards every RHI command to the GL state
/// machine, one call to one state/draw operation, mirroring
/// <c>D3D11RenderContext</c> exactly so both backends produce identical frames.
/// A single shared VAO is kept bound; vertex attributes are (re)configured from
/// the bound program's layout on <see cref="SetVertexBuffer"/> so the attribute
/// location equals the layout element's ordinal.
/// </summary>
internal sealed unsafe class GlRenderContext : IRenderContext
{
    private readonly GL _gl;
    private readonly uint _vao;

    private IReadOnlyList<VertexAttribute>? _layout;
    private int _enabledAttribs;
    private PrimitiveType _topology = PrimitiveType.Triangles;

    public GlRenderContext(GL gl)
    {
        _gl = gl;
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
    }

    public void SetRenderTarget(IRenderTarget target)
    {
        var t = (IGlTarget)target;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Framebuffer);
        _gl.Viewport(0, 0, (uint)t.Width, (uint)t.Height);
    }

    public void ClearColor(IRenderTarget target, Vector4 color)
    {
        var t = (IGlTarget)target;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Framebuffer);
        if (t.IntegerColor)
        {
            // R32_UINT id-buffer: clear with the integer path (glClearColor is float-only).
            uint* v = stackalloc uint[4] { (uint)color.X, (uint)color.Y, (uint)color.Z, (uint)color.W };
            _gl.ClearBuffer(GLEnum.Color, 0, v);
        }
        else
        {
            _gl.ColorMask(true, true, true, true);
            _gl.ClearColor(color.X, color.Y, color.Z, color.W);
            _gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        }
    }

    public void ClearDepth(IRenderTarget target)
    {
        var t = (IGlTarget)target;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Framebuffer);
        // Depth writes must be enabled for the clear to take effect; the depth
        // state set later re-establishes the per-pass write mask.
        _gl.DepthMask(true);
        _gl.ClearDepth(1.0);
        _gl.Clear((uint)ClearBufferMask.DepthBufferBit);
    }

    public void SetRasterizerState(IRasterizerState state)
    {
        var s = (GlRasterizerState)state;
        _gl.PolygonMode(GLEnum.FrontAndBack, s.Wireframe ? GLEnum.Line : GLEnum.Fill);
        if (s.Cull)
        {
            _gl.Enable(EnableCap.CullFace);
        }
        else
        {
            _gl.Disable(EnableCap.CullFace);
        }
    }

    public void SetDepthStencilState(IDepthStencilState state)
    {
        var s = (GlDepthStencilState)state;
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(s.Func);
        _gl.DepthMask(s.Write);
    }

    public void SetBlendState(IBlendState state)
    {
        var s = (GlBlendState)state;
        switch (s.Mode)
        {
            case GlBlendMode.Alpha:
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendFuncSeparate(
                    BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
                    BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                break;
            case GlBlendMode.Additive:
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendFuncSeparate(
                    BlendingFactor.One, BlendingFactor.One,
                    BlendingFactor.One, BlendingFactor.One);
                break;
            default:
                _gl.Disable(EnableCap.Blend);
                break;
        }
    }

    public void SetProgram(IShaderProgram program, bool pick)
    {
        var p = (GlShaderProgram)program;
        _gl.UseProgram(p.Program(pick));
        _layout = p.Layout;
    }

    public void SetSampler(int slot, IGpuSampler sampler) =>
        _gl.BindSampler((uint)slot, ((GlSampler)sampler).Handle);

    public void SetConstantBuffer(int slot, IGpuBuffer buffer) =>
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, (uint)slot, ((GlBuffer)buffer).Handle);

    public void UpdateConstantBuffer<T>(IGpuBuffer buffer, in T value)
        where T : unmanaged
    {
        var b = (GlBuffer)buffer;
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, b.Handle);
        fixed (T* p = &value)
        {
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)Unsafe.SizeOf<T>(), p);
        }
    }

    public void SetTexture(int slot, IGpuTexture texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + slot);
        _gl.BindTexture(TextureTarget.Texture2D, ((GlTexture)texture).Handle);
    }

    public void SetPrimitiveTopology(PrimitiveTopology topology) =>
        _topology = topology == PrimitiveTopology.LineList ? PrimitiveType.Lines : PrimitiveType.Triangles;

    public void SetVertexBuffer(IGpuBuffer buffer, int stride)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, ((GlBuffer)buffer).Handle);
        if (_layout is null)
        {
            return;
        }

        for (int i = 0; i < _layout.Count; i++)
        {
            VertexAttribute a = _layout[i];
            uint loc = (uint)i;
            _gl.EnableVertexAttribArray(loc);
            void* offset = (void*)a.Offset;
            switch (a.Format)
            {
                case VertexAttributeFormat.Float2:
                    _gl.VertexAttribPointer(loc, 2, VertexAttribPointerType.Float, false, (uint)stride, offset);
                    break;
                case VertexAttributeFormat.Float3:
                    _gl.VertexAttribPointer(loc, 3, VertexAttribPointerType.Float, false, (uint)stride, offset);
                    break;
                case VertexAttributeFormat.Float4:
                    _gl.VertexAttribPointer(loc, 4, VertexAttribPointerType.Float, false, (uint)stride, offset);
                    break;
                case VertexAttributeFormat.UNorm8x4:
                    _gl.VertexAttribPointer(loc, 4, VertexAttribPointerType.UnsignedByte, true, (uint)stride, offset);
                    break;
                case VertexAttributeFormat.UInt32:
                    _gl.VertexAttribIPointer(loc, 1, VertexAttribIType.UnsignedInt, (uint)stride, offset);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(buffer), a.Format, "unknown vertex attribute format");
            }
        }

        // Disable any attribute arrays left enabled by a previous program with more
        // attributes, so stale locations do not read from an unbound source.
        for (int i = _layout.Count; i < _enabledAttribs; i++)
        {
            _gl.DisableVertexAttribArray((uint)i);
        }

        _enabledAttribs = _layout.Count;
    }

    public void SetIndexBuffer(IGpuBuffer buffer) =>
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ((GlBuffer)buffer).Handle);

    public void DrawIndexed(int indexCount) =>
        _gl.DrawElements(_topology, (uint)indexCount, DrawElementsType.UnsignedInt, (void*)0);

    public void Draw(int vertexCount) => _gl.DrawArrays(_topology, 0, (uint)vertexCount);

    public void Dispose() => _gl.DeleteVertexArray(_vao);
}
