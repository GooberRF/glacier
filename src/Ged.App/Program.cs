using System;
using Avalonia;
using Ged.App.Viewport;
using Ged.Rendering.Graphics;

namespace Ged.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // When the OpenGL renderer is selected on Windows, configure Avalonia's Win32
        // platform for WGL so OpenGlControlBase yields a DESKTOP GL 3.3-core context — the
        // RHI GL backend runs GLSL-330 shaders that Avalonia's default ANGLE (GL ES) path
        // will not compile. This is a whole-app compositor decision, hence restart-scoped and
        // read from settings here at startup (mirrors ViewportBackends.Resolve, so the panes
        // and the platform never disagree). No override is needed off Windows: UsePlatformDetect
        // already gives desktop GL on X11/Wayland.
        if (OperatingSystem.IsWindows() && SelectedBackend() == GraphicsBackend.OpenGl)
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Wgl, Win32RenderingMode.Software },
            });
        }

        return builder;
    }

    private static GraphicsBackend SelectedBackend()
    {
        try
        {
            return ViewportBackends.Resolve(SettingsStore.Load().Renderer);
        }
        catch (Exception)
        {
            return GraphicsBackend.Direct3D11; // never let settings IO block startup
        }
    }
}
