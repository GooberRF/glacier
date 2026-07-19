using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// The OpenGL 3.3-core <see cref="IRenderDevice"/>: renders through an
/// <see cref="IGlContext"/> (L2 supplies the headless <see cref="WglOffscreenContext"/>;
/// L3 will supply an Avalonia-hosted one) and mirrors the D3D11 backend's pipeline
/// state so the two are pixel-faithful. It compiles the GLSL-330 side of each
/// <c>RhiShaderSource</c> and lays out UBOs/VAOs to match the HLSL cbuffers and
/// input layouts byte-for-byte.
/// </summary>
/// <remarks>
/// L2 (OpenGL) PARITY INVARIANTS mirrored from the D3D11 backend:
/// <list type="bullet">
/// <item>NO sRGB: color textures/attachments are GL_RGBA8 (never GL_SRGB8_ALPHA8)
///   and GL_FRAMEBUFFER_SRGB is left disabled (GL 3.3 core default).</item>
/// <item>Depth [0,1]: <c>glClipControl(GL_LOWER_LEFT, GL_ZERO_TO_ONE)</c> so the D3D
///   [0,1]-depth projection is reused unchanged, plus <c>glDepthRange(0,1)</c>. When
///   ARB_clip_control is absent (or <see cref="ForceProjectionDepthFixup"/> is set for
///   the fallback test) the vertex shader instead maps z:[0,w] to [-w,w] via the
///   GED_EMIT_CLIP macro, giving the GL-default [-1,1] NDC the same visible result.</item>
/// <item>Winding/cull: <c>glFrontFace(GL_CW)</c> + <c>glCullFace(GL_BACK)</c>; the
///   solid-cull state enables GL_CULL_FACE, solid/wireframe disable it, wireframe uses
///   <c>glPolygonMode(GL_LINE)</c>.</item>
/// <item>Depth states: Default = LESS/write on; NoWrite = LEQUAL/write off; NoTest =
///   ALWAYS/write off. Blend: Opaque = off; Alpha = straight source-over
///   (glBlendFuncSeparate(SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ONE, ONE_MINUS_SRC_ALPHA)).</item>
/// <item>UBOs b0=FrameConstants(160B)/b1=DrawConstants(96B) map to std140 blocks bound
///   to binding points 0/1 (set via glUniformBlockBinding since GLSL 330 has no
///   layout(binding=)). Matrices are uploaded untransposed: a row-major
///   System.Numerics matrix read column-major from a std140 mat4 yields the same
///   transpose HLSL's cbuffer read does, so HLSL <c>mul(M,v)</c> becomes GLSL
///   <c>M*v</c> with identical results (guarded by MeshTransformRegressionTests).</item>
/// <item>Textures: uploaded top-to-bottom (same bytes as D3D). Texture sampling is
///   origin-agnostic, so only the framebuffer READBACK is row-flipped (GL is
///   bottom-left) to keep PNG/pick output top-left origin.</item>
/// <item>Pick: R32_UINT attachment, glReadPixels(GL_RED_INTEGER, GL_UNSIGNED_INT),
///   0 = miss; the pick fragment discards on low billboard coverage.</item>
/// <item>Vertex attribute location = the layout element's ordinal; UInt32 attributes
///   use glVertexAttribIPointer (integer, not normalized).</item>
/// </list>
/// L3 HOSTING: construct <see cref="GlRenderDevice"/> with an <see cref="IGlContext"/>
/// that wraps the OpenGlControlBase context. The host must: make the context current
/// before issuing a frame; report its default framebuffer (Avalonia often binds a
/// nonzero FBO — return it from <see cref="IGlContext.DefaultFramebuffer"/>); implement
/// Resize on the swapchain target; and drive present/vsync through
/// <see cref="IGlContext.SwapBuffers"/>. Context sharing is not required — one device
/// per context is fine.
/// </remarks>
internal sealed class GlRenderDevice : IRenderDevice
{
    private const uint GlInvalidIndex = 0xFFFFFFFF;

    /// <summary>
    /// Test hook: forces the projection-based depth fixup path even when
    /// ARB_clip_control is available, so the no-clip-control fallback is exercised
    /// on hardware that has the extension. Off in production.
    /// </summary>
    internal static bool ForceProjectionDepthFixup;

    private readonly IGlContext _context;
    private readonly bool _ownsContext;
    private readonly GL _gl;
    private readonly GlRenderContext _rhiContext;
    private readonly bool _useDepthFixup;

