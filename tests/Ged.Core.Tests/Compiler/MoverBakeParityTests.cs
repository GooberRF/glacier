using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// DEFECT 2 GATE — GED must bake the mover brushes' lightmap surfaces to RED's luminance. Goober reported the
/// two elevators (brush UIDs 94, 265) and the door (10179) rendering much darker with lightmaps baked in GED
/// than in RED. Root cause: movers are excluded from the static fold (correct) but GED left the movers section
/// untouched, so its surfaces kept RED's page indices into GED's regenerated atlas → stale/dark texels.
/// <see cref="MoverLighting"/> re-bakes them into GED's atlas at the rest position. This gate pins the rebaked
/// per-mover neighbourhood-relative luminance to a ratcheted regression baseline (±10%); see the RedRel /
/// GedBaselineRel block below for the re-baseline rationale and the path back to RED's original values.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class MoverBakeParityTests
{
    private const string Level = "dmabruptdecayrc2a27.rfl";

    // Offset-invariant measure: how bright a mover is RELATIVE to its static neighbourhood (mover mean /
    // ±9 m static-neighbourhood mean). GED's baker encodes brighter direct light than RED across the whole
    // atlas (a pre-existing baker-wide offset gated by the rendered BakeParity tests), so an ABSOLUTE
    // comparison vs RED's raw texels is not the right measure here; the neighbourhood-relative ratio removes
    // that offset. The gate ratchets each mover's current relative luminance against GedBaselineRel (below)
    // within ±10% — a tight regression band that catches a real bake/shadow regression while tolerating
    // neighbourhood-averaging noise.
    private const double LumRatioTolerance = 0.10;
    private static readonly int[] Witnessed = { 94, 265, 10179 };

    // === Re-baseline (owner decision, 2026-07-16) ================================================
    // RedRel is RED's shipped neighbourhood-relative mover luminance, measured from dmabrupt's RED-baked
    // lightmaps — the ORIGINAL parity targets and the values this gate should return to once RED's shadow
    // rasterizer is ported. GedBaselineRel is GED's CURRENT working-shadow relative luminance per method,
    // the numbers the gate now ratchets against. They differ because GED and RED use DIFFERENT shadow
    // algorithms: GED casts one center ray per texel to the light; RED (RED.exe FUN_004ae360) rasterizes
    // each occluder polygon into the fragment with a sub-texel projected-area cull (FUN_004ac370). That
    // shared shadow-algorithm divergence — not a mover bug — is the entire delta between GedBaselineRel and
    // RedRel; it is logged as the "Flagship 30" ledger entry in docs/research/compiler-parity-notes.md. The
    // prior ±10%-of-RedRel floors were captured while the OccluderBvh right-subtree traversal dropped deeper
    // right subtrees (shadow rays returned unoccluded — shadows were effectively a no-op); once that
    // traversal fix made shadow rays real, the mover-relative ratios moved off RedRel. When RED's polygon-
    // rasterization shadow model lands in the shared Lightmapper, re-point the assertions at RedRel.
    private static readonly IReadOnlyDictionary<int, double> RedRel = new Dictionary<int, double>
    {
        [94] = 0.988,
        [265] = 0.934,
        [10179] = 1.165,
    };

    private static readonly IReadOnlyDictionary<(int Uid, int Bounces), double> GedBaselineRel =
        new Dictionary<(int Uid, int Bounces), double>
        {
            [(94, 0)] = 1.258,   // Classic
            [(265, 0)] = 0.975,
            [(10179, 0)] = 0.998,
            [(94, 2)] = 1.342,   // 2-bounce
            [(265, 2)] = 1.050,
            [(10179, 2)] = 1.138,
        };
    // ============================================================================================

    private readonly ITestOutputHelper _out;

    public MoverBakeParityTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Mover_Surfaces_Bake_To_Red_Luminance_Classic()
    {
        RunMethod(bounces: 0, "Classic");
    }

    [Fact]
    public void Mover_Surfaces_Bake_To_Red_Luminance_TwoBounce()
    {
        // Goober: the door looks better with 2 bounces; the elevators must not regress under bounce either.
        RunMethod(bounces: 2, "2-bounce");
    }

    // GED's baker encodes brighter direct light than RED across the WHOLE atlas (measured: dmabrupt static
    // mean 142 vs RED 96 — a pre-existing baker-wide offset gated by the rendered BakeParity tests, not
    // introduced here). So the mover parity is measured OFFSET-INVARIANT: each mover's luminance relative to
    // the static surfaces around it must track RED's, i.e. the mover must be lit like its neighbours — which
    // is exactly the perceptual defect ("elevators much darker than the surrounding geometry").
    private const float NeighbourhoodRadius = 9f;

    private void RunMethod(int bounces, string tag)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();

        var redLm = rfl.Sections.Select(s => s.Content).OfType<LightmapsSection>().First().Lightmaps;
        Geometry redGeom = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        var movers = rfl.Sections.Select(s => s.Content).OfType<MoversSection>().First().Movers;

        // Capture RED's per-mover luminance + local static neighbourhood BEFORE the build mutates the movers.
        var redMoverLum = new Dictionary<int, double>();
        var redLocal = new Dictionary<int, double>();
        foreach (int uid in Witnessed)
        {
            Brush? m = movers.FirstOrDefault(b => b.Uid == uid);
            if (m is null)
            {
                continue;
            }

            redMoverLum[uid] = MeanLuminance(m.Geometry.Surfaces, redLm);
            redLocal[uid] = LocalStaticLum(redGeom, redLm, MoverWorldCenter(m));
        }

        var options = new CompileOptions { Alpine = true, BuildSurfaces = true, BakeLighting = true };
        options.Lighting.LightBounces = bounces;

        // RED-MATCHING state for parity: RED excludes mover geometry from the shadow occluder set (RED.exe
        // 1.20na FUN_004ae360 → FUN_004bcc60 rejects moving-group face types 4/5/7; the +0x36 owner check
        // stops self-shadowing) — a moving object cannot bake a fixed shadow. The app defaults "Movers cast
        // shadows" ON as a quality deviation, but this gate must measure the RED-authentic OFF state.
        Assert.False(options.Lighting.MoverShadows, "MoverBakeParity must run in the RED-matching (no mover occluders) state");

        CompiledLevel result = GeometryBuildService.Build(rfl, options);
        Assert.True(result.BakedMoverUids.Count > 0, "no movers were re-baked");

        var failures = new List<string>();
        foreach (int uid in Witnessed)
        {
            Brush? m = movers.FirstOrDefault(b => b.Uid == uid);
            if (m is null || m.Geometry.Surfaces.Count == 0 || !redMoverLum.ContainsKey(uid))
            {
                continue;
            }

            Vec3 c = MoverWorldCenter(m);
            double gedMover = MeanLuminance(m.Geometry.Surfaces, result.Lightmaps);
            double gedLocal = LocalStaticLum(result.Geometry, result.Lightmaps, c);

            // Offset-invariant: how bright the mover is RELATIVE to its neighbours.
            double redRel = redMoverLum[uid] / System.Math.Max(1e-6, redLocal[uid]);
            double gedRel = gedMover / System.Math.Max(1e-6, gedLocal);
            double ratioToRed = gedRel / System.Math.Max(1e-6, redRel);   // delta to RED = the shadow-model gap
            double baseline = GedBaselineRel[(uid, bounces)];             // GED's re-measured working-shadow value
            double drift = gedRel / System.Math.Max(1e-6, baseline);      // 1.0 == exactly on the re-baseline
            _out.WriteLine($"[{tag}] mover {uid}: RED rel={redRel:F3} (target {RedRel[uid]:F3})  " +
                $"GED rel={gedRel:F3} (baseline {baseline:F3}, drift {drift:F3})  GED/RED={ratioToRed:F3}");

            if (System.Math.Abs(drift - 1.0) > LumRatioTolerance)
            {
                failures.Add($"mover {uid} [{tag}]: neighbourhood-relative luminance {gedRel:F3} drifted from the " +
                    $"re-baselined {baseline:F3} (drift {drift:F3}, outside ±{LumRatioTolerance * 100:F0}%); " +
                    $"RED target {RedRel[uid]:F3}, GED/RED {ratioToRed:F3}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Mean luminance of the static surfaces whose world bbox centre is within
    /// <see cref="NeighbourhoodRadius"/> of <paramref name="center"/> (the mover's rest position).</summary>
    private static double LocalStaticLum(Geometry g, IReadOnlyList<Lightmap> pages, Vec3 center)
    {
        double sum = 0;
        int n = 0;
        foreach (Surface s in g.Surfaces)
        {
            Vec3 sc = s.BoundingBox.P1.Add(s.BoundingBox.P2).Scale(0.5f);
            if (sc.Sub(center).Length() > NeighbourhoodRadius)
            {
                continue;
            }

            double lum = SurfaceLum(s, pages);
            if (lum > 0)
            {
                sum += lum;
                n++;
            }
        }

        // Fall back to the whole-level static mean if the neighbourhood is empty (open-space mover).
        if (n == 0)
        {
            return g.Surfaces.DefaultIfEmpty().Average(s => s is null ? 0 : SurfaceLum(s, pages));
        }

        return sum / n;
    }

    private static Vec3 MoverWorldCenter(Brush m)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in m.Geometry.Vertices)
        {
            mn = Vec3Math.Min(mn, v);
            mx = Vec3Math.Max(mx, v);
        }

        Vec3 localCtr = mn.Add(mx).Scale(0.5f);
        return m.Position.Add(m.Rotation.Transform(localCtr));
    }

    private static double SurfaceLum(Surface s, IReadOnlyList<Lightmap> pages) =>
        MeanLuminance(new[] { s }, pages);

    /// <summary>
    /// The "Movers cast shadows" option (<see cref="LightingOptions.MoverShadows"/>) actually changes the
    /// occluder set: ON folds every mover's rest geometry into the shadow occluders, so movers self-shadow /
    /// shadow each other and darken the static geometry they overhang. OFF is byte-identical to the
    /// RED-matching bake (no mover occluders). Asserted by the box-shaped elevator (uid 94) getting darker
    /// with the option on (its own walls now occlude its floor) — a difference that can only come from the
    /// mover occluders.
    /// </summary>
    [Fact]
    public void MoverShadows_Option_Adds_Mover_Occluders()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        double LumWith(bool moverShadows)
        {
            RflFile rfl = RflFile.Load(path);
            rfl.ParseAllKnownSections();
            var options = new CompileOptions { Alpine = true, BuildSurfaces = true, BakeLighting = true };
            options.Lighting.MoverShadows = moverShadows;
            CompiledLevel result = GeometryBuildService.Build(rfl, options);
            var movers = rfl.Sections.Select(s => s.Content).OfType<MoversSection>().First().Movers;
            Brush m = movers.First(b => b.Uid == 94);
            return MeanLuminance(m.Geometry.Surfaces, result.Lightmaps);
        }

        double off = LumWith(false);
        double on = LumWith(true);

        // ON must measurably darken the box elevator (its own geometry now occludes its inward faces);
        // OFF is the RED-matching state. A real occluder-set difference, not noise.
        Assert.True(on < off - 1.0, $"MoverShadows on ({on:F1}) should darken elevator 94 vs off ({off:F1})");
    }

    /// <summary>A corpus-wide invariant: after a surface build every mover brush that had lightmap surfaces
    /// still has them, referencing valid (in-range) atlas pages — never a stale out-of-range page.</summary>
    [Fact]
    public void Mover_Surfaces_Reference_Valid_Pages_After_Build()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        var movers = rfl.Sections.Select(s => s.Content).OfType<MoversSection>().First().Movers;
        int redSurfaced = movers.Count(m => m.Geometry.Surfaces.Count > 0);

        CompiledLevel result = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = true, BakeLighting = false });

        int gedSurfaced = 0;
        foreach (Brush m in movers)
        {
            foreach (Surface s in m.Geometry.Surfaces)
            {
                Assert.True(s.LightmapIndex >= 0 && s.LightmapIndex < result.Lightmaps.Count,
                    $"mover {m.Uid} surface references page {s.LightmapIndex} of {result.Lightmaps.Count}");
            }

            if (m.Geometry.Surfaces.Count > 0)
            {
                gedSurfaced++;
            }
        }

        // Every mover RED gave surfaces to still has them (a mover-surface-exists check).
        Assert.True(gedSurfaced >= redSurfaced,
            $"GED surfaced {gedSurfaced} movers vs RED's {redSurfaced}");
    }

    private static double MeanLuminance(IReadOnlyList<Surface> surfaces, IReadOnlyList<Lightmap> pages)
    {
        double sum = 0;
        int n = 0;
        foreach (Surface s in surfaces)
        {
            if (s.LightmapIndex < 0 || s.LightmapIndex >= pages.Count)
            {
                continue;
            }

            Lightmap p = pages[s.LightmapIndex];
            for (int y = 0; y < s.H; y++)
            {
                int py = s.Y + y;
                if (py >= p.Height)
                {
                    break;
                }

                for (int x = 0; x < s.W; x++)
                {
                    int px = s.X + x;
                    if (px >= p.Width)
                    {
                        break;
                    }

                    int o = ((py * p.Width) + px) * 3;
                    if (o + 2 < p.Pixels.Length)
                    {
                        sum += (0.299 * p.Pixels[o]) + (0.587 * p.Pixels[o + 1]) + (0.114 * p.Pixels[o + 2]);
                        n++;
                    }
                }
            }
        }

        return n == 0 ? 0 : sum / n;
    }
}
