using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ged.Automation.Tools;

/// <summary>Generates the JSON Schema of a tool's arguments and its behaviour annotations from the catalog.</summary>
public static class ToolSchema
{
    /// <summary>The <c>inputSchema</c> of <paramref name="spec"/>: an object schema with every argument, its type, range and doc.</summary>
    public static JsonObject For(ToolSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var properties = new JsonObject();
        foreach (ToolArg a in spec.Args)
        {
            var p = new JsonObject { ["type"] = a.Type, ["description"] = a.Doc };
            if (a.Enum is { } values)
            {
                p["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray());
            }

            if (a.Min is double min)
            {
                p["minimum"] = min;
            }

            if (a.Max is double max)
            {
                p["maximum"] = max;
            }

            if (a.MaxLength is int len)
            {
                p["maxLength"] = len;
            }

            if (a.Type == "array")
            {
                p["items"] = new JsonObject { ["type"] = a.ItemsType ?? "number" };
                if (a.Items is int n)
                {
                    p["minItems"] = n;
                    p["maxItems"] = n;
                }
            }

            if (a.Default is { } d)
            {
                p["default"] = d switch
                {
                    bool b => b,
                    int i => i,
                    double x => x,
                    string s => s,
                    _ => Convert.ToString(d, CultureInfo.InvariantCulture),
                };
            }

            properties[a.Name] = p;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        string[] required = spec.Args.Where(a => a.Required).Select(a => a.Name).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray());
        }

        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>The behaviour hints of a tool (MCP tool annotations): read-only, destructive, idempotent, open-world.</summary>
    public static (bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld) Annotations(ToolSpec spec) =>
        (spec.Safety == ToolSafety.Read, spec.Safety == ToolSafety.Destructive, spec.Safety == ToolSafety.Read, false);

    /// <summary>A human title for the tool ("render_plan" → "Render plan").</summary>
    public static string Title(ToolSpec spec)
    {
        string t = spec.Name.Replace('_', ' ');
        return char.ToUpperInvariant(t[0]) + t[1..];
    }
}
