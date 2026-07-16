using System;
using System.IO;

namespace Ged.Core.Playtest;

/// <summary>Which game executable is configured — stock RF.exe or the Alpine Faction launcher.</summary>
public enum GameKind
{
    /// <summary>Stock <c>RF.exe</c> (or a RED/DashFaction-style build): single-player <c>-level</c> only.</summary>
    StockRf,

    /// <summary>The Alpine Faction launcher: supports <c>-level</c> and multiplayer <c>-levelm</c>.</summary>
    AlpineLauncher,
}

/// <summary>Single-player or multiplayer playtest.</summary>
public enum PlaytestMode
{
    Single,
    Multi,
}

/// <summary>
/// A fully resolved playtest launch: the executable, its argument string, the
/// working directory, and the staged .rfl destination path. Built by
/// <see cref="GameLauncher.BuildCommand"/> without touching the filesystem or
/// launching anything, so it is unit-testable.
/// </summary>
public sealed record PlaytestCommand(
    string ExePath,
    string Arguments,
    string WorkingDirectory,
    string DestinationRflPath,
    PlaytestMode Mode,
    bool FromCamera);

/// <summary>
/// Builds (and, separately, stages files for) a Red Faction playtest launch. Pure
/// command-line + path construction — the App owns the actual Process.Start and the
/// build/save flow. Play Level stages the level's .rfl into
/// <c>&lt;install&gt;\user_maps\&lt;single|multi&gt;\</c> and launches the exe with
/// <c>-level</c> (single) or <c>-levelm</c> (multi, Alpine launcher only).
/// </summary>
public static class GameLauncher
{
    /// <summary>Detects the game kind from the executable file name.</summary>
    public static GameKind DetectKind(string exePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return name.Contains("alpine") || name.Contains("launcher") ? GameKind.AlpineLauncher : GameKind.StockRf;
    }

    /// <summary>Only the Alpine launcher can start a dedicated multiplayer level (-levelm).</summary>
    public static bool SupportsMulti(GameKind kind) => kind == GameKind.AlpineLauncher;

    /// <summary>The staging directory for a mode: <c>&lt;install&gt;\user_maps\&lt;single|multi&gt;</c>.</summary>
    public static string DestinationDir(string installDir, PlaytestMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        return Path.Combine(installDir, "user_maps", mode == PlaytestMode.Multi ? "multi" : "single");
    }

    /// <summary>
    /// Builds the launch command. Throws <see cref="NotSupportedException"/> when a
    /// multiplayer launch is requested for a stock exe (callers should gate on
    /// <see cref="SupportsMulti"/> first).
    /// </summary>
    public static PlaytestCommand BuildCommand(
        string exePath, string installDir, string levelFileName,
        PlaytestMode mode, bool fromCamera, string? extraArgs = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentException.ThrowIfNullOrEmpty(levelFileName);

        GameKind kind = DetectKind(exePath);
        if (mode == PlaytestMode.Multi && !SupportsMulti(kind))
        {
            throw new NotSupportedException("Multiplayer playtest (-levelm) requires the Alpine Faction launcher.");
        }

        string levelName = EnsureRflExtension(Path.GetFileName(levelFileName));
        string destPath = Path.Combine(DestinationDir(installDir, mode), levelName);

        string flag = mode == PlaytestMode.Multi ? "-levelm" : "-level";
        string args = $"{flag} {Quote(levelName)}";
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            args += " " + extraArgs.Trim();
        }

