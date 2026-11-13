using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Opt-in measurement (GED_ALPINE_REF_MEASURE=1) of Glacier's build against the Alpine RED reference
/// builds (<c>research/fixtures/alpine-ref/&lt;id&gt;/build</c>): non-detail open edges (the
/// <see cref="HoleDetector"/> rule <see cref="HoleParityGateTests"/> uses), rooms, portals and faces, for
/// several compile variants. GED_ALPINE_REF_LEVELS narrows the set (comma list); GED_ALPINE_REF_VARIANTS picks
/// variants (comma list of: norepair, repair, repairnoseal, repairthenseal, noseam). Writes
/// <c>tests/artifacts/alpine_ref_measure.txt</c>.
/// </summary>
public sealed class AlpineRefMeasureDiag
{
    private readonly ITestOutputHelper _out;

    public AlpineRefMeasureDiag(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("GED_ALPINE_REF_MEASURE") == "1";

    private static CompileOptions Variant(string name) => name switch
    {
        "norepair" => new CompileOptions { BuildSurfaces = false, AlpineRepair = false },
        "repair" => new CompileOptions { BuildSurfaces = false, AlpineRepair = true },
        "repairnoseal" => new CompileOptions { BuildSurfaces = false, AlpineRepair = true, SealTolerance = 0f },
        "repairthenseal" => new CompileOptions { BuildSurfaces = false, AlpineRepair = true, RepairSealOrder = RepairSealOrder.RepairThenSeal },
        "noseam" => new CompileOptions { BuildSurfaces = false, AlpineRepair = true, CoplanarSeamTolerance = false },
        _ => throw new ArgumentException(name),
    };

    /// <summary>GED_ALPINE_REF_BISECT=level: holes with each repair pass skipped in turn, plus the new open edges.</summary>
    [Fact]
    public void Bisect()
    {
        string? name = Environment.GetEnvironmentVariable("GED_ALPINE_REF_BISECT");
        if (string.IsNullOrEmpty(name) || AlpineReference.Find()?.Get(name) is not { } level
            || !AlpineReference.TryLoad(level, out AlpineReference.Loaded? loaded) || loaded is null)
        {
            return;
        }

        Geometry baseGeo = GeometryCompiler.Compile(loaded.Brushes, loaded.Effects, Variant("norepair")).Geometry;
        var baseHoles = new HashSet<(float, float, float)>(HoleDetector.Detect(baseGeo).Select(v => (v.X, v.Y, v.Z)));
        _out.WriteLine($"norepair holes {baseHoles.Count}");
        foreach (BuildRepair.Pass skip in Enum.GetValues<BuildRepair.Pass>())
        {
            var o = new CompileOptions { BuildSurfaces = false, RepairSkip = skip };
            Geometry g = GeometryCompiler.Compile(loaded.Brushes, loaded.Effects, o).Geometry;
            List<Vec3> holes = HoleDetector.Detect(g);
            _out.WriteLine($"skip {skip}: holes {holes.Count}");
            if (skip == BuildRepair.Pass.None)
            {
                foreach (Vec3 h in holes.Where(v => !baseHoles.Contains((v.X, v.Y, v.Z))))
                {
                    _out.WriteLine($"  new open edge at {h}");
                    DumpAround(g, h, "repair");
                    DumpAround(baseGeo, h, "norepair");
                }

                foreach (Vec3 h in HoleDetector.Detect(baseGeo).Where(v => !holes.Any(x => x.ApproxEquals(v, 1e-4f))))
                {
                    _out.WriteLine($"  closed edge at {h}");
                }
            }
        }
    }

    private void DumpAround(Geometry g, Vec3 p, string tag)
    {
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            bool near = false;
            for (int k = 0; k < f.Vertices.Count; k++)
            {
                Vec3 a = g.Vertices[f.Vertices[k].Index];
                Vec3 b = g.Vertices[f.Vertices[(k + 1) % f.Vertices.Count].Index];
                Vec3 ab = b.Sub(a);
                float t = Math.Clamp(p.Sub(a).Dot(ab) / Math.Max(ab.Dot(ab), 1e-12f), 0f, 1f);
                near |= a.Add(ab.Scale(t)).Sub(p).Length() < 2e-3f;
            }

            if (!near)
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.Append($"    [{tag}] face {fi} room {f.RoomIndex} flags 0x{f.Flags:X} tex {f.Texture} portal {f.PortalIndexPlus2} id {f.FaceId}:");
            foreach (FaceVertex fv in f.Vertices)
            {
                Vec3 v = g.Vertices[fv.Index];
                sb.Append($" {fv.Index}({v.X:0.#####},{v.Y:0.#####},{v.Z:0.#####})");
            }

            _out.WriteLine(sb.ToString());
        }
    }

    [Fact]
    public void Measure()
    {
        if (!Enabled || AlpineReference.Find() is not { } refs)
        {
            return;
        }

        string[] variants = (Environment.GetEnvironmentVariable("GED_ALPINE_REF_VARIANTS") ?? "norepair,repair")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HashSet<string>? only = Environment.GetEnvironmentVariable("GED_ALPINE_REF_LEVELS") is { Length: > 0 } l
            ? new HashSet<string>(l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            : null;

        var sb = new StringBuilder();
        sb.AppendLine($"Alpine RED reference ({refs.Id}) vs Glacier. {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("columns: holes (HoleDetector non-detail open edges) / holes by Alpine RED's own per-room CheckForHoles rule / main rooms / portals / faces; [Glacier repair counters]; Glacier variants: " + string.Join(", ", variants));
        foreach (AlpineReference.Level level in refs.Levels)
        {
            if (only is not null && !only.Contains(level.Name))
            {
                continue;
            }

            if (!AlpineReference.TryLoad(level, out AlpineReference.Loaded? loaded) || loaded is null)
            {
                sb.AppendLine($"{level.Name,-30} (unreadable)");
                continue;
            }

            var line = new StringBuilder();
            line.Append($"{level.Name,-30} stock {(loaded.Stock is null ? "-" : AlpineReference.Stats(loaded.Stock))} | alpine {AlpineReference.Stats(loaded.Reference)}");
            foreach (string v in variants)
            {
                CompiledLevel c = GeometryCompiler.Compile(loaded.Brushes, loaded.Effects, Variant(v));
                BuildRepairStats r = c.Report.Repair;
                line.Append($" | {v} {AlpineReference.Stats(c.Geometry)}");
                if (v != "norepair")
                {
                    line.Append($" [{r.HolesAfterRooms},{r.PortalPlanesUnified},{r.PortalCopiesMoved},{r.SplitRoomsJoined},{r.VerticesWelded},{r.EdgesSplit},{r.CollapsedFacesRemoved},{r.PortalStripsRemoved},{r.PortalGapsFilled},{r.WallGapsFilled},{r.UnplacedPortalFacesRemoved}]");
                }
            }

            line.Append($" | alpine counters [{string.Join(",", level.Counters)}]");
            _out.WriteLine(line.ToString());
            sb.AppendLine(line.ToString());
        }

        string? root = TestPaths.RepoRoot;
        if (root is not null)
        {
            string dir = Path.Combine(root, "tests", "artifacts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Environment.GetEnvironmentVariable("GED_ALPINE_REF_OUT") ?? "alpine_ref_measure.txt"), sb.ToString());
        }
    }
}
