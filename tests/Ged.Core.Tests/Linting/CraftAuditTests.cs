using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Compiler;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Ged.Core.Tests.Scripting;
using Ged.Core.Tests.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests;

/// <summary>
/// The craft audit (<see cref="CraftAudit"/>) on small synthetic levels, one per check: sunk, floating and random props,
/// stairs and bridges that lead nowhere (the intent layer read from the geometry), pieces built with purpose off,
/// openings into solid, prism rock against organic rock, basic areas; the lint, Lua and scorer hooks.
/// </summary>
public sealed class CraftAuditTests
{
    private readonly ITestOutputHelper _out;

    public CraftAuditTests(ITestOutputHelper output) => _out = output;

    private static SpatialLevel Build(string script, Action<EditorDocument>? after = null)
    {
        KitTestHarness.KitRun run = KitTestHarness.Run(script);
        Assert.True(run.Result.Success, run.Result.Error?.ToDisplayString());
        CompiledLevel compiled = KitTestHarness.Sealed(run.Doc, sharedBsp: true);
        GeometryBuildService.Apply(run.Doc.Rfl, compiled);
        after?.Invoke(run.Doc);
        run.Doc.RefreshObjects();
        return SpatialLevel.FromDocument(run.Doc, null, GeometryFreshness.StoredFile, "dm_craft.rfl");
    }

    /// <summary>Every object is a 1 m cube standing on its origin.</summary>
    private static readonly Func<LevelObject, (Vec3 Min, Vec3 Max)?> Cube = _ => (new Vec3(-0.5f, 0, -0.5f), new Vec3(0.5f, 1, 0.5f));

    private CraftAuditReport Audit(SpatialLevel level, params string[] checks)
    {
        CraftAuditReport r = CraftAudit.Run(level, new CraftAuditOptions { Checks = checks.Length > 0 ? checks : null, ObjectBounds = Cube });
        foreach (CraftFinding f in r.Findings)
        {
            _out.WriteLine($"{f.Check}/{f.Kind}: {f.Message}");
        }

        foreach (string n in r.Notes)
        {
            _out.WriteLine("note: " + n);
        }

        return r;
    }

    private static readonly string Hall = """
        level.brush{pos = {0, 3, 0}, size = {24, 6, 24}, air = true, texture = "mtl_floor01.tga"}
        for _, p in ipairs({ {-8, 1, -8}, {8, 1, -8}, {-8, 1, 8}, {8, 1, 8} }) do level.place{kind = 'respawn', pos = p} end
        """ + "\n";

