using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitLayoutHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Free-form layouts: zones turned by <c>rot</c> (built in their own frame and turned into place about the world
/// origin), traced <c>poly</c> zones, free links (any heading, bends, stairs and ramps on their longest straight,
/// chasm and open-air bridges) and joins. Every construct builds sealed under BOTH build methods; the checker names
/// the link or zone of every free-form geometry error; population by zone name works on turned and traced zones.
/// </summary>
public sealed class KitLayoutFreeFormTests
{
    private readonly ITestOutputHelper _out;

    public KitLayoutFreeFormTests(ITestOutputHelper output) => _out = output;

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Every brush's world vertices, rounded to the millimetre, sorted: a fingerprint of the geometry.</summary>
    private static List<string> VertexSets(EditorDocument doc, double turnDeg = 0)
    {
        double r = turnDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        var sets = new List<string>();
        foreach (Brush b in new BrushEditor(doc).Brushes)
        {
            var pts = new List<string>();
            for (int i = 0; i < b.Geometry.Vertices.Count; i++)
            {
                Vec3 w = BrushTransform.WorldVertex(b, b.Geometry.Vertices[i]);
                // world -> the frame turned back by turnDeg (x' = x c - z s, z' = x s + z c)
                double x = w.X * c - w.Z * s, z = w.X * s + w.Z * c;
                pts.Add(string.Create(CultureInfo.InvariantCulture, $"{Math.Round(x * 100) / 100:0.00},{Math.Round(w.Y * 100) / 100:0.00},{Math.Round(z * 100) / 100:0.00}"));
            }

            pts.Sort(StringComparer.Ordinal);
            sets.Add(string.Join(";", pts.Distinct()));
        }

        sets.Sort(StringComparer.Ordinal);
        return sets;
    }

    /// <summary>Every brush's vertex count and the centroid of its world vertices, turned back by turnDeg about the origin.</summary>
    private static List<(Vec3 C, int N)> Centroids(EditorDocument doc, double turnDeg)
    {
        double r = turnDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        var list = new List<(Vec3, int)>();
        foreach (Brush b in new BrushEditor(doc).Brushes)
        {
            double x = 0, y = 0, z = 0;
            int n = b.Geometry.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                Vec3 w = BrushTransform.WorldVertex(b, b.Geometry.Vertices[i]);
                x += w.X * c - w.Z * s;
                y += w.Y;
                z += w.X * s + w.Z * c;
            }

            list.Add((new Vec3((float)(x / n), (float)(y / n), (float)(z / n)), n));
        }

        return list;
    }

    private void AssertSealed(EditorDocument doc, string what)
    {
        (CompiledLevel shared, List<Vec3> holes) = Holes(doc, sharedBsp: true);
        Assert.True(holes.Count == 0, $"{what}: {holes.Count} hole(s) on the sealed Shared BSP build, e.g. " +
            string.Join("; ", holes.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
        Assert.NotEmpty(shared.Geometry.Faces);
        (_, List<Vec3> inc) = Holes(doc, sharedBsp: false);
        Assert.True(inc.Count == 0, $"{what}: {inc.Count} hole(s) on the sealed Incremental build, e.g. " +
            string.Join("; ", inc.Take(4).Select(h => $"({Fmt(h.X)}, {Fmt(h.Y)}, {Fmt(h.Z)})")));
    }

    private static void AssertLintClean(EditorDocument doc)
    {
        HashSet<string> before = KitTestHarness.LintKeys(LevelTemplates.CreateNew(KitTestHarness.FixedDate));
        string[] added = KitTestHarness.LintKeys(doc).Where(k => !before.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0, "the layout introduced lint findings:\n" + string.Join("\n", added));
    }

    /// <summary>A generated DM layout, optionally with every zone turned by deg about the world origin.</summary>
    private static string TurnedDm(double deg) => FormattableString.Invariant($$"""
        local g = kit.layout{ gametype = "dm", players = 8, seed = 3 }
        local ROT = {{deg}}
        if ROT ~= 0 then
          local c, s = math.cos(math.rad(ROT)), math.sin(math.rad(ROT))
          local function turn(q) if q then local x, zz = q[1], q[2] q[1], q[2] = x * c + zz * s, -x * s + zz * c end end
          for _, z in ipairs(g.zones) do
            local x, zz = z.pos[1], z.pos[3]
            z.pos = { x * c + zz * s, z.pos[2], -x * s + zz * c }
            -- a traced zone turns its outline (world points); a rectangle zone adds to its rot
            if z.archetype == "poly" then for _, q in ipairs(z.outline) do turn(q) end else z.rot = (z.rot or 0) + ROT end
          end
          -- the routes written into free links (world points) turn with the plan
          for _, l in ipairs(g.links) do
            turn(l.a_at) turn(l.b_at)
            for _, q in ipairs(l.path or {}) do turn(q) end
          end
        end
        """);

    // ---- zone.rot -----------------------------------------------------------------------------------

    [Fact]
    public void Rot_Zero_Builds_Exactly_Like_A_Graph_Without_Rot()
    {
        (EditorDocument a, ScriptRunResult ra) = Run(TurnedDm(0) + "\nlocal rep = kit.build_layout{ graph = g, name = 'L' }\nreturn level.all_brushes().count");
        // (zones without a rot get rot = 0; a generated wing keeps its own turn)
        (EditorDocument b, ScriptRunResult rb) = Run(TurnedDm(0) + "\nfor _, z in ipairs(g.zones) do if z.archetype ~= 'poly' then z.rot = z.rot or 0 end end\nlocal rep = kit.build_layout{ graph = g, name = 'L' }\nreturn level.all_brushes().count");
        Assert.Equal(ra.ReturnValue, rb.ReturnValue);
        Assert.Equal(VertexSets(a), VertexSets(b));
    }

    [Fact]
    [Trait("Category", "DeepGate")]
    public void A_Layout_Turned_By_Rot_Builds_The_Same_Brushes_Sealed()
    {
        const string build = "\nlocal V = kit.check_layout{ graph = g }\nlocal e = 0 for _, v in ipairs(V) do if v.severity == 'error' then e = e + 1 end end\n" +
            // (the zones' and links' own brushes: the detail pass reads the built world, where the turned build's sky box and
            // neighbours stand elsewhere, so its parts are compared by the detail tests, not brush for brush here)
            "local rep = kit.build_layout{ graph = g, name = 'L', detail = false }\nreturn e .. '|' .. level.all_brushes().count .. '|' .. rep.spawns";
        (EditorDocument flat, ScriptRunResult rf) = Run(TurnedDm(0) + build);
        (EditorDocument turned, ScriptRunResult rt) = Run(TurnedDm(30) + build);
        _out.WriteLine($"rot 0: {rf.ReturnValue}; rot 30: {rt.ReturnValue}");
        Assert.StartsWith("0|", rt.ReturnValue, StringComparison.Ordinal);
        Assert.Equal(rf.ReturnValue, rt.ReturnValue); // same brush count and spawns
        // every brush of the turned build is the flat build's brush turned by 30 degrees
        List<(Vec3 C, int N)> a = Centroids(flat, 0), b = Centroids(turned, 30);
        // (0.05 m: a traced zone's outline turns in world coordinates and snaps back to the 1/16 m grid)
        int same = a.Count(x => b.Any(y => y.N == x.N && Math.Abs(y.C.X - x.C.X) < 0.05 && Math.Abs(y.C.Y - x.C.Y) < 0.05 && Math.Abs(y.C.Z - x.C.Z) < 0.05));
        // (the sky box is the one brush placed by the level's bounds, not by a zone)
        Assert.True(same >= a.Count - 1, $"only {same} of {a.Count} brushes match the flat build turned by 30 degrees");
        AssertSealed(turned, "rot 30");
    }

    // ---- the mixed graph --------------------------------------------------------------------------

    /// <summary>
    /// A rot-0 body (hall, storage), a traced bay joined to the hall, a wing of two zones at 45 degrees (a colonnade and
    /// a courtyard a storey up joined by a classic stair in their own frame), a free corridor from the hall to the wing
    /// (auto-routed), two traced zones a storey up (an L-shaped loft and a terrace) joined by a free doorway across a
    /// 1 m wall, a free stair from the storage up to the loft, a free ramp from the bay up to the terrace, and a free
    /// corridor with an explicit bending path from the terrace to the courtyard.
    /// </summary>
    internal const string Mixed = """
        local function W45(lx, lz) local c = math.cos(math.rad(45)) return { lx * c + lz * c, 0, -lx * c + lz * c } end
        local function Z(id, arch, pos, size, rot, extra)
          local z = { id = id, archetype = arch, pos = pos, size = size, rot = rot, palette = { theme = "industrial", variant = 1 } }
          for k, v in pairs(extra or {}) do z[k] = v end
          return z
        end
        local w1 = W45(30, 20)
        local w2 = W45(30, 47) w2[2] = 5
        local g = { kind = "layout", gametype = "dm", class = "standard", step = 5, zones = {
            Z("hall", "hall", { 0, 0, 0 }, { 24, 18 }, nil, { role = "hub" }),
            Z("store", "storage", { -34, 0, 0 }, { 20, 16 }),
            { id = "bay", archetype = "poly", pos = { nil, 0, nil }, outline = { { -6, 9 }, { 6, 9 }, { 4, 15 }, { -4, 15 } } },
            Z("wing", "colonnade", w1, { 22, 16 }, 45),
            Z("court", "courtyard", w2, { 20, 18 }, 45),
            { id = "terrace", archetype = "poly", pos = { nil, 5, nil }, outline = { { -20, 28 }, { 4, 28 }, { 8, 36 }, { 4, 44 }, { -20, 44 } } },
            { id = "loft", archetype = "poly", pos = { nil, 5, nil }, outline = { { -45, 18 }, { -21, 18 }, { -21, 44 }, { -33, 44 }, { -33, 30 }, { -45, 30 } } },
          }, links = {
            { type = "corridor", a = "hall", b = "store" },
            { type = "join", a = "hall", b = "bay" },
            { type = "corridor", a = "hall", b = "wing", a_side = "+x" },
            { type = "stair", a = "wing", b = "court" },
            { type = "stair", a = "store", b = "loft" },
            { type = "doorway", a = "loft", b = "terrace" },
            { type = "ramp", a = "bay", b = "terrace" },
            { type = "corridor", a = "terrace", b = "court", path = { { 24, 26 }, { 36, 26 } } },
          }, objectives = {
            { kind = "spawns", id = "s1", zone = "hall", count = 2 }, { kind = "spawns", id = "s2", zone = "store", count = 2 },
            { kind = "spawns", id = "s3", zone = "loft", count = 2 }, { kind = "spawns", id = "s4", zone = "court", count = 2 },
          } }
        """;

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Mixed_Free_Form_Graph_Builds_Sealed_Lint_Clean_And_Walkable()
    {
        (EditorDocument doc, ScriptRunResult r) = Run(Mixed + """

            local V = kit.check_layout{ graph = g }
            local errs = {}
            for _, v in ipairs(V) do if v.severity == "error" then errs[#errs + 1] = v.message end end
            local rep = kit.build_layout{ graph = g, name = "M" }
            local modes = {}
            for _, l in ipairs(rep.links) do modes[#modes + 1] = l.id .. "=" .. l.mode .. (l.path and ("/" .. (#l.path - 2)) or "") end
            ops.build()
            local sp = level.objects_of("respawn")[1]
            local w = spatial.walk{ from = { sp.pos.x, sp.pos.y - 1, sp.pos.z } }
            return #errs .. "~" .. table.concat(errs, " | ") .. "~" .. table.concat(modes, ",") .. "~" .. rep.spawns .. "~" .. #w.unreachable_spawns
            """);
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.True(p[0] == "0", "check errors: " + p[1]);
        Assert.Contains("l1=classic", p[2], StringComparison.Ordinal);
        Assert.Contains("l2=join", p[2], StringComparison.Ordinal);
        Assert.Contains("l3=free", p[2], StringComparison.Ordinal);
        Assert.Contains("l4=classic", p[2], StringComparison.Ordinal);
        Assert.Contains("l8=free/", p[2], StringComparison.Ordinal);
        Assert.Equal("8", p[3]);
        Assert.Equal("0", p[4]);
        AssertSealed(doc, "mixed graph");
        AssertLintClean(doc);
    }

    [Fact]
    public void Building_The_Same_Graph_Twice_Gives_Identical_Brushes()
    {
        const string body = "\nkit.build_layout{ graph = g, name = 'M', lights = false }\nreturn level.all_brushes().count";
        (EditorDocument a, _) = Run(Mixed + body);
        (EditorDocument b, _) = Run(Mixed + body);
        Assert.Equal(VertexSets(a), VertexSets(b));
    }

    // ---- the checker on free-form geometry -------------------------------------------------------------

    private string Check(string edit)
    {
        (_, ScriptRunResult r) = Run(Mixed + "\n" + edit + """

            local out = {}
            for _, v in ipairs(kit.check_layout{ graph = g }) do if v.severity == "error" then out[#out + 1] = v.rule .. ": " .. v.message end end
            return table.concat(out, " | ")
            """);
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue ?? string.Empty;
    }

    [Fact]
    public void Checker_Names_Free_Form_Geometry_Errors()
    {
        // a traced zone laid over a rectangle zone, and a turned zone standing too close to another
        string overlap = Check("g.zones[3].outline = { { -6, 8 }, { 6, 8 }, { 4, 15 }, { -4, 15 } } g.links[2].type = 'corridor' g.links[2].path = {}");
        Assert.Contains("zones hall and bay overlap or stand closer than 1 m", overlap, StringComparison.Ordinal);
        string close = Check("g.zones[4].pos = { g.zones[4].pos[1] - 10, 0, g.zones[4].pos[3] }");
        Assert.Contains("zones hall and wing overlap or stand closer than 1 m", close, StringComparison.Ordinal);
        // a path through another zone, and a turn over 90 degrees
        string through = Check("g.links[8].path = { { 0, 36 }, { 30, 36 } }");
        Assert.Contains("link l8: its path from terrace to court does not work", through, StringComparison.Ordinal);
        string sharp = Check("g.links[8].path = { { 24, 26 }, { 36, 26 }, { 30, 34 } }");
        Assert.Contains("degrees at path point", sharp, StringComparison.Ordinal);
        Assert.Contains("at most 90", sharp, StringComparison.Ordinal);
        // a drop cannot be free, and a join needs zones that touch
        string drop = Check("g.links[5].type = 'drop' g.links[5].a, g.links[5].b = 'loft', 'store'");
        Assert.Contains("link l5: a drop cannot be a free link", drop, StringComparison.Ordinal);
        string apart = Check("g.zones[3].outline = { { -6, 11 }, { 6, 11 }, { 4, 17 }, { -4, 17 } }");
        Assert.Contains("link l2: zones hall and bay do not touch or overlap along one wall", apart, StringComparison.Ordinal);
        // schema: a poly zone without a usable outline, a rot that is not a number
        string schema = Check("g.zones[3].outline = { { 0, 0 }, { 4, 0 } } g.zones[2].rot = 'east'");
        Assert.Contains("zone bay (poly) needs outline = 3 to 64 world points", schema, StringComparison.Ordinal);
        Assert.Contains("zone store: rot must be a yaw in degrees", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_Draws_Turned_And_Traced_Zones_And_Free_Routes()
    {
        (_, ScriptRunResult r) = Run(Mixed + "\nlocal p = kit.layout_preview{ graph = g }\nreturn p.text .. '\\n@@' .. tostring(p.svg:find('<polygon') ~= nil)");
        string text = r.ReturnValue!;
        _out.WriteLine(text);
        Assert.Contains("wing colonnade 22x16 m, rot 45", text, StringComparison.Ordinal);
        Assert.Contains("loft poly 6-point outline", text, StringComparison.Ordinal);
        Assert.Contains("l8 corridor terrace-court", text, StringComparison.Ordinal);
        Assert.Contains(", free, ", text, StringComparison.Ordinal);
        Assert.Contains("l2 join hall-bay", text, StringComparison.Ordinal);
        Assert.EndsWith("@@true", text, StringComparison.Ordinal);
    }

    // ---- population hooks on turned and traced zones -------------------------------------------------------

    [Fact]
    public void Population_By_Zone_Name_Works_On_Turned_And_Traced_Zones()
    {
        (_, ScriptRunResult r) = Run(Mixed + """

            local rep = kit.build_layout{ graph = g, name = "M", lights = false }
            local zr = {}
            for _, z in ipairs(rep.zones) do zr[z.id] = z end
            local function inside(poly, x, z)
              local c, j = false, #poly
              for i = 1, #poly do
                local a, b = poly[i], poly[j]
                if ((a[2] > z) ~= (b[2] > z)) and (x < (b[1] - a[1]) * (z - a[2]) / (b[2] - a[2]) + a[1]) then c = not c end
                j = i
              end
              return c
            end
            local out = {}
            for _, id in ipairs({ "wing", "loft" }) do
              local z = zr[id]
              local d = kit.dress{ room = z.name, seed = 1 }
              local s = kit.spawn_points{ rooms = { z.name }, count = 3 }
              local it = kit.items{ rooms = { z.name }, seed = 1 }
              local n, ins = 0, 0
              for _, list in ipairs({ d.objects or {}, it.items or {} }) do
                for _, o in ipairs(list) do
                  if o.pos then
                    n = n + 1
                    if inside(z.outline, o.pos.x, o.pos.z) then ins = ins + 1 end
                  end
                end
              end
              local sin = 0 for _, p in ipairs(s) do if inside(z.outline, p.pos.x, p.pos.z) then sin = sin + 1 end end
              out[#out + 1] = id .. ":" .. d.count .. ":" .. #s .. ":" .. sin .. ":" .. it.count .. ":" .. n .. ":" .. ins
            end
            return table.concat(out, ",")
            """);
        _out.WriteLine(r.ReturnValue);
        foreach (string z in r.ReturnValue!.Split(','))
        {
            string[] p = z.Split(':');
            Assert.True(int.Parse(p[1], CultureInfo.InvariantCulture) > 0, $"{p[0]}: nothing dressed ({z})");
            Assert.True(int.Parse(p[2], CultureInfo.InvariantCulture) > 0, $"{p[0]}: no spawn points ({z})");
            Assert.True(p[2] == p[3], $"{p[0]}: spawn points outside the zone's world outline ({z})");
            Assert.True(int.Parse(p[4], CultureInfo.InvariantCulture) > 0, $"{p[0]}: no items ({z})");
        }
    }

    // ---- traced rooms at primary-room sizes ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Traced_Rooms_Convex_And_Not_Build_Sealed_Lit_And_Articulated()
    {
        // eight traced rooms of 300-580 m2 (trapezoid, pentagon, hexagon, wedge; L, T, notched, canted L), each with a
        // free corridor to a small hall: sealed under both methods, a raised ceiling bay, pilasters on long blank
        // walls, lights about every 6 m, spawn spots inside the outline
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local shapes = {
              { { -12, -8 }, { 12, -8 }, { 8, 8 }, { -10, 10 } },
              { { -10, -12 }, { 10, -12 }, { 16, 4 }, { 0, 14 }, { -16, 4 } },
              { { -12, -7 }, { 0, -14 }, { 12, -7 }, { 12, 7 }, { 0, 14 }, { -12, 7 } },
              { { -14, -9 }, { 14, -9 }, { 3, 9 }, { -3, 9 } },
              { { -15, -15 }, { 15, -15 }, { 15, -3 }, { -3, -3 }, { -3, 15 }, { -15, 15 } },
              { { -16, 0 }, { 16, 0 }, { 16, 10 }, { 5, 10 }, { 5, 24 }, { -5, 24 }, { -5, 10 }, { -16, 10 } },
              { { -14, -10 }, { 14, -10 }, { 14, 10 }, { 4, 10 }, { 4, 4 }, { -4, 4 }, { -4, 10 }, { -14, 10 } },
              { { -14, -12 }, { 10, -14 }, { 14, -2 }, { 0, 0 }, { 2, 14 }, { -12, 12 } },
            }
            local zones, links, obj = {}, {}, {}
            for i, sh in ipairs(shapes) do
              local ox, oz = ((i - 1) % 4) * 90 - 135, math.floor((i - 1) / 4) * 90 - 45
              local outline = {}
              for j, q in ipairs(sh) do outline[j] = { q[1] + ox, q[2] + oz } end
              zones[#zones + 1] = { id = "p" .. i, archetype = "poly", pos = { nil, 0, nil }, outline = outline, palette = { theme = "industrial", variant = 1 } }
              zones[#zones + 1] = { id = "h" .. i, archetype = "hall", pos = { ox, 0, oz - 34 }, size = { 16, 12 }, palette = { theme = "bunker", variant = 1 } }
              links[#links + 1] = { type = "corridor", a = "p" .. i, b = "h" .. i }
              obj[#obj + 1] = { kind = "spawns", id = "s" .. i, zone = "p" .. i, count = 3 }
            end
            local g = { kind = "layout", gametype = "dm", class = "standard", step = 5, zones = zones, links = links, objectives = obj }
            local rep = kit.build_layout{ graph = g, name = "T", force = true }
            local out = {}
            for _, z in ipairs(rep.zones) do
              if z.archetype == "poly" then
                local parts = { raised = 0, pilaster = 0, light = 0 }
                for _, b in ipairs(level.brushes_tagged("kit.zone", z.name).brushes) do
                  local p = b:tags()["kit.part"] or ""
                  if p == "raised" then parts.raised = parts.raised + 1 elseif p:find("^pilaster") then parts.pilaster = parts.pilaster + 1 end
                end
                for _, l in ipairs(level.objects_of("light")) do if l.name:find("^" .. z.name .. "_") then parts.light = parts.light + 1 end end
                out[#out + 1] = z.id .. ":" .. parts.raised .. ":" .. parts.pilaster .. ":" .. parts.light .. ":" .. #z.spots
              end
            end
            return table.concat(out, ",") .. "~" .. rep.spawns
            """);
        _out.WriteLine(r.ReturnValue);
        string[] p = r.ReturnValue!.Split('~');
        Assert.Equal("24", p[1]);
        foreach (string z in p[0].Split(','))
        {
            int[] n = z.Split(':').Skip(1).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            Assert.True(n[0] >= 1 && n[1] >= 2 && n[2] >= 6 && n[3] >= 6, "a traced room without its ceiling bay, pilasters, lights or spots: " + z);
        }

        AssertSealed(doc, "traced rooms");
    }

    // ---- every archetype turned, far from the origin ----------------------------------------------------------

    /// <summary>
    /// Every rectangle archetype of a class as a single zone turned by rot, in 70 m cells from (ox, -140): far from the
    /// origin (ox = 450) the world coordinates of a turned brush carry the most rounding, so any face a recipe lays on
    /// another brush's oblique face (a partition on a wall, a step on a step, a flight's top on a deck) opens seams.
    /// </summary>
    private static string EveryArchetypeTurned(string cls, double rot, double ox) => FormattableString.Invariant($$"""
        local CLASS, ROT, OX = "{{cls}}", {{rot}}, {{ox}}
        local zones = {}
        for _, a in ipairs(kit.layout_archetypes{}) do
          if (CLASS == "open_air") == (a.class == "open_air") and a.class ~= "traced" then
            local i = #zones
            local z = { id = a.name, archetype = a.name, pos = { OX + (i % 5) * 70, 0, -140 + math.floor(i / 5) * 70 },
              size = { math.min(a.max[1], math.max(a.min[1], 28)), math.min(a.max[2], math.max(a.min[2], 28)) }, rot = ROT,
              palette = { theme = "industrial", variant = 1 } }
            if a.name == "road" then z.size = { 10, 50 } z.params = { heading = "+z", rise = 4.5, south = 10, north = 10 } end
            if a.name == "finale" then z.params = { heading = "+z" } end
            zones[#zones + 1] = z
          end
        end
        local g = { kind = "layout", gametype = "dm", class = CLASS, step = 5, zones = zones, links = {}, objectives = {} }
        kit.build_layout{ graph = g, name = "S", force = true, lights = false }
        return #zones
        """);

    [Theory]
    [Trait("Category", "DeepGate")]
    [InlineData("standard", 22.5, -140)]
    [InlineData("standard", 15, -140)]
    [InlineData("standard", 37, 450)]
    [InlineData("standard", -45, 450)]
    [InlineData("open_air", 45, -140)]
    [InlineData("open_air", 37, 450)]
    public void Every_Archetype_Turned_Builds_Sealed_Under_Both_Methods(string cls, double rot, double ox)
    {
        (EditorDocument doc, ScriptRunResult r) = Run(EveryArchetypeTurned(cls, rot, ox));
        _out.WriteLine($"{r.ReturnValue} zones");
        AssertSealed(doc, FormattableString.Invariant($"{cls} archetypes at rot {rot}, from x {ox}"));
    }

    // ---- oblong rings, turned terraces, joins onto traced zones -------------------------------------------

    /// <summary>
    /// Ring hubs with unequal sides (and square ones) at several turns, up to ~300 m from the origin: the slab, its
    /// underside and the hole through both meet no other brush on an oblique plane.
    /// </summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void Oblong_Rings_Build_Sealed_Under_Both_Methods()
    {
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local zs = {}
            local sizes = { { 30, 36 }, { 26, 30 }, { 34, 28 }, { 30, 30 } }
            local rots = { 0, 35, 210 }
            for a, sz in ipairs(sizes) do
              for b, rot in ipairs(rots) do
                zs[#zs + 1] = { id = "z" .. #zs + 1, archetype = "ring", pos = { 60 + (b - 1) * 70 + 0.37 * a, 5 * (a % 2), -100 + (a - 1) * 70 }, size = sz, rot = rot,
                  palette = { theme = "industrial", variant = 1 } }
              end
            end
            local g = { kind = "layout", gametype = "dm", class = "open_air", step = 5, zones = zs, links = {}, objectives = {} }
            kit.build_layout{ graph = g, name = "R", lights = false }
            return #zs
            """);
        Assert.Equal("12", r.ReturnValue);
        AssertSealed(doc, "oblong rings");
    }

    /// <summary>
    /// A terrace turned 30 degrees ~150 m out takes a bridge on each side wall: on the low half of its +z wall at its
    /// floor and on the high half of its -z wall a storey up (the side walls' halves are measured in its own frame).
    /// </summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void A_Turned_Terrace_Takes_Links_On_The_Halves_Of_Its_Side_Walls()
    {
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local ROT, OX, OZ = 30, 120, -90
            local function T(lx, lz) local c, s = math.cos(math.rad(ROT)), math.sin(math.rad(ROT)) return OX + lx * c + lz * s, OZ - lx * s + lz * c end
            local function Z(id, arch, lx, lz, y, size, params)
              local x, z = T(lx, lz)
              return { id = id, archetype = arch, pos = { x, y, z }, size = size, rot = ROT, params = params, palette = { theme = "industrial", variant = 1 } }
            end
            local g = { kind = "layout", gametype = "dm", class = "open_air", step = 5, zones = {
              Z("t", "terrace", 0, 0, 0, { 28, 22 }, { split = "+x" }),
              Z("a", "deck", -6, 26, 0, { 18, 16 }),
              Z("b", "deck", 6, -26, 5, { 18, 16 }),
            }, links = {
              { id = "l1", type = "bridge", a = "t", b = "a", a_y = 0, b_y = 0, width = 4 },
              { id = "l2", type = "bridge", a = "t", b = "b", a_y = 5, b_y = 5, width = 4 },
            }, objectives = {} }
            local errs = {}
            for _, v in ipairs(kit.check_layout{ graph = g }) do if v.severity == "error" then errs[#errs + 1] = v.message end end
            local rep = kit.build_layout{ graph = g, name = "T", lights = false }
            local modes = {}
            for _, l in ipairs(rep.links) do modes[#modes + 1] = l.mode end
            return #errs .. "|" .. table.concat(modes, ",") .. "|" .. table.concat(errs, " ;; ")
            """);
        _out.WriteLine(r.ReturnValue);
        Assert.Equal("0|classic,classic|", r.ReturnValue);
        AssertSealed(doc, "turned terrace with side-wall bridges");
    }

    /// <summary>
    /// An overlook turned 280 degrees joined to a traced room whose outline reaches a quarter metre over the overlook's
    /// wall and whose side wall leaves it nearly square: the opening's air box ends inside the traced room, clear of its
    /// oblique walls.
    /// </summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void A_Join_Of_A_Turned_Zone_And_A_Traced_Zone_Builds_Sealed()
    {
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local g = { kind = "layout", gametype = "payload", class = "payload", step = 4.5, zones = {
              { id = "z17", archetype = "overlook", role = "high", rot = 280, size = { 22, 22 }, pos = { -65.109632369773976, 13.5, -39.925387335915481 },
                params = { split = "-x" }, palette = { theme = "marsbase", variant = 2 } },
              { id = "z28", archetype = "poly", role = "room", outline = { { -64.0625, -29.1875 }, { -56.1875, -30.5625 }, { -49.1875, 2.0625 }, { -53, -2.5 } },
                pos = { -56.625, 13.5, -14.25 }, palette = { theme = "marsbase", variant = 2 } },
            }, links = { { id = "l40", type = "join", a = "z17", b = "z28" } }, objectives = {} }
            local rep = kit.build_layout{ graph = g, name = "S", lights = false }
            return rep.links[1].mode .. " " .. string.format("%.2f", rep.links[1].width)
            """);
        _out.WriteLine(r.ReturnValue);
        Assert.StartsWith("join ", r.ReturnValue, StringComparison.Ordinal);
        AssertSealed(doc, "overlook joined to a traced room");
    }

    // ---- bent stairs and ramps leaving turned zones -------------------------------------------------------

    /// <summary>
    /// Eighteen free links of one kind of far zone, each in its own 100 m cell: a hall turned 30 (or 45) degrees, a stair
    /// or a ramp leaving its +z wall on an explicit path that bends 30, 45 or 88 degrees, the flight on the first, the
    /// last or the middle segment, climbing or descending 5 m, into an unturned storage room (the last bend folded so
    /// the link arrives square to it), a storage room turned to the link's heading, or a traced pentagon. Returns per
    /// case "case:reachable" after a sealed build.
    /// </summary>
    private static string BentFlights(string farKind) => $$"""
        local BK = "{{farKind}}"
        local function fwd(h) return math.sin(math.rad(h)), math.cos(math.rad(h)) end
        local function rgt(h) return math.cos(math.rad(h)), -math.sin(math.rad(h)) end
        local zones, links, objectives, cases = {}, {}, {}, {}
        for _, typ in ipairs({ "stair", "ramp" }) do
          for _, shape in ipairs({ "first", "last", "middle" }) do
            for _, bend in ipairs({ 30, 45, 88 }) do cases[#cases + 1] = { typ = typ, shape = shape, bend = bend } end
          end
        end
        for ci, c in ipairs(cases) do
          local ox, oz = ((ci - 1) % 6) * 100 - 250, math.floor((ci - 1) / 6) * 100 - 100
          local up = (ci % 2 == 1)
          local run = c.typ == "stair" and 7.5 or 10
          local long, short = run + 7, 6
          local rA = 30 + ((ci % 3 == 0) and 15 or 0)
          local bends, lens
          if c.shape == "first" then bends, lens = { c.bend }, { long, short }
          elseif c.shape == "last" then bends, lens = { c.bend }, { short, long }
          else bends, lens = { c.bend, -c.bend }, { short, long, short } end
          local final = rA
          for _, b in ipairs(bends) do final = final + b end
          local bside = "-z"
          if BK == "rect0" then
            local f = math.floor(final / 90 + 0.5) * 90
            local nb = bends[#bends] + (f - final)
            if math.abs(nb) < 10 or math.abs(nb) > 90 then f = f + ((nb < 0) and 90 or -90) nb = bends[#bends] + (f - final) end
            bends[#bends] = nb
            final = f
          end
          local ya, yb = up and 0 or 5, up and 5 or 0
          local aid, bid = "a" .. ci, "b" .. ci
          zones[#zones + 1] = { id = aid, archetype = "hall", pos = { ox, ya, oz }, size = { 24, 18 }, rot = rA, palette = { theme = "industrial", variant = 1 } }
          local fx, fz = fwd(rA)
          local P = { ox + fx * 9, oz + fz * 9 }
          local h, q, wps = rA, P, {}
          for i, L in ipairs(lens) do
            local dx, dz = fwd(h)
            q = { q[1] + dx * L, q[2] + dz * L }
            if i < #lens then wps[#wps + 1] = q h = h + bends[i] end
          end
          local dx, dz = fwd(final)
          if BK == "poly" then
            local outline, rx, rz = {}, rgt(final)
            for _, uv in ipairs({ { -10, 0 }, { 10, 0 }, { 12, 10 }, { 0, 18 }, { -12, 10 } }) do
              outline[#outline + 1] = { q[1] + rx * uv[1] + dx * uv[2], q[2] + rz * uv[1] + dz * uv[2] }
            end
            zones[#zones + 1] = { id = bid, archetype = "poly", pos = { nil, yb, nil }, outline = outline, palette = { theme = "industrial", variant = 1 } }
            bside = nil
          else
            local half = 8
            if BK == "rect0" then
              local fm = ((final % 360) + 360) % 360
              if fm == 90 or fm == 270 then half = 10 end
              bside = ({ [0] = "-z", [90] = "-x", [180] = "+z", [270] = "+x" })[fm]
            end
            zones[#zones + 1] = { id = bid, archetype = "storage", pos = { q[1] + dx * half, yb, q[2] + dz * half }, size = { 20, 16 },
              rot = (BK == "rectR") and final or 0, palette = { theme = "industrial", variant = 1 } }
          end
          links[#links + 1] = { type = c.typ, a = aid, b = bid, a_side = "+z", a_at = P, b_side = bside, b_at = q, path = wps }
          objectives[#objectives + 1] = { kind = "spawns", id = "sa" .. ci, zone = aid, count = 1 }
          objectives[#objectives + 1] = { kind = "spawns", id = "sb" .. ci, zone = bid, count = 1 }
        end
        local g = { kind = "layout", gametype = "dm", class = "standard", step = 5, zones = zones, links = links, objectives = objectives }
        local errs = {}
        for _, v in ipairs(kit.check_layout{ graph = g }) do if v.severity == "error" and v.message:find("^link") then errs[#errs + 1] = v.message end end
        local rep = kit.build_layout{ graph = g, name = "T", force = true, lights = false }
        ops.build()
        local out = {}
        for ci = 1, #cases do
          local sa, sb = level.objects_named("T_sa" .. ci .. "_1")[1], level.objects_named("T_sb" .. ci .. "_1")[1]
          local r = (sa and sb) and spatial.routes{ a = sa, b = sb, k = 1 } or nil
          out[#out + 1] = ci .. ":" .. tostring(r ~= nil and r.reachable == true)
        end
        return #errs .. "~" .. table.concat(errs, " | ") .. "~" .. table.concat(out, ",")
        """;

    [Theory]
    [Trait("Category", "DeepGate")]
    [InlineData("rect0")]
    [InlineData("rectR")]
    [InlineData("poly")]
    public void Bent_Free_Stairs_And_Ramps_From_Turned_Zones_Build_Sealed_And_Walkable(string farKind)
    {
        (EditorDocument doc, ScriptRunResult r) = Run(BentFlights(farKind));
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.True(p[0] == "0", "check errors: " + p[1]);
        string[] cut = p[2].Split(',').Where(c => !c.EndsWith(":true", StringComparison.Ordinal)).ToArray();
        Assert.True(cut.Length == 0, "no walk between the two zones of case(s) " + string.Join(", ", cut));
        AssertSealed(doc, "bent flights into " + farKind);
    }

    /// <summary>
    /// Population by a zone's name stays on that zone's floor: zone z (a hall turned 45 degrees, or an L-shaped traced
    /// room) has an unturned nook standing 1.2 m off its wall inside z's bounding box. Returns, for dress props
    /// (objects and brush centres), spawn points and items: placed:inside z's world outline.
    /// </summary>
    private static string NeighbourInBox(bool traced) => $$"""
        local traced = {{(traced ? "true" : "false")}}
        local zones
        if traced then
          zones = {
            { id = "z", archetype = "poly", pos = { nil, 0, nil }, outline = { { 0, 0 }, { 30, 0 }, { 30, 12 }, { 12, 12 }, { 12, 30 }, { 0, 30 } } },
            { id = "n", archetype = "nook", pos = { 19.5, 0, 19.5 }, size = { 12, 12 }, palette = { theme = "industrial", variant = 1 } },
          }
        else
          zones = {
            { id = "z", archetype = "hall", pos = { 0, 0, 0 }, size = { 20, 20 }, rot = 45, palette = { theme = "industrial", variant = 1 } },
            { id = "n", archetype = "nook", pos = { 13, 0, 13 }, size = { 10, 10 }, palette = { theme = "industrial", variant = 1 } },
          }
        end
        local g = { kind = "layout", gametype = "dm", class = "standard", step = 5, zones = zones, links = {}, objectives = {} }
        local rep = kit.build_layout{ graph = g, name = "Q", force = true, lights = false }
        local z = rep.zones[1]
        local function inside(poly, x, zz)
          local c, j = false, #poly
          for i = 1, #poly do
            local a, b = poly[i], poly[j]
            if ((a[2] > zz) ~= (b[2] > zz)) and (x < (b[1] - a[1]) * (zz - a[2]) / (b[2] - a[2]) + a[1]) then c = not c end
            j = i
          end
          return c
        end
        local function tally(points)
          local n, k = 0, 0
          for _, p in ipairs(points) do n = n + 1 if inside(z.outline, p[1], p[2]) then k = k + 1 end end
          return n .. ":" .. k
        end
        local d = kit.dress{ room = z.name, seed = 1 }
        local dp = {}
        for _, o in ipairs(d.objects or {}) do if o.pos then dp[#dp + 1] = { o.pos.x, o.pos.z } end end
        for _, b in ipairs(d.brushes or {}) do local bb = b.bounds dp[#dp + 1] = { (bb.min.x + bb.max.x) / 2, (bb.min.z + bb.max.z) / 2 } end
        local sp = {}
        for _, o in ipairs(kit.spawn_points{ rooms = { z.name }, count = 4 }) do sp[#sp + 1] = { o.pos.x, o.pos.z } end
        local ip = {}
        for _, o in ipairs(kit.items{ rooms = { z.name }, seed = 1 }.items) do ip[#ip + 1] = { o.pos.x, o.pos.z } end
        return tally(dp) .. "," .. tally(sp) .. "," .. tally(ip)
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Population_By_Zone_Name_Stays_Inside_A_Turned_Or_Traced_Zone_Beside_A_Neighbour(bool traced)
    {
        (_, ScriptRunResult r) = Run(NeighbourInBox(traced));
        _out.WriteLine(r.ReturnValue);
        string[] what = { "dress", "spawn points", "items" };
        string[] p = r.ReturnValue!.Split(',');
        for (int i = 0; i < 3; i++)
        {
            string[] nk = p[i].Split(':');
            Assert.True(int.Parse(nk[0], CultureInfo.InvariantCulture) > 0, $"{what[i]}: nothing placed ({r.ReturnValue})");
            Assert.True(nk[0] == nk[1], $"{what[i]}: {int.Parse(nk[0], CultureInfo.InvariantCulture) - int.Parse(nk[1], CultureInfo.InvariantCulture)} placed outside the zone's outline ({r.ReturnValue})");
        }
    }

    [Fact]
    public void An_Unturned_Box_Zone_Gives_The_Probe_The_Same_Floor_As_Its_Box()
    {
        // every zone of an unturned layout (halls, a gallery a storey up, a sunken court, a split level): the lattice of
        // the zone by name holds exactly the nodes of its box
        (_, ScriptRunResult r) = Run("""
            local function Z(id, arch, x, z, w, d) return { id = id, archetype = arch, pos = { x, 0, z }, size = { w, d }, palette = { theme = "industrial", variant = 1 } } end
            local g = { kind = "layout", gametype = "dm", class = "standard", step = 5, zones = {
                Z("h", "hall", 0, 0, 24, 18), Z("g", "gallery", 40, 0, 24, 24), Z("s", "sunken", 0, 44, 32, 32), Z("p", "split", 44, 44, 26, 26),
              }, links = { { type = "corridor", a = "h", b = "g" }, { type = "corridor", a = "h", b = "s" }, { type = "corridor", a = "g", b = "p" }, { type = "doorway", a = "s", b = "p" } },
              objectives = {} }
            local rep = kit.build_layout{ graph = g, name = "U", lights = false, force = true }
            local P = ged.import("kit_probe")
            local out = {}
            for _, zr in ipairs(rep.zones) do
              local s = P.spaces("test", { room = zr.name }, function(_, m) error(m) end)[1]
              local pr = P.shared()
              local a = pr:space_lattice(s).nodes
              local b = pr:lattice(s.x0, s.z0, s.x1, s.z1, s.y0 - 0.1, math.max(s.y0, s.y1 - 1.0)).nodes
              local same = #a == #b
              for i = 1, math.min(#a, #b) do if a[i].x ~= b[i].x or a[i].z ~= b[i].z or a[i].y ~= b[i].y then same = false end end
              out[#out + 1] = zr.id .. "=" .. #a .. "/" .. #b .. (same and "" or "!")
            end
            return table.concat(out, " ")
            """);
        _out.WriteLine(r.ReturnValue);
        Assert.DoesNotContain("!", r.ReturnValue, StringComparison.Ordinal);
        Assert.DoesNotContain("=0/", r.ReturnValue, StringComparison.Ordinal);
    }

    // ---- payload and open air ------------------------------------------------------------------------------

    /// <summary>
    /// A payload track: north up a graded road into a yard, out of it on a free road that bends 35 degrees, then up a
    /// graded road turned to rot 35, through a yard, along a level road and into a finale, all at rot 35.
    /// </summary>
    internal const string TurnedTrack = """
        local S = 4.5
        local function T(lx, lz) local r = math.rad(35) local c, s = math.cos(r), math.sin(r) return lx * c + lz * s, -lx * s + lz * c end
        local function Zr(id, arch, role, seg, lx, lz, y, size, params)
          local x, z = T(lx, lz)
          return { id = id, archetype = arch, role = role, segment = seg, pos = { x, y, z }, size = size, rot = 35, params = params, palette = { theme = "industrial", variant = 1 } }
        end
        local function Z(id, arch, role, seg, x0, x1, z0, z1, y, params)
          return { id = id, archetype = arch, role = role, segment = seg, pos = { (x0 + x1) / 2, y, (z0 + z1) / 2 }, size = { x1 - x0, z1 - z0 }, params = params,
            palette = { theme = "industrial", variant = 1 } }
        end
        local a, b = -63, 127
        local g = { kind = "layout", gametype = "payload", class = "payload", step = S, zones = {
            Z("start", "start", "spawn", 1, -10, 10, -8, 8, 0),
            Z("r1", "road", "span", 1, -5, 5, 9, 65, 0, { heading = "+z", rise = S, south = 12, north = 12 }),
            Z("y1", "yard", "checkpoint", 1, -16, 16, 66, 98, S),
            Zr("r2", "road", "span", 2, a, b, S, { 10, 50 }, { heading = "+z", rise = S, south = 10, north = 10 }),
            Zr("y2", "yard", "checkpoint", 2, a, b + 42, 2 * S, { 32, 32 }),
            Zr("r3", "road", "span", 3, a, b + 75, 2 * S, { 10, 32 }, { heading = "+z", south = 10, north = 10 }),
            Zr("fin", "finale", "checkpoint", 3, a, b + 112, 2 * S, { 40, 40 }, { heading = "+z" }),
          }, links = {
            { type = "doorway", a = "start", b = "r1" }, { type = "road", a = "r1", b = "y1" }, { type = "road", a = "y1", b = "r2" },
            { type = "road", a = "r2", b = "y2" }, { type = "road", a = "y2", b = "r3" }, { type = "road", a = "r3", b = "fin" },
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
    [Trait("Category", "DeepGate")]
    public void Payload_Track_Turns_On_A_Free_Road_Into_A_Turned_Graded_Road()
    {
        (EditorDocument doc, ScriptRunResult r) = Run(TurnedTrack + """

            local V = kit.check_layout{ graph = g }
            local errs, pay = {}, {}
            for _, v in ipairs(V) do
              if v.severity == "error" then errs[#errs + 1] = v.message elseif v.rule == "payload" then pay[#pay + 1] = v.message end
            end
            local rep = kit.build_layout{ graph = g, name = "P", lights = false }
            local road = nil
            for _, l in ipairs(rep.links) do if l.a == "y1" and l.b == "r2" then road = l end end
            local cart = level.find_mover("P_cart")
            local kf = {}
            for _, k in ipairs(cart.keyframes) do kf[#kf + 1] = string.format("%.3f,%.3f,%.3f", k.pos.x, k.pos.y, k.pos.z) end
            local path = {}
            for _, q in ipairs(road.path) do path[#path + 1] = string.format("%.3f,%.3f", q[1], q[2]) end
            return #errs .. "~" .. table.concat(errs, " | ") .. "~" .. table.concat(pay, " | ") .. "~" .. road.mode .. "~" .. table.concat(path, ";") .. "~" .. table.concat(kf, ";")
            """);
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.True(p[0] == "0", "check errors: " + p[1]);
        Assert.DoesNotContain("never turns", p[2], StringComparison.Ordinal);
        Assert.DoesNotContain("never climbs", p[2], StringComparison.Ordinal);
        Assert.DoesNotContain("the cart cannot go", p[2], StringComparison.Ordinal);
        Assert.DoesNotContain("the cart enters", p[2], StringComparison.Ordinal);
        Assert.Equal("free", p[3]);
        // the cart rolls through every bend of the free road (its keyframes include the route's inner points)
        List<double[]> path = p[4].Split(';').Select(q => q.Split(',').Select(D).ToArray()).ToList();
        List<double[]> kf = p[5].Split(';').Select(q => q.Split(',').Select(D).ToArray()).ToList();
        Assert.True(path.Count >= 3, p[4]);
        foreach (double[] q in path.Skip(1).Take(path.Count - 2))
        {
            Assert.Contains(kf, k => Math.Abs(k[0] - q[0]) < 0.01 && Math.Abs(k[2] - q[1]) < 0.01);
        }

        // ... and ends in the finale, up both graded roads (floor 9 m + 0.75 m above the floor, less the final pit)
        Assert.Equal(9.75, kf.Max(k => k[1]), 2);
        AssertSealed(doc, "turned payload track");
        var grade = Grade(doc, Holes(doc, true).Build, "payload", "payload", 4);
        List<string> failed = DesignGrader.FailedIds(grade);
        _out.WriteLine(string.Join(",", failed));
        Assert.DoesNotContain("P01", failed);
        Assert.DoesNotContain("P06", failed);
    }

    /// <summary>The agent guide's traced "supply line" payload (layout-trace sample): poly yards and a canyon, free roads
    /// that bend, a chasm bridge to a depot, a graded road and a hold turned to rot -30, an overlook a storey up.</summary>
    internal const string SupplyLine = """
        local function traced(id, y, outline, extra)
          local z = { id = id, archetype = "poly", pos = { nil, y, nil }, outline = outline, sky = true }
          for k, v in pairs(extra) do z[k] = v end
          return z
        end
        -- The depot, its road and the hold are set pieces in one frame turned 30 degrees: give each its spot in that
        -- frame, turned into world coordinates, and the same rot (classic links join them wall to wall).
        local function turned(id, arch, lx, lz, y, size, extra)
          local r = math.rad(-30)
          local z = { id = id, archetype = arch, pos = { lx * math.cos(r) + lz * math.sin(r), y, -lx * math.sin(r) + lz * math.cos(r) },
            size = size, rot = -30 }
          for k, v in pairs(extra) do z[k] = v end
          return z
        end
        local g = { kind = "layout", gametype = "payload", class = "payload", step = 4.5, zones = {
            traced("yard", 0, { { -26, -22 }, { 24, -22 }, { 30, 0 }, { 20, 20 }, { -26, 20 } }, { palette = { theme = "industrial", variant = 1 }, role = "spawn", segment = 1 }),
            traced("gate", 0, { { -14, 30 }, { 18, 30 }, { 24, 45 }, { 18, 60 }, { -14, 60 } }, { palette = { theme = "industrial", variant = 2 }, role = "checkpoint", segment = 1 }),
            traced("canyon", 0, { { 32, 34 }, { 66, 28 }, { 78, 62 }, { 56, 86 }, { 30, 72 } }, { palette = { theme = "cave", variant = 1 }, role = "span", segment = 2 }),
            traced("overlook", 4.5, { { 16, 100 }, { 40, 98 }, { 43, 116 }, { 20, 120 } }, { palette = { theme = "bunker", variant = 1 }, role = "high", segment = 2 }),
            turned("depot", "yard", 120, 37, 0, { 32, 32 }, { palette = { theme = "admin", variant = 1 }, role = "checkpoint", segment = 2 }),
            turned("climb", "road", 120, 79, 0, { 10, 50 }, { palette = { theme = "marsbase", variant = 1 }, role = "span", segment = 3, params = { heading = "+z", rise = 4.5, south = 10, north = 10 } }),
            turned("hold", "finale", 120, 127, 4.5, { 44, 44 }, { palette = { theme = "bunker", variant = 2 }, role = "checkpoint", segment = 3, params = { heading = "+z" } }),
          }, links = {
            { type = "road", a = "yard", b = "gate" },               -- free links: routed for you between traced zones
            { type = "corridor", a = "yard", b = "canyon" },
            { type = "road", a = "gate", b = "canyon" },
            { type = "stair", a = "gate", b = "overlook" },
            { type = "bridge", a = "canyon", b = "depot", width = 8 }, -- the cart's chasm bridge, straight across
            { type = "road", a = "depot", b = "climb" },             -- classic: facing walls in one frame
            { type = "ramp", a = "depot", b = "overlook" },
            { type = "road", a = "climb", b = "hold" },
            { type = "corridor", a = "overlook", b = "hold" },
            { type = "stair", a = "overlook", b = "hold", b_y = 9 },  -- up to the hold's terrace
          }, objectives = {
            { kind = "cart", zones = { "yard", "gate", "canyon", "depot", "climb", "hold" }, attacking_team = "blue" },
            { kind = "spawns", id = "a1", zone = "yard", team = "blue", role = "attack", count = 3 },
            { kind = "spawns", id = "d1", zone = "depot", team = "red", role = "defend", count = 3 },
            { kind = "spawns", id = "a2", zone = "gate", team = "blue", role = "attack", count = 3 },
            { kind = "spawns", id = "d2", zone = "overlook", team = "red", role = "defend", count = 3 },
            { kind = "checkpoint", index = 1, zone = "gate", spawns = { "a1", "d1" } },
            { kind = "checkpoint", index = 2, zone = "depot", spawns = { "a2", "d2" } },
            { kind = "checkpoint", index = 3, zone = "hold", spawns = { "a2", "d2" } },
          } }
        """;

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Traced_Supply_Line_Meets_Every_Rule_And_Builds_Sealed()
    {
        (EditorDocument doc, ScriptRunResult r) = Run(SupplyLine + """

            local V = kit.check_layout{ graph = g }
            local msgs = {}
            for _, v in ipairs(V) do msgs[#msgs + 1] = v.severity .. "/" .. v.rule .. ": " .. v.message end
            local rep = kit.build_layout{ graph = g, name = "S" }
            local free = 0
            for _, l in ipairs(rep.links) do if l.mode == "free" then free = free + 1 end end
            return #V .. "~" .. table.concat(msgs, " | ") .. "~" .. free .. "~" .. rep.spawns .. "~" .. string.format("%.0f", rep.area)
            """);
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.True(p[0] == "0", "the traced layout breaks rules: " + p[1]);
        Assert.Equal("8", p[2]);
        Assert.Equal("12", p[3]);
        Assert.True(D(p[4]) >= 10000, p[4]);
        AssertSealed(doc, "supply line");
        AssertLintClean(doc);
        var grade = Grade(doc, Holes(doc, true).Build, "payload", "payload", 12);
        List<string> failed = DesignGrader.FailedIds(grade);
        _out.WriteLine(string.Join(",", failed));
        foreach (string id in new[] { "P01", "P06" })
        {
            Assert.DoesNotContain(id, failed);
        }
    }

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Open_Air_Platforms_At_Different_Rot_Join_By_Free_Bridges_And_A_Flying_Stair()
    {
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local function Z(id, arch, pos, size, rot) return { id = id, archetype = arch, pos = pos, size = size, rot = rot, palette = { theme = "industrial", variant = 1 } } end
            local g = { kind = "layout", gametype = "dm", class = "open_air", step = 5, zones = {
                Z("p1", "deck", { 0, 0, 0 }, { 24, 20 }, 0),
                Z("p2", "plaza", { 40, 0, 0 }, { 24, 24 }, 20),
                Z("p3", "garden", { 12, 5, 40 }, { 22, 22 }, 350),
              }, links = {
                { type = "bridge", a = "p1", b = "p2" },
                { type = "stair", a = "p1", b = "p3" },
              }, objectives = {
                { kind = "spawns", id = "s1", zone = "p1", count = 2 }, { kind = "spawns", id = "s2", zone = "p2", count = 2 },
                { kind = "spawns", id = "s3", zone = "p3", count = 2 },
              } }
            local errs = {}
            for _, v in ipairs(kit.check_layout{ graph = g }) do if v.severity == "error" then errs[#errs + 1] = v.message end end
            local rep = kit.build_layout{ graph = g, name = "O" }
            local modes = {}
            for _, l in ipairs(rep.links) do modes[#modes + 1] = l.mode end
            ops.build()
            local sp = level.objects_of("respawn")[1]
            local w = spatial.walk{ from = { sp.pos.x, sp.pos.y - 1, sp.pos.z } }
            return #errs .. "~" .. table.concat(errs, " | ") .. "~" .. table.concat(modes, ",") .. "~" .. rep.spawns .. "~" .. #w.unreachable_spawns
            """);
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.True(p[0] == "0", "check errors: " + p[1]);
        Assert.Equal("free,free", p[2]);
        Assert.Equal("6", p[3]);
        Assert.Equal("0", p[4]);
        AssertSealed(doc, "open-air platforms");
    }

    // Two open-air platforms at different rot, 5 m apart in height, joined by a free stair (run 7.5 m) on given ports.
    // "long": a 10.07 m gap, longer than the run plus 1 m, so the flight starts at the low platform's turned edge.
    // "run": a 7.53 m gap, about the run, so the flight starts on the low platform and the landing lies over its top step.
    private const string FlyingStairLong = """
        Z("high", "terrace", { -43.014, 0, 1.003 }, { 24, 28 }, 5, 2, { 0, 5 }, "+z"),
        Z("low", "mast", { -29.838, -5, -32.324 }, { 20, 20 }, 75, 3, { 0 }, "-x"),
        }, links = { { id = "l1", type = "stair", a = "high", b = "low", a_side = "-z", b_side = "-x", a_y = 0, b_y = -5, width = 3.5,
          a_at = { -37.323, -13.549 }, b_at = { -34.720, -23.280 } } },
        """;

    private const string FlyingStairRun = """
        Z("high", "terrace", { -44.973, 0, 2.994 }, { 24, 28 }, 5, 2, { 0, 5 }, "-z"),
        Z("low", "isle", { -55.588, -5, 33.548 }, { 22, 18 }, 150, 3, { 0 }, "-z"),
        }, links = { { id = "l1", type = "stair", a = "high", b = "low", a_side = "+z", b_side = "+z", a_y = 0, b_y = -5, width = 4,
          a_at = { -50.352, 17.518 }, b_at = { -54.119, 24.003 } } },
        """;

    /// <summary>
    /// A flying stair between two turned open-air platforms seals under both build methods whatever the gap: the flight
    /// starts at least a quarter metre onto the low platform (a first step at its turned edge would put a face on that
    /// edge's oblique plane), and the landing is a little wider than the flight (two overlapping side faces on one
    /// oblique plane do not coincide exactly once turned).
    /// </summary>
    [Theory]
    [Trait("Category", "DeepGate")]
    [InlineData("long", 8.6, 12)]
    [InlineData("run", 7.4, 7.6)]
    public void A_Flying_Stair_Between_Turned_Open_Air_Platforms_Builds_Sealed(string gap, double minGap, double maxGap)
    {
        (EditorDocument doc, ScriptRunResult r) = Run("""
            local function Z(id, arch, pos, size, rot, chamfer, levels, split)
              return { id = id, archetype = arch, pos = pos, size = size, rot = rot, chamfer = chamfer, levels = levels,
                params = { split = split }, palette = { theme = "industrial", variant = 1 } }
            end
            local g = { kind = "layout", gametype = "dm", class = "open_air", step = 5, zones = {
            """ + (gap == "long" ? FlyingStairLong : FlyingStairRun) + """
              objectives = {} }
            local errs = {}
            for _, v in ipairs(kit.check_layout{ graph = g }) do if v.severity == "error" then errs[#errs + 1] = v.message end end
            local rep = kit.build_layout{ graph = g, name = "S" }
            local p = rep.links[1].path
            local G = math.sqrt((p[#p][1] - p[1][1]) ^ 2 + (p[#p][2] - p[1][2]) ^ 2)
            return #errs .. "~" .. table.concat(errs, " | ") .. "~" .. rep.links[1].mode .. "~" .. string.format("%.3f", G)
            """);
        string[] p = r.ReturnValue!.Split('~');
        _out.WriteLine(r.ReturnValue);
        Assert.True(p[0] == "0", "check errors: " + p[1]);
        Assert.Equal("free", p[2]);
        Assert.InRange(D(p[3]), minGap, maxGap);
        AssertSealed(doc, $"flying stair ({gap} gap)");
    }
}
