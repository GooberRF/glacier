using Silk.NET.OpenGL;
using Ged.Rendering.Rhi.Gl;

namespace Ged.Rendering.Graphics;

/// <summary>
/// A GL 3.3-core context OWNED BY THE HOST (not by the RHI) that the OpenGL backend
/// renders through — the public seam L3 uses to host the GL device inside Avalonia's
/// <c>OpenGlControlBase</c>, and that L5 will implement over the Linux window system
/// (EGL/GLX) for the same composited viewport. The host creates the context, makes it
/// current on the render thread, and hands its GL entry-point loader plus the default
/// (window-system) framebuffer to <see cref="GraphicsDevice.CreateOpenGlHosted"/>.
/// <para>
/// Contract mirrored from the internal offscreen context so the two are pixel-faithful:
/// the loader must resolve GL 3.3-core entry points; <see cref="DefaultFramebuffer"/>
/// is whatever the host binds onscreen (Avalonia usually binds a NON-zero FBO, so the
/// host must return exactly the fb it was handed each frame — update it before every
/// render); <see cref="MakeCurrent"/> binds the context to the calling thread (a no-op
/// when the host already made it current, e.g. inside an <c>OnOpenGlRender</c> callback);
/// and <see cref="SwapBuffers"/> presents (a no-op when the host/compositor presents).
/// </para>
/// </summary>
public interface IExternalGlContext
{
    /// <summary>Resolves a GL entry point by name (e.g. via <c>wglGetProcAddress</c>/<c>eglGetProcAddress</c>), or 0.</summary>
    nint GetProcAddress(string name);

    /// <summary>
    /// The default (window-system) framebuffer the host renders onscreen into. Avalonia
    /// typically binds a nonzero FBO, so return exactly what the host was handed; the host
    /// updates this before each frame.
    /// </summary>
    uint DefaultFramebuffer { get; }

    /// <summary>Binds this context to the calling thread. Idempotent / no-op when the host already made it current.</summary>
    void MakeCurrent();

    /// <summary>Presents the default framebuffer. A no-op when the host or compositor presents (e.g. Avalonia).</summary>
    void SwapBuffers();
}

/// <summary>
/// Adapts a host-owned <see cref="IExternalGlContext"/> to the internal
/// <see cref="IGlContext"/> the <see cref="GlRenderDevice"/> consumes: loads the
/// Silk.NET GL 3.3 function table from the host's entry-point resolver and derives
/// the version / clip-control capability the depth path keys off. The GL context
/// lifetime belongs to the host, so <see cref="Dispose"/> only drops the managed GL
/// binding — it never tears the host's context down.
/// </summary>
internal sealed class ExternalGlContextAdapter : IGlContext
{
    private readonly IExternalGlContext _external;

    public ExternalGlContextAdapter(IExternalGlContext external)
    {
        _external = external;
        _external.MakeCurrent();
        Gl = GL.GetApi(_external.GetProcAddress);
        VersionTens = QueryVersionTens(Gl);
        ClipControlSupported = HasExtension(Gl, "GL_ARB_clip_control");
    }

    public GL Gl { get; }

    public bool ClipControlSupported { get; }

    public int VersionTens { get; }

    public uint DefaultFramebuffer => _external.DefaultFramebuffer;

    public void MakeCurrent() => _external.MakeCurrent();

    public void SwapBuffers() => _external.SwapBuffers();

    public void Dispose() => Gl.Dispose();

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
}
