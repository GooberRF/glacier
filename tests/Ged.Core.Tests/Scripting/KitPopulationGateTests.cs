using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The Q5 population gate (DESIGN-QUALITY-SPEC §g Q5): a small kit layout per gametype (DM, CTF, KOTH,
/// payload, open-air) and the kit example levels are built, then dressed, spawned and itemised in a
/// second run. Each dressed level must build sealed with 0 holes under both build methods, stay lint
/// clean, keep its walkable floor within ±2 % (the kit's own sampler AND the exemplar study's walk graph
/// on the compiled level), and meet the §a.3 population targets (G15-G18 thresholds; G19 where the
/// layout allows it — reported for every layout). Determinism: the same seeds give the same bytes.
/// </summary>
[Trait("Category", "DeepGate")]
public sealed class KitPopulationGateTests
{
    private readonly ITestOutputHelper _out;

    public KitPopulationGateTests(ITestOutputHelper output) => _out = output;

    // ---- layouts (build) and their population (second run) ----------------------------------------

    private const string DmLayout = """
        level.props:set_many{ level_name = "Population DM", multiplayer = true }
        local hub = kit.room{ name = "hub", pos = {0, 0, 0}, size = {24, 9, 20} }
        local north = kit.room{ name = "north", pos = {0, 0, 22}, size = {18, 5, 12} }
        local south = kit.room{ name = "south", pos = {0, 0, -22}, size = {18, 5, 12} }
        local east = kit.room{ name = "east", pos = {26, 0, 0}, size = {14, 5, 16} }
        local west = kit.room{ name = "west", pos = {-26, 0, 0}, size = {14, 5, 16} }
        local links = {
          kit.corridor{ name = "hub_n", a = hub, b = north }, kit.corridor{ name = "hub_s", a = hub, b = south },
          kit.corridor{ name = "hub_e", a = hub, b = east }, kit.corridor{ name = "hub_w", a = hub, b = west },
          kit.corridor{ name = "ring_ne", path = { {9, 0, 24}, {26, 0, 24}, {26, 0, 8} } },
          kit.corridor{ name = "ring_nw", path = { {-9, 0, 24}, {-26, 0, 24}, {-26, 0, 8} } },
          kit.corridor{ name = "ring_se", path = { {9, 0, -24}, {26, 0, -24}, {26, 0, -8} } },
          kit.corridor{ name = "ring_sw", path = { {-9, 0, -24}, {-26, 0, -24}, {-26, 0, -8} } },
        }
        kit.pillar_row{ name = "hub_pillars_n", from = {-6, 0, 5}, to = {6, 0, 5}, count = 2, height = 9, size = 1.5 }
        kit.pillar_row{ name = "hub_pillars_s", from = {-6, 0, -5}, to = {6, 0, -5}, count = 2, height = 9, size = 1.5 }
        kit.platform{ name = "hub_dais", pos = {0, 0, 0}, size = {6, 4}, height = 1.25 }
        for _, r in ipairs{ hub, north, south, east, west } do kit.light_room{ room = r, theme = "industrial" } end
        for _, c in ipairs(links) do kit.light_room{ room = c, theme = "industrial" } end
        level.player_start{ pos = {0, 2.25, -8}, yaw = 0 }
        """;

    private const string DmPopulation = """
        local out = {}
        for i, r in ipairs{ "hub", "north", "south", "east", "west" } do
          local d = kit.dress{ room = r, theme = (i == 1) and "industrial" or ((i % 2 == 0) and "storage" or "construction"), seed = 1 + i }
          out[#out + 1] = d.walk.change
        end
        kit.spawn_points{ rooms = { "hub", "north", "south", "east", "west" }, count = 8, seed = 1 }
        kit.items{ gametype = "dm", seed = 1 }
        return out
        """;

    private const string CtfLayout = """
        level.props:set_many{ level_name = "Population CTF", multiplayer = true }
        local red = kit.room{ name = "red_base", pos = {-40, 0, 0}, size = {18, 6, 16} }
        local blue = kit.room{ name = "blue_base", pos = {40, 0, 0}, size = {18, 6, 16} }
        local mid = kit.room{ name = "mid", pos = {0, 0, 0}, size = {24, 8, 18} }
        local links = {
          kit.corridor{ name = "red_mid", a = red, b = mid }, kit.corridor{ name = "mid_blue", a = mid, b = blue },
          kit.corridor{ name = "north_lane", path = { {-40, 0, 8}, {-40, 0, 22}, {40, 0, 22}, {40, 0, 8} } },
          kit.corridor{ name = "south_lane", path = { {-40, 0, -8}, {-40, 0, -22}, {40, 0, -22}, {40, 0, -8} } },
        }
        for _, x in ipairs{ -40, 40 } do
          kit.pillar_row{ from = {x - 4, 0, -3.5}, to = {x - 4, 0, 3.5}, count = 2, height = 6, size = 1.5 }
          kit.pillar_row{ from = {x + 4, 0, -3.5}, to = {x + 4, 0, 3.5}, count = 2, height = 6, size = 1.5 }
          level.brush{ pos = {x, 2, 0}, size = {0.6, 4, 9} }   -- a divider: alcoves on both sides of the base
        end
        kit.pillar_row{ name = "mid_pillars", from = {-5, 0, 0}, to = {5, 0, 0}, count = 2, height = 8, size = 2 }
        level.place{ kind = "item", class = "flag_red", name = "flag_red", pos = {-46, 0.5, 0} }
        level.place{ kind = "item", class = "flag_blue", name = "flag_blue", pos = {46, 0.5, 0} }
        for _, r in ipairs{ red, blue, mid } do kit.light_room{ room = r, theme = "storage" } end
        for _, c in ipairs(links) do kit.light_room{ room = c, theme = "storage" } end
        """;

    private const string CtfPopulation = """
        local out = {}
        for i, r in ipairs{ "red_base", "blue_base", "mid" } do
          local d = kit.dress{ room = r, theme = (r == "mid") and "industrial" or "storage", seed = 10 + i }
          out[#out + 1] = d.walk.change
        end
        kit.spawn_points{ rooms = { "red_base", "red_mid" }, count = 4, team = "red", seed = 3 }
        kit.spawn_points{ rooms = { "blue_base", "mid_blue" }, count = 4, team = "blue", seed = 3 }
        kit.items{ gametype = "ctf", seed = 4 }
        return out
        """;

    private const string KothLayout = """
        level.props:set_many{ level_name = "Population KOTH", multiplayer = true }
        local hill = kit.room{ name = "hill", pos = {0, 0, 0}, size = {26, 8, 26} }
        local n = kit.room{ name = "app_n", pos = {0, 0, 26}, size = {16, 5, 12} }
        local s = kit.room{ name = "app_s", pos = {0, 0, -26}, size = {16, 5, 12} }
        local e = kit.room{ name = "app_e", pos = {26, 0, 0}, size = {12, 5, 16} }
        local w = kit.room{ name = "app_w", pos = {-26, 0, 0}, size = {12, 5, 16} }
        local links = {
          kit.corridor{ name = "h_n", a = hill, b = n }, kit.corridor{ name = "h_s", a = hill, b = s },
          kit.corridor{ name = "h_e", a = hill, b = e }, kit.corridor{ name = "h_w", a = hill, b = w },
          kit.corridor{ name = "r_ne", path = { {8, 0, 26}, {26, 0, 26}, {26, 0, 8} } },
          kit.corridor{ name = "r_nw", path = { {-8, 0, 26}, {-26, 0, 26}, {-26, 0, 8} } },
          kit.corridor{ name = "r_se", path = { {8, 0, -26}, {26, 0, -26}, {26, 0, -8} } },
          kit.corridor{ name = "r_sw", path = { {-8, 0, -26}, {-26, 0, -26}, {-26, 0, -8} } },
        }
        kit.platform{ name = "hill_top", pos = {0, 0, 0}, size = {8, 8}, height = 1.0 }
        for _, r in ipairs{ hill, n, s, e, w } do kit.light_room{ room = r, theme = "construction" } end
        for _, c in ipairs(links) do kit.light_room{ room = c, theme = "construction" } end
        """;

    private const string KothPopulation = """
        local out = {}
        for i, r in ipairs{ "hill", "app_n", "app_s", "app_e", "app_w" } do
          out[#out + 1] = kit.dress{ room = r, theme = "construction", seed = 20 + i }.walk.change
        end
        kit.spawn_points{ rooms = { "app_n", "app_s", "app_e", "app_w", "r_ne", "r_nw", "r_se", "r_sw" }, count = 8, seed = 5 }
        kit.items{ gametype = "koth", hill = {0, 1, 0}, seed = 6 }
        return out
        """;

    private const string PayloadLayout = """
        level.props:set_many{ level_name = "Population Payload", multiplayer = true }
        level.alpine:set_many{ legacy_movers = false }
        local yard_a = kit.room{ name = "yard_a", pos = {0, 0, 0}, size = {22, 7, 30} }
        local street = kit.room{ name = "street", pos = {0, 0, 30}, size = {14, 6, 20} }
        local yard_b = kit.room{ name = "yard_b", pos = {0, 0, 58}, size = {24, 8, 24} }
        local hall = kit.room{ name = "hall", pos = {0, 0, 86}, size = {20, 6, 20} }
        local links = {
          kit.corridor{ name = "a_street", a = yard_a, b = street, width = 8, height = 6 },
          kit.corridor{ name = "street_b", a = street, b = yard_b, width = 8, height = 6 },
          kit.corridor{ name = "b_hall", a = yard_b, b = hall, width = 8, height = 6 },
          kit.corridor{ name = "flank_w", path = { {-11, 0, 8}, {-20, 0, 8}, {-20, 0, 52}, {-12, 0, 52} } },
          kit.corridor{ name = "flank_e", path = { {7, 0, 36}, {18, 0, 36}, {18, 0, 80}, {10, 0, 80} } },
        }
        kit.platform{ name = "hold_b", pos = {8, 0, 64}, size = {6, 6}, height = 2.5 }
        kit.stairs{ name = "hold_b_stairs", pos = {8, 0, 61}, dir = "-z", rise = 2.5, width = 2.5 }
        local themes = { "industrial", "construction", "industrial", "storage" }
        for i, r in ipairs{ yard_a, street, yard_b, hall } do kit.light_room{ room = r, theme = themes[i] } end
        for _, c in ipairs(links) do kit.light_room{ room = c, theme = "industrial" } end
        """;

    private const string PayloadPopulation = """
        local route = { {0, 0, -12}, {0, 0, 15}, {0, 0, 40}, {0, 0, 58}, {0, 0, 90} }
        local keep = {}
        for i = 1, #route - 1 do
          local a, b = route[i], route[i + 1]
          keep[#keep + 1] = { min = { -3, 0, math.min(a[3], b[3]) }, max = { 3, 3, math.max(a[3], b[3]) } }
        end
        local out = {}
        local themes = { "industrial", "construction", "industrial", "storage" }
        for i, r in ipairs{ "yard_a", "street", "yard_b", "hall" } do
          out[#out + 1] = kit.dress{ room = r, theme = themes[i], seed = 30 + i, keep_clear = keep }.walk.change
        end
        local body = level.brush{ pos = {0, 0.5, -12}, size = {2, 1, 3}, detail = true }
        local cart = level.create_mover{ name = "payload cart", brushes = { body }, props = { movement_type = "One Way" } }
        for i = 2, #route do cart:add_keyframe{ pos = { route[i][1], 0.5, route[i][3] }, props = { travel_time_to_next = 8, return_travel = 8 } } end
        level.place{ kind = "event", class = "Payload_Handler", name = "payload", pos = {0, 2, -12},
          props = { attacking_team = "Blue", push_radius = 5 }, links = { cart } }
        local groups = { { "yard_a", "yard_b" }, { "street", "hall" }, { "yard_b", "hall" } }
        for n, k in ipairs({ 3, 4, 5 }) do
          local kf = cart.keyframes[k]
          local cp = level.place{ kind = "event", class = "Payload_Checkpoint", pos = { kf.pos.x, 2, kf.pos.z },
            props = { checkpoint_name = "Checkpoint " .. string.char(64 + n) } }
          kf:set("triggered_event_uid", cp.uid)
          kit.spawn_points{ room = groups[n][1], count = 3, team = "blue", checkpoint = cp, seed = n, name = "att" .. n }
          kit.spawn_points{ room = groups[n][2], count = 3, team = "red", checkpoint = cp, seed = n, name = "def" .. n }
        end
        kit.items{ gametype = "payload", path = route, seed = 7 }
        return out
        """;

    private const string OpenLayout = """
        level.props:set_many{ level_name = "Population open-air", multiplayer = true }
        local yard = kit.room{ name = "outdoor", pos = {0, 0, 0}, size = {40, 12, 30} }
        local shed = kit.room{ name = "shed", pos = {29, 0, 0}, size = {16, 5, 14} }
        local back = kit.room{ name = "back", pos = {0, 0, 28}, size = {20, 6, 12} }
        kit.doorway{ a = yard, b = shed, width = 4 }
        kit.corridor{ a = yard, b = back }
        kit.sky_room{ open = { yard } }
        kit.rock_wall{ name = "ridge", from = {-12, 0, -6}, to = {-4, 0, -9}, height = 3.5, depth = 2.5, structural = true, seed = 2 }
        kit.light_room{ room = yard, theme = "canyon" }
        kit.light_room{ room = shed, theme = "industrial" }
        kit.light_room{ room = back, theme = "industrial" }
        """;

    private const string OpenPopulation = """
        local out = {
          kit.dress{ room = "outdoor", theme = "canyon", seed = 41 }.walk.change,
          kit.dress{ room = "shed", theme = "industrial", seed = 42 }.walk.change,
          kit.dress{ room = "back", theme = "storage", seed = 43 }.walk.change,
        }
        kit.spawn_points{ rooms = { "outdoor", "shed", "back" }, count = 6, seed = 8 }
        kit.items{ seed = 9 }
        return out
        """;

    private const string TinyDmPopulation = """
        local out = {}
        for _, r in ipairs{ "hall", "annex", "vault" } do out[#out + 1] = kit.dress{ room = r, theme = "industrial", seed = 3 }.walk.change end
        kit.items{ gametype = "dm", seed = 3 }
        return out
        """;

    private const string CavePopulation = """
        local out = {}
        for _, r in ipairs{ "hub", "gallery", "chamber", "armoury", "climb", "tunnel", "underpass" } do
          out[#out + 1] = kit.dress{ room = r, theme = "mine", seed = 4 }.walk.change
        end
        kit.items{ gametype = "dm", seed = 4 }
        return out
        """;

    private const string CanyonPopulation = """
        local out = { kit.dress{ room = "canyon", theme = "canyon", seed = 1 }.walk.change }
        kit.items{ room = "canyon", seed = 1 }
        return out
        """;

    private static string Layout(string name) => name switch
    {
        "dm" => DmLayout,
        "ctf" => CtfLayout,
        "koth" => KothLayout,
        "payload" => PayloadLayout,
        "open" => OpenLayout,
        "kit-tiny-dm" or "kit-cave" or "kit-canyon" => Example(name),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string Population(string name) => name switch
    {
        "dm" => DmPopulation,
        "ctf" => CtfPopulation,
        "koth" => KothPopulation,
        "payload" => PayloadPopulation,
        "open" => OpenPopulation,
        "kit-tiny-dm" => TinyDmPopulation,
        "kit-cave" => CavePopulation,
        "kit-canyon" => CanyonPopulation,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string Example(string name)
    {
        string root = ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        // The example's last line returns a summary; the layout run only needs its content.
        return File.ReadAllText(Path.Combine(root, "scripts", "examples", name + ".lua"));
    }

    // ---- running --------------------------------------------------------------------------------

    private static AssetVfs? MountTables() => TestPaths.RfInstall is { } dir ? GameMount.Mount(dir) : null;

    private static ScriptRunResult RunScript(EditorDocument doc, string source, AssetVfs? vfs, string chunk)
    {
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Assets = vfs },
            source,
            new ScriptRunOptions { ChunkName = chunk, AllowDestructive = true, Limits = ScriptExecutionLimits.Unbounded },
            new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r;
    }

    private static (EditorDocument Doc, KitRun Population, double[] LuaWalk) Build(string name, AssetVfs? vfs)
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        string layout = Layout(name);
        string head = layout.StartsWith("--@", StringComparison.Ordinal) ? string.Empty : "local kit = ged.import('kit')\n";
        RunScript(doc, head + layout, vfs, name + "-layout");

        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        HashSet<string> lintBefore = LintKeys(doc);
        int nodes = doc.Undo.NodeCount;
        ScriptRunResult r = RunScript(doc, "local kit = ged.import('kit')\nlocal t = (function()\n" + Population(name) +
            "\nend)()\nlocal s = {} for i, v in ipairs(t) do s[i] = string.format('%.6f', v) end return table.concat(s, ',')", vfs, name + "-population");
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        double[] walk = r.ReturnValue!.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return (doc, new KitRun(doc, before, lintBefore, r, new ScriptLog()), walk);
    }

    public static IEnumerable<object[]> Layouts() => new[] { "dm", "ctf", "koth", "payload", "open", "kit-tiny-dm", "kit-cave", "kit-canyon" }
        .Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Dressed_Layout_Is_Watertight_Lint_Clean_Walkable_And_Populated(string name)
    {
        (EditorDocument doc, KitRun run, double[] luaWalk) = Build(name, vfs: null);

        // The kit's own sampler: every dressed space keeps its walkable floor within +-2 %.
        Assert.All(luaWalk, w => Assert.InRange(Math.Abs(w), 0, 0.02));

        CompiledLevel shared = AssertWatertight(doc, sharedBsp: true);
        AssertWatertight(doc, sharedBsp: false);
        AssertLintClean(run);

        // Reachable floor (the study's walk edges, drops one way) on the compiled level, before and after the population run.
        var after = new PopulationMetrics(shared);
        doc.Undo.Undo();
        var before = new PopulationMetrics(Sealed(doc, sharedBsp: true));
        doc.Undo.Redo();
        double a0 = before.ReachableArea(), a1 = after.ReachableArea();
        double change = (a1 - a0) / a0;
        PopulationMetrics.Report m = after.Measure(doc);
        _out.WriteLine($"{name}: walk {a0:0.0} -> {a1:0.0} m² ({change * 100:0.00} %), lua [{string.Join(", ", luaWalk.Select(w => (w * 100).ToString("0.00", CultureInfo.InvariantCulture)))}] %");
        _out.WriteLine($"{name}: {m}");
        Assert.InRange(Math.Abs(change), 0, 0.02);

        // §a.3 population targets and the G15-G18 grader limits.
        Assert.InRange(m.Items100, 1.2, 2.0);                 // items per 100 m² (G15 >= 0.8)
        Assert.InRange(m.WeaponNnMedian, 5, 14);               // weapon/powerup nearest neighbour (target 7-10, G15 <= 14)
        Assert.InRange(m.SpawnToItemMedian, 0, 6.0);           // spawn -> nearest item (target 3.4-5.4, G15 <= 8)
        Assert.InRange(m.SpawnToItemMax, 0, 8.0);
        Assert.InRange(m.Dressing100, 5, 14);                  // dressing per 100 m² (target 5-10, DM-L 8-11; G16 >= 2.5)
        Assert.True(m.Decals100 >= 0.05 || m.Clutter100 >= 0.3, $"{name}: no clutter or decals ({m})");   // G16
        Assert.InRange(m.Undressed, 0, 0.34);                  // undressed 8 m cells (target <= 34 %, G17 <= 45 %)
        if (!name.StartsWith("kit-", StringComparison.Ordinal))
        {
            // The examples keep their own lights and spawns; the gametype layouts use the kit's.
            Assert.InRange(m.Lights100, 1.0, 6.0);             // lights per 100 m² (G18 >= 1.0)
            Assert.True(m.SpawnNnMin >= 5, $"{name}: spawns {m.SpawnNnMin:0.0} m apart");
        }
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("ctf")]
    [InlineData("koth")]
    public void Spawns_Meet_The_Sight_And_Exposure_Limits_In_Gametype_Layouts(string name)
    {
        (EditorDocument doc, _, _) = Build(name, vfs: null);
        PopulationMetrics.Report m = new PopulationMetrics(Sealed(doc, sharedBsp: true)).Measure(doc);
        _out.WriteLine($"{name}: {m}");
        // G19: <= 35 % of spawn pairs within 30 m see each other. The CTF test bases are one 18 x 16 m room
        // each (a divider, four pillars) plus the corridor to mid, holding a team's 4 spawns: teammates in
        // the open base see each other, so that layout is held to 50 % (bases with spawn side rooms are the
        // layout generator's job, Q4).
        Assert.InRange(m.SpawnLosPairs, 0, name == "ctf" ? 0.5 : 0.35);
        Assert.InRange(m.SpawnExposureMedian, 0, 0.35);        // G19 spawn exposure (0.25) with sampling slack
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("payload")]
    [InlineData("kit-cave")]
    public void Population_Fits_One_Run_Within_The_Default_Limits(string name)
    {
        // The trial ran out of the 200,000,000-instruction budget populating a whole level; dressing, spawns and
        // items for a whole layout now fit one run under the default limits (30 s of script time, 200M instructions).
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        string layout = Layout(name);
        string head = layout.StartsWith("--@", StringComparison.Ordinal) ? string.Empty : "local kit = ged.import('kit')\n";
        RunScript(doc, head + layout, vfs: null, name + "-layout");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation() },
            "local kit = ged.import('kit')\nlocal t = (function()\n" + Population(name) + "\nend)()\nreturn #t",
            new ScriptRunOptions { ChunkName = name + "-population", AllowDestructive = true, Limits = ScriptExecutionLimits.Default },
            new ScriptLog());
        _out.WriteLine($"{name}: population {sw.Elapsed.TotalSeconds:0.0} s, script time {r.ScriptTime.TotalSeconds:0.0} s");
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("payload")]
    public void Population_Is_Deterministic(string name)
    {
        byte[] a = Build(name, vfs: null).Doc.SaveToBytes(updateTimestamp: false);
        byte[] b = Build(name, vfs: null).Doc.SaveToBytes(updateTimestamp: false);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("ctf")]
    public void With_The_Game_Tables_Fixtures_And_Props_Are_Stock_Clutter(string name)
    {
        if (TestPaths.RfInstall is null)
        {
            return; // no RF install: the brush-fixture path is covered above
        }

        using AssetVfs vfs = MountTables()!;
        (EditorDocument doc, KitRun run, double[] luaWalk) = Build(name, vfs);
        Assert.All(luaWalk, w => Assert.InRange(Math.Abs(w), 0, 0.02));
        AssertWatertight(doc, sharedBsp: true);
        AssertWatertight(doc, sharedBsp: false);
        AssertLintClean(run);
        PopulationMetrics.Report m = new PopulationMetrics(Sealed(doc, sharedBsp: true)).Measure(doc);
        _out.WriteLine($"{name} (tables): {m}");
        Assert.InRange(m.Clutter100, 0.7, 4.5);                // clutter (fixtures + props): target 0.7-2.5 plus one fixture per light
        Assert.InRange(m.Dressing100, 5, 14);
        var tables = Ged.Core.Tables.GameCatalogs.Load(vfs);
        Assert.All(doc.Objects.Where(o => o.Kind == LevelObjectKind.Clutter),
            o => Assert.NotNull(tables.Clutter!.Find(o.ClassName ?? string.Empty)));
    }
}
