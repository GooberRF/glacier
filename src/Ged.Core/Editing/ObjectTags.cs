using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ged.Core.Editing;

/// <summary>
/// Provenance tags (string → string) stored per UID as a <see cref="Model.GedMetadataType.Tags"/>
/// block in the <c>ged_object_metadata</c> chunk. The payload is canonical UTF-8 JSON — a single
/// object, keys sorted ordinal, no whitespace, no BOM, the default (minimal, non-relaxed) escaping of
/// <see cref="Utf8JsonWriter"/> — so the same tags always serialize to the same bytes. A level that
/// carries no tags carries no block (an empty tag set encodes to null, which removes the block).
/// </summary>
public static class ObjectTags
{
    /// <summary>The most tags one UID may carry.</summary>
    public const int MaxTagsPerUid = 32;

    /// <summary>The longest tag value, in UTF-16 characters.</summary>
    public const int MaxValueLength = 256;

    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="key"/> is a valid tag key (<c>^[a-z0-9][a-z0-9_.-]{0,63}$</c>).</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern.IsMatch(key);

    /// <summary>Why <paramref name="key"/> is not a valid tag key, or null when it is.</summary>
    public static string? KeyProblem(string? key) => IsValidKey(key)
        ? null
        : $"Tag key '{key}' is invalid: use 1-64 characters of a-z, 0-9, '_', '.', '-', starting with a letter or digit.";

    /// <summary>Why <paramref name="value"/> is not a valid tag value, or null when it is.</summary>
    public static string? ValueProblem(string? value)
    {
        if (value is null)
        {
            return "Tag values must be strings.";
        }

        if (value.Length > MaxValueLength)
        {
            return $"Tag values may be at most {MaxValueLength} characters (got {value.Length}).";
        }

        return value.Any(char.IsControl) ? "Tag values may not contain control characters." : null;
    }

    /// <summary>
    /// The canonical payload of <paramref name="tags"/>, or null when there are none (so the block is
    /// dropped). Keys are written in ordinal order regardless of the dictionary's order.
    /// </summary>
    public static byte[]? Encode(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (KeyValuePair<string, string> kv in tags.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WriteString(kv.Key, kv.Value);
            }

            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decodes a tag payload. A malformed payload, or one that is not a JSON object of strings,
    /// decodes to an empty set (the caller never rewrites it unless a tag is actually set, so unknown
    /// data survives).
    /// </summary>
    public static Dictionary<string, string> Decode(byte[]? payload) =>
        TryDecode(payload, out Dictionary<string, string> tags) ? tags : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Decodes a tag payload; false (and an empty set) when it is malformed.</summary>
    public static bool TryDecode(byte[]? payload, out Dictionary<string, string> tags)
    {
        tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload is null || payload.Length == 0)
        {
            return payload is not null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                result[p.Name] = p.Value.GetString() ?? string.Empty;
            }

            tags = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
