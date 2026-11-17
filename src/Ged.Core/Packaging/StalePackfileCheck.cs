using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.IO.Vpp;

namespace Ged.Core.Packaging;

/// <summary>
/// The save-time check for a packfile that would shadow the level just written: the level's own <c>&lt;name&gt;.vpp</c>
/// beside it, older than the level, still carrying an older copy of the level file.
/// <para>
/// Red Faction resolves a bare file name through its packfiles before the loose files on its search paths. RF.exe's
/// <c>File::find</c> (0x00523CE0) tries, in order, the loose-file table that <c>file_add_path</c> (0x00514070 →
/// 0x00523B40) fills only with each path's registered extensions — RF.exe 0x004B1810 registers <c>user_maps\single</c> and
/// <c>user_maps\multi</c> for <c>*.vpp</c> only, so no loose <c>.rfl</c> is in it — then the packfiles (0x005237D0 →
/// <c>vpackfile_find</c> 0x0052C220, which Alpine Faction replaces with its own lookup table:
/// game_patch/misc/vpackfile.cpp:555-567, 840; the user_maps packfiles are mounted at :786-789 and :873-881), and only
/// then the on-disk search paths. So when <c>g.vpp</c> holds a <c>g.rfl</c>, the game plays that copy, not the loose
/// file the editor just saved.
/// </para>
/// </summary>
public static class StalePackfileCheck
{
    /// <summary>
    /// The level's own packfile — <c>&lt;level name&gt;.vpp</c> beside <paramref name="levelPath"/> (the file Create Level
    /// Packfile writes) — when it is older than the level file and its entry of the level's name holds other bytes than
    /// the level file. Empty otherwise, or when the folder or the packfile cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Find(string levelPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(levelPath);
        var stale = new List<string>();
        try
        {
            string full = Path.GetFullPath(levelPath);
            string pack = PackfileBuildPlan.LevelFolderOutputPath(full);
            if (!File.Exists(full) || !File.Exists(pack) || File.GetLastWriteTimeUtc(pack) >= File.GetLastWriteTimeUtc(full))
            {
                return stale;
            }

            if (HoldsOtherCopy(pack, Path.GetFileName(full), File.ReadAllBytes(full)))
            {
                stale.Add(pack);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A file we cannot read has nothing to warn about.
        }

        return stale;
    }

    /// <summary>The one save-log note for <see cref="Find"/>'s result, or null when no packfile shadows the level.</summary>
    public static string? NoteFor(string levelPath)
    {
        IReadOnlyList<string> stale = Find(levelPath);
        if (stale.Count == 0)
        {
            return null;
        }

        string levelFile = Path.GetFileName(levelPath);
        string names = string.Join(", ", stale.Select(Path.GetFileName));
        string holds = stale.Count == 1 ? "holds an older copy" : "hold older copies";
        string it = stale.Count == 1 ? "it" : "them";
        return $"{names} in this folder {holds} of {levelFile}. Red Faction loads a level from a packfile before the loose "
            + $"file, so the game will play that copy, not this save: rebuild {it} with Create Level Packfile, or delete {it}.";
    }

    // True when the packfile carries an entry of the level's name whose bytes differ from the level file (a packfile
    // rebuilt from this very save is not stale even if its timestamp says so).
    private static bool HoldsOtherCopy(string pack, string entry, byte[] levelBytes)
    {
        try
        {
            using VppArchive vpp = VppArchive.Open(pack);
            return vpp.Find(entry) is { } e && !vpp.Read(e).AsSpan().SequenceEqual(levelBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VppFormatException or InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }
}
