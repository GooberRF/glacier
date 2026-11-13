using Ged.Core.Analysis;

namespace Ged.Core.Scripting;

/// <summary>One metric's corpus norms in one bucket (Lua type <c>norm_info</c>; CORPUS-NORMS-SPEC §5.3).</summary>
[ScriptDoc("Corpus norms for one level metric in one bucket: one-level-one-vote percentiles over the fingerprinted level corpus (aggregates only).", Since = 2)]
public sealed class ScriptNormInfo
{
    internal ScriptNormInfo(NormStats s)
    {
        Metric = s.Metric;
        Bucket = s.Bucket;
        N = s.N;
        P5 = s.P5;
        P25 = s.P25;
        P50 = s.P50;
        P75 = s.P75;
        P95 = s.P95;
        Unit = s.Unit;
        Reliability = s.Reliability;
        Use = s.Use;
        Doc = s.Doc;
    }

    [ScriptDoc("The metric id, e.g. \"opening.width\".", Since = 2)]
    public string Metric { get; }

    [ScriptDoc("The bucket the numbers come from: \"exemplar\" (curated reference levels), \"overall\", a gametype (\"dm\", \"ctf\", \"sp\"), an origin (\"retail\", \"community\") or a combination (\"exemplar.dm\").", Since = 2)]
    public string Bucket { get; }

    [ScriptDoc("How many levels voted (one vote per level).", Since = 2)]
    public int N { get; }

    [ScriptDoc("5th percentile.", Since = 2)]
    public double P5 { get; }

    [ScriptDoc("25th percentile (lower quartile).", Since = 2)]
    public double P25 { get; }

    [ScriptDoc("Median.", Since = 2)]
    public double P50 { get; }

    [ScriptDoc("75th percentile (upper quartile).", Since = 2)]
    public double P75 { get; }

    [ScriptDoc("95th percentile.", Since = 2)]
    public double P95 { get; }

    [ScriptDoc("Unit: \"m\", \"m2\", \"m3\", \"count\", \"fraction\", \"per_100m2\", …", Since = 2)]
    public string Unit { get; }

    [ScriptDoc("How far the extractor can be trusted for this metric: \"high\", \"medium\" or \"low\".", Since = 2)]
    public string Reliability { get; }

    [ScriptDoc("\"default\" when the metric drives kit defaults and the out-of-norm lint; \"reference\" when it is for comparison only.", Since = 2)]
    public string Use { get; }

    [ScriptDoc("What the metric measures.", Since = 2)]
    public string Doc { get; }

    public override string ToString() => $"{Metric} [{Bucket}] p50 {P50} {Unit} (p25 {P25}, p75 {P75}, n {N})";
}
