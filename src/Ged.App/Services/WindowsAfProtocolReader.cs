using System;
using Ged.Core.Playtest;

namespace Ged.App.Services;

/// <summary>
/// The live reader for the Alpine Faction launcher's <c>af://</c> protocol registration:
/// the default value of <c>HKEY_CLASSES_ROOT\af\shell\open\command</c>. Windows-only — on
/// any other platform (or when the key is absent / unreadable) it returns null, so the
/// launcher resolution falls back to the beside-the-install probe. The launcher-path parse
/// and detection logic lives in <see cref="GameLauncher"/> behind
/// <see cref="IAlpineProtocolReader"/> so it stays unit-testable with a fake command string.
/// </summary>
internal sealed class WindowsAfProtocolReader : IAlpineProtocolReader
{
    /// <summary>The shared reader instance wired into the wizard / settings guess paths.</summary>
    public static readonly WindowsAfProtocolReader Instance = new();

    public string? ReadAfShellOpenCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"af\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch (Exception)
        {
            // A missing/locked key, or a registry provider that throws, must never block
            // startup or the wizard — silently fall back to the filesystem probe.
            return null;
        }
    }
}
