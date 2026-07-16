using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// A live OpenGL context the <see cref="GlRenderDevice"/> renders through. The
/// offscreen/test path has one implementation per platform, selected by
/// <see cref="OffscreenGlContext"/>: <see cref="WglOffscreenContext"/> (Windows,
/// hidden-window WGL), <see cref="EglOffscreenContext"/> (Linux/Unix, EGL
/// surfaceless/pbuffer — works headless on llvmpipe) and
/// <see cref="GlxOffscreenContext"/> (Linux/Unix GLX fallback). L3 adds a further
/// implementation that wraps the context Avalonia's <c>OpenGlControlBase</c> hands
/// the control (see the L3 notes in <see cref="GlRenderDevice"/>): the device code
/// never assumes it owns the context, so an onscreen host can supply its own.
/// </summary>
internal interface IGlContext : IDisposable
{
    /// <summary>The loaded GL 3.3-core function table (valid only while current).</summary>
    GL Gl { get; }

    /// <summary>True when <c>GL_ARB_clip_control</c> is available (see GlRenderDevice's depth notes).</summary>
    bool ClipControlSupported { get; }

    /// <summary>The GL major*10+minor version reported by the driver (e.g. 33 for 3.3).</summary>
    int VersionTens { get; }

    /// <summary>The default (window-system) framebuffer the host renders onscreen into (0 for a pbuffer/hidden window).</summary>
    uint DefaultFramebuffer { get; }

    /// <summary>Binds this context to the calling thread. Idempotent when already current.</summary>
    void MakeCurrent();

    /// <summary>Presents the default framebuffer (onscreen hosts only; a no-op offscreen).</summary>
    void SwapBuffers();
}

/// <summary>
/// A headless OpenGL 3.3-core context created via raw WGL over a hidden 1x1
/// window. Chosen as the LEAST-dependency offscreen route on Windows: it needs
/// only the system <c>opengl32.dll</c> (like D3D11 needs the system D3D runtime),
/// so no native NuGet payload is added and the single-file publish stays clean.
/// GL entry points are resolved with the standard two-tier scheme (base GL 1.1
/// from <c>opengl32.dll</c> exports, everything else from
/// <c>wglGetProcAddress</c>) and handed to Silk.NET's managed bindings.
/// <para>
/// <see cref="TryCreate"/> returns null when a 3.3-core context cannot be made
/// (no GPU, generic GDI-only OpenGL 1.1, remote session), so the tests skip
/// exactly like the D3D11 path does when hardware is absent.
/// </para>
/// </summary>
internal sealed unsafe class WglOffscreenContext : IGlContext
{
    // WGL context-attribute tokens (wglCreateContextAttribsARB).
    private const int WglContextMajorVersion = 0x2091;
    private const int WglContextMinorVersion = 0x2092;
    private const int WglContextFlags = 0x2094;
    private const int WglContextProfileMask = 0x9126;
    private const int WglContextCoreProfileBit = 0x0001;
    private const int WglContextForwardCompatibleBit = 0x0002;

    private static readonly object ClassLock = new();
    private static Native.WndProc? _wndProc;
    private static nint _classAtomName;
    private static bool _classRegistered;

    private nint _hwnd;
    private nint _dc;
    private nint _rc;
    private nint _openglModule;

    private WglOffscreenContext(nint hwnd, nint dc, nint rc)
    {
        _hwnd = hwnd;
        _dc = dc;
        _rc = rc;
        Gl = null!;
    }

    public GL Gl { get; private set; }

    public bool ClipControlSupported { get; private set; }

    public int VersionTens { get; private set; }

    public uint DefaultFramebuffer => 0;

