using System;
using System.IO;
using Ged.Core.IO.Tex;
using Ged.Core.IO.Vpp;

namespace Ged.Core.Assets;

/// <summary>
/// A dedicated, read-only icon source for the stock Alpine object icons that ship only in
/// <c>alpinefaction.vpp</c> (<c>Icon_AFNote.tga</c>, <c>Icon_AFCorona.tga</c>,
/// <c>Icon_EAX.tga</c>, <c>Icon_Event.tga</c>). That packfile sits beside
/// <c>AlpineFactionLauncher.exe</c>, which may live OUTSIDE the RF install directory — so the
/// main game VFS won't contain it, and those icons would otherwise fall back to GED's drawn
/// glyphs even with "use original object icons" on. This opens the vpp directly (given the
/// configured launcher path) so the original-icons atlas can composite them. Icons are never
/// shipped by GED — they are read from the user's own Alpine install.
/// </summary>
public sealed class AlpineIconSource : IDisposable
{
    /// <summary>The Alpine packfile name probed beside the launcher.</summary>
    public const string AlpineVppName = "alpinefaction.vpp";

    private readonly VppArchive _archive;

    private AlpineIconSource(VppArchive archive) => _archive = archive;

    /// <summary>
    /// Opens <c>alpinefaction.vpp</c> beside the given launcher — <paramref name="launcherPathOrDir"/>
    /// may be the <c>AlpineFactionLauncher.exe</c> path or the directory containing it. Returns null
    /// when the path is blank, the vpp is absent, or it cannot be opened (never throws).
    /// </summary>
    public static AlpineIconSource? BesideLauncher(string? launcherPathOrDir)
    {
        string? vpp = LocateVpp(launcherPathOrDir);
        if (vpp is null)
        {
            return null;
        }

        try
        {
            return new AlpineIconSource(VppArchive.Open(vpp));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The <c>alpinefaction.vpp</c> path beside a launcher exe/dir, or null when absent.</summary>
    public static string? LocateVpp(string? launcherPathOrDir)
    {
        if (string.IsNullOrWhiteSpace(launcherPathOrDir))
        {
            return null;
        }

        string dir = Directory.Exists(launcherPathOrDir)
            ? launcherPathOrDir
            : Path.GetDirectoryName(launcherPathOrDir) ?? string.Empty;
        if (dir.Length == 0)
        {
            return null;
        }

        string vpp = Path.Combine(dir, AlpineVppName);
        return File.Exists(vpp) ? vpp : null;
    }

    /// <summary>Decodes a named icon (e.g. <c>Icon_AFNote.tga</c>) to RGBA, or null when absent / undecodable.</summary>
    public TextureImage? Load(string iconFileName)
    {
        if (string.IsNullOrEmpty(iconFileName))
        {
            return null;
        }

        try
        {
            byte[]? data = _archive.Find(iconFileName) is { } entry ? _archive.Read(entry) : null;
            return data is null ? null : TextureDecoder.Decode(iconFileName, data).Primary;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _archive.Dispose();
}
