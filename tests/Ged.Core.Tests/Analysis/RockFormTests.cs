using System;
using System.Globalization;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The form of kit_rock's eroded rock (kit.trace's canyon / cave skins and vault, kit.rock_mass), measured on the
/// compiled level the way a player sees it: rays from eye height over the floor. "Seen vertical" is the share of
/// rock hits whose surface is within 5 degrees of vertical. A curtain of chunks with surface noise sees about 0.55
/// (the previous skins measured 0.53 to 0.60 by face area and 0.57 seen); eroded rock that leans 10 to 30 degrees,
/// bulges in spurs and steps back in ledges must stay under 0.4. "Seen non-rock" is the share of those hits on a
/// non-rock texture: a gap between chunks shows the traced wall (or a sliver takes the floor texture), the grey
/// slashes the previous skins had; it must stay under 2 %.
/// </summary>
public sealed class RockFormTests
{
    private readonly ITestOutputHelper _out;

    public RockFormTests(ITestOutputHelper output) => _out = output;

    private const string Seen = """
        local function inside(p, x, z)
          local c, j = false, #p
          for i = 1, #p do
            local a, b = p[i], p[j]
            if ((a[2] > z) ~= (b[2] > z)) and (x < (b[1] - a[1]) * (z - a[2]) / (b[2] - a[2]) + a[1]) then c = not c end
            j = i
          end
          return c
        end
        local function seen(r)
          local p = r.points
          local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
          for _, q in ipairs(p) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
          local n, vert, nonrock = 0, 0, 0
          for x = x0 + 1.5, x1 - 1.5, 3 do
            for z = z0 + 1.5, z1 - 1.5, 3 do
              if inside(p, x, z) then
                local from = { x, r.floor + 1.7, z }
                local up = spatial.raycast{ from = from, dir = { 0, 1, 0 }, max = 60 }
                if not (up.hit and up.back_face) then
                  for ai = 0, 11 do
                    local az = ai * math.pi / 6 + 0.1
                    for _, el in ipairs({ -12, 8, 28, 50 }) do
                      local ce = math.cos(math.rad(el))
                      local h = spatial.raycast{ from = from, dir = { math.cos(az) * ce, math.sin(math.rad(el)), math.sin(az) * ce }, max = 80 }
                      if h.hit and not h.sky and not h.back_face and not (math.abs(h.pos.y - r.floor) < 0.03 and h.normal.y > 0.99)
                        and h.pos.x > x0 - 3 and h.pos.x < x1 + 3 and h.pos.z > z0 - 3 and h.pos.z < z1 + 3 then
                        n = n + 1
                        if math.abs(h.normal.y) < 0.087 then vert = vert + 1 end
                        if not tostring(h.texture or ""):lower():find("rck_") then nonrock = nonrock + 1 end
                      end
                    end
                  end
                end
              end
            end
          end
          return vert / math.max(1, n), nonrock / math.max(1, n), n
        end
        """;

    [Fact]
    public void Canyon_And_Cave_Rock_Leans_Steps_And_Is_Watertight()
    {
        string script = Seen + "\n" + """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "rf", seed = 3, regions = {
              { name = "canyon", kind = "canyon", floor = 0, height = 14, overhang = 1.5,
                poly = {{-14, -12}, {6, -16}, {16, -6}, {14, 10}, {2, 14}, {-12, 8}} },
              { name = "cave", kind = "cave", floor = 0, height = 6, poly = {{30, -10}, {48, -12}, {52, 4}, {40, 12}, {28, 6}} },
              { name = "tun", kind = "path", organic = true, width = 4, floor = 0, height = 4, path = {{17, 0}, {22, 1}, {25, -3}, {27.5, -2}} } },
              openings = { { a = "canyon", b = "tun", at = {16, 0}, width = 3.5 }, { a = "tun", b = "cave", at = {28.4, -2}, width = 3.5 } },
              check = true }
            ops.build()
            local cv, cn, cc = seen(tr.regions.canyon)
            local vv, vn, vc = seen(tr.regions.cave)
            assert(cc > 500 and vc > 500, "enough rays: " .. cc .. ", " .. vc)
            assert(tr.regions.canyon.rock.foot <= 0.35 + 1e-9 and tr.regions.cave.rock.foot <= 0.35 + 1e-9, "the foot keeps the outline")
            -- the vault is one continuous underside: from eye height well inside the cave, straight up hits rock
            -- hanging below the ceiling almost everywhere
            local p = tr.regions.cave.points
            local under, probes = 0, 0
            for x = 33, 48, 1.5 do
              for z = -7, 8, 1.5 do
                if inside(p, x, z) then
                  local ok = true
                  for _, d in ipairs({ {3, 0}, {-3, 0}, {0, 3}, {0, -3} }) do if not inside(p, x + d[1], z + d[2]) then ok = false end end
                  if ok then
                    probes = probes + 1
                    local h = spatial.raycast{ from = { x, 1.7, z }, dir = { 0, 1, 0 }, max = 10 }
                    if h.hit and h.pos.y < 6 - 0.1 then under = under + 1 end
                  end
                end
              end
            end
            return string.format("%d %.3f %.3f %.3f %.3f %.3f", tr.holes, cv, cn, vv, vn, under / math.max(1, probes))
            """;
        string[] v = Run(script).Split(' ');
        Assert.Equal("0", v[0]);
        Assert.True(D(v[1]) < 0.40, "canyon seen-vertical share " + v[1]);
        Assert.True(D(v[2]) < 0.02, "canyon seen non-rock share " + v[2]);
        Assert.True(D(v[3]) < 0.40, "cave seen-vertical share " + v[3]);
        Assert.True(D(v[4]) < 0.02, "cave seen non-rock share " + v[4]);
        Assert.True(D(v[5]) > 0.8, "cave vault coverage " + v[5]);
    }

