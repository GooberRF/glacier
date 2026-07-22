using Ged.Rendering.Graphics;

namespace Ged.App;

/// <summary>
/// Owns the single shared <see cref="GraphicsDevice"/> for the process — the device the
/// off-viewport GPU consumers use (mesh/texture thumbnails, the object-icon atlas). The
/// live viewport panes do NOT use this device when running OpenGL: each composited GL pane
/// owns its own host-driven GL context. The backend defaults to Direct3D 11 (the Windows
/// reference) and is switched to OpenGL by <see cref="Configure"/> at startup — always on
/// non-Windows (there is no D3D11 there) and on Windows when the OpenGL renderer is selected.
/// </summary>
internal static class GpuHost
{
    private static GraphicsDevice? _device;
    private static GraphicsBackend _backend = GraphicsBackend.Direct3D11;
    private static readonly object Lock = new();

    /// <summary>Sets the backend the shared device is created on. Must run before the first
    /// <see cref="Device"/> access (startup); ignored once the device exists.</summary>
    public static void Configure(GraphicsBackend backend)
    {
        lock (Lock)
        {
            if (_device is null)
            {
                _backend = backend;
            }
        }
    }

    public static GraphicsDevice Device
    {
        get
        {
            lock (Lock)
            {
                // preferWindowingGl: this shared off-viewport device lives on the UI thread
                // alongside the windowing-system GL compositor. On Linux/X11 that means its
                // offscreen context must use GLX (not EGL) to coexist with Avalonia's GLX
                // compositor on the same thread; otherwise an EGL context left current there
                // makes Avalonia's glXMakeContextCurrent fail and the app crashes at first paint.
                return _device ??= new GraphicsDevice(_backend, preferWindowingGl: true);
            }
        }
    }

    public static void Shutdown()
    {
        lock (Lock)
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
