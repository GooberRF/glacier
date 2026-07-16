using Avalonia.OpenGL;
using Ged.Rendering.Graphics;

namespace Ged.App.Viewport;

/// <summary>
/// Wraps the GL context Avalonia's <c>OpenGlControlBase</c> hands each render callback
/// as an <see cref="IExternalGlContext"/> so the OpenGL RHI backend can render composited
/// into the dock (no native child window — this is what fixes the documented airspace
/// limitation). Avalonia owns the context: it makes it current on the render thread
/// before invoking <c>OnOpenGlRender(gl, fb)</c> and composites/presents afterwards, so
/// <see cref="MakeCurrent"/> and <see cref="SwapBuffers"/> are no-ops. Avalonia usually
/// binds a NON-zero default framebuffer, so the host feeds the exact <c>fb</c> it was
/// handed into <see cref="Framebuffer"/> before every frame and the GL swapchain target
/// reads it back through <see cref="DefaultFramebuffer"/>.
/// </summary>
internal sealed class AvaloniaGlContext : IExternalGlContext
{
    private readonly GlInterface _gl;

    public AvaloniaGlContext(GlInterface gl) => _gl = gl;

    /// <summary>The framebuffer Avalonia handed the current render callback (updated per frame).</summary>
    public uint Framebuffer { get; set; }

    public uint DefaultFramebuffer => Framebuffer;

    public nint GetProcAddress(string name) => _gl.GetProcAddress(name);

    // Avalonia makes its GL context current on the render thread before OnOpenGlRender
    // and composites the result itself, so neither of these needs to do anything.
    public void MakeCurrent()
    {
    }

    public void SwapBuffers()
    {
    }
}
