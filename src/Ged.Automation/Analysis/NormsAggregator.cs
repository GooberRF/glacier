using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;

namespace Ged.Automation.Analysis;

/// <summary>Settings for <see cref="NormsAggregator"/>.</summary>
public sealed class NormsOptions
{
    /// <summary>Respect version-series collapse (only the newest version of a series votes). Default on.</summary>
    public bool Collapse { get; init; } = true;

    /// <summary>The month the norms are generated, <c>yyyy-MM</c> (month-granular: no build-time nondeterminism).</summary>
    public string Generated { get; init; } = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Publication threshold for community / mixed buckets: levels.</summary>
    public int MinLevels { get; init; } = 30;

    /// <summary>Publication threshold for community / mixed buckets: distinct canonical authors.</summary>
    public int MinAuthors { get; init; } = 10;

    /// <summary>Publication threshold for retail buckets (one publisher): levels.</summary>
    public int MinRetailLevels { get; init; } = 15;

    /// <summary>Motifs ship when present in at least this share of a bucket's levels.</summary>
    public double MotifMinFraction { get; init; } = 0.02;

    /// <summary>At most this many motifs per bucket.</summary>
    public int MaxMotifs { get; init; } = 200;

    /// <summary>A texture family ships when present in this share of a bucket's levels…</summary>
    public double FamilyMinFraction { get; init; } = 0.02;

    /// <summary>…and (community / mixed buckets) in levels by at least this many authors.</summary>
    public int FamilyMinAuthors { get; init; } = 5;

    /// <summary>
    /// The owner-curated exemplar list (one level file name per line, <c>.rfl</c> optional, <c>#</c> comments), or null.
    /// A missing file means no exemplar bucket, never an error. Local only: names never reach the norms.
    /// </summary>
    public string? ExemplarListPath { get; init; }

    /// <summary>
    /// A folder of stock levels that are all exemplars (read-only; each <c>.rfl</c> under it is matched to a fingerprint by
    /// SHA-256 and counts when that fingerprint's origin is retail), or null.
    /// </summary>
    public string? ExemplarStockDir { get; init; }

    /// <summary>Publication threshold for the curated exemplar buckets: levels (a hand-picked set, like retail).</summary>
    public int MinExemplarLevels { get; init; } = 15;
}

/// <summary>One level as the aggregate sees it (from its fingerprint).</summary>
public sealed class NormsLevel
{
    public required string Sha { get; init; }

    public required string Gametype { get; init; }

    public bool Conflict { get; init; }

    public required string Origin { get; init; }

    public required string Author { get; init; }

    public bool Vote { get; init; } = true;

    /// <summary>True when the level is in the curated exemplar set (votes in the exemplar buckets regardless of collapse).</summary>
    public bool Exemplar { get; init; }

    /// <summary>The extractor version that produced the level's fingerprint.</summary>
    public int ExtractorVersion { get; init; } = FingerprintSchema.ExtractorVersion;

    /// <summary>The level class (standard, open_air, payload; fingerprint <c>identity.class</c>, extractor 2), or null when unmeasured.</summary>
    public string? LevelClass { get; init; }

    /// <summary>The player class S / M / L by spawn count (<c>identity.playerClass</c>, else from <c>spawns.count</c>), or null without spawns.</summary>
    public string? PlayerClass { get; init; }

    public required IReadOnlyDictionary<string, double> Values { get; init; }

    public required IReadOnlySet<string> Motifs { get; init; }

    public required IReadOnlyDictionary<string, double> Families { get; init; }

    /// <summary>The level's median |opening width − portal width| over matched pairs (§4-2), or null.</summary>
    public double? OpeningPortalDiff { get; init; }