    /// <summary>Creates the offscreen context, or returns null (with a reason) if unavailable.</summary>
    public static WglOffscreenContext? TryCreate(out string reason)
    {
        nint hwnd = 0;
        nint dc = 0;
        nint dummyRc = 0;
        try
        {
            EnsureWindowClass();
            hwnd = Native.CreateWindowExW(
                0, _classAtomName, "GedGlOffscreen", 0, 0, 0, 1, 1, 0, 0, Native.GetModuleHandleW(null), 0);
            if (hwnd == 0)
            {
                reason = $"CreateWindowEx failed (Win32 {Marshal.GetLastWin32Error()})";
                return null;
            }

            dc = Native.GetDC(hwnd);
            var pfd = new Native.PixelFormatDescriptor
            {
                NSize = (ushort)sizeof(Native.PixelFormatDescriptor),
                NVersion = 1,
                DwFlags = Native.PfdDrawToWindow | Native.PfdSupportOpengl | Native.PfdDoublebuffer,
                IPixelType = 0, // PFD_TYPE_RGBA
                CColorBits = 32,
                CAlphaBits = 8,
                CDepthBits = 24,
                ILayerType = 0, // PFD_MAIN_PLANE
            };
            int fmt = Native.ChoosePixelFormat(dc, ref pfd);
            if (fmt == 0 || !Native.SetPixelFormat(dc, fmt, ref pfd))
            {
                reason = "no compatible pixel format (headless GL unavailable)";
                Native.ReleaseDC(hwnd, dc);
                Native.DestroyWindow(hwnd);
                return null;
            }

            // A legacy context is required to query wglCreateContextAttribsARB.
            dummyRc = Native.wglCreateContext(dc);
            if (dummyRc == 0 || !Native.wglMakeCurrent(dc, dummyRc))
            {
                reason = "wglCreateContext failed (no OpenGL driver)";
                Cleanup(hwnd, dc, dummyRc);
                return null;
            }

            nint createAttribsPtr = Native.wglGetProcAddress("wglCreateContextAttribsARB");
            if (createAttribsPtr == 0)
            {
                reason = "wglCreateContextAttribsARB missing (GL < 3.0 / GDI generic)";
                Cleanup(hwnd, dc, dummyRc);
                return null;
            }

            var createAttribs = Marshal.GetDelegateForFunctionPointer<Native.CreateContextAttribs>(createAttribsPtr);
            int[] attribs =
            {
                WglContextMajorVersion, 3,
                WglContextMinorVersion, 3,
                WglContextProfileMask, WglContextCoreProfileBit,
                WglContextFlags, WglContextForwardCompatibleBit,
                0,
            };
            nint coreRc = createAttribs(dc, 0, attribs);
            if (coreRc == 0)
            {
                reason = "no OpenGL 3.3 core context (driver caps too low)";
                Cleanup(hwnd, dc, dummyRc);
                return null;
            }

            Native.wglMakeCurrent(dc, coreRc);
            Native.wglDeleteContext(dummyRc);
            dummyRc = 0;

            var ctx = new WglOffscreenContext(hwnd, dc, coreRc);
            ctx.Gl = GL.GetApi(ctx.Resolve);

            ctx.VersionTens = QueryVersionTens(ctx.Gl);
            if (ctx.VersionTens < 33)
            {
                reason = $"GL version {ctx.VersionTens / 10.0:F1} < 3.3";
                ctx.Dispose();
                return null;
            }

            ctx.ClipControlSupported = HasExtension(ctx.Gl, "GL_ARB_clip_control");
            reason = ctx.ClipControlSupported ? "GL 3.3 core" : "GL 3.3 core (no clip_control; depth fixup)";
            return ctx;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            Cleanup(hwnd, dc, dummyRc);
            return null;
        }
    }

    public void MakeCurrent()
    {
        if (Native.wglGetCurrentContext() != _rc)
        {
            Native.wglMakeCurrent(_dc, _rc);
        }
    }

    public void SwapBuffers() => Native.SwapBuffers(_dc);

    /// <summary>Exposes the two-tier GL entry-point resolver so tests can drive the public
    /// host seam (<see cref="Ged.Rendering.Graphics.IExternalGlContext"/>) over a real WGL context.</summary>
    internal nint GetProcAddress(string name) => Resolve(name);

    private nint Resolve(string name)
    {
        nint p = Native.wglGetProcAddress(name);
        if (p == 0 || p == 1 || p == 2 || p == 3 || p == unchecked((nint)(-1)))
        {
            if (_openglModule == 0)
            {
                _openglModule = Native.LoadLibraryW("opengl32.dll");
            }

            p = Native.GetProcAddress(_openglModule, name);
        }

        return p;
    }

    private static int QueryVersionTens(GL gl)
    {
        int major = gl.GetInteger(GLEnum.MajorVersion);
        int minor = gl.GetInteger(GLEnum.MinorVersion);
        if (major == 0)
        {
            string v = gl.GetStringS(StringName.Version) ?? string.Empty;
            string[] parts = v.Split('.', ' ');
            if (parts.Length >= 2 && int.TryParse(parts[0], out major))
            {
                int.TryParse(parts[1], out minor);
            }
        }

        return (major * 10) + minor;
    }

