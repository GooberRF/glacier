using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes;
using Ged.App.Docking;

namespace Ged.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        // CP3: assign the OS-appropriate platform backends (Linux audio, etc.) before
        // anything uses them.
        PlatformIntegration.Configure();

        // CP3 (AppImage): when the exe dir is read-only (AppImage mount) the scripts dir falls back to
        // the empty profile location, hiding the bundled examples + Lua api stub shipped beside the binary.
        // Seed them (copy-if-absent) so the scripting tour works; no-op for a writable/portable install.
        Ged.Core.AppPaths.SeedBundledScriptsToFallback();

        Styles.Add(new FluentTheme());
        Styles.Add(new DockFluentTheme());

        // Items 1 & 2: correct Dock.Avalonia's chrome brushes so a runtime theme switch applies
        // live (they are otherwise stuck at the startup variant) and unselected tab labels stay
        // legible in light theme. See ThemeResources for the full diagnosis.
        ThemeResources.Install(this);

        DataTemplates.Add(new PanelViewLocator());

        AppSettings settings = SettingsStore.Load();

        // Select the GPU backend for the shared off-viewport device (thumbnails, icon atlas)
        // to match the viewport panes: Direct3D 11 on Windows unless OpenGL is chosen, and
        // OpenGL everywhere else (no D3D11 off Windows). The live GL panes host their own
        // contexts; this only governs the shared device.
        GpuHost.Configure(Ged.App.Viewport.ViewportBackends.Resolve(settings.Renderer));

        RequestedThemeVariant = settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? openPath = ParseOpenArg(desktop.Args);
            var window = new MainWindow(openPath);

            // Crash hardening: write a crash log + emergency-autosave the open document
            // on a fatal exception, and route background faults to the session log.
            CrashHandler.Install(window.TryGetEmergencyDocument);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => GpuHost.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Reads a <c>--open &lt;path&gt;</c> argument (added for testability/automation).</summary>
    private static string? ParseOpenArg(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--open", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
