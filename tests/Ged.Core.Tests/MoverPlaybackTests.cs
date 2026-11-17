using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The mover preview's playback (<see cref="MoverPlayback"/>): the game's timing (travel times or speeds, pauses,
/// trapezoid ramps, Alpine easing), the movement types' keyframe order, Alpine curves only with Legacy Movers off,
/// facing (Keyframe Rotation, Follow Path, Force Orient, Rotate in Place) and the member pose; plus RED's Propagate
/// Sounds / Properties and the Level Flags buttons, and their Lua calls.
/// </summary>
public sealed class MoverPlaybackTests
{
    private static EditorDocument NewLevel(bool legacyMovers = false)
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15, 12, 0, 0));
        if (legacyMovers)
        {
            LevelPropertyAccess.Set(doc, AlpineLevelPropertiesSchema.Fields.Single(f => f.Field.EffectiveKey == "legacy_movers"), true);
        }

        doc.MarkSaved();
        return doc;
    }

    /// <summary>A mover with a 2 m box at the origin and keyframes at the given points (the first is the box centre).</summary>
    private static (EditorDocument Doc, Group Mover, MoverService Movers) Mover(int movementType, bool legacy = false, params Vec3[] more)
    {
        EditorDocument doc = NewLevel(legacy);
        int box = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "lift");
        foreach (Vec3 p in more)
        {
            movers.AddKeyframe(g, p, Mat3.Identity);
        }

        g.MovingData!.MovementType = movementType;
        foreach (Keyframe k in g.MovingData.Keyframes)
        {
            k.DepartTravelTime = 2f;
            k.ReturnTravelTime = 4f;
        }

        return (doc, g, movers);
    }

    private static MoverPlayback Play(EditorDocument doc, Group g) => new(MoverPlaybackPath.From(doc.Rfl, g)!);

    private static InspectorField KeyField(string key) => MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == key);

    [Fact]
    public void A_One_Way_Mover_Travels_Its_Segment_In_Its_Travel_Time_And_Stops()
    {
        (EditorDocument doc, Group g, _) = Mover(1, false, new Vec3(10, 0, 0));
        MoverPlayback p = Play(doc, g);
        Assert.True(p.CanPlay);
        Assert.Equal(0f, p.Pose().Now.X);

        p.PlayPause();
        Assert.Equal(2f, p.RunLength, 4);
        p.Tick(1f);
        Assert.Equal(5f, p.Pose().Now.X, 3); // linear: no ramps, no easing
        p.Tick(1.5f);
        Assert.False(p.Playing);
        Assert.Equal(10f, p.Pose().Now.X, 3);
        Assert.Equal(1, p.State.From);
    }

    [Fact]
    public void Ramps_Follow_The_Trapezoid_And_A_Short_Travel_Time_Is_Raised_To_Cover_Them()
    {
        Assert.Equal(0.5f * (1f / 1.5f) * 0.25f, MoverTiming.Trapezoid(0.5f, 2f, 1f, 0f), 5); // accelerating: ½·a·t²
        Assert.Equal(0.5f, MoverTiming.Trapezoid(1f, 2f, 0.5f, 0.5f), 5);                 // symmetric ramps: half way at half time
        Assert.Equal(3f, MoverTiming.EffectiveTravelParam(1f, 2f, 1f, timeIsVelocity: false)); // raised to accel + decel
        Assert.Equal(1f, MoverTiming.EffectiveTravelParam(1f, 2f, 1f, timeIsVelocity: true));  // a speed is not raised
        Assert.Equal(5f, MoverTiming.TravelSeconds(2f, 10f, timeIsVelocity: true));         // 10 m at 2 m/s

        float a = 3f, d = 3f;
        MoverTiming.FitRamps(5f, true, ref a, ref d); // speed-driven ramps keep within 90% of the time
        Assert.Equal(4.5f, a + d, 4);
    }

    [Theory]
    [InlineData(1, 0.25f)]
    [InlineData(2, 0.75f)]
    [InlineData(3, 0.5f)]
    public void Alpine_Easing_Shapes_The_Progress(byte ease, float expected) =>
        Assert.Equal(expected, MoverTiming.Ease(ease, 0.5f), 5);

    [Fact]
    public void Ping_Pong_Once_Goes_Out_Pauses_And_Returns_On_Its_Return_Time()
    {
        (EditorDocument doc, Group g, _) = Mover(2, false, new Vec3(10, 0, 0));
        g.MovingData!.Keyframes[1].PauseTime = 1f;
        MoverPlayback p = Play(doc, g);
        p.PlayPause();
        Assert.Equal(2f + 1f + 4f, p.RunLength, 4); // depart 2, pause 1, return 4 (the departing keyframe's return time)
        p.Tick(2.5f);
        Assert.True(p.State.Pause > 0f);
        Assert.Equal(10f, p.Pose().Now.X, 3);
        p.Tick(0.5f + 2f); // the rest of the pause, then half way back
        Assert.Equal(5f, p.Pose().Now.X, 3);
        p.Tick(3f);
        Assert.False(p.Playing);
        Assert.Equal(0f, p.Pose().Now.X, 3);
    }

    [Fact]
    public void A_Loop_Runs_Its_Closing_Segment_And_Scrubbing_Repeats_Exactly()
    {
        (EditorDocument doc, Group g, _) = Mover(5, false, new Vec3(10, 0, 0), new Vec3(10, 0, 10));
        MoverPlayback p = Play(doc, g);
        Assert.True(p.Path.Closing);
        p.PlayPause();
        Assert.Equal(6f, p.RunLength, 4); // three segments of 2 s, back to the start
        p.Scrub(5f / 6f);
        Vector3 at = p.Pose().Now;
        Assert.Equal(5f, at.X, 3); // half way along the closing segment from (10,0,10) to the origin
        Assert.Equal(5f, at.Z, 3);
        Assert.False(p.Playing);
    }

    [Fact]
    public void Curves_Play_Only_With_Legacy_Movers_Off()
    {
        foreach (bool legacy in new[] { false, true })
        {
            (EditorDocument doc, Group g, _) = Mover(1, legacy, new Vec3(10, 0, 0), new Vec3(10, 0, 10));
            if (!legacy)
            {
                foreach (Keyframe k in g.MovingData!.Keyframes)
                {
                    InspectorAccess.Set(doc, doc.FindByUid(k.Uid)!, KeyField("path_segment"), 1);
                }
            }

            MoverPlayback p = Play(doc, g);
            p.PlayPause();
            p.Tick(3f); // half way along the second segment
            Vector3 at = p.Pose().Now;
            if (legacy)
            {
                Assert.Equal(new Vector3(10, 0, 5), at); // stock: straight
                Assert.Null(p.Path.Curves[1]);
            }
            else
            {
                Assert.NotNull(p.Path.Curves[1]);
                Assert.True(at.X > 10.1f, $"a curved segment bulges out of the chord: {at}");
            }
        }
    }

    [Fact]
    public void Legacy_Movers_Ignore_Alpine_Easing_As_The_Game_Does()
    {
        (EditorDocument doc, Group g, _) = Mover(1, false, new Vec3(10, 0, 0));
        InspectorAccess.Set(doc, doc.FindByUid(g.MovingData!.Keyframes[0].Uid)!, KeyField("path_ease"), 1);
        MoverPlayback eased = Play(doc, g);
        eased.PlayPause();
        eased.Tick(1f);
        Assert.Equal(2.5f, eased.Pose().Now.X, 3); // ease in: x² at half time

        LevelPropertyAccess.Set(doc, AlpineLevelPropertiesSchema.Fields.Single(f => f.Field.EffectiveKey == "legacy_movers"), true);
        MoverPlayback stock = Play(doc, g);
        stock.PlayPause();
        stock.Tick(1f);
        Assert.Equal(5f, stock.Pose().Now.X, 3);
    }

    [Fact]
    public void Rotate_In_Place_Spins_About_The_First_Keyframe_Up_Axis()
    {
        (EditorDocument doc, Group g, _) = Mover(1, false);
        g.MovingData!.RotateInPlace = 1;
        g.MovingData.Keyframes[0].DegreesAboutAxis = 90f;
        MoverPlayback p = Play(doc, g);
        Assert.True(p.CanPlay);
        p.PlayPause();
        p.Tick(2f);
        MoverPose pose = p.Pose();
        (Vec3 pos, Mat3 rot) = pose.Place(new Vec3(1, 0, 0), Mat3.Identity);
        Assert.Equal(0f, pos.X, 3);       // a member 1 m along +X swings a quarter turn about +Y
        Assert.Equal(1f, MathF.Abs(pos.Z), 3);
        Assert.Equal(1f, MathF.Abs(rot.Right.Z), 3);
    }

    [Fact]
    public void Follow_Path_Faces_Along_The_Path_And_Members_Start_Where_The_Level_Places_Them()
    {
        (EditorDocument doc, Group g, MoverService movers) = Mover(1, false, new Vec3(0, 0, 10), new Vec3(10, 0, 10));
        InspectorAccess.Set(movers, g, MoverInspectorSchema.MoverPathFields.Single(f => f.EffectiveKey == "path_orientation"), 1);
        MoverPlayback p = Play(doc, g);
        (Vec3 rest, _) = p.Pose().Place(new Vec3(0, 0, 1), Mat3.Identity);
        Assert.Equal(1f, rest.Z, 3); // at rest the pose is the reference: members where the level placed them

        p.PlayPause();
        p.Tick(3f); // on the second segment, heading +X: the mover has turned a quarter
        MoverPose pose = p.Pose();
        Assert.True(pose.Turns);
        Assert.Equal(1f, pose.ToFrame.F.X, 3);
    }

    [Fact]
    public void Force_Orient_Aims_Along_The_Chord_Without_A_Path_Facing()
    {
        (EditorDocument doc, Group g, _) = Mover(1, true, new Vec3(10, 0, 0));
        g.MovingData!.ForceOrient = 1;
        MoverPlayback p = Play(doc, g);
        p.PlayPause();
        p.Tick(0.5f);
        MoverPose pose = p.Pose();
        Assert.True(pose.Aimed);
        Assert.Equal(1f, pose.Aim.F.X, 3);
    }

    [Fact]
    public void The_Preview_Starts_At_The_Starting_Keyframe_And_Never_Writes_The_Level()
    {
        (EditorDocument doc, Group g, _) = Mover(3, false, new Vec3(10, 0, 0), new Vec3(10, 0, 10));
        g.MovingData!.StartingKeyframe = 1; // stored zero-based: the second keyframe
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;
        MoverPlayback p = Play(doc, g);
        Assert.Equal(1, p.State.From);
        Assert.Equal(new Vector3(10, 0, 0), p.Pose().Now);
        p.PlayPause();
        p.Tick(7.3f);
        p.Scrub(0.4f);
        p.Stop();
        Assert.Equal(1, p.State.From);
        Assert.Equal(nodes, doc.Undo.NodeCount);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Timing_Ticks_Bunch_Where_The_Mover_Is_Slow()
    {
        (EditorDocument doc, Group g, _) = Mover(1, false, new Vec3(10, 0, 0));
        g.MovingData!.Keyframes[0].AccelTime = 1f;
        IReadOnlyList<Vector3> ticks = Play(doc, g).TimingTicks();
        Assert.Equal(7, ticks.Count); // 2 s in quarter seconds
        Assert.True(ticks[1].X - ticks[0].X < ticks[6].X - ticks[5].X, "ticks are closer while accelerating");
    }

    // ---- RED's Propagate buttons --------------------------------------------------------------------------------

    private static (EditorDocument Doc, MoverService Movers, Group A, Group B, Group C) ThreeDoors()
    {
        EditorDocument doc = NewLevel();
        var be = new BrushEditor(doc);
        var movers = new MoverService(doc);
        Group Door(float x, string name)
        {
            int box = be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(x, 0, 0), Mat3.Identity);
            Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), name);
            movers.AddKeyframe(g, new Vec3(x, 3, 0), Mat3.Identity);
            return g;
        }

        Group a = Door(0, "a");
        Group b = Door(10, "b");
        Group c = Door(20, "c");
        a.MovingData!.Keyframes[0].ScriptName = "Door";
        b.MovingData!.Keyframes[1].ScriptName = "door"; // RED compares case-insensitively
        c.MovingData!.Keyframes[0].ScriptName = "other";
        doc.MarkSaved();
        return (doc, movers, a, b, c);
    }

    [Fact]
    public void Propagate_Sounds_Copies_The_Four_Sounds_To_Movers_With_A_Keyframe_Of_That_Name()
    {
        (EditorDocument doc, MoverService movers, Group a, Group b, Group c) = ThreeDoors();
        a.MovingData!.StartSound = "door_start.wav";
        a.MovingData.LoopingVol = 0.5f;
        a.MovingData.CloseSound = "door_close.wav";
        int nodes = doc.Undo.NodeCount;

        MoverPropagationResult r = MoverPropagation.PropagateSounds(movers, a.MovingData.Keyframes[0]);
        Assert.Null(r.Refusal);
        Assert.Equal(2, r.Movers);
        Assert.Equal("door_start.wav", b.MovingData!.StartSound);
        Assert.Equal(0.5f, b.MovingData.LoopingVol);
        Assert.Equal("door_close.wav", b.MovingData.CloseSound);
        Assert.Equal(string.Empty, c.MovingData!.StartSound);
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);

        doc.Undo.Undo();
        Assert.Equal(string.Empty, b.MovingData.StartSound);
        Assert.Equal(1f, b.MovingData.LoopingVol);
    }

    [Fact]
    public void Propagate_Properties_Copies_The_Chain_To_Movers_And_The_Timing_To_Keyframes_Of_That_Name()
    {
        (EditorDocument doc, MoverService movers, Group a, Group b, Group c) = ThreeDoors();
        a.MovingData!.IsDoor = 1;
        a.MovingData.MovementType = 2;
        Keyframe src = a.MovingData.Keyframes[0];
        src.PauseTime = 3f;
        src.AccelTime = 0.25f;
        src.EventUid = 77;

        MoverPropagationResult r = MoverPropagation.PropagateProperties(movers, src);
        Assert.Equal(2, r.Movers);
        Assert.Equal(2, r.Keyframes);
        Assert.Equal((byte)1, b.MovingData!.IsDoor);
        Assert.Equal(2, b.MovingData.MovementType);
        Keyframe named = b.MovingData.Keyframes[1];
        Assert.Equal(3f, named.PauseTime);
        Assert.Equal(0.25f, named.AccelTime);
        Assert.Equal(77, named.EventUid);
        Assert.Equal(0f, b.MovingData.Keyframes[0].PauseTime); // other keyframes keep theirs
        Assert.Equal((byte)0, c.MovingData!.IsDoor);

        doc.Undo.Undo();
        Assert.Equal((byte)0, b.MovingData.IsDoor);
        Assert.Equal(-1, named.EventUid);
    }

    [Theory]
    [InlineData("Keyframe", "default script name")]
    [InlineData("", "without a script name")]
    public void Propagation_Refuses_A_Default_Or_Empty_Name(string name, string message)
    {
        (EditorDocument doc, MoverService movers, Group a, _, _) = ThreeDoors();
        a.MovingData!.Keyframes[0].ScriptName = name;
        int nodes = doc.Undo.NodeCount;
        MoverPropagationResult r = MoverPropagation.PropagateSounds(movers, a.MovingData.Keyframes[0]);
        Assert.Contains(message, r.Refusal, StringComparison.Ordinal);
        Assert.Equal(nodes, doc.Undo.NodeCount);
    }

    [Fact]
    public void Lua_Propagates_And_Refuses_Like_The_Buttons()
    {
        (EditorDocument doc, _, _, Group b, _) = ThreeDoors();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local m = level.find_mover('a') m:set('start_sound', 'x.wav') return m:propagate_sounds(m.keyframes[1]) .. ',' .. m:propagate_properties(m.keyframes[1])");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("2,2", r.ReturnValue);
        Assert.Equal("x.wav", b.MovingData!.StartSound);
        ScriptApiV2ContractTests.AssertApiError(doc, "local m = level.find_mover('a') m.keyframes[2]:set('script_name', 'Keyframe') m:propagate_sounds(m.keyframes[2])",
            "default script name", "script name");
    }

    // ---- Level Flags --------------------------------------------------------------------------------------------

    [Fact]
    public void Outside_And_Inside_Flag_Every_Room_Effect_And_Compiled_Room_In_One_Step()
    {
        EditorDocument doc = NewLevel();
        doc.Rfl.GetOrCreateSection(Ged.Core.IO.Rfl.SectionType.RoomEffects, () => new RoomEffectsSection());
        RoomEffectsSection live = doc.Rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>().Single();
        live.Effects.Clear();
        live.Effects.Add(new RoomEffect { EffectType = RoomEffectsSection.EffectNone });
        live.Effects.Add(new RoomEffect { EffectType = RoomEffectsSection.EffectSkyRoom, RoomIsOutside = 1 });
        GeometrySection? geo = doc.Rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault();
        geo?.Geometry.Rooms.Add(new Room());
        int nodes = doc.Undo.NodeCount;

        int changed = LevelRoomFlags.SetAllRooms(doc, outside: true);
        Assert.Equal(geo is null ? 1 : 2, changed);
        Assert.All(live.Effects, e => Assert.Equal((byte)1, e.RoomIsOutside));
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        Assert.Equal(0, LevelRoomFlags.SetAllRooms(doc, outside: true)); // already so: nothing to do

        doc.Undo.Undo();
        Assert.Equal((byte)0, live.Effects[0].RoomIsOutside);
        Assert.Equal((byte)1, live.Effects[1].RoomIsOutside);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "return level.set_all_rooms_outside(false)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.All(live.Effects, e => Assert.Equal((byte)0, e.RoomIsOutside));
    }
}
