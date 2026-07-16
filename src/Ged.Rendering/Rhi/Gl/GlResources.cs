using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>An OpenGL buffer object (vertex, index, or uniform/constant buffer).</summary>
internal sealed class GlBuffer : IGpuBuffer
{
    private readonly GL _gl;
    private uint _handle;

    public GlBuffer(GL gl, uint handle, int byteWidth)
    {
        _gl = gl;
        _handle = handle;
        ByteWidth = byteWidth;
    }

    public uint Handle => _handle;

    /// <summary>Allocated size in bytes (constant buffers; used to guard sub-updates).</summary>
    public int ByteWidth { get; }

    public void Dispose()
    {
        if (_handle != 0)
        {
            _gl.DeleteBuffer(_handle);
            _handle = 0;
        }
    }
}

/// <summary>An OpenGL 2D texture (RGBA8, top-left origin — see the upload note in GlRenderDevice).</summary>
internal sealed class GlTexture : IGpuTexture
{
    private readonly GL _gl;
    private uint _handle;

    public GlTexture(GL gl, uint handle)
    {
        _gl = gl;
        _handle = handle;
    }

    public uint Handle => _handle;

    public void Dispose()
    {
        if (_handle != 0)
        {
            _gl.DeleteTexture(_handle);
            _handle = 0;
        }
    }
}

/// <summary>An OpenGL sampler object (linear filter, wrap addressing).</summary>
internal sealed class GlSampler : IGpuSampler
{
    private readonly GL _gl;
    private uint _handle;

    public GlSampler(GL gl, uint handle)
    {
        _gl = gl;
        _handle = handle;
    }

    public uint Handle => _handle;

    public void Dispose()
    {
        if (_handle != 0)
        {
            _gl.DeleteSampler(_handle);
            _handle = 0;
        }
    }
}

/// <summary>
/// A GL rasterizer state token. GL has no state objects, so this just carries the
/// two flags the context applies: back-face culling and wireframe fill.
/// </summary>
internal sealed class GlRasterizerState : IRasterizerState
{
    public GlRasterizerState(bool cull, bool wireframe)
    {
        Cull = cull;
        Wireframe = wireframe;
    }

    public bool Cull { get; }

    public bool Wireframe { get; }

    public void Dispose()
    {
    }
}

/// <summary>A GL depth-stencil state token (depth compare func + write mask; test always enabled).</summary>
internal sealed class GlDepthStencilState : IDepthStencilState
{
    public GlDepthStencilState(DepthFunction func, bool write)
    {
        Func = func;
        Write = write;
    }

    public DepthFunction Func { get; }

    public bool Write { get; }

    public void Dispose()
    {
    }
}

/// <summary>A GL blend state token (blending on/off; the alpha equation is the fixed source-over set).</summary>
internal sealed class GlBlendState : IBlendState
{
    public GlBlendState(bool enabled) => Enabled = enabled;

    public bool Enabled { get; }

    public void Dispose()
    {
    }
}

/// <summary>
/// A compiled GL program pair: the shading program (vertex + shading fragment)
/// and, when the source has a pick stage, the pick program (same vertex + the
/// id-buffer fragment). <see cref="IRenderContext.SetProgram"/> selects one.
/// The vertex layout travels with the program so the context can configure the
/// VAO attributes (attribute location = the element's ordinal) at bind time.
/// </summary>
internal sealed class GlShaderProgram : IShaderProgram
{
    private readonly GL _gl;
    private uint _shade;
    private uint _pick;

    public GlShaderProgram(GL gl, uint shade, uint pick, IReadOnlyList<VertexAttribute> layout)
    {
        _gl = gl;
        _shade = shade;
        _pick = pick;
        Layout = layout;
    }

    public bool HasPick => _pick != 0;

    public IReadOnlyList<VertexAttribute> Layout { get; }

    /// <summary>The GL program object to bind for the requested stage.</summary>
    public uint Program(bool pick) => pick && _pick != 0 ? _pick : _shade;

    public void Dispose()
    {
        if (_pick != 0)
        {
            _gl.DeleteProgram(_pick);
            _pick = 0;
        }

        if (_shade != 0)
        {
            _gl.DeleteProgram(_shade);
            _shade = 0;
        }
    }
}
