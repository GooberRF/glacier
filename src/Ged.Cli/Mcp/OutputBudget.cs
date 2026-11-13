using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Automation;
using Ged.Automation.Tools;

namespace Ged.Cli.Mcp;

/// <summary>
/// Shapes a tool result to fit an agent's context (MCP-SERVER-SPEC §6) STRUCTURALLY: lists are cut and
/// marked (<c>truncated</c>, <c>total</c>, <c>returned</c>, a <c>hint</c> naming the narrowing argument),
/// never a JSON text cut mid-value. Per-tool rules run first (script log, hole positions, lint findings,
/// render object lists); a generic pass then halves the largest remaining list until the result fits.
/// </summary>
internal static class OutputBudget
{
    /// <summary>A run_lua log keeps its first and last lines.</summary>
    public const int LogHead = 40;

    public const int LogTail = 160;

    /// <summary>The most characters of run_lua log kept.</summary>
    public const int LogChars = 8_000;

    /// <summary>check_holes positions listed.</summary>
    public const int HolePositions = 100;

    /// <summary>lint findings listed.</summary>
    public const int LintFindings = 100;

    /// <summary>render object glyphs listed with the mapping.</summary>
    public const int RenderObjects = 100;

    /// <summary>The serialized length of a node (compact JSON, the text block's form).</summary>
    public static int Size(JsonNode? node) => node?.ToJsonString(AutomationJson.Options).Length ?? 4;

    /// <summary>Applies the per-tool rules, then fits <paramref name="result"/> into <paramref name="budget"/> characters.</summary>
    public static JsonNode? Shape(string tool, JsonNode? result, int budget)
    {
        if (result is not JsonObject o)
        {
            return result;
        }

        switch (tool)
        {
            case "run_lua":
                ShapeLog(o);
                break;
            case "check_holes":
                CapList(o, "positions", HolePositions, "count", "Fix the listed leaks first, then call check_holes again.");
                break;
            case "lint":
                CapList(o, "findings", LintFindings, null, "Raise min_severity to warning or error to see fewer findings.");
                break;
            case "render_plan":
            case "render_view":
                CapList(o, "objects", RenderObjects, null, "Frame a smaller region, room or uid to list every glyph.");
                break;
        }

        Fit(o, budget, Hint(tool));
        return o;
    }

    /// <summary>The argument that narrows a tool's output, for the generic hint.</summary>
    public static string Hint(string tool) => tool switch
    {
        "query" => "Use limit / offset to page, or narrow with kind, class, name, tag or region.",
        "lint" => "Raise min_severity to see fewer findings.",
        "level_summary" => "Use detail brief or normal, or query / inspect for specific objects.",
        "diff_since" => "Lower max_changes, or inspect UIDs individually.",
        "list_textures" => "Narrow with filter, or lower limit.",
        "list_classes" => "Narrow with filter.",
        _ => "Ask for less (narrower arguments), or raise max_chars where the tool accepts it.",
    };

    private static void ShapeLog(JsonObject o)
    {
        if (o["log"] is not JsonArray log)
        {
            return;
        }

        int dropped = 0;
        if (log.Count > LogHead + LogTail)
        {
            var kept = new JsonArray();
            for (int i = 0; i < log.Count; i++)
            {
                if (i < LogHead || i >= log.Count - LogTail)
                {
                    kept.Add(log[i]?.DeepClone());
                }
                else
                {
                    dropped++;
                }
            }

            o["log"] = log = kept;
        }

        // Character cap: drop lines from the middle of what is left until the log fits.
        while (Size(log) > LogChars && log.Count > 1)
        {
            log.RemoveAt(Math.Min(LogHead, log.Count / 2));
            dropped++;
        }

        if (dropped > 0)
        {
            o["logTruncated"] = dropped;
        }
    }

    private static void CapList(JsonObject o, string name, int cap, string? totalField, string hint)
    {
        if (o[name] is not JsonArray list || list.Count <= cap)
        {
            return;
        }

        int total = totalField is not null && o[totalField] is JsonValue tv && tv.TryGetValue(out int t) ? t : list.Count;
        while (list.Count > cap)
        {
            list.RemoveAt(list.Count - 1);
        }

        o["truncated"] = true;
        o[name + "Total"] = total;
        o[name + "Returned"] = list.Count;
        o["hint"] = hint;
    }

    /// <summary>Halves the largest list until <paramref name="o"/> fits; records what was cut.</summary>
    internal static void Fit(JsonObject o, int budget, string hint)
    {
        var cut = new List<string>();
        int target = Math.Max(1000, budget - 600); // room for the budget note and any notes added after shaping
        for (int round = 0; round < 64 && Size(o) > (round == 0 ? budget : target); round++)
        {
            (JsonArray? largest, string path) = Largest(o, string.Empty);
            if (largest is null || largest.Count == 0)
            {
                break;
            }

            int before = largest.Count;
            int keep = before / 2;
            while (largest.Count > keep)
            {
                largest.RemoveAt(largest.Count - 1);
            }

            cut.Add($"{path}: {before} -> {keep}");
        }

        if (cut.Count == 0)
        {
            return;
        }

        // Merge successive halvings of the same list into one entry (first count -> last count).
        var merged = new Dictionary<string, (string First, string Last)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (string c in cut)
        {
            int colon = c.IndexOf(':', StringComparison.Ordinal);
            string key = c[..colon];
            string[] counts = c[(colon + 2)..].Split(" -> ");
            if (merged.TryGetValue(key, out (string First, string Last) v))
            {
                merged[key] = (v.First, counts[1]);
            }
            else
            {
                merged[key] = (counts[0], counts[1]);
                order.Add(key);
            }
        }

        o["truncated"] = true;
        o["budget"] = new JsonObject
        {
            ["limit"] = budget,
            ["cut"] = new JsonArray(order.Select(k => (JsonNode?)$"{k}: {merged[k].First} -> {merged[k].Last} items").ToArray()),
            ["hint"] = hint,
        };
    }

    private static (JsonArray? List, string Path) Largest(JsonNode node, string path)
    {
        JsonArray? best = null;
        string bestPath = string.Empty;
        int bestSize = 0;

        void Consider(JsonArray a, string p)
        {
            int s = Size(a);
            if (a.Count > 0 && s > bestSize)
            {
                (best, bestPath, bestSize) = (a, p, s);
            }
        }

        void Walk(JsonNode? n, string p)
        {
            switch (n)
            {
                case JsonArray a:
                    Consider(a, p);
                    foreach (JsonNode? item in a)
                    {
                        if (item is JsonObject)
                        {
                            Walk(item, p + "[]");
                        }
                    }

                    break;
                case JsonObject obj:
                    foreach (KeyValuePair<string, JsonNode?> kv in obj)
                    {
                        Walk(kv.Value, p.Length == 0 ? kv.Key : p + "." + kv.Key);
                    }

                    break;
            }
        }

        Walk(node, path);
        return (best, bestPath);
    }

    /// <summary>The budget a call asked for (<c>max_chars</c>), clamped to the hard cap; else the default.</summary>
    public static int BudgetOf(JsonObject? arguments)
    {
        if (arguments?["max_chars"] is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d))
        {
            return (int)Math.Clamp(d, 2000, ToolCatalog.MaxResultBudget);
        }

        return ToolCatalog.DefaultResultBudget;
    }
}
