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
/// Open-air layouts (kit.layout{class = "open_air"}) are composed sky platforms, not rows and columns: over seeds
/// 1-6 per gametype every graph meets kit.check_layout's rules, has three floor levels or more, platforms at two
/// yaws or more, a size hierarchy (the largest platform at least twice the median), CTF halves that mirror each
/// other, and the same seed gives the same graph. One layout builds sealed under both build methods with every
/// spawn reachable on foot.
/// </summary>
public sealed class KitLayoutOpenComposeTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutOpenComposeTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Gametypes() => new[] { new object[] { "dm" }, new object[] { "ctf" }, new object[] { "koth" } };

    /// <summary>Per graph: "violations|levels|yaws|largest/median|mirrored|method|messages".</summary>
    private const string Metrics = """
        local function metrics(g)
          local v = kit.check_layout{ graph = g }
          local msgs = {}
          for _, x in ipairs(v) do msgs[#msgs + 1] = x.severity .. "/" .. x.rule .. ": " .. x.message end
          local yaws, ny, areas = {}, 0, {}
          for _, z in ipairs(g.zones) do
            local r = string.format("%.1f", (z.rot or 0) % 360)
            if not yaws[r] then yaws[r] = true ny = ny + 1 end
            areas[#areas + 1] = z.size[1] * z.size[2]
          end
          table.sort(areas)
          local med = areas[math.floor((#areas + 1) / 2)]
          -- mirror symmetry about x = 0: every zone has a twin of its archetype at (-x, z) turned the other way
          local mirrored = true
          for _, z in ipairs(g.zones) do
            local found = false
            for _, o in ipairs(g.zones) do
              local dr = math.abs(((o.rot or 0) + (z.rot or 0) + 180) % 360 - 180)
              if o.archetype == z.archetype and math.abs(o.pos[1] + z.pos[1]) < 0.01 and math.abs(o.pos[3] - z.pos[3]) < 0.01 and
                math.abs(o.pos[2] - z.pos[2]) < 0.01 and dr < 0.01 then found = true end
            end
            if not found then mirrored = false end
          end
          return #v .. "|" .. #g.levels .. "|" .. ny .. "|" .. string.format("%.2f", areas[#areas] / med) .. "|" .. tostring(mirrored) .. "|" ..
            tostring(g.method) .. "|" .. table.concat(msgs, " ;; ")
        end
        local function ser(v, out)
          local t = type(v)
          if t == "table" then
            -- (keys as text, sorted without a comparator: a comparator is slow in the interpreter)
            local keys, of = {}, {}
            for k in pairs(v) do local sk = tostring(k) keys[#keys + 1] = sk of[sk] = k end
            table.sort(keys)
            out[#out + 1] = "{"
            for _, sk in ipairs(keys) do out[#out + 1] = sk .. "=" ser(v[of[sk]], out) out[#out + 1] = "," end
            out[#out + 1] = "}"
          elseif t == "number" then out[#out + 1] = string.format("%.4f", v)
          else out[#out + 1] = tostring(v) end
        end
        local function text(g) local out = {} ser(g, out) return table.concat(out) end
        """;

    [Theory]
    [Trait("Category", "DeepGate")]
    [MemberData(nameof(Gametypes))]
    public void Open_Air_Layouts_Are_Composed_Sky_Platforms_That_Meet_The_Rules(string gametype)
    {
        var lines = new List<string>();
        for (int seed = 1; seed <= 6; seed++)
        {
            int players = Players(gametype, seed);
            string call = LayoutCall(gametype, "open_air", seed, players);
            (_, ScriptRunResult r) = Run(Metrics + "\n" +
                $"local g = {call}\n" +
                $"local same = text(g) == text({call})\n" +
                "return metrics(g) .. '|' .. tostring(same)");
            string[] p = r.ReturnValue!.Split('|');
            string line = $"{gametype} seed {seed} ({players} players): violations {p[0]}, levels {p[1]}, yaws {p[2]}, largest/median {p[3]}, mirrored {p[4]}, method {p[5]}, deterministic {p[7]} {p[6]}";
            lines.Add(line);
            _out.WriteLine(line);
            Assert.True(p[0] == "0", "the layout breaks rules: " + line);
            Assert.True(int.Parse(p[1], CultureInfo.InvariantCulture) >= 3, "fewer than 3 floor levels: " + line);
            Assert.True(int.Parse(p[2], CultureInfo.InvariantCulture) >= 2, "every platform at one yaw: " + line);
            Assert.True(double.Parse(p[3], CultureInfo.InvariantCulture) >= 2, "no size hierarchy: " + line);
            Assert.Equal("free", p[5]);
            Assert.Equal("true", p[7]);
            if (gametype == "ctf")
            {
                Assert.True(p[4] == "true", "the CTF halves do not mirror each other: " + line);
            }
        }
    }

    [Fact]
    [Trait("Category", "DeepGate")]
    public void An_Open_Air_Layout_Builds_Sealed_With_Every_Spawn_Reachable()
    {
        const int seed = 3;
        int players = Players("dm", seed);
        (EditorDocument doc, ScriptRunResult r) = Run(
            $"local g = {LayoutCall("dm", "open_air", seed, players)}\n" +
            "local v = kit.check_layout{ graph = g }\n" +
            "local rep = kit.build_layout{ graph = g, name = 'O' }\n" +
            "ops.build()\n" +
            "local sp = level.objects_of('respawn')\n" +
            "local w = spatial.walk{ from = { sp[1].pos.x, sp[1].pos.y - 1, sp[1].pos.z } }\n" +
            "return #v .. '~' .. rep.spawns .. '~' .. #sp .. '~' .. #w.unreachable_spawns");
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.Equal("0", p[0]);
        Assert.True(int.Parse(p[2], CultureInfo.InvariantCulture) >= 4, "too few spawns: " + r.ReturnValue);
        Assert.True(p[3] == "0", "spawns not reachable on foot from the first: " + r.ReturnValue);
        (CompiledLevel _, List<Vec3> holes) = Holes(doc, sharedBsp: true);
        Assert.True(holes.Count == 0, $"{holes.Count} hole(s) on the sealed Shared BSP build, e.g. " +
            string.Join("; ", holes.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
        (CompiledLevel _, List<Vec3> holesIncremental) = Holes(doc, sharedBsp: false);
        Assert.True(holesIncremental.Count == 0, $"{holesIncremental.Count} hole(s) on the sealed Incremental build");
    }
}