    [Fact]
    public void Rock_Mass_Has_A_Talus_Apron_Rounded_Strata_And_A_Wandering_Rim()
    {
        string script = """
            local kit = ged.import("kit")
            level.brush{ shape = "box", air = true, pos = { 22, 7, 6 }, size = { 72, 14, 44 } }
            local m = kit.rock_mass{ name = "mesa", poly = {{0, 0}, {14, -1}, {16, 11}, {2, 13}}, y0 = 0, y1 = 10, seed = 5 }
            local sheer = kit.rock_mass{ name = "sheer", poly = {{30, 0}, {44, -1}, {46, 11}, {32, 13}}, y0 = 0, y1 = 10, seed = 5, talus = 0, step = 0 }
            local x0, x1, top = math.huge, -math.huge, -math.huge
            for _, b in ipairs(m.brushes) do
              assert(b.detail, "detail")
              x0, x1, top = math.min(x0, b.bounds.min.x), math.max(x1, b.bounds.max.x), math.max(top, b.bounds.max.y)
            end
            ops.build()
            -- what a player sees of each mass, walking round it at 14 m (eye height, mid and upper heights)
            local function seen(cx, cz)
              local n, vert = 0, 0
              for ai = 0, 23 do
                local az = ai * math.pi / 12
                for _, hgt in ipairs({ 1.7, 5, 9 }) do
                  local from = { cx + math.cos(az) * 14, hgt, cz + math.sin(az) * 14 }
                  for _, el in ipairs({ -20, 0, 20 }) do
                    for _, da in ipairs({ -0.25, 0, 0.25 }) do
                      local a2, ce = az + math.pi + da, math.cos(math.rad(el))
                      local h = spatial.raycast{ from = from, dir = { math.cos(a2) * ce, math.sin(math.rad(el)), math.sin(a2) * ce }, max = 19 }
                      if h.hit and not h.back_face and h.pos.y > 0.05 and tostring(h.texture or ""):lower():find("rck_") then
                        n = n + 1
                        if math.abs(h.normal.y) < 0.087 then vert = vert + 1 end
                      end
                    end
                  end
                end
              end
              return vert / math.max(1, n), n
            end
            local mv, mn = seen(8, 6)
            local sv, sn = seen(38, 6)
            assert(mn > 200 and sn > 200, "rays " .. mn .. ", " .. sn)
            -- the apron: at the foot the rock reaches the outline; 1.2 m up it has stepped back
            local foot_hit = spatial.raycast{ from = { 8, 0.3, -6 }, dir = { 0, 0, 1 }, max = 10 }
            local up_hit = spatial.raycast{ from = { 8, 1.2, -6 }, dir = { 0, 0, 1 }, max = 10 }
            return string.format("%.3f %.3f %.2f %.2f %.2f %.2f %.2f %d", mv, sv, x0, x1, top,
              foot_hit.hit and foot_hit.pos.z or 99, up_hit.hit and up_hit.pos.z or 99, m.strata)
            """;
        string[] v = Run(script).Split(' ');
        Assert.True(D(v[0]) < 0.40, "mesa seen-vertical share " + v[0]);
        Assert.True(D(v[0]) < D(v[1]), "eroded mesa " + v[0] + " vs sheer column " + v[1]);
        Assert.InRange(D(v[2]), -1.5, 0.5);
        Assert.InRange(D(v[3]), 15.5, 17.5);
        Assert.InRange(D(v[4]), 9.0, 10.01);
        Assert.True(D(v[6]) - D(v[5]) > 0.3, "the talus slopes back: foot z " + v[5] + ", 1.2 m up z " + v[6]);
        Assert.True(int.Parse(v[7], CultureInfo.InvariantCulture) >= 3, "strata " + v[7]);
    }
    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private string Run(string script)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("rock-form"), new ScriptLog());
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
        Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(240), InstructionBudget = 2_000_000_000 },
    };
}
