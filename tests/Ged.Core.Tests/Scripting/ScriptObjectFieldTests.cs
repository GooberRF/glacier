using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Generic fields: keys, lookup, and value marshalling for every object kind, event
/// class, keyframe and brush field. For each field: <c>set(get())</c> is a byte no-op; a
/// representative new value round-trips through <c>get</c> as one undo node whose undo restores the
/// exact bytes; a wrong-typed value is rejected without changing anything; read-only fields refuse
/// writes; conditional (discriminated) members read nil while they do not apply and refuse writes
/// with a hint naming what to change first.
/// </summary>
public sealed class ScriptObjectFieldTests
{
    // ---- keys ---------------------------------------------------------------------

    [Theory]
    [InlineData("Script Name", "script_name")]
    [InlineData("Size / Range", "size_range")]
    [InlineData("Don't Hum", "dont_hum")]
    [InlineData("Hold Open [Alpine]", "hold_open")]
    [InlineData("Hardness (0–100, 0 allowed [Alpine])", "hardness")]
    [InlineData("Face when finished (int1==1):", "face_when_finished")]
    [InlineData("Start Delay ms", "start_delay_ms")]
    [InlineData("  Spot FOV  ", "spot_fov")]
    public void Keys_Are_Derived_From_Labels(string label, string key) => Assert.Equal(key, InspectorKeys.FromLabel(label));

