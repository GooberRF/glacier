using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ged.Core.Analysis;

/// <summary>
/// One level's fingerprint (<see cref="FingerprintSchema.Schema"/>): a JSON document with the identity block and
/// one object per metric group. Deterministic: two computations over the same bytes and options serialize to the
/// same text (no run timestamps, numbers rounded to 3 decimals, lists sorted ordinally).
/// </summary>
public sealed class LevelFingerprint
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LevelFingerprint(JsonObject json)
    {
        Json = json ?? throw new ArgumentNullException(nameof(json));
    }

    /// <summary>The fingerprint document.</summary>
    public JsonObject Json { get; }

    /// <summary>The level status (<see cref="FingerprintSchema.Status"/>).</summary>
    public string Status => Json["status"]?.GetValue<string>() ?? FingerprintSchema.Status.Ok;

    /// <summary>The level's SHA-256 (lower-case hex).</summary>
    public string Sha256 => Json["identity"]?["sha256"]?.GetValue<string>() ?? string.Empty;

    /// <summary>The canonical text: indented JSON with LF line endings and a trailing newline.</summary>
    public string ToJsonText() => Serialize(Json);

    /// <summary>Serializes any fingerprint-shaped node the canonical way (indented, LF, trailing newline).</summary>
    public static string Serialize(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Parses fingerprint text (as written by <see cref="ToJsonText"/>).</summary>
    public static LevelFingerprint Parse(string text) =>
        new(JsonNode.Parse(text) as JsonObject ?? throw new JsonException("A fingerprint must be a JSON object."));
}
