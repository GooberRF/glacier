using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Two save-path parity gates uncovered while diagnosing the in-game "mover collision does not follow"
/// + "lightmaps not saving" reports:
/// <list type="bullet">
/// <item><b>Unique mover FaceIds</b> — RED gives every compiled face (static world AND mover) an id from
/// one global counter, so no mover face ever shares a FaceId with a static face (data-verified on
/// RED-authored dmabrupt: <c>moverFidsInStatic == 0</c>). GED compiles only the static world (movers are
/// excluded from the fold), so a GED-authored one-brush lift shipped mover faceIds 0..5 identical to the
/// room's 0..5. The build now renumbers mover faces above the static max, restoring RED's invariant.</item>
/// <item><b>Lightmap preservation</b> — a geometry-only / live-CSG preview build makes zero lightmap
/// pages; RED never empties the atlas on a geometry edit. Applying such a build must PRESERVE the existing
/// bake (stale but present), not wipe it. So a bake → move → save keeps the baked pages on disk.</item>
/// </list>
/// </summary>
public sealed class MoverCollisionAndLightmapTests
{
    private static EditorDocument NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "mc.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    /// <summary>An air room (so the static fold has real faces) plus a solid box turned into a mover.</summary>
    private static (EditorDocument Doc, BrushEditor Be, int Room, int MoverBrush) RoomWithMover()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        int room = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Air = true, Width = 20, Height = 12, Depth = 20 },
            new Vec3(0, 0, 0), Mat3.Identity);
        int box = be.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 3, Height = 3, Depth = 3 },
            new Vec3(2, -3, 0), Mat3.Identity);
        var mv = new MoverService(doc);
        mv.CreateMover(new[] { box }, Array.Empty<int>(), "Lift");
        return (doc, be, room, box);
    }

    private static MoversSection Movers(EditorDocument doc) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<MoversSection>().First();

    // ---- Unique mover FaceIds (collision-follow parity) ------------------------------------------

    [Fact]
    public void Build_Assigns_Movers_Globally_Unique_FaceIds_Disjoint_From_Static()
    {
        var (doc, _, _, box) = RoomWithMover();

        CompiledLevel result = GeometryBuildService.Build(doc.Rfl, new CompileOptions { BuildSurfaces = true });

        var staticFids = new HashSet<int>(result.Geometry.Faces.Select(f => f.FaceId));
        Assert.NotEmpty(staticFids); // the air room compiled to real faces (test is meaningful)

        Brush mover = Movers(doc).Movers.Single(m => m.Uid == box);
        var moverFids = mover.Geometry.Faces.Select(f => f.FaceId).ToList();
        Assert.NotEmpty(moverFids);

        // RED's invariant: not one mover face shares a FaceId with a static face.
        Assert.All(moverFids, id => Assert.DoesNotContain(id, staticFids));
        // And they were pushed above the static world's max (a disjoint range, deterministic).
        Assert.True(moverFids.Min() > staticFids.Max(),
            $"mover faceIds {moverFids.Min()}..{moverFids.Max()} not above static max {staticFids.Max()}");
        // Every FaceId across the whole level (static + mover) is unique.
        Assert.Equal(staticFids.Count + moverFids.Count, staticFids.Count + moverFids.Distinct().Count());
    }

    [Fact]
    public void AssignGlobalFaceIds_Is_Idempotent_And_Reports_Change_Once()
    {
        var (doc, _, _, _) = RoomWithMover();
        // First build renumbers (authored 0..n collide with the room's 0..n) → a change.
        CompiledLevel first = GeometryBuildService.Build(doc.Rfl, new CompileOptions { BuildSurfaces = true });
        var movers = Movers(doc).Movers;

        // Re-running the renumber against the same static geometry is a no-op (already disjoint/above max).
        Assert.False(MoverBrushes.AssignGlobalFaceIds(movers, first.Geometry),
            "AssignGlobalFaceIds should be idempotent once movers sit above the static max");
    }

    // ---- Lightmap preservation (bake survives a preview build + save) ----------------------------

    [Fact]
    public void Preview_Build_Preserves_The_Existing_Baked_Lightmaps()
    {
        var (doc, _, _, _) = RoomWithMover();

        // A full surface+bake build populates the atlas.
        GeometryBuildService.BuildAndApply(doc.Rfl, new CompileOptions { BuildSurfaces = true, BakeLighting = true });
        var baked = doc.Rfl.Sections.Select(s => s.Content).OfType<LightmapsSection>().First().Lightmaps;
        Assert.NotEmpty(baked);
        // Snapshot the exact pixels of the baked pages.
        var before = baked.Select(p => (byte[])p.Pixels.Clone()).ToList();

        // A live-CSG PREVIEW build (no surface stage) produces zero pages — it must NOT wipe the bake.
        GeometryBuildService.BuildAndApply(doc.Rfl, new CompileOptions { BuildSurfaces = false });

        var after = doc.Rfl.Sections.Select(s => s.Content).OfType<LightmapsSection>().First().Lightmaps;
        Assert.Equal(before.Count, after.Count); // pages preserved, not emptied
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i], after[i].Pixels); // exact baked pixels survive (stale but present, like RED)
        }
    }

    [Fact]
    public void Bake_Then_Preview_Then_Save_Keeps_The_Baked_Pages_On_Disk()
    {
        var (doc, _, _, _) = RoomWithMover();
        GeometryBuildService.BuildAndApply(doc.Rfl, new CompileOptions { BuildSurfaces = true, BakeLighting = true });
        int bakedPages = doc.Rfl.Sections.Select(s => s.Content).OfType<LightmapsSection>().First().Lightmaps.Count;
        Assert.True(bakedPages > 0);

        // Simulate the "move a brush" that arms the background live-CSG preview.
        GeometryBuildService.BuildAndApply(doc.Rfl, new CompileOptions { BuildSurfaces = false });

        byte[] saved = doc.SaveToBytes();
        RflFile reloaded = RflFile.Load(saved);
        reloaded.ParseAllKnownSections();
        int savedPages = reloaded.Sections.Select(s => s.Content).OfType<LightmapsSection>().First().Lightmaps.Count;

        Assert.Equal(bakedPages, savedPages); // the baked atlas is still on disk, not vanished
    }
}
