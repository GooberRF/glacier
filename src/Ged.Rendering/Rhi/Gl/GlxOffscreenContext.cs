using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// A headless OpenGL 3.3-core context created through <b>GLX</b> — the fallback for
/// the (rare) Linux/Unix box that has <c>libGL.so.1</c> + an X display but no usable
/// <c>libEGL</c>. Preferred path is <see cref="EglOffscreenContext"/> (works without
/// any display server); this one needs an X server (or Xvfb), so it is only tried
/// after EGL fails. Like the other offscreen contexts it bundles NO native payload —
/// it P/Invokes the system <c>libGL.so.1</c> / <c>libX11.so.6</c> and renders into a
/// 1x1 pbuffer. <see cref="TryCreate"/> returns null (with a reason) when no X
/// display / FBConfig / 3.3-core context is available, so callers skip gracefully.
/// </summary>
internal sealed unsafe class GlxOffscreenContext : IGlContext
{
    private const int GlxRenderType = 0x8011;
    private const int GlxRgbaBit = 0x00000001;
    private const int GlxDrawableType = 0x8010;
    private const int GlxPbufferBit = 0x00000004;
    private const int GlxRedSize = 8;
    private const int GlxGreenSize = 9;
    private const int GlxBlueSize = 10;
    private const int GlxAlphaSize = 11;
    private const int GlxDepthSize = 12;
    private const int GlxPbufferWidth = 0x8041;
    private const int GlxPbufferHeight = 0x8040;
    private const int GlxContextMajorVersionArb = 0x2091;
    private const int GlxContextMinorVersionArb = 0x2092;
    private const int GlxContextProfileMaskArb = 0x9126;
    private const int GlxContextCoreProfileBitArb = 0x00000001;
    private const int GlxContextFlagsArb = 0x2094;
    private const int GlxContextForwardCompatibleBitArb = 0x00000002;

    private nint _display;
    private nint _context;
    private nint _pbuffer;
    private nint _glModule;

    private GlxOffscreenContext(nint display, nint context, nint pbuffer)
    {
        _display = display;
        _context = context;
        _pbuffer = pbuffer;
        Gl = null!;
    }

    public GL Gl { get; private set; }

    public bool ClipControlSupported { get; private set; }

    public int VersionTens { get; private set; }

    public uint DefaultFramebuffer => 0;

    /// <summary>Creates the GLX offscreen context, or returns null (with a reason) if unavailable.</summary>
    public static GlxOffscreenContext? TryCreate(out string reason)
    {
        nint display = 0;
        nint context = 0;
        nint pbuffer = 0;
        try
        {
            display = X.XOpenDisplay(null);
            if (display == 0)
            {
                reason = "XOpenDisplay failed (no X display for GLX; set DISPLAY or use Xvfb)";
                return null;
            }

            int screen = X.XDefaultScreen(display);
            int[] fbAttribs =
            {
                GlxRenderType, GlxRgbaBit,
                GlxDrawableType, GlxPbufferBit,
                GlxRedSize, 8, GlxGreenSize, 8, GlxBlueSize, 8, GlxAlphaSize, 8,
                GlxDepthSize, 24,
                0,
            };
            nint fbList = Glx.glXChooseFBConfig(display, screen, fbAttribs, out int nConfigs);
            if (fbList == 0 || nConfigs < 1)
            {
                reason = "glXChooseFBConfig found no pbuffer-capable RGBA config";
                Cleanup(display, 0, 0);
                return null;
            }

            nint fbConfig = Marshal.ReadIntPtr(fbList);
            X.XFree(fbList);

            nint createAttribsPtr = Glx.glXGetProcAddress("glXCreateContextAttribsARB");
            if (createAttribsPtr == 0)
            {
                reason = "glXCreateContextAttribsARB missing (GLX < 1.4 / GL < 3.0)";
                Cleanup(display, 0, 0);
                return null;
            }

            var createAttribs = Marshal.GetDelegateForFunctionPointer<Glx.CreateContextAttribs>(createAttribsPtr);
            int[] ctxAttribs =
            {
                GlxContextMajorVersionArb, 3,
                GlxContextMinorVersionArb, 3,
                GlxContextProfileMaskArb, GlxContextCoreProfileBitArb,
                GlxContextFlagsArb, GlxContextForwardCompatibleBitArb,
                0,
            };
            context = createAttribs(display, fbConfig, 0, direct: 1, ctxAttribs);
            if (context == 0)
            {
                reason = "glXCreateContextAttribsARB (3.3 core) failed";
                Cleanup(display, 0, 0);
                return null;
            }

            int[] pbAttribs = { GlxPbufferWidth, 1, GlxPbufferHeight, 1, 0 };
            pbuffer = Glx.glXCreatePbuffer(display, fbConfig, pbAttribs);
            if (pbuffer == 0 || Glx.glXMakeContextCurrent(display, pbuffer, pbuffer, context) == 0)
            {
                reason = "glXCreatePbuffer / glXMakeContextCurrent failed";
                Cleanup(display, context, pbuffer);
                return null;
            }

            var ctx = new GlxOffscreenContext(display, context, pbuffer);
            ctx.Gl = GL.GetApi(ctx.Resolve);

            ctx.VersionTens = GlVersion.QueryTens(ctx.Gl);
            if (ctx.VersionTens < 33)
            {
                reason = $"GL version {ctx.VersionTens / 10.0:F1} < 3.3";
                ctx.Dispose();
                return null;
            }

            ctx.ClipControlSupported = GlVersion.HasExtension(ctx.Gl, "GL_ARB_clip_control");
            reason = ctx.ClipControlSupported ? "GLX 3.3 core (pbuffer)" : "GLX 3.3 core (no clip_control; depth fixup)";
            return ctx;
        }
        catch (Exception ex)
        {
            reason = $"GLX unavailable: {ex.Message}";
            Cleanup(display, context, pbuffer);
            return null;
        }
    }

