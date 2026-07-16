using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using D3D11Api = Silk.NET.Direct3D11.D3D11;

namespace Ged.Rendering.Rhi.D3D11;

/// <summary>
/// The Direct3D 11 <see cref="IRenderDevice"/>: one <see cref="ID3D11Device"/> and
/// immediate context, a DXGI factory for per-viewport swapchains, runtime HLSL
/// compilation via D3DCompile, and the common render states. Created once and
/// shared by every render target. Falls back to the WARP software rasterizer if no
/// hardware feature-level-11 device is available.
/// </summary>
/// <remarks>
/// L2 (OpenGL 3.3) PARITY INVARIANTS — the GL backend must mirror these exactly to
/// match this backend pixel-for-pixel:
/// <list type="bullet">
/// <item>NO sRGB anywhere. Color surfaces and textures are R8G8B8A8_UNORM (never
///   _SRGB); shading is in native texture space. GL: use GL_RGBA8, never
///   GL_SRGB8_ALPHA8, and keep GL_FRAMEBUFFER_SRGB disabled.</item>
/// <item>Depth range is [0,1] (D3D NDC z, and the viewport is minDepth=0/maxDepth=1).
///   The camera projection produces [0,1] depth. GL: glClipControl(GL_LOWER_LEFT,
///   GL_ZERO_TO_ONE) so one matrix set works, and glDepthRange(0,1).</item>
/// <item>Winding: FrontCounterClockwise = 0 (clockwise = front) with CullMode.Back.
///   GL: glFrontFace(GL_CW), glCullFace(GL_BACK). RasterizerSolid = cull off,
///   RasterizerSolidCull = cull back, RasterizerWireframe = fill line + cull off.</item>
/// <item>Depth states: Default = test Less / write on; NoWrite = test LessEqual /
///   write off; NoTest = test Always / write off (on-top overlays).</item>
/// <item>Blend: Opaque = disabled; Alpha = straight-alpha source-over
///   (Src=SrcAlpha, Dst=InvSrcAlpha, add; alpha Src=One, Dst=InvSrcAlpha).</item>
/// <item>Sampler: MinMagMipLinear, wrap UVW.</item>
/// <item>Constant buffers: b0 = FrameConstants (160 bytes), b1 = DrawConstants
///   (96 bytes), both bound to VS and PS, updated via write-discard map. GL: std140
///   UBOs at binding points 0 and 1 with identical field offsets (see Vertices.cs /
///   Shaders.cs). Matrices are row-major System.Numerics uploaded untransposed and
///   read column-major by HLSL; GLSL is column-major by default, so upload the same
///   bytes and use mul(M, v) equivalently (or transpose once — verify against
///   MeshTransformRegressionTests / the camera tests).</item>
/// <item>Textures are top-left origin (SysMemPitch, rows top-to-bottom) and readback
///   is top-left origin. GL is bottom-left: flip rows on upload or V in-shader, and
///   flip glReadPixels output so the PNG/pick paths keep top-left origin.</item>
/// <item>Pick target is R32_UINT; readback copies 1 pixel and reads a uint (0 = miss,
///   decoded by PickId.Decode). GL: R32UI attachment, glReadPixels(GL_RED_INTEGER,
///   GL_UNSIGNED_INT). Pick pixel shaders discard on low coverage (billboards).</item>
/// <item>Vertex attribute location = the element's ordinal in the layout array
///   (see ShaderPrograms); GLSL 330 must declare inputs layout(location = N) in that
///   order. UInt32 attributes use glVertexAttribIPointer (integer, not normalized).</item>
/// </list>
/// </remarks>
internal sealed unsafe class D3D11RenderDevice : IRenderDevice
{
    private readonly D3D11Api _d3d;
    private readonly DXGI _dxgi;
    private readonly D3DCompiler _compiler;

    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<IDXGIFactory2> _factory;

    private readonly D3D11RenderContext _rhiContext;

