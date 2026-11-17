using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The round-3 recreation fixes for build speed and the lightmap surface cap: the run's brush index (level.brushes_in,
/// brush.bounds, brush:contains and the uid lookups answer exactly what a full scan does, through edits), the tag index,
/// the MoonSharp value-less local (a search loop's <c>local found</c> starts nil), the surface budget (enclosed faces
/// give up their surfaces first past the cap; the rest are reported at build, save, lint and level.stats) and the kit's
/// organic budget (coarser far rock).
/// </summary>
public sealed class KitBudgetAndSpeedTests
{
    private readonly ITestOutputHelper _out;

    public KitBudgetAndSpeedTests(ITestOutputHelper output) => _out = output;

    private static EditorDocument NewDoc() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    private string Run(EditorDocument doc, string script)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, script);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        _out.WriteLine(r.ReturnValue);
        return r.ReturnValue ?? string.Empty;
    }

    // ---- MoonSharp's value-less local ------------------------------------------------------------------------------

    [Fact]
    public void A_Value_Less_Local_Starts_Nil_Inside_A_Generic_For()
    {
        string got = Run(ScriptTestFixture.NewDoc(entities: 0), """
            local s = ""
            for _, v in ipairs({ 1, 2 }) do local y s = s .. tostring(y) .. " " y = v end
            local r = ""
            for i = 1, 3 do local x if i == 1 then x = 5 end r = r .. tostring(x) .. " " end
            return s .. "| " .. r
            """);
        Assert.Equal("nil nil | 5 nil nil ", got);
    }

    [Fact]
    public void A_Clustering_Loop_With_A_Value_Less_Local_Ends()
    {
        // The steelshaper build's door-frame clustering: with the stale local it appended to the list it walked, forever.
        var doc = ScriptTestFixture.NewDoc(entities: 0);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(ScriptTestFixture.Services(doc), """
            local cl, frames = {}, {}
            for i = 1, 5 do frames[i] = { k = i } end
            for i, f in ipairs(frames) do
              local found
              for _, c in ipairs(cl) do
                for _, g in ipairs(c) do
                  if g.k == f.k - 1 then found = c break end
                end
                if found then break end
              end
              if found then found[#found + 1] = f else cl[#cl + 1] = { f } end
            end
            return #cl .. " " .. #cl[1]
            """, new ScriptRunOptions { ChunkName = "cluster", Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(20) } }, log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("1 5", r.ReturnValue);
    }

    [Fact]
    public void Local_Init_Rewrites_Only_Value_Less_Locals_On_Their_Own_Line()
    {
        const string src = "local a\nlocal b, c = 1\nlocal function f() end\nlocal s = 'local x'\n-- local y\n--[[ local z\n]]\nlocal d, e\nx = [[local w]]\nlocal g\n= 3";
        const string want = "local a = nil\nlocal b, c = 1\nlocal function f() end\nlocal s = 'local x'\n-- local y\n--[[ local z\n]]\nlocal d, e = nil, nil\nx = [[local w]]\nlocal g\n= 3";
        Assert.Equal(want, LuaLocalInit.Apply(src));
        Assert.Equal(src.Count(ch => ch == '\n'), LuaLocalInit.Apply(src).Count(ch => ch == '\n'));
    }

    // ---- The run's brush index ---------------------------------------------------------------------------------------

    [Fact]
    public void Brushes_In_Bounds_And_Contains_Match_A_Scan_Through_Edits()
    {
        // Expected boxes are kept in Lua (independent of the engine's bounds); every query is checked against them after
        // adds, moves, flag changes and deletes.
        string got = Run(NewDoc(), """
            local seed = 7
            local function rnd() seed = (seed * 16807) % 2147483647 return seed / 2147483647 end
            local boxes = {}
            local function add(i)
              local x, y, z = rnd() * 120 - 60, rnd() * 10, rnd() * 120 - 60
              local w, h, d = 0.5 + rnd() * 6, 0.5 + rnd() * 4, 0.5 + rnd() * 6
              if i % 37 == 0 then w = 70 end
              local b = level.brush{ shape = "box", pos = { x, y, z }, size = { w, h, d }, detail = true }
              boxes[b.uid] = { x0 = x - w / 2, y0 = y - h / 2, z0 = z - d / 2, x1 = x + w / 2, y1 = y + h / 2, z1 = z + d / 2, b = b }
            end
            for i = 1, 400 do add(i) end
            local bad = 0
            local function check(n)
              for _ = 1, n do
                local x, y, z = rnd() * 140 - 70, rnd() * 12 - 1, rnd() * 140 - 70
                local w, h, d = rnd() * 20, rnd() * 6, rnd() * 20
                local mn, mx = { x, y, z }, { x + w, y + h, z + d }
                local want = {}
                for uid, e in pairs(boxes) do
                  if e.x0 <= mx[1] + 1e-3 and e.x1 >= mn[1] - 1e-3 and e.y0 <= mx[2] + 1e-3 and e.y1 >= mn[2] - 1e-3 and e.z0 <= mx[3] + 1e-3 and e.z1 >= mn[3] - 1e-3 then
                    want[uid] = true
                  end
                end
                local seen = {}
                for _, b in ipairs(level.brushes_in{ min = mn, max = mx }.brushes) do
                  seen[b.uid] = true
                  if not want[b.uid] then bad = bad + 1 end
                end
                for uid in pairs(want) do
                  local e = boxes[uid]
                  -- boxes touching the query only within float slack are allowed either way
                  local strict = e.x0 < mx[1] - 1e-3 and e.x1 > mn[1] + 1e-3 and e.y0 < mx[2] - 1e-3 and e.y1 > mn[2] + 1e-3 and e.z0 < mx[3] - 1e-3 and e.z1 > mn[3] + 1e-3
                  if strict and not seen[uid] then bad = bad + 1 end
                end
              end
              for uid, e in pairs(boxes) do
                local bb = e.b.bounds
                if math.abs(bb.min.x - e.x0) > 1e-3 or math.abs(bb.max.z - e.z1) > 1e-3 then bad = bad + 1 end
                local cx, cy, cz = (e.x0 + e.x1) / 2, (e.y0 + e.y1) / 2, (e.z0 + e.z1) / 2
                if not e.b:contains(cx, cy, cz) or e.b:contains(e.x1 + 0.05, cy, cz) then bad = bad + 1 end
              end
            end
            check(150)
            -- moves, deletes and new brushes
            local n = 0
            for uid, e in pairs(boxes) do
              n = n + 1
              if n % 5 == 0 then
                e.b:move(3, 1, -2)
                e.x0, e.x1, e.y0, e.y1, e.z0, e.z1 = e.x0 + 3, e.x1 + 3, e.y0 + 1, e.y1 + 1, e.z0 - 2, e.z1 - 2
              end
            end
            local gone = {}
            n = 0
            for uid, e in pairs(boxes) do n = n + 1 if n % 7 == 0 then gone[#gone + 1] = uid end end
            for _, uid in ipairs(gone) do level.find_brush(uid):delete() boxes[uid] = nil end
            for i = 401, 460 do add(i) end
            check(150)
            return tostring(bad)
            """);
        Assert.Equal("0", got);
    }

    [Fact]
    public void Brush_Tags_Read_Back_Exactly_Through_Writes()
    {
        string got = Run(NewDoc(), """
            local bs = {}
            for i = 1, 300 do bs[i] = level.brush{ shape = "box", pos = { i * 2, 0, 0 }, size = { 1, 1, 1 }, detail = true } end
            for i, b in ipairs(bs) do b:tag("k", "v" .. i) end
            for i = 1, 300, 3 do bs[i]:tag("k", "w" .. i) end
            for i = 2, 300, 5 do bs[i]:tag("other", "x") end
            local bad = 0
            for i, b in ipairs(bs) do
              local t = b:tags()
              local want = (i % 3 == 1) and ("w" .. i) or ("v" .. i)
              if t.k ~= want then bad = bad + 1 end
              if ((i - 2) % 5 == 0) ~= (t.other == "x") then bad = bad + 1 end
            end
            return bad .. " " .. level.brushes_tagged("other", "x").count
            """);
        Assert.Equal("0 60", got);
    }

    // ---- The lightmap surface cap -----------------------------------------------------------------------------------

    /// <summary>A hall with standing detail boxes (seen), a box buried in the world's solid and a box inside another one (enclosed).</summary>
    private const string CapHall = """
        local kit = ged.import('kit')
        kit.room{ name = "hall", pos = {0, 0, 0}, size = {24, 6, 24} }
        for i = 0, 5 do level.brush{ shape = "box", pos = { -8 + i * 3, 1, 0 }, size = { 1, 2, 1 }, detail = true, texture = "rck_canyon_rock03.tga" } end
        level.brush{ shape = "box", pos = { 40, 1, 40 }, size = { 2, 2, 2 }, detail = true, texture = "rck_canyon_rock03.tga" }
        level.brush{ shape = "box", pos = { 0, 1.5, 6 }, size = { 3, 3, 3 }, detail = true, texture = "rck_canyon_rock03.tga" }
        level.brush{ shape = "box", pos = { 0, 1.5, 6 }, size = { 1, 1, 1 }, detail = true, texture = "rck_canyon_rock03.tga" }
        return 1
        """;

    private static CompiledLevel BuildSealed(EditorDocument doc, int cap)
    {
        CompileOptions o = CompileOptionsFactory.CreateForDocument(doc, vfs: null, new BuildSettings(), new CompileRequest { Sealed = true });
        o.SurfaceCap = cap;
        return GeometryBuildService.Build(doc.Rfl, o);
    }

    [Fact]
    public void Past_The_Cap_Enclosed_Faces_Give_Up_Their_Surfaces_First()
    {
        EditorDocument doc = NewDoc();
        Run(doc, CapHall);
        CompiledLevel all = BuildSealed(doc, SurfaceBuilder.MaxLevelSurfaces);
        int wanted = all.Report.Surfaces;
        Assert.Equal(0, all.Report.UnlitFaces);
        Assert.Equal(0, all.Report.SurfaceBudget!.EnclosedFaces); // below the cap nothing changes

        // One surface short: the enclosed faces (the buried box, the inner box, the boxes' bottoms on the floor and the
        // outer box's bottom) give theirs up and every seen face keeps one.
        CompiledLevel tight = BuildSealed(doc, wanted - 1);
        _out.WriteLine($"wanted {wanted}, tight: {tight.Report.Surfaces} surfaces, enclosed {tight.Report.SurfaceBudget!.EnclosedFaces}, unlit {tight.Report.UnlitFaces}");
        Assert.Equal(0, tight.Report.UnlitFaces);
        Assert.True(tight.Report.SurfaceBudget.EnclosedFaces >= 6 + 6 + 6, "enclosed " + tight.Report.SurfaceBudget.EnclosedFaces);
        Assert.True(tight.Report.Surfaces < wanted - 1);
        Assert.DoesNotContain(tight.Report.Messages, m => m.Text.StartsWith("Lightmap surface cap", StringComparison.Ordinal));

        // Far too few: seen faces lose theirs, and the build says how many and where.
        CompiledLevel starved = BuildSealed(doc, 4);
        Assert.Equal(4, starved.Report.Surfaces);
        Assert.True(starved.Report.UnlitFaces > 0);
        BuildMessage msg = Assert.Single(starved.Report.Messages, m => m.Text.StartsWith("Lightmap surface cap", StringComparison.Ordinal));
        Assert.Equal(BuildSeverity.Warning, msg.Severity);
        Assert.NotNull(msg.Location);
        Assert.Contains("lit faces", msg.Text);
    }

    [Fact]
    public void A_Level_At_The_Cap_Is_Reported_By_Lint_Save_And_Level_Stats()
    {
        EditorDocument doc = NewDoc();
        Run(doc, CapHall);
        CompiledLevel built = BuildSealed(doc, SurfaceBuilder.MaxLevelSurfaces);
        GeometryBuildService.Apply(doc.Rfl, built);
        string under = Run(doc, "local s = level.stats() return s.faces .. ' ' .. s.surfaces .. ' ' .. s.unlit_faces .. ' ' .. tostring(s.surface_budget_ok) .. ' ' .. s.surface_cap");
        string[] u = under.Split(' ');
        Assert.True(int.Parse(u[0], CultureInfo.InvariantCulture) > 0);
        Assert.Equal(built.Geometry.Surfaces.Count.ToString(CultureInfo.InvariantCulture), u[1]);
        Assert.Equal("0", u[2]);
        Assert.Equal("true", u[3]);
        Assert.Equal("32767", u[4]);
        Assert.DoesNotContain(LevelLinter.Lint(doc.Rfl).Findings, f => f.Category == LintCategory.LightmapSurfaceCap);

        // The saved geometry of a level that reached the cap: surfaces padded to 32,767 and two lit faces left at −1.
        Geometry g = doc.Rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        Surface template = g.Surfaces[0];
        while (g.Surfaces.Count < SurfaceBuilder.MaxLevelSurfaces)
        {
            g.Surfaces.Add(template);
        }

        int dropped = 0;
        foreach (Face f in g.Faces)
        {
            if (dropped < 2 && f.SurfaceIndex >= 0 && !f.IsPortalFace)
            {
                f.SurfaceIndex = -1;
                dropped++;
            }
        }

        LintFinding finding = Assert.Single(LevelLinter.Lint(doc.Rfl).Findings, f => f.Category == LintCategory.LightmapSurfaceCap);
        Assert.Equal(LintSeverity.Error, finding.Severity);
        Assert.Contains("2 lit faces", finding.Message);
        string over = Run(doc, "local s = level.stats() return s.unlit_faces .. ' ' .. tostring(s.surface_budget_ok) .. ' ' .. tostring(s.unlit_min ~= nil)");
        Assert.Equal("2 false true", over);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ged-cap-" + Guid.NewGuid().ToString("N") + ".rfl");
        try
        {
            doc.Save(path);
            Assert.Contains(doc.LastSaveNotes, n => n.StartsWith("Lightmap surface cap", StringComparison.Ordinal));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ---- kit.dress_trace lights follow the lighting plan --------------------------------------------------------------

    [Fact]
    public void Dress_Trace_Lights_Only_Regions_That_Ask_And_Hold_None_Overhead()
    {
        string got = Run(NewDoc(), """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "dl", seed = 2, lights = false, regions = {
              { name = "a", kind = "room", floor = 0, height = 5, poly = {{0, 0}, {10, 0}, {10, 10}, {0, 10}} },
              { name = "b", kind = "room", floor = 0, height = 5, poly = {{10, 0}, {20, 0}, {20, 10}, {10, 10}} },
              { name = "c", kind = "room", floor = 0, height = 5, poly = {{20, 0}, {30, 0}, {30, 10}, {20, 10}} } },
              openings = { { a = "a", b = "b", at = {10, 5}, width = 2 }, { a = "b", b = "c", at = {20, 5}, width = 2 } } }
            level.place{ kind = "light", pos = {15, 4, 5}, name = "mine" }
            local L = { light_color = {200, 205, 210}, intensity = 1 }
            local d = kit.dress_trace{ trace = tr, regions = { a = { floor = "cem_dark_floor01.tga" }, b = { lighting = L }, c = { lighting = L } } }
            local low = 0
            for _, l in ipairs(d.lights) do if l.pos.y < 3 then low = low + 1 end end
            return string.format("%d %d %d %d", d.regions.a.lights, d.regions.b.lights, d.regions.c.lights, low)
            """);
        string[] v = got.Split(' ');
        Assert.Equal("0", v[0]); // textures only: no lights, no fixtures
        Assert.Equal("0", v[1]); // already holds the script's light: no stacked key lights
        Assert.True(int.Parse(v[2], CultureInfo.InvariantCulture) > 0, "c lights " + v[2]);
        Assert.Equal("0", v[3]); // none hung near the floor
    }

    [Fact]
    public void Place_Seat_Without_The_Game_Meshes_Warns_And_Leaves_The_Object()
    {
        // (With an install mounted, seat lifts the mesh's lowest corner onto pos.y; the CLI run with GED_RF_DIR shows it.)
        EditorDocument doc = NewDoc();
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, "local o = level.place{ kind = 'clutter', class = 'crate1', pos = {0, 0, 0}, seat = true } return o.pos.y");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("0", r.ReturnValue);
        Assert.Contains(log.Entries, e => e.Message.Contains("seat needs", StringComparison.Ordinal));
    }

    [Fact]
    public void Rotate_About_Keeps_Boxes_Beams_And_Hulls_Together()
    {
        string got = Run(NewDoc(), """
            local kit = ged.import("kit")
            local function corners(b) local o = {} for _, f in ipairs(b.faces) do for _, v in ipairs(f.vertices) do o[#o + 1] = { v.x, v.y, v.z } end end return o end
            local pieces = {
              level.brush{ shape = "box", pos = {1, 1, 2}, size = {2, 1, 4} },
              level.brush{ shape = "box", pos = {1, 1, 2}, size = {2, 1, 4}, rot = { yaw = 30 } },
              level.brush_from_points{ points = {{0, 0, 0}, {1, 0, 0}, {0, 1, 0}, {0, 0, 1}, {1, 1, 1}} },
            }
            local bm = kit.beam{ from = {0, 2, 0}, to = {6, 2, 1}, size = 0.3 }
            for _, b in ipairs(bm.brushes or { bm.brush }) do pieces[#pieces + 1] = b end
            local err = 0
            for _, b in ipairs(pieces) do
              local before = corners(b)
              b:rotate_about("y", 90, 5, 0, 3)
              for i, p in ipairs(corners(b)) do
                local x, z = before[i][1] - 5, before[i][3] - 3
                err = math.max(err, math.abs(p[1] - (5 + z)) + math.abs(p[3] - (3 - x)) + math.abs(p[2] - before[i][2]))
              end
            end
            return string.format("%d %.4f", #pieces, err)
            """);
        Assert.EndsWith(" 0.0000", got);
    }

    // ---- The kit's organic budget -------------------------------------------------------------------------------------

    private const string TallCanyon = """
        local kit = ged.import("kit")
        BUDGET
        local tr = kit.trace{ name = "tc", seed = 3, regions = {
          { name = "canyon", kind = "canyon", floor = 0, height = 24, poly = {{-20, -16}, {10, -20}, {22, -6}, {20, 14}, {2, 20}, {-18, 10}} } },
          check = true }
        local n, faces = 0, 0
        level.brushes_tagged("kit", "trace_rock"):each(function(b) n = n + 1 faces = faces + #b.faces end)
        ops.build()
        local s = level.stats()
        return string.format("%d %d %d %d %s %s", tr.holes, n, faces, s.surfaces, tostring(tr.rock_budget and tr.rock_budget.scale or 0), tostring(tr.rock_budget and tr.rock_budget.band_scale or 0))
        """;

    [Fact]
    public void Organic_Budget_Coarsens_Far_Rock_And_Keeps_It_Sealed()
    {
        string full = Run(NewDoc(), TallCanyon.Replace("BUDGET", "kit.defaults.organic_budget = false", StringComparison.Ordinal));
        string budgeted = Run(NewDoc(), TallCanyon.Replace("BUDGET", "", StringComparison.Ordinal));
        string tight = Run(NewDoc(), TallCanyon.Replace("BUDGET", "kit.defaults.organic_budget = 300", StringComparison.Ordinal));
        string[] f = full.Split(' '), b = budgeted.Split(' '), t = tight.Split(' ');
        Assert.Equal("0", f[0]);
        Assert.Equal("0", b[0]);
        Assert.Equal("0", t[0]);
        int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        // The far rock (more than 6 m over the foot of a 24 m wall) is cut 2-3x coarser: far fewer chunks and surfaces.
        Assert.True(I(b[1]) < 0.7 * I(f[1]), $"chunks {b[1]} vs {f[1]}");
        Assert.True(I(b[3]) < 0.8 * I(f[3]), $"surfaces {b[3]} vs {f[3]}");
        Assert.Equal(1.0, D(b[4]), 3);
        // A small budget coarsens the view band and the far rock further.
        Assert.Equal(1.0, D(b[5]), 3);
        Assert.True(D(t[4]) > 1.0 && D(t[5]) > 1.0, "scales " + t[4] + " " + t[5]);
        Assert.True(I(t[1]) < I(b[1]), $"chunks {t[1]} vs {b[1]}");
    }
}
