using System.Text;
using System.Text.RegularExpressions;

namespace Ged.Core.Tables;

/// <summary>
/// Derives the script-facing field key from an inspector label: lower-case;
/// apostrophes dropped; every <c>[...]</c> / <c>(...)</c> group dropped; every other run of
/// non-alphanumerics becomes <c>_</c>; leading/trailing <c>_</c> trimmed. <c>Script Name</c> →
/// <c>script_name</c>, <c>Size / Range</c> → <c>size_range</c>, <c>Don't Hum</c> → <c>dont_hum</c>,
/// <c>Hold Open [Alpine]</c> → <c>hold_open</c>. A schema whose derived keys would collide sets an
/// explicit <see cref="InspectorField.Key"/> instead.
/// </summary>
public static class InspectorKeys
{
    private static readonly Regex Brackets = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Parens = new(@"\([^()]*\)", RegexOptions.Compiled);

    /// <summary>The key derived from <paramref name="label"/>.</summary>
    public static string FromLabel(string label)
    {
        string s = (label ?? string.Empty).ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
        s = StripGroups(s, Brackets);
        s = StripGroups(s, Parens);

        var sb = new StringBuilder(s.Length);
        bool pendingUnderscore = false;
        foreach (char c in s)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                if (pendingUnderscore && sb.Length > 0)
                {
                    sb.Append('_');
                }

                pendingUnderscore = false;
                sb.Append(c);
            }
            else
            {
                pendingUnderscore = true;
            }
        }

        return sb.ToString();
    }

    private static string StripGroups(string s, Regex group)
    {
        string prev;
        do
        {
            prev = s;
            s = group.Replace(s, " ");
        }
        while (!ReferenceEquals(prev, s) && prev != s);

        return s;
    }
}
