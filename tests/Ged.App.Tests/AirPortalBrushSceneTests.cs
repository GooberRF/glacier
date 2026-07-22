using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 4 (real-path): an AIR + Portal brush (ctf06 UID 414) whose 6 faces carry REAL textures rendered
/// in OBJECT mode but its faces appeared DELETED in BRUSH mode. Root cause: the authored BrushEmitter
/// folded the brush-level Portal flag into every face's portal predicate, dropping the real textures; in
/// Object mode the compiled static world (the air-carve's surviving cavity walls) covered for the drop,
/// so it only surfaced in Brush mode where the compiled world is suppressed. This drives the REAL
/// pipeline — a loaded+built level, the real merged stash, Brush mode via the actual chokepoint, and the
/// real <see cref="EditorSession.BuildScene"/> with the session's true flags — and asserts UID 414's real
/// faces emit as real-textured (not portal, not pick-only) geometry under ALL THREE View ▸ Portal Faces
/// settings, mode-independently. Emission-layer asserts (headless-safe; no GPU device required).
/// </summary>
public sealed class AirPortalBrushSceneTests
{
    private const int Uid414 = 414;

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

    private static bool Covers(GeometryBatch batch, int uid) =>
        batch.Vertices.Any(v => PickId.Decode(v.PickId) is { Kind: PickKind.Brush } p && p.Index == uid);

    private static int RealTexturedTris(RenderScene scene, int uid) =>
        scene.Batches
            .Where(b => !b.IsPortal && !b.PickOnly && b.TextureName.Length > 0 && Covers(b, uid))
            .Sum(b => b.Indices.Count / 3);

    [AvaloniaFact]
    public void Air_Portal_Brush_Faces_Render_In_Brush_Mode_Under_Every_Portal_Faces_Setting()
    {
        string? path = Ctf06();
        if (path is null)
        {
            return; // corpus unavailable
        }

        var session = new EditorSession();
        session.OpenLevel(path);
        BrushEditor be = session.BrushEditor!;
        BuildAndStash(session);

        Brush? b = be.Brushes.FirstOrDefault(x => x.Uid == Uid414);
        Assert.NotNull(b);
        Assert.NotEqual(0u, (uint)(BrushFlags.Air & (BrushFlags)b!.Flags));
        Assert.NotEqual(0u, (uint)(BrushFlags.Portal & (BrushFlags)b.Flags));

        // Enter BRUSH mode through the ACTUAL chokepoint (compiled world suppressed, solidFill on).
        be.SetMode(EditMode.Brush);
        session.SyncSelectionToKinds(SelectionFilter.PrimaryKindFor(EditMode.Brush));

        int? tris = null;
        foreach (PortalFaceDrawMode mode in new[] { PortalFaceDrawMode.None, PortalFaceDrawMode.SeeThru, PortalFaceDrawMode.Opaque })
        {
            session.PortalFaces = mode;
            RenderScene scene = session.BuildScene();

            int realTris = RealTexturedTris(scene, Uid414);
            Assert.True(realTris > 0,
                $"UID 414's real-textured faces must render in Brush mode under Portal Faces = {mode} (got {realTris} tris).");

            // The setting must not delete or tint these real faces: the emitted real geometry is identical
            // across all three modes (matching Object mode's compiled cavity walls).
            tris ??= realTris;
            Assert.Equal(tris.Value, realTris);
        }
    }
}
