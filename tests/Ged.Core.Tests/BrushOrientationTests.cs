using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Brushes are stored the way RED stores them (<see cref="BrushOrientation"/>; compiler-parity-notes.md
/// "Brush face orientation"): an air brush's faces — winding AND stored plane — face INWARD, a solid brush's
/// outward, a new portal brush inward. RED folds an air brush into the world with its faces as stored, so an
/// outward air brush rebuilds inside-out in RED. Glacier's compiler reads every air brush inward, so a repaired
/// brush builds exactly as it did before the repair.
/// </summary>
public sealed class BrushOrientationTests
{
    private static EditorDocument NewLevel() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    private static ScriptRunResult Run(EditorDocument doc, string lua)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, lua);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r;
    }

    private static int RunUid(EditorDocument doc, string lua) =>
        int.Parse(Run(doc, lua).ReturnValue!, System.Globalization.CultureInfo.InvariantCulture);

    private static Brush Find(EditorDocument doc, int uid) =>
        doc.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().SelectMany(s => s.Brushes).Single(b => b.Uid == uid);

    /// <summary>
    /// Every face's winding AND stored plane point the given way: the plane agrees with the winding's right-hand
    /// (Newell) normal, and the plane normal points away from (outward) or toward (inward) the vertex mean.
    /// </summary>
    private static void AssertStored(Brush b, bool inward)
    {
        Geometry g = b.Geometry;
        Assert.Equal(inward ? BrushWinding.Inward : BrushWinding.Outward, BrushOrientation.MeasureLocal(g));
        Vec3 mean = GeometryUtil.Centroid(g.Vertices);
        foreach (Face f in g.Faces)
        {
            List<Vec3> poly = GeometryUtil.Corners(g, f);
            Vec3 newell = GeometryUtil.Normal(poly);
            Assert.True(f.Plane.Normal.Dot(newell) > 0.99f, $"brush {b.Uid}: a face's stored plane disagrees with its winding.");
            float side = f.Plane.Normal.Dot(GeometryUtil.Centroid(poly).Sub(mean));
            Assert.True(inward ? side < 0f : side > 0f, $"brush {b.Uid}: a face's plane points {(inward ? "outward" : "inward")}.");
            Assert.True(MathF.Abs(f.Plane.Normal.Dot(poly[0]) + f.Plane.Offset) < 1e-3f, $"brush {b.Uid}: plane offset is not Normal·X + Offset = 0.");
        }
    }

    // ---- creation --------------------------------------------------------------------------------

    [Theory]
    [InlineData(BrushShape.Box)]
    [InlineData(BrushShape.Cylinder)]
    [InlineData(BrushShape.Cone)]
    [InlineData(BrushShape.Sphere)]
    [InlineData(BrushShape.Wedge)]
    public void Factory_Air_And_Portal_Brushes_Are_Stored_Inward_Solid_Outward(BrushShape shape)
    {
        var p = new BrushCreateParams { Shape = shape, Width = 4, Height = 3, Depth = 5 };
        Brush solid = BrushFactory.Create(p, 1);
        AssertStored(solid, inward: false);

        p.Air = true;
        Brush air = BrushFactory.Create(p, 2);
        AssertStored(air, inward: true);

        p.Air = false;
        p.Portal = true;
        Brush portal = BrushFactory.Create(p, 3);
        AssertStored(portal, inward: true);

        p.Air = true;
        Brush airPortal = BrushFactory.Create(p, 4);
        AssertStored(airPortal, inward: true);

        // Same shape, same textures on the same faces, same UVs at the same corners: only the order is reversed.
        Assert.Equal(solid.Geometry.Faces.Count, air.Geometry.Faces.Count);
        for (int i = 0; i < solid.Geometry.Faces.Count; i++)
        {
            Face s = solid.Geometry.Faces[i];
            Face a = air.Geometry.Faces[i];
            Assert.Equal(solid.Geometry.Textures[s.Texture], air.Geometry.Textures[a.Texture]);
            var sc = s.Vertices.Select(v => (v.Index, v.TextureCoords)).ToList();
            var ac = a.Vertices.Select(v => (v.Index, v.TextureCoords)).Reverse().ToList();
            Assert.Equal(sc, ac);
        }
    }

    [Fact]
    public void Gui_Created_Air_Brush_Is_Stored_Inward()
    {
        EditorDocument doc = NewLevel();
        var ed = new BrushEditor(doc);
        int air = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 8, Height = 4, Depth = 8, Air = true }, new Vec3(0, 2, 0), Mat3.Identity);
        int portal = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 3, Depth = 0.5f, Portal = true }, new Vec3(0, 2, 4), Mat3.Identity);
        int solid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 1, Height = 1, Depth = 1 }, new Vec3(0, 1, 0), Mat3.Identity);
        AssertStored(ed.FindBrush(air)!, inward: true);
        AssertStored(ed.FindBrush(portal)!, inward: true);
        AssertStored(ed.FindBrush(solid)!, inward: false);
    }

    [Fact]
    public void Lua_Created_Air_And_Portal_Brushes_Are_Stored_Inward()
    {
        EditorDocument doc = NewLevel();
        int air = RunUid(doc, "return level.brush{pos = {0, 2, 0}, size = {8, 4, 8}, air = true}.uid");
        int portal = RunUid(doc, "return level.brush{pos = {0, 2, 4}, size = {2, 3, 0.5}, portal = true}.uid");
        int solid = RunUid(doc, "return level.brush{pos = {0, 1, 0}, size = 1}.uid");
        int hull = RunUid(doc, "return level.brush_from_points{points = {{0,0,0}, {4,0,0}, {0,0,4}, {4,0,4}, {2,3,2}}, air = true}.uid");
        AssertStored(Find(doc, air), inward: true);
        AssertStored(Find(doc, portal), inward: true);
        AssertStored(Find(doc, solid), inward: false);
        AssertStored(Find(doc, hull), inward: true);
    }

    [Fact]
    public void Lua_Air_Brush_Keeps_Outward_Semantics_For_Normals_Roles_And_Contains()
    {
        EditorDocument doc = NewLevel();
        string r = Run(doc,
            "local b = level.brush{pos = {0, 2, 0}, size = {8, 4, 8}, air = true, textures = {floor = 'floor01.tga', ceiling = 'ceil01.tga', wall = 'wall01.tga'}}\n" +
            "local up = b:faces_facing('up') local down = b:faces_facing('down')\n" +
            "return #up .. ',' .. #down .. ',' .. up[1].texture .. ',' .. down[1].texture .. ',' .. string.format('%.2f', up[1].normal.y) .. ',' ..\n" +
            "  tostring(b:contains(0, 2, 0)) .. ',' .. tostring(b:contains(0, 2, 3.99)) .. ',' .. tostring(b:contains(0, 2, 5)) .. ',' ..\n" +
            "  tostring(geo.check(b).ok) .. ',' .. tostring(geo.check(b).convex)").ReturnValue!;
        // up = the top face (outward normal +Y) carries the ceiling for an air room; down = the floor the player stands on.
        Assert.Equal("1,1,ceil01.tga,floor01.tga,1.00,true,true,false,true,true", r);
    }

    // ---- toggles ----------------------------------------------------------------------------------

    [Fact]
    public void Air_Solid_Toggle_Reverses_The_Faces_And_Undo_Restores_Them()
    {
        EditorDocument doc = NewLevel();
        int uid = RunUid(doc, "return level.brush{pos = {0, 2, 0}, size = {8, 4, 8}, air = true}.uid");
        Brush b = Find(doc, uid);
        AssertStored(b, inward: true);

        Run(doc, $"level.find_brush({uid}):set_air(false)");
        AssertStored(Find(doc, uid), inward: false);

        Run(doc, $"level.find_brush({uid}):set_air(true)");
        AssertStored(Find(doc, uid), inward: true);

        Run(doc, $"level.find_brush({uid}):set_air(false)");
        doc.Undo.Undo();
        AssertStored(Find(doc, uid), inward: true);
    }

    [Fact]
    public void Brush_Query_Set_Flags_Toggles_Air_Like_Red()
    {
        EditorDocument doc = NewLevel();
        int uid = RunUid(doc, "return level.brush{pos = {0, 1, 0}, size = 2}.uid");
        Run(doc, $"level.brushes_where(function(b) return b.uid == {uid} end):set_flags{{air = true}}");
        AssertStored(Find(doc, uid), inward: true);
    }

    [Fact]
    public void Portal_Toggle_Leaves_The_Faces_Alone_As_Red_Does()
    {
        EditorDocument doc = NewLevel();
        var ed = new BrushEditor(doc);
        int solid = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, default, Mat3.Identity);
        ed.EditBrushes(new[] { solid }, "Portal", b => { b.Flags |= (uint)BrushFlags.Portal; return OpResult.Ok(); });
        AssertStored(ed.FindBrush(solid)!, inward: false);

        int air = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Air = true }, default, Mat3.Identity);
        ed.EditBrushes(new[] { air }, "Portal", b => { b.Flags |= (uint)BrushFlags.Portal; return OpResult.Ok(); });
        AssertStored(ed.FindBrush(air)!, inward: true);
    }

    [Fact]
    public void Air_Toggle_Never_Turns_An_Already_Correct_Brush_Inside_Out()
    {
        // An authored inward solid (RED levels have some) made air is already stored the way air is stored.
        Brush b = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, 1);
        BrushOrientation.Reverse(b.Geometry);
        uint old = b.Flags;
        b.Flags |= (uint)BrushFlags.Air;
        Assert.False(BrushOrientation.ApplyAirToggle(b, old));
        AssertStored(b, inward: true);
    }

    // ---- editing keeps the stored orientation -----------------------------------------------------

    [Fact]
    public void Clip_Mirror_Extrude_And_Carve_Keep_An_Air_Brush_Inward()
    {
        Brush air = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = 4, Height = 4, Depth = 4, Air = true }, 1);
        ClipResult clip = BrushOps.Clip(air, new Vec3(0.5f, 0, 0), new Vec3(1, 0, 0), ClipMode.Split, flipNormal: false);
        Assert.True(clip.Success);
        foreach (Geometry piece in clip.Pieces)
        {
            AssertStored(new Brush { Uid = 9, Geometry = piece, Rotation = Mat3.Identity, Flags = air.Flags }, inward: true);
        }

        BrushOps.Mirror(air, 0);
        AssertStored(air, inward: true);

        int top = air.Geometry.Faces.FindIndex(f => f.Plane.Normal.Y < -0.9f); // the inward-facing top face
        Assert.True(FaceOps.Extrude(air.Geometry, top, 2f).Success);
        AssertStored(air, inward: true);
        Assert.Equal(4f, air.Geometry.Vertices.Max(v => v.Y), 3); // grew upward (2 + 2), out of the brush

        Brush cutter = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = 1, Height = 10, Depth = 1 }, 2);
        cutter.Position = new Vec3(2, 0, 0);
        Geometry? carved = CarveOps.Carve(air, cutter);
        Assert.NotNull(carved);
        Assert.Equal(BrushWinding.Inward, BrushOrientation.MeasureLocal(carved!));
    }

    [Fact]
    public void Hull_Rebuild_Keeps_An_Air_Brush_Inward_And_Matches_Textures_By_Outward_Normal()
    {
        EditorDocument doc = NewLevel();
        int uid = RunUid(doc,
            "local b = level.brush{pos = {0, 2, 0}, size = {8, 4, 8}, air = true, textures = {floor = 'floor01.tga', ceiling = 'ceil01.tga', wall = 'wall01.tga'}}\n" +
            "geo.snap{brushes = b} return b.uid");
        Brush b = Find(doc, uid);
        AssertStored(b, inward: true);
        float sign = BrushOrientation.OutwardSign(b);
        Face down = b.Geometry.Faces.Single(f => f.Plane.Normal.Y * sign < -0.9f);
        Assert.Equal("floor01.tga", b.Geometry.Textures[down.Texture]);
    }

    [Fact]
    public void Geo_Check_Reports_An_Outward_Air_Brush()
    {
        Brush air = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Air = true }, 1);
        GeometryReport ok = GeometryCheck.Check(air);
        Assert.True(ok.Closed && ok.Outward && ok.Convex && ok.Planar, string.Join(" ", ok.Issues));

        BrushOrientation.Reverse(air.Geometry);
        GeometryReport bad = GeometryCheck.Check(air);
        Assert.True(bad.Closed && bad.Convex);
        Assert.False(bad.Outward);
        Assert.Contains(bad.Issues, i => i.Contains("inside-out", StringComparison.Ordinal));
    }

    // ---- save safety net, linter, RED corpus -----------------------------------------------------------

    private static EditorDocument LevelWithOutwardAirBrush(out int airUid, out int solidPortalUid)
    {
        EditorDocument doc = NewLevel();
        airUid = RunUid(doc, "return level.brush{pos = {0, 2, 0}, size = {8, 4, 8}, air = true}.uid");
        solidPortalUid = RunUid(doc, "local b = level.brush{pos = {0, 2, 4}, size = {2, 3, 0.5}} b:set_portal(true) return b.uid");
        RunUid(doc, "return level.brush{pos = {0, 2, 0}, size = {16, 8, 16}}.uid");

        // What an older Glacier wrote: the air brush wound outward (winding and plane).
        BrushOrientation.Reverse(Find(doc, airUid).Geometry);
        foreach (RflSection s in doc.Rfl.Sections.Where(s => s.Content is BrushesSection))
        {
            s.Dirty = true;
        }

        AssertStored(Find(doc, airUid), inward: false);
        AssertStored(Find(doc, solidPortalUid), inward: false); // a solid portal made from a solid keeps its faces
        return doc;
    }

    [Fact]
    public void Linter_Warns_About_An_Outward_Air_Brush_Only()
    {
        EditorDocument doc = LevelWithOutwardAirBrush(out int air, out int portal);
        List<LintFinding> findings = LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings
            .Where(f => f.Category == LintCategory.AirBrushInsideOut).ToList();
        LintFinding f = Assert.Single(findings);
        Assert.Equal(air, f.Uid);
        Assert.Equal(LintSeverity.Warning, f.Severity);
        Assert.Contains("RED", f.Message, StringComparison.Ordinal);
        Assert.Contains("inside-out", f.Message, StringComparison.Ordinal);
        Assert.NotEqual(portal, f.Uid);
    }

    [Fact]
    public void Save_Repairs_An_Outward_Air_Brush_And_Says_So()
    {
        EditorDocument doc = LevelWithOutwardAirBrush(out int air, out int portal);
        string path = Path.Combine(Path.GetTempPath(), $"ged-orient-{Guid.NewGuid():N}.rfl");
        try
        {
            doc.Save(path, updateTimestamp: false);
            Assert.Contains(doc.LastSaveNotes, n => n.Contains("Re-oriented 1 air brush", StringComparison.Ordinal) && n.Contains($"brush {air}", StringComparison.Ordinal));

            EditorDocument reopened = EditorDocument.Open(path);
            AssertStored(Find(reopened, air), inward: true);
            AssertStored(Find(reopened, portal), inward: false); // solid portals are never re-oriented on save
            Assert.DoesNotContain(LevelLinter.Lint(reopened.Rfl, new LintOptions()).Findings, f => f.Category == LintCategory.AirBrushInsideOut);

            // Saving again repairs nothing and changes nothing.
            byte[] first = File.ReadAllBytes(path);
            reopened.Save(path, updateTimestamp: false);
            Assert.DoesNotContain(reopened.LastSaveNotes, n => n.Contains("Re-oriented", StringComparison.Ordinal));
            Assert.Equal(first, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Repaired_Air_Brush_Compiles_Exactly_Like_The_Outward_One()
    {
        EditorDocument doc = LevelWithOutwardAirBrush(out _, out _);
        CompiledLevel before = KitTestHarness.Sealed(doc, sharedBsp: true);
        Assert.NotEmpty(BrushOrientation.RepairLevel(doc.Rfl));
        CompiledLevel after = KitTestHarness.Sealed(doc, sharedBsp: true);
        Assert.Equal(before.Geometry.Faces.Count, after.Geometry.Faces.Count);
        Assert.Equal(before.Geometry.Vertices.Count, after.Geometry.Vertices.Count);
        Assert.Equal(before.Geometry.Rooms.Count, after.Geometry.Rooms.Count);
    }

    /// <summary>Every RED-made corpus level already stores its air brushes inward: the save repair never fires on one.</summary>
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Red_Corpus_Levels_Have_No_Outward_Air_Brushes(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        RflFile file = RflFile.Load(Path.Combine(Corpus.Directory!, fileName));
        file.ParseAllKnownSections();
        if (file.Header.Version >= 300)
        {
            return; // written by Glacier / Alpine-era tools, not RED-era authoring
        }

        foreach (Brush b in file.Sections.Select(s => s.Content).OfType<BrushesSection>().SelectMany(s => s.Brushes))
        {
            if (BrushOrientation.RequiresInward(b.Flags))
            {
                Assert.NotEqual(BrushWinding.Outward, BrushOrientation.Measure(b));
            }
        }

        Assert.Empty(BrushOrientation.RepairLevel(file));
    }
}
