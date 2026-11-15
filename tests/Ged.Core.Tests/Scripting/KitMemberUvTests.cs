using System;
using System.Globalization;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Thin members built by the design kit, read back with level.uv_report (members): kit.space structure (truss
/// catwalk, gallery posts, rack posts and beams), kit.trim posts and frames, kit.beam posts, kit.catwalk and
/// kit.railing primitives, and a final level.uv_align{} over everything all lay a girder or trim texture along each
/// member with at most one repeat across it, readable (grain_along, no_repeats_across and mapped all 1). The Lua
/// surface: uv_align members = "only" | "off" and texture_info.grain. Needs an RF install for texture sizes; skipped
/// without one.
/// </summary>
public sealed class KitMemberUvTests
{
    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string RunWithInstall(string install, string body)
    {
        using AssetVfs vfs = GameMount.Mount(install);
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Assets = vfs },
            "local kit = ged.import('kit')\n" + body,
            new ScriptRunOptions { ChunkName = "members", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return r.ReturnValue ?? string.Empty;
    }

    private const string Report =
        "local function rep(q)\n" +
        "  local m = level.uv_report{brushes = q, issues = 0}.members\n" +
        "  if m == nil then return 'none' end\n" +
        "  return string.format('%d %.3f %.3f %.3f %s', m.members, m.grain_along, m.no_repeats_across, m.mapped, tostring(m.vertical_grain_along))\n" +
        "end\n";

    private static void AssertLaid(string line, int minMembers, double minMapped = 1.0)
    {
        string[] p = line.Split(' ');
        Assert.True(p.Length == 5, "no members measured: " + line);
        Assert.True(int.Parse(p[0], CultureInfo.InvariantCulture) >= minMembers, line);
        Assert.Equal(1.0, D(p[1]), 3); // grain along every member
        Assert.Equal(1.0, D(p[2]), 3); // never repeats across a member
        Assert.True(D(p[3]) >= minMapped - 1e-3, line); // readable, not smeared (library modules keep their source mapping)
        Assert.True(p[4] == "nil" || D(p[4]) == 1.0, line);
    }

    [Fact]
    public void Kit_Space_Structure_Lays_Girders_Along_Every_Post_Beam_And_Truss_Member()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        string r = RunWithInstall(install, Report +
            "kit.space{archetype = 'atrium', name = 'a', pos = {0, 0, 0}, lights = false}\n" +
            "kit.space{archetype = 'storage', name = 's', pos = {60, 0, 0}, lights = false, trim_texture = 'mtl_girder07.tga'}\n" +
            "return rep(level.brushes_tagged('kit'))");
        AssertLaid(r, 16);
    }

    [Fact]
    public void Kit_Trim_Posts_And_Frames_And_Kit_Beams_Carry_The_Grain_Along_And_A_Final_Uv_Align_Keeps_Them()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        string r = RunWithInstall(install, Report +
            "local room = kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 6, 12}}\n" +
            "kit.trim{room = room, palette = kit.palette{theme = 'industrial'}, base = false, top = false}\n" +
            "kit.beam{name = 'p1', from = {-4, 0, 3}, to = {-4, 5.5, 3}, size = 0.4, texture = 'mtl_girder06.tga'}\n" +
            "kit.beam{name = 'b1', from = {-6, 5.5, 0}, to = {6, 5.5, 0}, size = {0.4, 0.5}, texture = 'sld_woodpost01.tga'}\n" +
            "local before = rep(level.brushes_tagged('kit'))\n" +
            "level.uv_align{}\n" +
            "return before .. '|' .. rep(level.brushes_tagged('kit'))");
        string[] parts = r.Split('|');
        AssertLaid(parts[0], 4);
        AssertLaid(parts[1], 4);
    }

    [Fact]
    public void Primitive_Catwalk_And_Railing_Posts_Rails_And_Edge_Beams_Are_Laid_Along()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        // custom = false and an empty library folder are not needed: the mine style's catwalk and railing fall back
        // to primitives (posts, rails, deck edge beams) in stock girder and timber textures.
        string r = RunWithInstall(install, Report +
            "local room = kit.room{name = 'r', pos = {0, 0, 0}, size = {24, 8, 16}}\n" +
            "kit.catwalk{from = {-8, 3.5, -4.5}, to = {8, 3.5, -4.5}, width = 2.5, style = 'mine'}\n" +
            "kit.railing{from = {-9, 0, 4}, to = {9, 0, 4}, style = 'mine'}\n" +
            "return rep(level.brushes_tagged('kit', 'catwalk')) .. '|' .. rep(level.brushes_tagged('kit', 'railing'))");
        string[] parts = r.Split('|');
        foreach (string p in parts)
        {
            if (p != "none")
            {
                AssertLaid(p, 1, 0.0); // library modules among them keep their source level's mapping
            }
        }
    }

    [Fact]
    public void Members_Off_Stacks_A_Girder_Up_A_Post_And_Only_Mode_Fixes_Just_The_Members()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install: skipped
        }

        string r = RunWithInstall(install, Report +
            "local room = kit.room{name = 'r', pos = {0, 0, 0}, size = {12, 6, 12}}\n" +
            "local post = level.brush{pos = {2, 3, 2}, size = {0.5, 6, 0.25}, texture = 'mtl_chromegirder02.tga', detail = true}\n" +
            "local wall = level.brush{pos = {-2, 3, -2}, size = {3, 6, 0.25}, texture = 'mtl_chromegirder02.tga', detail = true}\n" +
            "post:box_map{ppm = 64} wall:box_map{ppm = 64}\n" +
            "local w0 = wall.faces[1].uvs[1].u\n" +
            "level.uv_align{brushes = {post, wall}, members = 'off'}\n" +
            "local off = rep(post)\n" +
            "local w1 = wall.faces[1].uvs[1].u\n" +
            "local a = level.uv_align{brushes = {post, wall}, members = 'only'}\n" +
            "local w2 = wall.faces[1].uvs[1].u\n" +
            "assert(catalog.texture_info('mtl_chromegirder02.tga').grain == 'u')\n" +
            "assert(catalog.texture_info('sld_woodpost01.tga').grain == 'v')\n" +
            "assert(catalog.texture_info('cem_greypanel01.tga').grain == nil)\n" +
            "return off .. '|' .. rep(post) .. '|' .. tostring(w1 == w2) .. '|' .. a.members .. '|' .. tostring(w0 ~= nil)");
        string[] parts = r.Split('|');
        Assert.Equal(0.0, D(parts[0].Split(' ')[1]), 3); // upright like a wall: the girder stacks across the post
        AssertLaid(parts[1], 1);
        Assert.Equal("true", parts[2]); // members = "only" left the 3 m wide panel alone
        Assert.Equal("4", parts[3]);
    }
}
