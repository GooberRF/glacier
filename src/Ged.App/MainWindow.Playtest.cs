using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ged.Core.Input;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Playtest;

namespace Ged.App;

/// <summary>
/// Playtest launch: F7 Play Level, F8 Play Level from Camera, and
/// Play in Multi / from Camera. Saves RED-style (the save writes what was last built and never bakes,
/// so a playtest launches with whatever geometry / lighting is on disk), stages the .rfl
/// into <c>&lt;install&gt;\user_maps\&lt;single|multi&gt;</c>, and launches the
/// configured Alpine Faction launcher (<c>-level</c> / <c>-levelm</c>; a manually
/// configured non-launcher exe still runs, gated to single-player). "From Camera"
/// temporarily relocates the Player Start to the active camera in the staged copy
/// only — the saved level keeps its real spawn.
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
        // "From Camera" does NOT persist the relocated spawn to the user's file. A save the seal guard
        // aborts (a build in flight, or the geometry re-seal did not complete) must abort the launch too
        // — its notification already fired.
        bool saved = await SaveAsync(saveAs: Document.Path is null);
        if (!saved || Document.Path is not string levelPath)
        {
            return; // save cancelled or aborted — do not launch on unsealed geometry
        }

        try
        {
            string levelName = Path.GetFileName(levelPath);
            PlaytestCommand cmd = GameLauncher.BuildCommand(exe, installDir, levelName, mode, fromCamera, _settings.PlaytestExtraArgs);

            byte[] rflBytes = fromCamera ? SaveBytesWithCameraSpawn() : File.ReadAllBytes(levelPath);
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

    /// <summary>
    /// Serializes the level with the Player Start temporarily moved to the active
    /// camera (position + orientation), then restores it. The editor document and the
    /// user's saved file are left unchanged — only the returned (staged) bytes carry
    /// the camera spawn. This is GED's stand-in for RED's Play-from-camera (which
    /// injects the camera into the running game — a mechanism GED cannot replicate
    /// cheaply); the difference is the spawn is a real Player Start move, not a
    /// live-camera handoff.
    /// </summary>
    private byte[] SaveBytesWithCameraSpawn()
    {
        RflSection? section = Document!.Rfl.Sections.FirstOrDefault(s => s.Content is PlayerStartSection);
        if (section?.Content is not PlayerStartSection ps || _viewportGrid.ActiveSurface.Camera is not { } cam)
        {
            return Document.SaveToBytes();
        }

        Vec3 oldPos = ps.Position;
        Mat3 oldRot = ps.Rotation;
        bool oldDirty = section.Dirty;

        ps.Position = new Vec3(cam.Position.X, cam.Position.Y, cam.Position.Z);
        ps.Rotation = new Mat3(
            new Vec3(cam.Forward.X, cam.Forward.Y, cam.Forward.Z),
            new Vec3(cam.Right.X, cam.Right.Y, cam.Right.Z),
            new Vec3(cam.Up.X, cam.Up.Y, cam.Up.Z)).Orthonormalize();
        section.Dirty = true;

        byte[] bytes = Document.SaveToBytes();

        ps.Position = oldPos;
        ps.Rotation = oldRot;
        section.Dirty = oldDirty;
        return bytes;
    }
}
