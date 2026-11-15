using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ged.Core.Modules;

/// <summary>One mapper decision about one module (from <c>curation.json</c>).</summary>
/// <param name="Verdict">"keep", "reject", or null (undecided; the entry may still relabel).</param>
/// <param name="Category">A replacement category, or null.</param>
/// <param name="Role">A replacement role, or null.</param>
/// <param name="Styles">A replacement style list (primary first), or null.</param>
/// <param name="Note">The mapper's free-text note, or null.</param>
public sealed record ModuleCurationEntry(string? Verdict, string? Category, string? Role, IReadOnlyList<string>? Styles, string? Note)
{
    /// <summary>True when the entry changes the category, role or styles.</summary>
    public bool Relabels => Category is not null || Role is not null || Styles is not null;
}

/// <summary>Counts of the curation applied to a library.</summary>
/// <param name="Kept">Modules the mapper kept.</param>
/// <param name="Rejected">Modules the mapper rejected.</param>
/// <param name="Relabelled">Modules whose category, role or styles were changed.</param>
/// <param name="Undecided">Modules with no verdict.</param>
/// <param name="UnknownIds">Curation entries naming ids the library does not hold.</param>
/// <param name="Ignored">Curation entries ignored for a bad value.</param>
public sealed record ModuleCurationCounts(int Kept, int Rejected, int Relabelled, int Undecided, int UnknownIds, int Ignored);

/// <summary>
/// The mapper's curation of the module library: <c>library/modules/curation.json</c> (schema
/// <see cref="Schema"/>), <c>{"schema": …, "modules": {"&lt;id&gt;": {"verdict": "keep"|"reject", "category"?,
/// "role"?, "styles"?: […], "note"?}}}</c>. It is the mapper's file: harvests never write it, and the library applies it
/// on every load — rejected modules leave every selection unless asked for, relabels replace the harvested category,
/// role and styles, and kept modules rank <see cref="KeepBonus"/> higher. Reading is tolerant: a bad entry is ignored
/// with a warning and an unreadable file is ignored with a warning; nothing throws.
/// </summary>
public static class ModuleCuration
{
    /// <summary>The curation schema id.</summary>
    public const string Schema = "glacier.module-curation/1";

    /// <summary>The curation file name inside the <c>library/modules</c> folder.</summary>
    public const string FileName = "curation.json";

    /// <summary>The selection-score bonus of a kept module (harvest scores lie in 0–1).</summary>
    public const double KeepBonus = 0.5;

    /// <summary>The verdicts a curation entry may give.</summary>
    public static readonly IReadOnlyList<string> Verdicts = new[] { "keep", "reject" };

    /// <summary>Reads <c>curation.json</c> in <paramref name="libraryDir"/>; a missing file is an empty curation. Never throws.</summary>
    public static IReadOnlyDictionary<string, ModuleCurationEntry> Load(string? libraryDir, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (string.IsNullOrEmpty(libraryDir))
        {
            return new Dictionary<string, ModuleCurationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        string path = Path.Combine(libraryDir, FileName);
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, ModuleCurationEntry>(StringComparer.OrdinalIgnoreCase);
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{FileName}: could not be read ({ex.Message}); no curation applied.");
            return new Dictionary<string, ModuleCurationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        return Parse(text, warnings);
    }

    /// <summary>Parses curation JSON; bad entries are skipped with a warning. Never throws.</summary>
    public static IReadOnlyDictionary<string, ModuleCurationEntry> Parse(string json, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var result = new Dictionary<string, ModuleCurationEntry>(StringComparer.OrdinalIgnoreCase);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? string.Empty, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            warnings.Add($"{FileName}: not valid JSON ({ex.Message}); no curation applied.");
            return result;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{FileName}: the top level must be an object; no curation applied.");
                return result;
            }

            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema)
            {
                warnings.Add($"{FileName}: expected \"schema\": \"{Schema}\"; reading it anyway.");
            }

            if (!root.TryGetProperty("modules", out JsonElement modules) || modules.ValueKind != JsonValueKind.Object)
            {
                if (root.TryGetProperty("modules", out _))
                {
                    warnings.Add($"{FileName}: \"modules\" must be an object keyed by module id; no curation applied.");
                }

                return result;
            }

            foreach (JsonProperty p in modules.EnumerateObject())
            {
                string id = p.Name.Trim();
                if (id.Length == 0)
                {
                    warnings.Add($"{FileName}: an entry has an empty id (ignored).");
                    continue;
                }

                if (TryEntry(p.Value, out ModuleCurationEntry? entry, out string? why))
                {
                    result[id] = entry!;
                }
                else
                {
                    warnings.Add($"{FileName}: '{id}' {why} (entry ignored).");
                }
            }
        }

        return result;
    }

    private static bool TryEntry(JsonElement e, out ModuleCurationEntry? entry, out string? why)
    {
        entry = null;
        why = null;
        if (e.ValueKind != JsonValueKind.Object)
        {
            why = "is not an object";
            return false;
        }

        if (!Text(e, "verdict", out string? verdict, ref why) || !Text(e, "category", out string? category, ref why)
            || !Text(e, "role", out string? role, ref why) || !Text(e, "note", out string? note, ref why))
        {
            return false;
        }

        verdict = verdict?.ToLowerInvariant();
        category = category?.ToLowerInvariant();
        role = role?.ToLowerInvariant();
        if (verdict is not null && !Verdicts.Contains(verdict, StringComparer.Ordinal))
        {
            why = $"has an unknown verdict '{verdict}' (keep or reject)";
            return false;
        }

        if (category is not null && !ModuleVocabulary.Categories.Contains(category, StringComparer.Ordinal))
        {
            why = $"has an unknown category '{category}'";
            return false;
        }

        if (role is not null && !ModuleVocabulary.Roles.Contains(role, StringComparer.Ordinal))
        {
            why = $"has an unknown role '{role}'";
            return false;
        }

        List<string>? styles = null;
        if (e.TryGetProperty("styles", out JsonElement st) && st.ValueKind != JsonValueKind.Null)
        {
            if (st.ValueKind != JsonValueKind.Array)
            {
                why = "has \"styles\" that is not a list";
                return false;
            }

            styles = new List<string>();
            foreach (JsonElement s in st.EnumerateArray())
            {
                string? name = s.ValueKind == JsonValueKind.String ? s.GetString()?.Trim().ToLowerInvariant() : null;
                if (name is null || !ModuleVocabulary.Styles.Contains(name, StringComparer.Ordinal))
                {
                    why = $"has an unknown style '{(s.ValueKind == JsonValueKind.String ? s.GetString() : s.GetRawText())}'";
                    return false;
                }

                if (!styles.Contains(name, StringComparer.Ordinal))
                {
                    styles.Add(name);
                }
            }

            if (styles.Count == 0)
            {
                styles = null; // an empty list keeps the harvested styles
            }
        }

        entry = new ModuleCurationEntry(verdict, category, role, styles, string.IsNullOrWhiteSpace(note) ? null : note);
        return true;

        static bool Text(JsonElement o, string name, out string? value, ref string? why)
        {
            value = null;
            if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (v.ValueKind != JsonValueKind.String)
            {
                why = $"has \"{name}\" that is not a string";
                return false;
            }

            string t = v.GetString()!.Trim();
            value = t.Length == 0 ? null : t;
            return true;
        }
    }
}