    public void MakeCurrent()
    {
        if (Glx.glXGetCurrentContext() != _context)
        {
            Glx.glXMakeContextCurrent(_display, _pbuffer, _pbuffer, _context);
        }
    }

    public void SwapBuffers()
    {
    }

    private nint Resolve(string name)
    {
        nint p = Glx.glXGetProcAddress(name);
        if (p != 0)
        {
            return p;
        }

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

    private static void Cleanup(nint display, nint context, nint pbuffer)
    {
        if (display == 0)
        {
            return;
        }

        Glx.glXMakeContextCurrent(display, 0, 0, 0);
        if (pbuffer != 0)
        {
            Glx.glXDestroyPbuffer(display, pbuffer);
        }

        if (context != 0)
        {
            Glx.glXDestroyContext(display, context);
        }

        X.XCloseDisplay(display);
    }

    public void Dispose()
    {
        Gl?.Dispose();
        Cleanup(_display, _context, _pbuffer);
        _context = 0;
        _pbuffer = 0;
        _display = 0;

        if (_glModule != 0)
        {
            NativeLibrary.Free(_glModule);
            _glModule = 0;
        }
    }

    private static class Glx
    {
        private const string Lib = "libGL.so.1";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint CreateContextAttribs(nint dpy, nint config, nint share, int direct, int[] attribList);

        [DllImport(Lib)]
        public static extern nint glXChooseFBConfig(nint dpy, int screen, int[] attribList, out int nElements);

        [DllImport(Lib)]
        public static extern nint glXCreatePbuffer(nint dpy, nint config, int[] attribList);

        [DllImport(Lib)]
        public static extern void glXDestroyPbuffer(nint dpy, nint pbuffer);

        [DllImport(Lib)]
        public static extern int glXMakeContextCurrent(nint dpy, nint draw, nint read, nint ctx);

        [DllImport(Lib)]
        public static extern void glXDestroyContext(nint dpy, nint ctx);

        [DllImport(Lib)]
        public static extern nint glXGetCurrentContext();

        [DllImport(Lib, CharSet = CharSet.Ansi)]
        public static extern nint glXGetProcAddress(string name);
    }

    private static class X
    {
        private const string Lib = "libX11.so.6";

        [DllImport(Lib)]
        public static extern nint XOpenDisplay(string? display);

        [DllImport(Lib)]
        public static extern int XCloseDisplay(nint display);

        [DllImport(Lib)]
        public static extern int XDefaultScreen(nint display);

        [DllImport(Lib)]
        public static extern int XFree(nint data);
    }
}
