using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Scripting;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitLayoutHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The composed plans of kit.layout read as a building, not a lattice (indoor DM, CTF and KOTH): over seeds 1-6 most
/// links join zones that share a wall (doorways and wide openings through 1 m walls, joins), long straight corridors
/// are rare, the zone sizes have a clear hierarchy (the largest well above the median in most plans, small rooms and
/// long galleries among them), a share of the zones are traced outlines, most rectangle rooms keep plain corners,
/// headroom varies, few neighbouring zones line up wall end to wall end, the zone count and the area keep to the class
/// budget, and every plan has parts at an angle (a turned zone) or a connector that bends (a CTF half's wing). The
/// same seed always gives the same graph.
/// </summary>
public sealed class KitLayoutCompositionTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutCompositionTests(ITestOutputHelper output) => _out = output;

    /// <summary>Per graph: "abut|long|ratio|gridlike|traced|chamfer0|aspect|small|heights|hratio|turned|zones_ok|area_ok".</summary>
    private const string Metrics = """
        local function tw(rot, x, z) local r = math.rad(rot or 0) local c, s = math.cos(r), math.sin(r) return x * c + z * s, -x * s + z * c end
        local function tl(rot, x, z) local r = math.rad(rot or 0) local c, s = math.cos(r), math.sin(r) return x * c - z * s, x * s + z * c end
        local function poly(z)
          if z.archetype == "poly" then local o = {} for i, q in ipairs(z.outline) do o[i] = { q[1], q[2] } end return o end
          local cx, cz = tl(z.rot, z.pos[1], z.pos[3])
          local w, d = z.size[1] / 2, z.size[2] / 2
          local o = {}
          for i, q in ipairs({ { -w, -d }, { w, -d }, { w, d }, { -w, d } }) do local x, zz = tw(z.rot, cx + q[1], cz + q[2]) o[i] = { x, zz } end
          return o
        end
        local function pseg(p, a, b)
          local ex, ez = b[1] - a[1], b[2] - a[2]
          local L2 = ex * ex + ez * ez
          local t = L2 > 0 and math.max(0, math.min(1, ((p[1] - a[1]) * ex + (p[2] - a[2]) * ez) / L2)) or 0
          local x, z = a[1] + ex * t - p[1], a[2] + ez * t - p[2]
          return math.sqrt(x * x + z * z)
        end
        local function pdist(A, B)
          local best = math.huge
          for i = 1, #A do for j = 1, #B do best = math.min(best, pseg(A[i], B[j], B[j % #B + 1]), pseg(B[j], A[i], A[i % #A + 1])) end end
          return best
        end
        local function parea(p) local s = 0 for i = 1, #p do local a, b = p[i], p[i % #p + 1] s = s + a[1] * b[2] - b[1] * a[2] end return math.abs(s / 2) end
        local function perim(p) local s = 0 for i = 1, #p do local a, b = p[i], p[i % #p + 1] s = s + math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2) end return s end
        local function metrics(g)
          local byid, P = {}, {}
          for _, z in ipairs(g.zones) do byid[z.id] = z P[z.id] = poly(z) end
          local n, abut, long, pairs_, grid, bent = 0, 0, 0, 0, 0, 0
          for _, l in ipairs(g.links) do
            local za, zb = byid[l.a], byid[l.b]
            n = n + 1
            local d = pdist(P[l.a], P[l.b])
            if l.type == "join" or d <= 1.5 then abut = abut + 1 end
            local run = (l.type == "join") and 0 or d
            if l.path then
              local pts = { l.a_at }
              for _, q in ipairs(l.path) do pts[#pts + 1] = q end
              pts[#pts + 1] = l.b_at
              run = 0
              for i = 1, #pts - 1 do run = math.max(run, math.sqrt((pts[i + 1][1] - pts[i][1]) ^ 2 + (pts[i + 1][2] - pts[i][2]) ^ 2)) end
              if #l.path > 0 then bent = bent + 1 end
            end
            if (l.type == "corridor" or l.type == "doorway" or l.type == "road") and run > 8 then long = long + 1 end
            if za.archetype ~= "poly" and zb.archetype ~= "poly" and (za.rot or 0) == (zb.rot or 0) and d <= 12 then
              pairs_ = pairs_ + 1
              local ax, az = tl(za.rot, za.pos[1], za.pos[3])
              local bx, bz = tl(zb.rot, zb.pos[1], zb.pos[3])
              local w1, d1, w2, d2 = za.size[1] / 2, za.size[2] / 2, zb.size[1] / 2, zb.size[2] / 2
              local same
              if math.max(bx - w2 - ax - w1, ax - w1 - bx - w2) > 0 then same = math.abs((az - d1) - (bz - d2)) < 0.6 and math.abs((az + d1) - (bz + d2)) < 0.6
              else same = math.abs((ax - w1) - (bx - w2)) < 0.6 and math.abs((ax + w1) - (bx + w2)) < 0.6 end
              if same then grid = grid + 1 end
            end
          end
          local areas, turned = {}, bent > 0
          local npoly, nrect, nch0, maxasp, small = 0, 0, 0, 0, 0
          local hs, nh, hmin, hmax = {}, 0, math.huge, 0
          for _, z in ipairs(g.zones) do
            local p = P[z.id]
            local a = parea(p)
            areas[#areas + 1] = a
            if a <= 110 then small = small + 1 end
            if (z.rot or 0) ~= 0 then turned = true end
            local asp
            if z.archetype == "poly" then
              npoly = npoly + 1
              local per = perim(p)
              local s1 = per / 4 + math.sqrt(math.max(0, per * per / 16 - a))
              asp = s1 / (a / s1)
            else
              nrect = nrect + 1
              if (z.chamfer or 0) == 0 then nch0 = nch0 + 1 end
              asp = math.max(z.size[1], z.size[2]) / math.min(z.size[1], z.size[2])
            end
            maxasp = math.max(maxasp, asp)
            local h = math.floor((z.height or 0) * 2 + 0.5) / 2
            if not hs[h] then hs[h] = true nh = nh + 1 end
            hmin, hmax = math.min(hmin, z.height or 99), math.max(hmax, z.height or 0)
          end
          table.sort(areas)
          local ratio = areas[#areas] / areas[math.floor((#areas + 1) / 2)]
          local zok = #g.zones >= g.budget.zones[1] and #g.zones <= g.budget.zones[2]
          local aok = g.area >= g.budget.area[1] * 0.9 and g.area <= g.budget.area[2] * 1.1
          return string.format("%.3f|%.3f|%.3f|%.3f|%.3f|%.3f|%.3f|%d|%d|%.3f|%s|%s|%s", abut / n, long / n, ratio, pairs_ > 0 and grid / pairs_ or 0,
            npoly / #g.zones, nrect > 0 and nch0 / nrect or 0, maxasp, small, nh, hmax / hmin, tostring(turned), tostring(zok), tostring(aok))
        end
        """;

    /// <summary>A graph as a canonical string (sorted keys, exact numbers).</summary>
    private const string Serialize = """
        local function ser(v, out)
          local t = type(v)
          if t == "number" then out[#out + 1] = string.format("%.17g", v)
          elseif t == "string" then out[#out + 1] = string.format("%q", v)
          elseif t == "table" then
            local keys = {}
            for k in pairs(v) do keys[#keys + 1] = k end
            table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
            out[#out + 1] = "{"
            for _, k in ipairs(keys) do out[#out + 1] = tostring(k) .. "="; ser(v[k], out); out[#out + 1] = "," end
            out[#out + 1] = "}"
          else out[#out + 1] = tostring(v) end
        end
        local function canon(g) local o = {} ser({ zones = g.zones, links = g.links, objectives = g.objectives }, o) return table.concat(o) end
        """;

    [Theory]
    [InlineData("dm")]
    [InlineData("ctf")]
    [InlineData("koth")]
    public void Composed_Plans_Abut_Vary_In_Size_And_Shape_And_Keep_Off_The_Lattice(string gametype)
    {
        var rows = new List<double[]>();
        int withAspect = 0, withSmall = 0, withRatio = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            int players = Players(gametype, seed);
            (_, ScriptRunResult r) = Run(Metrics + "\n" + $"return metrics({LayoutCall(gametype, "standard", seed, players)})");
            string[] p = r.ReturnValue!.Split('|');
            _out.WriteLine($"{gametype} seed {seed} ({players} players): abut {p[0]} long {p[1]} largest/median {p[2]} gridlike {p[3]} traced {p[4]} " +
                $"plain-corners {p[5]} max-aspect {p[6]} small {p[7]} heights {p[8]} (max/min {p[9]}) turned {p[10]} zones-in-budget {p[11]} area-in-budget {p[12]}");
            Assert.Equal("true", p[11]);
            Assert.Equal("true", p[12]);
            Assert.True(p[10] == "true", $"{gametype} seed {seed}: no zone at an angle and no bent connector");
            if (gametype == "ctf")
            {
                // each half a designed base complex: its traced rooms (the wing's hinge and vestibule, a corner spawn
                // room, re-outlined lane rooms) are a third of the plan or more
                Assert.True(double.Parse(p[4], CultureInfo.InvariantCulture) >= 0.3, $"ctf seed {seed}: only {p[4]} of the zones are traced");
            }

            if (double.Parse(p[2], CultureInfo.InvariantCulture) >= 2.5)
            {
                withRatio++;
            }

            // headroom varies in every plan: at least three heights, the tallest at least 1.8 x the lowest
            Assert.True(int.Parse(p[8], CultureInfo.InvariantCulture) >= 3, $"{gametype} seed {seed}: only {p[8]} distinct headroom(s)");
            Assert.True(double.Parse(p[9], CultureInfo.InvariantCulture) >= 1.8, $"{gametype} seed {seed}: headroom max/min only {p[9]}");
            if (double.Parse(p[6], CultureInfo.InvariantCulture) >= 3)
            {
                withAspect++;
            }

            if (int.Parse(p[7], CultureInfo.InvariantCulture) >= 1)
            {
                withSmall++;
            }

            rows.Add(p.Take(6).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray());
        }

        double Mean(int i) => rows.Average(x => x[i]);
        _out.WriteLine($"{gametype} means: abut {Mean(0):0.00} long {Mean(1):0.00} largest/median {Mean(2):0.00} gridlike {Mean(3):0.00} traced {Mean(4):0.00} plain-corners {Mean(5):0.00}; " +
            $"a gallery (aspect >= 3) in {withAspect}/6, a small room (<= 110 m2) in {withSmall}/6, largest/median >= 2.5 in {withRatio}/6");
        Assert.True(Mean(0) >= 0.7, $"{gametype}: only {Mean(0):0.00} of the links join zones that share a wall");
        Assert.True(Mean(1) <= 0.1, $"{gametype}: {Mean(1):0.00} of the links are straight corridors over 8 m");
        Assert.True(Mean(2) >= 2.8, $"{gametype}: the largest zone is only {Mean(2):0.00} x the median");
        Assert.True(withRatio >= 4, $"{gametype}: the largest zone is 2.5 x the median in only {withRatio} of 6 plans");
        Assert.True(Mean(3) <= 0.15, $"{gametype}: {Mean(3):0.00} of the neighbouring zone pairs line up wall end to wall end");
        Assert.True(Mean(4) >= 0.25, $"{gametype}: only {Mean(4):0.00} of the zones are traced outlines");
        Assert.True(Mean(5) >= 0.45, $"{gametype}: only {Mean(5):0.00} of the rectangle zones keep plain corners");
        Assert.True(withAspect >= 5, $"{gametype}: a long gallery (aspect >= 3) in only {withAspect} of 6 plans");
        Assert.True(withSmall >= 5, $"{gametype}: a small room (<= 110 m2) in only {withSmall} of 6 plans");
    }

    [Theory]
    [InlineData("dm", 2)]
    [InlineData("ctf", 3)]
    [InlineData("koth", 4)]
    public void The_Same_Seed_Gives_The_Same_Graph(string gametype, int seed)
    {
        int players = Players(gametype, seed);
        string call = LayoutCall(gametype, "standard", seed, players);
        (_, ScriptRunResult r) = Run(Serialize + "\n" + $"local a = canon({call})\nlocal b = canon({call})\nreturn (a == b) and ('same ' .. #a) or 'differs'");
        _out.WriteLine($"{gametype} seed {seed}: {r.ReturnValue}");
        Assert.StartsWith("same", r.ReturnValue);
    }
}
