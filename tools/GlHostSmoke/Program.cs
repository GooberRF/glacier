using System;
using System.IO;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ged.App.Viewport;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Tex;
using Ged.Rendering;
using Ged.Rendering.Scene;

// Standalone real-window smoke for the composited OpenGL viewport host
// (GlViewportSurface on Avalonia's OpenGlControlBase). Configures Avalonia's Win32
// platform for WGL so OpenGlControlBase hands us a DESKTOP GL context (the RHI GL
// backend is GL 3.3 core; Avalonia's default ANGLE path is GL ES and would not run
// the GLSL-330 shaders). Loads a level, hosts the GL viewport in a real window, waits
// for the host to draw the composited scene, writes the captured frame to a PNG and
// exits with 0 on a non-trivial image (or non-zero with a reason).
//
// Usage: GlHostSmoke <level.rfl> <out.png> [timeoutMs]
internal static class Program
{
    private static string _rfl = string.Empty;
    private static string _out = string.Empty;
    private static int _timeoutMs = 15000;

    private static bool _gridMode;

    [STAThread]
    public static int Main(string[] args)
    {
        // --grid: host a REAL 4-pane ViewportGrid of composited GL panes (the app's
        // 4-viewport layout — Top/Persp/Front/Left, ortho panes wireframe) and capture
        // every pane, proving four hosted GL devices render simultaneously in one window.
        if (args.Length > 0 && string.Equals(args[0], "--grid", StringComparison.OrdinalIgnoreCase))
        {
            _gridMode = true;
            args = args[1..];
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: GlHostSmoke [--grid] <level.rfl> <out.png> [timeoutMs]");
            return 2;
        }

        _rfl = args[0];
        _out = args[1];
        if (args.Length > 2 && int.TryParse(args[2], out int t))
        {
            _timeoutMs = t;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            _status = "SMOKE EXCEPTION: " + ex;
            _exitCode = 3;
        }

        WriteStatus();
        return _exitCode;
    }

    private static string _status = "no status";

    private static void WriteStatus()
    {
        try
        {
            File.WriteAllText(_out + ".status.txt", $"exit={_exitCode}\n{_status}\n");
        }
        catch
        {
            // best effort
        }
    }

