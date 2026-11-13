using System;
using System.Collections.Generic;
using System.IO;

namespace Ged.Core.Assets;

/// <summary>Where a mounted asset file comes from.</summary>
public enum AssetOrigin
{
    /// <summary>A retail Red Faction packfile: every player has it.</summary>
    Stock,

    /// <summary><c>alpinefaction.vpp</c>: every Alpine Faction player has it.</summary>
    Alpine,

    /// <summary>Any other packfile (a mod or community pack).</summary>
    Packfile,

    /// <summary>A loose file in a folder (user_maps, the install root, an extra directory).</summary>
    Loose,
}

/// <summary>Where one texture comes from (see <see cref="StockContent.Describe"/>).</summary>
/// <param name="Name">The name asked for.</param>
/// <param name="ResolvedName">The file the editor shows (the supercede chain's winner).</param>
/// <param name="Stock">A retail packfile contains the texture (under any chain name): every player has it.</param>
/// <param name="Origin">Where the winning file comes from (the first source in mount order).</param>
/// <param name="Source">The winning source's packfile name or loose folder name (never a full path).</param>
/// <param name="Overridden">Stock, but the winning file is not the retail one.</param>
public sealed record TextureProvenance(string Name, string ResolvedName, bool Stock, AssetOrigin Origin, string Source, bool Overridden);

/// <summary>
/// Stock-content identification for texture provenance (ORGANIC-GEOMETRY-SPEC §2.6). A packfile counts as
/// stock by its FILE NAME alone — a general mechanism; a modified retail packfile is not detected
/// (documented approximation).
/// </summary>
public static class StockContent
{
    /// <summary>The Alpine Faction packfile name (mounted ahead of the base game, <see cref="GameMount"/>).</summary>
    public const string AlpinePackfile = "alpinefaction.vpp";

    /// <summary>
    /// The retail Red Faction 1.20 packfiles. Source: the packfile-name table compiled into the retail
    /// executables — RF.exe and RED.exe both carry, in one string table, <c>audio.vpp, maps_en.vpp,
    /// levels1.vpp, levels2.vpp, levels3.vpp, levelsm.vpp, levelseb.vpp, levelsbg.vpp, maps1.vpp … maps4.vpp,
    /// meshes.vpp, motions.vpp, music.vpp, tables.vpp, ui.vpp</c> (the names the game mounts at start-up;
    /// a stock install ships all of them except the optional <c>levelseb.vpp</c> / <c>levelsbg.vpp</c>).
    /// <c>maps_gr.vpp</c> and <c>maps_fr.vpp</c> are the German and French counterparts of <c>maps_en.vpp</c>
    /// in the localized retail editions (not present in the English binary's table).
    /// </summary>
    public static readonly IReadOnlyList<string> RetailPackfiles = new[]
    {
        "audio.vpp",
        "levels1.vpp",
        "levels2.vpp",
        "levels3.vpp",
        "levelsbg.vpp",
        "levelseb.vpp",
        "levelsm.vpp",
        "maps1.vpp",
        "maps2.vpp",
        "maps3.vpp",
        "maps4.vpp",
        "maps_en.vpp",
        "maps_fr.vpp",
        "maps_gr.vpp",
        "meshes.vpp",
        "motions.vpp",
        "music.vpp",
        "tables.vpp",
        "ui.vpp",
    };

    private static readonly HashSet<string> Retail = new(RetailPackfiles, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="vppFileName"/> (a bare name or a path) is a retail packfile name (case-insensitive).</summary>
    public static bool IsStockPackfile(string vppFileName) =>
        !string.IsNullOrWhiteSpace(vppFileName) && Retail.Contains(Path.GetFileName(vppFileName.Trim()));

    /// <summary>The origin class of a mounted source: stock / Alpine / other packfile by name, else loose.</summary>
    public static AssetOrigin OriginOf(IAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != AssetSourceKind.Packfile)
        {
            return AssetOrigin.Loose;
        }

        string name = Path.GetFileName(source.Description ?? string.Empty);
        return IsStockPackfile(name) ? AssetOrigin.Stock
            : string.Equals(name, AlpinePackfile, StringComparison.OrdinalIgnoreCase) ? AssetOrigin.Alpine
            : AssetOrigin.Packfile;
    }

    /// <summary>
    /// The provenance of texture <paramref name="name"/> in <paramref name="vfs"/>: the winning file through
    /// the supercede chain (<see cref="AssetVfs.ResolveTexture"/>), its source and origin, and whether a
    /// retail packfile holds the texture under any of its chain names (every player has it). Null when no
    /// file resolves.
    /// </summary>
    public static TextureProvenance? Describe(AssetVfs vfs, string name)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        if (string.IsNullOrWhiteSpace(name) || vfs.ResolveTexture(name) is not { } resolved || vfs.FindSource(resolved) is not { } winner)
        {
            return null;
        }

        string baseName = IO.Tex.SupercedeChain.GetBaseName(name);
        bool stock = false;
        foreach (IAssetSource s in vfs.Sources)
        {
            if (OriginOf(s) != AssetOrigin.Stock)
            {
                continue;
            }

            foreach (string ext in IO.Tex.SupercedeChain.Extensions)
            {
                if (s.Contains(baseName + ext))
                {
                    stock = true;
                    break;
                }
            }

            if (stock)
            {
                break;
            }
        }

        AssetOrigin origin = OriginOf(winner);
        return new TextureProvenance(name, resolved, stock, origin, ShortName(winner), stock && origin != AssetOrigin.Stock);
    }

    /// <summary>The Lua spelling of an origin: "stock", "alpine", "packfile" or "loose".</summary>
    public static string LuaName(AssetOrigin origin) => origin switch
    {
        AssetOrigin.Stock => "stock",
        AssetOrigin.Alpine => "alpine",
        AssetOrigin.Packfile => "packfile",
        _ => "loose",
    };

    /// <summary>
    /// A source's short display name: the packfile's file name, or the loose folder's own name — never a
    /// full path.
    /// </summary>
    public static string ShortName(IAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string d = (source.Description ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
        int cut = Math.Max(d.LastIndexOf('/'), d.LastIndexOf('\\'));
        return cut >= 0 ? d[(cut + 1)..] : d;
    }
}