    [Fact]
    public void Props_Sunk_Into_Or_Floating_Over_The_Floor_Are_Reported_And_Resting_Ones_Are_Not()
    {
        SpatialLevel level = Build(Hall + """
            level.brush{pos = {-6, 0.2, 0}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {0, 0.5, 6}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {6, 1.5, 0}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            """, doc =>
        {
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(-6, -0.4f, 6), "Plastic Crate")!.ScriptName = "sunk_crate";
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(6, 0, 6), "Plastic Crate")!.ScriptName = "good_crate";
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(-6, 1.2f, -6), "Plastic Crate")!.ScriptName = "floating_crate";
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(11.8f, 0, -2), "Plastic Crate")!.ScriptName = "wall_crate";
        });
        CraftAuditReport r = Audit(level, "props");
        Assert.Contains(r.Findings, f => f.Kind == "sunk" && f.Message.Contains("sunk_crate", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Kind == "sunk" && f.Message.StartsWith("Detail brush", StringComparison.Ordinal) && f.Value is > 0.25 and < 0.35);
        Assert.Contains(r.Findings, f => f.Kind == "floating" && f.Message.Contains("floating_crate", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Kind == "floating" && f.Message.StartsWith("Detail brush", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Kind == "in_wall" && f.Message.Contains("wall_crate", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Findings, f => f.Message.Contains("good_crate", StringComparison.Ordinal) && f.Kind is "sunk" or "floating" or "in_wall");
        Assert.DoesNotContain(r.Findings, f => f.Pos.Z > 5 && Math.Abs(f.Pos.X) < 1);
        Assert.All(r.Findings, f => Assert.False(string.IsNullOrEmpty(f.Fix)));
    }

    private static readonly string TwoRooms = """
        level.brush{pos = {-5, 3, 0}, size = {10, 6, 20}, air = true, texture = "mtl_floor01.tga"}
        level.brush{pos = {6, 3, 0}, size = {10, 6, 20}, air = true, texture = "mtl_floor01.tga"}
        level.brush{pos = {0.5, 1.5, 0}, size = {1.5, 3, 3}, air = true, texture = "mtl_floor01.tga"}
        level.brush{pos = {-10.25, 1.5, 6}, size = {1.5, 3, 3}, air = true, texture = "mtl_floor01.tga"}
        for _, p in ipairs({ {-5, 1, -5}, {-5, 1, 5}, {6, 1, -5}, {6, 1, 5} }) do level.place{kind = 'respawn', pos = p} end
        """ + "\n";

    [Fact]
    public void A_Doorway_Into_Solid_Is_Reported_And_One_Between_Two_Rooms_Is_Not()
    {
        CraftAuditReport r = Audit(Build(TwoRooms), "openings");
        CraftFinding f = Assert.Single(r.Findings);
        Assert.Equal("into_nothing", f.Kind);
        Assert.InRange(f.Pos.X, -10.6, -9.9);
        Assert.Contains("solid", f.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Prop_In_A_Doorway_And_A_Lone_Prop_In_The_Open_Are_Random()
    {
        SpatialLevel level = Build(TwoRooms + "level.brush{pos = {-40, 3, 0}, size = {24, 6, 24}, air = true, texture = 'mtl_floor01.tga'}\n"
            + "level.brush{pos = {-19, 1.5, 0}, size = {18.5, 3, 3}, air = true, texture = 'mtl_floor01.tga'}\n", doc =>
        {
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(0.5f, 0, 0), "Plastic Crate")!.ScriptName = "door_crate";
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(-40, 0, 0), "Plastic Crate")!.ScriptName = "lone_crate";
        });
        CraftAuditReport r = Audit(level, "props");
        Assert.Contains(r.Findings, f => f.Kind == "in_doorway" && f.Message.Contains("door_crate", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Kind == "lone" && f.Message.Contains("lone_crate", StringComparison.Ordinal));
    }

    [Fact]
    public void Stairs_That_Lead_Nowhere_Are_Read_From_The_Geometry_And_Purpose_Off_Pieces_Are_Held_To_Their_Purpose()
    {
        CraftAuditReport r = Audit(SpatialFixtures.Get("flights").Level, "stairs", "perches", "purpose");
        Assert.Contains(r.Findings, f => f.Check == "stairs" && f.Kind is "into_obstacle" or "onto_nothing" && f.Pos.X > 5);
        Assert.DoesNotContain(r.Findings, f => f.Check == "stairs" && f.Pos.X < -5);
        Assert.Contains(r.Findings, f => f.Check == "purpose" && f.Kind == "purpose_failed" && f.Message.Contains("wallward", StringComparison.Ordinal));

        CraftAuditReport pit = Audit(SpatialFixtures.Get("pit_bridge").Level, "stairs", "purpose");
        Assert.Equal(2, pit.Findings.Count(f => f.Check == "stairs" && f.Kind == "in_gap"));
        Assert.Contains(pit.Findings, f => f.Check == "purpose" && f.Message.Contains("pit_", StringComparison.Ordinal));
    }

    [Fact]
    public void Flat_Sided_Prism_Rock_Fails_And_An_Organic_Rock_Wall_Passes()
    {
        CraftAuditReport prism = Audit(Build(Hall + "level.brush{pos = {0, 3, 0}, size = {6, 6, 6}, texture = 'rck_default.tga'}\n"), "rock");
        Assert.Contains(prism.Findings, f => f.Kind == "flat_sided" && f.Value > CraftAudit.RockFlatShare);

        CraftAuditReport organic = Audit(Build(Hall + "kit.rock_wall{name = 'ridge', from = {-9, 0, -6}, to = {9, 0, -6}, height = 5, texture = 'rck_default.tga'}\n"), "rock");
        _out.WriteLine(organic.RockRegions.ToJsonString());
        Assert.True(organic.RockRegions.Count > 0, "the rock wall is read");
        Assert.Empty(organic.Findings);
    }

    [Fact]
    public void A_Stacked_Prism_Rock_Pile_Is_Flat_Sided()
    {
        // Three extruded rock prisms stacked into a pile in one 16 m region: every side a sheer plane.
        CraftAuditReport r = Audit(Build(Hall + """
            level.brush{pos = {6, 1, 6}, size = {8, 2, 8}, shape = 'cylinder', sides = 6, detail = true, texture = 'rck_default.tga'}
            level.brush{pos = {6.5, 3, 6.4}, size = {6, 2, 6}, shape = 'cylinder', sides = 6, detail = true, texture = 'rck_default.tga'}
            level.brush{pos = {5.8, 5, 5.6}, size = {4, 2, 4}, shape = 'cylinder', sides = 5, detail = true, texture = 'rck_default.tga'}
            """), "rock");
        _out.WriteLine(r.RockRegions.ToJsonString());
        Assert.Contains(r.Findings, f => f.Kind == "flat_sided" && f.Value > CraftAudit.RockFlatShare);
    }

    [Fact]
    public void Rock_Hidden_Behind_Or_Inside_The_Organic_Builders_Skins_Is_Not_Read()
    {
        // Stock organic rock only: traced canyons (one leaning and overhanging) and a rock-walled yard, whose detail rock
        // skins stand in front of flat structural walls (which survive the build: detail does not carve).
        SpatialLevel level = Build("""
            kit.trace{name = 't', seed = 3, regions = {
              {name = 'a', kind = 'canyon', floor = 0, height = 16, poly = {{-60, -20}, {-20, -20}, {-20, 20}, {-60, 20}}},
              {name = 'b', kind = 'canyon', floor = 0, height = 16, lean = 4, overhang = 1.5, poly = {{0, -20}, {40, -20}, {40, 20}, {0, 20}}},
              {name = 'c', kind = 'yard', floor = 0, height = 16, sky = true, walls = 'rock', poly = {{60, -20}, {100, -20}, {100, 20}, {60, 20}}},
            }}
            level.player_start{pos = {-40, 1, 0}, yaw = 0}
            """);
        CraftAuditReport r = Audit(level, "rock");
        _out.WriteLine(r.RockRegions.ToJsonString());
        Assert.True(r.RockRegions.Count >= 12, "the canyons' and the yard's rock is read");
        Assert.Contains(r.RockRegions, g => g!["hidden_area"]!.GetValue<double>() > 100);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void A_Climbing_Cave_Leg_And_Framed_Doorways_Are_Not_Openings_And_A_Doorway_Into_Rock_Still_Is()
    {
        // A kit.cave climbing 6 m between two rooms (its thin air slices are no doorways); two doorways framed by one
        // detail pass (one kit.name: each frame is its own candidate, never one 30 m "opening"); and a door-sized cut
        // from the first room into the solid rock beside the cave.
        SpatialLevel level = Build("""
            local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {14, 7, 14}}
            local b = kit.room{name = 'b', pos = {36, 6, 0}, size = {14, 7, 14}}
            local c = kit.room{name = 'c', pos = {0, 0, -30}, size = {14, 7, 40}}
            local d = kit.room{name = 'd', pos = {-18, 0, 0}, size = {14, 7, 14}}
            kit.cave{name = 'leg', path = { {5, 0, 0}, {11, 0, 0}, {18, 6, 0}, {31, 6, 0} }, radius = 2.1, height = 3.5, roughness = 0.12, floor_flatness = 0.9, seed = 1}
            kit.frame_opening{doorway = kit.doorway{name = 'ac', a = a, b = c, width = 3, height = 3}, name = 'frames'}
            kit.frame_opening{doorway = kit.doorway{name = 'ad', a = a, b = d, width = 3, height = 3}, name = 'frames'}
            level.brush{pos = {0, 1.5, 7.75}, size = {3, 3, 1.5}, air = true, texture = 'rck_default.tga'}
            for _, p in ipairs({ {-4, 1, -4}, {4, 1, 4}, {32, 7, -4}, {40, 7, 4}, {0, 1, -40}, {-18, 1, 0} }) do level.place{kind = 'respawn', pos = p} end
            """);
        CraftAuditReport r = Audit(level, "openings");
        CraftFinding f = Assert.Single(r.Findings);
        Assert.Equal("into_nothing", f.Kind);
        Assert.InRange(f.Pos.Z, 7, 8.6);
        Assert.Contains("solid", f.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Bridge_Between_Two_Tunnel_Mouths_Ends_On_The_Tunnel_Floor_Not_On_A_Ledge_Beside_It()
    {
        // A deck across a 6 m deep hall between two tunnels at deck height, each leading to a room. Beside the east mouth a
        // rock shelf 0.55 m below the deck touches the deck's side and runs on past its end (a dead end: the drop all round,
        // rock behind). The deck's walk-on end is the tunnel floor in line with it, and the bridge serves the route.
        SpatialLevel level = Build("""
            level.brush{pos = {0, 8, 0}, size = {30, 16, 30}, air = true, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {19.5, 8, 6}, size = {9, 16, 8}, air = true, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {19, 8, 0}, size = {8, 4, 3}, air = true, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {30, 9, 0}, size = {14, 6, 14}, air = true, texture = 'mtl_floor01.tga'}
            level.brush{pos = {-19, 8, 0}, size = {8, 4, 3}, air = true, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {-30, 9, 0}, size = {14, 6, 14}, air = true, texture = 'mtl_floor01.tga'}
            level.brush{pos = {0, 5.75, 0}, size = {30, 0.5, 3}, detail = true, texture = 'mtl_floor01.tga'}
            level.brush{pos = {13.25, 2.725, 3}, size = {2.5, 5.45, 3}, detail = true, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {19.25, 2.725, 3.5}, size = {9.5, 5.45, 3}, detail = true, texture = 'rck_canyon_rock03.tga'}
            for _, p in ipairs({ {-30, 7, 0}, {-34, 7, -4}, {30, 7, 0}, {34, 7, 4} }) do level.place{kind = 'respawn', pos = p} end
            """);
        JsonObject purpose = SpatialQueries.Analyze(level, new SpatialBudget()).Purpose();
        JsonObject bridge = purpose["bridges"]!.AsArray().Select(b => b!.AsObject()).OrderByDescending(b => b["span"]!.GetValue<double>()).First();
        _out.WriteLine(bridge.ToJsonString());
        JsonArray end = bridge["end_b"]!.AsArray();
        Assert.InRange(end[0]!.GetValue<double>(), 14.5, 17);
        Assert.InRange(end[1]!.GetValue<double>(), 5.9, 6.1);
        Assert.InRange(end[2]!.GetValue<double>(), -1.5, 1.5);
        Assert.Null(bridge["fault"]);
        Assert.DoesNotContain(Audit(level, "stairs").Findings, f => f.Kind == "purposeless_bridge");
    }

    [Fact]
    public void An_Empty_Hall_Is_Basic()
    {
        CraftAuditReport r = Audit(SpatialFixtures.Get("empty").Level, "density");
        Assert.Contains(r.Findings, f => f.Kind == "basic");
        Assert.True(r.DensityRegions.Count > 0);
    }

    [Fact]
    public void The_Linter_Reports_Craft_Findings_On_Explicit_Runs_Only_And_Lua_Lists_Them()
    {
        SpatialLevel level = Build(TwoRooms);
        LintReport quiet = LevelLinter.Lint(level.Rfl, new LintOptions { CheckGeometryLeaks = false });
        Assert.DoesNotContain(quiet.Findings, f => f.Category == LintCategory.CraftOpening);
        LintReport loud = LevelLinter.Lint(level.Rfl, new LintOptions { CheckGeometryLeaks = false, CheckCraft = true });
        Assert.Contains(loud.Findings, f => f.Category == LintCategory.CraftOpening && f.Severity == LintSeverity.Warning && f.Location is not null);

        (ScriptRunResult run, ScriptLog log) = ScriptTestFixture.Run(SpatialFixtures.Get("flights").Doc, """
            local a = level.audit{checks = {"stairs", "purpose"}}
            assert(not a.clean, "the wallward flight fails")
            assert(a.counts.stairs >= 1, "stairs counted")
            local f = a.findings[1]
            assert(f.pos.x ~= nil and f.fix ~= nil and f.message ~= nil, "findings carry pos, fix and message")
            """);
        Assert.True(run.Success, run.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries.Select(e => e.Message)));
        (ScriptRunResult bad, _) = ScriptTestFixture.Run(SpatialFixtures.Get("flights").Doc, "level.audit{checks = {'nope'}}");
        Assert.False(bad.Success);
        Assert.Contains("unknown check", bad.Error!.ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lights_At_The_Ceiling_Of_A_Tall_Hall_Count_For_Its_Floor()
    {
        // A 14 m hall, four 24 m density regions: its only lights hang 0.5 m under the ceiling, 13.5 m over the floor. They
        // light the hall, so every region reads lights (the reading used to stop 8 m above the floor and found none).
        SpatialLevel level = Build("""
            level.brush{pos = {0, 7, 0}, size = {48, 14, 48}, air = true, texture = "mtl_floor01.tga"}
            for _, p in ipairs({ {-12, 1, -12}, {12, 1, -12}, {-12, 1, 12}, {12, 1, 12} }) do level.place{kind = 'respawn', pos = p} end
            for _, p in ipairs({ {-12, 13.5, -12}, {12, 13.5, -12}, {-12, 13.5, 12}, {12, 13.5, 12} }) do level.place{kind = 'light', pos = p} end
            """);
        CraftAuditReport r = Audit(level, "density");
        _out.WriteLine(r.DensityRegions.ToJsonString());
        Assert.Equal(4, r.DensityRegions.Count);
        Assert.All(r.DensityRegions, g => Assert.True(g!["lights_per_100m2"]!.GetValue<double>() > 0, g.ToJsonString()));
    }

    /// <summary>A hall over a 5 m deep trench, its floor reached from the spawns, with a deck along the trench.</summary>
    private static string Trench(string deck) => """
        level.brush{pos = {0, 4, 0}, size = {40, 8, 30}, air = true, texture = 'mtl_floor01.tga'}
        level.brush{pos = {0, -2.5, 0}, size = {24, 5, 3.4}, air = true, texture = 'mtl_floor01.tga'}
        for _, p in ipairs({ {-16, 1, -12}, {16, 1, -12}, {-16, 1, 12}, {16, 1, 12} }) do level.place{kind = 'respawn', pos = p} end
        """ + "\n" + deck + "\n";

    [Fact]
    public void The_Same_Level_Audited_Twice_Gives_The_Same_Findings_And_A_Measurement_Cut_Short_Is_Not_Clean()
    {
        string script = Trench("level.brush{pos = {0, -0.25, 0}, size = {26, 0.5, 1.6}, detail = true, texture = 'mtl_floor01.tga'}") + """
            level.brush{pos = {12, 0.2, 8}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {-12, 1.5, 8}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {14, 3, -10}, size = {5, 6, 5}, texture = 'rck_default.tga'}
            for _, p in ipairs({ {-10, 7.5, 0}, {10, 7.5, 0} }) do level.place{kind = 'light', pos = p} end
            """;
        string Once()
        {
            JsonObject j = CraftAudit.Run(Build(script), new CraftAuditOptions { ObjectBounds = Cube }).ToJson();
            j.Remove("rules");
            return j.ToJsonString();
        }

        string a = Once(), b = Once();
        _out.WriteLine(a);
        Assert.Equal(a, b);

        // A measurement cut short by its time limit never reads as zero findings: the walk-graph checks are skipped,
        // with no count, and the report is neither complete nor clean.
        CraftAuditReport cut = CraftAudit.Run(Build(script), new CraftAuditOptions { ObjectBounds = Cube, Budget = new SpatialBudget { TimeLimit = TimeSpan.FromTicks(1) } });
        Assert.False(cut.Complete);
        Assert.False(cut.Clean);
        Assert.Equal(new[] { "stairs", "perches", "purpose", "density", "props.random" }, cut.Skipped);
        Assert.False(cut.Counts.ContainsKey("density"));
        Assert.True(cut.Counts.ContainsKey("rock"));
        JsonObject cj = cut.ToJson();
        Assert.False(cj["complete"]!.GetValue<bool>());
        Assert.Equal(5, cj["skipped"]!.AsArray().Count);
    }

    [Fact]
    public void Stacked_Props_And_Wall_Fixtures_Are_Supported_And_A_Floating_Crate_Still_Floats()
    {
        SpatialLevel level = Build(Hall + """
            level.brush{pos = {-6, 0.5, -6}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {-6, 1.4, -6}, size = {0.8, 0.8, 0.8}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {-6.1, 2.03, -6}, size = {0.5, 0.5, 0.5}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {-2, 1.25, -6}, size = {0.3, 2.5, 0.3}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {-2, 2.7, -6}, size = {0.4, 0.4, 0.4}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {5, 2.7, -11.8}, size = {0.4, 0.4, 0.4}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {-8, 2.5, -11.5}, size = {1, 1, 1}, detail = true, texture = "mtl_floor01.tga"}
            level.brush{pos = {6, 3, 6}, size = {5, 6, 5}, shape = 'cylinder', sides = 16, texture = "mtl_floor01.tga"}
            level.brush{pos = {6 + 2.57 * 0.7071, 3.5, 6 + 2.57 * 0.7071}, size = {1.2, 1, 0.2}, rot = {yaw = 45}, detail = true, texture = "mtl_floor01.tga"}
            """);
        CraftAuditReport r = Audit(level, "props");
        List<CraftFinding> floating = r.Findings.Where(f => f.Kind is "floating" or "sunk").ToList();

        // The crate stack (a crate exactly on a crate, a third 2 cm into the second) and the lamp on a post top rest on
        // detail tops; the wall lamp and the emblem plate on the round silo hang on their walls.
        Assert.DoesNotContain(floating, f => Math.Abs(f.Pos.X + 6) < 1 && Math.Abs(f.Pos.Z + 6) < 1);
        Assert.DoesNotContain(floating, f => Math.Abs(f.Pos.X + 2) < 1);
        Assert.DoesNotContain(floating, f => Math.Abs(f.Pos.X - 5) < 0.5);
        Assert.DoesNotContain(floating, f => f.Pos.X > 7 && f.Pos.Z > 7);

        // A 1 m crate held against the wall in mid-air is no fixture: it still floats.
        Assert.Contains(floating, f => f.Kind == "floating" && Math.Abs(f.Pos.X + 8) < 0.5);
    }

    [Fact]
    public void A_Prop_Under_A_Balcony_Is_Measured_To_The_Floor_It_Stands_On()
    {
        // A 4 m tall prop on the floor beside a balcony slab (top 3.3 m) that reaches over a strip of its box: the balcony is
        // overhead, not the floor it stands on, so it is neither sunk 3.3 m nor floating. The same prop 0.5 m into the
        // floor is still sunk.
        SpatialLevel level = Build(Hall + "level.brush{pos = {3.25, 3.15, 6}, size = {5.5, 0.3, 6}, detail = true, texture = 'mtl_floor01.tga'}\n", doc =>
        {
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(0, 0, 6), "Plastic Crate")!.ScriptName = "pump_by_balcony";
            doc.PlaceObject(LevelObjectKind.Clutter, new Vec3(-6, -0.5f, -6), "Plastic Crate")!.ScriptName = "pump_sunk";
        });
        CraftAuditReport r = CraftAudit.Run(level, new CraftAuditOptions { Checks = new[] { "props" }, ObjectBounds = _ => (new Vec3(-1, 0, -1), new Vec3(1, 4, 1)) });
        foreach (CraftFinding f in r.Findings)
        {
            _out.WriteLine($"{f.Kind}: {f.Message}");
        }

        Assert.DoesNotContain(r.Findings, f => f.Kind is "sunk" or "floating" && f.Message.Contains("pump_by_balcony", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Kind == "sunk" && f.Message.Contains("pump_sunk", StringComparison.Ordinal) && f.Value is > 0.4 and < 0.6);
    }

    [Fact]
    public void A_Round_Pipe_Top_Is_No_Bridge_And_A_Redundant_Deck_Is_Reported_With_Its_Brush()
    {
        // A 2.4 m pipe along a 5 m deep trench, its top level with the floor: the walk lattice reads its rounded top as a
        // deck, but it is level across under 1 m.
        SpatialLevel pipe = Build(Trench("level.brush{pos = {0, -1.2, 0}, size = {2.4, 26, 2.4}, shape = 'cylinder', sides = 20, rot = {roll = 90}, detail = true, texture = 'mtl_floor01.tga'}"));
        JsonObject pp = SpatialQueries.Analyze(pipe, new SpatialBudget()).Purpose();
        _out.WriteLine(pp["bridges"]!.ToJsonString());
        Assert.NotEmpty(pp["bridges"]!.AsArray());
        Assert.All(pp["bridges"]!.AsArray(), b => Assert.Contains("no walkable deck", b!["not_a_bridge"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.DoesNotContain(Audit(pipe, "stairs").Findings, f => f.Kind == "purposeless_bridge");

        // A 1.6 m flat deck along the same trench is a bridge, and a redundant one (the floor beside the trench joins its
        // ends as directly): the finding names the deck's brush, in the JSON and in Lua.
        KitTestHarness.KitRun run = KitTestHarness.Run(Trench("level.brush{pos = {0, -0.25, 0}, size = {26, 0.5, 1.6}, detail = true, texture = 'mtl_floor01.tga'}"));
        Assert.True(run.Result.Success, run.Result.Error?.ToDisplayString());
        GeometryBuildService.Apply(run.Doc.Rfl, KitTestHarness.Sealed(run.Doc, sharedBsp: true));
        run.Doc.RefreshObjects();
        SpatialLevel deck = SpatialLevel.FromDocument(run.Doc, null, GeometryFreshness.StoredFile, "dm_craft.rfl");
        int deckUid = deck.Brushes().Single(b => (b.Flags & (uint)BrushFlags.Detail) != 0).Uid;
        CraftFinding f = Assert.Single(Audit(deck, "stairs").Findings, x => x.Kind == "purposeless_bridge");
        Assert.Equal(deckUid, f.Uid);
        Assert.Contains(deckUid, f.Uids!);
        Assert.Equal(deckUid, f.ToJson()["uids"]!.AsArray()[0]!.GetValue<int>());

        (ScriptRunResult lua, ScriptLog log) = ScriptTestFixture.Run(run.Doc, $"local deck = {deckUid}\n" + """
            local a = level.audit{checks = {"stairs"}}
            local found = false
            for _, f in ipairs(a.findings) do
              if f.kind == "purposeless_bridge" then found = f.uid == deck and f.uids[1] == deck end
            end
            assert(found, "the bridge finding carries its deck brush")
            """);
        Assert.True(lua.Success, lua.Error?.ToDisplayString() + "\n" + string.Join("\n", log.Entries.Select(e => e.Message)));
    }

    [Fact]
    public void A_Lintel_No_Walk_Reaches_Is_No_Bridge_And_The_Same_Deck_Reached_By_Steps_Is()
    {
        // A 14 m rock lintel over the passage between two 7 m buttresses (running from the front wall to a rock shelf at the
        // back), over the spawns' floor: its top and the buttress tops are kept as playable (floor reached by moves the walk graph does not model), but no walk from the spawns
        // reaches them, so the lintel is no bridge. With steps up to one buttress, the same deck is a bridge.
        const string Lintel = """
            level.brush{pos = {0, 8, 0}, size = {60, 16, 30}, air = true, texture = 'mtl_floor01.tga'}
            level.brush{pos = {-13, 3.5, -4.5}, size = {12, 7, 21}, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {13, 3.5, -4.5}, size = {12, 7, 21}, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {0, 3.5, 10.5}, size = {60, 7, 9}, texture = 'rck_canyon_rock03.tga'}
            level.brush{pos = {0, 6.6, 0}, size = {14, 0.8, 2.5}, detail = true, texture = 'rck_canyon_rock03.tga'}
            for _, p in ipairs({ {-27, 1, -12}, {27, 1, -12}, {0, 1, -12}, {-25, 1, 3} }) do level.place{kind = 'respawn', pos = p} end
            """ + "\n";
        JsonObject alone = SpatialQueries.Analyze(Build(Lintel), new SpatialBudget()).Purpose();
        _out.WriteLine(alone["bridges"]!.ToJsonString());
        JsonObject lintel = Assert.Single(alone["bridges"]!.AsArray())!.AsObject();
        Assert.Contains("no walk from the spawns", lintel["not_a_bridge"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(lintel["fault"]);
        Assert.Null(lintel["role"]);

        string steps = "for i = 1, 7 do level.brush{pos = {-26.5 + i, i / 2, 0}, size = {1, i, 3}, texture = 'mtl_floor01.tga'} end\n";
        JsonObject reached = SpatialQueries.Analyze(Build(Lintel + steps), new SpatialBudget()).Purpose();
        _out.WriteLine(reached["bridges"]!.ToJsonString());
        Assert.Contains(reached["bridges"]!.AsArray(), b => b!["not_a_bridge"] is null);
    }

    [Fact]
    public void Mockup_Reachability_Leaves_Out_Kill_Zones_And_Floor_Only_Reached_By_Climbing()
    {
        var samples = new List<(int I, int K, FloorPoint P)>();
        for (int i = 0; i < 10; i++)
        {
            samples.Add((i, 0, new FloorPoint(i < 5 ? 0 : -18, double.PositiveInfinity))); // route floor, then a chasm floor 18 m down
        }

        for (int i = 0; i < 5; i++)
        {
            samples.Add((i, 1, new FloorPoint(10, double.PositiveInfinity))); // a mesa top 10 m up
        }

        RecreationFloor floor = RecreationFloor.FromSamples(0.5, samples);
        var stand = new List<Vec3d> { new(0.2, 1, 0.2) };
        var legacy = floor.Reachable(stand)!.Value;
        Assert.Equal(15, legacy.Kept.Count); // the level rule: a drop joins either way, so everything stays

        var mockup = floor.Reachable(stand, oneWay: true, RecreationFloor.MaxFall, RecreationFloor.KillDepth)!.Value;
        Assert.Equal(5, mockup.Kept.Count);
        Assert.Equal(0, mockup.Kept.MinY);
        Assert.Equal(0, mockup.Kept.MaxY);
    }
}