    private static bool HasExtension(GL gl, string name)
    {
        int count = gl.GetInteger(GLEnum.NumExtensions);
        for (uint i = 0; i < count; i++)
        {
            if (gl.GetStringS(StringName.Extensions, i) == name)
            {
                return true;
            }
        }

        return false;
    }

    private static void Cleanup(nint hwnd, nint dc, nint rc)
    {
        if (rc != 0)
        {
            Native.wglMakeCurrent(0, 0);
            Native.wglDeleteContext(rc);
        }

        if (dc != 0 && hwnd != 0)
        {
            Native.ReleaseDC(hwnd, dc);
        }

        if (hwnd != 0)
        {
            Native.DestroyWindow(hwnd);
        }
    }

    private static void EnsureWindowClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
            {
                return;
            }

            _wndProc = Native.DefWindowProcW;
            _classAtomName = Marshal.StringToHGlobalUni("GedGlOffscreenClass");
            var wc = new Native.WndClass
            {
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                HInstance = Native.GetModuleHandleW(null),
                LpszClassName = _classAtomName,
            };
            Native.RegisterClassW(ref wc);
            _classRegistered = true;
        }
    }

    public void Dispose()
    {
        if (_rc != 0)
        {
            Gl?.Dispose();
            Native.wglMakeCurrent(0, 0);
            Native.wglDeleteContext(_rc);
            _rc = 0;
        }

        if (_dc != 0 && _hwnd != 0)
        {
            Native.ReleaseDC(_hwnd, _dc);
            _dc = 0;
        }

        if (_hwnd != 0)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    /// <summary>Minimal Win32/WGL P/Invokes for the headless context (no extra dependencies).</summary>
    private static class Native
    {
        public const uint PfdDoublebuffer = 0x00000001;
        public const uint PfdDrawToWindow = 0x00000004;
        public const uint PfdSupportOpengl = 0x00000020;

        public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint CreateContextAttribs(nint dc, nint shareContext, [In] int[] attribList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern nint GetProcAddress(nint module, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint LoadLibraryW(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassW(ref WndClass wndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowExW(
            uint exStyle, nint className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(nint hWnd, nint dc);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(nint hWnd);

        [DllImport("gdi32.dll")]
        public static extern int ChoosePixelFormat(nint dc, ref PixelFormatDescriptor pfd);

        [DllImport("gdi32.dll")]
        public static extern bool SetPixelFormat(nint dc, int format, ref PixelFormatDescriptor pfd);

        [DllImport("gdi32.dll")]
        public static extern bool SwapBuffers(nint dc);

        [DllImport("opengl32.dll")]
        public static extern nint wglCreateContext(nint dc);

        [DllImport("opengl32.dll")]
        public static extern bool wglMakeCurrent(nint dc, nint rc);

        [DllImport("opengl32.dll")]
        public static extern bool wglDeleteContext(nint rc);

        [DllImport("opengl32.dll")]
        public static extern nint wglGetCurrentContext();

        [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
        public static extern nint wglGetProcAddress(string name);

        [StructLayout(LayoutKind.Sequential)]
        public struct WndClass
        {
            public uint Style;
            public nint LpfnWndProc;
            public int CbClsExtra;
            public int CbWndExtra;
            public nint HInstance;
            public nint HIcon;
            public nint HCursor;
            public nint HbrBackground;
            public nint LpszMenuName;
            public nint LpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PixelFormatDescriptor
        {
            public ushort NSize;
            public ushort NVersion;
            public uint DwFlags;
            public byte IPixelType;
            public byte CColorBits;
            public byte CRedBits;
            public byte CRedShift;
            public byte CGreenBits;
            public byte CGreenShift;
            public byte CBlueBits;
            public byte CBlueShift;
            public byte CAlphaBits;
            public byte CAlphaShift;
            public byte CAccumBits;
            public byte CAccumRedBits;
            public byte CAccumGreenBits;
            public byte CAccumBlueBits;
            public byte CAccumAlphaBits;
            public byte CDepthBits;
            public byte CStencilBits;
            public byte CAuxBuffers;
            public byte ILayerType;
            public byte BReserved;
            public uint DwLayerMask;
            public uint DwVisibleMask;
            public uint DwDamageMask;
        }
    }
}
