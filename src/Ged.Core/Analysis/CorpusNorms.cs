using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ged.Core.Analysis;

/// <summary>One metric's published percentile set in one bucket of the shipped norms.</summary>
/// <param name="Metric">Metric id (e.g. <c>opening.width</c>).</param>
/// <param name="Bucket">The bucket the numbers come from (<c>exemplar</c>, <c>overall</c>, <c>dm</c>, …).</param>
public sealed record NormStats(
    string Metric,
    string Bucket,
    int N,
    double P5,
    double P25,
    double P50,
    double P75,
    double P95,
    string Unit,
    string Reliability,
    string Use,
    string Doc)
{
    /// <summary>Interquartile range (never below a small floor, so ratios stay finite).</summary>
    public double Iqr => Math.Max(P75 - P25, 1e-6);

    /// <summary>True when the metric may drive kit defaults and lint (<c>use: default</c>, reliability medium or high).</summary>
    public bool DrivesDefaults => Use == "default" && Reliability is "medium" or "high";
}

/// <summary>A shipped metric: its description and the buckets it is published for.</summary>
public sealed record NormMetric(string Id, string Unit, string Reliability, string Use, string Doc, IReadOnlyList<string> Buckets);

/// <summary>
/// The shipped corpus norms (CORPUS-NORMS-SPEC §5.2): aggregate percentiles of level metrics over the fingerprinted
/// level corpus, embedded in Ged.Core as <c>Analysis/corpus-norms.json</c> and regenerated only by
/// <c>glacier-cli corpus aggregate --promote</c>. The file holds aggregates only (no level, author or texture names).
/// Consumers: Lua <c>catalog.norms</c>, the <c>norms</c> command, kit defaults and the out-of-norm lint.
/// </summary>
public static class CorpusNorms
{
    /// <summary>
    /// The bucket kit defaults and the out-of-norm lint follow: the owner-curated exemplar set (well-regarded stock and
    /// community levels). A metric the reference bucket does not publish falls back to <see cref="OverallBucket"/>. One
    /// constant: set it to <see cref="OverallBucket"/> to follow the whole corpus instead.
    /// </summary>
    public const string ReferenceBucket = "exemplar";

    /// <summary>Every voting level of the corpus.</summary>
    public const string OverallBucket = "overall";

    private static readonly Lazy<JsonObject?> Data = new(Load);

    /// <summary>The raw embedded document (null when the build carries no norms).</summary>
    public static JsonObject? Document => Data.Value;

    /// <summary>True when norms are embedded in this build.</summary>
    public static bool Available => Data.Value?["metrics"] is JsonObject;

    /// <summary>The month the norms were generated (<c>yyyy-MM</c>), or null.</summary>
    public static string? Generated => Data.Value?["generated"]?.GetValue<string>();

    /// <summary>Every shipped metric id, sorted ordinally.</summary>
    public static IReadOnlyList<string> MetricIds { get; } = (Data.Value?["metrics"] as JsonObject)?
        .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();

    /// <summary>The shipped metric, or null when it is not in the norms.</summary>
    public static NormMetric? Metric(string id)
    {
        if (Data.Value?["metrics"]?[id] is not JsonObject m)
        {
            return null;
        }

        return new NormMetric(
            id,
            m["unit"]?.GetValue<string>() ?? string.Empty,
            m["reliability"]?.GetValue<string>() ?? "low",
            m["use"]?.GetValue<string>() ?? "reference",
            m["doc"]?.GetValue<string>() ?? string.Empty,
            (m["buckets"] as JsonObject)?.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>());
    }

