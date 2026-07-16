using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Regression for the "Check for Holes reports THOUSANDS of leaks on dmabrupt" hotfix.
/// <para>
/// Root cause: the editor's Check-for-Holes ran on PREVIEW-quality geometry. The live-CSG /
/// merged-stash preview build compiles with <see cref="CompileOptions.FixTJoints"/> = false — it
/// skips the t-joint SEAL — so its geometry carries thousands of open t-joint edges (dmabrupt: 13k)
/// that the sealed interactive build's <see cref="SeamSealer"/> closes down to the real residual
/// (dmabrupt: 6, the same number the <see cref="HoleParityGateTests"/> parity metric measures).
/// The controller now re-seals preview geometry before the leak check (GeometryIsPreview guard).
/// </para>
/// <para>
/// The exclusions are <see cref="HoleDetector"/>'s binary-proven set — IsDetail (0x0008) and
/// LiquidSurface (0x0004) — identical on the editor checker and the parity metric.
/// </para>
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class HoleSealAndLiquidReprTests
{
    private readonly ITestOutputHelper _out;

    public HoleSealAndLiquidReprTests(ITestOutputHelper output) => _out = output;

    // The editor Check-for-Holes must run on SEALED geometry so its count matches the parity
    // metric (~6). A PREVIEW build (FixTJoints = false) is the trap that produced the user's
    // "thousands of holes" — it stays > 1000, which is why the checker must never see it.
    [Fact]
    public void EditorChecker_On_Sealed_Geometry_Matches_The_Parity_Floor_Not_The_Preview_Explosion()
    {
        if (!Load("dmabruptdecayrc2a27.rfl", out _, out var brushes, out var effects))
        {
            return;
        }

        // The interactive editor build: surfaces + t-joint seal on (RunBuildAsync interactive == true).
        Geometry sealed_ = GeometryCompiler.Compile(brushes, effects,
            new CompileOptions { BuildSurfaces = true, FixTJoints = true }).Geometry;
        // The live-CSG / merged-stash PREVIEW build: surfaces + seal off (interactive == false).
        Geometry preview = GeometryCompiler.Compile(brushes, effects,
            new CompileOptions { BuildSurfaces = false, FixTJoints = false }).Geometry;

        int sealedHoles = HoleDetector.Detect(sealed_).Count;
        int previewHoles = HoleDetector.Detect(preview).Count;
        _out.WriteLine($"dmabrupt sealed={sealedHoles} preview={previewHoles}");

        // Sealed geometry sits at the parity floor (HoleParityGateTests ceiling for dmabrupt is 8).
        Assert.True(sealedHoles <= 8,
            $"sealed dmabrupt reports {sealedHoles} holes — should sit at the ~6 parity floor");

        // The preview (unsealed) geometry is the thousands-of-holes trap the checker must avoid;
        // pin it well above the sealed floor so a regression that lets the seal lapse is caught.
        Assert.True(previewHoles > 1000,
            $"preview dmabrupt reports {previewHoles} — expected the unsealed t-joint explosion (>1000)");
        Assert.True(previewHoles > sealedHoles * 50,
            "the seal must close the overwhelming majority of preview open edges");
    }

    // RED stores the mode-6 liquid surface as a self-contained DOUBLE-SIDED sub-manifold: literal
    // twin faces (an up-facing front + a mirrored down-facing back at the same plane), NOT a single
    // face with a double-sided flag. Binary ground truth from RED's original dmabrupt: 118 liquid
    // faces = 59 up + 59 down, every vertex-set a twin, zero single-sided. GED's compile must emit
    // the same representation so the surface renders from above AND from below (swimming) in-game.
    [Fact]
    public void LiquidSurface_Is_Emitted_Double_Sided_As_Twin_Faces_Matching_RED()
    {
        if (!Load("dmabruptdecayrc2a27.rfl", out Geometry red, out var brushes, out var effects))
        {
            return;
        }

        (int up, int down, int single, int sets) redLiquid = LiquidTwinStats(red);
        _out.WriteLine($"RED liquid up={redLiquid.up} down={redLiquid.down} single={redLiquid.single} sets={redLiquid.sets}");
        Assert.True(redLiquid.up > 0, "RED dmabrupt must carry liquid faces (the water surface)");
        Assert.Equal(redLiquid.up, redLiquid.down);   // every up face has a mirrored down twin
        Assert.Equal(0, redLiquid.single);            // RED is fully double-sided (no lone faces)
        Assert.Equal(redLiquid.up, redLiquid.sets);   // one twin PAIR per distinct vertex-set

        Geometry ged = GeometryCompiler.Compile(brushes, effects,
            new CompileOptions { BuildSurfaces = true, FixTJoints = true }).Geometry;
        (int up, int down, int single, int sets) gedLiquid = LiquidTwinStats(ged);
        _out.WriteLine($"GED liquid up={gedLiquid.up} down={gedLiquid.down} single={gedLiquid.single} sets={gedLiquid.sets}");

        // GED emits the surface double-sided (twin faces) exactly as RED does.
        Assert.True(gedLiquid.up > 0, "GED must emit a liquid surface for the water room");
        Assert.Equal(gedLiquid.up, gedLiquid.down);   // double-sided: equal up/down counts
        Assert.Equal(0, gedLiquid.single);            // no single-sided liquid fragments
        Assert.Equal(gedLiquid.up, gedLiquid.sets);   // each vertex-set is a front+back twin PAIR
    }

    // Groups LiquidSurface-flagged faces by welded vertex-set; returns up/down face counts, the
    // number of single-sided sets (a set with only one face), and the count of distinct sets.
    private static (int up, int down, int single, int sets) LiquidTwinStats(Geometry g)
    {
        var bySig = new Dictionary<string, (int up, int down)>();
        foreach (Face f in g.Faces)
        {
            if (((FaceFlags)f.Flags & FaceFlags.LiquidSurface) == 0)
            {
                continue;
            }

            string sig = string.Join(",", f.Vertices.Select(v => v.Index).OrderBy(x => x));
            (int up, int down) t = bySig.GetValueOrDefault(sig);
            if (f.Plane.Normal.Y >= 0f)
            {
                t.up++;
            }
            else
            {
                t.down++;
            }

            bySig[sig] = t;
        }

        int up = bySig.Values.Sum(v => v.up);
        int down = bySig.Values.Sum(v => v.down);
        int single = bySig.Values.Count(v => v.up + v.down == 1);
        return (up, down, single, bySig.Count);
    }

    private static bool Load(string fileName, out Geometry orig, out List<Brush> brushes, out List<RoomEffect> effects)
    {
        orig = null!;
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        if (!Corpus.Available)
        {
            return false;
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? o = null;
        BrushesSection? b = null;
        RoomEffectsSection? e = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                o ??= gs.Geometry;
            }
            else if (s.Content is BrushesSection bs)
            {
                b ??= bs;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                e ??= es;
            }
        }

        if (o is null || b is null)
        {
            return false;
        }

        orig = o;
        brushes = b.Brushes.ToList();
        effects = e?.Effects.ToList() ?? new List<RoomEffect>();
        return true;
    }
}