    private readonly D3D11RasterizerState _rsSolid;
    private readonly D3D11RasterizerState _rsSolidCull;
    private readonly D3D11RasterizerState _rsWireframe;
    private readonly D3D11DepthStencilState _dsDefault;
    private readonly D3D11DepthStencilState _dsNoWrite;
    private readonly D3D11DepthStencilState _dsNoTest;
    private readonly D3D11BlendState _blendOpaque;
    private readonly D3D11BlendState _blendAlpha;
    private readonly D3D11Sampler _sampler;

    public D3D11RenderDevice()
    {
        // The only D3D11/DXGI GetApi overload in this Silk.NET build is the
        // [Obsolete] DXSwapchainProvider one; the recommended INativeWindow
        // overload does not exist here. Passing null simply loads the default
        // system library, which is exactly what we want for a desktop device.
#pragma warning disable CS0618
        _d3d = D3D11Api.GetApi();
        _dxgi = DXGI.GetApi();
#pragma warning restore CS0618
        _compiler = D3DCompiler.GetApi();

        IsSoftware = CreateDevice();
        CreateFactory();
        _rhiContext = new D3D11RenderContext(_context);

        (_rsSolid, _rsSolidCull, _rsWireframe) = CreateRasterizerStates();
        (_dsDefault, _dsNoWrite, _dsNoTest) = CreateDepthStates();
        (_blendOpaque, _blendAlpha) = CreateBlendStates();
        _sampler = CreateSampler();
    }

    public bool IsSoftware { get; }

    public IRenderContext Context => _rhiContext;

    // ---- Raw D3D handles for the target/context implementations ----

    internal ComPtr<ID3D11Device> Device => _device;

    internal ComPtr<ID3D11DeviceContext> DeviceContext => _context;

    internal ComPtr<IDXGIFactory2> Factory => _factory;

    // ---- Fixed pipeline states ----

    public IRasterizerState RasterizerSolid => _rsSolid;

    public IRasterizerState RasterizerSolidCull => _rsSolidCull;

    public IRasterizerState RasterizerWireframe => _rsWireframe;

    public IDepthStencilState DepthDefault => _dsDefault;

    public IDepthStencilState DepthNoWrite => _dsNoWrite;

    public IDepthStencilState DepthNoTest => _dsNoTest;

    public IBlendState BlendOpaque => _blendOpaque;

    public IBlendState BlendAlpha => _blendAlpha;

    public IGpuSampler LinearWrapSampler => _sampler;

    private bool CreateDevice()
    {
        ID3D11Device* dev = null;
        ID3D11DeviceContext* ctx = null;
        const uint flags = 0;

        int hr = _d3d.CreateDevice(
            (IDXGIAdapter*)null, D3DDriverType.Hardware, 0, flags,
            (D3DFeatureLevel*)null, 0, D3D11Api.SdkVersion, &dev, (D3DFeatureLevel*)null, &ctx);

        bool warp = false;
        if (hr < 0)
        {
            warp = true;
            hr = _d3d.CreateDevice(
                (IDXGIAdapter*)null, D3DDriverType.Warp, 0, flags,
                (D3DFeatureLevel*)null, 0, D3D11Api.SdkVersion, &dev, (D3DFeatureLevel*)null, &ctx);
        }

        SilkMarshal.ThrowHResult(hr);
        _device = new ComPtr<ID3D11Device>(dev);
        _context = new ComPtr<ID3D11DeviceContext>(ctx);
        return warp;
    }

    private void CreateFactory()
    {
        IDXGIFactory2* factory = null;
        Guid iid = IDXGIFactory2.Guid;
        SilkMarshal.ThrowHResult(_dxgi.CreateDXGIFactory2(0, ref iid, (void**)&factory));
        _factory = new ComPtr<IDXGIFactory2>(factory);
    }