    private readonly GlRasterizerState _rsSolid = new(cull: false, wireframe: false);
    private readonly GlRasterizerState _rsSolidCull = new(cull: true, wireframe: false);
    private readonly GlRasterizerState _rsWireframe = new(cull: false, wireframe: true);
    private readonly GlDepthStencilState _dsDefault = new(DepthFunction.Less, write: true);
    private readonly GlDepthStencilState _dsNoWrite = new(DepthFunction.Lequal, write: false);
    private readonly GlDepthStencilState _dsNoTest = new(DepthFunction.Always, write: false);
    private readonly GlBlendState _blendOpaque = new(GlBlendMode.Off);
    private readonly GlBlendState _blendAlpha = new(GlBlendMode.Alpha);
    private readonly GlBlendState _blendAdditive = new(GlBlendMode.Additive);
    private readonly GlSampler _sampler;

    public GlRenderDevice(IGlContext context, bool ownsContext)
    {
        _context = context;
        _ownsContext = ownsContext;
        _gl = context.Gl;
        _context.MakeCurrent();

        _useDepthFixup = ForceProjectionDepthFixup || !context.ClipControlSupported;
        IsSoftware = DetectSoftware(_gl);

        ConfigureGlobalState();
        _rhiContext = new GlRenderContext(_gl);
        _sampler = CreateSampler();
    }

    public bool IsSoftware { get; }

    public IRenderContext Context => _rhiContext;

    public IRasterizerState RasterizerSolid => _rsSolid;

    public IRasterizerState RasterizerSolidCull => _rsSolidCull;

    public IRasterizerState RasterizerWireframe => _rsWireframe;

    public IDepthStencilState DepthDefault => _dsDefault;

    public IDepthStencilState DepthNoWrite => _dsNoWrite;

    public IDepthStencilState DepthNoTest => _dsNoTest;

    public IBlendState BlendOpaque => _blendOpaque;

    public IBlendState BlendAlpha => _blendAlpha;

    public IBlendState BlendAdditive => _blendAdditive;

    public IGpuSampler LinearWrapSampler => _sampler;

