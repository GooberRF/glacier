using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ged.Core.Analysis;

/// <summary>The gametype verdict for a level (CORPUS-NORMS-SPEC §3.1).</summary>
/// <param name="Value">sp, dm, ctf, an Alpine mode id, or mp_other.</param>
/// <param name="Source">prefix, objects, or levelinfo.</param>
/// <param name="Conflict">The filename prefix and the objects disagree (excluded from per-gametype buckets).</param>
public sealed record GametypeVerdict(string Value, string Source, bool Conflict);

/// <summary>Identity helpers: author normalisation, gametype classification, version-series keys.</summary>
public static partial class LevelIdentity
{
    /// <summary>
    /// Multiplayer filename prefixes → gametype id. dm / ctf / tdm are the stock modes; koth, dc, rev, run, esc and
    /// pay are Alpine Faction's level-name prefixes (alpinefaction game_patch/multi/gametype.h:20-30,
    /// <c>multi_rfl_prefixes</c>); bag, tbag, pit, wo and gg are the remaining Alpine mode abbreviations
    /// (alpinefaction docs/gametype-rules.md:148-160). Behaviour reference only; no code is taken from Alpine.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GametypePrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dm"] = "dm",
        ["ctf"] = "ctf",
        ["tdm"] = "tdm",
        ["teamdm"] = "tdm",
        ["koth"] = "koth",
        ["dc"] = "dc",
        ["rev"] = "rev",
        ["run"] = "run",
        ["esc"] = "esc",
        ["pay"] = "pay",
        ["bag"] = "bag",
        ["tbag"] = "tbag",
        ["pit"] = "pit",
        ["wo"] = "wo",
        ["gg"] = "gg",
    };

    /// <summary>Windows-1252 code points for bytes 0x80–0x9F (the strings were decoded as Latin-1).</summary>
    private static readonly char[] Cp1252High =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex ClanTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Re-reads a Latin-1-decoded level string as Windows-1252 (RED wrote ANSI text).</summary>
    public static string FromWindows1252(string latin1)
    {
        ArgumentNullException.ThrowIfNull(latin1);
        var sb = new StringBuilder(latin1.Length);
        foreach (char c in latin1)
        {
            sb.Append(c >= '\u0080' && c <= '\u009F' ? Cp1252High[c - 0x80] : c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The normalised author: Windows-1252 decode, <c>[clan]</c>/<c>{clan}</c> tags removed, whitespace collapsed,
    /// lower-cased, surrounding punctuation stripped. Empty when nothing remains.
    /// </summary>
    public static string NormalizeAuthor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = FromWindows1252(raw);
        s = ClanTag().Replace(s, " ");
        s = Whitespace().Replace(s, " ").Trim().ToLowerInvariant();
        int start = 0, end = s.Length;
        while (start < end && IsTrimmable(s[start]))
        {
            start++;
        }

        while (end > start && IsTrimmable(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    private static bool IsTrimmable(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    /// <summary>The canonical author: an alias-table hit on the normalised string, else the normalised string.</summary>
    public static string CanonicalAuthor(string? raw, IReadOnlyDictionary<string, string>? aliases)
    {
        string norm = NormalizeAuthor(raw);
        return aliases is not null && norm.Length > 0 && aliases.TryGetValue(norm, out string? canon) ? canon : norm;
    }

    /// <summary>The filename prefix up to the first digit, '-' or '_' (lower-case, extension removed).</summary>
    public static string FilePrefix(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        int cut = stem.Length;
        for (int i = 0; i < stem.Length; i++)
        {
            char c = stem[i];
            if (char.IsDigit(c) || c == '-' || c == '_' || c == ' ')
            {
                cut = i;
                break;
            }
        }

        return stem[..cut];
    }

    /// <summary>
    /// The gametype: <c>sp</c> for a single-player level (level-info flag); otherwise the filename prefix looked up in
    /// <see cref="GametypePrefixes"/>, cross-checked against the objects (CTF flags present ⇒ ctf). A known prefix
    /// that disagrees with the flags is a conflict; an unknown prefix takes the objects' verdict (ctf) or mp_other.
    /// </summary>
    public static GametypeVerdict ClassifyGametype(bool multiplayer, string? fileName, bool hasCtfFlags, bool hasTeamSpawns)
    {
        if (!multiplayer)
        {
            return new GametypeVerdict("sp", "levelinfo", false);
        }

        string prefix = FilePrefix(fileName);
        if (GametypePrefixes.TryGetValue(prefix, out string? byPrefix))
        {
            bool conflict = (byPrefix == "ctf") != hasCtfFlags;
            return new GametypeVerdict(byPrefix, "prefix", conflict);
        }

        if (hasCtfFlags)
        {
            return new GametypeVerdict("ctf", "objects", false);
        }

        _ = hasTeamSpawns; // recorded separately (spawns.red / spawns.blue); not a gametype on its own
        return new GametypeVerdict("mp_other", "prefix", false);
    }

    [GeneratedRegex(@"(?:[ _\-.]+(?:v\d+[a-z]?|b\d+|beta\d*|alpha\d*|rc\d*|final\d*|fix(?:ed)?\d*|\d+[a-z]?)|v\d+[a-z]?|beta\d*|final\d*|alpha\d*)$")]
    private static partial Regex TrailingVersionToken();

    /// <summary>
    /// The version-series base name: the lower-case file stem with trailing version tokens removed (<c>_v2</c>,
    /// <c>-b3</c>, <c>final</c>, <c>beta</c>, separated digits). Digits glued to letters stay (<c>dm01</c> is its own
    /// level, not version 01 of "dm").
    /// </summary>
    public static string SeriesBaseName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        string s = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Trim();
        for (int guard = 0; guard < 8; guard++)
        {
            string next = TrailingVersionToken().Replace(s, string.Empty).TrimEnd(' ', '_', '-', '.');
            if (next == s || next.Length == 0)
            {
                break;
            }

            s = next;
        }

        return s;
    }

    /// <summary>
    /// The autosave/backup exclusion test on a file or packfile-entry name (case-insensitive): <c>*autosave*</c>,
    /// <c>*.bak.rfl</c>, <c>~*</c>, <c>*~.rfl</c>, <c>* - copy*.rfl</c>, <c>*_backup*</c>.
    /// </summary>
    public static bool IsAutosaveName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        string n = Path.GetFileName(fileName).ToLowerInvariant();
        return n.Contains("autosave", StringComparison.Ordinal)
            || n.EndsWith(".bak.rfl", StringComparison.Ordinal)
            || n.StartsWith('~')
            || n.EndsWith("~.rfl", StringComparison.Ordinal)
            || (n.Contains(" - copy", StringComparison.Ordinal) && n.EndsWith(".rfl", StringComparison.Ordinal))
            || n.Contains("_backup", StringComparison.Ordinal);
    }

    /// <summary>True for item class names that are CTF flags (a flag class naming a team colour).</summary>
    public static bool IsCtfFlagClass(string? className) =>
        className is not null
        && className.Contains("flag", StringComparison.OrdinalIgnoreCase)
        && (className.Contains("red", StringComparison.OrdinalIgnoreCase) || className.Contains("blue", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The texture family: the lower-case name up to the first '_' (stock naming: mtl_, rck_, cem_, …); a name without
    /// a prefix is "(none)" — a family is never a whole texture name.
    /// </summary>
    public static string TextureFamily(string textureName)
    {
        string s = Path.GetFileNameWithoutExtension(textureName ?? string.Empty).ToLowerInvariant();
        int cut = s.IndexOf('_', StringComparison.Ordinal);
        return cut > 0 ? s[..cut] : "(none)";
    }

    /// <summary>A sorted, de-duplicated copy (ordinal).</summary>
    internal static List<string> SortedDistinct(IEnumerable<string> items) =>
        items.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
}
