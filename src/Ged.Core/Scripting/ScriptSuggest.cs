using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Scripting;

/// <summary>
/// Deterministic "did you mean" suggestions for unknown names (kinds, classes, fields, shapes,
/// table keys, options). Matching is case-insensitive and ranked: exact, then prefix matches
/// (either string starting with the other), then substring matches (either containing the
/// other), then Levenshtein distance ≤ <c>max(2, input.Length / 3)</c>; a case-insensitive exact
/// match is returned on its own. Ties break
/// ordinal-ignore-case, so the same input always yields the same list.
/// </summary>
public static class ScriptSuggest
{
    /// <summary>Above this many candidates a hint no longer lists them all.</summary>
    public const int MaxListed = 40;

    /// <summary>The nearest candidates to <paramref name="input"/>, best first (at most <paramref name="max"/>).</summary>
    public static IReadOnlyList<string> Nearest(string input, IEnumerable<string> candidates, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string needle = (input ?? string.Empty).Trim();
        if (needle.Length == 0 || max <= 0)
        {
            return Array.Empty<string>();
        }

        int maxDistance = Math.Max(2, needle.Length / 3);
        var ranked = new List<(int Tier, int Score, string Name)>();
        foreach (string c in candidates.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(c, needle, StringComparison.OrdinalIgnoreCase))
            {
                ranked.Add((0, 0, c));
            }
            else if (c.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ||
                     (c.Length >= 3 && needle.StartsWith(c, StringComparison.OrdinalIgnoreCase)))
            {
                ranked.Add((1, Math.Abs(c.Length - needle.Length), c));
            }
            else if (c.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                     (c.Length >= 3 && needle.Contains(c, StringComparison.OrdinalIgnoreCase)))
            {
                ranked.Add((2, Math.Abs(c.Length - needle.Length), c));
            }
            else
            {
                int d = Levenshtein(needle.ToLowerInvariant(), c.ToLowerInvariant());
                if (d <= maxDistance)
                {
                    ranked.Add((3, d, c));
                }
            }
        }

        // A case-insensitive exact match is the answer on its own (e.g. "Pos" → "pos").
        if (ranked.Any(r => r.Tier == 0))
        {
            ranked.RemoveAll(r => r.Tier != 0);
        }

        return ranked
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Score)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(r => r.Name)
            .ToList();
    }

    /// <summary>
    /// A hint line for an unknown <paramref name="input"/>: <c>"Did you mean: a, b, c?"</c>; when
    /// nothing is near and there are ≤ <see cref="MaxListed"/> candidates, <c>"Valid &lt;noun&gt;s: …"</c>;
    /// otherwise <c>"Use catalog.classes("&lt;kind&gt;") to list valid &lt;noun&gt;s."</c> when
    /// <paramref name="kind"/> is given, else a count of the valid names.
    /// </summary>
    public static string Hint(string input, IEnumerable<string> candidates, string noun, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        List<string> all = candidates.Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<string> near = Nearest(input, all);
        if (near.Count > 0)
        {
            return $"Did you mean: {string.Join(", ", near)}?";
        }

        if (all.Count <= MaxListed)
        {
            return $"Valid {Plural(noun)}: {string.Join(", ", all)}.";
        }

        return kind is { Length: > 0 }
            ? $"Use catalog.classes(\"{kind}\") to list valid {Plural(noun)}."
            : $"There are {all.Count} valid {Plural(noun)}; check the spelling.";
    }

    /// <summary>A simple English plural for a hint noun (class → classes, shape → shapes).</summary>
    internal static string Plural(string noun) =>
        noun.EndsWith('s') || noun.EndsWith('x') || noun.EndsWith("ch", StringComparison.Ordinal) || noun.EndsWith("sh", StringComparison.Ordinal)
            ? noun + "es"
            : noun + "s";

    /// <summary>Classic edit distance (insert / delete / substitute, cost 1 each).</summary>
    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