    private void ConfigureGlobalState()
    {
        // RF is left-handed with clockwise front faces; cull the back (CCW) faces.
        _gl.FrontFace(FrontFaceDirection.CW);
        _gl.CullFace(TriangleFace.Back);
        _gl.DepthRange(0.0, 1.0);
        _gl.Disable(EnableCap.Dither);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.FramebufferSrgb);
        _gl.ColorMask(true, true, true, true);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        if (!_useDepthFixup)
        {
            // Match D3D's [0,1] clip-space depth so one projection matrix serves both.
            _gl.ClipControl(GLEnum.LowerLeft, GLEnum.ZeroToOne);
        }
    }

    private static bool DetectSoftware(GL gl)
    {
        string renderer = (gl.GetStringS(StringName.Renderer) ?? string.Empty).ToLowerInvariant();
        return renderer.Contains("llvmpipe")
            || renderer.Contains("softpipe")
            || renderer.Contains("swrast")
            || renderer.Contains("gdi generic")
            || renderer.Contains("basic render");
    }

    private GlSampler CreateSampler()
    {
        uint s = _gl.GenSampler();
        _gl.SamplerParameter(s, SamplerParameterI.MinFilter, (int)GLEnum.Linear);
        _gl.SamplerParameter(s, SamplerParameterI.MagFilter, (int)GLEnum.Linear);
        _gl.SamplerParameter(s, SamplerParameterI.WrapS, (int)GLEnum.Repeat);
        _gl.SamplerParameter(s, SamplerParameterI.WrapT, (int)GLEnum.Repeat);
        _gl.SamplerParameter(s, SamplerParameterI.WrapR, (int)GLEnum.Repeat);
        return new GlSampler(_gl, s);
    }

    // ---- Resource creation ----

    public unsafe IGpuBuffer CreateVertexBuffer<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        uint handle = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, handle);
        int bytes = data.Length * sizeof(T);
        fixed (T* p = data)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)bytes, p, BufferUsageARB.StaticDraw);
        }

        return new GlBuffer(_gl, handle, bytes);
    }

    public unsafe IGpuBuffer CreateIndexBuffer(ReadOnlySpan<uint> data)
    {
        uint handle = _gl.GenBuffer();
        // Upload via ARRAY_BUFFER so the shared VAO's element binding is not disturbed;
        // the buffer is bound as ELEMENT_ARRAY_BUFFER later at draw time.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, handle);
        int bytes = data.Length * sizeof(uint);
        fixed (uint* p = data)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)bytes, p, BufferUsageARB.StaticDraw);
        }

        return new GlBuffer(_gl, handle, bytes);
    }

    public unsafe IGpuBuffer CreateConstantBuffer(int byteWidth)
    {
        uint handle = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, handle);
        _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)byteWidth, null, BufferUsageARB.DynamicDraw);
        return new GlBuffer(_gl, handle, byteWidth);
    }

    public unsafe IGpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        uint handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
        {
            // Same top-to-bottom bytes as the D3D11 upload; sampling is origin-agnostic.
            _gl.TexImage2D(
                TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        return new GlTexture(_gl, handle);
    }

    public IShaderProgram CreateShaderProgram(ShaderProgramDesc desc)
    {
        RhiShaderSource src = desc.Source;
        if (src.GlslVertex is null || src.GlslFragment is null)
        {
            throw new InvalidOperationException($"GLSL source missing for program '{desc.Name}'.");
        }

        string prelude = _useDepthFixup
            ? "#define GED_EMIT_CLIP(c) do { gl_Position = (c); gl_Position.z = 2.0 * gl_Position.z - gl_Position.w; } while (false)\n"
            : "#define GED_EMIT_CLIP(c) gl_Position = (c)\n";

        uint vs = CompileShader(desc.Name, ShaderType.VertexShader, Inject(src.GlslVertex, prelude));
        uint shade = LinkProgram(desc.Name, vs, CompileShader(desc.Name, ShaderType.FragmentShader, src.GlslFragment));

        uint pick = 0;
        if (src.HasPick)
        {
            if (src.GlslPickFragment is null)
            {
                throw new InvalidOperationException($"GLSL pick fragment missing for program '{desc.Name}'.");
            }

            uint vsPick = CompileShader(desc.Name, ShaderType.VertexShader, Inject(src.GlslVertex, prelude));
            pick = LinkProgram(desc.Name, vsPick, CompileShader(desc.Name, ShaderType.FragmentShader, src.GlslPickFragment));
        }

        return new GlShaderProgram(_gl, shade, pick, desc.VertexLayout);
    }

    private static string Inject(string source, string prelude)
    {
        // Insert the prelude immediately after the mandatory "#version" line.
        int nl = source.IndexOf('\n');
        return nl < 0 ? source : source[..(nl + 1)] + prelude + source[(nl + 1)..];
    }

    private uint CompileShader(string name, ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"GLSL {type} compile failed ({name}): {log}");
        }

        return shader;
    }

    private uint LinkProgram(string name, uint vs, uint fs)
    {
        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);
        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(prog);
            _gl.DeleteProgram(prog);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
            throw new InvalidOperationException($"GLSL link failed ({name}): {log}");
        }

        // Shaders are no longer needed once linked.
        _gl.DetachShader(prog, vs);
        _gl.DetachShader(prog, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        BindProgramInterfaces(prog);
        return prog;
    }

    /// <summary>Binds the two UBO blocks to points 0/1 and the two texture samplers to units 0/1.</summary>
    private void BindProgramInterfaces(uint prog)
    {
        uint frame = _gl.GetUniformBlockIndex(prog, "FrameConstants");
        if (frame != GlInvalidIndex)
        {
            _gl.UniformBlockBinding(prog, frame, 0);
        }

        uint draw = _gl.GetUniformBlockIndex(prog, "DrawConstants");
        if (draw != GlInvalidIndex)
        {
            _gl.UniformBlockBinding(prog, draw, 1);
        }

        _gl.UseProgram(prog);
        int tex0 = _gl.GetUniformLocation(prog, "Tex0");
        if (tex0 >= 0)
        {
            _gl.Uniform1(tex0, 0);
        }

        int tex1 = _gl.GetUniformLocation(prog, "Tex1");
        if (tex1 >= 0)
        {
            _gl.Uniform1(tex1, 1);
        }

        _gl.UseProgram(0);
    }

    /// <summary>
    /// Diagnostics for the UBO layout-parity test: the std140 byte offset the driver
    /// assigns <paramref name="member"/> within a linked program's block, or false when
    /// the member is inactive in the selected stage. Proves the GL driver lays the
    /// std140 blocks out at the same offsets the D3D11 cbuffers (and the uploaded
    /// System.Numerics structs) use.
    /// </summary>
    internal unsafe bool TryGetUniformOffset(IShaderProgram program, bool pick, string member, out int offset)
    {
        offset = -1;
        uint prog = ((GlShaderProgram)program).Program(pick);
        uint index = GlInvalidIndex;
        var names = new[] { member };
        _gl.GetUniformIndices(prog, 1, names, &index);
        if (index == GlInvalidIndex)
        {
            return false;
        }

        int value = 0;
        _gl.GetActiveUniforms(prog, 1, &index, UniformPName.Offset, &value);
        offset = value;
        return true;
    }

    // ---- Render targets ----

    public ISwapChainTarget CreateSwapChain(nint windowHandle, int width, int height) =>
        new GlSwapChainTarget(_context, width, height);

    public IReadbackTarget CreateReadbackTarget(int width, int height) =>
        new GlReadbackTarget(_context, width, height);

    public IPickTarget CreatePickTarget(int width, int height) =>
        new GlPickTarget(_context, width, height);

    public void Dispose()
    {
        _sampler.Dispose();
        _rhiContext.Dispose();
        if (_ownsContext)
        {
            _context.Dispose();
        }
    }
}
