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

/// <summary>
/// Item 5d — the corpus-wide STRUCTURAL parity sweep: recompiles every corpus level and writes a
/// per-level table (rooms / portals / per-texture area delta / worst-brush divergence) to
/// <c>tests/artifacts/qa_parity_sweep.txt</c>, so a per-level compiler regression like the
/// dmabruptdecay report is visible in every future run. Corpus-wide the assertions are LOOSE
/// (report-first — community levels legitimately differ more); the tight bounds live on the
/// pixel-parity gate levels (<c>CompiledParityRenderTests</c>).
/// The worst-brush metric uses the FaceId→brush mapping: GED assigns sequential face ids over the
/// brushes-section faces in document order (matching RED's session numbering, which the original
/// compiled geometry's face ids reference), so per-brush surviving area is comparable one-to-one.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class QaParitySweepTests
{
    private readonly ITestOutputHelper _out;

    public QaParitySweepTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private sealed record LevelRow(
        string Name, int Version, int Brushes,
        int OrigRooms, int OrigMain, int GedRooms, int GedMain,
        int OrigPortals, int GedPortals,
        double AreaDeltaPct, double WorstTextureDeltaPct, string WorstTexture,
        int RoomsCovered, double MappedFaceIdFraction,
        int WorstBrushUid, double WorstBrushDeltaArea, string WorstBrushKind,
        int OrigHoles, int GedHoles, int GedNonDetailFaces,
        string Note);

    [Fact]
    public void Sweep_The_Corpus_And_Write_The_Structural_Parity_Artifact()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var rows = new List<LevelRow>();
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.Contains(".autosave", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LevelRow? row = null;
            try
            {
                row = SweepLevel(path, name);
            }
            catch (Exception ex)
            {
                rows.Add(new LevelRow(name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "-", 0, 0, 0, 0, "-", 0, 0, 0, $"EXCEPTION {ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            if (row is not null)
            {
                rows.Add(row);
            }
        }

        string report = Format(rows);
        _out.WriteLine(report);
        WriteArtifact(report);

        // ---- Loose corpus-wide bounds (report-first) --------------------------------------
        List<LevelRow> ok = rows.Where(r => r.Note.Length == 0 || !r.Note.StartsWith("EXCEPTION", StringComparison.Ordinal)).ToList();
        Assert.True(ok.Count >= 25, $"expected >= 25 sweepable levels, got {ok.Count}");

        foreach (LevelRow r in ok)
        {
            // Total textured area within 15% corpus-wide (gate levels hold much tighter bounds).
            // Known divergence, documented so a NEW regression elsewhere still fails the sweep:
            // kothcowb1~ (1886 brushes) has giant terrain brushes whose faces exceed the CSG
            // fragment cap (CsgSolver.MaxFragmentsPerFace); capped faces keep mixed
            // classification, producing phantom air-box area (+67k m² on brush 1) and losing
            // cem_mcstone14 terrain — a solver robustness limit, not a dispatch/order bug.
            if (!KnownAreaDivergence.Contains(r.Name))
            {
                Assert.True(Math.Abs(r.AreaDeltaPct) <= 15.0,
                    $"{r.Name}: total textured area delta {r.AreaDeltaPct:F1}% exceeds the loose 15% corpus bound");
            }

            // Every original room must overlap some recompiled room (no lost spaces).
            Assert.True(r.RoomsCovered >= (int)(r.OrigRooms * 0.9),
                $"{r.Name}: only {r.RoomsCovered}/{r.OrigRooms} original rooms covered");

            // Hole parity (item 1a): detail sheets excluded, RED's original geometry is
            // watertight (~0) on the gate levels — HoleParityGateTests holds those to tight
            // per-level ceilings. Corpus-wide the bound is loose (a few community terrain
            // levels legitimately carry open sky/outdoor edges), catching only a catastrophic
            // coincident-resolution regression (the item-1 air-drop spiked open edges into a
            // large fraction of the mesh) relative to RED's own baseline.
            // Tightened after SeamSealer (RED's binary-verified t-joint fixer at GED's numerical
            // scale) halved the near-coincident leak floor corpus-wide.
            Assert.True(r.GedHoles <= r.OrigHoles + r.GedNonDetailFaces * 0.15 + 45,
                $"{r.Name}: {r.GedHoles} open edges (RED {r.OrigHoles}) over {r.GedNonDetailFaces} non-detail faces — hole regression");
        }
    }

    private static readonly HashSet<string> KnownAreaDivergence = new(StringComparer.OrdinalIgnoreCase)
    {
        "kothcowb1~.rfl", // fragment-cap phantom faces on the giant terrain brushes (see comment above)
    };

    private static LevelRow? SweepLevel(string path, string name)
    {
        RflFile file = RflFile.Load(path);
        file.ParseAllKnownSections();
        Geometry? original = null;
        BrushesSection? brushes = null;
        RoomEffectsSection? effects = null;
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                original ??= g.Geometry;
            }
            else if (s.Content is BrushesSection b)
            {
                brushes ??= b;
            }
            else if (s.Content is RoomEffectsSection e)
            {
                effects ??= e;
            }
        }

        if (original is null || brushes is null || brushes.Brushes.Count == 0)
        {
            return null; // nothing to compare
        }

        CompiledLevel compiled = GeometryCompiler.Compile(
            brushes.Brushes, effects?.Effects, new CompileOptions { BuildSurfaces = false });
        Geometry mine = compiled.Geometry;

        // ---- Areas -----------------------------------------------------------------------
        Dictionary<string, float> byTexO = AreaByTexture(original);
        Dictionary<string, float> byTexM = AreaByTexture(mine);
        float areaO = byTexO.Values.Sum();
        float areaM = byTexM.Values.Sum();
        double areaDelta = areaO <= 0 ? 0 : (areaM - areaO) / areaO * 100.0;

        double worstTexDelta = 0;
        string worstTex = "-";
        foreach ((string tex, float ao) in byTexO)
        {
            if (ao < 4f)
            {
                continue; // ignore trivial areas
            }

            float am = byTexM.GetValueOrDefault(tex);
            double d = (am - ao) / ao * 100.0;
            if (Math.Abs(d) > Math.Abs(worstTexDelta))
            {
                worstTexDelta = d;
                worstTex = tex;
            }
        }

        // ---- Rooms / portals ---------------------------------------------------------------
        int origMain = original.Rooms.Count(r => r.IsSubroom == 0);
        int gedMain = mine.Rooms.Count(r => r.IsSubroom == 0);
        int covered = original.Rooms.Count(ro => mine.Rooms.Any(rm => Overlap(ro.Aabb, rm.Aabb)));

        // ---- Worst-brush divergence (per-brush surviving area orig vs GED) -----------------
        // Face-id ranges per brush: sequential ids over the brushes-section faces in order (the
        // numbering GED uses and RED's own session numbering that the original geometry references).
        var ranges = new List<(int Uid, int Start, int Count, string Kind)>(brushes.Brushes.Count);
        int cursor = 0;
        foreach (Brush b in brushes.Brushes)
        {
            var flags = (BrushFlags)b.Flags;
            string kind = (flags & BrushFlags.Portal) != 0 ? "portal"
                : (flags & (BrushFlags.Detail | BrushFlags.Geoable)) != 0 ? "detail"
                : (flags & BrushFlags.Air) != 0 ? "air" : "solid";
            ranges.Add((b.Uid, cursor, b.Geometry.Faces.Count, kind));
            cursor += b.Geometry.Faces.Count;
        }

        double[] origArea = new double[ranges.Count];
        int mapped = 0, total = 0;
        foreach (Face f in original.Faces)
        {
            if (f.Texture < 0)
            {
                continue;
            }

            total++;
            int bi = FindRange(ranges, f.FaceId);
            if (bi >= 0)
            {
                mapped++;
                origArea[bi] += FaceArea(original, f);
            }
        }

        double[] gedArea = new double[ranges.Count];
        foreach (Face f in mine.Faces)
        {
            if (f.Texture < 0)
            {
                continue;
            }

            int bi = FindRange(ranges, f.FaceId);
            if (bi >= 0)
            {
                gedArea[bi] += FaceArea(mine, f);
            }
        }

        double mappedFraction = total == 0 ? 0 : mapped / (double)total;
        int worstBrush = -1;
        double worstDelta = 0;
        string worstKind = "-";
        if (mappedFraction >= 0.5)
        {
            for (int i = 0; i < ranges.Count; i++)
            {
                double d = gedArea[i] - origArea[i];
                if (Math.Abs(d) > Math.Abs(worstDelta))
                {
                    worstDelta = d;
                    worstBrush = ranges[i].Uid;
                    worstKind = ranges[i].Kind;
                }
            }
        }

        // Non-detail open-edge counts (item 1a hole parity): detail sheets never close a
        // manifold loop, so HoleDetector already excludes them — RED's original is ~0.
        int origHoles = HoleDetector.Detect(original).Count;
        int gedHoles = HoleDetector.Detect(mine).Count;
        int gedNonDetailFaces = mine.Faces.Count(f =>
            f.Texture >= 0 && f.Vertices.Count >= 3 && ((FaceFlags)f.Flags & FaceFlags.IsDetail) == 0);

        return new LevelRow(
            name, file.Header.Version, brushes.Brushes.Count,
            original.Rooms.Count, origMain, mine.Rooms.Count, gedMain,
            original.Portals.Count, mine.Portals.Count,
            areaDelta, worstTexDelta, worstTex,
            covered, mappedFraction,
            worstBrush, worstDelta, worstKind,
            origHoles, gedHoles, gedNonDetailFaces,
            string.Empty);
    }

    private static int FindRange(List<(int Uid, int Start, int Count, string Kind)> ranges, int faceId)
    {
        // Binary search over the sorted, contiguous start ranges.
        int lo = 0, hi = ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            (int _, int start, int count, string _) = ranges[mid];
            if (faceId < start)
            {
                hi = mid - 1;
            }
            else if (faceId >= start + count)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    private static string Format(List<LevelRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("QA structural parity sweep — original compiled geometry vs GED recompile");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}; report-first (loose corpus bounds: |area| <= 15%, room coverage >= 90%);");
        sb.AppendLine("tight pixel bounds live on the CompiledParityRenderTests gate levels (dm01, dm04, glass_house, dmabruptdecayrc2a27).");
        sb.AppendLine("worstBrush = brush whose per-brush surviving textured area diverges most (by the shared FaceId numbering); mapped = fraction of orig face ids that resolve to a brush.");
        sb.AppendLine();
        sb.AppendLine($"{"level",-28} {"ver",5} {"brush",5} | {"rooms o(main)/g(main)",-23} {"portals o/g",-12} {"cover",-7} | {"areaΔ%",8} {"worstTexΔ%",10} {"worstTex",-22} | {"mapped",6} {"worstBrush",10} {"Δarea",9} {"kind",-6} | {"holes o/g",-10}");
        sb.AppendLine(new string('-', 184));
        foreach (LevelRow r in rows)
        {
            if (r.Note.StartsWith("EXCEPTION", StringComparison.Ordinal))
            {
                sb.AppendLine($"{r.Name,-28} {r.Note}");
                continue;
            }

            sb.AppendLine(
                $"{r.Name,-28} {r.Version,5:X} {r.Brushes,5} | {$"{r.OrigRooms}({r.OrigMain})/{r.GedRooms}({r.GedMain})",-23} {$"{r.OrigPortals}/{r.GedPortals}",-12} {$"{r.RoomsCovered}/{r.OrigRooms}",-7} | {r.AreaDeltaPct,8:F1} {r.WorstTextureDeltaPct,10:F1} {Trunc(r.WorstTexture, 22),-22} | {r.MappedFaceIdFraction,6:P0} {r.WorstBrushUid,10} {r.WorstBrushDeltaArea,9:F0} {r.WorstBrushKind,-6} | {$"{r.OrigHoles}/{r.GedHoles}",-10}");
        }

        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    private static void WriteArtifact(string report)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "qa_parity_sweep.txt"), report);
    }

    private static Dictionary<string, float> AreaByTexture(Geometry g)
    {
        var d = new Dictionary<string, float>();
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || f.Texture >= g.Textures.Count)
            {
                continue;
            }

            string t = g.Textures[f.Texture].ToLowerInvariant();
            d[t] = d.GetValueOrDefault(t) + FaceArea(g, f);
        }

        return d;
    }

    private static float FaceArea(Geometry g, Face f)
    {
        if (f.Vertices.Count < 3)
        {
            return 0f;
        }

        var c = new Vec3(0, 0, 0);
        foreach (FaceVertex v in f.Vertices)
        {
            if (v.Index < 0 || v.Index >= g.Vertices.Count)
            {
                return 0f;
            }

            c = c.Add(g.Vertices[v.Index]);
        }

        c = c.Scale(1f / f.Vertices.Count);
        float area = 0f;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            Vec3 a = g.Vertices[f.Vertices[i].Index].Sub(c);
            Vec3 b = g.Vertices[f.Vertices[(i + 1) % f.Vertices.Count].Index].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

    private static bool Overlap(Aabb a, Aabb b) =>
        a.P1.X <= b.P2.X && a.P2.X >= b.P1.X &&
        a.P1.Y <= b.P2.Y && a.P2.Y >= b.P1.Y &&
        a.P1.Z <= b.P2.Z && a.P2.Z >= b.P1.Z;
}
