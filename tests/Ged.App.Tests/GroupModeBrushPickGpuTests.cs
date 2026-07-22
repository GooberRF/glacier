using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Services;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Q1 (THIRD attempt) — brushes MUST be click-selectable in Group mode, end to end through the REAL
/// pipeline: a loaded+built level, Group mode entered via the actual mode-transition chokepoint
/// (<see cref="EditorSession.SyncSelectionToKinds"/>), the real <see cref="EditorSession.BuildScene"/>,
/// and the real GPU id-buffer pick (<see cref="SceneRenderer.RenderPick"/>).
/// <para>
/// The two prior fixes shipped green while the app still failed because their tests never rendered the
/// id buffer: they asserted the pick-only batch carried the brush id and that <c>SelectBrush</c> worked,
/// then called <c>SelectBrush</c> DIRECTLY — skipping the GPU depth resolution that actually breaks. In
/// Group mode the compiled static world is drawn (IncludeStaticGeometry is on outside brush-edit modes)
/// coincident with the brush's pick-only faces (they ARE the surviving compiled fragments). The world is
/// drawn first and carries <see cref="PickKind.Face"/>; the pick pass depth-tests with strict Less, so
/// the coincident brush id lost and the readback returned an unselectable Face — the brush never
/// selected. The fix draws pick-only faces FIRST so the whole-brush id wins coincident pixels. This test
/// renders the real id buffer and asserts a brush pick resolves to <see cref="PickKind.Brush"/> and
/// routes to a selection; pre-fix it returned <see cref="PickKind.Face"/>.
/// </para>
/// </summary>
public sealed class GroupModeBrushPickGpuTests
{
    private static string? Ctf06()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                string p = Path.Combine(dir.FullName, "research", "example_rfls", "ctf06.rfl");
                return File.Exists(p) ? p : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static GraphicsDevice? TryDevice()
    {
        try
        {
            return new GraphicsDevice(GraphicsBackend.Direct3D11);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compiles the level and installs the merged-brush stash, exactly as GeometryBuildController does.</summary>
    private static void BuildAndStash(EditorSession session)
    {
        var doc = session.Document!;
        var options = new CompileOptions { Alpine = doc.Rfl.Context.IsAlpine, SharedBsp = true, BuildSurfaces = true, FixTJoints = true };
        CompiledLevel result = GeometryBuildService.Build(doc.Rfl, options);
        GeometryBuildService.Apply(doc.Rfl, result);
        doc.MarkDirty();
        session.BrushFaceSurvival = result.SurvivingBrushFaces;
        session.BrushFragments = BrushFragmentIndex.Build(result.Geometry, result.BrushFaceIdStart, result.SurvivingBrushFaces);
        session.StaleFragmentBrushUids.Clear();
    }

    [AvaloniaFact]
    public void Group_Mode_Whole_Brush_Resolves_To_Brush_Through_The_Real_Gpu_Pick()
    {
        string? path = Ctf06();
        if (path is null)
        {
            return; // corpus unavailable
        }

        using GraphicsDevice? gd = TryDevice();
        if (gd is null)
        {
            return; // no GPU device in this environment
        }

        var session = new EditorSession();
        session.OpenLevel(path);
        BrushEditor be = session.BrushEditor!;
        BuildAndStash(session);

        // Enter GROUP mode through the ACTUAL chokepoint (mode → chip → ActiveSelectKinds), not by
        // hand-setting fields — the exact path ApplyMode drives.
        be.SetMode(EditMode.Group);
        session.SyncSelectionToKinds(SelectionFilter.PrimaryKindFor(EditMode.Group));
        Assert.Equal(SelectKinds.Groups, session.ActiveSelectKinds);

        RenderScene scene = session.BuildScene();

        // The compiled world (Face ids) AND the whole-brush pick-only faces are both in the scene.
        Assert.Contains(scene.Batches, b => b.PickOnly);
        Assert.Contains(scene.Batches, b => !b.PickOnly && b.Vertices.Count > 0
            && PickId.Decode(b.Vertices[0].PickId).Kind == PickKind.Face);

        const int W = 400, H = 300;
        using var renderer = new SceneRenderer(gd);
        using var pickTarget = gd.CreatePickTarget(W, H);
        using var gpu = new GpuScene(gd, scene, null);

        // Probe several plain solid brushes head-on; EVERY one must resolve to its brush, not to the
        // coincident compiled world Face. Pre-fix this returned PickKind.Face for all of them.
        var solids = be.Brushes
            .Where(b => ((BrushFlags)b.Flags & (BrushFlags.Air | BrushFlags.Portal | BrushFlags.Detail)) == 0)
            .Take(10)
            .ToList();
        Assert.NotEmpty(solids);

        var cam = new Ged.Rendering.Camera { AspectRatio = (float)W / H };
        int brushHits = 0, faceHits = 0;
        int? routedUid = null;
        foreach (Brush b in solids)
        {
            (Vector3 eye, Vector3 target) = FrameBrush(b);
            cam.LookAt(eye, target);
            PickId hit = renderer.RenderPick(cam, gpu, pickTarget, W / 2, H / 2);
            if (hit.Kind == PickKind.Brush)
            {
                brushHits++;
                routedUid ??= hit.Index;
            }
            else if (hit.Kind == PickKind.Face)
            {
                faceHits++;
            }
        }

        // The whole point: the id buffer resolves the BRUSH, never the coincident compiled Face.
        Assert.Equal(0, faceHits);
        Assert.Equal(solids.Count, brushHits);

        // ROUTE the real readback exactly as HandleModePick does in Group mode: the chip-gated PickGate
        // admits a whole-brush hit, and the selection router selects it.
        Assert.NotNull(routedUid);
        Assert.True(PickGate.AllowsBrushEditor(session.ActiveSelectKinds, PickKind.Brush));
        Assert.True(session.Selection.SelectBrush(routedUid!.Value, false));
        Assert.Contains(routedUid.Value, be.SelectedBrushes);
    }

    /// <summary>An eye/target framing a brush head-on from a corner of its world AABB.</summary>
    private static (Vector3 Eye, Vector3 Target) FrameBrush(Brush b)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        Mat3 r = b.Rotation;
        foreach (Vec3 lv in b.Geometry.Vertices)
        {
            Vector3 w = new Vector3(b.Position.X, b.Position.Y, b.Position.Z)
                + (new Vector3(r.Right.X, r.Right.Y, r.Right.Z) * lv.X)
                + (new Vector3(r.Up.X, r.Up.Y, r.Up.Z) * lv.Y)
                + (new Vector3(r.Forward.X, r.Forward.Y, r.Forward.Z) * lv.Z);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }

        Vector3 center = (min + max) * 0.5f;
        float extent = MathF.Max((max - min).Length(), 2f);
        Vector3 dir = Vector3.Normalize(new Vector3(0.6f, 0.5f, -0.6f));
        return (center + (dir * extent * 1.6f), center);
    }
}
