using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Ged.Rendering.Rhi.D3D11;

/// <summary>
/// A D3D11 render target the context can bind: exposes the raw color and depth
/// views. Every D3D11 target (swapchain, readback, pick) implements it so the
/// context's <c>SetRenderTarget</c>/clear operations can reach the views without
/// leaking D3D types across the RHI boundary.
/// </summary>
internal unsafe interface ID3D11Target : IRenderTarget
{
    ID3D11RenderTargetView* RtvHandle { get; }

    ID3D11DepthStencilView* DsvHandle { get; }
}

/// <summary>A depth buffer (D32_FLOAT) sized to a surface.</summary>
internal sealed unsafe class D3D11DepthBuffer : IDisposable
{
    private ComPtr<ID3D11Texture2D> _texture;

    public ComPtr<ID3D11DepthStencilView> View;

    public static D3D11DepthBuffer Create(D3D11RenderDevice gd, int width, int height)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatD32Float,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.DepthStencil,
        };

        var db = new D3D11DepthBuffer();
        SilkMarshal.ThrowHResult(gd.Device.CreateTexture2D(in desc, (SubresourceData*)null, db._texture.GetAddressOf()));
        SilkMarshal.ThrowHResult(gd.Device.CreateDepthStencilView(
            (ID3D11Resource*)db._texture.Handle, (DepthStencilViewDesc*)null, db.View.GetAddressOf()));
        return db;
    }

    public void Dispose()
    {
        View.Dispose();
        _texture.Dispose();
    }
}

/// <summary>
/// An offscreen color+depth target (R8G8B8A8) that supports CPU readback. Used by
/// tests, the PNG/thumbnail path, and offscreen rendering; drives the exact same
/// scene code as a live viewport.
/// </summary>
internal sealed unsafe class D3D11ReadbackTarget : IReadbackTarget, ID3D11Target
{
    private readonly D3D11RenderDevice _gd;
    private ComPtr<ID3D11Texture2D> _color;
    private ComPtr<ID3D11Texture2D> _staging;
    private ComPtr<ID3D11RenderTargetView> _rtv;
    private D3D11DepthBuffer _depth = null!;

    public D3D11ReadbackTarget(D3D11RenderDevice gd, int width, int height)
    {
        _gd = gd;
        Width = width;
        Height = height;
        Create();
    }

    public int Width { get; }

    public int Height { get; }

    public ID3D11RenderTargetView* RtvHandle => _rtv.Handle;

    public ID3D11DepthStencilView* DsvHandle => _depth.View.Handle;

    /// <summary>Copies the rendered color buffer back to CPU memory as tightly packed RGBA8.</summary>
    public byte[] ReadPixels()
    {
        _gd.DeviceContext.CopyResource((ID3D11Resource*)_staging.Handle, (ID3D11Resource*)_color.Handle);

        MappedSubresource mapped = default;
        SilkMarshal.ThrowHResult(_gd.DeviceContext.Map((ID3D11Resource*)_staging.Handle, 0, Map.Read, 0, ref mapped));
        var result = new byte[Width * Height * 4];
        fixed (byte* dst = result)
        {
            byte* src = (byte*)mapped.PData;
            for (int y = 0; y < Height; y++)
            {
                System.Buffer.MemoryCopy(src + (y * mapped.RowPitch), dst + (y * Width * 4), Width * 4, Width * 4);
            }
        }

        _gd.DeviceContext.Unmap((ID3D11Resource*)_staging.Handle, 0);
        return result;
    }

    private void Create()
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
        };
        SilkMarshal.ThrowHResult(_gd.Device.CreateTexture2D(in desc, (SubresourceData*)null, _color.GetAddressOf()));
        SilkMarshal.ThrowHResult(_gd.Device.CreateRenderTargetView(
            (ID3D11Resource*)_color.Handle, (RenderTargetViewDesc*)null, _rtv.GetAddressOf()));

        var staging = desc;
        staging.Usage = Usage.Staging;
        staging.BindFlags = 0;
        staging.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        SilkMarshal.ThrowHResult(_gd.Device.CreateTexture2D(in staging, (SubresourceData*)null, _staging.GetAddressOf()));

        _depth = D3D11DepthBuffer.Create(_gd, Width, Height);
    }

    public void Dispose()
    {
        _depth.Dispose();
        _rtv.Dispose();
        _staging.Dispose();
        _color.Dispose();
    }
}

/// <summary>A per-viewport DXGI swapchain bound to a native (Win32) window, plus its depth buffer.</summary>
internal sealed unsafe class D3D11SwapChainTarget : ISwapChainTarget, ID3D11Target
{
    private readonly D3D11RenderDevice _gd;
    private ComPtr<IDXGISwapChain1> _swapChain;
    private ComPtr<ID3D11RenderTargetView> _rtv;
    private D3D11DepthBuffer _depth = null!;

