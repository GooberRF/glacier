using System;
using System.Globalization;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// kit.trace's organic options (canyon and cave kinds, organic paths, rock edges, organic ground, boulders) and
/// kit.rock_mass: the traced air still seals (0 holes), the rock is detail and keeps the floor footprint within the
/// traced outline (plan IoU over 0.95), doorways stay clear, and every rock piece carries its kit.role intent tag.
/// </summary>
public sealed class TraceOrganicTests
{
    private readonly ITestOutputHelper _out;

    public TraceOrganicTests(ITestOutputHelper output) => _out = output;

    // Walkable floor sampled every 0.5 m over a region's box (the floor is under the sample, no rock above it within
    // 1.5 m, no back face of a rock within 0.6 m sideways) against the traced polygon: intersection over union.
    private const string Iou = """
        local function inside(p, x, z)
          local c, j = false, #p
          for i = 1, #p do
            local a, b = p[i], p[j]
            if ((a[2] > z) ~= (b[2] > z)) and (x < (b[1] - a[1]) * (z - a[2]) / (b[2] - a[2]) + a[1]) then c = not c end
            j = i
          end
          return c
        end
        local function iou(r)
          local p = r.points
          local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
          for _, q in ipairs(p) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
          local inter, uni = 0, 0
          for x = x0 - 1, x1 + 1, 0.5 do
            for z = z0 - 1, z1 + 1, 0.5 do
              local inP = inside(p, x, z)
              local from = { x, r.floor + 0.3, z }
              local h = spatial.raycast{ from = from, dir = { 0, -1, 0 }, max = 1 }
              local walk = h.hit and not h.back_face and math.abs(h.pos.y - r.floor) < 0.2
              if walk then
                local up = spatial.raycast{ from = from, dir = { 0, 1, 0 }, max = 1.5 }
                if up.hit and up.back_face then walk = false end
                for _, d in ipairs({ { 1, 0, 0 }, { -1, 0, 0 }, { 0, 0, 1 }, { 0, 0, -1 } }) do
                  local s = spatial.raycast{ from = from, dir = d, max = 0.6 }
                  if s.hit and s.back_face then walk = false end
                end
              end
              if inP and walk then inter = inter + 1 end
              if inP or walk then uni = uni + 1 end
            end
          end
          return inter / math.max(1, uni)
        end
        """;

