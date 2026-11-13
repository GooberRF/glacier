using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Analysis;
using Ged.Core.Compiler;
using Ged.Core.Tests.Scripting;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// Synthetic levels for the fingerprint tests: a kit script run on a fresh level, given a SEALED Shared BSP build
/// that is applied and serialized — so the metrics read exactly what a saved level stores.
/// </summary>
internal static class AnalysisFixtures
{
    /// <summary>The kit script → saved level bytes with compiled geometry.</summary>
    public static byte[] Build(string kitBody)
    {
        KitTestHarness.KitRun run = KitTestHarness.Run(kitBody);
        CompiledLevel compiled = KitTestHarness.Sealed(run.Doc, sharedBsp: true);
        GeometryBuildService.Apply(run.Doc.Rfl, compiled);
        return run.Doc.SaveToBytes(updateTimestamp: false);
    }

    /// <summary>Fingerprints <paramref name="bytes"/> with default options (file name given).</summary>
    public static LevelFingerprint Analyze(byte[] bytes, string fileName = "fixture.rfl", MetricOptions? options = null) =>
        LevelMetrics.Analyze(bytes, options ?? new MetricOptions { FileName = fileName }, CancellationToken.None);

    /// <summary>A metric's per-level summary value: p50 for a distribution, the number for a scalar; null when absent.</summary>
    public static double? Value(LevelFingerprint fp, string metricId)
    {
        int dot = metricId.IndexOf('.', StringComparison.Ordinal);
        JsonNode? node = fp.Json[metricId[..dot]]?[metricId[(dot + 1)..]];
        return node switch
        {
            JsonObject o => o["p50"]?.GetValue<double>(),
            JsonValue v when v.TryGetValue(out double d) => d,
            JsonValue v when v.TryGetValue(out int i) => i,
            _ => null,
        };
    }

    /// <summary>The 3 golden fixtures (§8.2).</summary>
    public static readonly (string Name, string Kit)[] Goldens =
    {
        ("box-room", "kit.room{name = 'r', pos = {0, 0, 0}, size = {12, 4, 12}}\n" +
                     "level.place{kind = 'respawn', pos = {2, 1, 2}}\n" +
                     "level.place{kind = 'light', pos = {0, 3, 0}}"),
        ("rooms-door-stairs",
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {12, 6, 12}}\n" +
            "local b = kit.room{name = 'b', pos = {13, 0, 0}, size = {12, 6, 12}}\n" +
            "kit.doorway{name = 'd', a = a, b = b, width = 3, height = 3}\n" +
            "kit.stairs{name = 's', pos = {10, 0, -2}, dir = '+x', rise = 2, width = 3}"),
        // Explicit 3 x 3 m doorways: the extractor fixtures do not follow the kit's (norm-sourced) default sizes.
        ("portal-loop",
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {10, 4, 10}}\n" +
            "local b = kit.room{name = 'b', pos = {11, 0, 0}, size = {10, 4, 10}}\n" +
            "local c = kit.room{name = 'c', pos = {11, 0, 11}, size = {10, 4, 10}}\n" +
            "local d = kit.room{name = 'd', pos = {0, 0, 11}, size = {10, 4, 10}}\n" +
            "kit.doorway{name = 'ab', width = 3, height = 3, a = a, b = b, portal = true}\n" +
            "kit.doorway{name = 'bc', width = 3, height = 3, a = b, b = c, portal = true}\n" +
            "kit.doorway{name = 'cd', width = 3, height = 3, a = c, b = d, portal = true}\n" +
            "kit.doorway{name = 'da', width = 3, height = 3, a = d, b = a, portal = true}\n" +
            "local e = level.place{kind = 'event', class = 'Delay', pos = {0, 1, 0}}\n" +
            "level.place{kind = 'trigger', pos = {2, 1, 0}, links = {e}}"),
    };

    /// <summary>The repository root (located by Glacier.sln).</summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Glacier.sln not found above the test binaries.");
        }
    }

    public static string GoldenDir => Path.Combine(RepoRoot, "tests", "Ged.Core.Tests", "Analysis", "Golden");
}
