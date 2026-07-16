using System.Text;

namespace Ged.Core.Tables;

/// <summary>
/// Parser for Red Faction <c>.tbl</c> plaintext tables: <c>#Section</c> markers
/// (nestable, closed by <c>#End</c>), <c>$Key: value</c> record lines, and
/// <c>+sub: value</c> continuation lines, with <c>//</c> line and <c>/* */</c>
/// block comments. Records are split by the recurrence of the first <c>$</c>-key
/// seen in each section (e.g. <c>$Class Name</c>, <c>$Name</c>, <c>$Event Name</c>).
/// </summary>
public static class TblParser
{
    /// <summary>Parses table bytes (decoded as Latin-1 for lossless byte handling).</summary>
    public static TblDocument Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Parse(Encoding.Latin1.GetString(data));
    }

    public static TblDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var records = new List<TblRecord>();
        var stack = new List<string>();
        TblRecord? current = null;
        string? startKey = null;

        foreach (string logical in RemoveBlockComments(text).Split('\n'))
        {
            string line = StripLineComment(logical).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                if (current is not null)
                {
                    records.Add(current);
                    current = null;
                }

                string name = line[1..].Trim();
                if (name.Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                else
                {
                    stack.Add(name);
                }

                startKey = null;
                continue;
            }

            if (line[0] == '$')
            {
                (string key, string value) = SplitKeyValue(line);
                startKey ??= key;

                if (current is null || key.Equals(startKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (current is not null)
                    {
                        records.Add(current);
                    }

                    current = new TblRecord(stack.ToArray());
                }

                current.AddEntry(key, value);
            }
            else if (line[0] == '+' && current is not null)
            {
                (string key, string value) = SplitKeyValue(line);
                current.AddEntry(key, value);
            }
        }

        if (current is not null)
        {
            records.Add(current);
        }

        return new TblDocument(records);
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        string key;
        string value;
        if (colon >= 0)
        {
            key = line[..colon];
            value = line[(colon + 1)..].Trim();
        }
        else
        {
            // No colon: the key is the first whitespace-delimited token, the rest is the value.
            int ws = line.IndexOfAny(new[] { ' ', '\t' });
            if (ws >= 0)
            {
                key = line[..ws];
                value = line[ws..].Trim();
            }
            else
            {
                key = line;
                value = string.Empty;
            }
        }

        return (NormalizeKey(key), value);
    }

    /// <summary>Trims the leading <c>$</c>/<c>+</c> sigil, surrounding whitespace, and trailing colon.</summary>
    private static string NormalizeKey(string key)
    {
        string k = key.Trim();
        if (k.Length > 0 && (k[0] == '$' || k[0] == '+'))
        {
            k = k[1..];
        }

        k = k.Trim();
        if (k.EndsWith(':'))
        {
            k = k[..^1].TrimEnd();
        }

        return k;
    }

    /// <summary>Removes a trailing <c>//</c> comment, honouring double-quoted strings.</summary>
    private static string StripLineComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && c == '/' && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string RemoveBlockComments(string text)
    {
        // Normalise line endings first so downstream splitting on '\n' is clean.
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int open = normalized.IndexOf("/*", StringComparison.Ordinal);
        if (open < 0)
        {
            return normalized;
        }

        var sb = new StringBuilder(normalized.Length);
        int pos = 0;
        while (open >= 0)
        {
            sb.Append(normalized, pos, open - pos);
            int close = normalized.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                pos = normalized.Length;
                break;
            }

            // Preserve newlines inside the block so line numbers/structure stay aligned.
            for (int i = open; i < close + 2; i++)
            {
                if (normalized[i] == '\n')
                {
                    sb.Append('\n');
                }
            }

            pos = close + 2;
            open = normalized.IndexOf("/*", pos, StringComparison.Ordinal);
        }

        sb.Append(normalized, pos, normalized.Length - pos);
        return sb.ToString();
    }
}