    [Fact]
    public void Canyon_Cave_And_Organic_Tunnel_Seal_And_Keep_Their_Footprint()
    {
        string script = Iou + "\n" + """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "org", seed = 3, regions = {
              { name = "canyon", kind = "canyon", floor = 0, height = 12, overhang = 1,
                poly = {{-14, -12}, {6, -16}, {16, -6}, {14, 10}, {2, 14}, {-12, 8}} },
              { name = "cave", kind = "cave", floor = 0, height = 6, poly = {{30, -10}, {48, -12}, {52, 4}, {40, 12}, {28, 6}} },
              { name = "tun", kind = "path", organic = true, width = 4, floor = 0, height = 4, path = {{17, 0}, {22, 1}, {25, -3}, {27.5, -2}} } },
              openings = { { a = "canyon", b = "tun", at = {16, 0}, width = 3.5 }, { a = "tun", b = "cave", at = {28.4, -2}, width = 3.5 } },
              check = true }
            ops.build()
            local c, v = tr.regions.canyon.rock, tr.regions.cave.rock
            assert(c.skin > 20 and c.lean > 0, "the canyon has a rock skin and leans back")
            assert(v.skin > 20 and v.vault > 0, "the cave has a rock skin and a vault")
            assert(tr.regions["tun.1"].rock.skin > 0, "the tunnel is rock")
            assert(c.foot <= 0.35 + 1e-9, "the foot keeps the outline")
            -- every rock piece is detail and carries its intent
            local n, bad = 0, 0
            level.brushes_tagged("kit.name", tr.rock_name):each(function(b)
              n = n + 1
              if not b.detail or b:tags()["kit.role"] == nil or b:tags()["kit.zone"] == nil then bad = bad + 1 end
            end)
            assert(n > 0 and bad == 0, bad .. " of " .. n .. " rock pieces lack detail, kit.role or kit.zone")
            return string.format("%d %.3f %.3f", tr.holes, iou(tr.regions.canyon), iou(tr.regions.cave))
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.True(double.Parse(v[1], CultureInfo.InvariantCulture) > 0.95, "canyon IoU " + v[1]);
        Assert.True(double.Parse(v[2], CultureInfo.InvariantCulture) > 0.95, "cave IoU " + v[2]);
    }

    [Fact]
    public void Rock_Edge_Only_Dresses_That_Edge_And_Keeps_The_Doorway_Clear()
    {
        string script = """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "re", regions = {
              { name = "yard", kind = "yard", floor = 0, height = 8, poly = {{0, 0}, {20, 0}, {20, 16}, {0, 16}}, rock_edges = {3},
                ground = "organic", boulders = 6 },
              { name = "hall", poly = {{0, 18}, {20, 18}, {20, 26}, {0, 26}}, floor = 0, height = 4 } },
              openings = { { a = "yard", b = "hall", at = {10, 17}, width = 3 } },
              keep_clear = { { {2, 8}, {18, 8} } },
              check = true }
            local maxz, minz = -math.huge, math.huge
            local in_door = 0
            level.brushes_tagged("kit.part", "yard.skin"):each(function(b)
              maxz, minz = math.max(maxz, b.bounds.max.z), math.min(minz, b.bounds.min.z)
              if b.bounds.min.x < 11.5 and b.bounds.max.x > 8.5 then in_door = in_door + 1 end
            end)
            -- the cart route stays clear of boulders and mounds
            local on_route = 0
            for _, part in ipairs({ "yard.boulder", "yard.mound" }) do
              level.brushes_tagged("kit.part", part):each(function(b)
                if b.bounds.min.z < 8 + 1.5 and b.bounds.max.z > 8 - 1.5 then on_route = on_route + 1 end
              end)
            end
            return string.format("%d %.2f %d %d %d", tr.holes, minz, in_door, on_route, tr.regions.yard.rock.boulders)
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.True(double.Parse(v[1], CultureInfo.InvariantCulture) > 14, "only the north edge (edge 3) is rock: lowest z " + v[1]);
        Assert.Equal("0", v[2]);
        Assert.Equal("0", v[3]);
        Assert.Equal("6", v[4]);
    }

    [Fact]
    public void Rock_Mass_Follows_Its_Outline_In_Strata()
    {
        string script = """
            local kit = ged.import("kit")
            local m = kit.rock_mass{ name = "mesa", poly = {{0, 0}, {8, -1}, {9, 6}, {1, 7}}, y0 = 0, y1 = 9, seed = 2 }
            assert(m.strata >= 2 and m.count >= m.strata, "strata")
            local x0, x1, top = math.huge, -math.huge, -math.huge
            local tops = {}
            for _, b in ipairs(m.brushes) do
              assert(b.detail, "detail")
              x0, x1, top = math.min(x0, b.bounds.min.x), math.max(x1, b.bounds.max.x), math.max(top, b.bounds.max.y)
            end
            return string.format("%.2f %.2f %.2f", x0, x1, top)
            """;
        string[] v = Run(script).Split(' ');
        // the foot follows the outline (corners pushed out by at most the roughness), the top reaches y1
        Assert.InRange(double.Parse(v[0], CultureInfo.InvariantCulture), -1.5, 0.5);
        Assert.InRange(double.Parse(v[1], CultureInfo.InvariantCulture), 8.5, 10.5);
        Assert.InRange(double.Parse(v[2], CultureInfo.InvariantCulture), 8.0, 9.01);
    }

    [Theory]
    [InlineData("kit.trace{ regions = { { name = 'a', poly = {{0,0},{8,0},{8,8},{0,8}}, floor = 0, walls = 'stone' } } }", "walls must be \"rock\"")]
    [InlineData("kit.trace{ regions = { { name = 'a', poly = {{0,0},{8,0},{8,8},{0,8}}, floor = 0, rock_edges = {7} } } }", "names edge 7")]
    [InlineData("kit.trace{ regions = { { name = 'a', poly = {{0,0},{8,0},{8,8},{0,8}}, floor = 0, ground = 'lumpy' } } }", "ground must be")]
    [InlineData("kit.rock_mass{ poly = {{0,0},{4,0},{4,4}}, y0 = 0, y1 = 0.2 }", "at least 0.5 m above")]
    public void Organic_Options_Refuse_With_The_Reason(string call, string expected)
    {
        string err = RunFails("local kit = ged.import('kit')\n" + call);
        Assert.Contains(expected, err, StringComparison.Ordinal);
    }

    private string Run(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("trace-organic"), new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue!;
    }

    private string RunFails(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("trace-organic-refuse"), new ScriptLog());
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
        Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(180), InstructionBudget = 2_000_000_000 },
    };
}
