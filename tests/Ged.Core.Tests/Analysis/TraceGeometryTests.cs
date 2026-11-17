using System;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// kit.trace geometry: shared walls that are nearly (not exactly) collinear weld into a watertight build or refuse
/// with the reason (never a silent leak), ramps and stairs inside a room, stacked storeys, decks over a sky yard,
/// and path regions (a centre line plus a width, sloped along it).
/// </summary>
public sealed class TraceGeometryTests
{
    // The calibration RB3 used on dm02 (15.11 px per metre, origin pixel 653, 695): picture pixels to world metres.
    private const string Dm02Px = """
        local function W(x, y) return { (x - 653) / 15.11, -(y - 695) / 15.11 } end
        local function poly(list) local out = {} for i, q in ipairs(list) do out[i] = W(q[1], q[2]) end return out end
        """;

    private readonly ITestOutputHelper _out;

    public TraceGeometryTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Near_Collinear_Shared_Edge_Welds_Watertight()
    {
        // RB3's dm02 seam: the pink room's edge (808,682)-(872,683) against the hall's (812,682)-(379,682), a 1 px
        // slope over 64 px; it built with 4 open edges before the edge weld.
        string script = Dm02Px + "\n" + """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "seam", regions = {
              { name = "hallN", poly = poly{{379, 435}, {869, 435}, {869, 682}, {379, 682}}, floor = 0, height = 9 },
              { name = "hallS", poly = poly{{379, 682}, {808, 682}, {808, 896}, {379, 896}}, floor = 0, height = 9 },
              { name = "pink", poly = poly{{808, 682}, {872, 683}, {932, 683}, {932, 896}, {808, 896}}, floor = 0, height = 9 } },
              check = true }
            assert(tr.inserted >= 1, "a T-vertex is inserted into the shared wall")
            local h = ops.holes()
            return string.format("%d %d %d %d", h.count, tr.holes, tr.inserted, tr.snapped)
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.Equal("0", v[1]);
    }

    [Fact]
    public void A_Thin_Wedge_Between_Regions_Refuses_With_The_Place()
    {
        // Two rooms whose facing walls diverge from a shared corner at a shallow angle: a solid wedge thinner than
        // a wall that no weld can close.
        string script = """
            local kit = ged.import("kit")
            kit.trace{ regions = {
              { name = "a", poly = {{0, 0}, {10, 0}, {10, 10}, {0, 10}}, floor = 0 },
              { name = "b", poly = {{0, 0}, {0, -10}, {10, -10}, {10, -0.6}}, floor = 0 } } }
            """;
        string err = RunFails(script);
        Assert.Contains("wedge", err, StringComparison.Ordinal);
        Assert.Contains("'a'", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Ramp_Inside_A_Room_Builds_Without_Tiling()
    {
        string script = """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "rr", regions = {
              { name = "hall", poly = {{0, 0}, {20, 0}, {20, 12}, {0, 12}}, floor = 0, ceiling = 8 },
              { name = "upper", kind = "deck", poly = {{14, 0}, {20, 0}, {20, 12}, {14, 12}}, floor = 3 },
              { name = "ramp", kind = "ramp", poly = {{6, 0}, {14, 0}, {14, 3}, {6, 3}}, floor = {0, 3}, climb = {1, 0} } },
              check = true }
            ops.build()
            assert(tr.regions.ramp.hosts[1] == "hall", "the ramp records its host")
            assert(tr.regions.ramp.ceiling == 8, "the ramp takes the host's ceiling")
            local mid = spatial.raycast{ from = {10, 6, 1.5}, dir = {0, -1, 0} }
            local deck = spatial.raycast{ from = {17, 6, 6}, dir = {0, -1, 0} }
            local floor = spatial.raycast{ from = {4, 6, 6}, dir = {0, -1, 0} }
            return string.format("%d %.2f %.2f %.2f", tr.holes, mid.pos.y, deck.pos.y, floor.pos.y)
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.Equal(1.5, Num(v[1]), 1); // half way up the ramp
        Assert.Equal(3.0, Num(v[2]), 1);
        Assert.Equal(0.0, Num(v[3]), 1);
    }

    [Fact]
    public void Stacked_Storeys_Build_With_A_Slab_And_Refuse_Shared_Volume()
    {
        string script = """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "st", storeys = { { name = "ground", floor = 0, ceiling = 4 }, { name = "first", floor = 4.5, height = 3.5 } },
              regions = {
                { name = "low", storey = "ground", poly = {{0, 0}, {16, 0}, {16, 10}, {0, 10}} },
                { name = "high", storey = "first", poly = {{4, 0}, {16, 0}, {16, 10}, {4, 10}} },
                { name = "stairs", kind = "stairs", poly = {{0, 0}, {4, 0}, {4, 6}, {0, 6}}, floor = {0, 4.5}, climb = {0, 1}, ceiling = 8 },
                { name = "yard", kind = "yard", poly = {{16, 0}, {30, 0}, {30, 10}, {16, 10}}, floor = 0 },
                { name = "bridge", kind = "deck", poly = {{20, 0}, {24, 0}, {24, 10}, {20, 10}}, floor = 4.5 } },
              check = true }
            ops.build()
            assert(#tr.stacked >= 1, "low and high are stacked")
            local up = spatial.raycast{ from = {10, 7, 5}, dir = {0, -1, 0} }
            local down = spatial.raycast{ from = {10, 3, 5}, dir = {0, -1, 0} }
            local bridge = spatial.raycast{ from = {22, 8, 5}, dir = {0, -1, 0} }
            local info = spatial.point_info({22, 6, 5})
            return string.format("%d %.2f %.2f %.2f %s", tr.holes, up.pos.y, down.pos.y, bridge.pos.y, tostring(info.open_sky))
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.Equal(4.5, Num(v[1]), 1);
        Assert.Equal(0.0, Num(v[2]), 1);
        Assert.Equal(4.5, Num(v[3]), 1);
        Assert.Equal("true", v[4]);

        string clash = """
            local kit = ged.import("kit")
            kit.trace{ regions = {
              { name = "low", poly = {{0, 0}, {16, 0}, {16, 10}, {0, 10}}, floor = 0, ceiling = 4 },
              { name = "high", poly = {{4, 0}, {16, 0}, {16, 10}, {4, 10}}, floor = 3, height = 3 } } }
            """;
        Assert.Contains("share", RunFails(clash), StringComparison.Ordinal);
    }

    [Fact]
    public void Path_Region_Builds_A_Curved_Sloped_Corridor()
    {
        string script = """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "pp", regions = {
                { name = "a", poly = {{-10, -4}, {0, -4}, {0, 4}, {-10, 4}}, floor = 0 },
                { name = "tube", kind = "path", path = {{0, 0}, {10, 0}, {17, 7}, {17, 17}}, width = 4, floors = {0, 0, 3, 3}, height = 3.5 },
                { name = "b", poly = {{15, 17}, {19, 17}, {19, 25}, {15, 25}}, floor = 3 } },
              check = true }
            ops.build()
            assert(tr.regions["tube.2"] ~= nil, "a path expands into segment regions")
            local mid = spatial.raycast{ from = {13.5, 6, 3.5}, dir = {0, -1, 0} }
            local top = spatial.raycast{ from = {17, 5, 13}, dir = {0, -1, 0} }
            return string.format("%d %.2f %.2f", tr.holes, mid.pos.y, top.pos.y)
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.InRange(Num(v[1]), 1.0, 2.0);
        Assert.Equal(3.0, Num(v[2]), 1);

        string sharp = """
            local kit = ged.import("kit")
            kit.trace{ regions = { { name = "t", kind = "path", path = {{0, 0}, {10, 0}, {1, 1}}, width = 3, floor = 0 } } }
            """;
        Assert.Contains("too sharp", RunFails(sharp), StringComparison.Ordinal);
    }

    [Fact]
    public void Traced_Regions_Are_Kit_Zones_With_Materials_And_Dressing_Data()
    {
        // A hall (concrete) with a grate deck along its east wall, a metal corridor through a doorway in a wall gap,
        // and a path: every region is a zone the population tools take, textured by its material.
        string script = """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "zz", material = "concrete", regions = {
                { name = "hall", poly = {{0, 0}, {20, 0}, {20, 14}, {0, 14}}, floor = 0, ceiling = 8 },
                { name = "gallery", kind = "deck", material = "grate", poly = {{15, 0}, {20, 0}, {20, 14}, {15, 14}}, floor = 3 },
                { name = "side", kind = "corridor", material = "metal", poly = {{20.5, 4}, {32, 4}, {32, 8}, {20.5, 8}}, floor = 3 },
                { name = "tube", kind = "path", path = {{-0.5, 7}, {-12, 7}, {-18, 13}}, width = 3, floor = 0 } },
              openings = { { a = "gallery", b = "side", at = {20.25, 6}, width = 2, height = 2.5 } } }
            -- zones: one per region, a path as one zone; the brushes answer to the zone's name
            assert(#tr.zones == 4, "4 zones, got " .. #tr.zones)
            local hall = tr.zone.hall
            assert(hall.min.x == 0 and hall.max.z == 14 and hall.floor_y == 0 and hall.max.y == 8, "hall box")
            assert(tr.zone.tube.kind == "corridor" and #tr.zone.tube.regions == 2, "the path is one corridor zone")
            -- (the hall's air answers to its name; so do the trims, frames and beams the layout builder's detail pass adds)
            local hall_air = 0
            for _, b in ipairs(level.brushes_tagged("kit.zone", "hall").brushes) do if b.air then hall_air = hall_air + 1 end end
            assert(hall_air == #tr.regions.hall.air, "hall air tagged")
            assert(#tr.regions.gallery.fills >= 1 and tr.zone.gallery.storey == nil, "the deck slab is a fill")
            -- materials on the faces
            local pal = kit.palette{ theme = "bunker" }
            local function has(list, tex)
              for _, b in ipairs(list) do for _, f in ipairs(b.faces) do if f.texture:lower() == tex then return true end end end
              return false
            end
            assert(has(tr.regions.hall.air, pal.textures.floor), "hall floor is the concrete family")
            assert(has(tr.regions.gallery.fills, "mtl_dark_grating01.tga"), "gallery is grating")
            assert(tr.regions.side.material == "metal" and tr.regions.hall.material == "concrete", "materials reported")
            -- dressing data: the doorway, the deck's railing edge, the walls
            local o = tr.openings[1]
            assert(o.pos and math.abs(o.pos[1] - 20.25) < 0.3 and o.pos[2] == 3 and o.height == 2.5 and o.width == 2, "opening pos/height")
            assert(o.dir[1] > 0.9, "opening faces from the gallery into side")
            -- the doorway is also listed in connections (kind "opening"), with its place, width and regions
            local oc = nil
            for _, c in ipairs(tr.connections) do if c.kind == "opening" then oc = c end end
            assert(oc and oc.a == "gallery" and oc.b == "side" and oc.width == 2 and oc.opening == 1 and math.abs(oc.at[1] - 20.25) < 0.3, "opening in connections")
            local rail = nil
            for _, e in ipairs(tr.edges) do if e.kind == "deck" and e.region == "gallery" then rail = e end end
            assert(rail and rail.below == "hall" and math.abs(rail.drop - 3) < 1e-6 and math.abs(rail.a[1] - 15) < 1e-6, "the gallery's open edge needs a railing")
            assert(#tr.regions.hall.walls == 4 and #tr.regions.gallery.walls == 0 and #tr.regions["tube.1"].walls >= 2, "walls: the hall's four, none for a hosted deck: " .. #tr.regions.hall.walls .. " " .. #tr.regions.gallery.walls .. " " .. #tr.regions["tube.1"].walls .. " " .. #tr.regions.hall.points)
            -- the population tools take a traced zone: its record, or its name
            local lights = kit.light_room{ room = tr.zone.hall }
            local d = kit.detail{ room = "hall", level = "light", name = "hall_detail" }
            local dr = kit.dress{ room = "side", seed = 2, decals = false, clutter = false }
            return string.format("%d %d %d %d", #lights, #tr.edges, #d.placed, dr.count)
            """;
        string[] v = Run(script).Split(' ');
        Assert.True(int.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture) >= 2, "lights " + v[0]);
        Assert.True(int.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture) >= 1);
    }

    private static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private string Run(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("trace-geo"), new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue!;
    }

    private string RunFails(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("trace-geo-refuse"), new ScriptLog());
        Assert.False(r.Success, "expected a refusal; got " + r.ReturnValue);
        string e = r.Error!.ToDisplayString();
        _out.WriteLine(e);
        return e;
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
        Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(120), InstructionBudget = 2_000_000_000 },
    };
}
