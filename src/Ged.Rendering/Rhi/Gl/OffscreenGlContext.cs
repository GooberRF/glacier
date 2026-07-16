using System.Runtime.InteropServices;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// Platform-selecting factory for the headless <see cref="IGlContext"/> the
/// <see cref="GlRenderDevice"/> renders through in the offscreen/test path. Windows
/// uses WGL (system <c>opengl32.dll</c>); Linux/Unix prefers EGL surfaceless
/// (works on llvmpipe with no display server) and falls back to GLX. Every backend
/// bundles NO native payload and returns null (with a chained reason) when a 3.3-core
/// context cannot be made, so the render tests skip exactly as they do without a GPU.
/// L3's onscreen Avalonia <c>OpenGlControlBase</c> host supplies its own
/// <see cref="IGlContext"/> and does not go through this factory.
/// </summary>
internal static class OffscreenGlContext
{
    public static IGlContext? TryCreate(out string reason)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WglOffscreenContext.TryCreate(out reason);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            EglOffscreenContext? egl = EglOffscreenContext.TryCreate(out string eglReason);
            if (egl is not null)
            {
                reason = eglReason;
                return egl;
            }

            GlxOffscreenContext? glx = GlxOffscreenContext.TryCreate(out string glxReason);
            if (glx is not null)
            {
                reason = glxReason;
                return glx;
            }

            reason = $"no headless GL context (EGL: {eglReason}; GLX: {glxReason})";
            return null;
        }

        // macOS: WGL/EGL/GLX all inapplicable; the GL offscreen path is not wired for
        // Darwin here (out of scope), so report cleanly and let the caller skip.
        reason = "headless GL offscreen context not implemented for this platform";
        return null;
    }
}
