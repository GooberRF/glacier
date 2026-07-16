using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// A GL render target the context can bind and clear. Exposes the framebuffer
/// object plus whether its color attachment is an integer (pick) format, which
/// the context needs because integer targets clear with <c>glClearBufferuiv</c>
/// rather than <c>glClearColor</c>.
/// </summary>
internal interface IGlTarget : IRenderTarget
{
    /// <summary>The framebuffer object to bind (0 = the context's default/window framebuffer).</summary>
    uint Framebuffer { get; }

    /// <summary>True when the color attachment is R32_UINT (the pick id-buffer).</summary>
    bool IntegerColor { get; }
}

/// <summary>
/// An offscreen color+depth FBO (RGBA8 + depth32f) whose color buffer can be read
/// back to the CPU. Readback flips rows so the returned buffer is top-left origin,
/// matching the D3D11 staging-copy path byte-for-byte.
/// </summary>
internal sealed unsafe class GlReadbackTarget : IReadbackTarget, IGlTarget
{
    private readonly GL _gl;
    private readonly IGlContext _context;
    private uint _fbo;
    private uint _color;
    private uint _depth;

    public GlReadbackTarget(IGlContext context, int width, int height)
    {
        _gl = context.Gl;
        _context = context;
        Width = width;
        Height = height;
        Create();
    }

    public int Width { get; }

    public int Height { get; }

    public uint Framebuffer => _fbo;

    public bool IntegerColor => false;

    public byte[] ReadPixels()
    {
        _context.MakeCurrent();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        int stride = Width * 4;
        var raw = new byte[stride * Height];
        fixed (byte* p = raw)
        {
            _gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        // GL readback is bottom-left origin; flip rows so row 0 is the top of the
        // image (D3D11's top-left-origin convention). Byte layout is then identical.
        var result = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
        {
            Array.Copy(raw, (Height - 1 - y) * stride, result, y * stride, stride);
        }

        return result;
    }

    private void Create()
    {
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _color = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _color);
        _gl.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)Width, (uint)Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _color, 0);

        _depth = GlFramebufferHelpers.CreateDepthBuffer(_gl, Width, Height);
        GlFramebufferHelpers.Validate(_gl, "readback");
    }

    public void Dispose()
    {
        if (_depth != 0)
        {
            _gl.DeleteRenderbuffer(_depth);
            _depth = 0;
        }

        if (_color != 0)
        {
            _gl.DeleteTexture(_color);
            _color = 0;
        }

        if (_fbo != 0)
        {
            _gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }
    }
}

/// <summary>
/// A GPU id-buffer: an R32_UINT color attachment plus depth. The scene is
/// re-rendered with the pick fragment shaders, then a single pixel is read back.
/// The Y coordinate is flipped on read because GL is bottom-left origin while the
/// caller passes a top-left-origin pixel (matching the D3D11 pick path).
/// </summary>
internal sealed unsafe class GlPickTarget : IPickTarget, IGlTarget
{
    private readonly GL _gl;
    private readonly IGlContext _context;
    private uint _fbo;
    private uint _color;
    private uint _depth;

    public GlPickTarget(IGlContext context, int width, int height)
    {
        _gl = context.Gl;
        _context = context;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Create();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public uint Framebuffer => _fbo;

    public bool IntegerColor => true;

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        DisposeTargets();
        Width = width;
        Height = height;
        Create();
    }

    public uint ReadPick(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }

        _context.MakeCurrent();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        uint value = 0;
        _gl.ReadPixels(x, Height - 1 - y, 1, 1, PixelFormat.RedInteger, PixelType.UnsignedInt, &value);
        return value;
    }

    private void Create()
    {
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _color = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _color);
        _gl.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.R32ui, (uint)Width, (uint)Height, 0,
            PixelFormat.RedInteger, PixelType.UnsignedInt, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _color, 0);

        _depth = GlFramebufferHelpers.CreateDepthBuffer(_gl, Width, Height);
        GlFramebufferHelpers.Validate(_gl, "pick");
    }

    private void DisposeTargets()
    {
        if (_depth != 0)
        {
            _gl.DeleteRenderbuffer(_depth);
            _depth = 0;
        }

        if (_color != 0)
        {
            _gl.DeleteTexture(_color);
            _color = 0;
        }

        if (_fbo != 0)
        {
            _gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }
    }

    public void Dispose() => DisposeTargets();
}

/// <summary>
/// The onscreen target: renders into the context's default (window-system)
/// framebuffer and presents by swapping buffers. For L2's headless context the
/// default framebuffer is the hidden 1x1 window, so this type exists mainly to
/// keep the RHI complete and to give L3 the exact seam it hosts: an
/// <c>OpenGlControlBase</c>-backed context supplies its own default framebuffer
/// (often a nonzero FBO Avalonia binds) and a real present.
/// </summary>
internal sealed class GlSwapChainTarget : ISwapChainTarget, IGlTarget
{
    private readonly IGlContext _context;

    public GlSwapChainTarget(IGlContext context, int width, int height)
    {
        _context = context;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public uint Framebuffer => _context.DefaultFramebuffer;

    public bool IntegerColor => false;

    public void Present(bool vsync) => _context.SwapBuffers();

    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public void Dispose()
    {
    }
}

/// <summary>Shared framebuffer-attachment helpers for the GL targets.</summary>
internal static class GlFramebufferHelpers
{
    /// <summary>Creates a depth renderbuffer (depth32f, matching the D3D11 D32_FLOAT depth) and attaches it.</summary>
    public static uint CreateDepthBuffer(GL gl, int width, int height)
    {
        uint rbo = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent32f, (uint)width, (uint)height);
        gl.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, rbo);
        return rbo;
    }

    /// <summary>Throws when the currently bound framebuffer is incomplete.</summary>
    public static void Validate(GL gl, string name)
    {
        GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"GL {name} framebuffer incomplete: {status}");
        }
    }
}