    /// <summary>One metric in one named bucket, exactly (null when the metric or that bucket is not published).</summary>
    public static NormStats? GetBucket(string metric, string bucket)
    {
        if (Data.Value?["metrics"]?[metric] is not JsonObject m || m["buckets"]?[bucket] is not JsonObject row)
        {
            return null;
        }

        double P(string k) => row[k] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;
        return new NormStats(
            metric,
            bucket,
            row["n"] is JsonValue nv && nv.TryGetValue(out int n) ? n : 0,
            P("p5"),
            P("p25"),
            P("p50"),
            P("p75"),
            P("p95"),
            m["unit"]?.GetValue<string>() ?? string.Empty,
            m["reliability"]?.GetValue<string>() ?? "low",
            m["use"]?.GetValue<string>() ?? "reference",
            m["doc"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>
    /// The buckets consulted, in order, for a level of <paramref name="gametype"/> (null = any):
    /// <c>reference.gametype</c>, <c>reference</c>, then <c>overall</c> (with <see cref="ReferenceBucket"/> = overall:
    /// the gametype bucket, then overall).
    /// </summary>
    public static IReadOnlyList<string> ResolutionOrder(string? gametype)
    {
        var order = new List<string>();
        if (string.Equals(ReferenceBucket, OverallBucket, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(gametype))
            {
                order.Add(gametype);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(gametype))
            {
                order.Add(ReferenceBucket + "." + gametype);
            }

            order.Add(ReferenceBucket);
        }

        order.Add(OverallBucket);
        return order;
    }

    /// <summary>The reference numbers for a metric (first published bucket of <see cref="ResolutionOrder(string?)"/>), or null.</summary>
    public static NormStats? Get(string metric, string? gametype = null)
    {
        foreach (string b in ResolutionOrder(gametype))
        {
            if (GetBucket(metric, b) is { } s)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// The buckets consulted, in order, for a level of <paramref name="gametype"/>, level class
    /// <paramref name="levelClass"/> (standard / open_air / payload) and player class <paramref name="playerClass"/>
    /// (S / M / L) — the per-class norms of DESIGN-QUALITY-SPEC E12. Most specific first:
    /// <c>reference.gametype.class</c>; for a non-standard class the whole corpus's <c>gametype.class</c> (its shape
    /// differs from the reference set's, which is mostly standard); <c>reference.gametype.size</c>; then the
    /// <see cref="ResolutionOrder(string?)"/> chain. With neither class given it is exactly that chain (norms v1).
    /// Payload is its own gametype: its class adds no bucket.
    /// </summary>
    public static IReadOnlyList<string> ResolutionOrder(string? gametype, string? levelClass, string? playerClass)
    {
        var order = new List<string>();
        string? cls = string.IsNullOrWhiteSpace(levelClass) || levelClass == "payload" ? null : levelClass.Trim().ToLowerInvariant();
        string? size = string.IsNullOrWhiteSpace(playerClass) ? null : playerClass.Trim().ToUpperInvariant();
        bool reference = !string.Equals(ReferenceBucket, OverallBucket, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(gametype))
        {
            if (cls is not null)
            {
                if (reference)
                {
                    order.Add(ReferenceBucket + "." + gametype + "." + cls);
                }

                if (cls != "standard" || !reference)
                {
                    order.Add(gametype + "." + cls);
                }
            }

            if (size is not null)
            {
                order.Add((reference ? ReferenceBucket + "." : string.Empty) + gametype + "." + size);
            }
        }

        foreach (string b in ResolutionOrder(gametype))
        {
            if (!order.Contains(b, StringComparer.Ordinal))
            {
                order.Add(b);
            }
        }

        return order;
    }

    /// <summary>
    /// The reference numbers for a metric for a level of a gametype, level class and player class (first published bucket
    /// of <see cref="ResolutionOrder(string?, string?, string?)"/>), or null. Buckets that are not published (too few
    /// levels of that class) fall through to the norms v1 chain.
    /// </summary>
    public static NormStats? Get(string metric, string? gametype, string? levelClass, string? playerClass)
    {
        foreach (string b in ResolutionOrder(gametype, levelClass, playerClass))
        {
            if (GetBucket(metric, b) is { } s)
            {
                return s;
            }
        }

        return null;
    }

    private static JsonObject? Load()
    {
        using Stream? s = typeof(CorpusNorms).Assembly.GetManifestResourceStream("Ged.Core.Analysis.corpus-norms.json");
        if (s is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(s) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