    private (D3D11RasterizerState, D3D11RasterizerState, D3D11RasterizerState) CreateRasterizerStates()
    {
        var rs = new RasterizerDesc
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = 0,
            DepthClipEnable = 1,
        };
        ComPtr<ID3D11RasterizerState> solid = default;
        SilkMarshal.ThrowHResult(_device.CreateRasterizerState(in rs, ref solid));

        // Back-face culling for the solid world/mesh passes. RF is left-handed with
        // clockwise front faces (FrontCounterClockwise = 0), so CullMode.Back removes
        // the counter-clockwise (rear) faces — front faces survive.
        rs.CullMode = CullMode.Back;
        ComPtr<ID3D11RasterizerState> solidCull = default;
        SilkMarshal.ThrowHResult(_device.CreateRasterizerState(in rs, ref solidCull));

        rs.CullMode = CullMode.None;
        rs.FillMode = FillMode.Wireframe;
        ComPtr<ID3D11RasterizerState> wireframe = default;
        SilkMarshal.ThrowHResult(_device.CreateRasterizerState(in rs, ref wireframe));

        return (new D3D11RasterizerState(solid), new D3D11RasterizerState(solidCull), new D3D11RasterizerState(wireframe));
    }

    private (D3D11DepthStencilState, D3D11DepthStencilState, D3D11DepthStencilState) CreateDepthStates()
    {
        var ds = new DepthStencilDesc
        {
            DepthEnable = 1,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.Less,
            StencilEnable = 0,
        };
        ComPtr<ID3D11DepthStencilState> def = default;
        SilkMarshal.ThrowHResult(_device.CreateDepthStencilState(in ds, ref def));

        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunc.LessEqual;
        ComPtr<ID3D11DepthStencilState> noWrite = default;
        SilkMarshal.ThrowHResult(_device.CreateDepthStencilState(in ds, ref noWrite));

        // Depth test disabled (always passes), no write — the gizmo/manipulator draws ON TOP
        // of scene geometry so its handles stay visible and aimable even behind brushes (item 12).
        ds.DepthFunc = ComparisonFunc.Always;
        ComPtr<ID3D11DepthStencilState> noTest = default;
        SilkMarshal.ThrowHResult(_device.CreateDepthStencilState(in ds, ref noTest));

        return (new D3D11DepthStencilState(def), new D3D11DepthStencilState(noWrite), new D3D11DepthStencilState(noTest));
    }

    private (D3D11BlendState, D3D11BlendState) CreateBlendStates()
    {
        var opaque = new BlendDesc();
        opaque.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 0,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> blendOpaque = default;
        SilkMarshal.ThrowHResult(_device.CreateBlendState(in opaque, ref blendOpaque));

        var alpha = new BlendDesc();
        alpha.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.InvSrcAlpha,
            BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> blendAlpha = default;
        SilkMarshal.ThrowHResult(_device.CreateBlendState(in alpha, ref blendAlpha));

        return (new D3D11BlendState(blendOpaque), new D3D11BlendState(blendAlpha));
    }

    private D3D11Sampler CreateSampler()
    {
        var samp = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunc.Never,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };
        ComPtr<ID3D11SamplerState> sampler = default;
        SilkMarshal.ThrowHResult(_device.CreateSamplerState(in samp, ref sampler));
        return new D3D11Sampler(sampler);
    }

    // ---- Resource creation ----

    public IGpuBuffer CreateVertexBuffer<T>(ReadOnlySpan<T> data)
        where T : unmanaged => new D3D11Buffer(CreateImmutableBuffer(data, BindFlag.VertexBuffer));

    public IGpuBuffer CreateIndexBuffer(ReadOnlySpan<uint> data) =>
        new D3D11Buffer(CreateImmutableBuffer(data, BindFlag.IndexBuffer));

    public IGpuBuffer CreateConstantBuffer(int byteWidth)
    {
        var desc = new BufferDesc
        {
            ByteWidth = (uint)byteWidth,
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = 0,
            StructureByteStride = 0,
        };

        ComPtr<ID3D11Buffer> buffer = default;
        SilkMarshal.ThrowHResult(_device.CreateBuffer(in desc, (SubresourceData*)null, ref buffer));
        return new D3D11Buffer(buffer);
    }

    private ComPtr<ID3D11Buffer> CreateImmutableBuffer<T>(ReadOnlySpan<T> data, BindFlag bind)
        where T : unmanaged
    {
        var desc = new BufferDesc
        {
            ByteWidth = (uint)(data.Length * sizeof(T)),
            Usage = Usage.Immutable,
            BindFlags = (uint)bind,
            CPUAccessFlags = 0,
            MiscFlags = 0,
            StructureByteStride = 0,
        };

        ComPtr<ID3D11Buffer> buffer = default;
        fixed (T* p = data)
        {
            var srd = new SubresourceData { PSysMem = p };
            SilkMarshal.ThrowHResult(_device.CreateBuffer(in desc, in srd, ref buffer));
        }

        return buffer;
    }

    /// <summary>Creates an immutable RGBA8 texture (top-left origin) and its shader-resource view.</summary>
    public IGpuTexture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Immutable,
            BindFlags = (uint)BindFlag.ShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        ComPtr<ID3D11Texture2D> tex = default;
        fixed (byte* p = rgba)
        {
            var srd = new SubresourceData { PSysMem = p, SysMemPitch = (uint)(width * 4) };
            SilkMarshal.ThrowHResult(_device.CreateTexture2D(in desc, in srd, ref tex));
        }

        ComPtr<ID3D11ShaderResourceView> srv = default;
        SilkMarshal.ThrowHResult(_device.CreateShaderResourceView(
            (ID3D11Resource*)tex.Handle, (ShaderResourceViewDesc*)null, srv.GetAddressOf()));
        tex.Dispose();
        return new D3D11Texture(srv);
    }

    // ---- Shaders ----

    public IShaderProgram CreateShaderProgram(ShaderProgramDesc desc)
    {
        RhiShaderSource src = desc.Source;
        ComPtr<ID3D10Blob> vsBlob = CompileShader(desc.Name, src.Hlsl, "VSMain", "vs_5_0");
        ComPtr<ID3D10Blob> psBlob = CompileShader(desc.Name, src.Hlsl, "PSMain", "ps_5_0");

        var prog = new D3D11ShaderProgram
        {
            Vertex = CreateVertexShader(vsBlob),
            Pixel = CreatePixelShader(psBlob),
            Layout = CreateInputLayout(desc.VertexLayout, vsBlob),
            HasPick = src.HasPick,
        };

        psBlob.Dispose();

        if (src.HasPick)
        {
            ComPtr<ID3D10Blob> pickBlob = CompileShader(desc.Name, src.Hlsl, "PSPick", "ps_5_0");
            prog.Pick = CreatePixelShader(pickBlob);
            pickBlob.Dispose();
        }

        vsBlob.Dispose();
        return prog;
    }

    private ComPtr<ID3D10Blob> CompileShader(string name, string source, string entry, string target)
    {
        byte[] srcBytes = Encoding.ASCII.GetBytes(source);
        byte[] entryZ = Encoding.ASCII.GetBytes(entry + "\0");
        byte[] targetZ = Encoding.ASCII.GetBytes(target + "\0");
        ComPtr<ID3D10Blob> code = default;
        ComPtr<ID3D10Blob> errors = default;

        fixed (byte* pSrc = srcBytes)
        fixed (byte* pEntry = entryZ)
        fixed (byte* pTarget = targetZ)
        {
            int hr = _compiler.Compile(
                pSrc, (nuint)srcBytes.Length, (byte*)null, null, (ID3DInclude*)null,
                pEntry, pTarget, 0, 0, code.GetAddressOf(), errors.GetAddressOf());

            if (hr < 0)
            {
                string message = "unknown error";
                if (errors.Handle is not null)
                {
                    message = SilkMarshal.PtrToString((nint)errors.GetBufferPointer()) ?? message;
                    errors.Dispose();
                }

                throw new InvalidOperationException($"HLSL compile failed ({name} {entry}/{target}): {message}");
            }
        }

        if (errors.Handle is not null)
        {
            errors.Dispose();
        }

        return code;
    }

    private ComPtr<ID3D11VertexShader> CreateVertexShader(ComPtr<ID3D10Blob> blob)
    {
        ComPtr<ID3D11VertexShader> vs = default;
        SilkMarshal.ThrowHResult(_device.CreateVertexShader(
            blob.GetBufferPointer(), blob.GetBufferSize(), (ID3D11ClassLinkage*)null, vs.GetAddressOf()));
        return vs;
    }

    private ComPtr<ID3D11PixelShader> CreatePixelShader(ComPtr<ID3D10Blob> blob)
    {
        ComPtr<ID3D11PixelShader> ps = default;
        SilkMarshal.ThrowHResult(_device.CreatePixelShader(
            blob.GetBufferPointer(), blob.GetBufferSize(), (ID3D11ClassLinkage*)null, ps.GetAddressOf()));
        return ps;
    }

    private ComPtr<ID3D11InputLayout> CreateInputLayout(
        IReadOnlyList<VertexAttribute> elements,
        ComPtr<ID3D10Blob> vsBlob)
    {
        var names = new nint[elements.Count];
        var descs = new InputElementDesc[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            names[i] = SilkMarshal.StringToPtr(elements[i].Semantic);
            descs[i] = new InputElementDesc
            {
                SemanticName = (byte*)names[i],
                SemanticIndex = elements[i].SemanticIndex,
                Format = ToDxgiFormat(elements[i].Format),
                InputSlot = 0,
                AlignedByteOffset = elements[i].Offset,
                InputSlotClass = InputClassification.PerVertexData,
                InstanceDataStepRate = 0,
            };
        }

        ComPtr<ID3D11InputLayout> layout = default;
        fixed (InputElementDesc* pDescs = descs)
        {
            SilkMarshal.ThrowHResult(_device.CreateInputLayout(
                pDescs, (uint)descs.Length, vsBlob.GetBufferPointer(), vsBlob.GetBufferSize(), ref layout));
        }

        foreach (nint n in names)
        {
            SilkMarshal.Free(n);
        }

        return layout;
    }

    private static Format ToDxgiFormat(VertexAttributeFormat format) => format switch
    {
        VertexAttributeFormat.Float2 => Format.FormatR32G32Float,
        VertexAttributeFormat.Float3 => Format.FormatR32G32B32Float,
        VertexAttributeFormat.Float4 => Format.FormatR32G32B32A32Float,
        VertexAttributeFormat.UNorm8x4 => Format.FormatR8G8B8A8Unorm,
        VertexAttributeFormat.UInt32 => Format.FormatR32Uint,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    // ---- Render targets ----

    public ISwapChainTarget CreateSwapChain(nint windowHandle, int width, int height) =>
        new D3D11SwapChainTarget(this, windowHandle, width, height);

    public IReadbackTarget CreateReadbackTarget(int width, int height) =>
        new D3D11ReadbackTarget(this, width, height);

    public IPickTarget CreatePickTarget(int width, int height) =>
        new D3D11PickTarget(this, width, height);

    public void Dispose()
    {
        _sampler.Dispose();
        _blendAlpha.Dispose();
        _blendOpaque.Dispose();
        _dsNoTest.Dispose();
        _dsNoWrite.Dispose();
        _dsDefault.Dispose();
        _rsWireframe.Dispose();
        _rsSolidCull.Dispose();
        _rsSolid.Dispose();
        _factory.Dispose();
        _context.Dispose();
        _device.Dispose();
        _compiler.Dispose();
        _dxgi.Dispose();
        _d3d.Dispose();
    }
}
