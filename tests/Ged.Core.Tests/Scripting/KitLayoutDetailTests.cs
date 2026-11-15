using System;
using System.Collections.Generic;
using System.Globalization;
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
/// kit.build_layout's room detail pass: it runs by default once the geometry stands (wall pilasters and trims, door
/// frames, ceiling beams, railings on the edges that drop away, stair finishes, rocks in rock spaces), detail = false
/// skips it and a table turns parts off; the same graph gives the same detail; spawn spots, doorways and bridge decks
/// stay clear; no beam hangs under open sky; and a pit zone is a chasm crossed by its bridge, with no stairs into it.
/// </summary>
public sealed class KitLayoutDetailTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutDetailTests(ITestOutputHelper output) => _out = output;

    /// <summary>The kit tags of the detail pass's parts.</summary>
    private const string DetailKits = "{ wall_detail = true, frame_opening = true, ceiling_detail = true, railing = true, stair_detail = true, rock_detail = true }";

    /// <summary>A small generated arena (4 players), built with the given detail option; returns "parts:brushes:by kit".</summary>
    private static (EditorDocument Doc, string Value) Arena(string detail)
    {
        (EditorDocument doc, ScriptRunResult r) = ArenaRun(detail);
        return (doc, r.ReturnValue ?? string.Empty);
    }

    private static (EditorDocument Doc, ScriptRunResult Result) ArenaRun(string detail) => Run(
        "local g = kit.layout{ gametype = 'dm', players = 4, seed = 7 }\n" +
        $"local rep = kit.build_layout{{ graph = g, name = 'A'{(detail.Length > 0 ? ", detail = " + detail : string.Empty)} }}\n" +
        $"local KITS = {DetailKits}\n" +
        "local n, by, untagged, solid = 0, {}, 0, 0\n" +
        "for _, b in ipairs(level.all_brushes().brushes) do\n" +
        "  local t = b:tags()\n" +
        "  if KITS[t.kit] then\n" +
        "    n = n + 1\n" +
        "    by[t.kit] = (by[t.kit] or 0) + 1\n" +
        "    if t['kit.zone'] == nil or t['kit.role'] == nil then untagged = untagged + 1 end\n" +
        "    if not b.detail then solid = solid + 1 end\n" +
        "  end\n" +
        "end\n" +
        "local keys = {}\n" +
        "for k, v in pairs(by) do keys[#keys + 1] = k .. '=' .. v end\n" +
        "table.sort(keys)\n" +
        "return table.concat({ rep.detail and rep.detail.parts or -1, n, untagged, solid, table.concat(keys, ',') }, '|')");

    [Fact]
    public void Detail_Runs_By_Default_And_Detail_False_Or_A_Parts_Table_Turns_It_Off()
    {
        (_, string on) = Arena(string.Empty);
        _out.WriteLine("default: " + on);
        string[] p = on.Split('|');
        Assert.True(int.Parse(p[0], CultureInfo.InvariantCulture) > 0, on);   // the report counts the parts
        Assert.True(int.Parse(p[1], CultureInfo.InvariantCulture) > 0, on);   // and they are in the level
        Assert.Equal("0", p[2]);                                              // every part carries its zone and role
        Assert.Equal("0", p[3]);                                              // every part is detail (never seals or splits rooms)
        Assert.Contains("wall_detail=", p[4], StringComparison.Ordinal);
        Assert.Contains("frame_opening=", p[4], StringComparison.Ordinal);

        (_, string off) = Arena("false");
        Assert.Equal("-1|0|0|0|", off);

        (_, string some) = Arena("{ walls = false, ceilings = false }");
        _out.WriteLine("walls and ceilings off: " + some);
        Assert.DoesNotContain("wall_detail=", some, StringComparison.Ordinal);
        Assert.DoesNotContain("ceiling_detail=", some, StringComparison.Ordinal);
        Assert.Contains("frame_opening=", some, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_Option_Is_Checked()
    {
        ScriptDiagnostic bad = KitTestHarness.Fail("local g = kit.layout{ gametype = 'dm', players = 4, seed = 7 }\nkit.build_layout{ graph = g, detail = { wals = false } }");
        Assert.Contains("wals", bad.Message, StringComparison.Ordinal);
        ScriptDiagnostic style = KitTestHarness.Fail("local g = kit.layout{ gametype = 'dm', players = 4, seed = 7 }\nkit.build_layout{ graph = g, detail = { style = 'gothic' } }");
        Assert.Contains("unknown style 'gothic'", style.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_Is_Deterministic_For_A_Graph()
    {
        const string body =
            "local g = kit.layout{ gametype = 'dm', players = 4, seed = 7 }\n" +
            "kit.build_layout{ graph = g, name = 'A' }\n" +
            "local KITS = " + DetailKits + "\n" +
            "local out = {}\n" +
            "for _, b in ipairs(level.all_brushes().brushes) do\n" +
            "  local t = b:tags()\n" +
            "  if KITS[t.kit] then\n" +
            "    local bb = b.bounds\n" +
            "    out[#out + 1] = string.format('%s %s %.3f %.3f %.3f %.3f %.3f %.3f', t.kit, t['kit.module'] or '', bb.min.x, bb.min.y, bb.min.z, bb.max.x, bb.max.y, bb.max.z)\n" +
            "  end\n" +
            "end\n" +
            "return table.concat(out, '\\n')";
        (_, ScriptRunResult a) = Run(body);
        (_, ScriptRunResult b) = Run(body);
        Assert.False(string.IsNullOrEmpty(a.ReturnValue));
        Assert.Equal(a.ReturnValue, b.ReturnValue);
    }

    [Fact]
    public void Detail_Keeps_Spawns_And_Doorways_Clear()
    {
        (_, ScriptRunResult r) = Run(
            "local g = kit.layout{ gametype = 'dm', players = 4, seed = 7 }\n" +
            "kit.build_layout{ graph = g, name = 'A' }\n" +
            "local KITS = " + DetailKits + "\n" +
            "local parts = {}\n" +
            "for _, b in ipairs(level.all_brushes().brushes) do if KITS[b:tags().kit] then parts[#parts + 1] = b end end\n" +
            // every spawn stands at least half a metre clear of every part (the point tests of the spawn's body)
            "local near, what = 0, ''\n" +
            "for _, s in ipairs(level.objects_of('respawn')) do\n" +
            "  local p = s.pos\n" +
            "  for _, b in ipairs(parts) do\n" +
            "    for _, d in ipairs({ { 0, 0 }, { 0.5, 0 }, { -0.5, 0 }, { 0, 0.5 }, { 0, -0.5 } }) do\n" +
            "      for _, dy in ipairs({ -0.9, 0, 0.8 }) do\n" +
            "        if b:contains(p.x + d[1], p.y + dy, p.z + d[2]) then near = near + 1 what = s.name .. ' in ' .. (b:tags()['kit.module'] or '?') end\n" +
            "      end\n" +
            "    end\n" +
            "  end\n" +
            "end\n" +
            // every framed opening is still open through its middle (knee and chest height above its sill)
            "local pr = ged.import('kit_probe').shared()\n" +
            "local seen, blocked, frames = {}, 0, 0\n" +
            "for _, b in ipairs(level.brushes_tagged('kit', 'frame_opening').brushes) do\n" +
            "  local key = b:tags()['kit.opening']\n" +
            "  if key and not seen[key] then\n" +
            "    seen[key] = true\n" +
            "    frames = frames + 1\n" +
            "    local x, y, z = key:match('^([^,]+),([^,]+),([^,]+)$')\n" +
            "    x, y, z = tonumber(x), tonumber(y), tonumber(z)\n" +
            "    if not (pr:air_exact(x, y + 0.6, z) and pr:air_exact(x, y + 1.3, z)) then blocked = blocked + 1 what = what .. ' blocked ' .. key .. ' ' .. (b:tags()['kit.module'] or '?') end\n" +
            "  end\n" +
            "end\n" +
            "return near .. '|' .. frames .. '|' .. blocked .. '|' .. what");
        string[] p = r.ReturnValue!.Split('|');
        Assert.True(p[0] == "0", r.ReturnValue);
        Assert.True(int.Parse(p[1], CultureInfo.InvariantCulture) > 0, r.ReturnValue);
        Assert.True(p[2] == "0", r.ReturnValue);
    }

    /// <summary>A hand graph: a pit room between a hall and a courtyard under the sky, joined by corridors.</summary>
    private const string PitGraph =
        "local g = { kind = 'layout', gametype = 'dm', class = 'standard', step = 5, zones = {\n" +
        "    { id = 'pit', archetype = 'pit', pos = { 0, 0, 0 }, size = { 26, 26 }, palette = { theme = 'industrial', variant = 1 } },\n" +
        "    { id = 'west', archetype = 'hall', pos = { -27, 0, 0 }, size = { 20, 18 }, palette = { theme = 'bunker', variant = 1 } },\n" +
        "    { id = 'east', archetype = 'courtyard', pos = { 28, 0, 0 }, size = { 22, 20 }, palette = { theme = 'admin', variant = 1 } },\n" +
        "  }, links = { { type = 'corridor', a = 'west', b = 'pit' }, { type = 'corridor', a = 'pit', b = 'east' } },\n" +
        "  objectives = { { kind = 'spawns', id = 's', zone = 'west', count = 2 }, { kind = 'spawns', id = 't', zone = 'east', count = 2 } } }\n";

    [Fact]
    public void No_Beam_Hangs_Under_Open_Sky_And_A_Pit_Has_No_Stairs()
    {
        (_, ScriptRunResult r) = Run(PitGraph +
            "local rep = kit.build_layout{ graph = g, name = 'H' }\n" +
            "local sky = {}\n" +
            "for _, z in ipairs(rep.zones) do sky[z.name] = z.sky end\n" +
            "local under_sky, beams, flush = 0, 0, 0\n" +
            "local pr = ged.import('kit_probe').shared()\n" +
            "for _, b in ipairs(level.brushes_tagged('kit', 'ceiling_detail').brushes) do\n" +
            "  beams = beams + 1\n" +
            "  if sky[b:tags()['kit.zone']] then under_sky = under_sky + 1 end\n" +
            "end\n" +
            // the pit: no flight in it, its chasm is no floor of the zone, the deck and rim are railed
            "local pit_stairs = 0\n" +
            "for _, b in ipairs(level.brushes_tagged('kit', 'stairs').brushes) do if (b:tags()['kit.name'] or ''):find('H_pit', 1, true) then pit_stairs = pit_stairs + 1 end end\n" +
            "local pit\n" +
            "for _, z in ipairs(rep.zones) do if z.id == 'pit' then pit = z end end\n" +
            "local rails = 0\n" +
            "for _, b in ipairs(level.brushes_tagged('kit', 'railing').brushes) do if b:tags()['kit.zone'] == 'H_pit' then rails = rails + 1 end end\n" +
            "return table.concat({ beams, under_sky, pit_stairs, pit.min.y, rails, tostring(sky.H_east) }, '|')");
        _out.WriteLine(r.ReturnValue);
        string[] p = r.ReturnValue!.Split('|');
        Assert.True(int.Parse(p[0], CultureInfo.InvariantCulture) > 0, r.ReturnValue); // the roofed rooms got beams
        Assert.Equal("0", p[1]);    // none in the courtyard under the sky
        Assert.Equal("0", p[2]);    // no stairs into the pit
        Assert.Equal("0", p[3]);    // the zone's floor is its rim, not the chasm's bottom
        Assert.True(int.Parse(p[4], CultureInfo.InvariantCulture) > 0, r.ReturnValue); // the rim and the deck are railed
        Assert.Equal("true", p[5]);
    }

    [Fact]
    public void Space_Members_Follow_The_Room_Material_And_The_Pit_Room_Ledge_Leads_On()
    {
        (_, ScriptRunResult r) = Run(
            "local function textures_of()\n" +
            "  local t = {}\n" +
            "  for _, b in ipairs(level.all_brushes().brushes) do for _, f in ipairs(b.faces) do t[f.texture] = true end end\n" +
            "  return t\n" +
            "end\n" +
            "kit.space{ archetype = 'storage', name = 'st', pos = { 0, 0, 0 }, textures = { floor = 'cem_dark_floor01.tga', wall = 'cem_bunker04.tga', ceiling = 'cem_cementflr01.tga' } }\n" +
            "local a = textures_of()\n" +
            "kit.space{ archetype = 'storage', name = 'st2', pos = { 0, 0, 60 }, textures = { floor = 'sld_adminfloor01.tga', wall = 'sld_admin_new01.tga', ceiling = 'sld_admin_ceiling01.tga' } }\n" +
            "local b = textures_of()\n" +
            "local p = kit.space{ archetype = 'pit_room', name = 'pr', pos = { 0, 0, 120 } }\n" +
            "local upper = 0\n" +
            "for _, an in ipairs(p.anchors) do if an.pos.y > 3.9 and an.pos.y < 4.1 and an.dir == '+z' then upper = upper + 1 end end\n" +
            "return table.concat({ tostring(a['mtl_girder03.tga']), tostring(b['mtl_chromegirder01.tga']), tostring(b['mtl_girder06.tga'] or false), upper }, '|')");
        Assert.Equal("true|true|false|1", r.ReturnValue);
    }
}

/// <summary>The detail pass against the design grader: the pit recipe passes G30 and G31, and on a generated level the
/// detail keeps the sealed build watertight and fails no check the same layout without detail passes.</summary>
[Trait("Category", "DeepGate")]
public sealed class KitLayoutDetailGateTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutDetailGateTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Pit_Recipe_Passes_The_Flight_And_Ledge_Checks()
    {
        (EditorDocument doc, _) = Run(
            "local g = { kind = 'layout', gametype = 'dm', class = 'standard', step = 5, zones = {\n" +
            "    { id = 'pit', archetype = 'pit', pos = { 0, 0, 0 }, size = { 26, 26 }, palette = { theme = 'industrial', variant = 1 } },\n" +
            "    { id = 'west', archetype = 'hall', pos = { -27, 0, 0 }, size = { 20, 18 }, palette = { theme = 'bunker', variant = 1 } },\n" +
            "    { id = 'east', archetype = 'courtyard', pos = { 28, 0, 0 }, size = { 22, 20 }, palette = { theme = 'admin', variant = 1 } },\n" +
            "  }, links = { { type = 'corridor', a = 'west', b = 'pit' }, { type = 'corridor', a = 'pit', b = 'east' } },\n" +
            "  objectives = { { kind = 'spawns', id = 's', zone = 'west', count = 2 }, { kind = 'spawns', id = 't', zone = 'east', count = 2 } } }\n" +
            "kit.build_layout{ graph = g, name = 'H' }");
        (CompiledLevel shared, List<Vec3> holes) = Holes(doc, sharedBsp: true);
        Assert.Empty(holes);
        JsonObject grade = Grade(doc, shared, "dm", "standard", 4);
        List<string> failed = DesignGrader.FailedIds(grade);
        _out.WriteLine(string.Join(",", failed));
        Assert.DoesNotContain("G30", failed);
        Assert.DoesNotContain("G31", failed);
    }

    [Fact]
    public void Detail_Keeps_A_Level_Watertight_And_Fails_No_New_Check()
    {
        var failed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string detail in new[] { "false", "true" })
        {
            (EditorDocument doc, _) = Run($"local g = {LayoutCall("dm", "standard", 1, 8)}\nkit.build_layout{{ graph = g, name = 'L', detail = {detail} }}");
            (CompiledLevel shared, List<Vec3> holes) = Holes(doc, sharedBsp: true);
            Assert.True(holes.Count == 0, $"detail = {detail}: {holes.Count} hole(s)");
            failed[detail] = DesignGrader.FailedIds(Grade(doc, shared, "dm", "standard", 8));
            _out.WriteLine($"detail = {detail}: [{string.Join(",", failed[detail])}]");
        }

        string[] added = failed["true"].Except(failed["false"], StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0, "checks that fail only with the detail pass: " + string.Join(",", added));
        Assert.DoesNotContain("G30", failed["true"]);
        Assert.DoesNotContain("G31", failed["true"]);
    }
}
