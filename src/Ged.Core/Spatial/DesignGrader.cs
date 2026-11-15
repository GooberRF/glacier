using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Ged.Core.Editor;

namespace Ged.Core.Spatial;

/// <summary>What to grade a level as.</summary>
public sealed class GradeOptions
{
    /// <summary>dm, ctf, koth, cp, payload, … (null = detected: a Payload_Handler, CTF flags, capture-point handlers, else the file-name prefix, else dm).</summary>
    public string? Gametype { get; init; }

    /// <summary>The intended player count (null = from the spawn count); sets the player class whose targets are reported.</summary>
    public int? Players { get; init; }

    /// <summary>Apply the level-class thresholds (open-air, payload). Off grades every level with the base thresholds, as the exemplar study did.</summary>
    public bool Classes { get; init; } = true;

    /// <summary>Force a class: standard, open_air or payload (null = detected).</summary>
    public string? Class { get; init; }

    /// <summary>Locations listed per failure.</summary>
    public int MaxLocations { get; init; } = 6;

    /// <summary>Include the whole metric table in the result.</summary>
    public bool IncludeMetrics { get; init; }
}

/// <summary>
/// The self-review grader (DESIGN-QUALITY-SPEC §(d), E11): the checks of the embedded data table
/// (<c>design-grader.json</c>) evaluated on a level's <see cref="DesignMetrics"/>, with the level class's thresholds
/// (§(h)), each failure carrying the measured values, a fix hint naming the kit helper that addresses it and the
/// locations to act on; the verdict is PASS (≤ 2 failures), REVIEW (3–4) or FAIL (≥ 5, or any hard check failing).
/// The benchmark and the agents' self-checks run this same code.
/// </summary>
public static class DesignGrader
{
    private static readonly Lazy<JsonObject> TableDoc = new(LoadTable);

    /// <summary>The threshold table (schema <c>glacier.design-grader/1</c>).</summary>
    public static JsonObject Table => TableDoc.Value;

    /// <summary>The table version (bumped with every threshold change).</summary>
    public static int Version => Table["version"]!.GetValue<int>();

    /// <summary>Every check id, in table order.</summary>
    public static IReadOnlyList<string> CheckIds => Table["checks"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ToList();

    private static JsonObject LoadTable()
    {
        using Stream? s = typeof(DesignGrader).Assembly.GetManifestResourceStream("Ged.Core.Spatial.design-grader.json");
        if (s is null)
        {
            throw new InvalidOperationException("The design grader table is missing from this build.");
        }

        return JsonNode.Parse(s)!.AsObject();
    }

    /// <summary>The level's gametype: the option, else detected from the level's objects, else its file-name prefix, else dm.</summary>
    public static string Gametype(SpatialAnalysis a, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim().ToLowerInvariant();
        }

        if (a.HasPayloadHandler)
        {
            return "payload";
        }

        if (a.Flags() is not null)
        {
            return "ctf";
        }

        if (a.HillPositions().Count > 0)
        {
            return "koth";
        }

        string name = (a.Level.FileName ?? string.Empty).ToLowerInvariant();
        foreach (string prefix in new[] { "ctf", "koth", "pay", "tdm", "dm" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return prefix == "pay" ? "payload" : prefix;
            }
        }

        return "dm";
    }

    /// <summary>The player class (S, M, L) of a player or spawn count.</summary>
    public static string PlayerClass(int? players, int spawns) => players is int p
        ? (p <= 8 ? "S" : p <= 16 ? "M" : "L")
        : (spawns <= 10 ? "S" : spawns <= 18 ? "M" : "L");

    /// <summary>The level class: payload (gametype payload or a Payload_Handler), open_air (the table's detector), else standard.</summary>
    public static string LevelClass(SpatialAnalysis a, IReadOnlyDictionary<string, double?> metrics, string gametype)
    {
        if (gametype == "payload" || a.HasPayloadHandler)
        {
            return "payload";
        }

        JsonNode? detect = Table["classes"]?["open_air"]?["detect"];
        return detect is not null && Evaluate(detect, metrics) == true ? "open_air" : "standard";
    }

