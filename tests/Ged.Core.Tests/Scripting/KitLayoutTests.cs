using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitLayoutHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The layout graph (DESIGN-QUALITY-SPEC §c.1-1, §(h), §(g) Q4): kit.layout is deterministic per seed and its graphs
/// pass kit.check_layout; the checker names the zone or link and the rule for every c.1-1 constraint a hand-edited graph
/// breaks; and one seeded layout per gametype × class builds watertight (sealed, both build methods), lint-clean, with the
/// grader's flow checks passing and the hard simplicity/confusion checks never failing. The 20-seed gate is
/// <see cref="KitLayoutGateTests"/> (DeepGate).
/// </summary>
public sealed class KitLayoutTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> AllCombos() => Combos.Select(c => new object[] { c.Gametype, c.Class });

    // ---- determinism and generation ------------------------------------------------------------

    /// <summary>Serializes a graph with sorted keys (a stable text for comparing two generations).</summary>
    private const string Serializer = """
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
          elseif t == "userdata" then out[#out + 1] = string.format("(%.4f,%.4f,%.4f)", v.x, v.y, v.z)
          else out[#out + 1] = tostring(v) end
        end
        local function text(g) local out = {} ser(g, out) return table.concat(out) end
        """;

    [Theory]
    [MemberData(nameof(AllCombos))]
    public void Layout_Is_Deterministic_Per_Seed_And_Clean(string gametype, string cls)
    {
        int players = Players(gametype, 1);
        (_, ScriptRunResult r) = Run(Serializer + "\n" +
            $"local a = text({LayoutCall(gametype, cls, 1, players)})\n" +
            $"local b = text({LayoutCall(gametype, cls, 1, players)})\n" +
            $"local c = text({LayoutCall(gametype, cls, 2, players)})\n" +
            $"local g = {LayoutCall(gametype, cls, 1, players)}\n" +
            "local v = kit.check_layout{ graph = g }\n" +
            "local msgs = {} for _, x in ipairs(v) do msgs[#msgs + 1] = x.severity .. '/' .. x.rule .. ': ' .. x.message end\n" +
            "return tostring(a == b) .. '|' .. tostring(a ~= c) .. '|' .. #g.zones .. '|' .. g.class .. '|' .. table.concat(msgs, ' ;; ')");
        string[] p = r.ReturnValue!.Split('|');
        _out.WriteLine(r.ReturnValue);
        Assert.Equal("true", p[0]); // same seed, same graph
        Assert.Equal("true", p[1]); // another seed, another graph
        Assert.Equal(cls, p[3]);
        Assert.True(p[4].Length == 0, "the generated graph breaks layout rules: " + p[4]);
    }

    [Fact]
    public void Generated_Layouts_Keep_To_Their_Budgets_And_Rules()
    {
        // Several seeds per combination, generation only (no build): no geometry errors ever, and the c.1-1 rules met.
        var lines = new List<string>();
        int clean = 0, total = 0;
        foreach ((string gametype, string cls) in Combos)
        {
            for (int seed = 2; seed <= 4; seed++)
            {
                int players = Players(gametype, seed);
                (_, ScriptRunResult r) = Run(
                    $"local g = {LayoutCall(gametype, cls, seed, players)}\n" +
                    "local v = kit.check_layout{ graph = g }\n" +
                    "local e, m = 0, {} for _, x in ipairs(v) do if x.severity == 'error' then e = e + 1 end m[#m + 1] = x.rule .. ': ' .. x.message end\n" +
                    "local pv = kit.layout_preview{ graph = g }\n" +
                    "return e .. '|' .. #v .. '|' .. string.format('%.0f', g.area) .. '|' .. g.budget.area[1] .. '-' .. g.budget.area[2] .. '|' .. #pv.text .. '|' .. table.concat(m, ' ;; ')");
                string[] p = r.ReturnValue!.Split('|');
                lines.Add($"{gametype}/{cls} seed {seed} ({players} players): errors {p[0]}, violations {p[1]}, area {p[2]} (budget {p[3]}) {p[5]}");
                Assert.Equal("0", p[0]);
                Assert.True(int.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) > 200, "the preview is empty");
                total++;
                if (p[1] == "0")
                {
                    clean++;
                }
            }
        }

        foreach (string l in lines)
        {
            _out.WriteLine(l);
        }

        Assert.True(clean >= total * 0.9, $"only {clean} of {total} generated layouts meet every rule:\n" + string.Join("\n", lines));
    }

    [Fact]
    public void Preview_Draws_Zones_Links_And_Objectives()
    {
        (_, ScriptRunResult r) = Run(
            "local g = kit.layout{ gametype = 'ctf', seed = 3, players = 12 }\n" +
            "local p = kit.layout_preview{ graph = g }\n" +
            "local seen, types = {}, {}\n" +
            "for _, l in ipairs(g.links) do if not seen[l.type] then seen[l.type] = true types[#types + 1] = l.type end end\n" +
            "return p.text .. '\\n@@SVG ' .. #p.svg .. ' ' .. tostring(p.svg:find('<svg') ~= nil) .. ' ' .. #p.legend .. '\\n@@TYPES ' .. table.concat(types, ' ')");
        string text = r.ReturnValue!;
        _out.WriteLine(text);
        Assert.StartsWith("PLAN ctf standard", text, StringComparison.Ordinal);
        Assert.Contains("R", text, StringComparison.Ordinal); // the red flag
        Assert.Contains("B", text, StringComparison.Ordinal);
        Assert.Contains(" gallery [base]", text, StringComparison.Ordinal);
        // every link type the graph uses is listed in the legend ("l<n> <type> z<a>-z<b> ...")
        string[] types = text[(text.LastIndexOf("@@TYPES ", StringComparison.Ordinal) + 8)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(types.Length >= 2, "the generated graph uses fewer than two link types: " + string.Join(",", types));
        foreach (string t in types)
        {
            Assert.Contains($" {t} z", text, StringComparison.Ordinal);
        }
        Assert.Contains("spawns red", text, StringComparison.Ordinal);
        Assert.Contains("@@SVG", text, StringComparison.Ordinal);
        Assert.Contains(" true ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Archetype_Library_Lists_Every_Zone_Kind()
    {
        (_, ScriptRunResult r) = Run(
            "local out = {} for _, a in ipairs(kit.layout_archetypes{}) do out[#out + 1] = a.name .. '/' .. a.class end return table.concat(out, ',')");
        string[] listed = r.ReturnValue!.Split(',');
        Assert.Contains("atrium/indoor", listed);
        Assert.Contains("sunken/indoor", listed);
        Assert.Contains("isle/open_air", listed);
        Assert.Contains("summit/open_air", listed);
        Assert.Contains("road/payload", listed);
        Assert.Contains("finale/payload", listed);
        Assert.True(listed.Count(l => l.EndsWith("/indoor", StringComparison.Ordinal)) >= 12);
        Assert.True(listed.Count(l => l.EndsWith("/open_air", StringComparison.Ordinal)) >= 7);
    }

    // ---- the constraint checker on hand-edited and hand-built graphs ----------------------------

    /// <summary>Lua helpers for the checker tests: a generated graph, zone/link lookups and the violation text of one rule.</summary>
    private const string Helpers = """
        local function zone(g, id) for _, z in ipairs(g.zones) do if z.id == id then return z end end end
        local function drop_links(g, pred) local kept = {} for _, l in ipairs(g.links) do if not pred(l) then kept[#kept + 1] = l end end g.links = kept end
        local function rule(v, name)
          local out = {}
          for _, x in ipairs(v) do if x.rule == name then out[#out + 1] = (x.zone or x.link or "-") .. ": " .. x.message end end
          return table.concat(out, " | ")
        end
        -- A hand-built graph: halls 20 x 16 m in a row along x, 30 m apart, joined by corridors.
        local function row(n, opts)
          opts = opts or {}
          local g = { kind = "layout", gametype = opts.gametype or "dm", class = "standard", step = 5, size_class = "S", zones = {}, links = {}, objectives = {} }
          for i = 1, n do
            local y = opts.levels and opts.levels[i] or 0
            g.zones[i] = { id = "z" .. i, archetype = opts.arch and opts.arch[i] or "hall", role = opts.roles and opts.roles[i] or "room",
              pos = { (i - 1) * 30 - (n - 1) * 15, y, 0 }, size = { 20, 16 }, height = 7, levels = { 0 }, chamfer = 2, loops = 0, palette = { theme = "industrial" } }
          end
          for i = 1, n - 1 do
            local a, b = g.zones[i], g.zones[i + 1]
            local t = (math.abs(a.pos[2] - b.pos[2]) > 0.5) and (opts.climb or "stair") or "corridor"
            g.links[#g.links + 1] = { id = "l" .. i, type = t, a = a.id, b = b.id, a_y = a.pos[2], b_y = b.pos[2], width = 4 }
          end
          return g
        end
        """;

    private string Check(string body)
    {
        (_, ScriptRunResult r) = Run(Helpers + "\n" + body);
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue ?? string.Empty;
    }

    [Fact]
    public void Checker_Flags_Dead_Ends()
    {
        // A hand graph: a row of three halls, whose two end halls are dead ends.
        string row = Check("return rule(kit.check_layout{ graph = row(3) }, 'degree')");
        Assert.Contains("zone z1 has 1 walk link(s)", row, StringComparison.Ordinal);
        Assert.Contains("zone z3 has 1 walk link(s)", row, StringComparison.Ordinal);
        Assert.DoesNotContain("zone z2 has", row, StringComparison.Ordinal);
        Assert.Contains("no dead ends", row, StringComparison.Ordinal);
        // A generated graph cut down to one walk link at a zone that is not a reward nook.
        string v = Check(
            "local g = kit.layout{ gametype = 'dm', seed = 5, players = 12 }\n" +
            "local WALK = { corridor = true, doorway = true, stair = true, ramp = true, drop = true, bridge = true, lift = true, road = true, join = true }\n" +
            "local z = nil for _, c in ipairs(g.zones) do if not z and c.archetype ~= 'nook' then z = c end end\n" +
            "local kept = false\n" +
            "drop_links(g, function(l) if l.a == z.id or l.b == z.id then if kept or not WALK[l.type] then return true end kept = true end return false end)\n" +
            "return rule(kit.check_layout{ graph = g }, 'degree') .. ' @' .. z.id");
        string id = v[(v.LastIndexOf('@') + 1)..];
        Assert.Contains($"zone {id} has 1 walk link(s)", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Articulation_Loops_And_Single_Routes_Between_Hubs()
    {
        string v = Check(
            "local g = row(4, { roles = { 'hub', 'room', 'room', 'hub' }, arch = { 'hall', 'colonnade', 'storage', 'hall' }, levels = { 0, 5, 10, 5 } })\n" +
            "local V = kit.check_layout{ graph = g }\n" +
            "local all = {} for _, x in ipairs(V) do all[#all + 1] = x.rule .. ':' .. x.message end\n" +
            "return rule(V, 'articulation') .. ' ## ' .. rule(V, 'loops') .. ' ## ' .. rule(V, 'routes') .. ' ## ' .. table.concat(all, ' | ')");
        Assert.Contains("zone z2 is the only way to", v, StringComparison.Ordinal);
        Assert.Contains("add a second route around it", v, StringComparison.Ordinal);
        Assert.Contains("cycle rank", v, StringComparison.Ordinal);
        Assert.Contains("zones z1 and z4 (hubs/objectives) have 1 node-disjoint route(s); they need 2", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Flat_Layouts_And_Single_Kind_Climbs()
    {
        string flat = Check("return rule(kit.check_layout{ graph = row(3, { arch = { 'hall', 'colonnade', 'storage' } }) }, 'levels')");
        Assert.Contains("1 floor level(s)", flat, StringComparison.Ordinal);
        Assert.Contains("at least 3", flat, StringComparison.Ordinal);

        string tight = Check("return rule(kit.check_layout{ graph = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 3, 6 } }) }, 'levels')");
        Assert.Contains("are 3 m apart (storeys sit 4.5-10 m apart)", tight, StringComparison.Ordinal);

        string kinds = Check("return rule(kit.check_layout{ graph = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) }, 'vertical')");
        Assert.Contains("levels 0 and 5 are joined by 1 kind(s) of vertical link", kinds, StringComparison.Ordinal);
        Assert.Contains("levels 5 and 10", kinds, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Repeated_Archetypes_And_Mirror_Symmetry_Outside_Team_Modes()
    {
        string v = Check(
            "local g = row(4, { levels = { 0, 5, 5, 0 } })\n" +
            "local V = kit.check_layout{ graph = g }\n" +
            "return rule(V, 'variety') .. ' ## ' .. rule(V, 'symmetry')");
        Assert.Contains("archetype hall is used 4 times (at most 2)", v, StringComparison.Ordinal);
        Assert.Contains("three in a row", v, StringComparison.Ordinal);
        Assert.Contains("mirror symmetry is for team modes only", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Ctf_Lanes_Flag_Spacing_And_Base_Exits()
    {
        string v = Check(
            "local g = kit.layout{ gametype = 'ctf', seed = 4, players = 12 }\n" +
            "local red, blue\n" +
            "for _, o in ipairs(g.objectives) do if o.kind == 'flag' then if o.team == 'red' then red = o else blue = o end end end\n" +
            "local base = zone(g, red.zone)\n" +
            // the high and flank lanes lose their crossings of the centre zones (on the axis, of no team); the red base
            // keeps one exit. Joins stay: a join is part of two touching zones' shared outline, and without it the
            // zones overlap (a geometry error, which stops the checker before the design rules)
            "drop_links(g, function(l) local a, b = zone(g, l.a), zone(g, l.b) return l.type ~= 'join' and a.lane ~= 1 and b.lane ~= 1 and a.lane == b.lane and (a.team == nil or b.team == nil) end)\n" +
            "local kept = false\n" +
            "drop_links(g, function(l) if l.type ~= 'join' and (l.a == base.id or l.b == base.id) then if kept then return true end kept = true end return false end)\n" +
            "local V = kit.check_layout{ graph = g }\n" +
            "local lanes = rule(V, 'ctf')\n" +
            "blue.zone = g.zones[1].id == red.zone and g.zones[2].id or g.zones[1].id\n" +
            "for _, z in ipairs(g.zones) do local dx = z.pos[1] - base.pos[1] if z.id ~= red.zone and math.abs(dx) < 40 and math.abs(z.pos[3] - base.pos[3]) < 40 then blue.zone = z.id end end\n" +
            "return lanes .. ' ## ' .. rule(kit.check_layout{ graph = g }, 'ctf')");
        Assert.Contains("lane(s); CTF needs 3", v, StringComparison.Ordinal);
        Assert.Contains("exit(s) on", v, StringComparison.Ordinal);
        Assert.Contains("each base needs 3 exits on 2 heights", v, StringComparison.Ordinal);
        Assert.Contains("m apart (50-115 m)", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Koth_Approaches_And_A_Flat_Hill()
    {
        string v = Check(
            "local g = kit.layout{ gametype = 'koth', seed = 2, players = 12 }\n" +
            "local hill for _, o in ipairs(g.objectives) do if o.kind == 'hill' then hill = o.zone end end\n" +
            "local n = 0\n" +
            "drop_links(g, function(l) if l.a == hill or l.b == hill then n = n + 1 return n > 3 end return false end)\n" +
            "zone(g, hill).archetype = 'hall'\n" +
            "return rule(kit.check_layout{ graph = g }, 'koth')");
        Assert.Contains("has 3 approaches (5-8)", v, StringComparison.Ordinal);
        Assert.Contains("must be sunken or raised 3-5 m", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Payload_Structure()
    {
        string v = Check(
            "local g = kit.layout{ gametype = 'payload', seed = 2, players = 12 }\n" +
            "local cart for _, o in ipairs(g.objectives) do if o.kind == 'cart' then cart = o end end\n" +
            // boundary 1 keeps only the cart gate; the roads go flat and straight; the finale becomes a yard
            "drop_links(g, function(l) local a, b = zone(g, l.a), zone(g, l.b) return l.type ~= 'road' and a.segment ~= b.segment and math.min(a.segment, b.segment) == 1 and math.max(a.segment, b.segment) == 2 end)\n" +
            // the cart's spine without its roads: no climb, no turn, and it ends in a plain yard
            "local yards = {} for _, id in ipairs(cart.zones) do if zone(g, id).archetype == 'yard' then yards[#yards + 1] = id end end cart.zones = yards\n" +
            "for _, o in ipairs(g.objectives) do if o.kind == 'checkpoint' and o.index == 1 then o.spawns = {} end end\n" +
            "local V = kit.check_layout{ graph = g }\n" +
            "return rule(V, 'payload') .. ' ## ' .. rule(V, 'geometry')");
        Assert.Contains("the boundary after segment 1 has 1 entry route(s)", v, StringComparison.Ordinal);
        Assert.Contains("the cart track never climbs", v, StringComparison.Ordinal);
        Assert.Contains("the cart track never turns", v, StringComparison.Ordinal);
        Assert.Contains("fortified finale", v, StringComparison.Ordinal);
        Assert.Contains("checkpoint 1 must link an attacker and a defender spawn group", v, StringComparison.Ordinal);
        Assert.Contains("is linked to no checkpoint: Alpine never switches it off", v, StringComparison.Ordinal);

        string few = Check(
            "local g = kit.layout{ gametype = 'payload', seed = 3, players = 12 }\n" +
            "local kept = {} local n = 0\n" +
            "for _, o in ipairs(g.objectives) do if o.kind == 'checkpoint' then n = n + 1 if n <= 2 then kept[#kept + 1] = o end else kept[#kept + 1] = o end end\n" +
            "g.objectives = kept\n" +
            "return rule(kit.check_layout{ graph = g }, 'payload')");
        Assert.Contains("2 checkpoints; a payload layout has 3-5", few, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Flags_Open_Air_Links_And_The_Budget()
    {
        string v = Check(
            "local g = kit.layout{ gametype = 'dm', class = 'open_air', seed = 3, players = 8 }\n" +
            "local l = g.links[1] for _, x in ipairs(g.links) do if x.type == 'bridge' and x.a_y == x.b_y then l = x end end l.type = 'corridor'\n" +
            "g.budget.area = { 100, 200 }\n" +
            "local V = kit.check_layout{ graph = g }\n" +
            "return rule(V, 'open_air') .. ' ## ' .. rule(V, 'budget') .. ' @' .. l.id");
        string id = v[(v.LastIndexOf('@') + 1)..];
        Assert.Contains($"link {id}: platforms join by bridges, stairs or lifts, not corridors", v, StringComparison.Ordinal);
        Assert.Contains("is outside the S budget 100-200 m2", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Checker_Reports_Geometry_Errors_That_Stop_A_Build()
    {
        string overlap = Check("local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.zones[2].pos[1] = g.zones[1].pos[1] + 10\n" +
                               "local V = kit.check_layout{ graph = g } return V[1].severity .. ' ' .. rule(V, 'geometry')");
        Assert.StartsWith("error ", overlap, StringComparison.Ordinal);
        Assert.Contains("zones z1 and z2 overlap or stand closer than 1 m", overlap, StringComparison.Ordinal);

        string diagonal = Check("local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.zones[2].pos[3] = 40\n" +
                                "return rule(kit.check_layout{ graph = g }, 'geometry')");
        Assert.Contains("link l1: zones z1 and z2 cannot be joined: they are separated on both x and z (diagonal)", diagonal, StringComparison.Ordinal);

        string shortGap = Check("local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.zones[1].pos[1] = g.zones[1].pos[1] + 6\n" +
                                "return rule(kit.check_layout{ graph = g }, 'geometry')");
        Assert.Contains("link l1: the 4 m gap between z1 and z2 is too short for a stair", shortGap, StringComparison.Ordinal);

        string level = Check("local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.links[1].a_y = 2\n" +
                             "return rule(kit.check_layout{ graph = g }, 'level')");
        Assert.Contains("link l1: a_y 2 is not a port level of z1", level, StringComparison.Ordinal);

        string offset = Check("local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.links[2].offset = 7.5\n" +
                              "return rule(kit.check_layout{ graph = g }, 'geometry')");
        Assert.Contains("link l2: its offset 7.5 does not fit a free 4 m span on both walls", offset, StringComparison.Ordinal);

        string drop = Check("local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.links[1].type = 'drop'\n" +
                            "g.links[1].a, g.links[1].b, g.links[1].a_y, g.links[1].b_y = 'z2', 'z1', 5, 0\n" +
                            "return rule(kit.check_layout{ graph = g }, 'level')");
        Assert.Contains("link l1: z1 (height 7 m) is too low for a drop opening 5 m up", drop, StringComparison.Ordinal);

        string schema = Check("local g = row(2, { arch = { 'hall', 'atirum' } }) g.links[1].type = 'tube'\n" +
                              "return rule(kit.check_layout{ graph = g }, 'schema')");
        Assert.Contains("zone z2: unknown archetype 'atirum'", schema, StringComparison.Ordinal);
        Assert.Contains("link l1: unknown type 'tube'", schema, StringComparison.Ordinal);

        ScriptDiagnostic refused = KitTestHarness.Fail(Helpers + "\n" +
            "local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } }) g.zones[2].pos[3] = 40\n" +
            "kit.build_layout{ graph = g }");
        Assert.Contains("kit.build_layout: the layout has", refused.Message, StringComparison.Ordinal);
        Assert.Contains("kit.check_layout lists each violation", refused.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Refuses_Rule_Violations_Only_When_Strict()
    {
        // A two-zone corridor layout breaks rules (levels, loops) but has no errors: it builds, and refuses under strict.
        (EditorDocument doc, ScriptRunResult r) = Run(Helpers + "\n" +
            "local g = row(2, { arch = { 'hall', 'colonnade' } })\n" +
            "g.objectives = { { kind = 'spawns', id = 's1', zone = 'z1', count = 2 }, { kind = 'spawns', id = 's2', zone = 'z2', count = 2 } }\n" +
            "local rep = kit.build_layout{ graph = g, name = 'T' }\n" +
            "return #rep.zones .. ':' .. #rep.links .. ':' .. rep.spawns .. ':' .. #rep.warnings");
        string[] p = r.ReturnValue!.Split(':');
        Assert.Equal(new[] { "2", "1", "4" }, p.Take(3).ToArray());
        Assert.True(int.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Empty(Holes(doc, sharedBsp: true).Holes);
        ScriptDiagnostic strict = KitTestHarness.Fail(Helpers + "\n" + "kit.build_layout{ graph = row(2, { arch = { 'hall', 'colonnade' } }), strict = true }");
        Assert.Contains("design rule(s)", strict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Populate_Hook_Sees_Every_Built_Zone()
    {
        (_, ScriptRunResult r) = Run(Helpers + "\n" +
            "local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } })\n" +
            "local seen = {}\n" +
            "kit.build_layout{ graph = g, lights = false, populate = function(rep, z, graph) seen[#seen + 1] = rep.id .. '/' .. z.archetype .. '/' .. #rep.spots .. '/' .. tostring(graph == g) end }\n" +
            "return table.concat(seen, ',')");
        string[] seen = r.ReturnValue!.Split(',');
        Assert.Equal(3, seen.Length);
        Assert.StartsWith("z1/hall/", seen[0], StringComparison.Ordinal);
        Assert.All(seen, s => Assert.EndsWith("/true", s, StringComparison.Ordinal));
    }

    [Fact]
    public void Link_Rise_Comes_From_The_Zone_Floors_When_A_Link_Omits_Them()
    {
        // b_y alone: the a end is the zone's floor (13.5), so the rise is 4.5 m, not 18 m.
        string one = Check("local g = row(2, { arch = { 'hall', 'colonnade' }, levels = { 13.5, 18 } }) g.links[1].a_y = nil\n" +
                           "return rule(kit.check_layout{ graph = g }, 'geometry')");
        Assert.DoesNotContain("too short for a stair", one, StringComparison.Ordinal);
        // Neither given: the rise is the floors' 5 m, so a 4 m gap is too short (it passed every gap before).
        string none = Check("local g = row(2, { arch = { 'hall', 'colonnade' }, levels = { 0, 5 } }) g.links[1].a_y, g.links[1].b_y = nil, nil\n" +
                            "g.zones[1].pos[1] = g.zones[1].pos[1] + 6\n" +
                            "return rule(kit.check_layout{ graph = g }, 'geometry')");
        Assert.Contains("link l1: the 4 m gap between z1 and z2 is too short for a stair (needs 9.5 m)", none, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_Zones_Are_Kit_Rooms_By_Their_Names()
    {
        // Every zone kit.build_layout builds answers to its name (report.zones[i].name) wherever the kit takes a room name.
        (_, ScriptRunResult r) = Run(Helpers + "\n" +
            "local g = row(3, { arch = { 'hall', 'colonnade', 'storage' }, levels = { 0, 5, 10 } })\n" +
            "local rep = kit.build_layout{ graph = g, name = 'T', lights = false }\n" +
            "local names = {} for _, z in ipairs(rep.zones) do names[#names + 1] = z.name end\n" +
            "local d = kit.dress{ room = 'T_z2', seed = 1 }\n" +
            "local s = kit.spawn_points{ rooms = { names[1], names[3] }, count = 2 }\n" +
            "local it = kit.items{ rooms = names, seed = 1 }\n" +
            "local sp = d.spaces[1]\n" +
            "return table.concat(names, ',') .. '|' .. sp.name .. '|' .. string.format('%.0f', sp.area) .. '|' .. #s .. '|' .. tostring(it.count > 0) .. '|' .. tostring(d.count > 0)");
        string[] p = r.ReturnValue!.Split('|');
        Assert.Equal("T_z1,T_z2,T_z3", p[0]);
        Assert.Equal("T_z2", p[1]);
        Assert.True(double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) > 200, $"the dressed space is the whole 20 x 16 m zone: {r.ReturnValue}");
        Assert.Equal("2", p[3]);
        Assert.Equal("true", p[4]);
        Assert.Equal("true", p[5]);
    }

    /// <summary>
    /// A payload track on more than one heading: north up a graded road, a turn east in the first yard, east along a
    /// level road, across a chasm on a cart bridge, and up a graded road into a finale entered from the west.
    /// </summary>
    private const string TurningTrack = """
        local S = 4.5
        local function Z(id, arch, role, seg, x0, x1, z0, z1, y, params)
          return { id = id, archetype = arch, role = role, segment = seg, pos = { (x0 + x1) / 2, y, (z0 + z1) / 2 }, size = { x1 - x0, z1 - z0 },
            params = params, palette = { theme = "industrial", variant = 1 } }
        end
        local g = { kind = "layout", gametype = "payload", class = "payload", step = S, zones = {
            Z("start", "start", "spawn", 1, -10, 10, -8, 8, 0),
            Z("r1", "road", "span", 1, -5, 5, 9, 65, 0, { heading = "+z", rise = S, south = 12, north = 12 }),
            Z("y1", "yard", "checkpoint", 1, -16, 16, 66, 98, S),
            Z("r2", "road", "span", 2, 17, 57, 77, 87, S, { heading = "+x", south = 12, north = 12 }),
            Z("y2", "yard", "checkpoint", 2, 58, 90, 66, 98, S),
            Z("r3", "road", "span", 3, 110, 166, 77, 87, S, { heading = "+x", rise = S, south = 12, north = 12 }),
            Z("fin", "finale", "checkpoint", 3, 167, 207, 62, 102, 2 * S, { heading = "+x" }),
          }, links = {
            { type = "doorway", a = "start", b = "r1" }, { type = "road", a = "r1", b = "y1" }, { type = "road", a = "y1", b = "r2" },
            { type = "road", a = "r2", b = "y2" }, { type = "bridge", a = "y2", b = "r3", width = 8 }, { type = "road", a = "r3", b = "fin" },
          }, objectives = {
            { kind = "cart", zones = { "r1", "y1", "r2", "y2", "r3", "fin" }, attacking_team = "blue" },
            { kind = "spawns", id = "a", zone = "start", team = "blue", role = "attack", count = 2 },
            { kind = "spawns", id = "d", zone = "y2", team = "red", role = "defend", count = 2 },
            { kind = "checkpoint", index = 1, zone = "y1", spawns = { "a", "d" } },
            { kind = "checkpoint", index = 2, zone = "y2", spawns = { "a", "d" } },
            { kind = "checkpoint", index = 3, zone = "fin", spawns = { "a", "d" } },
          } }
        """;

    [Fact]
    public void Payload_Track_Turns_Onto_Any_Heading_And_Crosses_A_Chasm_Bridge()
    {
        (EditorDocument doc, ScriptRunResult r) = Run(Helpers + "\n" + TurningTrack + "\n" +
            "local V = kit.check_layout{ graph = g }\n" +
            "local errors = 0 for _, v in ipairs(V) do if v.severity == 'error' then errors = errors + 1 end end\n" +
            "local rep = kit.build_layout{ graph = g, name = 'P', lights = false }\n" +
            "local cart = level.find_mover('P_cart')\n" +
            "local kf = {} for _, k in ipairs(cart.keyframes) do kf[#kf + 1] = string.format('%.2f,%.2f,%.2f', k.pos.x, k.pos.y, k.pos.z) end\n" +
            "local cps = 0 for _, o in ipairs(rep.objectives) do if o.kind == 'checkpoint' then cps = cps + 1 end end\n" +
            "local chasm = level.brushes_tagged('kit.part', 'chasm').count\n" +
            "return errors .. '~~' .. rule(V, 'payload') .. '~~' .. cps .. '~~' .. chasm .. '~~' .. table.concat(kf, ';')");
        string[] p = r.ReturnValue!.Split("~~");
        Assert.Equal("0", p[0]);
        Assert.DoesNotContain("never turns", p[1], StringComparison.Ordinal);
        Assert.DoesNotContain("never climbs", p[1], StringComparison.Ordinal);
        Assert.DoesNotContain("the cart enters", p[1], StringComparison.Ordinal);
        Assert.DoesNotContain("the cart cannot go", p[1], StringComparison.Ordinal);
        Assert.Equal("3", p[2]);
        Assert.Equal("1", p[3]);
        List<double[]> kf = p[4].Split(';')
            .Select(k => k.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
        Assert.Equal(new[] { 0.0, 0.75, 10.0 }, kf[0]);                                 // in at the first road's south end
        int turn = kf.FindIndex(k => k[0] == 0 && k[2] == 82);                         // through the first yard's centre ...
        Assert.True(turn > 0, p[4]);
        Assert.True(kf.Skip(turn + 1).All(k => k[2] == 82 && k[0] > 0), p[4]);         // ... then east only, along z = 82
        Assert.DoesNotContain(kf, k => k[0] > 90 && k[0] < 110);                       // no keyframe over the chasm: it rolls across the deck
        Assert.Equal(9.75, kf.Max(k => k[1]), 2);                                      // up the last road to the finale floor (9 m + 0.75)

        (CompiledLevel shared, List<Vec3> holes) = Holes(doc, sharedBsp: true);
        Assert.True(holes.Count == 0, $"{holes.Count} hole(s) on the sealed Shared BSP build, e.g. " + string.Join("; ", holes.Take(4)));
        Assert.Empty(Holes(doc, sharedBsp: false).Holes);
        Assert.NotEmpty(shared.Geometry.Faces);

        // A road whose heading does not match the way the cart runs through it, and a chasm bridge outside payload.
        string wrong = Check(TurningTrack + "\n" + "g.zones[4].params.heading = '-x'\n" + "return rule(kit.check_layout{ graph = g }, 'payload')");
        Assert.Contains("the cart enters road r2 by its -x side, but a road heading -x is entered by its +x end", wrong, StringComparison.Ordinal);
        string dm = Check("local g = row(2, { arch = { 'hall', 'colonnade' } }) g.links[1].type = 'bridge'\n" + "return rule(kit.check_layout{ graph = g }, 'type')");
        Assert.Contains("bridges over a chasm are for payload layouts", dm, StringComparison.Ordinal);
    }

    // ---- one seeded layout per gametype x class: built, sealed, graded ---------------------------

    [Theory]
    [MemberData(nameof(AllCombos))]
    public void One_Layout_Per_Combination_Builds_Watertight_And_Passes_The_Flow_Checks(string gametype, string cls)
    {
        const int seed = 1;
        int players = Players(gametype, seed);
        HashSet<string> lintBefore = KitTestHarness.LintKeys(LevelTemplates.CreateNew(KitTestHarness.FixedDate));
        (EditorDocument doc, string summary) = Build(gametype, cls, seed, players);
        (CompiledLevel shared, List<Vec3> holes) = Holes(doc, sharedBsp: true);
        Assert.True(holes.Count == 0, $"{gametype}/{cls}: {holes.Count} hole(s) on the sealed Shared BSP build, e.g. " +
            string.Join("; ", holes.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
        (_, List<Vec3> holesIncremental) = Holes(doc, sharedBsp: false);
        Assert.True(holesIncremental.Count == 0, $"{gametype}/{cls}: {holesIncremental.Count} hole(s) on the sealed Incremental build");

        JsonObject grade = Grade(doc, shared, gametype, cls, players);
        List<string> failed = DesignGrader.FailedIds(grade);
        _out.WriteLine($"{gametype}/{cls} seed {seed}: {summary}; {grade["verdict"]} [{string.Join(",", failed)}] class {grade["class"]}");
        Assert.Equal(cls, grade["class"]!.GetValue<string>());
        foreach (string check in FlowChecks(gametype))
        {
            Assert.DoesNotContain(check, failed);
        }

        Assert.DoesNotContain("G24", failed);
        Assert.DoesNotContain("G25", failed);

        // No filter: the KOTH zone trigger (linked FROM its Capture_Point_Handler, as Alpine reads it) and the payload
        // checkpoints (named by cart keyframes) are patterns the linter knows.
        string[] added = KitTestHarness.LintKeys(doc).Where(k => !lintBefore.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0, "the layout introduced lint findings:\n" + string.Join("\n", added));
    }
}
