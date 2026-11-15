using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Texture defaults and UV tools (DESIGN-QUALITY-SPEC §(c.3), chunk Q2): the Lua face/brush UV operations
/// (align_to_edge, wrap_from, fit_faces), kit.palette, kit.trim (sealed and lint-clean under both build methods),
/// slope-aligned stairs and ramps, and pixels-per-metre mapping by the real texture size when a library is mounted.
/// </summary>
public sealed class KitUvTests
{
    private static string Ret(KitRun run) => run.Result.ReturnValue ?? string.Empty;

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Runs a read-only kit script (no edits, so no undo step) and returns its value.</summary>
    private static string Eval(string body)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(FixedDate), "local kit = ged.import('kit')\n" + body);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r.ReturnValue ?? string.Empty;
    }

    // ---- face:align_to_edge -------------------------------------------------------------------------

    [Fact]
    public void Align_To_Edge_Puts_A_Tile_Boundary_On_The_Floor_Line()
    {
        KitRun run = Run(
            "local b = level.brush{pos = {0, 2.3, 0}, size = {6, 4, 2}}\n" +
            "local f = b:faces_facing('side')[1]\n" +
            "assert(f:align_to_edge('bottom') == f)\n" +
            "local worst = 0\n" +
            "for i, v in ipairs(f.vertices) do\n" +
            "  local uv = f.uvs[i]\n" +
            "  if math.abs(v.y - 0.3) < 1e-4 then worst = math.max(worst, math.abs(uv.v)) else worst = math.max(worst, math.abs(uv.v + 1)) end\n" +
            "end\n" +
            "return string.format('%.5f', worst)");
        Assert.True(D(Ret(run)) < 1e-4, Ret(run)); // bottom row at V = 0, the top 4 m up at V = -1 (the kept 4 m tile)
    }

    [Fact]
    public void Align_To_Edge_With_Ppm_Maps_A_Slope_At_True_Length()
    {
        // No library mounted: 256 px is the size, so 64 px/m = 0.25 tiles per metre; the 4 m x 2 m wedge's slope is sqrt(20) m.
        KitRun run = Run(
            "local w = level.brush{shape = 'wedge', pos = {0, 1, 0}, size = {4, 2, 3}}\n" +
            "local span\n" +
            "for _, f in ipairs(w.faces) do\n" +
            "  if f.normal.y > 0.2 and f.normal.y < 0.98 then\n" +
            "    f:align_to_edge('bottom', 0, 64)\n" +
            "    local lo, hi = math.huge, -math.huge\n" +
            "    for _, uv in ipairs(f.uvs) do lo = math.min(lo, uv.v) hi = math.max(hi, uv.v) end\n" +
            "    span = hi - lo\n" +
            "  end\n" +
            "end\n" +
            "return string.format('%.4f', span)");
        Assert.Equal(Math.Sqrt(20) / 4, D(Ret(run)), 3);
    }

    [Theory]
    [InlineData("f:align_to_edge('sideways')", "unknown edge 'sideways'")]
    [InlineData("f:align_to_edge(9)", "is not an edge of this face")]
    [InlineData("f:align_to_edge(1.5)", "is not an edge of this face")]
    [InlineData("f:align_to_edge('bottom', 0, -2)", "ppm must be 0")]
    [InlineData("level.brush{}:faces_facing('up')[1]:align_to_edge('bottom')", "no edge of this face is furthest toward 'bottom'")]
    [InlineData("f:align_to_edge(true)", "edge must be a 1-based edge number")]
    public void Align_To_Edge_Rejects_Bad_Edges(string call, string message)
    {
        ScriptDiagnostic e = Fail("local f = level.brush{}:faces_facing('side')[1]\n" + call);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    // ---- face:wrap_from -----------------------------------------------------------------------------

    [Fact]
    public void Wrap_From_Continues_Uvs_Around_A_Column()
    {
        KitRun run = Run(
            "local c = level.brush{pos = {0.3, 3, -0.7}, size = {1.5, 6, 1.5}}\n" +
            "c:box_map{ppm = 64}\n" +
            "local function side(dx, dz) for _, f in ipairs(c:faces_facing('side')) do if f.normal.x * dx + f.normal.z * dz > 0.99 then return f end end end\n" +
            "local s = { side(0, -1), side(1, 0), side(0, 1), side(-1, 0) } -- around the column\n" +
            "for i = 2, #s do assert(s[i]:wrap_from(s[i - 1]) == s[i]) end\n" +
            "-- every corner shared by two consecutive sides has the same UV on both\n" +
            "local worst, pairs_seen = 0, 0\n" +
            "for i = 2, #s do\n" +
            "  local a, b = s[i - 1], s[i]\n" +
            "  for j, p in ipairs(b.vertices) do\n" +
            "    for k, q in ipairs(a.vertices) do\n" +
            "      if math.abs(p.x - q.x) + math.abs(p.y - q.y) + math.abs(p.z - q.z) < 1e-4 then\n" +
            "        pairs_seen = pairs_seen + 1\n" +
            "        worst = math.max(worst, math.abs(b.uvs[j].u - a.uvs[k].u), math.abs(b.uvs[j].v - a.uvs[k].v))\n" +
            "      end\n" +
            "    end\n" +
            "  end\n" +
            "end\n" +
            "return string.format('%d %.5f', pairs_seen, worst)");
        string[] parts = Ret(run).Split(' ');
        Assert.Equal(3 * 2, int.Parse(parts[0], CultureInfo.InvariantCulture)); // three corners, two shared vertices each
        Assert.True(D(parts[1]) < 1e-3, Ret(run));
    }

    [Fact]
    public void Wrap_From_Rejects_A_Missing_Source()
    {
        Assert.Contains("the source face is required", Fail("local f = level.brush{}.faces[1]\nf:wrap_from(nil)").Message, StringComparison.Ordinal);
    }

    // ---- brush:fit_faces ----------------------------------------------------------------------------

    [Fact]
    public void Fit_Faces_Gives_A_Rotated_Crate_One_Tile_Per_Face()
    {
        KitRun run = Run(
            "local c = level.brush{pos = {2, 1, 3}, size = {2, 2, 3}, rot = {yaw = 35}}\n" +
            "assert(c:fit_faces() == c)\n" +
            "local worst = 0\n" +
            "for _, f in ipairs(c.faces) do\n" +
            "  local ulo, uhi, vlo, vhi = math.huge, -math.huge, math.huge, -math.huge\n" +
            "  for _, uv in ipairs(f.uvs) do ulo = math.min(ulo, uv.u) uhi = math.max(uhi, uv.u) vlo = math.min(vlo, uv.v) vhi = math.max(vhi, uv.v) end\n" +
            "  worst = math.max(worst, math.abs(ulo), math.abs(uhi - 1), math.abs(vlo), math.abs(vhi - 1))\n" +
            "end\n" +
            "return string.format('%.5f', worst)");
        Assert.True(D(Ret(run)) < 1e-4, Ret(run));
    }

    [Fact]
    public void Fit_Faces_One_Axis_And_Facing_Fit_Only_What_They_Name()
    {
        KitRun run = Run(
            "local s = level.brush{pos = {0, 0.375, 0}, size = {6, 0.75, 0.1}}\n" +
            "s:fit_faces{axes = 'v', tiles = 2, facing = 'side'}\n" +
            "local f = s:faces_facing('side')[1]\n" +
            "for _, g in ipairs(s:faces_facing('side')) do if g.area > f.area then f = g end end\n" +
            "local ulo, uhi, vlo, vhi = math.huge, -math.huge, math.huge, -math.huge\n" +
            "for _, uv in ipairs(f.uvs) do ulo = math.min(ulo, uv.u) uhi = math.max(uhi, uv.u) vlo = math.min(vlo, uv.v) vhi = math.max(vhi, uv.v) end\n" +
            "return string.format('%.4f %.4f', uhi - ulo, vhi - vlo)");
        string[] parts = Ret(run).Split(' ');
        // Two tiles across 0.75 m (V) and square texels of the 256 px fallback along the 6 m length: 6 / 0.375 = 16 tiles.
        Assert.Equal(16, D(parts[0]), 3);
        Assert.Equal(2, D(parts[1]), 3);
    }

    [Theory]
    [InlineData("b:fit_faces{zz_probe = 1}", "Valid options: axes, tiles, facing.")]
    [InlineData("b:fit_faces{axes = 'w'}", "'axes' must be \"uv\", \"u\" or \"v\"")]
    [InlineData("b:fit_faces{tiles = 0}", "'tiles' must be between 1/64 and 64")]
    [InlineData("b:fit_faces{tiles = {1}}", "'tiles' must be a number or {u, v}")]
    [InlineData("b:fit_faces{facing = 'left'}", "'facing' must be \"all\"")]
    public void Fit_Faces_Rejects_Bad_Options(string call, string message)
    {
        ScriptDiagnostic e = Fail("local b = level.brush{}\n" + call);
        Assert.Contains(message, e.Message + " " + e.Hint, StringComparison.Ordinal);
    }

    // ---- kit.palette ----------------------------------------------------------------------------------

    private static readonly string[] Themes = { "industrial", "bunker", "admin", "cave", "marsbase" };

    [Fact]
    public void Palettes_Are_Coherent_And_Keep_Floors_Off_The_Walls()
    {
        foreach (string theme in Themes)
        {
            foreach (string team in new[] { "nil", "'red'", "'blue'" })
            {
                string value = Eval(
                    $"local p = kit.palette{{theme = '{theme}', variant = 2, team = {team}}}\n" +
                    "local out = {p.theme, p.family, #p.floor, #p.wall, #p.ceiling, #p.trim, #p.post, #p.frame, #p.accent, #p.stripe}\n" +
                    "local walls = {}\n" +
                    "for _, w in ipairs(p.wall) do walls[w] = true end\n" +
                    "local overlap = 0\n" +
                    "for _, f in ipairs(p.floor) do if walls[f] then overlap = overlap + 1 end end\n" +
                    "out[#out + 1] = overlap\n" +
                    "out[#out + 1] = p.textures.floor .. ',' .. p.textures.wall .. ',' .. p.textures.ceiling\n" +
                    "out[#out + 1] = p.trims.base .. ',' .. p.trims.top .. ',' .. p.trims.post .. ',' .. p.trims.frame .. ',' .. p.trims.stripe\n" +
                    "out[#out + 1] = p.frame[1]\n" +
                    "out[#out + 1] = p.wall[2]\n" +
                    "return table.concat(out, '|')");
                string[] f = value.Split('|');
                Assert.Equal(theme, f[0]);
                Assert.True(int.Parse(f[2], CultureInfo.InvariantCulture) >= 3, $"{theme}: floors");
                Assert.True(int.Parse(f[3], CultureInfo.InvariantCulture) >= 6, $"{theme}: walls (zone target 6-14)");
                Assert.True(int.Parse(f[4], CultureInfo.InvariantCulture) >= 2, $"{theme}: ceilings (zone target >= 2)");
                Assert.Equal("0", f[10]);                                   // floor/wall overlap 0
                string[] room = f[11].Split(',');
                Assert.Equal(f[14], room[1]);                               // variant 2 picks the second wall
                Assert.All(f[12].Split(','), t => Assert.EndsWith(".tga", t, StringComparison.Ordinal));
                if (team != "nil")
                {
                    Assert.Contains(team.Trim('\'') == "red" ? "red" : "blu", f[13], StringComparison.Ordinal); // team-keyed frame first
                    Assert.Equal(f[13], f[12].Split(',')[3]);
                }
            }
        }
    }

    [Fact]
    public void Palette_Aliases_And_Errors()
    {
        Assert.Equal("cave admin bunker marsbase", Eval(
            "return kit.palette{theme = 'rock'}.theme .. ' ' .. kit.palette{theme = 'Tech'}.theme .. ' ' .. " +
            "kit.palette{theme = 'concrete'}.theme .. ' ' .. kit.palette{theme = 'mars'}.theme"));
        Assert.Contains("unknown theme 'lava'", Fail("kit.palette{theme = 'lava'}").Message, StringComparison.Ordinal);
        Assert.Contains("Themes: admin, bunker, cave, industrial, marsbase", Fail("kit.palette{}").Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("'team' must be", Fail("kit.palette{theme = 'cave', team = 'green'}").Message, StringComparison.Ordinal);
        Assert.Contains("'variant' must be a whole number", Fail("kit.palette{theme = 'cave', variant = 0}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_Palette_Texture_Is_Stock_With_A_Known_Size()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        using AssetVfs vfs = GameMount.Mount(install);
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Assets = vfs },
            "local kit = ged.import('kit')\nlocal bad = {}\n" +
            "for _, theme in ipairs({'industrial', 'bunker', 'admin', 'cave', 'marsbase'}) do\n" +
            "  for _, team in ipairs({'red', 'blue', false}) do\n" +
            "    local p = kit.palette{theme = theme, team = team or nil}\n" +
            "    for _, role in ipairs({'floor', 'wall', 'ceiling', 'trim', 'post', 'frame', 'accent', 'stripe'}) do\n" +
            "      for _, n in ipairs(p[role]) do\n" +
            "        local t = catalog.texture_info(n)\n" +
            "        if not (t and t.stock and t.width > 0) then bad[#bad + 1] = theme .. '.' .. role .. ' ' .. n end\n" +
            "      end\n" +
            "    end\n" +
            "  end\n" +
            "end\n" +
            "return table.concat(bad, '; ')",
            new ScriptRunOptions { ChunkName = "palette-stock", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.True(string.IsNullOrEmpty(r.ReturnValue), "not stock / no size: " + r.ReturnValue);
    }

    // ---- kit.trim -------------------------------------------------------------------------------------

    private const string TrimScene =
        "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {16, 6, 12}}\n" +       // x -8..8, z -6..6
        "local b = kit.room{name = 'b', pos = {17, 0, 0}, size = {12, 6, 12}}\n" +      // x 11..23
        "local d = kit.doorway{name = 'd', a = a, b = b}\n" +                            // x 8..11, z -1.5..1.5, 4 m
        "kit.platform{name = 'p', pos = {-4, 0, 3}, size = {8, 6}, height = 2}\n" +    // x -8..0, z 0..6
        "kit.stairs{name = 's', pos = {4, 0, 3}, dir = '-x', rise = 2}\n" +             // x 0..4
        "local t = kit.trim{room = a, name = 'trim_a'}\n";

    [Fact]
    public void Trim_Builds_Bands_Posts_A_Door_Frame_And_A_Stripe_And_Stays_Sealed()
    {
        KitRun run = Run(TrimScene +
            "local blocked = 0\n" +
            "for _, br in ipairs(t.brushes) do\n" +
            "  assert(br.detail and br.solid)\n" +
            "  local lo, hi = br.bounds.min, br.bounds.max\n" +
            "  -- no trim piece reaches into the walkable doorway (x 8..11, z -1.5..1.5, y 0..4)\n" +
            "  if hi.x > 8 - 0.2 and lo.z < 1.5 - 1e-3 and hi.z > -1.5 + 1e-3 and lo.y < 4 - 1e-3 then blocked = blocked + 1 end\n" +
            "end\n" +
            "return string.format('%d %d %d %d %d %d %s', t.bands, t.posts, t.frames, t.stripes, t.openings, blocked, tostring(t.group ~= nil))");
        // Base bands: one per wall, the +x wall's split by the doorway (5); top bands: one per wall (4). Posts every
        // ~5 m: 1 + 2 + 2, the +x wall's skipped beside the door. One framed opening, one stripe on the platform's
        // open -z edge (its +x edge meets the stairs, the others the walls).
        Assert.Equal("9 5 1 1 1 0 true", Ret(run));
        AssertAccepted(run);
    }

    [Fact]
    public void Trim_Uvs_Are_Fitted_To_Each_Strip()
    {
        KitRun run = Run(TrimScene +
            "local worst = 0\n" +
            "for _, br in ipairs(t.brushes) do\n" +
            "  local part = br:tags()['kit.part']\n" +
            "  if part:sub(1, 4) == 'base' then\n" +
            "    for _, f in ipairs(br:faces_facing('side')) do\n" +
            "      local lo, hi = math.huge, -math.huge\n" +
            "      for _, uv in ipairs(f.uvs) do lo = math.min(lo, uv.v) hi = math.max(hi, uv.v) end\n" +
            "      worst = math.max(worst, math.abs(hi - lo - 1))\n" +
            "    end\n" +
            "  end\n" +
            "end\n" +
            "return string.format('%.5f', worst)");
        Assert.True(D(Ret(run)) < 1e-4, Ret(run)); // every base-band side shows the trim texture exactly once top to bottom
    }

    [Fact]
    public void Trim_Options_And_Errors()
    {
        KitRun run = Run(
            "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {10, 5, 10}}\n" +
            "local t = kit.trim{room = a, base = false, top = false, posts = false, stripes = false, " +
            "textures = {frame = 'mtl_doortrim.tga'}, palette = kit.palette{theme = 'cave'}}\n" +
            "return #t.brushes .. ' ' .. t.textures.frame .. ' ' .. t.textures.base");
        Assert.Equal("0 mtl_doortrim.tga sld_woodpost01.tga", Ret(run));

        Assert.Contains("single-box record", Fail(
            "local c = kit.corridor{path = {{0, 0, 0}, {10, 0, 0}, {10, 0, 10}}}\nkit.trim{room = c}").Message, StringComparison.Ordinal);
        Assert.Contains("leave less than 2 m of plain wall", Fail(
            "local a = kit.room{pos = {0, 0, 0}, size = {8, 3, 8}}\nkit.trim{room = a, base = 0.75, top = 0.4}").Message, StringComparison.Ordinal);
        Assert.Contains("'palette' must be a kit.palette record", Fail(
            "local a = kit.room{pos = {0, 0, 0}, size = {8, 6, 8}}\nkit.trim{room = a, palette = {}}").Message, StringComparison.Ordinal);
        Assert.Contains("unknown option 'trim'", Fail(
            "local a = kit.room{pos = {0, 0, 0}, size = {8, 6, 8}}\nkit.trim{room = a, textures = {trim = 'x.tga'}}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_Warns_Outside_The_Exemplar_Wall_Kit_Ranges()
    {
        KitRun run = Run("local a = kit.room{pos = {0, 0, 0}, size = {12, 8, 12}}\nkit.trim{room = a, base = 2, posts = 9}");
        Assert.Contains(run.Log.Entries, e => e.Message.Contains("outside the exemplar wall-kit range 0.5 to 1.2 m", StringComparison.Ordinal));
        Assert.Contains(run.Log.Entries, e => e.Message.Contains("every 4 to 6 m", StringComparison.Ordinal));
    }

    // ---- slope-aligned stairs and ramps ---------------------------------------------------------------

    [Theory]
    [InlineData("+x")]
    [InlineData("-x")]
    [InlineData("+z")]
    [InlineData("-z")]
    public void Stair_Treads_Start_At_The_Nosing_And_Run_Across_The_Width(string dir)
    {
        KitRun run = Run(
            "kit.room{name = 'r', pos = {0, 0, 0}, size = {20, 8, 20}}\n" +
            $"local s = kit.stairs{{name = 's', pos = {{0, 0, 0}}, dir = '{dir}', rise = 1, width = 4}}\n" +
            "local dx, dz = 0, 0\n" +
            $"if '{dir}' == '+x' then dx = 1 elseif '{dir}' == '-x' then dx = -1 elseif '{dir}' == '+z' then dz = 1 else dz = -1 end\n" +
            "local worst, treads = 0, 0\n" +
            "for _, b in ipairs(s.brushes) do\n" +
            "  if b:tags()['kit.part']:sub(1, 4) == 'step' then\n" +
            "    local f = b:faces_facing('up')[1]\n" +
            "    treads = treads + 1\n" +
            "    -- the nosing: the corners furthest toward the foot of the stairs\n" +
            "    local front = math.huge\n" +
            "    for _, p in ipairs(f.vertices) do front = math.min(front, p.x * dx + p.z * dz) end\n" +
            "    local ulo, uhi = math.huge, -math.huge\n" +
            "    for i, p in ipairs(f.vertices) do\n" +
            "      local uv = f.uvs[i]\n" +
            "      ulo = math.min(ulo, uv.u) uhi = math.max(uhi, uv.u)\n" +
            "      if math.abs(p.x * dx + p.z * dz - front) < 1e-4 then worst = math.max(worst, math.abs(uv.v)) \n" +
            "      else worst = math.max(worst, math.abs(uv.v + 0.125)) end\n" +
            "    end\n" +
            "    worst = math.max(worst, math.abs(uhi - ulo - 1))\n" +
            "  end\n" +
            "end\n" +
            "return string.format('%d %.5f', treads, worst)");
        // 4 treads; each is a 0.5 m x 4 m plate: V = 0 on its nosing and -0.125 (0.5 m at 0.25 tiles/m) at its back,
        // U spanning the 4 m width (one tile).
        string[] parts = Ret(run).Split(' ');
        Assert.Equal("4", parts[0]);
        Assert.True(D(parts[1]) < 1e-4, Ret(run));
    }

    [Fact]
    public void Ramp_Texture_Runs_Up_The_Slope_At_True_Length()
    {
        KitRun run = Run(
            "kit.room{name = 'r', pos = {0, 0, 0}, size = {20, 8, 20}}\n" +
            "local ramp = kit.ramp{name = 'rp', pos = {-4, 0, 0}, dir = '+z', rise = 2, run = 4}\n" +
            "local out\n" +
            "for _, f in ipairs(ramp.brush.faces) do\n" +
            "  if f.normal.y > 0.2 and f.normal.y < 0.98 then\n" +
            "    local lo, hi = math.huge, -math.huge\n" +
            "    for _, uv in ipairs(f.uvs) do lo = math.min(lo, uv.v) hi = math.max(hi, uv.v) end\n" +
            "    out = hi - lo\n" +
            "  end\n" +
            "end\n" +
            "return string.format('%.4f', out)");
        Assert.Equal(Math.Sqrt(20) / 4, D(Ret(run)), 3); // the box map would give the 4 m plan length: 1.0
        AssertAccepted(run);
    }

    // ---- texel density by real texture size ---------------------------------------------------------

    [Fact]
    public void With_A_Library_Mounted_Every_Kit_Surface_Maps_At_64_Pixels_Per_Metre()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        using AssetVfs vfs = GameMount.Mount(install);
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Assets = vfs },
            "local kit = ged.import('kit')\n" +
            // A 128 px wall, a 256 px floor and a 64x64 px ceiling; a cave in 128 px stripes.
            "local r = kit.room{name = 'r', pos = {0, 0, 0}, size = {12, 6, 12}, " +
            "textures = {wall = 'mtl_hazardstripe01.tga', floor = 'mtl_outerwall02.tga', ceiling = 'mtl_doortrim02.tga'}}\n" +
            "kit.cave{name = 'c', path = {{3, 0, 0}, {20, 0, 0}}, texture = 'mtl_hazardstripe01.tga'}\n",
            new ScriptRunOptions { ChunkName = "ppm", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());

        var sizes = new TextureSizeLookup(vfs);
        int faces = 0;
        foreach (Brush b in new BrushEditor(doc).Brushes)
        {
            foreach (Face f in b.Geometry.Faces.Where(f => !f.IsPortalFace && f.Texture >= 0))
            {
                (int w, int h) = sizes.Get(b.Geometry.Textures[f.Texture]) ?? throw new InvalidOperationException(b.Geometry.Textures[f.Texture]);
                // 64 px of this texture's own size per metre along both axes (square texels); a wall group's height fit
                // (kit.defaults.uv_align) may scale both by at most 15 %.
                Assert.True(UvAlignOps.TryGetUvMap(b.Geometry, f, b.Rotation, b.Position, out UvAlignOps.UvAffine m));
                double pu = m.Gu.Length * w, pv = m.Gv.Length * h;
                Assert.Equal(pu, pv, 1);
                Assert.InRange(pu, 64 / 1.15 - 0.01, 64 * 1.15 + 0.01);

                faces++;
            }
        }

        Assert.True(faces > 20, $"only {faces} faces measured");
    }
}
