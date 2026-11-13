using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The Lua side of provenance tags: validation of keys/values coming from Lua, and the
/// batch writes behind <c>h:tag</c> / <c>h:untag</c>, the query forms and the <c>tags = {…}</c>
/// creation option. Every write is ONE <see cref="GedObjectMetadataService.SetBlocks"/> command;
/// a UID whose tags do not change is not rewritten (so a malformed payload survives an untag).
/// </summary>
internal static class ScriptTags
{
    /// <summary>Validates a tag key from Lua.</summary>
    public static string Key(string api, string? key)
    {
        if (ObjectTags.KeyProblem(key) is { } problem)
        {
            throw new ScriptApiException($"{api}: {problem}", "For example \"kit.part\" or \"ai.session\".");
        }

        return key!;
    }

    /// <summary>Validates a tag value from Lua: strings only (numbers/booleans are rejected, never coerced).</summary>
    public static string Value(string api, string key, object? value)
    {
        if (value is not string s)
        {
            throw new ScriptApiException($"{api}: the value of tag '{key}' must be a string (got {ScriptArgs.LuaTypeOf(value)}).", "Use tostring(v).");
        }

        if (ObjectTags.ValueProblem(s) is { } problem)
        {
            throw new ScriptApiException($"{api}: tag '{key}': {problem}");
        }

        return s;
    }

    /// <summary>Parses a <c>{key = "value", …}</c> table (validated; an empty table is allowed).</summary>
    public static Dictionary<string, string> Table(string api, ScriptArgs? table)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (table is null)
        {
            return tags;
        }

        if (table.Items.Count > 0)
        {
            throw new ScriptApiException($"{api}: tags must be a table of key = \"value\" pairs, not a list.", "For example tags = {[\"kit.part\"] = \"floor\"}.");
        }

        foreach (KeyValuePair<string, object?> kv in table.Named.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            tags[Key(api, kv.Key)] = Value(api, kv.Key, kv.Value);
        }

        if (tags.Count > ObjectTags.MaxTagsPerUid)
        {
            throw new ScriptApiException($"{api}: at most {ObjectTags.MaxTagsPerUid} tags per object (got {tags.Count}).");
        }

        return tags;
    }

    /// <summary>The Lua view of a tag set: a table <c>{key = value}</c>.</summary>
    public static Dictionary<string, object?> ToLua(IReadOnlyDictionary<string, string> tags)
    {
        var t = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in tags.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            t[kv.Key] = kv.Value;
        }

        return t;
    }

    /// <summary>Sets (adds or replaces) <paramref name="tags"/> on every UID as one undo step.</summary>
    public static void Merge(ScriptContext ctx, string api, IReadOnlyCollection<int> uids, IReadOnlyDictionary<string, string> tags)
    {
        if (tags.Count == 0 || uids.Count == 0)
        {
            return;
        }

        // A few UIDs (one kit element at a time): read just theirs; many: decode the chunk once.
        IReadOnlyDictionary<int, Dictionary<string, string>>? all = uids.Count > 8 ? ctx.Metadata.AllTags() : null;
        var writes = new List<(int, GedMetadataType, byte[]?)>();
        foreach (int uid in uids.Distinct())
        {
            Dictionary<string, string> current = all is null
                ? ctx.Metadata.Tags(uid)
                : all.TryGetValue(uid, out Dictionary<string, string>? t) ? t : new Dictionary<string, string>(StringComparer.Ordinal);
            var next = new Dictionary<string, string>(current, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in tags)
            {
                next[kv.Key] = kv.Value;
            }

            if (next.Count > ObjectTags.MaxTagsPerUid)
            {
                throw new ScriptApiException($"{api}: UID {uid} would carry {next.Count} tags (at most {ObjectTags.MaxTagsPerUid}).",
                    "Remove tags you no longer need with untag.");
            }

            if (next.Count != current.Count || next.Any(kv => !current.TryGetValue(kv.Key, out string? v) || v != kv.Value))
            {
                writes.Add((uid, GedMetadataType.Tags, ObjectTags.Encode(next)));
            }
        }

        ctx.Metadata.SetBlocks(writes, writes.Count == 1 ? "Set tag" : "Set tags");
    }

    /// <summary>Removes <paramref name="key"/> from every UID that has it, as one undo step (no-op when absent).</summary>
    public static void Remove(ScriptContext ctx, IReadOnlyCollection<int> uids, string key)
    {
        if (uids.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<int, Dictionary<string, string>> all = ctx.Metadata.AllTags();
        var writes = new List<(int, GedMetadataType, byte[]?)>();
        foreach (int uid in uids.Distinct())
        {
            if (all.TryGetValue(uid, out Dictionary<string, string>? current) && current.ContainsKey(key))
            {
                var next = new Dictionary<string, string>(current, StringComparer.Ordinal);
                next.Remove(key);
                writes.Add((uid, GedMetadataType.Tags, ObjectTags.Encode(next)));
            }
        }

        ctx.Metadata.SetBlocks(writes, "Remove tag");
    }

    /// <summary>True when <paramref name="tags"/> has <paramref name="key"/> (and, when given, exactly <paramref name="value"/>).</summary>
    public static bool Matches(IReadOnlyDictionary<int, Dictionary<string, string>> all, int uid, string key, string? value) =>
        all.TryGetValue(uid, out Dictionary<string, string>? tags) &&
        tags.TryGetValue(key, out string? v) &&
        (value is null || string.Equals(v, value, StringComparison.Ordinal));
}
