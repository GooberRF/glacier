using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Ged.Core.Input;
using Ged.Core.Playtest;

namespace Ged.App;

/// <summary>
/// Playtest launch: F7 Play Level, F8 Play Level from Camera, and
/// Play in Multi / from Camera. Saves RED-style (the save writes what was last built and never bakes,
/// so a playtest launches with whatever geometry / lighting is on disk), stages the .rfl
/// into <c>&lt;install&gt;\user_maps\&lt;single|multi&gt;</c>, and launches the
/// configured Alpine Faction launcher (<c>-level</c> / <c>-levelm</c>; a manually
/// configured non-launcher exe still runs, gated to single-player). "From Camera" (F8/F10)
/// appends RED's real <c>-startpos</c> / <c>-startdir</c> spawn-override switches for the
/// active perspective camera's eye + forward, exactly as RED.exe does — the staged copy is a
/// byte-for-byte copy of the saved .rfl in every mode (the document and saved file are never
/// touched).
/// </summary>
public sealed partial class MainWindow
{
    private void InitPlaytest()
    {
        _dispatcher.Bind(CommandIds.FilePlayLevel, () => _ = PlayAsync(PlaytestMode.Single, fromCamera: false), () => Document is not null);
        _dispatcher.Bind(CommandIds.FilePlayFromCamera, () => _ = PlayAsync(PlaytestMode.Single, fromCamera: true), () => Document is not null);
        _dispatcher.Bind(CommandIds.FilePlayMulti, () => _ = PlayAsync(PlaytestMode.Multi, fromCamera: false), () => Document is not null);
        _dispatcher.Bind(CommandIds.FilePlayMultiFromCamera, () => _ = PlayAsync(PlaytestMode.Multi, fromCamera: true), () => Document is not null);
    }

    private async Task PlayAsync(PlaytestMode mode, bool fromCamera)
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open or create a level first.");
            return;
        }

        string? exe = ResolveGameExe();
        if (exe is null)
        {
            _dispatcher.ShowMessage("Set the game executable in Settings (or mount an RF install so it can be guessed).");
            return;
        }

        GameKind kind = GameLauncher.DetectKind(exe);
        if (mode == PlaytestMode.Multi && !GameLauncher.SupportsMulti(kind))
        {
            _dispatcher.ShowMessage("Play in Multi (-levelm) requires the Alpine Faction launcher; the configured exe is stock RF.");
            return;
        }

        string installDir = _session.RfInstallDir ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory;

        // Ensure the user's level is saved (RED-style: the save writes what was last built and never
        // bakes — F7 after brush edits launches with whatever lighting is on disk, stock-RED behavior).
        // A save the seal guard aborts (a build in flight, or the geometry re-seal did not complete)
        // must abort the launch too — its notification already fired.
        bool saved = await SaveAsync(saveAs: Document.Path is null);
        if (!saved || Document.Path is not string levelPath)
        {
            return; // save cancelled or aborted — do not launch on unsealed geometry
        }

        try
        {
            string levelName = Path.GetFileName(levelPath);

            // From Camera (F8/F10): pass the active perspective camera's eye + forward so
            // BuildCommand appends RED's -startpos/-startdir switches. Plain Play emits neither.
            // Staging is a plain copy of the on-disk .rfl in EVERY mode — no spawn relocation,
            // no re-serialization, so the byte-identity gates are never touched.
            PlaytestCommand cmd;
            if (fromCamera)
            {
                var cameraSurface = _viewportGrid.CameraSurface;
                cmd = GameLauncher.BuildCommand(
                    exe, installDir, levelName, mode, fromCamera: true, _settings.PlaytestExtraArgs,
                    cameraSurface.CameraPosition, cameraSurface.CameraForward);
            }
            else
            {
                cmd = GameLauncher.BuildCommand(exe, installDir, levelName, mode, fromCamera: false, _settings.PlaytestExtraArgs);
            }

            byte[] rflBytes = File.ReadAllBytes(levelPath);
            GameLauncher.StageLevel(cmd, rflBytes);

            // Compose through the launch template (blank = direct on Windows; "wine {exe} {args}"
            // on Linux by default). A wrapped launch invokes the wrapper directly (no shell), so
            // the wrapper (e.g. wine) resolves via PATH and receives the arguments verbatim.
            bool direct = string.IsNullOrWhiteSpace(_settings.PlaytestLaunchTemplate);
            GameLauncher.LaunchProcess launch = GameLauncher.ComposeProcess(cmd, _settings.PlaytestLaunchTemplate);

            Process.Start(new ProcessStartInfo
            {
                FileName = launch.FileName,
                Arguments = launch.Arguments,
                WorkingDirectory = cmd.WorkingDirectory,
                UseShellExecute = direct,
            });

            _dispatcher.ShowMessage($"Launching {launch.FileName} {launch.Arguments}  (staged → {cmd.DestinationRflPath})");
        }
        catch (Exception ex)
        {
            _notifications.Notify(Services.NotificationSeverity.Error, $"Play failed: {ex.Message}");
        }
    }

    /// <summary>Resolves the game exe from settings, else guesses it from the install dir and remembers it.</summary>
    private string? ResolveGameExe()
    {
        if (!string.IsNullOrWhiteSpace(_settings.GameExePath) && File.Exists(_settings.GameExePath))
        {
            return _settings.GameExePath;
        }

        // Registry-first (af:// protocol) then the beside-the-install probe (item 6).
        string? guess = GameLauncher.GuessExe(_session.RfInstallDir, Services.WindowsAfProtocolReader.Instance);
        if (guess is not null)
        {
            _settings.GameExePath = guess;
            Persist();
        }

        return guess;
    }
}