    public D3D11SwapChainTarget(D3D11RenderDevice gd, nint hwnd, int width, int height)
    {
        _gd = gd;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        CreateSwapChain(hwnd);
        CreateViews();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ID3D11RenderTargetView* RtvHandle => _rtv.Handle;

    public ID3D11DepthStencilView* DsvHandle => _depth.View.Handle;

    /// <summary>Presents the current back buffer. <paramref name="vsync"/> gates on vblank.</summary>
    public void Present(bool vsync)
    {
        _swapChain.Present(vsync ? 1u : 0u, 0);
    }

    /// <summary>Resizes the swapchain buffers and depth to a new client size (no-op if unchanged).</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        Width = width;
        Height = height;

        _rtv.Dispose();
        _depth.Dispose();
        SilkMarshal.ThrowHResult(_swapChain.ResizeBuffers(
            0, (uint)Width, (uint)Height, Format.FormatUnknown, 0));
        CreateViews();
    }

    private void CreateSwapChain(nint hwnd)
    {
        var desc = new SwapChainDesc1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.Discard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Unspecified,
        };

        SilkMarshal.ThrowHResult(_gd.Factory.CreateSwapChainForHwnd(
            (IUnknown*)_gd.Device.Handle, hwnd, in desc,
            (SwapChainFullscreenDesc*)null, (IDXGIOutput*)null, _swapChain.GetAddressOf()));
    }

    private void CreateViews()
    {
        ComPtr<ID3D11Texture2D> backBuffer = default;
        Guid iid = ID3D11Texture2D.Guid;
        SilkMarshal.ThrowHResult(_swapChain.GetBuffer(0, ref iid, (void**)backBuffer.GetAddressOf()));
        SilkMarshal.ThrowHResult(_gd.Device.CreateRenderTargetView(
            (ID3D11Resource*)backBuffer.Handle, (RenderTargetViewDesc*)null, _rtv.GetAddressOf()));
        backBuffer.Dispose();
        _depth = D3D11DepthBuffer.Create(_gd, Width, Height);
    }

    public void Dispose()
    {
        _depth?.Dispose();
        _rtv.Dispose();
        _swapChain.Dispose();
    }
}

/// <summary>
/// A GPU id-buffer: an R32_UINT color target plus its own depth buffer. The scene
/// is re-rendered with the pick pixel shaders, then a 1x1 region under the cursor
/// is copied to a staging texture and read back on the CPU.
/// </summary>
internal sealed unsafe class D3D11PickTarget : IPickTarget, ID3D11Target
{
    private readonly D3D11RenderDevice _gd;
    private ComPtr<ID3D11Texture2D> _color;
    private ComPtr<ID3D11Texture2D> _staging1x1;
    private ComPtr<ID3D11RenderTargetView> _rtv;
    private D3D11DepthBuffer _depth = null!;

    public D3D11PickTarget(D3D11RenderDevice gd, int width, int height)
    {
        _gd = gd;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Create();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ID3D11RenderTargetView* RtvHandle => _rtv.Handle;

    public ID3D11DepthStencilView* DsvHandle => _depth.View.Handle;

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

    /// <summary>Reads the raw encoded pick id at a pixel; out-of-range returns 0.</summary>
    public uint ReadPick(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }

        var box = new Box
        {
            Left = (uint)x,
            Top = (uint)y,
            Front = 0,
            Right = (uint)(x + 1),
            Bottom = (uint)(y + 1),
            Back = 1,
        };
        _gd.DeviceContext.CopySubresourceRegion(
            (ID3D11Resource*)_staging1x1.Handle, 0, 0, 0, 0,
            (ID3D11Resource*)_color.Handle, 0, in box);

        MappedSubresource mapped = default;
        SilkMarshal.ThrowHResult(_gd.DeviceContext.Map((ID3D11Resource*)_staging1x1.Handle, 0, Map.Read, 0, ref mapped));
        uint value = *(uint*)mapped.PData;
        _gd.DeviceContext.Unmap((ID3D11Resource*)_staging1x1.Handle, 0);
        return value;
    }

    private void Create()
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR32Uint,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.RenderTarget,
        };
        SilkMarshal.ThrowHResult(_gd.Device.CreateTexture2D(in desc, (SubresourceData*)null, _color.GetAddressOf()));
        SilkMarshal.ThrowHResult(_gd.Device.CreateRenderTargetView(
            (ID3D11Resource*)_color.Handle, (RenderTargetViewDesc*)null, _rtv.GetAddressOf()));

        var staging = desc;
        staging.Width = 1;
        staging.Height = 1;
        staging.Usage = Usage.Staging;
        staging.BindFlags = 0;
        staging.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        SilkMarshal.ThrowHResult(_gd.Device.CreateTexture2D(in staging, (SubresourceData*)null, _staging1x1.GetAddressOf()));

        _depth = D3D11DepthBuffer.Create(_gd, Width, Height);
    }

    private void DisposeTargets()
    {
        _depth.Dispose();
        _rtv.Dispose();
        _staging1x1.Dispose();
        _color.Dispose();
    }

    public void Dispose() => DisposeTargets();
}
