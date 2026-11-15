using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The population kit (scripts/lib/kit_dress.lua, kit_items.lua, kit_probe.lua) on small rooms: generative
/// fill (kit.dress), light fixtures (kit.light_room), protected spawns (kit.spawn_points) and the item
/// economy (kit.items) — options, keep-outs, walkable floor, density, determinism and acceptance (sealed
/// builds under both methods, lint, one exact undo). The per-gametype gates are in KitPopulationGateTests.
/// </summary>
public sealed class KitPopulationTests
{
    private const string TwoRooms = """
        local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {20, 7, 16} }
        local side = kit.room{ name = "side", pos = {17, 0, 0}, size = {12, 5, 12} }
        local way = kit.doorway{ name = "way", a = yard, b = side }
        """;

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Fields(string ret) =>
        ret.Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty);

    [Fact]
    public void Dress_Fills_A_Room_Keeps_Its_Walkable_Floor_And_Is_Accepted()
    {
        KitRun run = Run(TwoRooms + """
            kit.light_room{ room = yard, theme = "industrial" }
            local d = kit.dress{ room = yard, theme = "industrial", seed = 4 }
            return string.format("el=%d;walk=%.5f;cells=%d;dressed=%d;dens=%.3f;area=%.2f;decals=%d;group=%s",
              d.count, d.walk.change, d.cells.total, d.cells.dressed, d.density, d.area, d.by_kind.decals, tostring(d.group ~= nil))
            """);
        Dictionary<string, string> f = Fields(run.Result.ReturnValue!);
        Assert.InRange(Math.Abs(Num(f["walk"])), 0, 0.02);
        Assert.Equal(f["cells"], f["dressed"]);                       // every 8 m cell gets an element
        Assert.InRange(Num(f["dens"]), 4.5, 10);                      // 6 per 100 m² on top of the fixtures (at least 1 per cell)
        Assert.True(Num(f["decals"]) >= 1, run.Result.ReturnValue);
        Assert.Equal("true", f["group"]);

        // Every dressing brush is detail, tagged, and in the yard; none sits in the doorway's approach.
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        List<Brush> dress = new BrushEditor(run.Doc).Brushes
            .Where(b => tags.TryGetValue(b.Uid, out var t) && t.GetValueOrDefault("kit") == "dress").ToList();
        Assert.NotEmpty(dress);
        Assert.All(dress, b => Assert.True((b.Flags & (uint)BrushFlags.Detail) != 0, "dressing brushes are detail"));
        foreach (Brush b in dress)
        {
            (Vec3 mn, Vec3 mx) = Bounds(b);
            bool nearDoor = mx.X > 10 - 2.4f && mn.Y < 3 && mx.Z > -1.5f - 2.4f && mn.Z < 1.5f + 2.4f && mx.Y < 4.5f;
            Assert.False(nearDoor, $"dressing brush {b.Uid} ({mn} .. {mx}) blocks the doorway's approach");
        }

        AssertAccepted(run);
    }

    [Fact]
    public void Dress_Is_Deterministic_Per_Seed()
    {
        byte[] Bytes(int seed)
        {
            KitRun r = Run(TwoRooms + $"kit.dress{{ room = yard, theme = 'storage', seed = {seed} }}");
            return r.Doc.SaveToBytes(updateTimestamp: false);
        }

        Assert.Equal(Bytes(3), Bytes(3));
        Assert.NotEqual(Bytes(3), Bytes(4));
    }

    [Fact]
    public void Dress_Keeps_Spawns_Items_And_Caller_Areas_Clear()
    {
        KitRun run = Run(TwoRooms + """
            level.place{ kind = "respawn", pos = {-6, 1, 4} }
            level.place{ kind = "item", class = "Shotgun", pos = {4, 0.5, -4} }
            kit.dress{ room = yard, theme = "industrial", seed = 2, density = 2, keep_clear = { { min = {-10, 0, -8}, max = {-6, 3, -4} }, {0, 0, 5, r = 2} } }
            """);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        foreach (Brush b in new BrushEditor(run.Doc).Brushes.Where(b => tags.TryGetValue(b.Uid, out var t) && t.GetValueOrDefault("kit") == "dress"))
        {
            (Vec3 mn, Vec3 mx) = Bounds(b);
            if (mn.Y > 2.5f)
            {
                continue; // overhead structure
            }

            Assert.False(Overlaps(mn, mx, -6, 4, 1.6f), $"dressing brush {b.Uid} stands within 1.6 m of the spawn");
            Assert.False(Overlaps(mn, mx, 4, -4, 1.2f), $"dressing brush {b.Uid} stands within 1.2 m of the item");
            Assert.False(Overlaps(mn, mx, 0, 5, 2f), $"dressing brush {b.Uid} stands in the keep_clear circle");
            Assert.False(mx.X > -10 + 0.3f && mn.X < -6 - 0.3f && mx.Z > -8 + 0.3f && mn.Z < -4 - 0.3f, $"dressing brush {b.Uid} stands in the keep_clear box");
        }
    }

    // True when the brush's footprint comes closer than r to (x, z), less the 0.25 m half-step of the
    // kit's 0.5 m floor lattice (keep-outs are marked on lattice columns).
    private static bool Overlaps(Vec3 mn, Vec3 mx, float x, float z, float r)
    {
        float dx = MathF.Max(0, MathF.Max(mn.X - x, x - mx.X));
        float dz = MathF.Max(0, MathF.Max(mn.Z - z, z - mx.Z));
        return MathF.Sqrt((dx * dx) + (dz * dz)) < r - 0.36f;
    }

    [Theory]
    [InlineData("industrial")]
    [InlineData("construction")]
    [InlineData("storage")]
    [InlineData("control")]
    [InlineData("mine")]
    [InlineData("canyon")]
    public void Every_Theme_Dresses_A_Room_Within_The_Walk_Budget(string theme)
    {
        KitRun run = Run(TwoRooms + "local d = kit.dress{ room = yard, theme = '" + theme + "', seed = 1 }\n" +
            "return string.format('%d;%.5f', d.count, d.walk.change)");
        string[] p = run.Result.ReturnValue!.Split(';');
        Assert.True(int.Parse(p[0], CultureInfo.InvariantCulture) >= 10, $"{theme}: {run.Result.ReturnValue}");
        Assert.InRange(Math.Abs(Num(p[1])), 0, 0.02);
        AssertWatertight(run.Doc, sharedBsp: true);
        AssertLintClean(run);
    }

    /// <summary>
    /// A small storage room (100 m², 4 m high) is where the storage theme stands its racks: a rack's shelf boards over
    /// a low stack leave a column a walk step high but with too little headroom under the top board at 2.40 m, so
    /// that column is lost floor and counts against the walk budget. Seeds 5 and 6 took 2.5 % of the floor when it
    /// was not counted.
    /// </summary>
    [Fact]
    public void Dress_Counts_Floor_Under_A_Low_Shelf_Against_The_Walk_Budget()
    {
        KitRun run = Run("""
            local out = {}
            for i, seed in ipairs({ 4, 5, 6 }) do
              local r = kit.room{ name = "store" .. i, pos = { (i - 1) * 30, 0, 0 }, size = {10, 4, 10} }
              local d = kit.dress{ room = r, theme = "storage", seed = seed }
              out[#out + 1] = string.format("%d:%.5f", seed, d.walk.change)
            end
            return table.concat(out, ";")
            """);
        foreach (string p in run.Result.ReturnValue!.Split(';'))
        {
            Assert.True(Math.Abs(Num(p.Split(':')[1])) <= 0.02, $"seed {p}: kit.dress changed the walkable floor by more than 2 % ({run.Result.ReturnValue})");
        }

        Assert.DoesNotContain(run.Log.Entries, e => e.Message.Contains("changed the walkable floor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kit.dress{theme = 'jungle'}", "unknown theme 'jungle'")]
    [InlineData("kit.dress{theme = 'yard', densty = 1}", "unknown option 'densty'")]
    [InlineData("kit.dress{density = 5}", "'density' must be between 0.25 and 2")]
    [InlineData("kit.dress{budget = 0.05}", "'budget' must be between 0 and 0.02")]
    [InlineData("kit.dress{room = 'nowhere'}", "no kit room, corridor, cave or layout zone is named 'nowhere'")]
    [InlineData("kit.items{gametype = 'race'}", "'gametype' must be")]
    [InlineData("kit.items{weapons = {'BFG'}}", "is not a multiplayer weapon item")]
    [InlineData("kit.spawn_points{count = 2, checkpoint = 5}", "'checkpoint' must be a Payload_Checkpoint event")]
    [InlineData("kit.light_room{room = kit.room{pos = {0,0,0}, size = {8,4,8}}, fixture = 3}", "'fixture' must be true, false")]
    public void Population_Functions_Reject_Bad_Options(string call, string message)
    {
        ScriptDiagnostic e = Fail("kit.room{name = 'r', pos = {0, 0, 0}, size = {12, 5, 12}}\n" + call);
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LightRoom_Hangs_Each_Light_Under_A_Fixture()
    {
        KitRun run = Run("""
            local r = kit.room{ name = "hall", pos = {0, 0, 0}, size = {16, 6, 16} }
            local lights = kit.light_room{ room = r }
            local bare = kit.light_room{ room = kit.room{ name = "bare", pos = {0, 0, 30}, size = {10, 4, 10} }, fixture = false }
            local out = {}
            for i, l in ipairs(lights) do
              local f = lights.fixtures[i]
              local b = f.bounds
              out[#out + 1] = string.format("%.3f,%.3f,%.3f|%.3f,%.3f|%s", l.pos.x, l.pos.y, l.pos.z, b.min.y, b.max.y, tostring(f.detail))
            end
            return #lights .. ";" .. #lights.fixtures .. ";" .. #bare .. ";" .. #bare.fixtures .. ";" .. table.concat(out, ";")
            """);
        string[] parts = run.Result.ReturnValue!.Split(';');
        Assert.Equal("6", parts[0]);   // 256 m² under 6 m: about 1.8 per 100 m², at least 1.7 -> a 3 x 2 grid
        Assert.Equal("6", parts[1]);   // one fixture per light (no game tables: the detail lamp brush)
        Assert.Equal("0", parts[3]);   // fixture = false: bare lights
        foreach (string p in parts.Skip(4))
        {
            string[] q = p.Split('|');
            double[] pos = q[0].Split(',').Select(Num).ToArray();
            double[] fy = q[1].Split(',').Select(Num).ToArray();
            Assert.Equal(6.0, fy[1], 3);                  // flush under the 6 m ceiling
            Assert.True(pos[1] < fy[0] && pos[1] > fy[0] - 0.6, $"light at y {pos[1]} is not just under its fixture ({fy[0]})");
            Assert.Equal("true", q[2]);
        }

        AssertAccepted(run);
    }

    [Fact]
    public void SpawnPoints_Spread_Apart_Clear_Of_Doorways_Facing_The_Way_Out()
    {
        KitRun run = Run(TwoRooms + """
            local s = kit.spawn_points{ rooms = { yard, side }, count = 4, team = "red", seed = 2 }
            local out = {}
            for _, o in ipairs(s) do
              local f = o.rot.forward
              out[#out + 1] = string.format("%s@%.3f,%.3f,%.3f>%.3f,%.3f:%s%s", o.name, o.pos.x, o.pos.y, o.pos.z, f.x, f.z,
                tostring(o:get("red_team")), tostring(o:get("blue_team")))
            end
            return table.concat(out, ";") .. "|" .. string.format("%.3f", s.report.nn_min)
            """);
        string[] halves = run.Result.ReturnValue!.Split('|');
        string[] spawns = halves[0].Split(';');
        Assert.Equal(4, spawns.Length);
        Assert.True(Num(halves[1]) >= 7.99, $"nearest spawns only {halves[1]} m apart");
        var pts = new List<(double X, double Y, double Z, double Fx, double Fz)>();
        foreach (string s in spawns)
        {
            Assert.EndsWith(":truefalse", s, StringComparison.Ordinal);
            string[] a = s.Split('@')[1].Split('>');
            double[] p = a[0].Split(',').Select(Num).ToArray();
            double[] f = a[1].Split(':')[0].Split(',').Select(Num).ToArray();
            pts.Add((p[0], p[1], p[2], f[0], f[1]));
            Assert.Equal(1.0, p[1], 3);                               // 1 m above the floor
            Assert.True(Math.Sqrt(((p[0] - 10) * (p[0] - 10)) + (p[2] * p[2])) >= 2.5, $"spawn {s} stands in the doorway");
        }

        // Both rooms get spawns (spread over the zones).
        Assert.Contains(pts, p => p.X < 10);
        Assert.Contains(pts, p => p.X > 11);
        // Every spawn faces away from its wall into the room (toward the doorway or the open floor).
        Assert.All(pts, p => Assert.True(Math.Abs((p.Fx * p.Fx) + (p.Fz * p.Fz) - 1) < 1e-2));
        AssertAccepted(run);
    }

    [Fact]
    public void SpawnPoints_Join_A_Payload_Checkpoint_Group()
    {
        KitRun run = Run(TwoRooms + """
            local cp = level.place{ kind = "event", class = "Payload_Checkpoint", pos = {0, 2, 0} }
            local s = kit.spawn_points{ room = yard, count = 2, team = "blue", checkpoint = cp }
            local links = cp:links()
            return #links .. "," .. tostring(links[1] == s[1].uid or links[2] == s[1].uid)
            """);
        Assert.Equal("2,true", run.Result.ReturnValue);
    }

    [Fact]
    public void Kit_Lists_Link_And_Group_As_They_Are_And_Dress_Counts_Are_Numbers()
    {
        // kit.spawn_points returns its spawns with a named report beside them: link_many and create_group take that
        // list as it is (named fields beside the items are ignored; named fields alone stay an error). kit.dress
        // returns count as a number (like kit.items) with the split in by_kind.
        KitRun run = Run(TwoRooms + """
            local s = kit.spawn_points{ room = yard, count = 2, team = "red" }
            assert(s.report ~= nil)
            local cp = level.place{ kind = "event", class = "Payload_Checkpoint", pos = {0, 2, 0} }
            local r = cp:link_many(s)
            local g = level.create_group{ name = "spawns", objects = s }
            local d = kit.dress{ room = yard, seed = 2 }
            local ok = ged.try(function() cp:link_many{ report = 1 } end)
            return string.format("%d;%d;%d;%s;%s;%s", #r.linked, #cp:links(), #g.objects, type(d.count),
              tostring(d.count > 0 and type(d.by_kind) == "table" and d.by_kind.props ~= nil and d.by_kind.elements == nil), tostring(ok))
            """);
        Assert.Equal("2;2;2;number;true;false", run.Result.ReturnValue);
    }

    [Fact]
    public void Items_Meet_The_Density_And_Spacing_Targets()
    {
        KitRun run = Run("""
            local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {24, 7, 20} }
            local east = kit.room{ name = "east", pos = {22, 0, 0}, size = {14, 5, 14} }
            local west = kit.room{ name = "west", pos = {-22, 0, 0}, size = {14, 5, 14} }
            kit.corridor{ a = hall, b = east }
            kit.corridor{ a = hall, b = west }
            kit.spawn_points{ rooms = { hall, east, west }, count = 5 }
            local it = kit.items{ rooms = { hall, east, west }, seed = 3 }
            return string.format("per100=%.4f;wnn=%.3f;s2i=%.3f;w=%d;a=%d;h=%d;ar=%d;p=%d;n=%d",
              it.per100, it.weapon_nn.median or -1, it.spawn_to_item.max or -1, it.by_category.weapon, it.by_category.ammo, it.by_category.health,
              it.by_category.armor, it.by_category.powerup, it.count)
            """);
        Dictionary<string, string> f = Fields(run.Result.ReturnValue!);
        Assert.InRange(Num(f["per100"]), 1.2, 2.0);
        Assert.InRange(Num(f["wnn"]), 7, 10);
        Assert.InRange(Num(f["s2i"]), 0, 6.0);
        Assert.True(Num(f["w"]) >= 2 && Num(f["a"]) >= 2 && Num(f["h"]) >= 1 && Num(f["ar"]) >= 1, run.Result.ReturnValue);
        List<LevelObject> items = run.Doc.Objects.Where(o => o.Kind == LevelObjectKind.Item).ToList();
        Assert.Equal(int.Parse(f["n"], CultureInfo.InvariantCulture), items.Count);
        AssertAccepted(run);
    }

    // Two 16 x 8 x 16 rooms with a 1 m wall at x = 8..9, joined by <opener>; then <extra>. Returns whether the
    // probe's foot graph joins the two rooms' centres, the walkable area over both, and the column / exact air
    // state 1 m up in the middle of the wall gap.
    private static Dictionary<string, string> Probe(string opener, string extra = "")
    {
        KitRun run = Run(
            "local a = kit.room{ name = 'a', pos = {0, 0, 0}, size = {16, 8, 16} }\n" +
            "local b = kit.room{ name = 'b', pos = {17, 0, 0}, size = {16, 8, 16} }\n" +
            opener + "\n" + extra + "\n" + """
            local P = ged.import("kit_probe")
            local pr = P.shared()
            local L = pr:lattice(-8, -8, 25, 8, -0.5, 1, 2)
            local all = {}
            for id = 1, #L.nodes do all[id] = true end
            local comp = P.components(L, all)
            local na, nb = P.nearest(L, 0, 0, 0), P.nearest(L, 17, 0, 0)
            local area = P.walk_area(L)
            return string.format("conn=%s;area=%.3f;air=%s;exact=%s",
              tostring(na ~= nil and nb ~= nil and comp[na] ~= 0 and comp[na] == comp[nb]), area,
              tostring(pr:air(8.5, 1, 0)), tostring(pr:air_exact(8.5, 1, 0)))
            """);
        return Fields(run.Result.ReturnValue!);
    }

    /// <summary>
    /// An air portal brush carves after every ordinary brush (RED applies its portal list after its brush list):
    /// a kit.doorway / kit.opening{portal = true} opening is walkable to the probe, so the walk graph joins the two
    /// rooms and the walkable floor matches the same layout with a plain opening (within 2 %).
    /// </summary>
    [Theory]
    [InlineData("kit.doorway{ name = 'd', a = a, b = b, portal = true }", "kit.doorway{ name = 'd', a = a, b = b }")]
    [InlineData("kit.opening{ name = 'd', a = a, b = b, shape = 'door', portal = true }", "kit.opening{ name = 'd', a = a, b = b, shape = 'door' }")]
    [InlineData("kit.opening{ name = 'd', a = a, b = b, shape = 'arch', portal = true }", "kit.opening{ name = 'd', a = a, b = b, shape = 'arch' }")]
    public void Probe_Walks_Through_A_Portal_Opening(string portal, string plain)
    {
        Dictionary<string, string> p = Probe(portal), q = Probe(plain);
        Assert.Equal("true", q["conn"]);
        Assert.Equal("true", p["conn"]);
        Assert.Equal("true", p["air"]);
        Assert.Equal("true", p["exact"]);
        double aq = Num(q["area"]);
        Assert.InRange(Num(p["area"]), aq * 0.98, aq * 1.02);

        // A solid brush made after the portal opening does not refill it: the portal carves last.
        Dictionary<string, string> later = Probe(portal, "level.brush{ pos = {8.5, 2, 0}, size = {1, 4, 3} }");
        Assert.Equal("true", later["conn"]);
        Assert.Equal("true", later["exact"]);
    }

    /// <summary>A solid portal brush (RED's membrane form) adds no solid and carves nothing.</summary>
    [Fact]
    public void Probe_Ignores_A_Solid_Portal_Brush()
    {
        const string Membrane = "local m = level.brush{ pos = {8.5, 2, 0}, size = {1, 4, 3}, portal = true }\nm:set_air(false)";
        Dictionary<string, string> inDoor = Probe("kit.doorway{ name = 'd', a = a, b = b }", Membrane);
        Assert.Equal("true", inDoor["conn"]);
        Assert.Equal("true", inDoor["exact"]);
        Dictionary<string, string> inWall = Probe(string.Empty, Membrane);
        Assert.Equal("false", inWall["conn"]);
        Assert.Equal("false", inWall["exact"]);
    }

    [Fact]
    public void Dress_Keeps_A_Portal_Doorway_Clear()
    {
        string rooms = TwoRooms.Replace("b = side }", "b = side, portal = true }", StringComparison.Ordinal);
        Assert.Contains("portal = true", rooms, StringComparison.Ordinal);
        KitRun run = Run(rooms + """
            local d = kit.dress{ room = yard, theme = "industrial", seed = 4 }
            return string.format("walk=%.5f;el=%d", d.walk.change, d.count)
            """);
        Dictionary<string, string> f = Fields(run.Result.ReturnValue!);
        Assert.InRange(Math.Abs(Num(f["walk"])), 0, 0.02);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        List<Brush> dress = new BrushEditor(run.Doc).Brushes
            .Where(b => tags.TryGetValue(b.Uid, out var t) && t.GetValueOrDefault("kit") == "dress").ToList();
        Assert.NotEmpty(dress);
        foreach (Brush b in dress)
        {
            (Vec3 mn, Vec3 mx) = Bounds(b);
            bool nearDoor = mx.X > 10 - 2.4f && mn.Y < 3 && mx.Z > -1.5f - 2.4f && mn.Z < 1.5f + 2.4f && mx.Y < 4.5f;
            Assert.False(nearDoor, $"dressing brush {b.Uid} ({mn} .. {mx}) blocks the portal doorway's approach");
        }
    }

    [Fact]
    public void Population_Functions_Have_Kit_Docs()
    {
        string source = ScriptKitSource.Combined()!;
        string[] names = ScriptApiReference.ParseKitDocs(source).Select(d => d.Name).ToArray();
        Assert.Contains("dress", names);
        Assert.Contains("items", names);
        Assert.Contains("spawn_points", names);
        Assert.Contains("light_room", names);
        Assert.Single(names, n => n == "spawn_points");
        Assert.Contains("kit.dress{", ScriptApiReference.Describe("kit")!.Text, StringComparison.Ordinal);
        Assert.Contains("kit_dress", ScriptKitSource.ModuleNames());
        Assert.Contains("kit_items", ScriptKitSource.ModuleNames());
    }

    private static (Vec3 Min, Vec3 Max) Bounds(Brush b)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (Vec3 v in b.Geometry.Vertices)
        {
            Vec3 w = BrushTransform.WorldVertex(b, v);
            min = Vec3Math.Min(min, w);
            max = Vec3Math.Max(max, w);
        }

        return (min, max);
    }
}
