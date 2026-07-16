using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// A headless OpenGL 3.3-core context created through <b>EGL</b> — the Linux/Unix
/// counterpart to <see cref="WglOffscreenContext"/>. Like the WGL path it adds NO
/// native NuGet payload: it P/Invokes the system <c>libEGL.so.1</c> (Mesa) and
/// resolves GL entry points through <c>eglGetProcAddress</c> (with a
/// <c>libGL.so.1</c> fallback), exactly as the WGL path uses the system
/// <c>opengl32.dll</c>. This is the seam L5 drives the GL render gates through on a
/// headless Linux box with <c>LIBGL_ALWAYS_SOFTWARE=1</c> (llvmpipe).
/// <para>
/// The <b>surfaceless</b> EGL platform (<c>EGL_PLATFORM_SURFACELESS_MESA</c>) is
/// preferred because it needs no X/Wayland display server — it makes a context
/// current with no window-system surface at all, which is exactly what a CI/render
/// box wants. If the surfaceless platform or surfaceless make-current is
/// unavailable, it falls back to a 1x1 pbuffer surface on the default display.
/// </para>
/// <para>
/// <see cref="TryCreate"/> returns null (with a reason) when a 3.3-core context
/// cannot be made — no <c>libEGL</c>, no config, driver too old — so the tests skip
/// exactly like the D3D11 / WGL paths do when the platform is unavailable. The
/// caller then tries <see cref="GlxOffscreenContext"/> before giving up.
/// </para>
/// </summary>
internal sealed unsafe class EglOffscreenContext : IGlContext
{
    // EGL tokens (subset used here).
    private const int EglNone = 0x3038;
    private const int EglExtensions = 0x3055;
    private const int EglSurfaceType = 0x3033;
    private const int EglPbufferBit = 0x0001;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenglBit = 0x0008;
    private const int EglRedSize = 0x3024;
    private const int EglGreenSize = 0x3023;
    private const int EglBlueSize = 0x3022;
    private const int EglAlphaSize = 0x3021;
    private const int EglDepthSize = 0x3025;
    private const int EglWidth = 0x3057;
    private const int EglHeight = 0x3056;
    private const uint EglOpenglApi = 0x30A2;
    private const int EglContextMajorVersion = 0x3098;
    private const int EglContextMinorVersion = 0x30FB;
    private const int EglContextOpenglProfileMask = 0x30FD;
    private const int EglContextOpenglCoreProfileBit = 0x00000001;
    private const uint EglPlatformSurfacelessMesa = 0x31DD;

    private nint _display;
    private nint _context;
    private nint _surface; // EGL_NO_SURFACE (0) when surfaceless.
    private nint _glModule;

    private EglOffscreenContext(nint display, nint context, nint surface)
    {
        _display = display;
        _context = context;
        _surface = surface;
        Gl = null!;
    }

    public GL Gl { get; private set; }

    public bool ClipControlSupported { get; private set; }

    public int VersionTens { get; private set; }

    public uint DefaultFramebuffer => 0;

    /// <summary>Creates the EGL offscreen context, or returns null (with a reason) if unavailable.</summary>
    public static EglOffscreenContext? TryCreate(out string reason)
    {
        nint display = 0;
        nint context = 0;
        nint surface = 0;
        try
        {
            display = OpenDisplay();
            if (display == 0)
            {
                reason = "eglGetDisplay returned EGL_NO_DISPLAY (no libEGL / no headless platform)";
                return null;
            }

            if (Egl.eglInitialize(display, out int _, out int _) == 0)
            {
                reason = $"eglInitialize failed (0x{Egl.eglGetError():X})";
                return null;
            }

            if (Egl.eglBindAPI(EglOpenglApi) == 0)
            {
                reason = "eglBindAPI(EGL_OPENGL_API) failed (no desktop-GL support)";
                Cleanup(display, 0, 0);
                return null;
            }

            int[] configAttribs =
            {
                EglSurfaceType, EglPbufferBit,
                EglRenderableType, EglOpenglBit,
                EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8,
                EglDepthSize, 24,
                EglNone,
            };
            var configs = new nint[1];
            if (Egl.eglChooseConfig(display, configAttribs, configs, 1, out int numConfig) == 0 || numConfig < 1)
            {
                reason = "eglChooseConfig found no GL 3.3-capable config";
                Cleanup(display, 0, 0);
                return null;
            }

            int[] ctxAttribs =
            {
                EglContextMajorVersion, 3,
                EglContextMinorVersion, 3,
                EglContextOpenglProfileMask, EglContextOpenglCoreProfileBit,
                EglNone,
            };
            context = Egl.eglCreateContext(display, configs[0], 0, ctxAttribs);
            if (context == 0)
            {
                reason = $"eglCreateContext (3.3 core) failed (0x{Egl.eglGetError():X})";
                Cleanup(display, 0, 0);
                return null;
            }

            // Prefer a truly surfaceless current context (EGL_KHR_surfaceless_context);
            // fall back to a 1x1 pbuffer when the driver requires a draw surface.
            if (Egl.eglMakeCurrent(display, 0, 0, context) == 0)
            {
                int[] pbufferAttribs = { EglWidth, 1, EglHeight, 1, EglNone };
                surface = Egl.eglCreatePbufferSurface(display, configs[0], pbufferAttribs);
                if (surface == 0 || Egl.eglMakeCurrent(display, surface, surface, context) == 0)
                {
                    reason = $"eglMakeCurrent failed surfaceless and via pbuffer (0x{Egl.eglGetError():X})";
                    Cleanup(display, context, surface);
                    return null;
                }
            }

            var ctx = new EglOffscreenContext(display, context, surface);
            ctx.Gl = GL.GetApi(ctx.Resolve);

            ctx.VersionTens = GlVersion.QueryTens(ctx.Gl);
            if (ctx.VersionTens < 33)
            {
                reason = $"GL version {ctx.VersionTens / 10.0:F1} < 3.3";
                ctx.Dispose();
                return null;
            }

            ctx.ClipControlSupported = GlVersion.HasExtension(ctx.Gl, "GL_ARB_clip_control");
            reason = ctx.ClipControlSupported
                ? (surface == 0 ? "EGL 3.3 core (surfaceless)" : "EGL 3.3 core (pbuffer)")
                : "EGL 3.3 core (no clip_control; depth fixup)";
            return ctx;
        }
        catch (Exception ex)
        {
            reason = $"EGL unavailable: {ex.Message}";
            Cleanup(display, context, surface);
            return null;
        }
    }