    /// <summary>Reads the aggregate's view of a fingerprint (null for a fingerprint without metrics).</summary>
    public static NormsLevel? FromFingerprint(JsonObject fp, bool vote = true, bool exemplar = false)
    {
        ArgumentNullException.ThrowIfNull(fp);
        if (fp["status"]?.GetValue<string>() is not (FingerprintSchema.Status.Ok or FingerprintSchema.Status.OkPartial))
        {
            return null;
        }

        JsonNode? id = fp["identity"];
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (MetricDef m in FingerprintSchema.Metrics)
        {
            JsonNode? node = fp[m.Group]?[m.Field];
            double? v = m.IsDistribution ? Num(node?["p50"]) : Num(node);
            if (v is double d && double.IsFinite(d))
            {
                values[m.Id] = d;
            }
        }

        var motifs = new HashSet<string>(StringComparer.Ordinal);
        if (fp["events"]?["motifs"] is JsonArray arr)
        {
            foreach (JsonNode? n in arr)
            {
                if (n?["motif"]?.GetValue<string>() is { } s)
                {
                    motifs.Add(s);
                }
            }
        }

        var families = new Dictionary<string, double>(StringComparer.Ordinal);
        if (fp["textures"]?["family"] is JsonObject fam)
        {
            foreach ((string k, JsonNode? v) in fam)
            {
                if (Num(v) is double d)
                {
                    families[k] = d;
                }
            }
        }

        return new NormsLevel
        {
            Sha = id?["sha256"]?.GetValue<string>() ?? string.Empty,
            Gametype = id?["gametype"]?["value"]?.GetValue<string>() ?? "mp_other",
            Conflict = id?["gametype"]?["conflict"]?.GetValue<bool>() ?? false,
            Origin = id?["origin"]?.GetValue<string>() ?? "community",
            Author = id?["author"]?["canonical"]?.GetValue<string>() ?? string.Empty,
            Vote = vote,
            Exemplar = exemplar,
            ExtractorVersion = fp["extractorVersion"] is JsonValue ev && ev.TryGetValue(out int evi) ? evi : FingerprintSchema.ExtractorVersion,
            LevelClass = id?["class"]?["value"]?.GetValue<string>() is { } cls && FingerprintSchema.LevelClasses.Contains(cls) ? cls : null,
            PlayerClass = id?["playerClass"]?.GetValue<string>() is { } pc && FingerprintSchema.PlayerClasses.Contains(pc) ? pc
                : values.TryGetValue("spawns.count", out double spawns) && spawns > 0 ? Ged.Core.Spatial.DesignGrader.PlayerClass(null, (int)spawns) : null,
            Values = values,
            Motifs = motifs,
            Families = families,
            OpeningPortalDiff = (fp["opening"]?["portal_agreement"]?["pairs"] is JsonNode pairs && Num(pairs) > 0)
                ? Num(fp["opening"]?["portal_agreement"]?["abs_width_diff"]?["p50"]) : null,
        };
    }

    internal static double? Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue(out double d) ? d : v.TryGetValue(out int i) ? i : v.TryGetValue(out long l) ? l : v.TryGetValue(out uint u) ? u : null
        : null;
}

/// <summary>
/// Fingerprints → the shippable norms (<c>glacier.norms/1</c>, CORPUS-NORMS-SPEC §5.1/§5.2). One level, one vote: a
/// metric's aggregate is the percentile set over each level's own summary value (its p50 for a distribution).
/// Buckets: overall, per gametype, per origin and retail.&lt;gametype&gt;; a bucket ships only with ≥ 30 levels from
/// ≥ 10 distinct authors (≥ 15 levels for retail buckets). The output names no level, file, author, hash or texture
/// file. Operates only on the sweep's output folder; writes <c>norms/corpus-norms.json</c> and
/// <c>norms/norms-report.md</c> through <see cref="CorpusOutput"/>.
/// </summary>
public static class NormsAggregator
{
    public const string Schema = "glacier.norms/1";

    /// <summary>
    /// Metrics allowed to drive kit defaults and lint (<c>"use": "default"</c>) once validated. opening.* also needs the
    /// §4-2 cross-method check; stair.* stays <c>reference</c> until the debug-render eyeball set (§4-3) accepts it.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultCandidates = new HashSet<string>(StringComparer.Ordinal)
    {
        "opening.width", "opening.height", "corridor.width", "corridor.height", "lights.per_100m2", "texel.tile_metres",
        "space.headroom", "space.clear_width",
    };

    /// <summary>The §4-2 limit: median |opening.width − portal.width| over matched pairs.</summary>
    public const double CrossMethodLimit = 0.5;

