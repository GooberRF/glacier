using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Automation;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Cli;

/// <summary>
/// <c>glacier-cli recreate-score</c>: the recreation benchmark's scorer (<see cref="RecreationScorer"/>), a development
/// measuring stick for how closely a level recreates a reference level or a digitised mockup. File work in this
/// process only: both levels are read, never modified; score.json and the overlay PNGs go to <c>--out</c>.
/// </summary>
internal static class RecreateCli
{
    public const string Verb = "recreate-score";

    public static int Run(CliArguments a, TextWriter stdout)
    {
        if (a.Attach || a.Save || a.Light || a.DryRun || a.Sealed || a.NoBuild || a.RenderOptions.Count > 0 || a.SpatialOptions.Count > 0 || a.RefOptions.Count > 0)
        {
            throw new UsageException("That option does not apply to 'recreate-score'.", Verb);
        }

        if (a.Positionals.Count != 1)
        {
            throw new UsageException("recreate-score needs one candidate level.", Verb);
        }

        if (!a.RecreateOptions.TryGetValue("--truth", out string? truthPath) || string.IsNullOrEmpty(truthPath))
        {
            throw new UsageException("recreate-score needs --truth <level.rfl | truth.json>.", Verb);
        }

        string outDir = a.Out ?? throw new UsageException("recreate-score needs --out <dir>.", Verb);
        bool mirror = a.RecreateOptions.ContainsKey("--allow-mirror");
        string candPath = Path.GetFullPath(a.Positionals[0]);
        truthPath = Path.GetFullPath(truthPath);
        foreach (string p in new[] { candPath, truthPath })
        {
            if (!File.Exists(p))
            {
                throw new UsageException($"File not found: {p}", Verb);
            }
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        RflFile cand = RflFile.Load(candPath);
        (Geometry cgeo, string cstate) = GeometryOf(cand);
        RecreationScoreResult result;
        string tstate = "mockup";
        if (RecreationScorer.IsMockupTruth(truthPath))
        {
            result = RecreationScorer.ScoreAgainstMockup(cgeo, cand, truthPath, mirror, CancellationToken.None);
        }
        else
        {
            RflFile truth = RflFile.Load(truthPath);
            (Geometry tgeo, tstate) = GeometryOf(truth);
            result = RecreationScorer.ScoreAgainstLevel(cgeo, cand, tgeo, truth, mirror, CancellationToken.None);
        }

        Directory.CreateDirectory(outDir);
        JsonObject report = result.Report;
        report["candidatePath"] = candPath;
        report["truthPath"] = truthPath;
        report["totalSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
        report["geometry"] = new JsonObject { ["candidate"] = cstate, ["truth"] = tstate };
        report["overlays"] = new JsonArray(result.Images.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray());
        foreach ((string name, byte[] png) in result.Images)
        {
            File.WriteAllBytes(Path.Combine(outDir, name), png);
        }

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outDir, "score.json"), report.ToJsonString(opts));
        if (a.Json)
        {
            stdout.WriteLine(new JsonObject { ["ok"] = true, ["command"] = Verb, ["result"] = report.DeepClone() }.ToJsonString(opts));
        }
        else
        {
            stdout.WriteLine($"Score {result.Score:0.0} / 100  ({Path.GetFileName(candPath)} vs {Path.GetFileName(truthPath)})");
            foreach ((string key, JsonNode? c) in report["components"]!.AsObject())
            {
                stdout.WriteLine($"  {key,-14} {c!["value"]}  x{c["weight"]} = {c["points"]}");
            }

            stdout.WriteLine($"Wrote {Path.Combine(outDir, "score.json")} and {result.Images.Count} overlay(s).");
        }

        return ExitCodes.Ok;
    }

    /// <summary>The level's stored compiled geometry, or a private preview build when it has none.</summary>
    private static (Geometry Geo, string State) GeometryOf(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        Geometry? stored = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        if (stored is { Faces.Count: > 0 })
        {
            return (stored, "stored");
        }

        var doc = new EditorDocument(rfl);
        CompiledLevel built = LevelOperations.Compile(doc, vfs: null, BuildSettings.EditorDefaults, sealedBuild: false, bakeLighting: false, shadows: false, traits: null, CancellationToken.None);
        return (built.Geometry, "preview build (none stored)");
    }
}
