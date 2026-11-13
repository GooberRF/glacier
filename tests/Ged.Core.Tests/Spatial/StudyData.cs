using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ged.Core.Spatial;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The exemplar study's local data (research/exemplar-study, gitignored): the label map, the per-level metric table
/// (data/metrics.csv), the grader results (data/grader.json) and the texture pixel sizes the study measured with; the
/// study's input levels (copies) in research/fixtures/exemplar-study/levels, the stock ones also in research/example_rfls.
/// Tests that need it skip when it is absent.
/// </summary>
internal static class StudyData
{
    public static string? Root { get; } = TestPaths.Research is { } r && Directory.Exists(Path.Combine(r, "exemplar-study", "data"))
        ? Path.Combine(r, "exemplar-study") : null;

    public static string? LevelsDir { get; } = TestPaths.ResearchFixtures is { } f && Directory.Exists(Path.Combine(f, "exemplar-study", "levels"))
        ? Path.Combine(f, "exemplar-study", "levels") : null;

    public static bool Available => Root is not null;

    /// <summary>label-map.csv: level stem → (label, role, gametype).</summary>
    public static IReadOnlyDictionary<string, (string Label, string Role, string Gametype)> Labels => _labels ??= LoadLabels();

    private static IReadOnlyDictionary<string, (string, string, string)>? _labels;

    private static Dictionary<string, (string, string, string)> LoadLabels()
    {
        var d = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(Root!, "label-map.csv")).Skip(1))
        {
            List<string> f = Csv(line);
            d[f[1]] = (f[0], f[2], f[3]);
        }

        return d;
    }

    /// <summary>metrics.csv: level stem → metric id → value (null = empty cell).</summary>
    public static IReadOnlyDictionary<string, Dictionary<string, double?>> Metrics => _metrics ??= LoadMetrics();

    private static IReadOnlyDictionary<string, Dictionary<string, double?>>? _metrics;

    private static Dictionary<string, Dictionary<string, double?>> LoadMetrics()
    {
        string[] lines = File.ReadAllLines(Path.Combine(Root!, "data", "metrics.csv"));
        List<string> head = Csv(lines[0]);
        var d = new Dictionary<string, Dictionary<string, double?>>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1))
        {
            List<string> f = Csv(line);
            var row = new Dictionary<string, double?>(StringComparer.Ordinal);
            for (int i = 6; i < head.Count && i < f.Count; i++)
            {
                row[head[i]] = double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
            }

            d[f[0]] = row;
        }

        return d;
    }

    /// <summary>grader.json per_level: level stem → the failing check ids.</summary>
    public static IReadOnlyDictionary<string, List<string>> Failures => _failures ??= JsonNode.Parse(File.ReadAllText(Path.Combine(Root!, "data", "grader.json")))!["per_level"]!.AsObject()
        .ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(x => x!.GetValue<string>()).ToList(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, List<string>>? _failures;

    /// <summary>The texture pixel sizes the study measured with (research/fixtures/exemplar-study/texsizes.json), or null.</summary>
    public static Func<string, (int, int)?>? TextureSizes => _sizes.Value;

    private static readonly Lazy<Func<string, (int, int)?>?> _sizes = new(() =>
    {
        string? path = TestPaths.ResearchFixtures is { } f ? Path.Combine(f, "exemplar-study", "texsizes.json") : null;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        Dictionary<string, int[]> map = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(path))!;
        return n => map.TryGetValue(n, out int[]? wh) && wh.Length == 2 ? (wh[0], wh[1]) : null;
    });

    /// <summary>The study's input copy of a level: the fixture copy, else (stock levels) research/example_rfls; null when absent.</summary>
    public static string? LevelPath(string stem)
    {
        if (LevelsDir is not null && File.Exists(Path.Combine(LevelsDir, stem + ".rfl")))
        {
            return Path.Combine(LevelsDir, stem + ".rfl");
        }

        return Corpus.Directory is { } c && File.Exists(Path.Combine(c, stem + ".rfl")) ? Path.Combine(c, stem + ".rfl") : null;
    }

    /// <summary>A study level with the study's texture sizes.</summary>
    public static SpatialLevel Load(string path)
    {
        SpatialLevel level = SpatialLevel.FromBytes(File.ReadAllBytes(path), Path.GetFileName(path));
        return TextureSizes is { } t ? level.WithTextureSize(n => t(n)) : level;
    }

    /// <summary>The metrics that need texture pixel sizes (compared only when the study's sizes are present).</summary>
    public static readonly HashSet<string> TextureSizeMetrics = new(StringComparer.Ordinal) { "ppm_p50", "texel_within25", "texel_iqr_rel", "uv_aniso" };

    private static List<string> Csv(string line)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                q = !q;
                continue;
            }

            if (c == ',' && !q)
            {
                res.Add(cur.ToString());
                cur.Clear();
                continue;
            }

            cur.Append(c);
        }

        res.Add(cur.ToString());
        return res;
    }
}
