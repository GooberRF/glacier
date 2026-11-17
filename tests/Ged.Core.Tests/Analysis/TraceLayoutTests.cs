using System;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// kit.trace through the layout builder (via = "layout", the default): a traced plan is a normal build that follows a
/// set design. Its regions become literal zones of a kit.layout graph that kit.check_layout passes, its doorway gets a
/// kit door frame from the room detail pass, its stairs and ramp are kit.stairs / kit.ramp with their route purpose,
/// its decks get railings where they drop away, and it stays watertight.
/// </summary>
public sealed class TraceLayoutTests
{
    // Two rooms with a wall gap and a doorway through it, a raised room beyond, kit stairs up to it inside the second
    // room, and a ramp up to a gallery deck inside the first.
    private const string Plan = """
        local kit = ged.import("kit")
        local tr = kit.trace{ name = "t3", material = "concrete", check = true, regions = {
            { name = "a", poly = {{0, 0}, {16, 0}, {16, 12}, {0, 12}}, floor = 0, ceiling = 8 },
            { name = "b", poly = {{16.5, 0}, {30, 0}, {30, 12}, {16.5, 12}}, floor = 0, ceiling = 7 },
            { name = "up", poly = {{30, 0}, {44, 0}, {44, 12}, {30, 12}}, floor = 3, ceiling = 8 },
            { name = "st", kind = "stairs", poly = {{24, 4}, {30, 4}, {30, 8}, {24, 8}}, floor = {0, 3}, climb = {1, 0} },
            { name = "gal", kind = "deck", poly = {{10, 0}, {16, 0}, {16, 12}, {10, 12}}, floor = 4 },
            { name = "rp", kind = "ramp", poly = {{2, 0}, {10, 0}, {10, 3}, {2, 3}}, floor = {0, 4}, climb = {1, 0} } },
          openings = { { a = "a", b = "b", at = {16.25, 6}, width = 3, height = 3 } } }
        """;

    private readonly ITestOutputHelper _out;

    public TraceLayoutTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Traced_Doorway_Gets_A_Kit_Frame_And_Stairs_Are_Kit_Stairs()
    {
        string script = Plan + "\n" + """
            assert(tr.layout ~= nil and tr.graph ~= nil, "built through the layout builder")
            assert(tr.layout_check.ok, "kit.check_layout passes: " .. table.concat(tr.layout_check.errors, " "))
            assert(#tr.graph.zones == 3, "three zones (the stairs, ramp and deck belong to their hosts), got " .. #tr.graph.zones)
            local door = nil
            for _, l in ipairs(tr.layout.links) do if l.type == "doorway" then door = l end end
            assert(door and door.mode == "literal" and door.a == "a" and door.b == "b", "the doorway is a literal link at its traced place")
            -- the stairs: kit.stairs steps, a route with its purpose
            local steps, purpose = 0, nil
            for _, b in ipairs(tr.regions.st.fills) do
              local tg = b:tags()
              if tg.kit == "stairs" and tg["kit.role"] == "route" then steps = steps + 1 purpose = purpose or tg["kit.purpose"] end
            end
            assert(steps >= 12 and purpose ~= nil and purpose:find("flight", 1, true), "kit stairs with a purpose: " .. steps .. " " .. tostring(purpose))
            local ramp = tr.regions.rp.fills[1]:tags()
            assert(ramp.kit == "ramp" and ramp["kit.role"] == "route", "the ramp is kit.ramp")
            local d = tr.detail.by_part
            assert((d.frames or 0) >= 1, "the doorway is framed")
            assert((d.stairs or 0) >= 1, "the stairs get stringers / handrails")
            assert((d.railings or 0) >= 1, "the gallery deck gets a railing where it drops away")
            assert((d.walls or 0) >= 1, "wall trims")
            -- the footprint stays literal: the stairs climb to 3 m over x 24..30, the ramp to 4 m over x 2..10
            ops.build()
            local mid = spatial.raycast{ from = {6, 7, 1.5}, dir = {0, -1, 0} }
            local top = spatial.raycast{ from = {29.8, 6, 6}, dir = {0, -1, 0} }
            return string.format("%d %d %.2f %.2f %d", tr.holes, #tr.layout_check.errors, mid.pos.y, top.pos.y, tr.lights)
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.Equal("0", v[1]);
        Assert.Equal(2.0, Num(v[2]), 1); // half way up the ramp
        Assert.InRange(Num(v[3]), 2.7, 3.01);
        Assert.True(int.Parse(v[4], System.Globalization.CultureInfo.InvariantCulture) >= 3, "lights " + v[4]);
    }

    [Fact]
    public void Validate_Only_Runs_The_Layout_Check_And_Prism_Keeps_The_Plan_Alone()
    {
        string script = Plan.Replace("check = true,", "build = false,", StringComparison.Ordinal) + "\n" + """
            assert(tr.layout_check.ok and #tr.layout_check.errors == 0, "the traced plan passes kit.check_layout")
            assert(#level.brushes_tagged("kit.name", "t3").brushes == 0, "validate-only builds nothing")
            -- the graph in the record is plain data kit.check_layout takes again
            local V = kit.check_layout{ graph = tr.graph }
            for _, x in ipairs(V) do assert(x.severity ~= "error", x.message) end
            local p = kit.trace{ name = "pr", via = "prism", check = true, regions = {
                { name = "a", poly = {{0, 0}, {16, 0}, {16, 12}, {0, 12}}, floor = 0, ceiling = 8 },
                { name = "st", kind = "stairs", poly = {{8, 4}, {14, 4}, {14, 8}, {8, 8}}, floor = {0, 3}, climb = {1, 0} } } }
            assert(p.layout == nil and p.graph == nil, "the prism path builds no layout")
            return string.format("%d %d", p.holes, #V)
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
    }

    private static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private string Run(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("trace-layout"), new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue!;
    }

    private static ScriptServices Services(EditorDocument doc) => new()
    {
        Document = doc,
        Confirmation = new AllowAllConfirmation(),
        FileReadAccess = (p, _) => p,
        FileWriteAccess = (p, _) => p,
    };

    private static ScriptRunOptions Options(string chunk) => new()
    {
        ChunkName = chunk,
        AllowDestructive = true,
        Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(180), InstructionBudget = 2_000_000_000 },
    };
}
