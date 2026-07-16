namespace Ged.Core.Playtest;

/// <summary>
/// Reads the Windows registration of the <c>af://</c> protocol handler that the Alpine
/// Faction launcher installs. Abstracted behind an interface so the launcher-detection
/// logic is unit-testable with a fake command string (the live Windows-registry reader is
/// injected in the app; a fake is injected in tests). Non-Windows implementations return
/// null.
/// </summary>
public interface IAlpineProtocolReader
{
    /// <summary>
    /// The default value of <c>HKEY_CLASSES_ROOT\af\shell\open\command</c> — the shell
    /// "open" command line the launcher registered (e.g.
    /// <c>"C:\…\AlpineFactionLauncher.exe" "%1"</c>) — or null when the key is absent or
    /// the platform has no registry.
    /// </summary>
    string? ReadAfShellOpenCommand();
}
