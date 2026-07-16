namespace Ged.Core.Playtest;

/// <summary>
/// The headless settings state behind the first-run wizard's install + game-executable
/// steps. Choosing an RF install directory auto-guesses the Alpine Faction launcher
/// (<c>AlpineFactionLauncher.exe</c>, the one executable GED play-tests through — see
/// <see cref="GameLauncher.GuessExe"/>); the user can override it with an explicit pick,
/// after which changing the install dir no longer clobbers their choice. Exposed as a
/// UI-free model so the guessed-vs-picked outcomes are unit-testable without the Avalonia
/// wizard window.
/// </summary>
public sealed class FirstRunConfig
{
    private readonly IAlpineProtocolReader? _protocolReader;
    private bool _exePickedManually;

    /// <param name="initialInstallDir">The pre-filled install directory (may be null/empty).</param>
    /// <param name="initialGameExe">An already-configured game exe; when set, treated as a manual pick.</param>
    /// <param name="protocolReader">
    /// Optional reader for the <c>af://</c> protocol registration; when supplied, the launcher
    /// is detected from the Windows registry before the beside-the-install probe (and even when
    /// no install dir is set). Null (the default, used by tests) keeps the pure filesystem guess.
    /// </param>
    public FirstRunConfig(
        string? initialInstallDir = null, string? initialGameExe = null,
        IAlpineProtocolReader? protocolReader = null)
    {
        _protocolReader = protocolReader;
        if (!string.IsNullOrWhiteSpace(initialGameExe))
        {
            GameExePath = initialGameExe;
            _exePickedManually = true;
        }

        SetInstallDir(initialInstallDir);
    }

    /// <summary>The chosen RF install directory (null when blank).</summary>
    public string? RfInstallDir { get; private set; }

    /// <summary>The resolved game executable path (guessed from the install dir, or a manual pick); null when none.</summary>
    public string? GameExePath { get; private set; }

    /// <summary>True once the user has explicitly picked a game exe (auto-guessing then stops).</summary>
    public bool GameExePickedManually => _exePickedManually;

    /// <summary>Sets the install dir; re-guesses the game exe unless one was picked manually.</summary>
    public void SetInstallDir(string? dir)
    {
        RfInstallDir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        if (!_exePickedManually)
        {
            // Registry-first (when a reader is supplied) so the launcher is found even with no
            // install dir set; GuessExe handles a null install dir gracefully.
            GameExePath = GameLauncher.GuessExe(RfInstallDir, _protocolReader);
        }
    }

    /// <summary>Sets the game exe explicitly (a manual pick); it will no longer be auto-guessed.</summary>
    public void SetGameExe(string? path)
    {
        GameExePath = string.IsNullOrWhiteSpace(path) ? null : path;
        _exePickedManually = true;
    }
}
