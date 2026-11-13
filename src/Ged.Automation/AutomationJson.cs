using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ged.Automation;

/// <summary>The one JSON configuration every automation surface uses (CLI output and the bridge wire).</summary>
public static class AutomationJson
{
    /// <summary>camelCase names, nulls omitted, enums as camelCase strings, unescaped readable text.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>As <see cref="Options"/>, indented (human-facing <c>--json</c> output).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>Serializes a result object to a JSON node (the form responses carry).</summary>
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>Serializes a response for the wire (single line) or for display (indented).</summary>
    public static string Serialize(AutomationResponse response, bool indented = false) =>
        JsonSerializer.Serialize(new ResponseDto(response), indented ? Indented : Options);

    /// <summary>The wire/CLI shape of a response.</summary>
    internal sealed class ResponseDto
    {
        public ResponseDto(AutomationResponse r)
        {
            Ok = r.Ok;
            Command = r.Command;
            Result = r.Result?.DeepClone();
            Error = r.Error;
        }

        public bool Ok { get; }

        public string Command { get; }

        public JsonNode? Result { get; }

        public AutomationError? Error { get; }
    }
}
