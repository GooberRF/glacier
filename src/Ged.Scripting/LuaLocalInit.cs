using System.Collections.Generic;
using System.Text;

namespace Ged.Scripting;

/// <summary>
/// Works around a MoonSharp 2.0 defect: a <c>local</c> declared without a value inside a generic <c>for</c> loop does
/// not start as nil, it keeps whatever the register last held (another local, a table being iterated). Standard Lua
/// gives <c>nil</c>, and scripts rely on it: <c>local found</c> before a search loop is the idiom. With the stale value a
/// clustering loop like <c>if found then found[#found + 1] = f else cl[#cl + 1] = {f} end</c> appends to the table
/// it is iterating and never ends (a round-3 recreation build ran for hours on it). Every chunk is rewritten before it
/// is compiled so each <c>local a, b</c> without an initializer reads <c>local a, b = nil, nil</c>, which MoonSharp
/// honours; the text is added on the same line, so line numbers in errors do not move. Strings, long strings and
/// comments are skipped, and <c>local function</c> is left alone.
/// </summary>
public static class LuaLocalInit
{
    /// <summary>The source with every value-less <c>local</c> declaration given explicit nils.</summary>
    public static string Apply(string source)
    {
        if (string.IsNullOrEmpty(source) || !source.Contains("local", System.StringComparison.Ordinal))
        {
            return source ?? string.Empty;
        }

        List<Token> tokens = Lex(source);
        var inserts = new List<(int At, int Count)>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Name || tokens[i].Text != "local" || i + 1 >= tokens.Count || tokens[i + 1].Kind != Kind.Name || tokens[i + 1].Text == "function")
            {
                continue;
            }

            // local NAME {',' NAME}: a value-less declaration unless '=' follows the name list.
            int j = i + 1;
            int names = 1;
            while (j + 2 < tokens.Count && tokens[j + 1].Kind == Kind.Symbol && tokens[j + 1].Text == "," && tokens[j + 2].Kind == Kind.Name)
            {
                j += 2;
                names++;
            }

            bool assigned = j + 1 < tokens.Count && tokens[j + 1].Kind == Kind.Symbol && tokens[j + 1].Text == "=";
            if (!assigned)
            {
                inserts.Add((tokens[j].End, names));
            }

            i = j;
        }

        if (inserts.Count == 0)
        {
            return source;
        }

        var sb = new StringBuilder(source.Length + (inserts.Count * 12));
        int last = 0;
        foreach ((int at, int count) in inserts)
        {
            sb.Append(source, last, at - last);
            sb.Append(" = nil");
            for (int k = 1; k < count; k++)
            {
                sb.Append(", nil");
            }

            last = at;
        }

        sb.Append(source, last, source.Length - last);
        return sb.ToString();
    }

    private enum Kind
    {
        Name,
        Symbol,
        Other,
    }

    private readonly record struct Token(Kind Kind, string Text, int End);

    private static List<Token> Lex(string s)
    {
        var tokens = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i += 2;
                if (LongBracket(s, i) is int level)
                {
                    i = SkipLong(s, i, level);
                }
                else
                {
                    while (i < n && s[i] != '\n')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c == '"' || c == '\'')
            {
                int start = i++;
                while (i < n && s[i] != c && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < n)
                    {
                        i++;
                    }

                    i++;
                }

                i = System.Math.Min(n, i + 1);
                tokens.Add(new Token(Kind.Other, string.Empty, i));
                _ = start;
                continue;
            }

            if (c == '[' && LongBracket(s, i) is int lv)
            {
                i = SkipLong(s, i, lv);
                tokens.Add(new Token(Kind.Other, string.Empty, i));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Name, s.Substring(start, i - start), i));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || ((s[i] == '+' || s[i] == '-') && (s[i - 1] == 'e' || s[i - 1] == 'E' || s[i - 1] == 'p' || s[i - 1] == 'P'))))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Other, string.Empty, i));
                continue;
            }

            // '==' is not '=' (a comparison never follows a name list, but keep the tokens honest).
            if (c == '=' && i + 1 < n && s[i + 1] == '=')
            {
                i += 2;
                tokens.Add(new Token(Kind.Symbol, "==", i));
                continue;
            }

            i++;
            tokens.Add(new Token(Kind.Symbol, c.ToString(), i));
        }

        return tokens;
    }

    /// <summary>The level of a long bracket opening at <paramref name="i"/> ("[[", "[=[", ...), or null.</summary>
    private static int? LongBracket(string s, int i)
    {
        if (i >= s.Length || s[i] != '[')
        {
            return null;
        }

        int j = i + 1, level = 0;
        while (j < s.Length && s[j] == '=')
        {
            level++;
            j++;
        }

        return j < s.Length && s[j] == '[' ? level : null;
    }

    private static int SkipLong(string s, int i, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int end = s.IndexOf(close, i + level + 2, System.StringComparison.Ordinal);
        return end < 0 ? s.Length : end + close.Length;
    }
}