    /// <summary>Grades an analysed level.</summary>
    public static JsonObject Grade(SpatialAnalysis analysis, GradeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        options ??= new GradeOptions();
        JsonObject report = analysis.Report();
        SortedDictionary<string, double?> metrics = DesignMetrics.Extract(report);
        string gametype = Gametype(analysis, options.Gametype);
        string detected = LevelClass(analysis, metrics, gametype);
        string cls = !options.Classes ? "standard"
            : options.Class is { Length: > 0 } forced ? NormaliseClass(forced)
            : detected;

        var failures = new JsonArray();
        var passed = new JsonArray();
        var notApplicable = new JsonArray();
        int failCount = 0;
        bool hardFail = false;
        foreach (JsonNode? check in Table["checks"]!.AsArray())
        {
            string id = check!["id"]!.GetValue<string>();
            if (check["class"]?.GetValue<string>() is { } only && only != cls)
            {
                continue; // a class-only check (payload) on another class
            }

            JsonNode? rule = check["rule"];
            bool classLimits = false;
            if (cls != "standard" && check[cls] is { } over)
            {
                classLimits = true;
                rule = over is JsonValue v && v.TryGetValue(out string? na) && na == "na" ? null : over;
            }

            bool? ok = rule is null ? null : Evaluate(rule, metrics);
            if (ok is null)
            {
                notApplicable.Add(id);
                continue;
            }

            if (ok == true)
            {
                passed.Add(id);
                continue;
            }

            failCount++;
            bool hard = check["hard"]?.GetValue<bool>() == true;
            hardFail |= hard;
            var values = new JsonObject();
            foreach (string m in MetricsOf(rule!).Distinct())
            {
                values[m] = metrics.TryGetValue(m, out double? x) && x is double d ? d : null;
            }

            var locations = new JsonArray();
            foreach (JsonNode? kind in check["locate"]?.AsArray() ?? new JsonArray())
            {
                foreach (JsonNode? loc in analysis.Locations(kind!.GetValue<string>(), options.MaxLocations))
                {
                    if (locations.Count < options.MaxLocations * 2)
                    {
                        locations.Add(loc!.DeepClone());
                    }
                }
            }

            var failure = new JsonObject
            {
                ["id"] = id,
                ["group"] = check["group"]?.GetValue<string>(),
                ["hard"] = hard,
                ["check"] = check["doc"]?.GetValue<string>(),
                ["class_limits"] = classLimits ? cls : null,
                ["rule"] = rule!.DeepClone(),
                ["values"] = values,
                ["fix"] = check["fix"]?.GetValue<string>(),
                ["locations"] = locations,
            };
            if (check["explain"]?.GetValue<string>() is { } explain)
            {
                // The distribution behind the number (every space the median counts, the floor-height bins), so the fix
                // can be steered: which spaces sit either side of the limit, which bins are short of a class.
                failure["detail"] = analysis.Detail(explain, LimitOf(rule!, check["explain_metric"]?.GetValue<string>()));
            }

            failures.Add(failure);
        }

        JsonObject verdictDoc = Table["verdict"]!.AsObject();
        int passMax = verdictDoc["pass_max"]!.GetValue<int>(), reviewMax = verdictDoc["review_max"]!.GetValue<int>();
        string verdict = hardFail || failCount > reviewMax ? "FAIL" : failCount > passMax ? "REVIEW" : "PASS";
        int spawns = (int)(metrics.GetValueOrDefault("spawns") ?? 0);
        string playerClass = PlayerClass(options.Players, spawns);
        var result = new JsonObject
        {
            ["schema"] = "glacier.grade/1",
            ["grader_version"] = Version,
            ["level"] = analysis.Level.FileName,
            ["geometry"] = analysis.Level.Freshness.ToJson(),
            ["verdict"] = verdict,
            ["failures_count"] = failCount,
            ["hard_failure"] = hardFail,
            ["gametype"] = gametype,
            ["class"] = cls,
            ["class_detected"] = detected,
            ["player_class"] = playerClass,
            ["players"] = options.Players,
            ["playable_area"] = metrics.GetValueOrDefault("playable_area"),
            ["spawns"] = spawns,
            ["movement_links"] = analysis.Options.MovementLinks ? analysis.Walk.Links.Count : null,
            ["failures"] = failures,
            ["passed"] = passed,
            ["not_applicable"] = notApplicable,
            ["targets"] = Targets(playerClass, cls),
            ["targets_basis"] = cls == "payload" ? $"payload (playable_area, area_per_spawn); player class {playerClass} (the rest)" : $"player class {playerClass}",
            ["advisories"] = WithDetails(Advisories(metrics, playerClass, cls), analysis),
            ["advisory_rule"] = Table["players"]?["advisory"]?.DeepClone(),
            ["set_pieces"] = new JsonObject
            {
                ["bridges"] = metrics.GetValueOrDefault("setpiece_bridges"),
                ["bridge_span_max"] = metrics.GetValueOrDefault("bridge_span_max"),
                ["bridge_span"] = metrics.GetValueOrDefault("bridge_span"),
                ["bridge_width"] = metrics.GetValueOrDefault("bridge_width"),
                ["bridge_gap_width"] = metrics.GetValueOrDefault("bridge_gap_width"),
                ["bridge_gap_depth"] = metrics.GetValueOrDefault("bridge_gap_depth"),
                ["landmark_height_max"] = metrics.GetValueOrDefault("landmark_height_max"),
                ["atrium_height_max"] = metrics.GetValueOrDefault("atrium_height_max"),
            },
            ["rule"] = verdictDoc["doc"]?.DeepClone(),
        };
        if (options.IncludeMetrics)
        {
            var m = new JsonObject();
            foreach (KeyValuePair<string, double?> kv in metrics)
            {
                m[kv.Key] = kv.Value;
            }

            result["metrics"] = m;
        }

        return result;
    }

