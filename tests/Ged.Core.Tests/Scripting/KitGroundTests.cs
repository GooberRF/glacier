using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitLayoutHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// kit.ground (ALPINE-COMPAT-SPEC §4.4, chunk C10): the brush-or-terrain choice in each mode, auto's decision on areas
/// that qualify and areas that do not (small, roofed, a cave, no Direct3D 11 target), the explain result (inputs, no
/// change), terrain built through the level.terrain API and enclosed in air (a sky volume over open space) so the level
/// still seals, the ground provider reading terrain, and an open-air layout on terrain ground that builds sealed, passes
/// the flow checks, keeps every spawn reachable about 1 m above the terrain and is graded on the terrain's floor.
/// </summary>
public sealed class KitGroundTests
{
    private readonly ITestOutputHelper _out;

    public KitGroundTests(ITestOutputHelper output) => _out = output;

    // An outdoor yard (a kit.room open to the sky) and a small roofed room beside it.
    private const string YardRooms = """
        local yard = kit.room{ name = "yard", pos = { 0, 0, 0 }, size = { 30, 12, 26 } }
        kit.sky_room{ open = { yard } }
        local small = kit.room{ name = "small", pos = { 60, 0, 0 }, size = { 12, 6, 12 } }

        """;

    private const string Yard = YardRooms + """
        local function line(r) return r.choice .. "|" .. r.reason .. "|" .. tostring(r.built) end

        """;

