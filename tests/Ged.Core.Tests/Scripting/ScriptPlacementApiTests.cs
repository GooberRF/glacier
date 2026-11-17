using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Object rotation, <c>level.place{}</c> / <c>level.player_start{}</c> and links
///: per-kind orientation including header-based items/clutter, Forward vs corona-Up aiming,
/// placement of every placeable kind with class validation against (fake) game tables, schema-order
/// props, links stored as a mover's start keyframe, and rejection reasons.
/// </summary>
public sealed class ScriptPlacementApiTests
{
    private static EditorDocument NewLevel() => ScriptObjectFieldTests.NewLevel();

    private static (ScriptRunResult Result, ScriptLog Log) Run(EditorDocument doc, string lua, GameCatalogs? catalogs = null)
    {
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Catalogs = catalogs },
            lua, new ScriptRunOptions { ChunkName = "test", AllowDestructive = true }, log);
        return (r, log);
    }

    private static GameCatalogs FakeCatalogs() => new()
    {
        Items = ItemCatalog.Load("#Items\n$Class Name: \"Shotgun\"\n$HUD Msg Name: \"Shotgun\"\n$Gives Weapon: \"shotgun\"\n\n$Class Name: \"Medical Kit\"\n\n$Class Name: \"Assault Rifle\"\n#End\n"),
    };

    // ---- rotation -------------------------------------------------------------------

    public static TheoryData<LevelObjectKind> RotatableKinds()
    {
        var data = new TheoryData<LevelObjectKind>();
        foreach (PlaceableObjectType p in ObjectFactory.Palette)
        {
            if (ScriptCatalog.KindHasRotation(p.Kind, null))
            {
                data.Add(p.Kind);
            }
        }

        return data;
    }

    [Fact]
    public void Header_Based_Kinds_Are_Rotatable()
    {
        foreach (LevelObjectKind k in new[] { LevelObjectKind.Item, LevelObjectKind.Clutter, LevelObjectKind.ParticleEmitter, LevelObjectKind.Decal })
        {
            Assert.True(ScriptCatalog.KindHasRotation(k, null), $"{k} should report an orientation");
        }

        Assert.False(ScriptCatalog.KindHasRotation(LevelObjectKind.AmbientSound, null));
        Assert.False(ScriptCatalog.KindHasRotation(LevelObjectKind.Trigger, null)); // default sphere
    }

    [Theory]
    [MemberData(nameof(RotatableKinds))]
    public void Set_Rot_Round_Trips_And_Undoes_Exactly(LevelObjectKind kind)
    {
        EditorDocument Make()
        {
            EditorDocument d = NewLevel();
            d.PlaceObject(kind, new Vec3(1, 1, 1));
            d.MarkSaved();
            return d;
        }

        ScriptMutationAssert.AssertMutation(Make,
            $"local o = level.objects_of('{kind}')[1] o:set_rot{{yaw = 90, pitch = 10}} assert(math.abs(o.rot.yaw - 90) < 1e-3 and math.abs(o.rot.pitch - 10) < 1e-3, tostring(o.rot))",
            (d, _) =>
            {
                object model = d.Objects.First(o => o.Kind == kind).Model;
                Mat3 m = ObjectRotation.Get(model)!.Value;
                Assert.True(m.Forward.X > 0.9f, $"{kind}: forward {m.Forward}");
                if (model is Item or Clutter)
                {
                    Assert.Equal(m, ((ObjectHeader)model.GetType().GetProperty("Header")!.GetValue(model)!).Rotation);
                }
            });
    }

    [Fact]
    public void Look_At_Uses_Forward_Except_Corona_Up()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 0, 0));
        doc.PlaceObject(LevelObjectKind.CoronaObject, new Vec3(0, 0, 0));
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local l = level.objects_of('Light')[1]:look_at(10, 0, 0)\n" +
            "local c = level.objects_of('CoronaObject')[1]:look_at(0, 10, 10)\n" +
            "return l.rot.yaw .. ',' .. l.rot.forward.x .. ',' .. c.rot.right.x .. ',' .. c.rot.roll");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("90,1,1,0", r.ReturnValue);

        // The corona's Up axis (its aim) points at the target; Forward is the spin reference.
        var corona = (AlpineCoronaObject)doc.Objects.First(o => o.Kind == LevelObjectKind.CoronaObject).Model;
        Assert.Equal(MathF.Sqrt(0.5f), corona.Orientation.Up.Y, 4);
        Assert.Equal(MathF.Sqrt(0.5f), corona.Orientation.Up.Z, 4);
        Assert.Equal(-MathF.Sqrt(0.5f), corona.Orientation.Forward.Y, 4);
        var light = (Light)doc.Objects.First(o => o.Kind == LevelObjectKind.Light).Model;
        Assert.Equal(1f, light.Rotation.Forward.X, 4);
        Assert.Equal(1f, light.Rotation.Up.Y, 4);

        // Straight up is a vertical facing: the documented +X fallback for the right vector.
        (ScriptRunResult up, _) = ScriptTestFixture.Run(doc, "local l = level.objects_of('Light')[1]:look_at(0, 5, 0) return l.rot.forward.y .. ',' .. l.rot.right.x");
        Assert.True(up.Success, up.Error?.ToDisplayString());
        Assert.Equal("1,1", up.ReturnValue);
    }

    [Fact]
    public void Rotation_Errors_Explain_What_To_Change()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.AmbientSound, Vec3.Zero);
        doc.PlaceObject(LevelObjectKind.Trigger, Vec3.Zero);
        doc.PlaceEvent(EventSchemaCatalog.Find("Message")!, Vec3.Zero);
        doc.PlaceObject(LevelObjectKind.Light, Vec3.Zero);

        (ScriptRunResult nil, _) = ScriptTestFixture.Run(doc, "return tostring(level.objects_of('AmbientSound')[1].rot)");
        Assert.Equal("nil", nil.ReturnValue);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('AmbientSound')[1]:set_rot{yaw = 1}", "AmbientSound objects have no orientation.", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Trigger')[1]:set_rot{yaw = 1}", "Trigger objects have no orientation.", "Set shape to \"Box\" first.");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Event')[1]:look_at(1, 0, 0)", "Event objects have no orientation.", "Teleport");
        ScriptApiV2ContractTests.AssertApiError(doc, "local l = level.objects_of('Light')[1] l:look_at(l.x, l.y, l.z)", "the target is the object's own position", null);

        // A box trigger is oriented (identity until set).
        (ScriptRunResult box, _) = ScriptTestFixture.Run(doc, "local t = level.objects_of('Trigger')[1] t:set('shape', 'Box') t:set_rot{yaw = 45} return t.rot.yaw");
        Assert.True(box.Success, box.Error?.ToDisplayString());
        Assert.Equal("45", box.ReturnValue);
    }

    // ---- level.place{} ------------------------------------------------------------------

    [Fact]
    public void Place_Covers_Every_Placeable_Kind()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult kinds, _) = ScriptTestFixture.Run(doc, "return table.concat(catalog.kinds(), ',')");
        Assert.True(kinds.Success, kinds.Error?.ToDisplayString());
        string[] all = kinds.ReturnValue!.Split(',');
        Assert.Contains("Event", all);
        Assert.Contains("Light", all);
        Assert.DoesNotContain("PlayerStart", all);
        Assert.DoesNotContain("Keyframe", all);

        foreach (string kind in all)
        {
            string cls = kind switch
            {
                "Entity" => ", class = 'Guard'",
                "Item" => ", class = 'First_Aid'",
                "Clutter" => ", class = 'officebookcase'",
                "Event" => ", class = 'Message'",
                _ => string.Empty,
            };
            EditorDocument d = NewLevel();
            d.MarkSaved();
            byte[] before = d.SaveToBytes(updateTimestamp: false);
            (ScriptRunResult r, _) = ScriptTestFixture.Run(d, $"local o = level.place{{kind = '{kind}'{cls}, pos = {{1, 2, 3}}}} return o.kind .. ':' .. o.x .. ',' .. o.y .. ',' .. o.z");
            Assert.True(r.Success, $"{kind}: {r.Error?.ToDisplayString()}");
            Assert.Equal($"{kind}:1,2,3", r.ReturnValue);
            Assert.Equal(1, r.UndoNodesAdded);
            d.Undo.Undo();
            Assert.True(before.AsSpan().SequenceEqual(d.SaveToBytes(updateTimestamp: false)), $"{kind}: undo was not exact");
        }
    }

    [Theory]
    [InlineData("respawn", "MpRespawnPoint")]
    [InlineData("spawn", "MpRespawnPoint")]
    [InlineData("mp_respawn_point", "MpRespawnPoint")]
    [InlineData("corona", "CoronaObject")]
    [InlineData("mesh", "MeshObject")]
    [InlineData("note", "NoteObject")]
    [InlineData("Ambient_Sound", "AmbientSound")]
    [InlineData("LIGHT", "Light")]
    public void Place_Accepts_Kind_Aliases(string kind, string expected)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(NewLevel(), $"return level.place{{kind = '{kind}', pos = {{0, 0, 0}}}}.kind");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(expected, r.ReturnValue);
    }

    [Theory]
    [InlineData("level.place{pos = {0, 0, 0}}", "'kind' is required", "Placeable kinds")]
    [InlineData("level.place{kind = 'ligth', pos = {0, 0, 0}}", "unknown kind 'ligth'", "Light")]
    [InlineData("level.place{kind = 'player_start', pos = {0, 0, 0}}", "the Player Start is unique", "level.player_start")]
    [InlineData("level.place{kind = 'mover', pos = {0, 0, 0}}", "cannot be placed directly", "level.create_mover")]
    [InlineData("level.place{kind = 'keyframe', pos = {0, 0, 0}}", "cannot be placed directly", "level.create_mover")]
    [InlineData("level.place{kind = 'light'}", "'pos' is required", null)]
    [InlineData("level.place{kind = 'item', pos = {0, 0, 0}}", "'class' is required for a Item", "catalog.classes(\"Item\")")]
    [InlineData("level.place{kind = 'light', class = 'X', pos = {0, 0, 0}}", "'class' applies only to", null)]
    [InlineData("level.place{kind = 'event', class = 'Mesage', pos = {0, 0, 0}}", "unknown event class 'Mesage'", "Message")]
    [InlineData("level.place{kind = 'event', pos = {0, 0, 0}}", "'class' is required for an event", null)]
    [InlineData("level.place{kind = 'light', pos = {0, 0, 0}, rot = {yaw = 1}, yaw = 2}", "either 'rot' or 'yaw'", null)]
    [InlineData("level.place{kind = 'light', pos = {0, 0, 0}, position = {1, 1, 1}}", "unknown option 'position'", "'pos'")]
    [InlineData("level.place{kind = 'light', pos = {0, 0, 0}, props = {rnge = 3}}", "Light has no field 'rnge'", null)]
    [InlineData("level.place{kind = 'ambient_sound', pos = {0, 0, 0}, yaw = 90}", "AmbientSound objects have no orientation.", null)]
    [InlineData("level.place{kind = 'light', pos = {0, 0, 0}, links = {1}}", "cannot originate links", "Triggers, Events")]
    public void Place_Errors_Are_Actionable(string lua, string message, string? hint)
    {
        EditorDocument doc = NewLevel();
        doc.MarkSaved();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        ScriptApiV2ContractTests.AssertApiError(doc, lua, message, hint);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), "a failed place changed the level");
    }

    [Fact]
    public void Place_Validates_Classes_Against_The_Game_Tables()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult ok, ScriptLog okLog) = Run(doc, "return level.place{kind = 'item', class = 'shotgun', pos = {0, 0.5, 0}}.class_name", FakeCatalogs());
        Assert.True(ok.Success, ok.Error?.ToDisplayString());
        Assert.Equal("Shotgun", ok.ReturnValue); // stored in the table's spelling
        Assert.DoesNotContain(okLog.Entries, e => e.Message.Contains("not validated", StringComparison.Ordinal));

        (ScriptRunResult bad, _) = Run(doc, "level.place{kind = 'item', class = 'Shotgn', pos = {0, 0, 0}}", FakeCatalogs());
        Assert.False(bad.Success);
        Assert.Contains("unknown Item class 'Shotgn'", bad.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Shotgun", bad.Error.Hint ?? string.Empty, StringComparison.Ordinal);

        // Without tables the class is accepted and one warning per run says it is unvalidated.
        (ScriptRunResult free, ScriptLog log) = Run(NewLevel(),
            "level.place{kind = 'item', class = 'Anything', pos = {0, 0, 0}} level.place{kind = 'item', class = 'Other', pos = {1, 0, 0}}");
        Assert.True(free.Success, free.Error?.ToDisplayString());
        Assert.Single(log.Entries, e => e.Message.Contains("not validated", StringComparison.Ordinal));
    }

    [Fact]
    public void Place_Applies_Name_Props_In_Schema_Order_Then_Rotation_And_Links()
    {
        ScriptMutationAssert.AssertMutation(NewLevel,
            "local ev = level.place{kind = 'event', class = 'Message', pos = {0, 1, 0}, name = 'msg', props = {delay = 2}}\n" +
            "local t = level.place{kind = 'trigger', pos = {0, 0, 4}, name = 'gate', rot = {yaw = 90},\n" +
            "  props = {box_depth = 2, box_width = 4, shape = 'Box', box_height = 3, resets_after = 1.5}, links = {ev}}\n" +
            "local s = level.place{kind = 'respawn', pos = {2, 0, 0}, yaw = -90}\n" +
            "assert(t.rot.yaw == 90 and s.rot.yaw == -90)",
            (d, _) =>
            {
                LevelObject ev = d.Objects.Single(o => o.Kind == LevelObjectKind.Event);
                var t = (Trigger)d.Objects.Single(o => o.Kind == LevelObjectKind.Trigger).Model;
                Assert.Equal("msg", ev.ScriptName);
                Assert.Equal(2f, ((RflEvent)ev.Model).Delay);
                Assert.Equal("gate", t.ScriptName);
                Assert.Equal((Trigger.ShapeBox, 4f, 3f, 2f, 1.5f), (t.Shape, t.BoxWidth!.Value, t.BoxHeight!.Value, t.BoxDepth!.Value, t.ResetsAfter));
                Assert.Equal(new[] { ev.Uid }, t.Links);
                Assert.True(t.Rotation!.Value.Forward.X > 0.99f);
            });
    }

    [Fact]
    public void Player_Start_Is_Created_Moved_Or_Returned()
    {
        // New levels have one: move + rotate it (one undo step, exact undo).
        ScriptMutationAssert.AssertMutation(NewLevel, "local p = level.player_start{pos = {0, 1, -4}, yaw = 180} assert(p.z == -4 and p.rot.yaw == 180)",
            (d, _) =>
            {
                PlayerStartSection ps = d.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>().Single();
                Assert.Equal(new Vec3(0, 1, -4), ps.Position);
                Assert.True(ps.Rotation.Forward.Z < -0.99f);
            });

        // A level without one: nil, an actionable error without pos, and creation with pos.
        EditorDocument bare = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult none, _) = ScriptTestFixture.Run(bare, "return tostring(level.player_start())");
        Assert.Equal("nil", none.ReturnValue);
        ScriptApiV2ContractTests.AssertApiError(bare, "level.player_start{yaw = 0}", "'pos' is required to create one", null);
        ScriptMutationAssert.AssertMutation(() => ScriptTestFixture.NewDoc(entities: 0), "local p = level.player_start{pos = {3, 1, 2}} assert(p.kind == 'PlayerStart')",
            (d, _) => Assert.Equal(new Vec3(3, 1, 2), d.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>().Single().Position));
    }

    // ---- links --------------------------------------------------------------------------

    [Fact]
    public void Link_Many_Reports_Stored_And_Rejected_Targets()
    {
        EditorDocument doc = NewLevel();
        int brush = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, new Vec3(10, 0, 0), Mat3.Identity);
        doc.MarkSaved();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local t = level.place{kind = 'trigger', pos = {0, 0, 0}}\n" +
            "local l = level.place{kind = 'light', pos = {1, 0, 0}}\n" +
            "local e = level.place{kind = 'event', class = 'Message', pos = {2, 0, 0}}\n" +
            $"local rep = t:link_many{{l, e.uid, 99999, level.find_brush({brush}), l}}\n" +
            "local reasons = {} for _, x in ipairs(rep.rejected) do reasons[#reasons + 1] = x.uid .. '=' .. x.reason end\n" +
            "return tostring(rep.ok) .. '|' .. #rep.linked .. '|' .. table.concat(reasons, ';') .. '|' .. #t:links() .. '|' .. #l:links_in()");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] p = r.ReturnValue!.Split('|');
        Assert.Equal("false", p[0]);
        Assert.Equal("2", p[1]);
        Assert.Contains("99999=No object with UID 99999.", p[2], StringComparison.Ordinal);
        Assert.Contains($"{brush}=Brush {brush} is not part of a mover", p[2], StringComparison.Ordinal);
        Assert.Contains("Already linked", p[2], StringComparison.Ordinal);
        Assert.Equal("2", p[3]);
        Assert.Equal("1", p[4]);
    }

    [Fact]
    public void Links_To_A_Mover_Are_Stored_As_Its_Start_Keyframe()
    {
        EditorDocument Make()
        {
            EditorDocument d = NewLevel();
            int b = new BrushEditor(d).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, new Vec3(0, 2, 0), Mat3.Identity);
            new MoverService(d).CreateMover(new[] { b }, Array.Empty<int>(), "Door");
            d.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 3));
            d.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 5));
            d.MarkSaved();
            return d;
        }

        ScriptMutationAssert.AssertMutation(Make,
            "local b = level.brushes[1] local ts = level.objects_of('Trigger')\n" +
            "assert(ts[1]:link_to(b))\n" +
            "local rep = ts[2]:link_many{b}\n" +
            "assert(rep.ok and #rep.linked == 1)",
            (d, _) =>
            {
                // The mover is listed in moving_groups and, as RED stores it, in the editor groups (same instance).
                int start = Assert.Single(new MoverService(d).Movers).MovingData!.Keyframes[0].Uid;
                Assert.All(d.Objects.Where(o => o.Kind == LevelObjectKind.Trigger), t => Assert.Equal(new[] { start }, ((Trigger)t.Model).Links));
            });
    }

    [Fact]
    public void Back_Link_Break_Links_And_Links_In()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local e = level.place{kind = 'event', class = 'Message', pos = {0, 0, 0}}\n" +
            "local t1 = level.place{kind = 'trigger', pos = {1, 0, 0}}\n" +
            "local t2 = level.place{kind = 'trigger', pos = {2, 0, 0}}\n" +
            "local l = level.place{kind = 'light', pos = {3, 0, 0}}\n" +
            "local rep = e:back_link{t1, t2, l}\n" +
            "local a = tostring(rep.ok) .. ',' .. #rep.linked .. ',' .. rep.rejected[1].uid .. ',' .. #e:links_in()\n" +
            "local broke = e:break_links()\n" +
            "return a .. '|' .. tostring(broke) .. ',' .. #e:links_in() .. ',' .. tostring(e:break_links())");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string lightUid = doc.Objects.Single(o => o.Kind == LevelObjectKind.Light).Uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"false,2,{lightUid},2|true,0,false", r.ReturnValue);
    }
}
