using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Automation;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Cli;

/// <summary>
/// <c>glacier-cli audit &lt;level.rfl&gt; [--json] [--out report.json] [--rf-dir dir]</c>: the craft audit
/// (<see cref="CraftAudit"/>) on the level's stored compiled geometry (a sealed build is made in this process when it
/// has none; the file is never modified). Exit code 0 when clean, 1 when there are findings.
/// </summary>
internal static class AuditCli
{
    public const string Verb = "audit";

    public static int Run(CliArguments a, TextWriter stdout)
    {
        if (a.Attach || a.Save || a.Light || a.DryRun || a.RenderOptions.Count > 0 || a.SpatialOptions.Count > 0 || a.RefOptions.Count > 0)
        {
            throw new UsageException("That option does not apply to 'audit'.", Verb);
        }

        if (a.Positionals.Count != 1)
        {
            throw new UsageException("audit needs one level.", Verb);
        }

        string path = Path.GetFullPath(a.Positionals[0]);
        if (!File.Exists(path))
        {
            throw new UsageException($"File not found: {path}", Verb);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        var doc = new EditorDocument(rfl, path);
        Geometry? geo = SpatialLevel.StoredGeometry(rfl);
        string state = "stored";
        AssetVfs? vfs = OpenVfs(a);
        if (geo is not { Faces.Count: > 0 } || a.Sealed)
        {
            CompiledLevel built = LevelOperations.Compile(doc, vfs, BuildSettings.EditorDefaults, sealedBuild: true, bakeLighting: false, shadows: false, traits: null, CancellationToken.None);
            geo = built.Geometry;
            state = "sealed build (this process)";
        }

        SpatialLevel level = SpatialLevel.FromDocument(doc, geo, new GeometryFreshness("built", state), Path.GetFileName(path));
        CraftAuditReport report = CraftAudit.Run(level, new CraftAuditOptions
        {
            MaxPerCheck = 200,
            ObjectBounds = vfs is not null ? CraftAudit.MeshBoundsFrom(vfs) : null,
            // No wall-clock limit: the done gate must give the same findings on a loaded machine as on an idle one
            // (the lattice budget, not the clock, bounds the measurement).
            Budget = new SpatialBudget { TimeLimit = null },
        });
        if (vfs is null)
        {
            report.Notes.Add("No RF install is mounted (--rf-dir): objects were tested at their origins, not their mesh bounds.");
        }

        JsonObject json = report.ToJson();
        json["level"] = path;
        json["geometry"] = state;
        json["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        if (a.Out is { } outPath)
        {
            File.WriteAllText(Path.GetFullPath(outPath), json.ToJsonString(opts));
        }

        if (a.Json)
        {
            stdout.WriteLine(new JsonObject { ["ok"] = true, ["command"] = Verb, ["result"] = json }.ToJsonString(opts));
        }
        else
        {
            stdout.WriteLine($"Craft audit of {Path.GetFileName(path)}: {(report.Clean ? "clean" : $"{json["total"]} finding(s)")}{(report.Complete ? string.Empty : $", INCOMPLETE (not run: {string.Join(", ", report.Skipped)})")}  ({string.Join(", ", report.Counts.Select(kv => $"{kv.Key} {kv.Value}"))})");
            foreach (CraftFinding f in report.Findings)
            {
                stdout.WriteLine($"  [{f.Check}/{f.Kind}] {f.Message}");
                stdout.WriteLine($"      fix: {f.Fix}");
            }

            foreach (string n in report.Notes)
            {
                stdout.WriteLine($"  note: {n}");
            }
        }

        return report.Clean ? ExitCodes.Ok : 1;
    }

    private static AssetVfs? OpenVfs(CliArguments a)
    {
        string? dir = a.RfDir ?? Environment.GetEnvironmentVariable("GED_RF_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            return GameMount.Mount(dir);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
