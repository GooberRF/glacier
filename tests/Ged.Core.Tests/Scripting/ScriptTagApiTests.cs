using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Provenance tags from Lua: canonical storage, limits, last-untag drops the chunk, cookie
/// blocks coexist, malformed payloads survive, tagged queries, the <c>tags</c> creation option, tag
/// copy on <c>b:copy</c>, the auto-tag hook (exactly the created UIDs, script tags win, off by
/// default) and <see cref="ScriptRunResult.CreatedUids"/>.
/// </summary>
public sealed class ScriptTagApiTests
{
    private static EditorDocument MakeLevel()
    {
        EditorDocument doc = ScriptObjectFieldTests.NewLevel();
        new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(0, 1, 0), Mat3.Identity);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 3, 0));
        doc.MarkSaved();
        return doc;
    }

    private static bool HasMetadata(EditorDocument d) => d.Rfl.Sections.Any(s => s.TypeId == (uint)SectionType.GedObjectMetadata);

    private static string Payload(EditorDocument d, int uid) =>
        Encoding.UTF8.GetString(new GedObjectMetadataService(d).BlockPayload(uid, GedMetadataType.Tags)!);

    private static Func<EditorDocument, ScriptServices> AutoTagging(IReadOnlyDictionary<string, string>? tags) => d => new ScriptServices
    {
        Document = d,
        Confirmation = new AllowAllConfirmation(),
        AutoTags = tags,
    };

    [Fact]
    public void Object_And_Brush_Tags_Are_Stored_Canonically_And_Are_One_Undo_Step()
    {
        ScriptMutationAssert.AssertMutation(MakeLevel,
            "local o = level.objects_of('Light')[1]\n" +
            "o:tag('zeta', 'last'):tag('alpha', 'first'):tag('alpha', 'again')\n" +
            "local b = level.all_brushes():get(1)\n" +
            "b:tag('kit.part', 'floor')\n" +
            "assert(o:tags().alpha == 'again' and o:tags().zeta == 'last')\n" +
            "assert(b:tags()['kit.part'] == 'floor')",
            (d, _) =>
            {
                LevelObject light = d.Objects.Single(o => o.Kind == LevelObjectKind.Light);
                Assert.Equal("{\"alpha\":\"again\",\"zeta\":\"last\"}", Payload(d, light.Uid));
                int brush = new BrushEditor(d).Brushes.Single().Uid;
                Assert.Equal("{\"kit.part\":\"floor\"}", Payload(d, brush));
            });
    }

    [Theory]
    [InlineData("level.objects_of('Light')[1]:tag('k', 5)", "must be a string (got number)", "tostring")]
    [InlineData("level.objects_of('Light')[1]:tag('k', true)", "must be a string (got boolean)", "tostring")]
    [InlineData("level.objects_of('Light')[1]:tag('Upper', 'v')", "Tag key 'Upper' is invalid", null)]
    [InlineData("level.objects_of('Light')[1]:tag('k', string.rep('x', 257))", "at most 256 characters", null)]
    [InlineData("level.objects_of('Light')[1]:tag('k', 'a\\nb')", "control characters", null)]
    [InlineData("local o = level.objects_of('Light')[1] for i = 1, 33 do o:tag('t' .. i, 'v') end", "would carry 33 tags (at most 32)", "untag")]
    [InlineData("level.place{kind = 'Light', pos = {0,0,0}, tags = {'a', 'b'}}", "not a list", null)]
    public void Tag_Limits_Are_Enforced(string src, string message, string? hint) =>
        ScriptApiV2ContractTests.AssertApiError(MakeLevel(), src, message, hint);

    [Fact]
    public void Removing_The_Last_Tag_Drops_The_Chunk()
    {
        EditorDocument doc = MakeLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local o = level.objects_of('Light')[1] o:tag('a', '1') assert(#level.objects_tagged('a').objects == 1) o:untag('a') o:untag('never-set')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.False(HasMetadata(doc));
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Tags_Coexist_With_A_Light_Cookie()
    {
        EditorDocument doc = MakeLevel();
        int light = doc.Objects.Single(o => o.Kind == LevelObjectKind.Light).Uid;
        new GedObjectMetadataService(doc).SetCookie(light, "c.tga");
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local o = level.objects_of('Light')[1] o:tag('a', '1') o:untag('a') o:tag('b', '2')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        var meta = new GedObjectMetadataService(doc);
        Assert.Equal("c.tga", meta.Cookie(light));
        Assert.Equal("{\"b\":\"2\"}", Payload(doc, light));
    }

    [Fact]
    public void A_Malformed_Tag_Payload_Survives_Until_A_Tag_Is_Set()
    {
        EditorDocument doc = MakeLevel();
        int light = doc.Objects.Single(o => o.Kind == LevelObjectKind.Light).Uid;
        new GedObjectMetadataService(doc).SetBlock(light, GedMetadataType.Tags, Encoding.UTF8.GetBytes("not json"));
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local o = level.objects_of('Light')[1] o:untag('x') level.objects_tagged('x'):untag('x') return next(o:tags()) == nil");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("true", r.ReturnValue);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        (r, _) = ScriptTestFixture.Run(doc, "level.objects_of('Light')[1]:tag('k', 'v')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("{\"k\":\"v\"}", Payload(doc, light));
    }

    [Fact]
    public void Tagged_Queries_Filter_By_Key_And_Value_And_Bulk_Tag_Is_One_Step()
    {
        EditorDocument Make()
        {
            EditorDocument d = MakeLevel();
            var be = new BrushEditor(d);
            be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(5, 1, 0), Mat3.Identity);
            d.PlaceObject(LevelObjectKind.Light, new Vec3(5, 3, 0));
            d.MarkSaved();
            return d;
        }

        ScriptMutationAssert.AssertMutation(Make,
            "level.all_brushes():tag('pass', 'blockout')\n" +
            "level.all_brushes():get(2):tag('pass', 'detail')\n" +
            "level.objects_of('Light')[1]:tag('room', 'a')\n" +
            "assert(level.brushes_tagged('pass').count == 2)\n" +
            "assert(level.brushes_tagged('pass', 'detail').count == 1)\n" +
            "assert(level.brushes_tagged('pass', 'detail').uids[1] == level.all_brushes():get(2).uid)\n" +
            "assert(level.objects_tagged('room').count == 1 and level.objects_tagged('room', 'b').count == 0)\n" +
            "level.objects_in{min = {-100, -100, -100}, max = {100, 100, 100}}:where(function(o) return o.kind == 'Light' end):tag('lit', 'yes')\n" +
            "level.brushes_tagged('pass', 'blockout'):untag('pass')\n" +
            "assert(level.brushes_tagged('pass').count == 1 and level.objects_tagged('lit').count == 2)",
            (d, _) => Assert.Equal(2, new GedObjectMetadataService(d).AllTags().Count(kv => kv.Value.ContainsKey("lit"))));

        ScriptApiV2ContractTests.AssertApiError(Make(), "level.brushes_tagged('Bad Key')", "Tag key 'Bad Key' is invalid", null);
    }

    [Fact]
    public void Creation_Tags_And_Copy_Carry_Tags()
    {
        ScriptMutationAssert.AssertMutation(MakeLevel,
            "local b = level.brush{pos = {0, 2, 0}, size = 4, air = true, tags = {kit = 'room', ['kit.name'] = 'room1'}}\n" +
            "local c = b:copy(10, 0, 0)\n" +
            "assert(c:tags().kit == 'room' and c:tags()['kit.name'] == 'room1')\n" +
            "local o = level.place{kind = 'Light', pos = {0, 3, 0}, tags = {kit = 'light_room'}}\n" +
            "assert(o:tags().kit == 'light_room')");
    }

    [Fact]
    public void No_Tags_No_Chunk()
    {
        EditorDocument doc = MakeLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "level.brush{pos = {0, 2, 0}, size = 4, air = true} level.place{kind = 'Light', pos = {0, 3, 0}} level.place_box(4, 1, 0, 1, 1, 1)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.False(HasMetadata(doc));
        Assert.False(doc.SaveToBytes(updateTimestamp: false).AsSpan().IndexOf(BitConverter.GetBytes(0x6ED00002u)) >= 0);
    }

    [Fact]
    public void Auto_Tags_Go_On_Exactly_The_Created_Uids_And_Script_Tags_Win()
    {
        var auto = new Dictionary<string, string> { ["ai.script"] = "gen.lua", ["ai.session"] = "s1" };
        EditorDocument doc = MakeLevel();
        int existingLight = doc.Objects.Single(o => o.Kind == LevelObjectKind.Light).Uid;
        int existingBrush = new BrushEditor(doc).Brushes.Single().Uid;
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local a = level.brush{pos = {0, 2, 0}, size = 4, air = true, tags = {['ai.script'] = 'mine'}}\n" +
            "local l = level.place{kind = 'Light', pos = {0, 3, 0}}\n" +
            "local gone = level.place{kind = 'Light', pos = {1, 3, 0}} gone:delete()\n" +
            "level.objects_of('Light')[1]:set_pos(0, 4, 0)\n" + // an edit, not a creation
            "local m = level.create_mover{brushes = {level.place_box(8, 1, 0, 1, 1, 1)}}\n" +
            "return a.uid .. ',' .. l.uid",
            services: AutoTagging(auto));
        Assert.True(r.Success, r.Error?.ToDisplayString());
        var meta = new GedObjectMetadataService(doc);
        IReadOnlyDictionary<int, Dictionary<string, string>> all = meta.AllTags();
        int[] parts = r.ReturnValue!.Split(',').Select(int.Parse).ToArray();

        Assert.Equal("mine", all[parts[0]]["ai.script"]);   // the script's own tag wins
        Assert.Equal("s1", all[parts[0]]["ai.session"]);
        Assert.Equal("gen.lua", all[parts[1]]["ai.script"]);
        Assert.False(all.ContainsKey(existingLight));        // pre-existing objects are untouched
        Assert.False(all.ContainsKey(existingBrush));

        // CreatedUids: the brush, the light, the box and the mover's start keyframe — not the deleted light.
        Group mover = new MoverService(doc).Movers.Single();
        int[] expected = new[] { parts[0], parts[1], mover.Brushes.Single(), mover.MovingData!.Keyframes[0].Uid }.OrderBy(u => u).ToArray();
        Assert.Equal(expected, r.CreatedUids);
        Assert.Equal(expected, all.Keys.OrderBy(u => u).ToArray());

        // One undo step removes everything, tags included.
        doc.Undo.Undo();
        Assert.False(HasMetadata(doc));
    }

    [Fact]
    public void Auto_Tags_Are_Off_By_Default_And_In_Dry_Run_And_Invalid_Ones_Fail_The_Run()
    {
        EditorDocument doc = MakeLevel();
        (ScriptRunResult off, _) = ScriptTestFixture.Run(doc, "level.place{kind = 'Light', pos = {0, 3, 0}}");
        Assert.True(off.Success);
        Assert.False(HasMetadata(doc));
        Assert.Single(off.CreatedUids);

        var auto = new Dictionary<string, string> { ["ai.session"] = "s" };
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult dry, _) = ScriptTestFixture.Run(doc, "level.place{kind = 'Light', pos = {0, 3, 0}}", dryRun: true, services: AutoTagging(auto));
        Assert.True(dry.Success);
        Assert.Empty(dry.CreatedUids);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        (ScriptRunResult bad, _) = ScriptTestFixture.Run(doc, "level.place{kind = 'Light', pos = {0, 3, 0}}",
            services: AutoTagging(new Dictionary<string, string> { ["Not Valid"] = "x" }));
        Assert.False(bad.Success);
        Assert.Contains("automatic provenance tags are invalid", bad.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        (ScriptRunResult failed, _) = ScriptTestFixture.Run(doc, "level.place{kind = 'Light', pos = {0, 3, 0}} error('x')", services: AutoTagging(auto));
        Assert.False(failed.Success);
        Assert.Empty(failed.CreatedUids);
    }

    private const string AuthoringScript =
        "local room = level.brush{pos = {0, 3, 0}, size = {16, 6, 16}, air = true, tags = {kit = 'room', ['kit.name'] = 'room1'}}\n" +
        "local door = level.brush{pos = {8.5, 1.5, 0}, size = {0.25, 3, 3}, tags = {kit = 'door'}}\n" +
        "local m = level.create_mover{name = 'door1', brushes = {door}, props = {movement_type = 'Ping Pong Once', is_door = true}}\n" +
        "m:add_keyframe{offset = {0, 3, 0}, props = {pause_time = 3, return_travel = 1}}\n" +
        "m.start_keyframe:set('travel_time_to_next', 1)\n" +
        "m:set_hold_open(true)\n" +
        "local t = level.place{kind = 'Trigger', pos = {6, 1, 0}, name = 'door1_trigger', links = {m}, tags = {kit = 'door'}}\n" +
        "local s1 = level.brush{pos = {-4, 0.25, 4}, size = {2, 0.5, 2}}\n" +
        "local s2 = s1:copy(0, 0.5, 2)\n" +
        "level.create_group{name = 'steps', brushes = {s1, s2}}\n" +
        "level.all_brushes():where(function(b) return not b.air end):tag('pass', 'blockout')";

    [Fact]
    public void Level_Authored_With_Movers_Groups_And_Tags_Round_Trips_And_Is_Deterministic()
    {
        var auto = new Dictionary<string, string> { ["ai.session"] = "fixed" };
        byte[] Author()
        {
            EditorDocument d = ScriptObjectFieldTests.NewLevel();
            (ScriptRunResult r, _) = ScriptTestFixture.Run(d, AuthoringScript, services: AutoTagging(auto));
            Assert.True(r.Success, r.Error?.ToDisplayString());
            return d.SaveToBytes(updateTimestamp: false);
        }

        byte[] first = Author();
        Assert.Equal(first, Author()); // same script, same document, same bytes

        EditorDocument reloaded = EditorDocument.OpenBytes(first);
        Assert.Equal(first, reloaded.SaveToBytes(updateTimestamp: false));
        (ScriptRunResult check, _) = ScriptTestFixture.Run(reloaded,
            "local m = level.find_mover('door1')\n" +
            "assert(m and #m.keyframes == 2 and m.hold_open and m:get('is_door') and m:get('movement_type') == 'Ping Pong Once')\n" +
            "assert(m.keyframes[2]:get('pause_time') == 3)\n" +
            "local t = level.objects_named('door1_trigger')[1]\n" +
            "assert(t:links()[1] == m.start_keyframe.uid and t:tags().kit == 'door')\n" +
            "assert(level.find_group('steps') and #level.find_group('steps').brushes == 2)\n" +
            "assert(level.brushes_tagged('kit.name', 'room1').count == 1)\n" +
            "assert(level.brushes_tagged('pass', 'blockout').count == 3)\n" +
            "assert(level.objects_tagged('ai.session', 'fixed').count >= 2)\n" +
            "assert(m.brushes[1]:tags().kit == 'door')");
        Assert.True(check.Success, check.Error?.ToDisplayString());
    }

    [Fact]
    public void Repl_Lines_Report_Their_Own_Created_Uids()
    {
        EditorDocument doc = MakeLevel();
        var log = new ScriptLog();
        using ScriptReplSession repl = ScriptTestFixture.Runner().CreateRepl(AutoTagging(new Dictionary<string, string> { ["ai.session"] = "r" })(doc), new ScriptRunOptions(), log);
        ScriptRunResult first = repl.Eval("x = level.place{kind = 'Light', pos = {0, 3, 0}}");
        ScriptRunResult second = repl.Eval("y = level.place{kind = 'Light', pos = {1, 3, 0}}");
        ScriptRunResult query = repl.Eval("return x.uid");
        Assert.Single(first.CreatedUids);
        Assert.Single(second.CreatedUids);
        Assert.NotEqual(first.CreatedUids[0], second.CreatedUids[0]);
        Assert.Empty(query.CreatedUids);
        Assert.Equal(2, new GedObjectMetadataService(doc).AllTags().Count);
    }
}
