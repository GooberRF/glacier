using System.Globalization;

namespace Ged.Core.Tables;

/// <summary>A parsed <c>.tbl</c> file: an ordered list of records.</summary>
public sealed class TblDocument
{
    public TblDocument(IReadOnlyList<TblRecord> records)
    {
        Records = records;
    }

    public IReadOnlyList<TblRecord> Records { get; }

    /// <summary>Records whose innermost section name matches (case-insensitive).</summary>
    public IEnumerable<TblRecord> InSection(string section) =>
        Records.Where(r => r.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A single key/value pair from a table record (duplicates preserved in order).</summary>
public sealed class TblEntry
{
    public TblEntry(string key, string value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>Normalised key: no <c>$</c>/<c>+</c> sigil, no trailing colon.</summary>
    public string Key { get; }

    /// <summary>Raw value text after the colon (may be a quoted string, number, list, or XSTR).</summary>
    public string Value { get; }
}

/// <summary>
/// One record within a table (e.g. a single item, clutter, entity, or event),
/// tagged with the section stack that was active when it began.
/// </summary>
public sealed class TblRecord
{
    private readonly List<TblEntry> _entries = new();

    public TblRecord(IReadOnlyList<string> sectionPath)
    {
        SectionPath = sectionPath;
    }

    /// <summary>The full section stack, outermost first (e.g. ["Events", "AI_Actions"]).</summary>
    public IReadOnlyList<string> SectionPath { get; }

    /// <summary>The innermost section name, or empty when a record is at top level.</summary>
    public string Section => SectionPath.Count > 0 ? SectionPath[^1] : string.Empty;

    public IReadOnlyList<TblEntry> Entries => _entries;

    internal void AddEntry(string key, string value) => _entries.Add(new TblEntry(key, value));

    public bool Has(string key) =>
        _entries.Any(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>First raw value for a key, or null.</summary>
    public string? GetRaw(string key) =>
        _entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>All raw values for a key, in order.</summary>
    public IEnumerable<string> GetAllRaw(string key) =>
        _entries.Where(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value);

    /// <summary>First value for a key, unquoted (and un-XSTR'd), or null.</summary>
    public string? GetString(string key)
    {
        string? raw = GetRaw(key);
        return raw is null ? null : TblValue.AsText(raw);
    }

    /// <summary>First value for a key parsed as an integer, or null.</summary>
    public int? GetInt(string key)
    {
        string? raw = GetRaw(key);
        return raw is not null && TblValue.TryInt(raw, out int v) ? v : null;
    }

    /// <summary>First value for a key parsed as a float, or null.</summary>
    public float? GetFloat(string key)
    {
        string? raw = GetRaw(key);
        return raw is not null && TblValue.TryFloat(raw, out float v) ? v : null;
    }

    /// <summary>First value for a key parsed as a token list (<c>( … )</c> or <c>{ … }</c>).</summary>
    public IReadOnlyList<string> GetList(string key)
    {
        string? raw = GetRaw(key);
        return raw is null ? Array.Empty<string>() : TblValue.ParseList(raw);
    }
}

/// <summary>Helpers for interpreting raw <c>.tbl</c> value text.</summary>
public static class TblValue
{
    /// <summary>Strips surrounding double quotes if present.</summary>
    public static string Unquote(string value)
    {
        string v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            return v[1..^1];
        }

        return v;
    }

    /// <summary>Returns the display text of a value: XSTR payload if present, else the unquoted string.</summary>
    public static string AsText(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("XSTR", StringComparison.OrdinalIgnoreCase))
        {
            int q = v.IndexOf('"', StringComparison.Ordinal);
            int q2 = v.LastIndexOf('"');
            if (q >= 0 && q2 > q)
            {
                return v[(q + 1)..q2];
            }
        }

        return Unquote(v);
    }

    public static bool TryInt(string value, out int result) =>
        int.TryParse(FirstToken(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    public static bool TryFloat(string value, out float result) =>
        float.TryParse(FirstToken(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Tokenizes a list value: strips one layer of <c>()</c> or <c>{}</c> brackets and
    /// returns whitespace-separated tokens, unquoting quoted ones.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string value)
    {
        string v = value.Trim();
        if (v.Length >= 2 && ((v[0] == '(' && v[^1] == ')') || (v[0] == '{' && v[^1] == '}')))
        {
            v = v[1..^1];
        }

        return Tokenize(v);
    }

    /// <summary>Whitespace tokenizer that keeps double-quoted substrings intact (and unquotes them).</summary>
    public static IReadOnlyList<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i]))
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (value[i] == '"')
            {
                int end = value.IndexOf('"', i + 1);
                if (end < 0)
                {
                    tokens.Add(value[(i + 1)..]);
                    break;
                }

                tokens.Add(value[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < value.Length && !char.IsWhiteSpace(value[i]))
                {
                    i++;
                }

                tokens.Add(value[start..i]);
            }
        }

        return tokens;
    }

    private static string FirstToken(string value)
    {
        IReadOnlyList<string> t = Tokenize(value.Trim());
        return t.Count > 0 ? t[0] : value.Trim();
    }
}
