using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Modules;

/// <summary>
/// The texture → style-family mapping (the one table): a texture name is matched against <see cref="NameRules"/>
/// (first hit wins), then its stock browser category (<see cref="CategoryRules"/>), then its name prefix
/// (<see cref="PrefixRules"/>); anything else is <c>generic</c>. A module's styles blend its own textures (area
/// weighted) with its level's dominant families and an optional manifest hint.
/// </summary>
public static class ModuleStyles
{
    /// <summary>Name fragments (case-insensitive substrings) and the style they imply, in priority order.</summary>
    public static readonly IReadOnlyList<(string Fragment, string Style)> NameRules = new[]
    {
        ("canyon", "canyon"), ("cliff", "canyon"), ("sand", "canyon"), ("desert", "canyon"), ("redsoyna", "canyon"),
        ("mars", "mars_base"), ("airlock", "mars_base"), ("dome", "mars_base"), ("ss_", "mars_base"), ("extwall", "mars_base"), ("outerwall", "mars_base"),
        ("admin", "admin"), ("admn", "admin"), ("carpet", "admin"), ("office", "admin"), ("executive", "admin"), ("poster", "admin"),
        ("bathroom", "admin"), ("corp_", "admin"), ("chinese", "admin"),
        ("med", "lab"), ("_lab", "lab"), ("lab_", "lab"), ("hospital", "lab"), ("nurse", "lab"), ("science", "lab"), ("res_", "lab"), ("ward", "lab"),
        ("tech", "tech"), ("contrl", "tech"), ("control", "tech"), ("computer", "tech"), ("comp_", "tech"), ("console", "tech"), ("screen", "tech"),
        ("monitor", "tech"), ("circuit", "tech"), ("chrome", "tech"), ("server", "tech"),
        ("mine", "mine"), ("miner", "mine"), ("tunnel", "mine"), ("crusher", "mine"), ("timber", "mine"),
        ("bunker", "bunker"), ("concrete", "bunker"), ("poured", "bunker"),
        ("cave", "cave"), ("lava", "cave"), ("moss", "cave"), ("boulder", "cave"), ("dirt", "cave"), ("icy", "cave"),
        ("girder", "industrial"), ("pipe", "industrial"), ("grate", "industrial"), ("cargo", "industrial"), ("crate", "industrial"),
        ("rust", "industrial"), ("pump", "industrial"), ("turbine", "industrial"), ("vent", "industrial"), ("hazard", "industrial"),
        ("walkway", "industrial"), ("rivet", "industrial"), ("merc", "industrial"),
    };

    /// <summary>Stock browser categories (substring of the category name) and their style.</summary>
    public static readonly IReadOnlyList<(string Fragment, string Style)> CategoryRules = new[]
    {
        ("Rock", "cave"), ("Cement", "bunker"), ("Metal", "industrial"), ("Tech", "tech"), ("Pipes", "industrial"),
        ("Supports", "industrial"), ("Grating", "industrial"), ("Crates", "industrial"), ("Signs - Admin", "admin"),
        ("Signs - Industrial", "industrial"), ("Doors", "industrial"), ("Trim", "industrial"),
    };

    /// <summary>Texture name prefixes (before the first '_') and their style.</summary>
    public static readonly IReadOnlyList<(string Prefix, string Style)> PrefixRules = new[]
    {
        ("rck", "cave"), ("rock", "cave"), ("cem", "bunker"), ("mtl", "industrial"), ("pls", "lab"), ("tech", "tech"),
        ("sld", "generic"), ("gls", "generic"),
    };

    /// <summary>The name family of a texture: its lower-case prefix before the first '_', '-', or digit (e.g. <c>mtl</c>).</summary>
    public static string Family(string texture)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(texture ?? string.Empty).ToLowerInvariant();
        int end = 0;
        while (end < n.Length && char.IsLetter(n[end]))
        {
            end++;
        }

        return end == 0 ? "other" : n[..end];
    }

    /// <summary>The style one texture implies (see the rule tables).</summary>
    public static string StyleOf(string texture, string? category)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(texture ?? string.Empty).ToLowerInvariant();
        foreach ((string frag, string style) in NameRules)
        {
            if (n.Contains(frag, StringComparison.Ordinal))
            {
                return style;
            }
        }

        if (category is not null)
        {
            foreach ((string frag, string style) in CategoryRules)
            {
                if (category.Contains(frag, StringComparison.OrdinalIgnoreCase))
                {
                    return style;
                }
            }
        }

        string fam = Family(texture ?? string.Empty);
        foreach ((string prefix, string style) in PrefixRules)
        {
            if (fam == prefix)
            {
                return style;
            }
        }

        return "generic";
    }

    /// <summary>
    /// Ordered style list: own shares (weight 1) + level shares × <paramref name="levelWeight"/> + hint ×
    /// <paramref name="hintWeight"/>; styles at or above <paramref name="minShare"/> of the blend, strongest first
    /// (ties by name); <c>generic</c> only when nothing else qualifies.
    /// </summary>
    public static List<string> Blend(IReadOnlyDictionary<string, double> own, IReadOnlyDictionary<string, double>? level, string? hint,
        double levelWeight, double hintWeight, double minShare)
    {
        var w = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(IReadOnlyDictionary<string, double> shares, double weight)
        {
            double total = shares.Values.Sum();
            if (total <= 0)
            {
                return;
            }

            foreach ((string k, double v) in shares)
            {
                w[k] = w.GetValueOrDefault(k) + (weight * v / total);
            }
        }

        Add(own, 1.0);
        if (level is not null)
        {
            Add(level, levelWeight);
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            w[hint!] = w.GetValueOrDefault(hint!) + hintWeight;
        }

        double sum = w.Values.Sum();
        if (sum <= 0)
        {
            return new List<string> { "generic" };
        }

        var styles = w.Where(kv => kv.Key != "generic" && kv.Value / sum >= minShare - 1e-9)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToList();
        if (styles.Count == 0)
        {
            styles.Add("generic");
        }

        return styles;
    }
}
