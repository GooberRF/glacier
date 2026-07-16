using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

// flagship 12 Priority 2: dmwarzone is the sole level where inc (17) is worse than pb (14) — the flip-blocker.
// Measure RED-original holes; dump the inc-extra open-edge cohort; check whether pb's lower count comes from
// buried z-fighting junk that happens to pair edges (overlaps), not real coverage.
public sealed class DmWarzoneDiag
{
    private readonly ITestOutputHelper _out;

    public DmWarzoneDiag(ITestOutputHelper output) => _out = output;

    // The corpus-recompiling cohort probes are heavy (LeafExtraction_Corpus_Hole_Comparison recompiles 14
    // levels x3 CSG paths, ~3 min) and would contend with the QaCorpusSweep/LightBakePerf 30 s ceilings in
    // the parallel suite. Opt-in (set GED_DMWARZONE_MEASURE=1) like DetailAttachDiag; the fast Analyze runs.
    private static bool MeasureEnabled => Environment.GetEnvironmentVariable("GED_DMWARZONE_MEASURE") == "1";

    [Fact]
    public void Analyze()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmwarzoneclassicb1.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? orig = null;
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                orig ??= gs.Geometry;
            }

            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        if (bs is null)
        {
            return;
        }

        List<Brush> brs = bs.Brushes.ToList();
        List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();

        var sb = new StringBuilder();
        int origHoles = orig is null ? -1 : HoleDetector.Detect(orig).Count;
        int origOverlaps = orig is null ? -1 : TraceDm04Diag.FindOverlaps(orig).Count;
        sb.AppendLine($"dmwarzoneclassicb1: RED-original holes={origHoles} overlapPairs={origOverlaps}");

        CompiledLevel pb = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false });
        CompiledLevel inc = GeometryCompiler.Compile(brs, eff, new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = true });

        var pbHoles = HoleDetector.Detect(pb.Geometry);
        var incHoles = HoleDetector.Detect(inc.Geometry);
        var pbOv = TraceDm04Diag.FindOverlaps(pb.Geometry);
        var incOv = TraceDm04Diag.FindOverlaps(inc.Geometry);
        float pbOvArea = pbOv.Count == 0 ? 0 : pbOv.Sum(o => o.Area);
        float incOvArea = incOv.Count == 0 ? 0 : incOv.Sum(o => o.Area);
        sb.AppendLine($"pb:  holes={pbHoles.Count} faces={pb.Geometry.Faces.Count} overlapPairs={pbOv.Count} overlapAreaSum={pbOvArea:F4} m2");
        sb.AppendLine($"inc: holes={incHoles.Count} faces={inc.Geometry.Faces.Count} overlapPairs={incOv.Count} overlapAreaSum={incOvArea:F4} m2");

        // The inc holes: list them, and for each, count pb faces overlapping that location (buried junk indicator).
        sb.AppendLine();
        sb.AppendLine("inc open-edge midpoints (the 17) and whether pb has geometry (paired/overlap) nearby:");
        foreach (Vec3 h in incHoles.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z))
        {
            // Is this location also open in pb?
            bool pbOpenNear = pbHoles.Any(p => p.Sub(h).Length() < 0.05f);
            sb.AppendLine($"  inc hole ({h.X:F3},{h.Y:F3},{h.Z:F3})  pbAlsoOpenHere={pbOpenNear}");
        }

        sb.AppendLine();
        sb.AppendLine("pb open-edge midpoints (the 14):");
        foreach (Vec3 h in pbHoles.OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z))
        {
            bool incOpenNear = incHoles.Any(p => p.Sub(h).Length() < 0.05f);
            sb.AppendLine($"  pb hole ({h.X:F3},{h.Y:F3},{h.Z:F3})  incAlsoOpenHere={incOpenNear}");
        }

        // Largest pb overlaps (z-fighting junk) with locations.
        sb.AppendLine();
        sb.AppendLine("pb largest coplanar overlap pairs (z-fighting junk; area m2):");
        foreach ((int a, int b, float area) in pbOv.OrderByDescending(o => o.Area).Take(12))
        {
            Vec3 ca = Centroid(pb.Geometry, a);
            sb.AppendLine($"  area={area:F4} at ({ca.X:F2},{ca.Y:F2},{ca.Z:F2})");
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "tests", "artifacts"));
            File.WriteAllText(Path.Combine(dir.FullName, "tests", "artifacts", "dmwarzone_diag.txt"), report);
        }
    }

    /// <summary>ITEM 4: is the 17-hole floor cohort weldable by a wider seal, or genuine gap/overlap?
    /// Sweeps SeamSealer tolerance and classifies each residual open edge by nearest-neighbour distance.</summary>
    [Fact]
    public void Cohort_Tractability_Sweep()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmwarzoneclassicb1.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault();
        RoomEffectsSection? es = rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>().FirstOrDefault();
        if (bs is null)
        {
            return;
        }

        List<Brush> brs = bs.Brushes.ToList();
        List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();

        foreach (float tol in new[] { 3e-3f, 1e-2f, 2e-2f, 5e-2f, 1e-1f, 2e-1f })
        {
            CompiledLevel c = GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = true, SealTolerance = tol });
            int holes = HoleDetector.Detect(c.Geometry).Count;
            _out.WriteLine($"SealTolerance={tol:F3} m -> holes={holes} faces={c.Geometry.Faces.Count}");
        }

        // Path comparison: old per-brush (IncrementalAccumulator=false, the ledger's "pb=14") vs the shipping
        // incremental fold vs leaf extraction — under the CURRENT machinery, with overlap-pair counts so the
        // "14" can be checked for z-fighting-junk pairing (flagship-12 finding).
        foreach ((string name, CompileOptions opt) in new[]
        {
            ("per-brush(inc=false)", new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = false }),
            ("incremental(ship)", new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = true }),
            ("leaf-extraction", new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true }),
        })
        {
            CompiledLevel c = GeometryCompiler.Compile(brs, eff, opt);
            int holes = HoleDetector.Detect(c.Geometry).Count;
            int ov = TraceDm04Diag.FindOverlaps(c.Geometry).Count;
            _out.WriteLine($"PATH {name}: holes={holes} faces={c.Geometry.Faces.Count} overlapPairs={ov}");
        }

        // Classify the residual open edges at the shipping tolerance: for each open-edge endpoint, the
        // nearest OTHER vertex on a wall face (a near-miss weldable pair vs a genuine gap).
        CompiledLevel ship = GeometryCompiler.Compile(brs, eff,
            new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = true });
        var holesShip = HoleDetector.Detect(ship.Geometry);
        _out.WriteLine($"shipping holes={holesShip.Count}; nearest-vertex gap per hole midpoint:");
        var verts = ship.Geometry.Vertices;
        foreach (Vec3 h in holesShip.OrderBy(v => v.X).ThenBy(v => v.Z))
        {
            float nearest = float.MaxValue;
            foreach (Vec3 v in verts)
            {
                float d = v.Sub(h).Length();
                if (d > 1e-4f && d < nearest)
                {
                    nearest = d;
                }
            }

            _out.WriteLine($"  hole ({h.X:F3},{h.Y:F3},{h.Z:F3}) nearestVertGap={nearest:F4} m");
        }
    }

    /// <summary>ITEM 4: leaf-extraction closes dmwarzone to 0 — is it a corpus-wide win or a per-level
    /// tradeoff? Compares shipping (incremental) vs leaf-extraction holes on every gated level + RED.</summary>
    [Fact]
    public void LeafExtraction_Corpus_Hole_Comparison()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        string[] levels =
        {
            "dm01.rfl", "dm02.rfl", "dm04.rfl", "dm05.rfl", "dm06.rfl", "glass_house.rfl",
            "ctf01.rfl", "ctf02.rfl", "ctfwlpro.rfl", "dmabruptdecayrc2a27.rfl", "kothcowb1~.rfl",
            "dmwarzoneclassicb1.rfl", "ctf07.rfl", "dm15.rfl",
        };

        _out.WriteLine("level                         RED   inc  leaf   (holes)");
        foreach (string lvl in levels)
        {
            string p = Path.Combine(Corpus.Directory!, lvl);
            if (!File.Exists(p))
            {
                continue;
            }

            RflFile rfl = RflFile.Load(p);
            rfl.ParseAllKnownSections();
            Geometry? red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
            BrushesSection? bs = rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault();
            RoomEffectsSection? es = rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>().FirstOrDefault();
            if (bs is null)
            {
                continue;
            }

            List<Brush> brs = bs.Brushes.ToList();
            List<RoomEffect> eff = es?.Effects.ToList() ?? new List<RoomEffect>();
            int redH = red is null ? -1 : HoleDetector.Detect(red).Count;
            int incH = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, IncrementalAccumulator = true }).Geometry).Count;
            int leafH = HoleDetector.Detect(GeometryCompiler.Compile(brs, eff,
                new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true }).Geometry).Count;
            string flag = leafH > incH ? "  WORSE" : leafH < incH ? "  better" : "";
            _out.WriteLine($"{lvl,-28} {redH,4} {incH,5} {leafH,5}{flag}");
        }
    }

    private static Vec3 Centroid(Geometry g, int faceIdx)
    {
        Face f = g.Faces[faceIdx];
        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            c = c.Add(g.Vertices[v.Index]);
        }

        return c.Scale(1f / Math.Max(1, f.Vertices.Count));
    }
}
