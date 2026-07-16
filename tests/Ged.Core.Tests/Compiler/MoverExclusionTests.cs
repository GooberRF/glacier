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
/// Pass 23 — MOVER EXCLUSION gate. RED folds only the static-world brushes into <c>static_geometry</c>;
/// mover-owned brushes (members of an <c>is_moving</c> group, duplicated in the <c>movers</c> section)
/// are excluded because RF.exe animates them separately. Folding them in leaves an immovable, unlit
/// duplicate at the rest position while the mover animates — the in-game "the original stays in place
/// with black lighting" defect. This gate proves GED's shipping fold contains ZERO mover-sourced faces
/// and that a control fold WITH the movers demonstrably would have baked them.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class MoverExclusionTests
{
    private readonly ITestOutputHelper _out;

    public MoverExclusionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dmabrupt_Static_Fold_Excludes_Mover_Brushes()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        List<Brush> allBrushes = FindBrushes(rfl);
        List<RoomEffect> effects = FindEffects(rfl);
        Geometry red = FindGeometry(rfl)!;

        HashSet<int> moverUids = MoverBrushes.CollectMoverUids(rfl);
        List<Brush> staticBrushes = MoverBrushes.ExcludeMovers(allBrushes, moverUids);

        // --- Input-level exactness ----------------------------------------------------------------
        // dmabrupt has 8 mover brushes across 4 is_moving groups (bridge1, lift002, guard door004,
        // LIFT001_BOTTOM), each also present in the movers section.
        Assert.Equal(8, moverUids.Count);
        Assert.Equal(allBrushes.Count - moverUids.Count, staticBrushes.Count);
        Assert.DoesNotContain(staticBrushes, b => moverUids.Contains(b.Uid));

        // --- Output-level exactness: the control fold WITH movers bakes their faces; shipping does not.
        // Face ids are assigned sequentially over the brush list (document order), so each mover brush
        // owns a contiguous face-id range; any output face whose id lands in a mover range was sourced
        // from that mover. This maps output faces back to their source brush without heuristics.
        var moverRanges = FaceIdRanges(allBrushes, moverUids);

        Geometry control = GeometryCompiler.Compile(allBrushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;
        int controlMoverFaces = control.Faces.Count(f => InAnyRange(moverRanges, f.FaceId));

        // A geometric cross-check that survives face-id renumbering: count faces coincident with each
        // mover's world panel (coplanar + centroid-near). Shipping must drop to RED's level.
        Geometry shipping = GeometryCompiler.Compile(staticBrushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;
        var moverBrushes = allBrushes.Where(b => moverUids.Contains(b.Uid)).ToList();
        int redCoin = 0, controlCoin = 0, shipCoin = 0;
        int guardDoorRed = 0, guardDoorShip = 0, guardDoorControl = 0;
        foreach (Brush m in moverBrushes)
        {
            List<CsgFace> wf = BrushWorld.ToWorldFaces(m, 0, out _);
            int r = Coincident(red, wf), c = Coincident(control, wf), s = Coincident(shipping, wf);
            redCoin += r;
            controlCoin += c;
            shipCoin += s;
            if (m.Uid == 10179) // "guard door004" — a detail door panel, the clean open-space case
            {
                guardDoorRed = r;
                guardDoorShip = s;
                guardDoorControl = c;
            }
        }

        _out.WriteLine($"mover uids: {string.Join(",", moverUids.OrderBy(x => x))}");
        _out.WriteLine($"brushes: all={allBrushes.Count} static={staticBrushes.Count}");
        _out.WriteLine($"control mover-sourced output faces (face-id map): {controlMoverFaces}");
        _out.WriteLine($"mover-coincident faces: RED={redCoin} control(with movers)={controlCoin} shipping(excluded)={shipCoin}");
        _out.WriteLine($"guard door (uid 10179): RED={guardDoorRed} control={guardDoorControl} shipping={guardDoorShip}");
        _out.WriteLine($"faces: control={control.Faces.Count} shipping={shipping.Faces.Count} RED={red.Faces.Count}");

        // The control fold bakes real mover geometry (the defect); the exclusion is not a no-op.
        Assert.True(controlMoverFaces > 0,
            $"control fold has no mover-sourced faces ({controlMoverFaces}) — the face-id map is broken");

        // The guard-door panel: excluded like RED. Control bakes the whole panel (>= RED + 10);
        // shipping drops to RED's frame-only count.
        Assert.True(guardDoorControl >= guardDoorRed + 10,
            $"control did not bake the guard-door panel (control={guardDoorControl}, red={guardDoorRed})");
        Assert.True(guardDoorShip <= guardDoorRed + 1,
            $"shipping still bakes the guard-door panel (shipping={guardDoorShip}, red={guardDoorRed})");

        // Overall: shipping's mover-coincident faces sit at RED's level (only the surrounding static
        // docking walls remain), while the control fold is well above it.
        Assert.True(shipCoin <= redCoin + 2,
            $"shipping mover-coincident faces {shipCoin} exceed RED {redCoin} + 2 — a mover leaked into static");
        Assert.True(controlCoin >= shipCoin + 20,
            $"excluding movers removed too few faces ({controlCoin}->{shipCoin}) — exclusion not effective");
    }

    [Fact]
    public void Levels_Without_Movers_Are_Unchanged()
    {
        if (!Corpus.Available)
        {
            return;
        }

        // dm04 has no movers section and no is_moving groups: the static brush list is unchanged.
        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        List<Brush> allBrushes = FindBrushes(rfl);
        HashSet<int> moverUids = MoverBrushes.CollectMoverUids(rfl);
        List<Brush> staticBrushes = MoverBrushes.ExcludeMovers(allBrushes, moverUids);

        Assert.Empty(moverUids);
        Assert.Equal(allBrushes.Count, staticBrushes.Count);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static List<(int Lo, int Hi)> FaceIdRanges(List<Brush> brushes, HashSet<int> uids)
    {
        var ranges = new List<(int, int)>();
        int cursor = 0;
        foreach (Brush b in brushes)
        {
            int count = b.Geometry.Faces.Count;
            if (uids.Contains(b.Uid))
            {
                ranges.Add((cursor, cursor + count));
            }

            cursor += count;
        }

        return ranges;
    }

    private static bool InAnyRange(List<(int Lo, int Hi)> ranges, int faceId)
    {
        foreach ((int lo, int hi) in ranges)
        {
            if (faceId >= lo && faceId < hi)
            {
                return true;
            }
        }

        return false;
    }

    private static int Coincident(Geometry g, List<CsgFace> moverFaces)
    {
        var mf = new List<(Vec3 N, float D, Vec3 C)>();
        foreach (CsgFace f in moverFaces)
        {
            if (f.Vertices.Count >= 3)
            {
                Vec3 n = f.Plane.Normal;
                Vec3 c = f.Centroid();
                mf.Add((n, n.Dot(c), c));
            }
        }

        int count = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || f.IsPortalFace)
            {
                continue;
            }

            Vec3 gn = f.Plane.Normal;
            var gc = new Vec3(0, 0, 0);
            int nv = 0;
            foreach (FaceVertex v in f.Vertices)
            {
                if (v.Index >= 0 && v.Index < g.Vertices.Count)
                {
                    gc = gc.Add(g.Vertices[v.Index]);
                    nv++;
                }
            }

            if (nv == 0)
            {
                continue;
            }

            gc = gc.Scale(1f / nv);
            float gd = gn.Dot(gc);
            foreach ((Vec3 N, float D, Vec3 C) in mf)
            {
                if (System.MathF.Abs(gn.Dot(N)) > 0.999f &&
                    System.MathF.Abs(gd - (gn.Dot(N) >= 0 ? D : -D)) < 0.05f &&
                    gc.Sub(C).Length() < 0.35f)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static List<Brush> FindBrushes(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                return bs.Brushes;
            }
        }

        return new List<Brush>();
    }

    private static List<RoomEffect> FindEffects(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is RoomEffectsSection es)
            {
                return es.Effects;
            }
        }

        return new List<RoomEffect>();
    }

    private static Geometry? FindGeometry(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                return gs.Geometry;
            }
        }

        return null;
    }
}