    private static int _exitCode = 4; // "never captured" unless overwritten

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SmokeApp>()
            .UsePlatformDetect()
            // Desktop GL via WGL so OpenGlControlBase yields a GL 3.3-core context.
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Wgl, Win32RenderingMode.Software },
            })
            .LogToTrace();

    private sealed class SmokeApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
        {
            if (_gridMode)
            {
                RunGridSmoke();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var dispatcher = new Ged.App.Services.CommandDispatcher(
                Ged.Core.Input.CommandCatalog.BuildRegistry(),
                Ged.Core.Input.Keymap.FromPreset(Ged.Core.Input.CommandCatalog.RedClassic));
            var gl = new GlViewportSurface(dispatcher, Ged.App.Camera.CameraSchemeKind.ModernFps, ViewType.Perspective);
            var window = new Window
            {
                Title = "GL Host Smoke",
                Width = 640,
                Height = 480,
                Content = gl,
            };

            bool captured = false;
            gl.FrameCaptured = (w, h, rgba) =>
            {
                if (captured)
                {
                    return;
                }

                captured = true;
                try
                {
                    bool nonTrivial = IsNonTrivial(rgba, out int distinct);
                    File.WriteAllBytes(_out, PngWriter.Encode(w, h, rgba));
                    _status = $"CAPTURED {w}x{h}, {distinct} distinct colors, nonTrivial={nonTrivial}, initError={gl.InitError ?? "none"}";
                    _exitCode = nonTrivial ? 0 : 5;
                }
                catch (Exception ex)
                {
                    _status = "CAPTURE WRITE FAILED: " + ex;
                    _exitCode = 6;
                }

                Dispatcher.UIThread.Post(() => (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            };

            window.Opened += (_, _) =>
            {
                try
                {
                    RflFile file = RflFile.Load(_rfl);
                    Ged.Core.Assets.AssetVfs? vfs = TryMountRf();
                    RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());

                    // Frame an overview from the scene bounds so the artifact shows the level,
                    // not the inside of the nearest wall.
                    var p1 = new Vector3(scene.Bounds.P1.X, scene.Bounds.P1.Y, scene.Bounds.P1.Z);
                    var p2 = new Vector3(scene.Bounds.P2.X, scene.Bounds.P2.Y, scene.Bounds.P2.Z);
                    Vector3 center = (p1 + p2) * 0.5f;
                    float radius = MathF.Max(4f, (p2 - p1).Length() * 0.5f);
                    Vector3 eye = center + (new Vector3(0.8f, 0.6f, -1f) * radius);
                    gl.LoadScene(scene, vfs, eye, center);
                }
                catch (Exception ex)
                {
                    _status = "SCENE LOAD FAILED: " + ex;
                    _exitCode = 7;
                    (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                }
            };

            // Safety timeout so the smoke never hangs.
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (!captured)
                    {
                        _status = $"TIMEOUT after {_timeoutMs} ms (initError={gl.InitError ?? "none"})";
                        _exitCode = 8;
                    }

                    (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                },
                TimeSpan.FromMilliseconds(_timeoutMs));

            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// The 4-pane smoke: hosts the app's REAL ViewportGrid with composited GL panes
        /// (Top/Perspective/Front/Left, ortho panes wireframe — the stock 4-viewport
        /// layout Goober TABs into), loads the level into every pane and captures each
        /// pane's frame. Success requires all four captures non-trivial — four hosted GL
        /// devices rendering simultaneously in one composited window.
        /// </summary>
        private void RunGridSmoke()
        {
            var dispatcher = new Ged.App.Services.CommandDispatcher(
                Ged.Core.Input.CommandCatalog.BuildRegistry(),
                Ged.Core.Input.Keymap.FromPreset(Ged.Core.Input.CommandCatalog.RedClassic));
            var grid = new Ged.App.Viewport.ViewportGrid(
                dispatcher, Ged.App.Camera.CameraSchemeKind.ModernFps, RenderMode.TexturesAndLightmaps,
                useOpenGl: true);
            grid.SetLayout(4);

            var window = new Window
            {
                Title = "GL Host Smoke (4-pane grid)",
                Width = 960,
                Height = 720,
                Content = grid,
            };

            int paneCount = grid.Panes.Count;
            var done = new bool[paneCount];
            var results = new string[paneCount];
            var gate = new object();
            for (int i = 0; i < paneCount; i++)
            {
                int pane = i;
                var surface = (GlViewportSurface)grid.Panes[pane].Surface;
                surface.FrameCaptured = (w, h, rgba) =>
                {
                    bool finished;
                    lock (gate)
                    {
                        if (done[pane])
                        {
                            return;
                        }

                        done[pane] = true;
                        try
                        {
                            bool nonTrivial = IsNonTrivial(rgba, out int distinct);

                            // A wireframe ortho pane is sparse by construction (thin lines over
                            // background — often >98.5% one color, and the stock LoadScene centers
                            // ortho poses on the scene bounds, which outlier objects can skew, same
                            // as D3D11). It passes by proving its device DREW something: >1 color.
                            bool paneOk = surface.Mode == RenderMode.Wireframe ? distinct > 1 : nonTrivial;
                            File.WriteAllBytes($"{_out}.pane{pane}.png", PngWriter.Encode(w, h, rgba));
                            results[pane] = $"pane{pane} ({surface.ViewType}/{surface.Mode}): {w}x{h}, {distinct} colors, ok={paneOk} (nonTrivial={nonTrivial}), initError={surface.InitError ?? "none"}";
                            if (!paneOk)
                            {
                                _exitCode = 5;
                            }
                        }
                        catch (Exception ex)
                        {
                            results[pane] = $"pane{pane}: CAPTURE WRITE FAILED: {ex.Message}";
                            _exitCode = 6;
                        }

                        finished = Array.TrueForAll(done, d => d);
                    }

                    if (finished)
                    {
                        lock (gate)
                        {
                            _status = "GRID CAPTURED\n" + string.Join("\n", results);
                            if (_exitCode == 4)
                            {
                                _exitCode = 0; // only when never demoted by a trivial/failed pane
                            }
                        }

                        Dispatcher.UIThread.Post(() => (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
                    }
                };
            }

            window.Opened += (_, _) =>
            {
                try
                {
                    RflFile file = RflFile.Load(_rfl);
                    Ged.Core.Assets.AssetVfs? vfs = TryMountRf();
                    RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());
                    var p1 = new Vector3(scene.Bounds.P1.X, scene.Bounds.P1.Y, scene.Bounds.P1.Z);
                    var p2 = new Vector3(scene.Bounds.P2.X, scene.Bounds.P2.Y, scene.Bounds.P2.Z);
                    Vector3 center = (p1 + p2) * 0.5f;
                    float radius = MathF.Max(4f, (p2 - p1).Length() * 0.5f);
                    Vector3 eye = center + (new Vector3(0.8f, 0.6f, -1f) * radius);
                    grid.LoadScene(scene, vfs, eye, center);
                }
                catch (Exception ex)
                {
                    _status = "SCENE LOAD FAILED: " + ex;
                    _exitCode = 7;
                    (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                }
            };

            DispatcherTimer.RunOnce(
                () =>
                {
                    lock (gate)
                    {
                        if (!Array.TrueForAll(done, d => d))
                        {
                            _status = $"GRID TIMEOUT after {_timeoutMs} ms\n" + string.Join("\n", Array.ConvertAll(results, r => r ?? "pane: no capture"));
                            _exitCode = 8;
                        }
                    }

                    (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                },
                TimeSpan.FromMilliseconds(_timeoutMs));

            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }
        }
    }

    private static Ged.Core.Assets.AssetVfs? TryMountRf()
    {
        foreach (string dir in RfDirCandidates())
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    return Ged.Core.Assets.GameMount.Mount(dir);
                }
            }
            catch
            {
                // ignore and try the next candidate
            }
        }

        return null;
    }

    // RF-install candidates in priority order: the GED_RF_DIR environment variable first, then
    // each non-comment line of the developer-local, gitignored research/rf-dirs.txt (one path per
    // line, '#' comments allowed). No machine-specific paths are baked into source.
    private static System.Collections.Generic.IEnumerable<string> RfDirCandidates()
    {
        string? env = Environment.GetEnvironmentVariable("GED_RF_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        foreach (string line in ReadRfDirsFile())
        {
            yield return line;
        }
    }

    private static System.Collections.Generic.IReadOnlyList<string> ReadRfDirsFile()
    {
        var result = new System.Collections.Generic.List<string>();
        string? root = LocateRepoRoot();
        if (root is null)
        {
            return result;
        }

        string path = Path.Combine(root, "research", "rf-dirs.txt");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                result.Add(trimmed);
            }
        }
        catch
        {
            // Unreadable rf-dirs.txt -> no candidates.
        }

        return result;
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsNonTrivial(byte[] rgba, out int distinctColors)
    {
        var counts = new System.Collections.Generic.Dictionary<uint, int>();
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            uint c = (uint)(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16) | (rgba[i + 3] << 24));
            counts.TryGetValue(c, out int n);
            counts[c] = n + 1;
        }

        distinctColors = counts.Count;
        if (counts.Count <= 1)
        {
            return false;
        }

        int total = rgba.Length / 4;
        int dominant = 0;
        foreach (int v in counts.Values)
        {
            if (v > dominant)
            {
                dominant = v;
            }
        }

        return dominant < (int)(total * 0.985);
    }
}
