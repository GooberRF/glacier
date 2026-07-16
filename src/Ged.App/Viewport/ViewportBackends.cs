using System;
using Ged.Rendering.Graphics;

namespace Ged.App.Viewport;

/// <summary>
/// Resolves which GPU backend the viewport panes instantiate, from the persisted
/// <see cref="AppSettings.Renderer"/> setting and the host OS. Windows defaults to the
/// Direct3D 11 reference backend and switches to the composited OpenGL panes when the
/// user selects OpenGL (restart-scoped); every non-Windows platform is OpenGL-only (there
/// is no D3D11 there, and the Win32 child-HWND host must never be touched — that is exactly
/// where the Linux bring-up died). The same resolution drives the Avalonia-WGL platform
/// configuration at startup, so the two never disagree.
/// </summary>
internal static class ViewportBackends
{
    /// <summary>The backend the panes should instantiate for a given renderer setting.</summary>
    public static GraphicsBackend Resolve(int rendererSetting)
    {
        if (!OperatingSystem.IsWindows())
        {
            return GraphicsBackend.OpenGl; // Linux/other: GL panes always (no D3D11, no Win32 host)
        }

        return (GraphicsBackend)rendererSetting == GraphicsBackend.OpenGl
            ? GraphicsBackend.OpenGl
            : GraphicsBackend.Direct3D11;
    }

    /// <summary>Whether the resolved backend uses the composited OpenGL panes.</summary>
    public static bool UsesOpenGl(int rendererSetting) => Resolve(rendererSetting) == GraphicsBackend.OpenGl;
}