    [Fact]
    public void Field_Keys_Are_Unique_Per_Schema()
    {
        var problems = new List<string>();
        void Check(string schema, IEnumerable<string> keys)
        {
            foreach (IGrouping<string, string> g in keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add($"{schema}: key '{g.Key}' x{g.Count()}");
            }

            foreach (string k in keys.Where(k => k.Length == 0))
            {
                problems.Add($"{schema}: empty key");
            }
        }

        foreach (LevelObjectKind kind in ObjectInspectorCatalog.Kinds)
        {
            Check(kind.ToString(), ObjectInspectorCatalog.For(kind).Select(f => f.EffectiveKey));
        }

        Check("Keyframe", MoverInspectorSchema.KeyframeFields.Select(f => f.EffectiveKey));
        Check("Mover", MoverInspectorSchema.MoverFields.Select(f => f.EffectiveKey));
        Check("Brush", BrushInspectorCatalog.Fields.Select(f => f.EffectiveKey));
        Check("level.props", LevelPropertiesSchema.Fields.Select(f => f.Field.EffectiveKey));
        Check("level.alpine", AlpineLevelPropertiesSchema.Fields.Select(f => f.Field.EffectiveKey));
        string[] eventPseudo = { "name", "hidden", "pos", "class", "uid", "kind", "delay", "color" };
        foreach (EventSchema s in EventSchemaCatalog.All)
        {
            Check(s.ClassName, eventPseudo.Concat(s.Fields.Select(f => f.EffectiveKey)));
        }

        // The only schema key a pseudo-field shadows is the entity/target/camera "Class" row,
        // which the read-only pseudo-field `class` already covers.
        foreach (LevelObjectKind kind in ObjectInspectorCatalog.Kinds)
        {
            foreach (string k in ObjectInspectorCatalog.For(kind).Select(f => f.EffectiveKey)
                         .Where(k => ScriptFieldCatalog.PseudoKeys.Contains(k) && k != "class"))
            {
                problems.Add($"{kind}: schema key '{k}' collides with a pseudo-field");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ---- every kind × every field ----------------------------------------------------

    public static TheoryData<LevelObjectKind> SchemaKinds()
    {
        var data = new TheoryData<LevelObjectKind>();
        foreach (LevelObjectKind k in ObjectInspectorCatalog.Kinds.OrderBy(k => k))
        {
            if (ObjectFactory.Palette.Any(p => p.Kind == k))
            {
                data.Add(k);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SchemaKinds))]
    public void Generic_Fields_Round_Trip_For_Every_Kind_And_Field(LevelObjectKind kind)
    {
        EditorDocument doc = NewLevel();
        LevelObject o = doc.PlaceObject(kind, new Vec3(1, 2, 3))!;
        doc.MarkSaved();
        ExerciseAllFields(doc, o.Uid, $"{kind}");
    }

    /// <summary>All placeable event classes × fields (~15 s, so DeepGate); the fast tier covers a representative set below.</summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void Generic_Fields_Round_Trip_For_Every_Event_Class() => ExerciseEvents(EventSchemaCatalog.Placeable);

    [Fact]
    public void Generic_Fields_Round_Trip_For_Representative_Event_Classes() => ExerciseEvents(
        new[] { "Message", "Goto", "Skybox_State", "Modify_Rotating_Mover", "Set_Difficulty", "Teleport", "Spawn_Object", "Capture_Point_Handler" }
            .Select(n => EventSchemaCatalog.Find(n)!));

    private static void ExerciseEvents(IEnumerable<EventSchema> classes)
    {
        var failures = new List<string>();
        foreach (EventSchema schema in classes)
        {
            EditorDocument doc = NewLevel();
            LevelObject ev = doc.PlaceEvent(schema, new Vec3(0, 1, 0))!;
            doc.MarkSaved();
            try
            {
                ExerciseAllFields(doc, ev.Uid, schema.ClassName);
            }
            catch (Exception ex) when (ex is Xunit.Sdk.XunitException)
            {
                failures.Add(ex.Message);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void Keyframe_Fields_Round_Trip()
    {
        EditorDocument doc = NewLevel();
        var brushes = new BrushEditor(doc);
        int b = brushes.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, new Vec3(0, 2, 0), Mat3.Identity);
        Group mover = new MoverService(doc).CreateMover(new[] { b }, Array.Empty<int>(), "Lift");
        doc.MarkSaved();
        ExerciseAllFields(doc, mover.MovingData!.Keyframes[0].Uid, "Keyframe");
    }

    [Fact]
    public void Brush_Fields_Round_Trip()
    {
        EditorDocument doc = NewLevel();
        int b = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, new Vec3(0, 2, 0), Mat3.Identity);
        doc.MarkSaved();
        (ScriptRunResult keys, _) = ScriptTestFixture.Run(doc,
            $"local t = {{}} for _, f in ipairs(level.find_brush({b}):fields()) do t[#t + 1] = f.name .. '=' .. f.type .. '=' .. tostring(f.read_only) .. '=' .. table.concat(f.options, '|') end return table.concat(t, ';')");
        Assert.True(keys.Success, keys.Error?.ToDisplayString());
        foreach (string row in keys.ReturnValue!.Split(';'))
        {
            string[] p = row.Split('=');
            ExerciseField(doc, $"level.find_brush({b})", p[0], p[1], p[2] == "true", p[3].Length == 0 ? Array.Empty<string>() : p[3].Split('|'), "Brush");
        }
    }

    // ---- traps, discriminated members, read-only, errors -------------------------------

    [Fact]
    public void Event_Trap_Encodings_Write_What_EventFieldAccess_Writes()
    {
        EditorDocument doc = NewLevel();
        int message = doc.PlaceEvent(EventSchemaCatalog.Find("Message")!, Vec3.Zero)!.Uid;
        int go = doc.PlaceEvent(EventSchemaCatalog.Find("Goto")!, Vec3.Zero)!.Uid;
        int sky = doc.PlaceEvent(EventSchemaCatalog.Find("Skybox_State")!, Vec3.Zero)!.Uid;
        int rot = doc.PlaceEvent(EventSchemaCatalog.Find("Modify_Rotating_Mover")!, Vec3.Zero)!.Uid;
        int diff = doc.PlaceEvent(EventSchemaCatalog.Find("Set_Difficulty")!, Vec3.Zero)!.Uid;

        string lua =
            $"level.find_uid({message}):set('voice_extra_id', 7)\n" +
            $"level.find_uid({go}):set('face_when_finished', true)\n" +
            $"level.find_uid({sky}):set('on', true)\n" +
            $"level.find_uid({rot}):set('mode', 'decrease')\n" +
            $"level.find_uid({diff}):set('difficulty', 'Hard')\n" +
            $"return level.find_uid({message}):get('voice_extra_id') .. ',' .. tostring(level.find_uid({go}):get('face_when_finished')) .. ',' .. " +
            $"tostring(level.find_uid({sky}):get('on')) .. ',' .. level.find_uid({rot}):get('mode') .. ',' .. level.find_uid({diff}):get('difficulty')";
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, lua);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("7,true,true,Decrease,Hard", r.ReturnValue);

        RflEvent Ev(int uid) => (RflEvent)doc.FindByUid(uid)!.Model;
        Assert.Equal(7f, Ev(message).Float1);                      // int stored in float1
        Assert.Equal(1, Ev(go).Int1);                              // bool stored as int 1
        Assert.Equal(EventSchemaCatalog.Find("Skybox_State")!.Field(EventSlot.Str1)!.OnChar, Ev(sky).Str1); // one-char flag
        Assert.Equal("Decrease", Ev(rot).Str1);                    // text dropdown stores the option text (canonical case)
        Assert.Equal(2, Ev(diff).Int1);                            // index dropdown stores the index
    }

    [Fact]
    public void Event_Trap_Undo_Restores_Raw_Slots_Exactly()
    {
        // A non-canonical raw slot (2.6 in the int-in-float slot) must come back byte-exact on undo.
        ScriptMutationAssert.AssertMutation(() =>
        {
            EditorDocument d = NewLevel();
            LevelObject m = d.PlaceEvent(EventSchemaCatalog.Find("Message")!, Vec3.Zero)!;
            ((RflEvent)m.Model).Float1 = 2.6f;
            d.MarkSaved();
            return d;
        }, "level.objects_of('Event')[1]:set('voice_extra_id', 9)", (d, _) =>
            Assert.Equal(9f, ((RflEvent)d.Objects.First(o => o.Kind == LevelObjectKind.Event).Model).Float1));
    }

    [Fact]
    public void Trigger_Shape_And_Dimensions_Round_Trip_Through_Generic_Set()
    {
        EditorDocument Make()
        {
            EditorDocument d = NewLevel();
            d.PlaceObject(LevelObjectKind.Trigger, Vec3.Zero);
            d.MarkSaved();
            return d;
        }

        // Shape is applied before the dimensions (schema order) even though Lua table order differs.
        ScriptMutationAssert.AssertMutation(Make,
            "local t = level.objects_of('Trigger')[1] t:set_many{box_depth = 5, box_width = 4, shape = 'Box', box_height = 3, one_way = true}",
            (d, _) =>
            {
                var t = (Trigger)d.Objects.First(o => o.Kind == LevelObjectKind.Trigger).Model;
                Assert.Equal(Trigger.ShapeBox, t.Shape);
                Assert.Equal((4f, 3f, 5f), (t.BoxWidth!.Value, t.BoxHeight!.Value, t.BoxDepth!.Value));
                Assert.Null(t.SphereRadius);
                Assert.Equal(Mat3.Identity, t.Rotation);

                // Saved and reloaded, the box survives exactly.
                EditorDocument reloaded = EditorDocument.OpenBytes(d.SaveToBytes(updateTimestamp: false));
                var rt = (Trigger)reloaded.Objects.First(o => o.Kind == LevelObjectKind.Trigger).Model;
                Assert.Equal((4f, 3f, 5f, (byte)1), (rt.BoxWidth!.Value, rt.BoxHeight!.Value, rt.BoxDepth!.Value, rt.OneWay!.Value));
            });

        // A box dimension on a sphere trigger reads nil, refuses a write with a hint, and set(get()) is a no-op.
        EditorDocument sphere = Make();
        (ScriptRunResult nilRead, _) = ScriptTestFixture.Run(sphere, "local t = level.objects_of('Trigger')[1] t:set('box_width', t:get('box_width')) return tostring(t:get('box_width'))");
        Assert.True(nilRead.Success, nilRead.Error?.ToDisplayString());
        Assert.Equal("nil", nilRead.ReturnValue);
        ScriptApiV2ContractTests.AssertApiError(sphere, "level.objects_of('Trigger')[1]:set('box_width', 4)", "does not apply", "Set shape to \"Box\" first.");

        // Switching back to a sphere clears the box members and restores a radius.
        (ScriptRunResult back, _) = ScriptTestFixture.Run(Make(),
            "local t = level.objects_of('Trigger')[1] t:set('shape', 'Box') t:set('box_width', 6) t:set('shape', 0) return tostring(t:get('box_width')) .. ',' .. t:get('sphere_radius')");
        Assert.True(back.Success, back.Error?.ToDisplayString());
        Assert.Equal("nil,0", back.ReturnValue);
    }

    [Fact]
    public void Read_Only_Fields_Refuse_Writes()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Light, Vec3.Zero);
        new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, Vec3.Zero, Mat3.Identity);
        doc.PlaceObject(LevelObjectKind.AmbientSound, Vec3.Zero);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Light')[1]:set('uid', 5)", "Field 'uid' is read-only.", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Light')[1]:set('class', 'X')", "Field 'class' is read-only.", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Light')[1]:set('editor_only', true)", "Field 'editor_only' is read-only.", "editor_only_lights");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.brushes[1]:set('time_index', 3)", "Field 'time_index' is read-only.", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('AmbientSound')[1]:set('name', 'snd')", "Field 'name' is read-only.", "no script name");
    }

    [Theory]
    [InlineData("o:set('size_range', 'far')", "must be a number (got string)")]
    [InlineData("o:set('dynamic', 1)", "must be a boolean (got number)")]
    [InlineData("o:set('script', 5)", "must be a string (got number)")]
    [InlineData("o:set('type', true)", "must be a option name or 0-based index (got boolean)")]
    [InlineData("o:set('type', 'Laser')", "has no option 'Laser'")]
    [InlineData("o:set('type', 9)", "option index must be 0 to 3")]
    [InlineData("o:set('color', 'red')", "must be a colour")]
    [InlineData("o:set('color', {300, 0, 0})", "components must be integers 0-255")]
    [InlineData("o:set('pos', 'here')", "'pos' must be a vec")]
    [InlineData("o:set('size_range', 0/0)", "must be a finite number")]
    [InlineData("o:set('size_range', nil)", "needs a float value (got nil)")]
    [InlineData("o:set('rnge', 3)", "Light has no field 'rnge'")]
    [InlineData("o:set_many{range = 3, bogus = 1}", "Light has no field 'bogus'")]
    public void Wrong_Values_Are_Rejected_Without_Change(string lua, string fragment)
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Light, Vec3.Zero);
        doc.MarkSaved();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        ScriptApiV2ContractTests.AssertApiError(doc, "local o = level.objects_of('Light')[1] " + lua, fragment, null);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    [Fact]
    public void Unknown_Field_Suggests_The_Nearest()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Trigger, Vec3.Zero);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Trigger')[1]:get('sphere_radus')", "Trigger has no field 'sphere_radus'", "sphere_radius");
    }

    [Fact]
    public void Lookup_Accepts_Key_Label_Or_Path_And_Values_Marshal()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(1, 2, 3));
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local o = level.objects_of('Light')[1]\n" +
            "o:set('Size / Range', 12):set('Range', 13):set('size_range', 14)\n" +
            "o:set('color', {r = 10, g = 20, b = 30}):set('type', 'Spot'):set('initial_state', 1)\n" +
            "local c = o:get('color')\n" +
            "return o:get('size_range') .. '|' .. c.r .. ',' .. c.g .. ',' .. c.b .. ',' .. c.a .. '|' .. o:get('type') .. '|' .. o:get('initial_state') .. '|' .. o:get('pos').y .. '|' .. o:props().kind");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("14|10,20,30,255|Spot|Off|2|Light", r.ReturnValue);
    }

    [Fact]
    public void Object_Query_Set_Checks_Every_Object_Before_Mutating()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(100, 0, 0));
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(101, 0, 0));
        doc.PlaceObject(LevelObjectKind.AmbientSound, new Vec3(102, 0, 0));
        doc.MarkSaved();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        ScriptApiV2ContractTests.AssertApiError(doc,
            "level.objects_in{min = {95, -5, -5}, max = {105, 5, 5}}:set('size_range', 20)", "AmbientSound has no field 'size_range'", null);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));

        ScriptMutationAssert.AssertMutation(() =>
        {
            EditorDocument d = NewLevel();
            d.PlaceObject(LevelObjectKind.Light, new Vec3(100, 0, 0));
            d.PlaceObject(LevelObjectKind.Light, new Vec3(101, 0, 0));
            d.MarkSaved();
            return d;
        }, "local q = level.objects_in{min = {95, -5, -5}, max = {105, 5, 5}}:set('size_range', 20)",
            (d, _) => Assert.All(d.Objects.Where(o => o.Kind == LevelObjectKind.Light), o => Assert.Equal(20f, ((Light)o.Model).Range)));
    }

    [Fact]
    public void Describe_On_A_Handle_Lists_Its_Fields_And_Values()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 0, 0));
        new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box }, Vec3.Zero, Mat3.Identity);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "return ged.describe(level.objects_of('Trigger')[1]).text .. '\\n####\\n' .. ged.describe(level.brushes[1]).text");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Contains("Fields (Trigger):", r.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("  sphere_radius : float = 3", r.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("  box_width : float = nil", r.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("  uid : uid (read-only) = ", r.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("Fields (Brush):", r.ReturnValue, StringComparison.Ordinal);
        Assert.Contains("  kind : enum = \"Solid\"", r.ReturnValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Brush_Material_Edit_Undo_Removes_The_Alpine_Section_It_Created()
    {
        ScriptMutationAssert.AssertMutation(() =>
        {
            EditorDocument d = NewLevel();
            new BrushEditor(d).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Detail = true }, Vec3.Zero, Mat3.Identity);
            d.MarkSaved();
            return d;
        }, "level.brushes[1]:set_many{material = 'Wood', no_debris = true, life = 40}", (d, _) =>
        {
            Assert.Equal(2, BrushBreakableProps.GetMaterial(d, d.Rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().Single().Brushes[0].Uid));
            Assert.Contains(d.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        });
    }

    // ---- helpers ------------------------------------------------------------------------

    internal static EditorDocument NewLevel() =>
        new(LevelTemplates.CreateNewRfl(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), "fields.rfl");

    /// <summary>Lists the object's fields through Lua, then exercises each one.</summary>
    private static void ExerciseAllFields(EditorDocument doc, int uid, string owner)
    {
        (ScriptRunResult keys, _) = ScriptTestFixture.Run(doc,
            $"local t = {{}} for _, f in ipairs(level.find_uid({uid}):fields()) do t[#t + 1] = f.name .. '=' .. f.type .. '=' .. tostring(f.read_only) .. '=' .. table.concat(f.options, '|') .. '=' .. tostring(f.min) .. '=' .. tostring(f.max) end return table.concat(t, ';')");
        Assert.True(keys.Success, $"{owner}: {keys.Error?.ToDisplayString()}");
        foreach (string row in keys.ReturnValue!.Split(';'))
        {
            string[] p = row.Split('=');
            ExerciseField(doc, $"level.find_uid({uid})", p[0], p[1], p[2] == "true", p[3].Length == 0 ? Array.Empty<string>() : p[3].Split('|'), owner, p[4], p[5]);
        }
    }

    private static void ExerciseField(EditorDocument doc, string handle, string key, string type, bool readOnly, string[] options, string owner, string min = "nil", string max = "nil")
    {
        string where = $"{owner}.{key} ({type})";
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;

        // (1) set(get()) is a byte no-op (read-only fields: get must work, set must refuse).
        if (readOnly)
        {
            (ScriptRunResult ro, _) = ScriptTestFixture.Run(doc, $"local h = {handle} h:set('{key}', h:get('{key}'))");
            Assert.False(ro.Success, $"{where}: read-only field accepted a write");
            Assert.Contains("read-only", ro.Error!.Message, StringComparison.Ordinal);
            return;
        }

        (ScriptRunResult same, _) = ScriptTestFixture.Run(doc, $"local h = {handle} h:set('{key}', h:get('{key}'))");
        Assert.True(same.Success, $"{where}: set(get()) failed: {same.Error?.ToDisplayString()}");
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), $"{where}: set(get()) changed the bytes");
        Assert.Equal(nodes, doc.Undo.NodeCount);

        // Conditional members that do not apply read nil and refuse a write with a hint.
        (ScriptRunResult cur, _) = ScriptTestFixture.Run(doc, $"local v = {handle}:get('{key}') return v == nil and 'nil' or 'value'");
        Assert.True(cur.Success, $"{where}: {cur.Error?.ToDisplayString()}");
        string? probe = Probe(type, options);
        if (cur.ReturnValue == "nil")
        {
            (ScriptRunResult refused, _) = ScriptTestFixture.Run(doc, $"{handle}:set('{key}', {probe})");
            Assert.False(refused.Success, $"{where}: an absent member accepted a write");
            Assert.False(string.IsNullOrEmpty(refused.Error!.Hint), $"{where}: no hint for an absent member");
            return;
        }

        // (2) a representative new value round-trips, as one undo node with an exact undo.
        string lua =
            $"local h = {handle} local v = h:get('{key}') local p = {probe}\n" +
            $"local nv = {Next(type, options)}\n" +
            $"local lo, hi = {min}, {max} if type(nv) == 'number' and hi and nv > hi then nv = (v or 0) - 1.5 end if type(nv) == 'number' and lo and nv < lo then nv = (lo + (hi or lo + 3)) / 2 end\n" +
            $"h:set('{key}', nv)\n" +
            $"local g = h:get('{key}')\n" +
            "local function same(a, b) if type(a) == 'table' and a.r then return a.r == b[1] and a.g == b[2] and a.b == b[3] end if type(a) == 'userdata' then return math.abs(a.x - b[1]) < 1e-4 and math.abs(a.y - b[2]) < 1e-4 and math.abs(a.z - b[3]) < 1e-4 end if type(a) == 'number' and type(b) == 'number' then return math.abs(a - b) < 1e-3 end if type(a) == 'string' and type(b) == 'string' then return a:lower() == b:lower() end return a == b end\n" +
            "if type(nv) == 'number' and type(g) == 'string' then return 'ok' end\n" +
            "if not same(g, nv) then error('round-trip: set ' .. tostring(nv) .. ' got ' .. tostring(g)) end return 'ok'";
        (ScriptRunResult set, _) = ScriptTestFixture.Run(doc, lua);
        Assert.True(set.Success, $"{where}: {set.Error?.ToDisplayString()}\n{lua}");
        int added = doc.Undo.NodeCount - nodes;
        Assert.True(added <= 1, $"{where}: {added} undo nodes");
        if (added == 1)
        {
            doc.Undo.Undo();
            Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), $"{where}: undo did not restore the exact bytes");
        }

        // (3) a wrong-typed value is rejected and changes nothing.
        (ScriptRunResult wrong, _) = ScriptTestFixture.Run(doc, $"{handle}:set('{key}', {WrongType(type)})");
        Assert.False(wrong.Success, $"{where}: a wrong-typed value was accepted");
        Assert.Equal(ScriptErrorKind.Api, wrong.Error!.Kind);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)), $"{where}: a rejected value changed the bytes");
    }

    /// <summary>A valid literal for the type (used to probe absent members).</summary>
    private static string Probe(string type, string[] options) => type switch
    {
        "string" => options.Length > 0 ? Quote(options[^1]) : "'probe'",
        "bool" => "true",
        "float" => "1.5",
        "enum" => Quote(options.Length > 1 ? options[1] : options[0]),
        "color" => "{1, 2, 3}",
        "vec" => "{1, 2, 3}",
        _ => "1",
    };

    /// <summary>A Lua expression (using v = current value) that differs from v and is valid.</summary>
    private static string Next(string type, string[] options) => type switch
    {
        "string" when options.Length > 0 => $"(function() local o = {{{string.Join(", ", options.Select(Quote))}}} for _, x in ipairs(o) do if x ~= v then return x end end return o[1] end)()",
        "string" => "(v or '') .. 'x'",
        "bool" => "not v",
        "float" => "(v or 0) + 1.5",
        "enum" => $"(function() local o = {{{string.Join(", ", options.Select(Quote))}}} for _, x in ipairs(o) do if x ~= v then return x end end return o[1] end)()",
        "color" => "{(v.r + 1) % 256, (v.g + 2) % 256, (v.b + 3) % 256}",
        "vec" => "{v.x + 1, v.y + 2, v.z + 3}",
        _ => "(v == 1) and 2 or 1",
    };

    private static string WrongType(string type) => type switch
    {
        "string" => "5",
        "bool" => "1",
        "color" or "vec" => "'x'",
        "enum" => "true",
        _ => "'x'",
    };

    private static string Quote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    [Fact]
    public void No_Schema_Field_Is_Shadowed_By_A_Pseudo_Field()
    {
        // The common pseudo-fields (name, hidden, pos, class, uid, kind; delay and color for events) are
        // resolved before the schema; a schema field with one of those keys would be unreachable. The one
        // deliberate case is 'class': scripts choose the class when placing, so the pseudo-field is read-only
        // and the Properties panel's class field stays out of the generic setter.
        var pseudo = new HashSet<string>(ScriptFieldCatalog.PseudoKeys.Where(k => k != "class"), StringComparer.Ordinal);
        var shadowed = new List<string>();
        foreach (LevelObjectKind kind in Enum.GetValues<LevelObjectKind>())
        {
            IReadOnlyList<InspectorField> fields;
            try
            {
                fields = kind == LevelObjectKind.Keyframe ? MoverInspectorSchema.KeyframeFields : ObjectInspectorCatalog.For(kind);
            }
            catch (ArgumentException)
            {
                continue; // kinds without an inspector schema
            }

            shadowed.AddRange(fields.Where(f => pseudo.Contains(f.EffectiveKey)).Select(f => $"{kind}.{f.EffectiveKey}"));
        }

        var eventPseudo = new HashSet<string>(ScriptFieldCatalog.PseudoKeys, StringComparer.Ordinal) { "delay", "color" };
        foreach (EventSchema schema in EventSchemaCatalog.All)
        {
            shadowed.AddRange(schema.Fields.Where(f => eventPseudo.Contains(f.EffectiveKey)).Select(f => $"{schema.ClassName}.{f.EffectiveKey}"));
        }

        Assert.True(shadowed.Count == 0, "schema fields hidden by pseudo-fields: " + string.Join(", ", shadowed));
    }
}