    /// <summary>
    /// The advisory targets of a player class, with the level class's own rows over them: a payload map takes the payload
    /// row (the kit's payload layout budget) for the metrics it lists, and the player class's exemplar range for the rest.
    /// </summary>
    public static JsonObject? Targets(string playerClass, string levelClass = "standard")
    {
        if (Table["players"]?[playerClass] is not JsonObject row)
        {
            return null;
        }

        var targets = (JsonObject)row.DeepClone();
        if (Table["players"]?[levelClass] is JsonObject over && levelClass is not ("S" or "M" or "L"))
        {
            foreach (KeyValuePair<string, JsonNode?> kv in over)
            {
                if (kv.Value is JsonArray)
                {
                    targets[kv.Key] = kv.Value.DeepClone();
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// The advisory targets: every target of <see cref="Targets"/> (the player class's exemplar p25–p75, §(a); a payload
    /// map's own budget where the table has one) against the level's value — below, within or above (n/a when not
    /// measurable). Reported with every grade and never counted in the verdict.
    /// </summary>
    public static JsonArray Advisories(IReadOnlyDictionary<string, double?> metrics, string playerClass, string levelClass = "standard")
    {
        var list = new JsonArray();
        if (Targets(playerClass, levelClass) is not JsonObject targets)
        {
            return list;
        }

        foreach (KeyValuePair<string, JsonNode?> kv in targets)
        {
            if (kv.Value is not JsonArray { Count: 2 } range)
            {
                continue;
            }

            double lo = range[0]!.GetValue<double>(), hi = range[1]!.GetValue<double>();
            double? v = metrics.GetValueOrDefault(kv.Key);
            list.Add(new JsonObject
            {
                ["metric"] = kv.Key,
                ["value"] = v,
                ["target"] = new JsonArray(lo, hi),
                ["status"] = v is not double x ? "n/a" : x < lo ? "below" : x > hi ? "above" : "within",
                ["advisory"] = true,
            });
        }

        return list;
    }

    /// <summary>The advisory metrics that carry a detail (the distribution behind the number), by detail kind.</summary>
    private static readonly Dictionary<string, string> AdvisoryDetails = new(StringComparer.Ordinal) { ["floor_height_classes"] = "floor_height_classes" };

    /// <summary>Adds the detail of each advisory that has one (the floor-height bins: which count, which are short).</summary>
    private static JsonArray WithDetails(JsonArray advisories, SpatialAnalysis analysis)
    {
        foreach (JsonNode? a in advisories)
        {
            if (a?["metric"]?.GetValue<string>() is { } metric && AdvisoryDetails.TryGetValue(metric, out string? kind))
            {
                a["detail"] = analysis.Detail(kind);
            }
        }

        return advisories;
    }

    /// <summary>The upper limit a rule puts on <paramref name="metric"/> (its first <c>max</c>), or null.</summary>
    private static double? LimitOf(JsonNode rule, string? metric)
    {
        if (metric is null)
        {
            return null;
        }

        if (rule is JsonObject o)
        {
            if (o["metric"]?.GetValue<string>() == metric && o["max"] is JsonNode max)
            {
                return max.GetValue<double>();
            }

            foreach (KeyValuePair<string, JsonNode?> kv in o)
            {
                if (kv.Value is JsonObject or JsonArray && LimitOf(kv.Value, metric) is double d)
                {
                    return d;
                }
            }
        }
        else if (rule is JsonArray arr)
        {
            foreach (JsonNode? x in arr)
            {
                if (x is not null && LimitOf(x, metric) is double d)
                {
                    return d;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True for a check of the exemplar study's grader (research/exemplar-study/data/grader.json); the function checks
    /// (G27-G32, <c>"study": false</c>) came later and are calibrated by the purpose calibration gate instead.
    /// </summary>
    public static bool IsStudyCheck(string id) =>
        Table["checks"]!.AsArray().FirstOrDefault(c => c!["id"]!.GetValue<string>() == id) is not JsonObject c || c["study"]?.GetValue<bool>() != false;

    /// <summary>The ids of the checks that fail (table order), for calibration.</summary>
    public static List<string> FailedIds(JsonObject grade) =>
        grade["failures"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToList();

    private static string NormaliseClass(string c) => c.Trim().ToLowerInvariant().Replace('-', '_') switch
    {
        "standard" or "none" => "standard",
        "open_air" or "openair" => "open_air",
        "payload" => "payload",
        string other => throw new SpatialException("usage", $"Unknown level class '{other}'.", "Use standard, open_air or payload (or leave it out to detect it)."),
    };

    /// <summary>The metric ids a rule reads.</summary>
    private static IEnumerable<string> MetricsOf(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (o["metric"]?.GetValue<string>() is { } m)
            {
                yield return m;
            }

            if (o["na_if_missing"]?.GetValue<string>() is { } nm)
            {
                yield return nm;
            }

            foreach (KeyValuePair<string, JsonNode?> kv in o)
            {
                if (kv.Value is JsonObject or JsonArray && kv.Key != "na_if")
                {
                    foreach (string x in MetricsOf(kv.Value))
                    {
                        yield return x;
                    }
                }
            }
        }
        else if (node is JsonArray a)
        {
            foreach (JsonNode? x in a)
            {
                foreach (string y in MetricsOf(x!))
                {
                    yield return y;
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a rule: true (passes), false (fails) or null (not applicable / not measurable), with the exemplar study's
    /// semantics — a range on a missing metric is null; <c>all</c> fails when any part fails and is null when every part is;
    /// <c>any</c> passes when any part passes and is null when every part is; comparisons with a <c>default</c> read a
    /// missing metric as that value.
    /// </summary>
    internal static bool? Evaluate(JsonNode node, IReadOnlyDictionary<string, double?> m)
    {
        JsonObject o = node.AsObject();
        if (o["all"] is JsonArray all)
        {
            List<bool?> r = all.Select(x => Evaluate(x!, m)).ToList();
            return r.Any(x => x == false) ? false : r.All(x => x is null) ? null : true;
        }

        if (o["any"] is JsonArray any)
        {
            List<bool?> r = any.Select(x => Evaluate(x!, m)).ToList();
            return r.Any(x => x == true) ? true : r.All(x => x is null) ? null : false;
        }

        if (o["not"] is JsonNode not)
        {
            return Evaluate(not, m) is bool b ? !b : null;
        }

        if (o["na_if"] is JsonNode naIf)
        {
            return Evaluate(naIf, m) == true ? null : Evaluate(o["then"]!, m);
        }

        if (o["na_if_missing"]?.GetValue<string>() is { } key)
        {
            return m.GetValueOrDefault(key) is null ? null : Evaluate(o["then"]!, m);
        }

        if (o["fewer_than"] is JsonNode n)
        {
            int count = o["of"]!.AsArray().Count(x => Evaluate(x!, m) == true);
            return count < n.GetValue<int>();
        }

        string metric = o["metric"]!.GetValue<string>();
        double? v = m.GetValueOrDefault(metric) ?? (o["default"] is JsonNode d ? d.GetValue<double>() : null);
        if (v is not double x)
        {
            return null;
        }

        bool okay = true;
        if (o["min"] is JsonNode lo)
        {
            okay &= x >= lo.GetValue<double>();
        }

        if (o["max"] is JsonNode hi)
        {
            okay &= x <= hi.GetValue<double>();
        }

        if (o["gt"] is JsonNode gt)
        {
            okay &= x > gt.GetValue<double>();
        }

        if (o["ge"] is JsonNode ge)
        {
            okay &= x >= ge.GetValue<double>();
        }

        if (o["lt"] is JsonNode lt)
        {
            okay &= x < lt.GetValue<double>();
        }

        if (o["le"] is JsonNode le)
        {
            okay &= x <= le.GetValue<double>();
        }

        return okay;
    }
}
