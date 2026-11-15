using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The intent layer of the kit: every placed element carries a role (kit.role) and what it serves (kit.purpose); bridges
/// must serve a route (refused when the floor or another crossing already joins their ends, or when an end lands on no
/// floor), rocks keep to organic spaces and to walls and edges, cover stands on measured lanes, and kit.roles{} reads
/// it all back.
/// </summary>
public sealed class KitPurposeTests
{
    private static (bool Ok, string Value) Run(string body)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(FixedDate), "local kit = ged.import('kit')\n" + body);
        return (r.Success, r.Success ? r.ReturnValue ?? string.Empty : r.Error?.ToDisplayString() ?? string.Empty);
    }

    private const string Chasm =
        "kit.room{name = 'hall', pos = {0, 0, 0}, size = {60, 20, 24}}\n" +
        "kit.platform{name = 'west', pos = {-20, 0, 0}, size = {20, 24}, height = 10}\n" +
        "kit.platform{name = 'east', pos = {20, 0, 0}, size = {20, 24}, height = 10}\n";

    [Fact]
    public void A_Bridge_Over_A_Real_Drop_Serves_A_Route_And_Says_So()
    {
        (bool ok, string v) = Run(Chasm +
            "local b = kit.bridge{name = 'span', from = {-11, 10.125, 0}, to = {11, 10.125, 0}, width = 4, style = 'truss'}\n" +
            "local t = b.deck:tags()\n" +
            "return string.format('%s|%s|%s|%d|%d', b.role, tostring(b.purpose.ok), t['kit.role'], math.floor(b.purpose.catch_a), math.floor(b.purpose.catch_b)) .. '|' .. t['kit.purpose']");
        Assert.True(ok, v);
        string[] p = v.Split('|');
        Assert.Equal("route", p[0]);
        Assert.Equal("true", p[1]);
        Assert.Equal("route", p[2]);
        Assert.True(int.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) >= 150, v);
        Assert.True(int.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) >= 150, v);
        Assert.StartsWith("joins (-11", p[5], StringComparison.Ordinal);
    }

    [Fact]
    public void Bridges_That_Serve_No_Route_Are_Refused_With_The_Reason()
    {
        // A second crossing right beside the first: redundant.
        (bool ok, string v) = Run(Chasm +
            "kit.bridge{name = 'span', from = {-11, 10.125, 0}, to = {11, 10.125, 0}, width = 4, style = 'truss'}\n" +
            "kit.bridge{name = 'twin', from = {-11, 10.125, -6}, to = {11, 10.125, -6}, width = 3, style = 'plank'}");
        Assert.False(ok);
        Assert.Contains("twin serves no route: redundant", v, StringComparison.Ordinal);

        // A deck laid over ordinary floor.
        (ok, v) = Run("kit.room{name = 'flat', pos = {0, 0, 0}, size = {40, 6, 20}}\n" +
            "kit.bridge{name = 'flatbridge', from = {-12, 0.125, 0}, to = {12, 0.125, 0}, width = 3, style = 'plank', landmark = false}");
        Assert.False(ok);
        Assert.Contains("redundant", v, StringComparison.Ordinal);

        // An end in mid-air.
        (ok, v) = Run(Chasm + "kit.bridge{name = 'nowhere', from = {-11, 10.125, 0}, to = {11, 16.125, 0}, width = 3, style = 'plank'}");
        Assert.False(ok);
        Assert.Contains("lands on no walkable floor", v, StringComparison.Ordinal);

        // report builds it anyway, with a warning, and tags it unproven.
        (ok, v) = Run("kit.room{name = 'flat', pos = {0, 0, 0}, size = {40, 6, 20}}\n" +
            "local b = kit.bridge{name = 'flatbridge', from = {-12, 0.125, 0}, to = {12, 0.125, 0}, width = 3, style = 'plank', landmark = false, purpose = 'report'}\n" +
            "return tostring(b.purpose.ok) .. '|' .. #b.warnings .. '|' .. b.deck:tags()['kit.purpose']");
        Assert.True(ok, v);
        Assert.StartsWith("false|1|", v, StringComparison.Ordinal);
        Assert.Contains("unproven: redundant", v, StringComparison.Ordinal);
    }

    [Fact]
    public void Rocks_Keep_To_Organic_Spaces_And_To_Their_Walls()
    {
        (bool ok, string v) = Run("local hall = kit.room{name = 'hall', pos = {0, 0, 0}, size = {30, 6, 24}}\n" +
            "kit.boulders{room = hall, count = 4}");
        Assert.False(ok);
        Assert.Contains("rocks belong in caves, canyons and outdoor ground", v, StringComparison.Ordinal);

        (ok, v) = Run("local g = kit.room{name = 'gully', pos = {0, 0, 0}, size = {30, 10, 24}, texture = 'rck_canyon_rock01.tga'}\n" +
            "local r = kit.boulders{room = g, count = 8, size = {0.6, 1.4}, seed = 2}\n" +
            "local far = 0\n" +
            "for _, b in ipairs(r.brushes) do\n" +
            "  local bb = b.bounds\n" +
            "  local d = math.min(bb.min.x + 15, 15 - bb.max.x, bb.min.z + 12, 12 - bb.max.z)\n" +
            "  if d > 2.5 then far = far + 1 end\n" +
            "end\n" +
            "return r.count .. '|' .. far .. '|' .. r.context .. '|' .. r.brushes[1]:tags()['kit.role']");
        Assert.True(ok, v);
        string[] p = v.Split('|');
        Assert.True(int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) >= 6, v);
        Assert.Equal("0", p[1]);
        Assert.Equal("rock_walls", p[2]);
        Assert.Equal("theme_detail", p[3]);
    }

    [Fact]
    public void Dressing_Carries_Roles_Rocks_Leave_Built_Rooms_And_Cover_Stands_On_Lanes()
    {
        (bool ok, string v) = Run("local hall = kit.room{name = 'hall', pos = {0, 0, 0}, size = {30, 6, 24}}\n" +
            "level.place{kind = 'respawn', pos = {-10, 0, -8}}\n" +
            "local d = kit.dress{room = hall, theme = 'mine', seed = 3, name = 'dress_hall'}\n" +
            "local rocks, untagged, lanes = 0, 0, 0\n" +
            "for _, b in ipairs(d.brushes) do\n" +
            "  local t = b:tags()\n" +
            "  if t['kit.part'] == 'rock' then rocks = rocks + 1 end\n" +
            "  if t['kit.role'] == nil or t['kit.purpose'] == nil then untagged = untagged + 1 end\n" +
            "  if t['kit.role'] == 'cover' and (t['kit.purpose']:find(' lane ', 1, true) or t['kit.purpose']:find('approach', 1, true)) then lanes = lanes + 1 end\n" +
            "end\n" +
            "local r = kit.roles{}\n" +
            "return table.concat({ d.spaces[1].context, rocks, untagged, d.by_kind.cover, lanes > 0 and 'lanes' or 'none', r.counts.cover or 0, r.counts.theme_detail or 0, r.unassigned }, '|')");
        Assert.True(ok, v);
        string[] p = v.Split('|');
        Assert.Equal("built_interior", p[0]);
        Assert.Equal("0", p[1]);  // the mine theme's rocks became its built pieces
        Assert.Equal("0", p[2]);  // every element has a role and a purpose
        Assert.True(int.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) >= 1, v);
        Assert.Equal("lanes", p[4]);
        Assert.True(int.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture) >= 1, v);
        Assert.True(int.Parse(p[6], System.Globalization.CultureInfo.InvariantCulture) >= 1, v);
        Assert.Equal("0", p[7]);
    }

    [Fact]
    public void A_Landmark_Must_Be_Seen()
    {
        // A landmark in a closed 6 x 6 shaft far from the rest of the floor: hidden.
        (bool ok, string v) = Run("kit.room{name = 'yard', pos = {0, 0, 0}, size = {30, 8, 30}}\n" +
            "kit.room{name = 'shaft', pos = {60, 0, 0}, size = {6, 40, 6}}\n" +
            "kit.landmark{name = 'lost', pos = {60, 0, 0}, height = 30, base = 2}");
        Assert.False(ok);
        Assert.Contains("lost would be hidden", v, StringComparison.Ordinal);

        (ok, v) = Run("kit.room{name = 'yard', pos = {0, 0, 0}, size = {60, 46, 30}}\n" +
            "local l = kit.landmark{name = 'mast', pos = {0, 0, 0}, height = 30}\n" +
            "return l.role .. '|' .. tostring(l.purpose.ok) .. '|' .. l.brushes[1]:tags()['kit.role']");
        Assert.True(ok, v);
        Assert.Equal("landmark|true|landmark", v);
    }

    private const string Gallery =
        "kit.room{name = 'hall', pos = {0, 0, 0}, size = {40, 10, 24}}\n" +
        "kit.platform{name = 'gallery', pos = {0, 0, 8}, size = {40, 8}, height = 4}\n";

    [Fact]
    public void A_Flight_Up_To_A_Gallery_Serves_A_Route_And_Says_So()
    {
        (bool ok, string v) = Run(Gallery +
            "local s = kit.stairs{name = 'up', pos = {-10, 0, -4}, dir = '+z', rise = 4, width = 3}\n" +
            "return string.format('%s|%s|%s|%d', s.role, tostring(s.purpose.ok), s.brushes[1]:tags()['kit.role'], math.floor(s.purpose.top_area))");
        Assert.True(ok, v);
        string[] p = v.Split('|');
        Assert.Equal("route", p[0]);
        Assert.Equal("true", p[1]);
        Assert.Equal("route", p[2]);
        Assert.True(int.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) >= 16, v);
    }

    [Fact]
    public void Flights_That_Serve_No_Route_Are_Refused_With_The_Reason()
    {
        // A second flight right beside the first: the floors are already joined on foot nearby.
        (bool ok, string v) = Run(Gallery +
            "kit.stairs{name = 'up', pos = {-10, 0, -4}, dir = '+z', rise = 4, width = 3}\n" +
            "kit.stairs{name = 'twin', pos = {-6.5, 0, -4}, dir = '+z', rise = 4, width = 3}");
        Assert.False(ok);
        Assert.Contains("twin serves no route: redundant", v, StringComparison.Ordinal);

        // A flight that climbs into the hall's wall: no landing.
        (ok, v) = Run(Gallery + "kit.stairs{name = 'wallward', pos = {10, 0, -3.5}, dir = '-z', rise = 4, width = 3}");
        Assert.False(ok);
        Assert.Contains("wallward serves no route: its top", v, StringComparison.Ordinal);
        Assert.Contains("climbs into a wall", v, StringComparison.Ordinal);

        // A flight onto a 3 x 3 m block: its top leads nowhere.
        (ok, v) = Run(Gallery + "kit.platform{name = 'plinth', pos = {14, 0, -6}, size = {3, 3}, height = 2}\n" +
            "kit.stairs{name = 'onto', pos = {14, 0, -10.5}, dir = '+z', rise = 2, width = 2}");
        Assert.False(ok);
        Assert.Contains("onto serves no route: its top leads nowhere", v, StringComparison.Ordinal);

        // report builds it anyway, with the reason on the record.
        (ok, v) = Run(Gallery + "kit.platform{name = 'plinth', pos = {14, 0, -6}, size = {3, 3}, height = 2}\n" +
            "local s = kit.stairs{name = 'onto', pos = {14, 0, -10.5}, dir = '+z', rise = 2, width = 2, purpose = 'report'}\n" +
            "return tostring(s.purpose.ok) .. '|' .. s.purpose.text");
        Assert.True(ok, v);
        Assert.StartsWith("false|serves no route: its top leads nowhere", v, StringComparison.Ordinal);

        // A ramp is a flight too.
        (ok, v) = Run(Gallery +
            "kit.stairs{name = 'up', pos = {-10, 0, -4}, dir = '+z', rise = 4, width = 3}\n" +
            "kit.ramp{name = 'slope', pos = {-7, 0, -4}, dir = '+z', rise = 4, run = 8, width = 3}");
        Assert.False(ok);
        Assert.Contains("slope serves no route: redundant", v, StringComparison.Ordinal);
    }
}