    /// <summary>Aggregates the fingerprints of a sweep's output folder and writes the candidate norms + report.</summary>
    public static (JsonObject Norms, string Report) Run(string outputDir, NormsOptions? options = null)
    {
        options ??= new NormsOptions();
        if (!System.IO.Directory.Exists(outputDir))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"No sweep output folder '{outputDir}': run 'glacier-cli corpus sweep' first.");
        }

        var probe = new CorpusOutput(outputDir, Array.Empty<string>());
        if (!probe.Exists("manifest.json") || !probe.Exists("index.jsonl"))
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"No sweep output in '{probe.Root}' (manifest.json / index.jsonl): run 'glacier-cli corpus sweep' first.");
        }

        JsonObject manifest = JsonNode.Parse(probe.ReadText("manifest.json")) as JsonObject ?? new JsonObject();
        var roots = (manifest["roots"] as JsonArray ?? new JsonArray()).Select(n => n!.GetValue<string>()).ToList();
        if (manifest["rfInstall"]?.GetValue<string>() is { } install)
        {
            roots.Add(install);
        }

        var output = new CorpusOutput(outputDir, roots); // re-check the guard against the swept roots
        var rows = new List<(string Sha, bool Vote, JsonObject Fp)>();
        var namesBySha = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in output.ReadText("index.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (JsonNode.Parse(line) is not JsonObject row || row["sha256"]?.GetValue<string>() is not { Length: > 2 } sha)
            {
                continue;
            }

            if (!namesBySha.TryGetValue(sha, out HashSet<string>? names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                namesBySha[sha] = names;
            }

            names.Add(ExemplarKey(InstanceFileName(row["source"]?.GetValue<string>())));
            foreach (JsonNode? d in row["duplicates"] as JsonArray ?? new JsonArray())
            {
                names.Add(ExemplarKey(InstanceFileName(d?.GetValue<string>())));
            }

            if (row["canonical"]?.GetValue<bool>() != true)
            {
                continue;
            }

            string rel = CorpusSweep.FingerprintPath(sha);
            if (output.Exists(rel) && JsonNode.Parse(output.ReadText(rel)) is JsonObject fp)
            {
                rows.Add((sha, row["vote"]?.GetValue<bool>() ?? true, fp));
            }
        }

        ExemplarMatch exemplars = MatchExemplars(rows, namesBySha, options);
        var levels = new List<NormsLevel>();
        foreach ((string sha, bool vote, JsonObject fp) in rows)
        {
            if (NormsLevel.FromFingerprint(fp, vote, exemplars.Shas.Contains(sha)) is { } level)
            {
                levels.Add(level);
            }
        }

        (JsonObject norms, string report) = Aggregate(levels, options, exemplars);
        output.WriteText("norms/corpus-norms.json", LevelFingerprint.Serialize(norms));
        output.WriteText("norms/norms-report.md", report);
        return (norms, report);
    }

    /// <summary>How the curated exemplar list resolved against the sweep (local report only).</summary>
    public sealed record ExemplarMatch(IReadOnlySet<string> Shas, int ListedNames, IReadOnlyList<string> UnmatchedNames, int StockLevels, bool ListPresent)
    {
        public static readonly ExemplarMatch None = new(new HashSet<string>(StringComparer.Ordinal), 0, Array.Empty<string>(), 0, false);
    }

    /// <summary>The instance file name of an index source (<c>dir\a.rfl</c> or <c>pack.vpp|a.rfl</c>).</summary>
    private static string InstanceFileName(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        int bar = source.LastIndexOf('|');
        string tail = bar >= 0 ? source[(bar + 1)..] : source;
        return System.IO.Path.GetFileName(tail.Replace('/', '\\').Split('\\')[^1]);
    }

    /// <summary>The matching key of a level file name: lower-case, without a trailing <c>.rfl</c>.</summary>
    public static string ExemplarKey(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".rfl", StringComparison.Ordinal) ? n[..^4] : n;
    }

    /// <summary>One entry of the curated exemplar list.</summary>
    public readonly record struct ExemplarLine(string Name, bool CounterExample, string? Note);

    /// <summary>
    /// Parses one line of the exemplar list: blank lines and lines starting with <c>#</c> are skipped (null); a note
    /// may follow the name after whitespace and <c>#</c> (<c>dm02  # good hallway heights</c>); a leading <c>!</c>
    /// marks a counter-example — a known-weak layout kept for contrast, never part of the exemplar bucket.
    /// </summary>
    public static ExemplarLine? ParseExemplarLine(string raw)
    {
        string line = raw.Trim().Trim('﻿').Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return null;
        }

        string? note = null;
        int hash = line.IndexOf(" #", StringComparison.Ordinal);
        int tabHash = line.IndexOf("\t#", StringComparison.Ordinal);
        int cut = hash < 0 ? tabHash : tabHash < 0 ? hash : Math.Min(hash, tabHash);
        if (cut >= 0)
        {
            note = line[(cut + 2)..].Trim();
            line = line[..cut].Trim();
        }

        bool counter = line.StartsWith('!');
        if (counter)
        {
            line = line[1..].Trim();
        }

        return line.Length == 0 ? null : new ExemplarLine(line, counter, string.IsNullOrEmpty(note) ? null : note);
    }

    /// <summary>
    /// Resolves the exemplar set: every name in the list matches the canonical levels whose instances carry that file name
    /// (several distinct levels share a name ⇒ prefer the retail ones, else the voting ones); every stock level under
    /// <see cref="NormsOptions.ExemplarStockDir"/> matches by SHA-256 when its fingerprint is retail. Both inputs are
    /// optional; files are read read-only through <see cref="CorpusReader"/>.
    /// </summary>
    private static ExemplarMatch MatchExemplars(
        IReadOnlyList<(string Sha, bool Vote, JsonObject Fp)> rows,
        IReadOnlyDictionary<string, HashSet<string>> namesBySha,
        NormsOptions o)
    {
        var shas = new HashSet<string>(StringComparer.Ordinal);
        var bySha = rows.ToDictionary(r => r.Sha, r => r, StringComparer.Ordinal);
        string OriginOf(string sha) => bySha[sha].Fp["identity"]?["origin"]?.GetValue<string>() ?? "community";

        int listed = 0;
        var unmatched = new List<string>();
        bool present = o.ExemplarListPath is { } listPath && System.IO.File.Exists(listPath);
        if (present)
        {
            foreach (string raw in System.IO.File.ReadAllLines(o.ExemplarListPath!))
            {
                if (ParseExemplarLine(raw) is not { CounterExample: false } entry)
                {
                    continue; // blank, comment, or a counter-example ("!name": a known-weak layout, never in the bucket)
                }

                string line = entry.Name;

                listed++;
                string key = ExemplarKey(line);
                List<string> hits = rows.Where(r => namesBySha.TryGetValue(r.Sha, out HashSet<string>? n) && n.Contains(key))
                    .Select(r => r.Sha).ToList();
                if (hits.Count == 0)
                {
                    unmatched.Add(line);
                    continue;
                }

                List<string> pick = hits.Where(h => OriginOf(h) == "retail").ToList();
                if (pick.Count == 0)
                {
                    pick = hits.Where(h => bySha[h].Vote).ToList();
                }

                foreach (string h in pick.Count > 0 ? pick : hits)
                {
                    shas.Add(h);
                }
            }
        }

        int stock = 0;
        if (o.ExemplarStockDir is { } dir && System.IO.Directory.Exists(dir))
        {
            foreach (CorpusFile f in CorpusReader.EnumerateFiles(dir, System.Threading.CancellationToken.None))
            {
                if (!f.FullPath.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase) || !LooksLikeLevelFile(f.FullPath))
                {
                    continue;
                }

                string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(CorpusReader.ReadAll(f.FullPath))).ToLowerInvariant();
                if (bySha.ContainsKey(sha) && OriginOf(sha) == "retail" && shas.Add(sha))
                {
                    stock++;
                }
            }
        }

        return new ExemplarMatch(shas, listed, unmatched, stock, present);
    }

    /// <summary>A level file, not an editor autosave/backup copy (those never match a canonical stock fingerprint anyway).</summary>
    private static bool LooksLikeLevelFile(string path)
    {
        string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return !name.Contains("autosave", StringComparison.Ordinal) && !name.EndsWith("~.rfl", StringComparison.Ordinal);
    }

    /// <summary>The pure aggregation (tests call this directly).</summary>
    public static (JsonObject Norms, string Report) Aggregate(IReadOnlyList<NormsLevel> all, NormsOptions options, ExemplarMatch? exemplars = null)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(options);
        exemplars ??= ExemplarMatch.None;
        List<NormsLevel> voting = all.Where(l => !options.Collapse || l.Vote).OrderBy(l => l.Sha, StringComparer.Ordinal).ToList();

        // Cross-method agreement (§4-2): one vote per level with matched pairs.
        double[] diffs = voting.Where(l => l.OpeningPortalDiff.HasValue).Select(l => l.OpeningPortalDiff!.Value).OrderBy(d => d).ToArray();
        double? crossMedian = diffs.Length > 0 ? Percentiles.Type7(diffs, 0.5) : null;
        bool openingsValidated = crossMedian is double cm && cm <= CrossMethodLimit;

        var bucketLevels = new SortedDictionary<string, List<NormsLevel>>(StringComparer.Ordinal);
        foreach (NormsLevel l in voting)
        {
            foreach (string b in BucketsOf(l))
            {
                if (!bucketLevels.TryGetValue(b, out List<NormsLevel>? list))
                {
                    list = new List<NormsLevel>();
                    bucketLevels[b] = list;
                }

                list.Add(l);
            }
        }

        // The curated exemplar set votes whatever version-series collapse decided (the owner picked those exact levels).
        foreach (NormsLevel l in all.Where(l => l.Exemplar).OrderBy(l => l.Sha, StringComparer.Ordinal))
        {
            foreach (string b in l.Conflict ? new[] { ExemplarBucket } : new[] { ExemplarBucket, ExemplarBucket + "." + l.Gametype })
            {
                if (!bucketLevels.TryGetValue(b, out List<NormsLevel>? list))
                {
                    list = new List<NormsLevel>();
                    bucketLevels[b] = list;
                }

                list.Add(l);
            }
        }

        // Per-class buckets (DESIGN-QUALITY-SPEC E12), for the default-driving metrics only: dm.<S|M|L> by spawn count,
        // <gametype>.<class> by level class (open_air / standard; payload is its own gametype), and the same under
        // exemplar. One level, one vote, as everywhere; a gametype conflict votes in none of them.
        var classLevels = new SortedDictionary<string, List<NormsLevel>>(StringComparer.Ordinal);
        void AddClass(string b, NormsLevel l)
        {
            if (!classLevels.TryGetValue(b, out List<NormsLevel>? list))
            {
                list = new List<NormsLevel>();
                classLevels[b] = list;
            }

            list.Add(l);
        }

        foreach (NormsLevel l in voting)
        {
            foreach (string b in ClassBucketsOf(l))
            {
                AddClass(b, l);
            }
        }

        foreach (NormsLevel l in all.Where(l => l.Exemplar).OrderBy(l => l.Sha, StringComparer.Ordinal))
        {
            foreach (string b in ClassBucketsOf(l))
            {
                AddClass(ExemplarBucket + "." + b, l);
            }
        }

        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        var metrics = new JsonObject();
        foreach (MetricDef m in FingerprintSchema.Metrics)
        {
            MetricReliability reliability = m.Reliability;
            string use = DefaultCandidates.Contains(m.Id) && reliability >= MetricReliability.Medium ? "default" : "reference";
            if (m.Group == "opening" && !openingsValidated)
            {
                reliability = MetricReliability.Low;
                use = "reference";
            }

            var buckets = new JsonObject();
            IEnumerable<KeyValuePair<string, List<NormsLevel>>> sets = DefaultCandidates.Contains(m.Id) ? bucketLevels.Concat(classLevels) : bucketLevels;
            foreach ((string bucket, List<NormsLevel> members) in sets)
            {
                List<NormsLevel> withValue = members.Where(l => l.Values.ContainsKey(m.Id)).ToList();
                if (!Qualifies(bucket, withValue, options))
                {
                    if (withValue.Count > 0)
                    {
                        dropped.Add(bucket);
                    }

                    continue;
                }

                double[] v = withValue.Select(l => l.Values[m.Id]).OrderBy(x => x).ToArray();
                var row = new JsonObject { ["n"] = v.Length };
                foreach ((string name, double p) in Percentiles.DistributionPoints)
                {
                    row[name] = Percentiles.Round(Percentiles.Type7(v, p), 2);
                }

                buckets[bucket] = row;
            }

            if (buckets.Count == 0)
            {
                continue;
            }

            metrics[m.Id] = new JsonObject
            {
                ["unit"] = m.Unit,
                ["reliability"] = FingerprintSchema.Name(reliability),
                ["use"] = use,
                ["doc"] = m.Doc,
                ["buckets"] = buckets,
            };
        }

        var motifs = new JsonObject();
        var families = new JsonObject();
        foreach ((string bucket, List<NormsLevel> members) in bucketLevels)
        {
            if (!Qualifies(bucket, members, options))
            {
                continue;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (NormsLevel l in members)
            {
                foreach (string mo in l.Motifs)
                {
                    counts[mo] = counts.GetValueOrDefault(mo) + 1;
                }
            }

            JsonNode[] top = counts
                .Select(kv => (Motif: kv.Key, Fraction: (double)kv.Value / members.Count))
                .Where(x => x.Fraction >= options.MotifMinFraction)
                .OrderByDescending(x => x.Fraction).ThenBy(x => x.Motif, StringComparer.Ordinal)
                .Take(options.MaxMotifs)
                .Select(x => (JsonNode)new JsonObject { ["motif"] = x.Motif, ["levelFraction"] = Percentiles.Round(x.Fraction, 4) })
                .ToArray();
            if (top.Length > 0)
            {
                motifs[bucket] = new JsonArray(top);
            }

            bool retail = bucket.StartsWith("retail", StringComparison.Ordinal) || IsExemplarBucket(bucket);
            var fam = new JsonObject();
            foreach (string f in members.SelectMany(l => l.Families.Keys).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal))
            {
                List<NormsLevel> having = members.Where(l => l.Families.GetValueOrDefault(f) > 0).ToList();
                int authors = having.Select(l => l.Author).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Count();
                if (f == "(none)" || (double)having.Count / members.Count < options.FamilyMinFraction || (!retail && authors < options.FamilyMinAuthors))
                {
                    continue;
                }

                fam[f] = Percentiles.Round(members.Average(l => l.Families.GetValueOrDefault(f)), 4);
            }

            if (fam.Count > 0)
            {
                families[bucket] = fam;
            }
        }

        var norms = new JsonObject
        {
            ["schema"] = Schema,
            // The oldest extractor among the aggregated fingerprints: what the numbers were measured with.
            ["extractorVersion"] = all.Count > 0 ? all.Min(l => l.ExtractorVersion) : FingerprintSchema.ExtractorVersion,
            ["generated"] = options.Generated,
            ["corpus"] = new JsonObject
            {
                ["levels"] = voting.Count,
                ["retail"] = voting.Count(l => l.Origin == "retail"),
                ["community"] = voting.Count(l => l.Origin != "retail"),
                ["exemplar"] = all.Count(l => l.Exemplar),
            },
            ["validation"] = new JsonObject
            {
                ["openingPortalMedianAbsDiff"] = crossMedian is double c ? Percentiles.Round(c, 3) : null,
                ["openingPortalLevels"] = diffs.Length,
                ["openingsValidated"] = openingsValidated,
            },
            ["metrics"] = metrics,
            ["motifs"] = motifs,
            ["textureFamilies"] = families,
        };

        var reported = new SortedDictionary<string, List<NormsLevel>>(bucketLevels, StringComparer.Ordinal);
        foreach ((string b, List<NormsLevel> members) in classLevels)
        {
            reported[b] = members;
        }

        string report = Report(all, voting, reported, dropped, norms, crossMedian, diffs.Length, options, exemplars);
        return (norms, report);
    }

    /// <summary>The curated exemplar bucket (and <c>exemplar.&lt;gametype&gt;</c>).</summary>
    public const string ExemplarBucket = "exemplar";

    /// <summary>True for <c>exemplar</c> and <c>exemplar.&lt;gametype&gt;</c>.</summary>
    public static bool IsExemplarBucket(string bucket) =>
        bucket == ExemplarBucket || bucket.StartsWith(ExemplarBucket + ".", StringComparison.Ordinal);

    /// <summary>The size the promoted (shipped) norms must stay under (CORPUS-NORMS-SPEC §5.2).</summary>
    public const int ShippedMaxBytes = 150 * 1024;

    /// <summary>
    /// The candidate norms prepared for shipping: serialized like the candidate; when larger than
    /// <paramref name="maxBytes"/>, the per-bucket motif lists are cut to their most common entries (halving the cap until
    /// it fits — motifs are the only list the spec caps by count). Returns the text and the motif cap used (null = uncut).
    /// </summary>
    public static (string Text, int? MotifCap) PrepareForShipping(JsonObject candidate, int maxBytes = ShippedMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string text = LevelFingerprint.Serialize(candidate);
        int? cap = null;
        int max = candidate["motifs"] is JsonObject all ? all.Select(kv => (kv.Value as JsonArray)?.Count ?? 0).DefaultIfEmpty(0).Max() : 0;
        for (int c = max / 2; System.Text.Encoding.UTF8.GetByteCount(text) > maxBytes && c >= 1; c /= 2)
        {
            var copy = (JsonObject)candidate.DeepClone();
            if (copy["motifs"] is JsonObject motifs)
            {
                foreach (string bucket in motifs.Select(kv => kv.Key).ToList())
                {
                    if (motifs[bucket] is JsonArray arr && arr.Count > c)
                    {
                        motifs[bucket] = new JsonArray(arr.Take(c).Select(n => n!.DeepClone()).ToArray());
                    }
                }
            }

            text = LevelFingerprint.Serialize(copy);
            cap = c;
        }

        return (text, cap);
    }

    /// <summary>The buckets a level votes in; gametype conflicts count in overall and origin buckets only.</summary>
    public static IEnumerable<string> BucketsOf(NormsLevel l)
    {
        yield return "overall";
        yield return l.Origin == "retail" ? "retail" : "community";
        if (!l.Conflict)
        {
            yield return l.Gametype;
            if (l.Origin == "retail")
            {
                yield return "retail." + l.Gametype;
            }
        }
    }

    /// <summary>
    /// The per-class buckets a level votes in (E12; default-driving metrics only): <c>dm.&lt;S|M|L&gt;</c> by spawn count
    /// (E12's DM-S/M/L; CTF and KOTH keep one bucket each) and <c>&lt;gametype&gt;.&lt;class&gt;</c> by level class (not for
    /// payload: its gametype is its class).
    /// None for a gametype conflict, a single-player level or an unclassified multiplayer one (the design classes are
    /// multiplayer gametype classes).
    /// </summary>
    public static IEnumerable<string> ClassBucketsOf(NormsLevel l)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (l.Conflict || l.Gametype is "sp" or "mp_other")
        {
            yield break;
        }

        if (l.PlayerClass is { } pc && l.Gametype == "dm")
        {
            yield return l.Gametype + "." + pc;
        }

        if (l.LevelClass is { } cls && cls != "payload")
        {
            yield return l.Gametype + "." + cls;
        }
    }

    /// <summary>The publication threshold (§5.1).</summary>
    public static bool Qualifies(string bucket, IReadOnlyCollection<NormsLevel> levels, NormsOptions o)
    {
        if (IsExemplarBucket(bucket))
        {
            return levels.Count >= o.MinExemplarLevels;
        }

        if (bucket.StartsWith("retail", StringComparison.Ordinal))
        {
            return levels.Count >= o.MinRetailLevels;
        }

        int authors = levels.Select(l => l.Author).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Count();
        return levels.Count >= o.MinLevels && authors >= o.MinAuthors;
    }

    private static string Report(
        IReadOnlyList<NormsLevel> all,
        List<NormsLevel> voting,
        SortedDictionary<string, List<NormsLevel>> buckets,
        SortedSet<string> dropped,
        JsonObject norms,
        double? crossMedian,
        int crossLevels,
        NormsOptions o,
        ExemplarMatch exemplars)
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("# Corpus norms report\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Generated {o.Generated}; extractor version {FingerprintSchema.ExtractorVersion}.\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Fingerprinted levels (ok / ok_partial): {all.Count}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Voting levels: {voting.Count} (version-series collapse {(o.Collapse ? "on" : "off")}; {all.Count(l => !l.Vote)} older version(s) {(o.Collapse ? "removed" : "would be removed")})\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Retail: {voting.Count(l => l.Origin == "retail")}, community: {voting.Count(l => l.Origin != "retail")}, gametype conflicts: {voting.Count(l => l.Conflict)}\n\n");

        sb.Append("## Cross-method agreement (openings vs portals)\n\n");
        sb.Append(crossMedian is double c
            ? string.Create(CultureInfo.InvariantCulture, $"Median |opening.width - portal.width| = {c:0.###} m over {crossLevels} level(s) with matched pairs (limit {CrossMethodLimit} m): {(c <= CrossMethodLimit ? "PASS — opening.* may drive defaults" : "FAIL — opening.* ships as low / reference")}.\n\n")
            : "No level had an opening within 1 m of a vertical portal: not validated — opening.* ships as low / reference.\n\n");

        sb.Append("## Buckets\n\n| Bucket | Levels | Authors | Published |\n|---|---|---|---|\n");
        foreach ((string b, List<NormsLevel> members) in buckets)
        {
            int authors = members.Select(l => l.Author).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Count();
            sb.Append(CultureInfo.InvariantCulture, $"| {b} | {members.Count} | {authors} | {(Qualifies(b, members, o) ? "yes" : "no (below threshold)")} |\n");
        }

        if (dropped.Count > 0)
        {
            sb.Append("\nDropped (below the publication threshold for at least one metric): ").Append(string.Join(", ", dropped)).Append('\n');
        }

        sb.Append("\n## Exemplar set\n\n");
        if (!exemplars.ListPresent && exemplars.StockLevels == 0)
        {
            sb.Append("No exemplar list or stock-exemplar folder: no exemplar bucket (consumers use `overall`).\n");
        }
        else
        {
            List<NormsLevel> ex = all.Where(l => l.Exemplar).ToList();
            int exAuthors = ex.Select(l => l.Author).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Count();
            sb.Append(CultureInfo.InvariantCulture, $"- Exemplar levels: {ex.Count} from {exAuthors} author(s) ({exemplars.StockLevels} stock from the stock-exemplar folder; {exemplars.ListedNames} name(s) in the list)\n");
            sb.Append(CultureInfo.InvariantCulture, $"- By gametype: {string.Join(", ", ex.GroupBy(l => l.Gametype).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}\n");
            if (exemplars.UnmatchedNames.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- Listed but not found in the sweep (local report only): {string.Join(", ", exemplars.UnmatchedNames)}\n");
            }

            sb.Append("- Kit defaults and out-of-norm lint read the `exemplar` bucket (per metric fallback: `overall`).\n");
        }

        sb.Append("\n## Default-use metrics: exemplar / overall / retail / dm (p50, with p25–p75)\n\n| Metric | use | exemplar | overall | retail | dm | overall without collapse |\n|---|---|---|---|---|---|---|\n");
        JsonObject metrics = (JsonObject)norms["metrics"]!;
        foreach (string id in DefaultCandidates.Concat(new[] { "stair.riser", "stair.tread" }).OrderBy(x => x, StringComparer.Ordinal))
        {
            JsonNode? m = metrics[id];
            string Cell(string bucket) => m?["buckets"]?[bucket] is JsonObject row
                ? $"{F(row["p50"]!.GetValue<double>())} ({F(row["p25"]!.GetValue<double>())}–{F(row["p75"]!.GetValue<double>())}, n {row["n"]})"
                : "–";
            double[] noCollapse = all.Where(l => l.Values.ContainsKey(id)).Select(l => l.Values[id]).OrderBy(x => x).ToArray();
            string nc = noCollapse.Length > 0 ? F(Percentiles.Type7(noCollapse, 0.5)) : "–";
            sb.Append(CultureInfo.InvariantCulture, $"| {id} | {m?["use"]?.GetValue<string>() ?? "–"} | {Cell(ExemplarBucket)} | {Cell("overall")} | {Cell("retail")} | {Cell("dm")} | {nc} |\n");
        }

        sb.Append("\nstair.* stays `reference` until the debug-render eyeball set (CORPUS-NORMS-SPEC §4-3) accepts the detector.\n");
        return sb.ToString();
    }
}