    public void MakeCurrent()
    {
        if (Egl.eglGetCurrentContext() != _context)
        {
            Egl.eglMakeCurrent(_display, _surface, _surface, _context);
        }
    }

    // Offscreen: nothing to present (render targets are FBOs read back on the CPU).
    public void SwapBuffers()
    {
    }

    private static nint OpenDisplay()
    {
        // Surfaceless platform first (no X/Wayland needed — the headless CI path).
        nint getPlatformDisplay = Egl.eglGetProcAddress("eglGetPlatformDisplayEXT");
        if (getPlatformDisplay != 0)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<Egl.GetPlatformDisplayExt>(getPlatformDisplay);
            nint dpy = fn(EglPlatformSurfacelessMesa, 0, null);
            if (dpy != 0)
            {
                return dpy;
            }
        }

        // Fall back to the default display (X/Wayland/GBM, or a driver default).
        return Egl.eglGetDisplay(0);
    }

    private nint Resolve(string name)
    {
        nint p = Egl.eglGetProcAddress(name);
        if (p != 0)
        {
            return p;
        }

        // Core GL 1.1 entry points are not always exported by eglGetProcAddress;
        // resolve them from the system libGL like the WGL path uses opengl32.dll.
        if (_glModule == 0 && !NativeLibrary.TryLoad("libGL.so.1", out _glModule))
        {
            _glModule = 0;
        }

        if (_glModule != 0 && NativeLibrary.TryGetExport(_glModule, name, out nint addr))
        {
            return addr;
        }

        return 0;
    }

    private static void Cleanup(nint display, nint context, nint surface)
    {
        if (display == 0)
        {
            return;
        }

        Egl.eglMakeCurrent(display, 0, 0, 0);
        if (surface != 0)
        {
            Egl.eglDestroySurface(display, surface);
        }

        if (context != 0)
        {
            Egl.eglDestroyContext(display, context);
        }

        Egl.eglTerminate(display);
    }

    public void Dispose()
    {
        Gl?.Dispose();
        Cleanup(_display, _context, _surface);
        _context = 0;
        _surface = 0;
        _display = 0;

        if (_glModule != 0)
        {
            NativeLibrary.Free(_glModule);
            _glModule = 0;
        }
    }

    /// <summary>Minimal EGL 1.4/1.5 P/Invokes (system libEGL; no bundled native).</summary>
    private static class Egl
    {
        private const string Lib = "libEGL.so.1";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint GetPlatformDisplayExt(uint platform, nint nativeDisplay, int[]? attribList);

        [DllImport(Lib)]
        public static extern nint eglGetDisplay(nint displayId);

        [DllImport(Lib)]
        public static extern int eglInitialize(nint dpy, out int major, out int minor);

        [DllImport(Lib)]
        public static extern int eglTerminate(nint dpy);

        [DllImport(Lib)]
        public static extern int eglBindAPI(uint api);

        [DllImport(Lib)]
        public static extern int eglChooseConfig(nint dpy, int[] attribList, nint[] configs, int configSize, out int numConfig);

        [DllImport(Lib)]
        public static extern nint eglCreateContext(nint dpy, nint config, nint shareContext, int[] attribList);

        [DllImport(Lib)]
        public static extern int eglDestroyContext(nint dpy, nint ctx);

        [DllImport(Lib)]
        public static extern nint eglCreatePbufferSurface(nint dpy, nint config, int[] attribList);

        [DllImport(Lib)]
        public static extern int eglDestroySurface(nint dpy, nint surface);

        [DllImport(Lib)]
        public static extern int eglMakeCurrent(nint dpy, nint draw, nint read, nint ctx);

        [DllImport(Lib)]
        public static extern nint eglGetCurrentContext();

        [DllImport(Lib)]
        public static extern int eglGetError();

        [DllImport(Lib, CharSet = CharSet.Ansi)]
        public static extern nint eglGetProcAddress(string name);
    }
}
