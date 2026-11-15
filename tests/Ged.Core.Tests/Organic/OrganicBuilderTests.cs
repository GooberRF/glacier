using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// The geo builders (ORGANIC-GEOMETRY-SPEC §3 and its §10 deviations): geo.tube (cave / canyon), geo.passage, geo.rock_row,
/// geo.scatter, geo.pit and brush_query:set_face_flag — shape rules (grid, unrotated, convex, exactly planar
/// abutting caps, exact floors), the records, tags, determinism, the error table and sealed watertight builds.
/// </summary>
public sealed class OrganicBuilderTests
{
    private static List<Brush> Brushes(KitRun run) => new BrushEditor(run.Doc).Brushes.ToList();

    private static List<Brush> Brushes(EditorDocument doc) => new BrushEditor(doc).Brushes.ToList();

    /// <summary>Neighbouring segments share their whole cap: every corner of segment i on the plane of the shared ring is a corner of segment i+1.</summary>
    private static void AssertAbutting(IReadOnlyList<Brush> chain)
    {
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            var a = HullDeform.WorldVertices(chain[i]).ToHashSet();
            var b = HullDeform.WorldVertices(chain[i + 1]).ToHashSet();
            var shared = a.Intersect(b).ToList();
            Assert.True(shared.Count >= 3, $"segments {i + 1}/{i + 2} share {shared.Count} corners");
            AuditShape cap = AuditShape.FromBrush(chain[i], "a");
            Assert.Contains(cap.Faces, f => f.Points.All(shared.Contains) && f.Points.Length == shared.Count);
        }
    }

    private static (EditorDocument Doc, ScriptRunResult Result) Exec(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local kit = ged.import('kit')\n" + script);
        return (doc, r);
    }

    // ---- tube ------------------------------------------------------------------------------------

    [Fact]
    public void A_Cave_Is_A_Chain_Of_Exact_Abutting_Hulls_And_Watertight()
    {
        KitRun run = Run(
            "local t = geo.tube{path = {{0,0,0}, {0,0,24}, {12,0,40}, {12,2,60}}, profile = 'cave', radius = 3, seed = 3, part = 'seg', tags = {zone = 'a'}}\n" +
            "assert(t.brushes.count == #t.stations - 1)\n" +
            "assert(t.retries >= 0 and t.length > 60 and t.top_y == nil and t.clear_width == 1.8)\n" +
            "assert(#t.climbs == 1 and t.climbs[1].rise == 2 and t.start.z == 0 and math.abs(t['end'].z - 60) < 0.05)\n" +
            "return t.brushes.count");
        List<Brush> brushes = Brushes(run);
        Assert.Equal(int.Parse(run.Result.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture), brushes.Count);
        brushes.ForEach(ScriptGeoEditTests.AssertHull);
        Assert.All(brushes, b => Assert.True((b.Flags & (uint)BrushFlags.Air) != 0 && (b.Flags & (uint)BrushFlags.Detail) == 0));
        Assert.All(brushes, b => Assert.True(CompilerFit.Fits(b)));
        AssertAbutting(brushes);

        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        Assert.Equal("seg001", tags[brushes[0].Uid]["kit.part"]);
        Assert.Equal("3", tags[brushes[0].Uid]["geo.seed"]);
        Assert.Equal("a", tags[brushes[^1].Uid]["zone"]);
        AssertAccepted(run);
    }

    [Fact]
    public void A_Level_Leg_Has_One_Horizontal_Floor_And_A_Sloped_Leg_One_Exact_Plane()
    {
        KitRun run = Run("geo.tube{path = {{0,0,0}, {0,0,20}, {0,3,40}, {0,3,52}}, radius = 3, seed = 2}");
        List<Brush> brushes = Brushes(run);
        var floors = brushes.SelectMany(b => AuditShape.FromBrush(b, "s").Faces.Where(f => f.Normal.Y < -0.95)).ToList();
        Assert.All(floors.Where(f => f.Points.All(p => p.Z <= 20)), f => Assert.All(f.Points, p => Assert.Equal(0, p.Y)));

        // Every floor corner of the climbing leg lies on one plane (no folds between its segments).
        var sloped = floors.Where(f => f.Points.Any(p => p.Y > 0 && p.Y < 3)).SelectMany(f => f.Points).Distinct().ToList();
        Assert.True(sloped.Count >= 6);
        AuditFace first = floors.First(f => f.Points.Any(p => p.Y > 0 && p.Y < 3));
        Assert.All(sloped, p => Assert.True(Math.Abs(first.Normal.Dot(p.Sub(first.Centroid))) < 1e-9, $"{p} is off the climb's plane"));
        AssertAccepted(run);
    }

    [Fact]
    public void A_Canyon_Has_One_Common_Top_Plane_And_Kit_Canyon_Opens_It_To_The_Sky()
    {
        KitRun run = Run(
            "local c = kit.canyon{name = 'cn', path = {{0,0,0}, {0,0,30}, {20,-1,60}, {20,-1,90}}, width = 12, height = 12, seed = 5}\n" +
            "assert(c.kind == 'canyon' and c.top_y == 12 and c.sky ~= nil and c.group ~= nil and #c.brushes == c.tube.brushes.count)\n" +
            "local up = 0\n" +
            "for _, b in ipairs(c.brushes) do for _, f in ipairs(b:faces_facing('up')) do if f.show_sky then up = up + 1 end end end\n" +
            "assert(up >= #c.brushes)\n" +
            "local again = kit.canyon{name = 'cn2', path = {{60,0,0}, {60,0,30}}, sky = true}\n" +
            "assert(again.sky == nil)");
        List<Brush> segments = Brushes(run).Where(b => Tags(run.Doc)[b.Uid].GetValueOrDefault("kit.name") == "cn").ToList();
        Assert.All(segments, b => Assert.Equal(12, HullDeform.WorldVertices(b).Max(p => p.Y)));
        AssertAccepted(run);
    }

    [Fact]
    public void A_Closed_Loop_Ends_On_Its_Own_Start_Ring()
    {
        KitRun run = Run("local t = geo.tube{path = {{0,0,0}, {20,0,0}, {30,0,15}, {20,0,30}, {0,0,30}, {-10,0,15}, {0,0,0}}, seed = 4}\nreturn t.brushes.count");
        List<Brush> brushes = Brushes(run);
        AssertAbutting(brushes.Append(brushes[0]).ToList());
        AssertAccepted(run);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_Closed_Loop_Whose_Last_Leg_Climbs_Builds_With_One_Exact_Climb_Plane(int seed)
    {
        KitRun run = Run($"geo.tube{{path = {{{{0,0,0}}, {{30,0,0}}, {{30,0,30}}, {{0,2,30}}, {{0,0,0}}}}, seed = {seed}}}");
        List<Brush> brushes = Brushes(run);
        AssertAbutting(brushes.Append(brushes[0]).ToList());

        // The closing leg runs down from (0, 2, 30) to the start: all its sloped floor corners lie on one plane
        // (the third leg also climbs, along z = 30; its floors are left out).
        var floors = brushes.SelectMany(b => AuditShape.FromBrush(b, "s").Faces.Where(f => f.Normal.Y < -0.95)).ToList();
        var sloped = floors.Where(f => f.Points.Any(p => p.Y > 0 && p.Y < 2) && f.Points.All(p => p.Z < 25)).ToList();
        Assert.NotEmpty(sloped);
        AuditFace first = sloped[0];
        Assert.All(sloped.SelectMany(f => f.Points).Where(p => p.Y > 0 && p.Y < 2),
            p => Assert.True(Math.Abs(first.Normal.Dot(p.Sub(first.Centroid))) < 1e-9, $"{p} is off the climb's plane"));
        AssertAccepted(run);
    }

    [Fact]
    public void Leg_Alignment_Moves_A_Point_At_Most_Half_A_Grid_Step_Per_Earlier_Segment_And_Keeps_Straight_Runs_In_Line()
    {
        // A long zig-zag of short legs, then a straight run along +z of three legs that climb at different slopes.
        var path = new List<Vec3d>();
        for (int i = 0; i < 24; i++)
        {
            path.Add(new Vec3d((i * 7.3141) + (i % 2 == 0 ? 0 : 1.237), 0, (i % 2 == 0 ? 0 : 5.0123) + (i * 0.731)));
        }

        Vec3d end = path[^1];
        var dir = new Vec3d(0, 0, 1);
        foreach ((double run, double rise) in new[] { (9.7, 0.0), (8.3, 2.0), (11.1, 2.0) })
        {
            end = new Vec3d(end.X + (dir.X * run), end.Y + rise, end.Z + (dir.Z * run));
            path.Add(end);
        }

        TubeResult? tube = HullChain.Build(new TubeOptions { Path = path, Seed = 1, Spacing = 2 }, out GeoEditError? error);
        Assert.True(tube is not null, error?.Message);
        foreach (Vec3d p in path)
        {
            int index = tube!.Stations.Select((st, i) => (st, i)).MinBy(t => Math.Abs(t.st.Pos.X - p.X) + Math.Abs(t.st.Pos.Z - p.Z)).i;
            Vec3d at = tube.Stations[index].Pos;
            double bound = (index * HullBrush.Grid / 2) + 1e-9;
            Assert.True(Math.Abs(at.X - p.X) <= bound && Math.Abs(at.Z - p.Z) <= bound, $"{p} moved to {at} (bound {bound})");
        }

        // The straight run's stations all keep one x (its legs keep their authored deltas).
        double[] runX = tube!.Stations.Where(st => st.Pos.Z > path[23].Z + 0.1).Select(st => st.Pos.X).Distinct().ToArray();
        Assert.Single(runX);
    }

    [Fact]
    public void Tubes_Are_Deterministic_And_Do_Not_Touch_Rng()
    {
        const string Script = "rng.set_seed(5)\nlocal before = rng.int(1, 1000000)\nrng.set_seed(5)\n" +
                              "geo.tube{path = {{0,0,0}, {5,1,20}, {20,1,30}}, seed = 9}\nreturn tostring(rng.int(1, 1000000) == before)";
        (EditorDocument a, ScriptRunResult ra) = Exec(Script);
        (EditorDocument b, _) = Exec(Script);
        Assert.True(ra.Success, ra.Error?.ToDisplayString());
        Assert.Equal("true", ra.ReturnValue);
        Assert.Equal(a.SaveToBytes(updateTimestamp: false), b.SaveToBytes(updateTimestamp: false));
    }

    // ---- passage ---------------------------------------------------------------------------------

    [Fact]
    public void A_Passage_At_Thirty_Degrees_Abuts_Exactly_And_Is_Watertight()
    {
        KitRun run = Run(
            "local a = kit.room{name = 'a', pos = {0,0,0}, size = {12,5,12}}\n" +
            "local p = kit.passage{name = 'p', path = {{3,0,1}, {30,0,16.5}, {30,0,40}}, width = 3, height = 3}\n" +
            "assert(p.kind == 'passage' and #p.brushes == 2 and #p.climbs == 0 and p.tube.retries == 0)");
        List<Brush> prisms = Brushes(run).Skip(1).ToList();
        prisms.ForEach(ScriptGeoEditTests.AssertHull);
        Assert.All(prisms, b => Assert.Equal(6, b.Geometry.Faces.Count));
        AssertAbutting(prisms);
        AssertAccepted(run);
    }

    // ---- rocks and pit ---------------------------------------------------------------------------

    [Fact]
    public void Rock_Rows_Are_Detail_By_Default_And_A_Structural_Ridge_Stands_On_The_Floor()
    {
        KitRun run = Run(
            "kit.room{name = 'r', pos = {0,0,0}, size = {30,8,30}}\n" +
            "local d = geo.rock_row{from = {-8,0,-8}, to = {8,0,-8}, height = 3, depth = 2, part = 'rock'}\n" +
            "local s = geo.rock_row{from = {-8,0,8}, to = {8,0,8}, height = 3, depth = 2, structural = true}\n" +
            "local f = geo.rock_row{from = {-8,0,0}, to = {0,0,0}, height = 2, depth = 3, flat_top = true}\n" +
            "assert(d.count >= 3 and s.count >= 3 and f.count >= 2)");
        List<Brush> rocks = Brushes(run).Skip(1).ToList();
        rocks.ForEach(ScriptGeoEditTests.AssertHull);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        List<Brush> ridge = rocks.Where(b => (b.Flags & (uint)BrushFlags.Detail) == 0).ToList();
        Assert.NotEmpty(ridge);
        Assert.All(ridge, b => Assert.Equal(0, HullDeform.WorldVertices(b).Min(p => p.Y)));
        AssertAbutting(ridge);
        Assert.Equal("rock001", tags[rocks[0].Uid]["kit.part"]);
        Assert.Contains(rocks, b => HullDeform.WorldVertices(b).Count(p => p.Y == 2) >= 3); // a flat top at exactly 2 m
        AssertAccepted(run);
    }

    [Fact]
    public void Scatter_Keeps_Its_Spacing_And_Keeps_A_Tube_Walkway_Clear()
    {
        KitRun run = Run(
            "local t = geo.tube{path = {{0,0,0}, {0,0,40}}, profile = 'canyon', width = 12, height = 8, seed = 2}\n" +
            "local r = geo.scatter{along = t, count = 20, size = {0.6, 1.4}, seed = 3}\n" +
            "local a = geo.scatter{area = {min = {-4,0,10}, max = {4,0,30}}, count = 5, size = 1, seed = 1}\n" +
            "assert(r.count == 20 and a.count == 5)");
        List<Brush> rocks = Brushes(run).Where(b => (b.Flags & (uint)BrushFlags.Detail) != 0).ToList();
        Assert.Equal(25, rocks.Count);
        Assert.All(rocks.Take(20), b => Assert.True(HullDeform.WorldVertices(b).All(p => Math.Abs(p.X) > 0.9), $"rock {b.Uid} reaches into the walkway"));
        AssertAccepted(run);
    }

    [Fact]
    public void A_Pit_Opens_Through_The_Floor_And_Has_A_Flat_Floor()
    {
        KitRun run = Run(
            "kit.room{name = 'r', pos = {0,0,0}, size = {16,6,16}}\n" +
            "local p = kit.pit{name = 'pit', pos = {1,0,1}, radius = 3, depth = 5, seed = 2}\n" +
            "assert(p.kind == 'pit' and p.floor_y == -5 and p.depth == 5 and p.radius == 3 and #p.brushes >= 2)");
        List<Brush> rings = Brushes(run).Skip(1).ToList();
        rings.ForEach(ScriptGeoEditTests.AssertHull);
        Assert.Equal(0.0625, HullDeform.WorldVertices(rings[0]).Max(p => p.Y)); // a lap above the floor: air over air
        Assert.Equal(-5, HullDeform.WorldVertices(rings[^1]).Min(p => p.Y));
        AssertAbutting(rings);
        AssertAccepted(run);
    }

    [Theory]
    [InlineData(0.15)]
    [InlineData(0.02)]
    [InlineData(0.124)]
    public void A_Pit_In_A_Floor_Off_The_Sixteenth_Grid_Opens_Through_It(double floor)
    {
        string y = floor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KitRun run = Run(
            $"kit.room{{name = 'r', pos = {{0,{y},0}}, size = {{16,6,16}}}}\n" +
            $"local p = kit.pit{{name = 'pit', pos = {{1,{y},1}}, radius = 3, depth = 4, seed = 2}}\nreturn tostring(p.floor_y)");
        double pitFloor = double.Parse(run.Result.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(HullDeform.WorldVertices(Brushes(run)[1]).Max(p => p.Y) >= floor + 0.005, "the pit's top ring must stand above the floor");
        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);
        foreach (CompiledLevel c in new[] { shared, incremental })
        {
            // Over the pit centre the room floor is gone: the only floor there is the pit's own.
            List<double> floors = FloorCoverage.FloorsAt(c.Geometry, 1, 1);
            Assert.DoesNotContain(floors, h => Math.Abs(h - floor) < 0.05);
            Assert.Contains(floors, h => Math.Abs(h - pitFloor) < 1e-3);
        }
    }

    [Theory]
    [InlineData(0.04)]
    [InlineData(0.124)]
    public void A_Structural_Ridge_On_A_Floor_Off_The_Sixteenth_Grid_Sinks_Below_It(double floor)
    {
        string y = floor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KitRun run = Run(
            $"kit.room{{name = 'r', pos = {{0,{y},0}}, size = {{30,8,30}}}}\n" +
            $"geo.rock_row{{from = {{-8,{y},8}}, to = {{8,{y},8}}, height = 3, depth = 2, structural = true, seed = 3}}");
        List<Brush> ridge = Brushes(run).Skip(1).ToList();
        Assert.All(ridge, b => Assert.True(HullDeform.WorldVertices(b).Min(p => p.Y) <= floor - 0.005, $"ridge piece {b.Uid} floats over or skims the floor"));
        AssertAccepted(run);
    }

    [Fact]
    public void An_Existing_Brush_In_The_Way_Of_A_Rock_Ridge_Is_Named_In_The_Hint()
    {
        Assert.Contains("existing brush", RockGen.NearCoplanarHint("near-coplanar: a corner of brush#12 1.2 mm off face 3", "the row"), StringComparison.Ordinal);
        Assert.Equal("Change seed or raise roughness.", RockGen.NearCoplanarHint("near-coplanar: rock 2 face 1 / face 3: 1 mm off one plane", "the row"));
    }

    [Fact]
    public void A_Cave_Started_Inside_Another_On_The_Same_Line_Is_Diagnosed_As_Walls_Lining_Up()
    {
        // Collinear and overlapping: every seed fails, and the reason is the other cave's walls, not a floor height.
        foreach (int seed in new[] { 1, 2 })
        {
            ScriptDiagnostic d = Fail("kit.cave{ name = 'a', seed = 1, path = { {0, 0, 0}, {30, 0, 0} } }\n" +
                                      $"kit.cave{{ name = 'b', seed = {seed}, path = {{ {{20, 0, 0}}, {{50, 0, 0}} }} }}");
            Assert.Contains("near-coplanar", d.Message, StringComparison.Ordinal);
            Assert.Contains("walls nearly line up with the walls of cave 'a'", d.Message, StringComparison.Ordinal);
            Assert.Contains("at an angle", d.Hint, StringComparison.Ordinal);
            Assert.Contains("radii", d.Hint, StringComparison.Ordinal);
            Assert.DoesNotContain("1/16", d.Hint, StringComparison.Ordinal);
        }

        // The same branch joined at an angle builds.
        AssertAccepted(Run("kit.cave{ name = 'a', seed = 1, path = { {0, 0, 0}, {30, 0, 0} } }\n" +
                           "kit.cave{ name = 'b', seed = 1, path = { {20, 0, 0}, {28, 0, 14} } }"));
    }

    [Fact]
    public void Only_Near_Parallel_Walls_Of_An_Organic_Piece_Count_As_Lining_Up()
    {
        static AuditShape Box(double x0, double x1, string? organic)
        {
            var b = new Brush { Geometry = BrushFactory.Box((float)(x1 - x0), 4, 4, 1, 1, 1, "rock.tga"), Rotation = Mat3.Identity, Position = new Vec3((float)((x0 + x1) / 2), 2, 0) };
            return AuditShape.FromBrush(b, "brush#1", organic);
        }

        AuditShape candidate = Box(0, 4, null);
        foreach ((string? organic, bool expected) in new[] { ("cave 'a' (brush#1)", true), ((string?)null, false) })
        {
            AuditShape placed = Box(0.002, 6, organic);
            IReadOnlyList<AuditConflict> conflicts = HullAudit.Conflicts(new[] { placed }, candidate);
            Assert.NotEmpty(conflicts);
            AuditConflict? clash = HullAudit.OrganicWallClash(new[] { placed }, candidate, conflicts);
            Assert.Equal(expected, clash is not null);
            if (clash is not null)
            {
                Assert.True(clash.PlacedFace >= 0 && clash.CandidateFace >= 0);
            }
        }
    }

    [Fact]
    public void Boulders_On_A_Sloped_Floor_Reach_Down_To_The_Lowest_Floor_Under_Them()
    {
        KitRun run = Run(
            "local t = geo.tube{path = {{0,0,0}, {0,12,24}}, profile = 'canyon', width = 14, height = 8, seed = 2}" + Environment.NewLine +
            "geo.scatter{along = t, count = 12, size = {1.2, 2}, seed = 3}");
        foreach (Brush rock in Brushes(run).Where(b => (b.Flags & (uint)BrushFlags.Detail) != 0))
        {
            // The floor climbs 0.5 m per metre along z: the rock's lower body must reach under the floor even at its
            // downhill edge (its downhill-most lower corner is at or under the floor there), or it floats on that side.
            List<Vec3d> w = HullDeform.WorldVertices(rock);
            double low = w.Min(p => p.Y) + (0.25 * (w.Max(p => p.Y) - w.Min(p => p.Y))); // the buried cap and the rim
            Vec3d downhill = w.Where(p => p.Y <= low).MinBy(p => p.Z);
            Assert.True(downhill.Y <= (downhill.Z * 0.5) + 1e-6, $"rock {rock.Uid} floats: its downhill lower corner {downhill} is over the floor at y = {downhill.Z * 0.5:0.###}");
        }
    }

    // ---- kit room mouths ---------------------------------------------------------------------------

    [Fact]
    public void A_Cave_Whose_Crown_Would_Cross_A_Rooms_Ceiling_Fails_With_The_Room_Height_To_Use()
    {
        ScriptDiagnostic e = KitTestHarness.Fail(
            "kit.room{name = 'west', pos = {0, 0, 0}, size = {12, 5, 12}}\n" +
            "kit.cave{name = 'link', seed = 4, path = {{3, 0, 0}, {16, 0, 4}, {28, 0, 18}}}");
        Assert.Contains("room 'west' is 5 m high, but the cave's crown can reach 5.4 m", e.Message, StringComparison.Ordinal);
        Assert.Contains("Raise the room to at least 5.5 m, or lower the cave's height or roughness.", e.Hint ?? string.Empty, StringComparison.Ordinal);

        // A 6 m room clears the same cave's crown.
        Run("kit.room{name = 'west', pos = {0, 0, 0}, size = {12, 6, 12}}" + Environment.NewLine + "kit.cave{name = 'link', seed = 4, path = {{3, 0, 0}, {16, 0, 4}, {28, 0, 18}}}");
    }

    [Fact]
    public void A_Canyon_Whose_Rim_Is_Above_A_Rooms_Ceiling_Fails()
    {
        ScriptDiagnostic e = KitTestHarness.Fail(
            "kit.room{name = 'hall', pos = {0, 0, 0}, size = {20, 8, 20}}\n" +
            "kit.canyon{name = 'cn', path = {{0, 0, 5}, {0, 0, 40}}, height = 12, sky = false}");
        Assert.Contains("room 'hall' is 8 m high, but the canyon's rim can reach 12 m", e.Message, StringComparison.Ordinal);
        Assert.Contains("lower the canyon's height", e.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- brush_query:set_face_flag ----------------------------------------------------------------

    [Fact]
    public void Set_Face_Flag_Flags_Only_The_Faces_Facing_The_Way_Asked()
    {
        KitRun run = Run(
            "local q = level.brushes_from_points{hulls = {{{0,0,0},{4,0,0},{0,0,4},{4,0,4},{0,3,0},{4,3,0},{0,3,4},{4,3,4}}}, air = true}\n" +
            "q:set_face_flag('up', 'show_sky', true)\n" +
            "local b = q:get(1)\n" +
            "assert(#b:faces_facing('up') == 1 and b:faces_facing('up')[1].show_sky and not b:faces_facing('down')[1].show_sky)");
        AssertUndoRestores(run);
        ScriptDiagnostic e = KitTestHarness.Fail("level.brushes_in{min = {-1,-1,-1}, max = {1,1,1}}:set_face_flag('sideways', 'show_sky', true)");
        Assert.Contains("unknown direction 'sideways'", e.Message, StringComparison.Ordinal);
    }

    // ---- the call budget counter -------------------------------------------------------------------

    [Fact]
    public void The_Run_Counts_Facade_Calls_But_Not_Property_Reads()
    {
        KitRun run = Run("local b = level.brush{pos = {0,2,0}, size = 4, air = true}\nlocal x = b.uid + b.uid + b.uid\nb:tag('a', 'b')\nreturn x");
        // ged.import("kit") (harness) plus the kit's own imports of its extension modules (kit_struct, kit_space,
        // kit_layout, kit_compose, kit_compose_payload, kit_compose_open, kit_dress, kit_probe, kit_core from kit_probe,
        // kit_core again from kit_dress, kit_items, kit_probe and kit_core again from kit_items, kit_uv, kit_modules,
        // kit_probe again from kit_modules), level.brush, b:tag.
        Assert.Equal(19, run.Result.FacadeCalls);
        Assert.True(run.Result.ScriptTime > TimeSpan.Zero);
    }

    // ---- the compiler-fit guard --------------------------------------------------------------------

    [Fact]
    public void The_Compiler_Fit_Guard_Flags_A_Thin_Far_Facet()
    {
        // tests/fixtures/organic/findings/solver-hang-thin-facet.lua (compiled in HullCompileTests).
        int[][] grid =
        {
            new[] { -10259, -1024, 120252 }, new[] { 15613, -1024, 113784 }, new[] { 15346, -1024, 117030 }, new[] { -7229, -1024, 128769 },
            new[] { -10259, -896, 120252 }, new[] { -9859, 18432, 120152 }, new[] { 16237, 18432, 113628 }, new[] { 16941, 11364, 113452 },
            new[] { -10254, 8866, 130342 }, new[] { -13129, 18432, 131837 }, new[] { 20846, 18432, 114170 }, new[] { 17971, 8160, 115665 },
        };
        List<Vec3d> pts = grid.Select(g => new Vec3d(g[0] / 1024.0, g[1] / 1024.0, g[2] / 1024.0)).ToList();
        Assert.True(HullBrush.TryBuild(pts, new HullBrushParams { Air = true }, 1, out Brush? far, out _).Success);
        Assert.True(CompilerFit.WorstError(far!) > CompilerFit.Tolerance, $"fit {CompilerFit.WorstError(far!)}");

        // The same hull near the origin fits well.
        Assert.True(HullBrush.TryBuild(pts.Select(p => p with { Z = p.Z - 117 }).ToList(), new HullBrushParams { Air = true }, 1, out Brush? near, out _).Success);
        Assert.True(CompilerFit.Fits(near!));
    }

    // ---- errors --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}, {10,0,0}}}", "the path turns", "at most 90° per point")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,1}}}", "only 1 m apart", "Remove one of them")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,8,6}}}", "the path climbs", "or use kit.pit / kit.stairs")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, roughness = 0.9}", "'roughness' must be 0.05 to 0.5", "Nearest valid value: 0.5")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, profile = 'tunnel'}", "unknown profile 'tunnel'", null)]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, lean = 0.2}", "'lean' applies only to profile = \"canyon\"", "Drop 'lean'")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, radius = 1}", "under the 1.8 m walkway", "Raise radius or floor_flatness")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, height = 2}", "under the 2 m headroom", "Raise height")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, sides = 20}", "'sides' must be 6 to 14", "Nearest valid value: 14")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}, {8,0,16}}, segments = 40}", "is too many for the leg", "Use segments = ")]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, radius = 3, width = 6}", "give one of 'radius', 'radii', 'width' or 'widths'", null)]
    [InlineData("geo.passage{path = {{0,0,0}, {0,1,10}}}", "a passage is level", "Use kit.cave for a climbing path")]
    [InlineData("geo.passage{path = {{0,0,0}, {0,0,10}, {1,0,0}}}", "the path turns", "at most 120° per point")]
    [InlineData("geo.pit{pos = {0,0,0}, radius = 3, depth = 0.5}", "'depth' must be 1 to 1024", "Nearest valid value: 1")]
    [InlineData("geo.scatter{count = 3}", "give exactly one of 'area'", null)]
    [InlineData("geo.scatter{area = {min = {0,0,0}, max = {2,0,2}}, count = 50, size = 1.5}", "only", "Lower count or size, or enlarge the area.")]
    [InlineData("geo.rock_row{from = {0,0,0}, to = {5,0,0}}", "'height' is required", null)]
    [InlineData("geo.tube{path = {{0,0,0}, {0,0,10}}, seed = 1.5}", "'seed' must be an integer", null)]
    [InlineData("kit.cave{path = {{0,0,0}, {0,0,10}}, step = 0.25}", "unknown option 'step'", "Valid options:")]
    public void Builder_Errors_Have_The_Stated_Shape(string script, string message, string? hint)
    {
        ScriptDiagnostic e = KitTestHarness.Fail(script);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        if (hint is not null)
        {
            Assert.Contains(hint, e.Hint ?? string.Empty, StringComparison.Ordinal);
        }
    }

    // ---- compiler regression independent of Lua (spec §4.3 HullChainCompileTests) -------------------

    [Theory]
    [InlineData("straight", true)]
    [InlineData("straight", false)]
    [InlineData("s-bend", true)]
    [InlineData("s-bend", false)]
    [InlineData("ninety", true)]
    [InlineData("ninety", false)]
    public void Hull_Chains_Built_Directly_Compile_Watertight(string shape, bool sharedBsp)
    {
        Vec3d[] path = shape switch
        {
            "straight" => new[] { new Vec3d(0, 0, 0), new Vec3d(0, 0, 30) },
            "s-bend" => new[] { new Vec3d(0, 0, 0), new Vec3d(0, 0, 15), new Vec3d(10, 0, 28), new Vec3d(10, 0, 45) },
            _ => new[] { new Vec3d(0, 0, 0), new Vec3d(0, 0, 20), new Vec3d(20, 0, 20) },
        };
        TubeResult? tube = HullChain.Build(new TubeOptions { Path = path, Seed = 3 }, out GeoEditError? error);
        Assert.True(tube is not null, error?.Message);
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        foreach (Brush b in tube!.Brushes)
        {
            b.Uid = doc.AllocateUid();
        }

        new BrushEditor(doc).AddBrushes(tube.Brushes, "chain");
        CompiledLevel c = AssertWatertight(doc, sharedBsp);
        Assert.Equal(sharedBsp, c.Report.SharedBspUsed);
    }
}
