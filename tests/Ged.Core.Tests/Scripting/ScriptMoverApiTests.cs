using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Movers and keyframes from Lua: <c>level.create_mover{}</c> keeps RED's stored-twice
/// invariant, keyframes add/remove (removing the last dissolves), mover fields round-trip through the
/// inspector path, Alpine hold-open writes its table (and undo removes the section), <c>mover:sync()</c>
/// refreshes a stale copy, a member edited during a run is synced automatically, and mover handles
/// work as link targets and UID values.
/// </summary>
public sealed class ScriptMoverApiTests
{
    /// <summary>A File ▸ New level with one 2 m box (UID returned) — no movers / moving_groups / Alpine sections.</summary>
    private static (EditorDocument Doc, int Box) NewLevel()
    {
        EditorDocument doc = ScriptObjectFieldTests.NewLevel();
        int box = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 3, Depth = 0.25f }, new Vec3(2, 1.5f, 0), Mat3.Identity);
        doc.MarkSaved();
        return (doc, box);
    }

    private static EditorDocument MakeLevel() => NewLevel().Doc;

    private static EditorDocument MakeMoverLevel()
    {
        (EditorDocument doc, int box) = NewLevel();
        new MoverService(doc).CreateMover(new[] { box }, Array.Empty<int>(), "door1");
        doc.MarkSaved();
        return doc;
    }

    private static BrushesSection Brushes(EditorDocument d) => d.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().Single();

    private static MoverService Movers(EditorDocument d) => new(d);

    [Fact]
    public void Create_Mover_Keeps_The_Stored_Twice_Invariant_And_Is_One_Undo_Step()
    {
        ScriptRunResult r = ScriptMutationAssert.AssertMutation(MakeLevel,
            "local b = level.all_brushes():get(1)\n" +
            "local m = level.create_mover{name = 'door1', brushes = {b}, props = {is_door = true, movement_type = 'Ping Pong Once'}}\n" +
            "assert(m.name == 'door1' and m.exists and #m.brushes == 1 and #m.keyframes == 1)\n" +
            "assert(b.mover and b.mover.name == 'door1')\n" +
            "assert(m:get('is_door') == true and m:get('movement_type') == 'Ping Pong Once')\n" +
            "assert(level.find_mover('DOOR1') ~= nil and #level.movers == 1)\n" +
            "return m.start_keyframe.uid",
            (d, res) =>
            {
                MoverService mv = Movers(d);
                Group g = mv.Movers.Single();
                int brush = g.Brushes.Single();
                Assert.Contains(Brushes(d).Brushes, b => b.Uid == brush);   // stays in brushes
                Assert.Contains(mv.MoverBrushes, b => b.Uid == brush);      // and is copied to movers
                Assert.Equal(1, g.MovingData!.IsDoor);
                Assert.Equal(2, g.MovingData.MovementType);
                Assert.Equal(new[] { g.MovingData.Keyframes[0].Uid }, res.CreatedUids);
                Assert.Equal(res.ReturnValue, g.MovingData.Keyframes[0].Uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
        Assert.True(r.Success);
    }

    [Theory]
    [InlineData("level.create_mover{name = 'm'}", "'brushes' is required", "brushes = {door}")]
    [InlineData("level.create_mover{brushes = {12345}}", "there is no brush with UID 12345", null)]
    [InlineData("level.create_mover{brushes = {level.all_brushes():get(1)}, speed = 3}", "unknown option 'speed'", "Valid options")]
    [InlineData("level.create_mover{brushes = {level.all_brushes():get(1)}, name = ''}", "'name' must not be empty", null)]
    [InlineData("level.create_mover{brushes = {level.player_start()}}", "takes brushes", "objects")]
    [InlineData("level.create_mover{objects = {level.player_start()}}", "cannot ride a mover", null)]
    [InlineData("local b = level.all_brushes():get(1) level.create_mover{brushes = {b}} level.create_mover{brushes = {b}}", "already part of mover 'Mover'", "one mover only")]
    [InlineData("level.create_mover{brushes = {level.all_brushes():get(1)}, props = {movement_type = 'Sideways'}}", "has no option 'Sideways'", null)]
    public void Create_Mover_Errors_Are_Actionable(string src, string message, string? hint) =>
        ScriptApiV2ContractTests.AssertApiError(MakeLevel(), src, message, hint);

    [Fact]
    public void Add_Keyframe_By_Offset_Or_Pos_With_Props()
    {
        ScriptMutationAssert.AssertMutation(MakeMoverLevel,
            "local m = level.movers[1]\n" +
            "local s = m.start_keyframe.pos\n" +
            "local k = m:add_keyframe{offset = {0, 3, 0}, props = {pause_time = 2, travel_time_to_next = 1.5}}\n" +
            "assert(k.kind == 'Keyframe' and math.abs(k.pos.y - (s.y + 3)) < 1e-5)\n" +
            "assert(k:get('pause_time') == 2)\n" +
            "local k2 = m:add_keyframe{pos = {9, 9, 9}, rot = {yaw = 90}}\n" +
            "assert(k2.pos.x == 9 and #m.keyframes == 3)",
            (d, res) =>
            {
                MovingGroupData data = Movers(d).Movers.Single().MovingData!;
                Assert.Equal(3, data.Keyframes.Count);
                Assert.Equal(2f, data.Keyframes[1].PauseTime);
                Assert.Equal(-1, data.Keyframes[1].EventUid); // RED's none sentinel
                Assert.Equal(new[] { data.Keyframes[1].Uid, data.Keyframes[2].Uid }, res.CreatedUids);
            });

        EditorDocument doc = MakeMoverLevel();
        ScriptApiV2ContractTests.AssertApiError(doc, "level.movers[1]:add_keyframe{pos = {0,0,0}, offset = {0,1,0}}", "exactly one of 'pos'", "offset");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.movers[1]:add_keyframe{}", "exactly one of 'pos'", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.movers[1]:add_keyframe{offset = {0,1,0}, props = {pause = 1}}", "has no field 'pause'", "pause_time");
    }

    [Fact]
    public void Remove_Keyframe_And_Removing_The_Last_Dissolves()
    {
        ScriptMutationAssert.AssertMutation(MakeMoverLevel,
            "local m = level.movers[1]\n" +
            "local k = m:add_keyframe{offset = {0, 3, 0}}\n" +
            "m:remove_keyframe(k)\n" +
            "assert(#m.keyframes == 1 and m.exists)\n" +
            "m:remove_keyframe(m.start_keyframe)\n" +
            "assert(not m.exists and #level.movers == 0)\n" +
            "assert(level.all_brushes():get(1).mover == nil)",
            (d, _) =>
            {
                Assert.Empty(Movers(d).Movers);
                Assert.Empty(Movers(d).MoverBrushes);
                Assert.Single(Brushes(d).Brushes); // the brush is untouched, now static
            });

        (Ged.Core.Scripting.ScriptRunResult gone, _) = ScriptTestFixture.Run(MakeMoverLevel(), "local m = level.movers[1] m:dissolve() return m.name");
        Assert.False(gone.Success);
        Assert.Contains("This mover no longer exists.", gone.Error!.Message, StringComparison.Ordinal);

        ScriptApiV2ContractTests.AssertApiError(MakeMoverLevel(), "level.movers[1]:remove_keyframe(level.player_start())", "is not a keyframe of mover 'door1'", "m.keyframes");
    }

    [Fact]
    public void Dissolve_Is_One_Undoable_Step()
    {
        ScriptMutationAssert.AssertMutation(MakeMoverLevel, "level.find_mover('door1'):dissolve()", (d, _) =>
        {
            Assert.Empty(Movers(d).Movers);
            Assert.Empty(Movers(d).MoverBrushes);
        });
    }

    [Fact]
    public void Mover_Fields_Match_The_Schema_And_Round_Trip()
    {
        EditorDocument doc = MakeMoverLevel();
        string[] keys = MoverInspectorSchema.MoverFields.Concat(MoverInspectorSchema.MoverPathFields).Select(f => f.EffectiveKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        (Ged.Core.Scripting.ScriptRunResult names, _) = ScriptTestFixture.Run(doc,
            "local t = {} for _, f in ipairs(level.movers[1]:fields()) do t[#t+1] = f.name .. ':' .. f.type end return table.concat(t, ',')");
        Assert.True(names.Success, names.Error?.ToDisplayString());
        Assert.StartsWith("movement_type:enum,is_door:bool,", names.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("starting_keyframe:int", names.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("hold_open:bool", names.ReturnValue, StringComparison.Ordinal);
        Assert.EndsWith("path_orientation:enum,riders_turn:bool,riders_detach_on_leave:bool", names.ReturnValue, StringComparison.Ordinal);

        // set(get()) for every field is a byte no-op.
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (Ged.Core.Scripting.ScriptRunResult same, _) = ScriptTestFixture.Run(doc,
            "local m = level.movers[1] for _, f in ipairs(m:fields()) do m:set(f.name, m:get(f.name)) end");
        Assert.True(same.Success, same.Error?.ToDisplayString());
        Assert.Equal(0, same.UndoNodesAdded);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        ScriptApiV2ContractTests.AssertApiError(doc, "level.movers[1]:set('starting_keyframe', 1)", "must be between 0 and 0", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.movers[1]:set('is_door', 1)", "must be a boolean", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.movers[1]:set('start_volme', 1)", "Mover has no field 'start_volme'", "start_volume");

        ScriptMutationAssert.AssertMutation(MakeMoverLevel,
            "level.movers[1]:set_many{start_sound = 'door_open.wav', start_volume = 0.5, movement_type = 3}",
            (d, _) =>
            {
                MovingGroupData data = Movers(d).Movers.Single().MovingData!;
                Assert.Equal("door_open.wav", data.StartSound);
                Assert.Equal(0.5f, data.StartVol);
                Assert.Equal(4, data.MovementType); // 0-based option index 3 = "Loop Once", stored 1-based
            });
    }

    [Fact]
    public void Hold_Open_Writes_The_Alpine_Table_And_Undo_Removes_The_Section()
    {
        ScriptMutationAssert.AssertMutation(MakeMoverLevel,
            "local m = level.movers[1] m:set_hold_open(true) assert(m.hold_open and m:get('hold_open'))",
            (d, _) =>
            {
                AlpineLevelPropertiesSection alp = d.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
                Assert.Equal(new[] { Movers(d).Movers.Single().MovingData!.Keyframes[0].Uid }, alp.HoldOpenKeyframeUids);
            });
        ScriptMutationAssert.AssertMutation(MakeMoverLevel, "level.movers[1]:set('hold_open', true)");
    }

    [Fact]
    public void Sync_After_A_Member_Edit_Updates_The_Copy_And_Member_Transform()
    {
        ScriptMutationAssert.AssertMutation(MakeMoverLevel,
            "local m = level.movers[1]\n" +
            "local b = m.brushes[1]\n" +
            "b:set_pos(2, 5, 0):set_life(40)\n" +
            "m:sync()",
            (d, _) =>
            {
                MoverService mv = Movers(d);
                Group g = mv.Movers.Single();
                Brush member = Brushes(d).Brushes.Single();
                Brush copy = mv.MoverBrushes.Single();
                Assert.Equal(new Vec3(2, 5, 0), copy.Position);
                Assert.Equal(40, copy.Life);
                Assert.Equal(member.Position.Sub(g.MovingData!.Keyframes[0].Position), g.MovingData.MemberTransforms.Single().Position);
            });
    }

    [Fact]
    public void A_Member_Edited_After_The_Mover_Was_Made_Is_Synced_When_The_Run_Ends()
    {
        EditorDocument doc = MakeMoverLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc,
            "local b = level.movers[1].brushes[1] b:move(0, 1, 0) b:set_life(5)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.DoesNotContain(log.Entries, e => e.Level == ScriptLogLevel.Warning);
        ScriptLogEntry info = Assert.Single(log.Entries, e => e.Message.StartsWith("mover 'door1':", StringComparison.Ordinal));
        Assert.Equal(ScriptLogLevel.Info, info.Level);
        Assert.Matches(@"^mover 'door1': refreshed the mover copy of brush \d+, edited after it became a mover member\.$", info.Message);

        // The movers-section copy now matches the edited brush, inside the run's single undo step.
        MoverService mv = Movers(doc);
        Brush member = Brushes(doc).Brushes.Single();
        Brush copy = mv.MoverBrushes.Single();
        Assert.Equal(member.Position, copy.Position);
        Assert.Equal(5, copy.Life);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        // Synced explicitly, edited before the mover existed, or not touched at all: nothing more to do.
        (r, log) = ScriptTestFixture.Run(MakeMoverLevel(), "local m = level.movers[1] m.brushes[1]:move(0, 1, 0) m:sync()");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("refreshed the mover copy", StringComparison.Ordinal));
        (r, log) = ScriptTestFixture.Run(MakeLevel(), "local b = level.all_brushes():get(1) b:move(0, 1, 0) level.create_mover{brushes = {b}}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("refreshed the mover copy", StringComparison.Ordinal));
        // A run that edits other brushes leaves the mover's copy exactly as it was.
        EditorDocument untouched = MakeMoverLevel();
        Brush copyBefore = Movers(untouched).MoverBrushes.Single();
        (r, log) = ScriptTestFixture.Run(untouched, "level.brush{pos = {10, 1, 0}, size = 2}:set_life(3):move(0, 1, 0)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("refreshed the mover copy", StringComparison.Ordinal));
        Assert.Same(copyBefore, Movers(untouched).MoverBrushes.Single());
    }

    [Fact]
    public void A_Member_Edit_Is_Synced_Before_A_Build_In_The_Same_Run()
    {
        EditorDocument doc = MakeMoverLevel();
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc,
            "local b = level.movers[1].brushes[1] b:move(0, 1, 0)\n" +
            "log.info('before build')\n" +
            "ops.build()\n" +
            "log.info('after build')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] lines = log.Entries.Select(e => e.Message).ToArray();
        int synced = Array.FindIndex(lines, m => m.Contains("refreshed the mover copy", StringComparison.Ordinal));
        int beforeBuild = Array.IndexOf(lines, "before build");
        int afterBuild = Array.IndexOf(lines, "after build");
        Assert.True(beforeBuild >= 0 && synced > beforeBuild && synced < afterBuild, string.Join(" | ", lines));
        Assert.Single(lines, m => m.Contains("refreshed the mover copy", StringComparison.Ordinal)); // not again at the end
    }

    [Fact]
    public void Mover_Handles_Are_Link_Targets_And_Uid_Values()
    {
        EditorDocument Make()
        {
            EditorDocument d = MakeMoverLevel();
            d.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 3));
            d.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 5));
            d.MarkSaved();
            return d;
        }

        ScriptMutationAssert.AssertMutation(Make,
            "local m = level.movers[1]\n" +
            "local t = level.objects_of('Trigger')\n" +
            "assert(t[1]:link_to(m))\n" +
            "local r = t[2]:link_many{m}\n" +
            "assert(r.ok and r.linked[1] == m.start_keyframe.uid)\n" +
            "local k = m:add_keyframe{offset = {0, 2, 0}}\n" +
            "k:set('triggered_event_uid', m)\n" +
            "assert(k:get('triggered_event_uid') == m.start_keyframe.uid)",
            (d, _) =>
            {
                int start = Movers(d).Movers.Single().MovingData!.Keyframes[0].Uid;
                foreach (LevelObject t in d.Objects.Where(o => o.Kind == LevelObjectKind.Trigger))
                {
                    Assert.Equal(new[] { start }, LinkModel.LinksOf(t)!.ToArray());
                }
            });
    }

    [Fact]
    public void Keyframe_Objects_Move_And_Delete_Through_Ordinary_Handles()
    {
        ScriptMutationAssert.AssertMutation(MakeMoverLevel,
            "local m = level.movers[1]\n" +
            "local k = m:add_keyframe{offset = {0, 2, 0}}\n" +
            "k:set_pos(1, 2, 3) k:set_rot{yaw = 90}\n" +
            "assert(m.keyframes[2].pos.z == 3)",
            (d, _) => Assert.Equal(new Vec3(1, 2, 3), Movers(d).Movers.Single().MovingData!.Keyframes[1].Position));

        // Deleting a keyframe object (a destructive op, so not in a dry run) maintains the mover.
        EditorDocument doc = MakeMoverLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (Ged.Core.Scripting.ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local m = level.movers[1] local k = m:add_keyframe{offset = {0, 2, 0}} k:delete() assert(#m.keyframes == 1 and m.exists)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Single(Movers(doc).Movers.Single().MovingData!.Keyframes);
        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }
}