    [Fact]
    public void Explain_Reports_The_Choice_And_Inputs_In_Every_Mode_Without_Changing_The_Level()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult setup, _) = ScriptTestFixture.Run(doc, "local kit = ged.import('kit')\n" + YardRooms);
        Assert.True(setup.Success, setup.Error?.ToDisplayString());
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local kit = ged.import('kit')\n" + """
            local yard = { min = level.brushes_tagged("kit.name", "yard").brushes[1].bounds.min, max = level.brushes_tagged("kit.name", "yard").brushes[1].bounds.max, sky = true }
            local small = { min = { 54, 0, -6 }, max = { 66, 6, 6 } }
            local function line(r) return r.choice .. "|" .. r.reason .. "|" .. tostring(r.built) .. "|" .. tostring(r.explain) end
            local a = kit.ground{ area = yard, explain = true }
            local b = kit.ground{ area = yard, explain = true, d3d11 = true }
            local c = kit.ground{ area = yard, mode = "brush", explain = true, d3d11 = true }
            local d = kit.ground{ area = yard, mode = "terrain", explain = true }
            local e = kit.ground{ min = small.min, max = small.max, explain = true, d3d11 = true }
            local f = kit.ground{ min = { 60, 0, 40 }, max = { 72, 0, 52 }, explain = true, d3d11 = true }
            local i = b.inputs
            return table.concat({ line(a), line(b), line(c), line(d), line(e), line(f),
              i.area .. "/" .. i.min_side .. "/" .. tostring(i.large) .. "/" .. tostring(i.open_topped) .. "/" .. tostring(i.overhang) .. "/" .. tostring(i.natural) .. "/" .. tostring(i.d3d11) .. "/" .. table.concat(i.d3d11_signals, ","),
              tostring(a.inputs.d3d11) .. "/" .. #a.inputs.d3d11_signals, b.cells[1] .. "x" .. b.cells[2] .. "@" .. b.cell }, "\n")
            """);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        _out.WriteLine(r.ReturnValue);
        string[] l = r.ReturnValue!.Split('\n');
        Assert.StartsWith("brush|auto: brush ground, because the level does not target Direct3D 11", l[0], StringComparison.Ordinal);
        Assert.StartsWith("terrain|auto: terrain, because the area is large (780 m², 26 m across), open to the sky, needs no overhang and the level targets Direct3D 11 (declared)", l[1], StringComparison.Ordinal);
        Assert.Equal("brush|asked for brush ground|false|true", l[2]);
        Assert.Equal("terrain|asked for terrain ground (780 m², 26 m across)|false|true", l[3]);
        Assert.StartsWith("brush|auto: brush ground, because it is not open to the sky (0% of it)", l[4], StringComparison.Ordinal);
        // Over open space (no brush at all) a terrain would get its own sky volume, so the area is open; 144 m² is small.
        Assert.StartsWith("brush|auto: brush ground, because the area is small (144 m², 12 m across", l[5], StringComparison.Ordinal);
        Assert.Equal("780/26/true/true/false/true/true/declared", l[6]);
        Assert.Equal("false/0", l[7]);
        Assert.Equal("60x52@0.5", l[8]);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Auto_Picks_Terrain_Only_When_The_Level_Targets_Direct3D11_And_Refuses_Caves_And_Roofs()
    {
        (_, ScriptRunResult r) = Run(Yard + """
            local cave = kit.cave{ name = "c", path = { { -40, 0, 0 }, { -40, 0, 30 } } }
            local out = { line(kit.ground{ area = yard }) }
            level.alpine:set("require_d3d11_setting", true)
            local g = kit.ground{ area = yard, name = "yard_ground", seed = 3 }
            out[#out + 1] = line(g)
            out[#out + 1] = line(kit.ground{ area = cave, explain = true })
            local ok, err = ged.try(kit.ground, { area = cave, mode = "terrain" })
            out[#out + 1] = tostring(ok) .. "|" .. err.message
            ok, err = ged.try(kit.ground, { area = { min = { 54, 0, -6 }, max = { 66, 6, 6 } }, mode = "terrain" })
            out[#out + 1] = tostring(ok) .. "|" .. err.message
            out[#out + 1] = table.concat(g.inputs.d3d11_signals, ",") .. "|" .. tostring(level.terrain.count)
            return table.concat(out, "\n")
            """);
        _out.WriteLine(r.ReturnValue);
        string[] l = r.ReturnValue!.Split('\n');
        Assert.StartsWith("brush|auto: brush ground, because the level does not target Direct3D 11", l[0], StringComparison.Ordinal);
        Assert.Equal("terrain|auto: terrain, because the area is large (780 m², 26 m across), open to the sky, needs no overhang and the level targets Direct3D 11 (require_d3d11_setting)|true", l[1]);
        Assert.Equal("brush|auto: brush ground, because a cave overhangs: caves stay brush-built|false", l[2]);
        Assert.StartsWith("false|", l[3], StringComparison.Ordinal);
        Assert.Contains("kit.ground: terrain refused: a cave overhangs: caves stay brush-built.", l[3], StringComparison.Ordinal);
        Assert.StartsWith("false|", l[4], StringComparison.Ordinal);
        Assert.Contains("kit.ground: terrain refused: it is not open to the sky", l[4], StringComparison.Ordinal);
        Assert.Equal("require_d3d11_setting|1", l[5]);
    }

    [Fact]
    public void Terrain_Ground_In_An_Outdoor_Room_Is_Floor_Seals_And_Lints_Clean()
    {
        (EditorDocument doc, ScriptRunResult r) = Run(Yard + """
            local g = kit.ground{ area = yard, mode = "terrain", name = "yard_ground", seed = 2, relief = 1.5,
              paths = { { { -14, 0 }, { 0, 0 }, { 14, 4 } } }, flat = { { -15, 0, 3 } }, holes = { { 8, -8, 1.2 } } }
            local t = g.terrain
            local gp = kit.ground_at{ x = 3, z = 3 }
            local hole = t:surface_at(8, -8)
            local flat = t:surface_at(-14.5, 0)
            local sp = level.place{ kind = "respawn", name = "sp_on", pos = { 5, t:surface_at(5, 5) + 1, 5 } }
            level.player_start{ pos = { -5, t:surface_at(-5, 5) + 1, 5 }, yaw = 0 }
            ops.build()
            return table.concat({ g.choice, tostring(g.built), g.holes, gp.kind, string.format("%.3f", gp.y - t:surface_at(3, 3)),
              tostring(hole), string.format("%.4f", flat), tostring(g.sky == nil), string.format("%.2f", g.top), t.chunk_count }, "|")
            """);
        _out.WriteLine(r.ReturnValue);
        string[] p = r.ReturnValue!.Split('|');
        Assert.Equal("terrain", p[0]);
        Assert.Equal("true", p[1]);
        Assert.True(int.Parse(p[2], CultureInfo.InvariantCulture) is >= 4 and <= 16, "the 1.2 m opening cuts a handful of 0.5 m cells: " + p[2]);
        Assert.Equal("terrain", p[3]);
        Assert.Equal("0.000", p[4]);
        Assert.Equal("nil", p[5]);
        Assert.Equal("0.0625", p[6]); // level at the flat place: the lift above the brush floor, no mound
        Assert.Equal("true", p[7]); // inside the yard's air: no sky volume of its own
        Assert.True(double.Parse(p[8], CultureInfo.InvariantCulture) > 0.5, "the ground shape has relief");
        AssertSealedBoth(doc, "terrain in an outdoor room");
        AssertNoTerrainOrSpawnWarnings(doc);
    }

    [Fact]
    public void Terrain_Over_Open_Space_Gets_A_Sky_Volume_So_The_Level_Seals()
    {
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local g = kit.ground{ min = { 100, 0, -20 }, max = { 140, 0, 20 }, mode = "terrain", name = "field", relief = 2.5, seed = 4,
              outline = { { 104, -20 }, { 136, -20 }, { 140, -16 }, { 140, 16 }, { 136, 20 }, { 104, 20 }, { 100, 16 }, { 100, -16 } } }
            level.place{ kind = "respawn", name = "sp", pos = { 120, g.terrain:surface_at(120, 0) + 1, 0 } }
            level.player_start{ pos = { 115, g.terrain:surface_at(115, 0) + 1, 0 }, yaw = 0 }
            ops.build()
            return g.choice .. "|" .. tostring(g.sky ~= nil) .. "|" .. tostring(g.sky_room ~= nil) .. "|" .. g.holes .. "|" .. g.cells[1] .. "x" .. g.cells[2]
            """);
        _out.WriteLine(r.ReturnValue);
        string[] p = r.ReturnValue!.Split('|');
        Assert.Equal("terrain", p[0]);
        Assert.Equal("true", p[1]);
        Assert.Equal("true", p[2]);
        Assert.True(int.Parse(p[3], CultureInfo.InvariantCulture) > 0, "the chamfered corners are holes");
        Assert.Equal("80x80", p[4]);
        AssertSealedBoth(doc, "terrain in its sky volume");
        AssertNoTerrainOrSpawnWarnings(doc);
    }

    [Fact]
    public void Spawn_Height_Lint_And_The_Sampler_Treat_Built_Terrain_As_Floor_And_What_Is_Under_It_As_Ground()
    {
        (EditorDocument doc, ScriptRunResult r) = Run(Yard + """
            local g = kit.ground{ area = yard, mode = "terrain", name = "yard_ground", seed = 5, relief = 2 }
            local t = g.terrain
            local function top(x, z) return t:surface_at(x, z) end
            level.place{ kind = "respawn", name = "ok", pos = { -6, top(-6, -6) + 1, -6 } }
            level.player_start{ pos = { -8, top(-8, -6) + 1, -6 }, yaw = 0 }
            level.place{ kind = "respawn", name = "low", pos = { 6, top(6, 6) + 0.25, 6 } }
            -- under a mound: halfway between the yard's floor and the terrain's top at the highest point of the grid
            local hx, hz, hy = 0, 0, -1
            for x = -12, 12, 2 do for z = -10, 10, 2 do if top(x, z) > hy then hx, hz, hy = x, z, top(x, z) end end end
            level.place{ kind = "respawn", name = "under", pos = { hx, hy / 2, hz } }
            ops.build()
            local pi = spatial.point_info({ hx, hy / 2, hz })
            local above = spatial.point_info({ -6, top(-6, -6) + 1, -6 })
            return tostring(pi.in_solid) .. "|" .. tostring(pi.under_terrain) .. "|" .. tostring(above.in_solid) .. "|" .. tostring(above.under_terrain) .. "|" ..
              string.format("%.4f", hy)
            """);
        _out.WriteLine(r.ReturnValue);
        string[] p = r.ReturnValue!.Split('|');
        Assert.Equal("true|true|false|false", string.Join("|", p.Take(4)));
        Assert.True(double.Parse(p[4], CultureInfo.InvariantCulture) > 1, "the ground shape has a mound over 1 m to hide a spawn under: " + p[4]);

        List<LintFinding> spawn = LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings.Where(f => f.Category == LintCategory.SpawnHeight).ToList();
        foreach (LintFinding f in spawn)
        {
            _out.WriteLine(f.Message);
        }

        Assert.Equal(2, spawn.Count);
        Assert.Contains(spawn, f => f.Message.Contains("under the terrain's surface, inside the ground", StringComparison.Ordinal));
        Assert.Contains(spawn, f => f.Message.Contains("sits 0.25 m above the floor", StringComparison.Ordinal));

        // The sampler: the yard's brush floor under the terrain is ground, not a second floor (the valid floor is the
        // terrain's top over the yard once, plus the small room), and samples on the terrain are valid floor.
        Geometry geo = SpatialLevel.StoredGeometry(doc.Rfl)!;
        SpatialAnalysis a = SpatialAnalysis.Run(SpatialLevel.FromDocument(doc, geo));
        double area = a.WalkQuery()["valid_area"]!.GetValue<double>();
        _out.WriteLine($"valid floor {area} m2");
        const double expected = (30 * 26) + (12 * 12);
        Assert.InRange(area, expected * 0.85, expected * 1.02);
    }

    [Fact]
    public void Layout_Ground_Option_Changes_Only_Zone_Ground_And_Auto_Decides_Per_Zone()
    {
        (_, ScriptRunResult r) = Run("""
            local function text(g)
              local out = {}
              for _, z in ipairs(g.zones) do out[#out + 1] = z.id .. ":" .. z.archetype .. ":" .. tostring(z.ground) end
              return table.concat(out, ",") .. "#" .. #g.links .. "#" .. #g.objectives
            end
            local a = kit.layout{ gametype = "dm", class = "open_air", seed = 5, players = 8 }
            local b = kit.layout{ gametype = "dm", class = "open_air", seed = 5, players = 8, ground = "terrain" }
            local same = #a.zones == #b.zones and #a.links == #b.links
            for i, z in ipairs(a.zones) do
              local w = b.zones[i]
              if z.archetype ~= w.archetype or z.pos[1] ~= w.pos[1] or z.pos[2] ~= w.pos[2] or z.pos[3] ~= w.pos[3] or z.size[1] ~= w.size[1] or (z.rot or 0) ~= (w.rot or 0) then same = false end
            end
            -- A hand-made open-air graph: a large and a small isle and a deck; auto with the level on Direct3D 11.
            level.alpine:set("require_d3d11_setting", true)
            local g = { kind = "layout", gametype = "dm", class = "open_air", step = 5, zones = {
              { id = "big", archetype = "isle", pos = { 0, 0, 0 }, size = { 24, 24 }, ground = "auto", palette = { theme = "industrial", variant = 1 } },
              { id = "small", archetype = "isle", pos = { 44, 0, 0 }, size = { 18, 18 }, ground = "auto", palette = { theme = "industrial", variant = 1 } },
              { id = "deck", archetype = "deck", pos = { 0, 0, 46 }, size = { 20, 18 }, ground = "terrain", palette = { theme = "industrial", variant = 1 } },
            }, links = {
              { id = "l1", type = "bridge", a = "big", b = "small", width = 4 },
              { id = "l2", type = "bridge", a = "big", b = "deck", width = 4 },
            }, objectives = {} }
            local errs = {}
            for _, v in ipairs(kit.check_layout{ graph = g }) do if v.severity == "error" or v.rule == "ground" then errs[#errs + 1] = v.message end end
            local rep = kit.build_layout{ graph = g, name = "G", lights = false, detail = false }
            local out = { tostring(same), text(b), #errs .. ":" .. table.concat(errs, ";"), rep.ground.terrain .. "/" .. rep.ground.brush }
            for _, z in ipairs(rep.zones) do out[#out + 1] = z.id .. "=" .. z.ground.choice .. "|" .. z.ground.reason end
            -- A roofed zone asking for terrain is a design-rule violation (it keeps its brush floor).
            local h = { kind = "layout", gametype = "dm", class = "standard", step = 5, zones = {
              { id = "h", archetype = "hall", pos = { 0, 0, 200 }, size = { 24, 20 }, ground = "terrain" } }, links = {}, objectives = {} }
            for _, v in ipairs(kit.check_layout{ graph = h }) do if v.rule == "ground" then out[#out + 1] = v.severity .. "|" .. v.message end end
            return table.concat(out, "\n")
            """);
        _out.WriteLine(r.ReturnValue);
        string[] l = r.ReturnValue!.Split('\n');
        Assert.Equal("true", l[0]);
        Assert.Contains("isle:terrain", l[1], StringComparison.Ordinal);
        Assert.Contains("garden:terrain", l[1], StringComparison.Ordinal);
        Assert.DoesNotContain(":brush", l[1], StringComparison.Ordinal); // every open-air zone carries the request
        Assert.Equal("0:", l[2]);
        Assert.Equal("1/2", l[3]);
        Assert.StartsWith("big=terrain|auto: terrain, because the area is large (576 m², 24 m across)", l[4], StringComparison.Ordinal);
        Assert.StartsWith("small=brush|auto: brush ground, because the area is small (324 m², 18 m across", l[5], StringComparison.Ordinal);
        Assert.Equal("deck=brush|terrain refused: it is architecture (deck): built floors stay brush-built", l[6]);
        Assert.StartsWith("rule|zone h (hall) asks for terrain ground but is roofed", l[7], StringComparison.Ordinal);
    }

    /// <summary>
    /// The C10 gate: a seeded open-air DM layout (seed 5, 8 players: a garden and an isle) on terrain ground builds sealed
    /// under both methods, meets kit.check_layout's rules, passes the grader's flow checks, has every spawn reachable on
    /// foot and the spawns on terrain 1 m above its surface, and grades on the terrain's floor (playable area within a few
    /// percent of the brush-ground build of the same seed, not short of it and not doubled by the slab under the terrain).
    /// </summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void An_Open_Air_Layout_On_Terrain_Ground_Builds_Sealed_Graded_And_Reachable()
    {
        const int seed = 5, players = 8;
        string body(string ground) =>
            $"local g = kit.layout{{ gametype = 'dm', class = 'open_air', seed = {seed}, players = {players}, ground = '{ground}' }}\n" +
            "local v = kit.check_layout{ graph = g }\n" +
            "local rep = kit.build_layout{ graph = g, name = 'O' }\n" +
            "ops.build()\n" +
            "local sp = level.objects_of('respawn')\n" +
            "local w = spatial.walk{ from = { sp[1].pos.x, sp[1].pos.y - 1, sp[1].pos.z } }\n" +
            "local on, worst = 0, 0\n" +
            "for _, s in ipairs(sp) do\n" +
            "  for _, t in ipairs(level.terrain.all()) do\n" +
            "    local y = t:surface_at(s.pos.x, s.pos.z)\n" +
            "    if y then on = on + 1 worst = math.max(worst, math.abs(s.pos.y - y - 1)) end\n" +
            "  end\n" +
            "end\n" +
            "return #v .. '~' .. rep.spawns .. '~' .. #sp .. '~' .. #w.unreachable_spawns .. '~' .. (rep.ground and rep.ground.terrain or 0) .. '~' .. on .. '~' .. string.format('%.4f', worst) .. '~' .. level.terrain.count";

        (EditorDocument doc, ScriptRunResult r) = Run(body("terrain"));
        _out.WriteLine(r.ReturnValue);
        string[] p = r.ReturnValue!.Split('~');
        Assert.Equal("0", p[0]);
        Assert.True(int.Parse(p[2], CultureInfo.InvariantCulture) >= 4, "too few spawns: " + r.ReturnValue);
        Assert.True(p[3] == "0", "spawns not reachable on foot from the first: " + r.ReturnValue);
        Assert.Equal("2", p[4]);
        Assert.Equal("2", p[7]);
        Assert.True(int.Parse(p[5], CultureInfo.InvariantCulture) >= 1, "no spawn stands on the terrain: " + r.ReturnValue);
        Assert.True(double.Parse(p[6], CultureInfo.InvariantCulture) < 0.01, "a spawn on terrain is not 1 m above it: " + r.ReturnValue);

        (CompiledLevel shared, List<Vec3> holes) = Holes(doc, sharedBsp: true);
        Assert.True(holes.Count == 0, $"{holes.Count} hole(s) on the sealed Shared BSP build, e.g. " +
            string.Join("; ", holes.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
        (CompiledLevel _, List<Vec3> holesIncremental) = Holes(doc, sharedBsp: false);
        Assert.True(holesIncremental.Count == 0, $"{holesIncremental.Count} hole(s) on the sealed Incremental build");
        AssertNoTerrainOrSpawnWarnings(doc);

        JsonObject grade = Grade(doc, shared, "dm", "open_air", players);
        var failing = DesignGrader.FailedIds(grade).ToHashSet(StringComparer.Ordinal);
        _out.WriteLine("failing: " + string.Join(", ", failing.OrderBy(x => x, StringComparer.Ordinal)));
        foreach (string id in FlowChecks("dm"))
        {
            Assert.False(failing.Contains(id), $"flow check {id} fails on terrain ground");
        }

        double terrainArea = grade["playable_area"]!.GetValue<double>();
        (EditorDocument brushDoc, ScriptRunResult rb) = Run(body("brush"));
        Assert.Equal("0", rb.ReturnValue!.Split('~')[4]);
        JsonObject brushGrade = Grade(brushDoc, Holes(brushDoc, sharedBsp: true).Build, "dm", "open_air", players);
        double brushArea = brushGrade["playable_area"]!.GetValue<double>();
        _out.WriteLine($"playable area: terrain {terrainArea}, brush {brushArea}");
        Assert.InRange(terrainArea, brushArea * 0.95, brushArea * 1.03);
    }

    private static void AssertSealedBoth(EditorDocument doc, string what)
    {
        foreach (bool shared in new[] { true, false })
        {
            CompiledLevel c = KitTestHarness.Sealed(doc, shared);
            List<Vec3> holes = HoleDetector.Detect(c.Geometry);
            Assert.True(holes.Count == 0, $"{what}: {(shared ? "Shared BSP" : "Incremental")} sealed build has {holes.Count} hole(s), e.g. " +
                string.Join("; ", holes.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
        }
    }

    // No terrain warnings (built, in a room, inside the room's box) and no spawn-height warnings after ops.build().
    private void AssertNoTerrainOrSpawnWarnings(EditorDocument doc)
    {
        List<LintFinding> bad = LevelLinter.Lint(doc.Rfl, new LintOptions()).Findings
            .Where(f => f.Severity >= LintSeverity.Warning && (f.Category == LintCategory.SpawnHeight || f.Category == LintCategory.AlpineObject))
            .ToList();
        foreach (LintFinding f in bad)
        {
            _out.WriteLine($"{f.Category}: {f.Message}");
        }

        Assert.Empty(bad);
    }
}