        return new PlaytestCommand(exePath, args, installDir, destPath, mode, fromCamera);
    }

    /// <summary>
    /// Writes <paramref name="rflBytes"/> to the command's staging path (creating the
    /// directory), returning the written path. This is the product behaviour; tests
    /// stage into temp directories, never a real install.
    /// </summary>
    public static string StageLevel(PlaytestCommand command, byte[] rflBytes)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(rflBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(command.DestinationRflPath)!);
        File.WriteAllBytes(command.DestinationRflPath, rflBytes);
        return command.DestinationRflPath;
    }

    /// <summary>A composed OS process invocation: the program to run and its argument string.</summary>
    public readonly record struct LaunchProcess(string FileName, string Arguments);

    /// <summary>
    /// Composes the actual OS process to spawn for a playtest, applying the optional launch
    /// <paramref name="template"/> (e.g. <c>wine {exe} {args}</c> on Linux). With a blank
    /// template the game exe is launched directly (the Windows default). Otherwise the
    /// template's first whitespace-delimited token is the wrapper program (the returned
    /// <see cref="LaunchProcess.FileName"/>) and the remainder — with <c>{exe}</c> replaced by
    /// the quoted exe path and <c>{args}</c> by the level arguments — is the argument string.
    /// Pure string composition, so it is unit-testable without launching anything.
    /// </summary>
    public static LaunchProcess ComposeProcess(PlaytestCommand command, string? template)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ComposeProcess(command.ExePath, command.Arguments, template);
    }

    /// <summary>Composes the OS process from an exe + args + optional launch template (see the record overload).</summary>
    public static LaunchProcess ComposeProcess(string exePath, string arguments, string? template)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        arguments ??= string.Empty;

        if (string.IsNullOrWhiteSpace(template))
        {
            return new LaunchProcess(exePath, arguments);
        }

        string tmpl = template.Trim();
        int firstSpace = tmpl.IndexOf(' ');
        string fileName = firstSpace < 0 ? tmpl : tmpl[..firstSpace];
        string argTemplate = firstSpace < 0 ? string.Empty : tmpl[(firstSpace + 1)..];

        string composedArgs = argTemplate
            .Replace("{exe}", Quote(exePath))
            .Replace("{args}", arguments)
            .Trim();

        return new LaunchProcess(fileName, composedArgs);
    }

    /// <summary>The Alpine Faction launcher filename — the one executable GED play-tests through.</summary>
    public const string AlpineLauncherFileName = "AlpineFactionLauncher.exe";

    /// <summary>
    /// First-run guess of the Alpine Faction launcher beside an install dir (the pure
    /// filesystem probe). GED launches play-tests exclusively through
    /// <c>AlpineFactionLauncher.exe</c> (everyone runs Alpine Faction), so only the launcher
    /// is auto-detected — a stock <c>RF.exe</c> is never adopted automatically. Returns null
    /// when the launcher is not present beside the install.
    /// </summary>
    public static string? GuessExe(string installDir) => GuessExe(installDir, null);

    /// <summary>
    /// Locates the Alpine Faction launcher, consulting the Windows registry FIRST (via
    /// <paramref name="registryReader"/>, when supplied) and only then the beside-the-install
    /// filesystem probe. The launcher registers the <c>af://</c> protocol, so its actual
    /// install path is read from the protocol's shell-open command — this finds it even when
    /// it lives outside the RF install directory. Passing a null reader reproduces the pure
    /// filesystem behaviour (used by tests and non-Windows callers).
    /// </summary>
    public static string? GuessExe(string? installDir, IAlpineProtocolReader? registryReader)
    {
        // 1. The af:// protocol registration (Windows registry) — probed before the
        //    beside-the-install check so the actually-installed launcher path wins.
        if (registryReader is not null && DetectFromRegistry(registryReader) is { } fromRegistry)
        {
            return fromRegistry;
        }

        // 2. Beside the install dir.
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        string path = Path.Combine(installDir, AlpineLauncherFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Resolves the launcher from the <c>af://</c> protocol registration: reads the shell-open
    /// command via <paramref name="reader"/>, parses out the executable path, and returns it
    /// only when it points at a real file. Returns null when the key is absent, unparseable, or
    /// the exe no longer exists.
    /// </summary>
    public static string? DetectFromRegistry(IAlpineProtocolReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? exe = ParseLauncherPath(reader.ReadAfShellOpenCommand());
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Extracts the executable path from an <c>af\shell\open\command</c> value. Handles the
    /// quoted form (<c>"C:\…\AlpineFactionLauncher.exe" "%1"</c> — the launcher's own
    /// registration, and the only form that survives a path with spaces) and the bare unquoted
    /// form (<c>C:\…\Launcher.exe %1</c>). Pure string parsing — no filesystem or registry
    /// access — so it is unit-testable with a fake command string. Returns null for a blank or
    /// malformed value.
    /// </summary>
    public static string? ParseLauncherPath(string? shellOpenCommand)
    {
        if (string.IsNullOrWhiteSpace(shellOpenCommand))
        {
            return null;
        }

        string cmd = shellOpenCommand.Trim();
        string exe;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0)
            {
                return null; // an unterminated quote is malformed
            }

            exe = cmd[1..end];
        }
        else
        {
            int space = cmd.IndexOf(' ');
            exe = space < 0 ? cmd : cmd[..space];
        }

        exe = exe.Trim();
        return exe.Length == 0 ? null : exe;
    }

    private static string EnsureRflExtension(string name) =>
        name.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) ? name : name + ".rfl";

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
