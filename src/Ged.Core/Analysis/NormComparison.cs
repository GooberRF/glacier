using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ged.Core.Analysis;

/// <summary>One level metric compared with its corpus norms (CORPUS-NORMS-SPEC §5.3 <c>norms --compare</c>, §5.5 lint).</summary>
/// <param name="Value">The level's summary value: the median of a per-level distribution, else the scalar.</param>
/// <param name="Verdict"><c>below</c>, <c>within</c> or <c>above</c> the bucket's p5–p95 range.</param>
/// <param name="Extremity">Distance outside p5–p95 divided by the IQR (0 when within).</param>
public sealed record NormComparison(string Metric, double Value, NormStats Norm, string Verdict, double Extremity)
{
    /// <summary>The one-line, data-driven finding text (no per-metric wording).</summary>
    public string Message()
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        string unit = Norm.Unit is "m" or "m2" or "m3" ? " " + Norm.Unit : Norm.Unit is "ratio" or "count" ? string.Empty : " " + Norm.Unit;
        string what = Norm.Doc.TrimEnd('.');
        return $"{what}: typically {F(Norm.P5)}–{F(Norm.P95)}{unit} in {NormComparisons.BucketLabel(Norm.Bucket)} (corpus p5–p95); "
            + $"this level's {(FingerprintSchema.Metrics.FirstOrDefault(m => m.Id == Metric)?.IsDistribution == true ? "median" : "value")} is {F(Value)}{unit}.";
    }
}

/// <summary>Compares a level fingerprint with the shipped <see cref="CorpusNorms"/>.</summary>
public static class NormComparisons
{
    /// <summary>
    /// The per-level summary value of every numeric metric in a fingerprint: the p50 of a distribution, else the
    /// scalar (exactly what the norms aggregate votes with).
    /// </summary>
    public static Dictionary<string, double> SummaryValues(JsonObject fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (MetricDef m in FingerprintSchema.Metrics)
        {
            JsonNode? node = fingerprint[m.Group]?[m.Field];
            double? v = m.IsDistribution ? Num(node?["p50"]) : Num(node);
            if (v is double d && double.IsFinite(d))
            {
                values[m.Id] = d;
            }
        }

        return values;
    }

    /// <summary>The fingerprint's gametype (null when unknown or in conflict).</summary>
    public static string? Gametype(JsonObject fingerprint)
    {
        JsonNode? g = fingerprint?["identity"]?["gametype"];
        return g?["conflict"]?.GetValue<bool>() == true ? null : g?["value"]?.GetValue<string>();
    }

    /// <summary>
    /// Every metric that drives defaults (<c>use: default</c>, reliability medium or high) and that the level measured,
    /// compared with its reference bucket for the level's gametype; most extreme first.
    /// </summary>
    public static List<NormComparison> Compare(JsonObject fingerprint)
    {
        Dictionary<string, double> values = SummaryValues(fingerprint);
        string? gametype = Gametype(fingerprint);
        // Norms v2: the level's class and player class pick the per-class buckets where they are published.
        string? levelClass = fingerprint?["identity"]?["class"]?["value"]?.GetValue<string>();
        string? playerClass = fingerprint?["identity"]?["playerClass"]?.GetValue<string>();
        var result = new List<NormComparison>();
        foreach (string id in CorpusNorms.MetricIds)
        {
            if (!values.TryGetValue(id, out double v) || CorpusNorms.Get(id, gametype, levelClass, playerClass) is not { DrivesDefaults: true } norm)
            {
                continue;
            }

            string verdict = v < norm.P5 ? "below" : v > norm.P95 ? "above" : "within";
            double extremity = verdict == "below" ? (norm.P5 - v) / norm.Iqr : verdict == "above" ? (v - norm.P95) / norm.Iqr : 0;
            result.Add(new NormComparison(id, v, norm, verdict, Math.Round(extremity, 3)));
        }

        return result.OrderByDescending(c => c.Extremity).ThenBy(c => c.Metric, StringComparer.Ordinal).ToList();
    }

    /// <summary>A reader-facing name for a bucket (<c>exemplar.dm</c> → "reference DM levels", <c>dm.S</c> → "small DM levels").</summary>
    public static string BucketLabel(string bucket)
    {
        string Mode(string g)
        {
            int dot = g.IndexOf('.', StringComparison.Ordinal);
            string head = dot < 0 ? g : g[..dot];
            string name = head switch
            {
                "sp" => "single-player",
                "mp_other" => "other multiplayer",
                _ => head.ToUpperInvariant(),
            };
            return dot < 0 ? name : g[(dot + 1)..] switch
            {
                "S" => "small " + name,
                "M" => "mid-size " + name,
                "L" => "large " + name,
                "open_air" => "open-air " + name,
                "standard" => "standard " + name,
                string other => name + " " + other,
            };
        }

        if (bucket == "overall")
        {
            return "all corpus levels";
        }

        if (bucket == CorpusNorms.ReferenceBucket)
        {
            return "reference levels";
        }

        if (bucket.StartsWith(CorpusNorms.ReferenceBucket + ".", StringComparison.Ordinal))
        {
            return $"reference {Mode(bucket[(CorpusNorms.ReferenceBucket.Length + 1)..])} levels";
        }

        return bucket switch
        {
            "retail" => "the stock levels",
            "community" => "community levels",
            _ when bucket.StartsWith("retail.", StringComparison.Ordinal) => $"stock {Mode(bucket[7..])} levels",
            _ => $"{Mode(bucket)} levels",
        };
    }

    private static double? Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue(out double d) ? d : v.TryGetValue(out int i) ? i : v.TryGetValue(out long l) ? l : null
        : null;
}
