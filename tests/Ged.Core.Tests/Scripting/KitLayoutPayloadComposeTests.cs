using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitLayoutHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Composed payload routes (kit.layout{gametype = "payload"}): the cart track is designed as a polyline whose legs run
/// on different headings, each segment composed in its own frame, the track turning between segments through a bent
/// free cart road or round a corner inside a yard. Over seeds 1-6 every graph meets every layout rule, the track turns
/// by 20 degrees or more at least twice, never crosses itself, has the segments the player count asks for, and the
/// same seed gives the same graph; over seeds 1-4 the rooms meet the road, the yards and each other wall to wall,
/// long corridors are rare, every map has a long gallery, most rooms are plain rectangles of several headrooms, and
/// the yards are the biggest spaces (the finale the biggest of all); one layout builds sealed under both build methods
/// with the cart's keyframes on every bend of the track.
/// </summary>
public sealed class KitLayoutPayloadComposeTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutPayloadComposeTests(ITestOutputHelper output) => _out = output;

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// Lua: track(g) -> "turns|crossings|segments|checkpoints" for a payload graph: the world headings of its roads in
    /// cart order (rot + params.heading), the turns of 20 degrees or more between consecutive roads, and the
    /// crossings of the track's polyline (spine zone centres and every free cart road's waypoints).
    /// </summary>
    private const string Track = """
        local DIRV = { ["+x"] = { 1, 0 }, ["-x"] = { -1, 0 }, ["+z"] = { 0, 1 }, ["-z"] = { 0, -1 } }
        local function tw(rot, x, z) local r = math.rad(rot or 0) local c, s = math.cos(r), math.sin(r) return x * c + z * s, -x * s + z * c end
        local function cr(o, a, b) return (a[1] - o[1]) * (b[2] - o[2]) - (a[2] - o[2]) * (b[1] - o[1]) end
        local function hit(a, b, c, d)
          local d1, d2, d3, d4 = cr(a, b, c), cr(a, b, d), cr(c, d, a), cr(c, d, b)
          return ((d1 > 1e-9 and d2 < -1e-9) or (d1 < -1e-9 and d2 > 1e-9)) and ((d3 > 1e-9 and d4 < -1e-9) or (d3 < -1e-9 and d4 > 1e-9))
        end
        local function track(g)
          local byid = {}
          for _, z in ipairs(g.zones) do byid[z.id] = z end
          local cart, ncp = nil, 0
          for _, o in ipairs(g.objectives) do if o.kind == "cart" then cart = o elseif o.kind == "checkpoint" then ncp = ncp + 1 end end
          local turns, prev = {}, nil
          local pts = {}
          for i, id in ipairs(cart.zones) do
            local z = byid[id]
            if z.archetype == "road" then
              local d = DIRV[(z.params and z.params.heading) or "+z"]
              local x, zz = tw(z.rot, d[1], d[2])
              local yaw = math.deg(math.atan2(x, zz))
              if prev then
                local dd = math.abs((yaw - prev + 540) % 360 - 180)
                if dd >= 20 then turns[#turns + 1] = string.format("%.0f", dd) end
              end
              prev = yaw
            end
            -- the free cart road from the previous spine zone: its waypoints
            if i > 1 then
              for _, l in ipairs(g.links) do
                if l.path and ((l.a == cart.zones[i - 1] and l.b == id) or (l.b == cart.zones[i - 1] and l.a == id)) then
                  for _, q in ipairs(l.path) do pts[#pts + 1] = { q[1], q[2] } end
                end
              end
            end
            pts[#pts + 1] = { z.pos[1], z.pos[3] }
          end
          local cross = 0
          for i = 1, #pts - 1 do for j = i + 2, #pts - 1 do if hit(pts[i], pts[i + 1], pts[j], pts[j + 1]) then cross = cross + 1 end end end
          return table.concat(turns, ",") .. "|" .. cross .. "|" .. tostring(g.segments) .. "|" .. ncp
        end
        """;

    /// <summary>
    /// Lua: comp(g) -> "abut|long|asp3|plain|rect|heights|maxroom|minyard|finale_biggest" for a payload graph: the share
    /// of room links (any link but spine to spine) whose two zones stand within 1.5 m (wall to wall, or joined), the
    /// share that are corridors running straight more than 8 m, the rooms (zones off the spine) at least three times
    /// as long as wide, the rectangle rooms with plain corners and all rectangle rooms, the distinct room headrooms,
    /// the largest room's area, the smallest checkpoint yard's and whether the finale is the largest zone.
    /// </summary>
    private const string Composition = """
        local SPINE = { road = true, yard = true, finale = true, start = true }
        local function tw(rot, x, z) local r = math.rad(rot or 0) local c, s = math.cos(r), math.sin(r) return x * c + z * s, -x * s + z * c end
        local function tl(rot, x, z) local r = math.rad(rot or 0) local c, s = math.cos(r), math.sin(r) return x * c - z * s, x * s + z * c end
        local function poly(z)
          if z.archetype == "poly" then return z.outline end
          local cx, cz = tl(z.rot, z.pos[1], z.pos[3])
          local w, d = z.size[1] / 2, z.size[2] / 2
          local o = {}
          for i, q in ipairs({ { -w, -d }, { w, -d }, { w, d }, { -w, d } }) do local x, zz = tw(z.rot, cx + q[1], cz + q[2]) o[i] = { x, zz } end
          return o
        end
        local function pseg(p, a, b)
          local ex, ez = b[1] - a[1], b[2] - a[2]
          local l2 = ex * ex + ez * ez
          local t = l2 > 0 and math.max(0, math.min(1, ((p[1] - a[1]) * ex + (p[2] - a[2]) * ez) / l2)) or 0
          return math.sqrt((a[1] + ex * t - p[1]) ^ 2 + (a[2] + ez * t - p[2]) ^ 2)
        end
        local function pdist(A, B)
          local best = math.huge
          for i = 1, #A do for j = 1, #B do best = math.min(best, pseg(A[i], B[j], B[j % #B + 1]), pseg(B[j], A[i], A[i % #A + 1])) end end
          return best
        end
        local function area(z)
          if z.archetype ~= "poly" then return z.size[1] * z.size[2] end
          local s, p = 0, z.outline
          for i = 1, #p do local a, b = p[i], p[i % #p + 1] s = s + a[1] * b[2] - b[1] * a[2] end
          return math.abs(s / 2)
        end
        -- (a traced room's length: its longest edge; its aspect that squared over its area)
        local function aspect(z, a)
          if z.archetype ~= "poly" then return math.max(z.size[1], z.size[2]) / math.min(z.size[1], z.size[2]) end
          local e, p = 0, z.outline
          for i = 1, #p do local q, r = p[i], p[i % #p + 1] e = math.max(e, math.sqrt((r[1] - q[1]) ^ 2 + (r[2] - q[2]) ^ 2)) end
          return e * e / a
        end
        local function comp(g)
          local byid, P = {}, {}
          for _, z in ipairs(g.zones) do byid[z.id] = z P[z.id] = poly(z) end
          local n, abut, long = 0, 0, 0
          for _, l in ipairs(g.links) do
            local a, b = byid[l.a], byid[l.b]
            if not (SPINE[a.archetype] and SPINE[b.archetype]) then
              n = n + 1
              local d = pdist(P[l.a], P[l.b])
              if l.type == "join" or d <= 1.5 then abut = abut + 1 end
              local run = d
              if l.path then
                local pts = { l.a_at }
                for _, q in ipairs(l.path) do pts[#pts + 1] = q end
                pts[#pts + 1] = l.b_at
                run = 0
                for i = 1, #pts - 1 do run = math.max(run, math.sqrt((pts[i + 1][1] - pts[i][1]) ^ 2 + (pts[i + 1][2] - pts[i][2]) ^ 2)) end
              end
              if l.type == "corridor" and run > 8 then long = long + 1 end
            end
          end
          local asp3, plain, rect, hs, nh, maxroom, minyard, fin, zmax = 0, 0, 0, {}, 0, 0, math.huge, 0, 0
          for _, z in ipairs(g.zones) do
            local a = area(z)
            zmax = math.max(zmax, a)
            if z.archetype == "yard" then minyard = math.min(minyard, a) end
            if z.archetype == "finale" then fin = a end
            if not SPINE[z.archetype] then
              maxroom = math.max(maxroom, a)
              if aspect(z, a) >= 3 then asp3 = asp3 + 1 end
              if z.archetype ~= "poly" then rect = rect + 1 if (z.chamfer or 0) == 0 then plain = plain + 1 end end
              local h = string.format("%.1f", z.height or 0)
              if not hs[h] then hs[h] = true nh = nh + 1 end
            end
          end
          return string.format("%.3f|%.3f|%d|%d|%d|%d|%.0f|%.0f|%s", abut / n, long / n, asp3, plain, rect, nh, maxroom, minyard, tostring(fin >= zmax))
        end
        """;

    /// <summary>Serializes a graph with sorted keys (a stable text for comparing two generations).</summary>
    private const string Serializer = """
        local function ser(v, out)
          local t = type(v)
          if t == "table" then
            -- (keys sorted as strings by the native comparison: a Lua comparator is slow on a graph this size)
            local keys, byname = {}, {}
            for k in pairs(v) do local n = tostring(k) keys[#keys + 1] = n byname[n] = k end
            table.sort(keys)
            out[#out + 1] = "{"
            for _, n in ipairs(keys) do out[#out + 1] = n .. "=" ser(v[byname[n]], out) out[#out + 1] = "," end
            out[#out + 1] = "}"
          elseif t == "number" then out[#out + 1] = string.format("%.4f", v)
          else out[#out + 1] = tostring(v) end
        end
        local function text(g) local out = {} ser(g, out) return table.concat(out) end
        """;

    [Fact]
    public void Payload_Routes_Turn_Natively_And_Meet_Every_Rule()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            int players = Players("payload", seed);
            (_, ScriptRunResult r) = Run(Track + "\n" +
                $"local g = {LayoutCall("payload", "payload", seed, players)}\n" +
                "local v = kit.check_layout{ graph = g }\n" +
                "local msgs = {} for _, x in ipairs(v) do msgs[#msgs + 1] = x.severity .. '/' .. x.rule .. ': ' .. x.message end\n" +
                "return #v .. '~' .. table.concat(msgs, ' ;; ') .. '~' .. track(g) .. '~' .. #g.zones .. '~' .. string.format('%.0f', g.area)");
            string[] p = r.ReturnValue!.Split('~');
            string[] t = p[2].Split('|');
            string[] turns = t[0].Length > 0 ? t[0].Split(',') : Array.Empty<string>();
            _out.WriteLine($"seed {seed} ({players} players): violations {p[0]}; turns [{t[0]}]; crossings {t[1]}; segments {t[2]}; checkpoints {t[3]}; zones {p[3]}; area {p[4]}");
            Assert.True(p[0] == "0", $"seed {seed}: the graph breaks layout rules: {p[1]}");
            Assert.True(turns.Length >= 2, $"seed {seed}: the track turns by 20 degrees or more only {turns.Length} time(s) [{t[0]}]");
            Assert.Equal("0", t[1]);
            int segments = 3 + (players >= 12 ? 1 : 0) + (players >= 20 ? 1 : 0);
            Assert.Equal(segments.ToString(CultureInfo.InvariantCulture), t[2]);
            Assert.Equal(segments.ToString(CultureInfo.InvariantCulture), t[3]);
        }
    }

    [Fact]
    public void Payload_Rooms_Meet_Wall_To_Wall_In_Varied_Sizes()
    {
        for (int seed = 1; seed <= 4; seed++)
        {
            int players = Players("payload", seed);
            (_, ScriptRunResult r) = Run(Composition + "\n" + $"return comp({LayoutCall("payload", "payload", seed, players)})");
            string[] p = r.ReturnValue!.Split('|');
            double abut = D(p[0]), longShare = D(p[1]);
            int asp3 = int.Parse(p[2], CultureInfo.InvariantCulture), plain = int.Parse(p[3], CultureInfo.InvariantCulture);
            int rect = int.Parse(p[4], CultureInfo.InvariantCulture), heights = int.Parse(p[5], CultureInfo.InvariantCulture);
            double maxRoom = D(p[6]), minYard = D(p[7]);
            _out.WriteLine($"seed {seed} ({players} players): wall to wall {abut:0.00}; long corridors {longShare:0.00}; galleries {asp3}; " +
                $"plain {plain}/{rect}; headrooms {heights}; largest room {maxRoom:0} m2, smallest yard {minYard:0} m2; finale largest {p[8]}");
            Assert.True(abut >= 0.55, $"seed {seed}: only {abut:0.00} of the room links join rooms that stand wall to wall");
            Assert.True(longShare <= 0.15, $"seed {seed}: {longShare:0.00} of the room links are corridors running straight over 8 m");
            Assert.True(asp3 >= 1, $"seed {seed}: no room at least three times as long as wide");
            Assert.True(plain * 2 >= rect, $"seed {seed}: only {plain} of {rect} rectangle rooms keep plain corners");
            Assert.True(heights >= 4, $"seed {seed}: the rooms take {heights} headroom(s)");
            Assert.True(maxRoom < minYard, $"seed {seed}: a {maxRoom:0} m2 room outgrows the smallest checkpoint yard ({minYard:0} m2)");
            Assert.Equal("true", p[8]);
        }
    }

    [Fact]
    public void Same_Seed_Gives_The_Same_Payload_Graph()
    {
        int players = Players("payload", 2);
        (_, ScriptRunResult r) = Run(Serializer + "\n" +
            $"local a = text({LayoutCall("payload", "payload", 2, players)})\n" +
            $"local b = text({LayoutCall("payload", "payload", 2, players)})\n" +
            $"local c = text({LayoutCall("payload", "payload", 3, players)})\n" +
            "return tostring(a == b) .. '|' .. tostring(a ~= c)");
        Assert.Equal("true|true", r.ReturnValue);
    }

    [Fact]
    [Trait("Category", "DeepGate")]
    public void A_Turning_Route_Builds_Sealed_With_The_Cart_On_Every_Bend()
    {
        const int seed = 3;
        int players = Players("payload", seed);
        (EditorDocument doc, ScriptRunResult r) = Run(
            $"local g = {LayoutCall("payload", "payload", seed, players)}\n" +
            "local rep = kit.build_layout{ graph = g, name = 'P', lights = false }\n" +
            "local cart = level.find_mover('P_cart')\n" +
            "local kf = {} for _, k in ipairs(cart.keyframes) do kf[#kf + 1] = string.format('%.3f,%.3f', k.pos.x, k.pos.z) end\n" +
            "local cz = nil for _, o in ipairs(g.objectives) do if o.kind == 'cart' then cz = o.zones end end\n" +
            "local onspine = {} for _, id in ipairs(cz) do onspine[id] = true end\n" +
            "local bends = {}\n" +
            "for _, l in ipairs(rep.links) do\n" +
            "  if l.mode == 'free' and onspine[l.a] and onspine[l.b] and l.path then\n" +
            "    for i = 2, #l.path - 1 do bends[#bends + 1] = string.format('%.3f,%.3f', l.path[i][1], l.path[i][2]) end\n" +
            "  end\n" +
            "end\n" +
            "return table.concat(kf, ';') .. '~' .. table.concat(bends, ';')");
        string[] p = r.ReturnValue!.Split('~');
        List<double[]> kf = p[0].Split(';').Select(q => q.Split(',').Select(D).ToArray()).ToList();
        List<double[]> bends = p[1].Length > 0 ? p[1].Split(';').Select(q => q.Split(',').Select(D).ToArray()).ToList() : new List<double[]>();
        _out.WriteLine($"{kf.Count} keyframes; {bends.Count} bend point(s) on free cart roads");
        Assert.NotEmpty(bends);
        foreach (double[] q in bends)
        {
            Assert.Contains(kf, k => Math.Abs(k[0] - q[0]) < 0.01 && Math.Abs(k[1] - q[1]) < 0.01);
        }

        // The keyframe track turns: its heading changes by 20 degrees or more, between legs of at least 6 m, twice
        var dirs = new List<double>();
        double[] last = kf[0];
        foreach (double[] k in kf.Skip(1))
        {
            double dx = k[0] - last[0], dz = k[1] - last[1];
            if (Math.Sqrt(dx * dx + dz * dz) >= 6)
            {
                dirs.Add(Math.Atan2(dx, dz) * 180 / Math.PI);
                last = k;
            }
        }

        int turns = 0;
        double run = dirs[0];
        foreach (double d in dirs.Skip(1))
        {
            double dd = Math.Abs(((d - run) % 360 + 540) % 360 - 180);
            if (dd >= 20)
            {
                turns++;
                run = d;
            }
        }

        _out.WriteLine($"keyframe track turns: {turns}");
        Assert.True(turns >= 2, $"the cart's keyframes turn {turns} time(s) by 20 degrees or more");
        foreach (bool shared in new[] { true, false })
        {
            (CompiledLevel _, List<Vec3> holes) = Holes(doc, sharedBsp: shared);
            Assert.True(holes.Count == 0, $"{(shared ? "Shared BSP" : "Incremental")}: {holes.Count} hole(s), e.g. " +
                string.Join("; ", holes.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
        }
    }
}
