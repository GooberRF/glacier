using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// Authoring the Alpine Faction 1.5 objects (chunk C5): every object kind is placed, edited through its schema, saved,
/// reloaded and compared field for field; undo of a first placement restores the exact bytes; copy / paste, delete
/// and link rules behave as Alpine RED's; the mover-path and liquid-emission settings round-trip through their own
/// chunks; the rope decoration block, the level properties and the lint checks follow Alpine's rules.
/// </summary>
public sealed class Alpine15AuthoringTests
{
    private static readonly LevelObjectKind[] Kinds =
    {
        LevelObjectKind.WeatherRegion, LevelObjectKind.VehicleFactory, LevelObjectKind.ProjectionCamera,
        LevelObjectKind.RopeEmitter, LevelObjectKind.DirectionalLight, LevelObjectKind.WaypointHelper,
    };

    private static EditorDocument NewLevel()
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 11, 15, 12, 0, 0));
        doc.MarkSaved();
        return doc;
    }

    public static TheoryData<LevelObjectKind> AlpineKinds()
    {
        var d = new TheoryData<LevelObjectKind>();
        foreach (LevelObjectKind k in Kinds)
        {
            d.Add(k);
        }

        return d;
    }

    // ---- Placement and the field-for-field round trip -------------------------------

    [Theory]
    [MemberData(nameof(AlpineKinds))]
    public void Every_Kind_Is_Placeable_From_The_Palette_With_Alpines_Defaults(LevelObjectKind kind)
    {
        Assert.Contains(ObjectFactory.Palette, p => p.Kind == kind && p.Alpine);
        EditorDocument doc = NewLevel();
        LevelObject o = doc.PlaceObject(kind, new Vec3(1, 2, 3))!;
        Assert.False(o.IsReadOnly);
        Assert.True(o.CanRemove);
        Assert.Equal(new Vec3(1, 2, 3), o.Position);
        switch (o.Model)
        {
            case AlpineWeatherRegion w:
                Assert.Equal("Weather Region", w.ScriptName);
                Assert.Equal(10f, w.Width);
                Assert.Equal("af_gbrsnowfl01.tga", w.SnowBitmap);
                Assert.Equal(new RfColor(170, 190, 215, 110), w.RainColor);
                break;
            case AlpineVehicleFactory vf:
                Assert.Equal("Jeep01", vf.VehicleClass);
                Assert.Equal(0xFF, vf.Team);
                Assert.Equal(30f, vf.RespawnDelaySeconds);
                break;
            case AlpineRopeEmitter rope:
                Assert.Equal(-1, rope.TargetUid);
                Assert.Equal(24, rope.Segments);
                Assert.Equal(15f, rope.ZipDownSpeed);
                break;
            case AlpineDirectionalLight l:
                Assert.Equal(new Vec3(0, -1, 0), l.Orientation.Forward);
                Assert.Equal(1f, l.Intensity);
                break;
            case AlpineWaypointHelper h:
                Assert.Equal(2, h.HelperType);
                Assert.Equal(2, h.SizeClass);
                Assert.Equal(4f, h.Radius);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(AlpineKinds))]
    public void Placed_And_Edited_Objects_Save_And_Reload_Field_For_Field(LevelObjectKind kind)
    {
        EditorDocument doc = NewLevel();
        LevelObject o = doc.PlaceObject(kind, new Vec3(4, 5, 6))!;
        int uid = o.Uid;

        // Change every plain schema field to a distinct, in-range value through the shared accessor.
        foreach (InspectorField f in ObjectInspectorCatalog.For(kind).Where(f => !f.Virtual && !f.ReadOnly))
        {
            InspectorAccess.Set(doc, o, f, SampleValue(f, InspectorAccess.Get(doc, o, f)));
        }

        o.Position = new Vec3(7, 8, 9);
        object expected = ModelCloner.Clone(o.Model);

        byte[] saved = doc.SaveToBytes();
        var reloaded = EditorDocument.OpenBytes(saved);
        LevelObject back = reloaded.FindByUid(uid)!;
        Assert.Equal(kind, back.Kind);
        Assert.False(back.IsReadOnly);
        AssertSameFields(expected, back.Model);

        // A second save of the reloaded level is byte-identical (fixpoint), and so is an unmodified re-save.
        Assert.Equal(saved, reloaded.SaveToBytes());
    }

    [Theory]
    [MemberData(nameof(AlpineKinds))]
    public void Undoing_The_First_Placement_Restores_The_Exact_Bytes(LevelObjectKind kind)
    {
        EditorDocument doc = NewLevel();
        byte[] before = doc.SaveToBytes();
        doc.PlaceObject(kind, Vec3.Zero);
        Assert.NotEqual(before, doc.SaveToBytes());
        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes());
    }

    [Fact]
    public void Move_Rotate_Delete_Copy_Paste_And_Undo_Work_Like_Every_Object()
    {
        EditorDocument doc = NewLevel();
        LevelObject factory = doc.PlaceObject(LevelObjectKind.VehicleFactory, Vec3.Zero)!;
        int uid = factory.Uid;

        doc.EditValue(factory.Section, "Move", factory.Position, new Vec3(3, 0, 0), v => factory.Position = v);
        Mat3 turned = Mat3Math.RotationY(MathF.PI / 2f);
        Mat3 oldRot = ObjectRotation.Get(factory.Model)!.Value;
        doc.EditValue(factory.Section, "Rotate", oldRot, turned, r => ObjectRotation.Set(factory.Model, r));
        Assert.Equal(turned, ((AlpineVehicleFactory)doc.FindByUid(uid)!.Model).Orientation);

        doc.SelectMany(new[] { doc.FindByUid(uid)! });
        doc.CopySelection();
        IReadOnlyList<int> pasted = doc.Paste();
        int copy = Assert.Single(pasted);
        Assert.NotEqual(uid, copy);
        Assert.Equal(new Vec3(3, 0, 0), doc.FindByUid(copy)!.Position);

        doc.SelectMany(new[] { doc.FindByUid(uid)! });
        doc.DeleteSelection();
        Assert.Null(doc.FindByUid(uid));
        doc.Undo.Undo();
        Assert.NotNull(doc.FindByUid(uid));

        var reloaded = EditorDocument.OpenBytes(doc.SaveToBytes());
        Assert.Equal(2, reloaded.Objects.Count(o => o.Kind == LevelObjectKind.VehicleFactory));
    }

    [Fact]
    public void Pasted_Waypoint_Helpers_Keep_Only_The_Links_Between_Copied_Helpers()
    {
        EditorDocument doc = NewLevel();
        LevelObject a = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(0, 0, 0))!;
        LevelObject b = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(5, 0, 0))!;
        LevelObject c = doc.PlaceObject(LevelObjectKind.WaypointHelper, new Vec3(10, 0, 0))!;
        var links = new LinkService(doc);
        Assert.True(links.LinkOneToMany(a, new[] { b, c }).Ok);

        // A helper links only to helpers.
        LevelObject light = doc.PlaceObject(LevelObjectKind.Light, Vec3.Zero)!;
        Assert.False(LinkRules.Validate(doc.FindByUid(a.Uid)!, light).Ok);

        doc.SelectMany(new[] { doc.FindByUid(a.Uid)!, doc.FindByUid(b.Uid)! });
        doc.CopySelection();
        IReadOnlyList<int> pasted = doc.Paste();
        var copyA = (AlpineWaypointHelper)doc.FindByUid(pasted[0])!.Model;
        var copyB = (AlpineWaypointHelper)doc.FindByUid(pasted[1])!.Model;
        Assert.Equal(new[] { pasted[1] }, copyA.Links); // a→b re-aimed at b's copy; a→c (c not copied) dropped
        Assert.Empty(copyB.Links);
    }

    [Fact]
    public void A_Chunk_Kept_Raw_Refuses_New_Objects_And_Keeps_Its_Objects_Read_Only()
    {
        EditorDocument doc = NewLevel();
        doc.PlaceObject(LevelObjectKind.ProjectionCamera, Vec3.Zero);
        byte[] saved = doc.SaveToBytes();

        // Append a stray byte to the chunk body (fields a newer Alpine might add): the chunk is kept raw.
        RflFile rfl = RflFile.Load(saved);
        RflSection host = rfl.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineProjectionCameras);
        host.RawBytes = host.RawBytes.Concat(new byte[] { 0x7F }).ToArray();
        host.Content = null;
        var raw = EditorDocument.OpenBytes(rfl.Save());

        LevelObject cam = raw.Objects.Single(o => o.Kind == LevelObjectKind.ProjectionCamera);
        Assert.True(cam.IsReadOnly);
        Assert.False(cam.CanRemove);
        Assert.Throws<InvalidOperationException>(() => raw.PlaceObject(LevelObjectKind.ProjectionCamera, Vec3.Zero));
    }

    // ---- Mover paths --------------------------------------------------------------------

    private static (EditorDocument Doc, Group Mover, List<Keyframe> Keys) MoverLevel(int movementType = 1)
    {
        EditorDocument doc = NewLevel();
        int box = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        var movers = new MoverService(doc);
        Group g = movers.CreateMover(new[] { box }, Array.Empty<int>(), "lift");
        movers.AddKeyframe(g, new Vec3(10, 0, 0), Mat3.Identity);
        movers.AddKeyframe(g, new Vec3(10, 0, 10), Mat3.Identity);
        g.MovingData!.MovementType = movementType;
        doc.MarkSaved();
        return (doc, g, g.MovingData.Keyframes);
    }

    private static InspectorField KeyField(string key) => MoverInspectorSchema.KeyframePathFields.Single(f => f.EffectiveKey == key);

    [Fact]
    public void Keyframe_Curve_Settings_Round_Trip_And_Default_Settings_Store_Nothing()
    {
        (EditorDocument doc, Group g, List<Keyframe> keys) = MoverLevel();
        byte[] before = doc.SaveToBytes();
        LevelObject k1 = doc.FindByUid(keys[1].Uid)!;

        InspectorAccess.Set(doc, k1, KeyField("path_segment"), 1);
        InspectorAccess.Set(doc, k1, KeyField("path_ease"), 3);
        AlpineMoverPathsSection paths = AlpineAttachedData.MoverPaths(doc.Rfl)!;
        AlpineMoverPathKeyframe rec = Assert.Single(paths.Keyframes);
        Assert.Equal((keys[1].Uid, (byte)1, (byte)3), (rec.KeyframeUid, rec.SegmentMode, rec.Ease));

        var reloaded = EditorDocument.OpenBytes(doc.SaveToBytes());
        Assert.Equal(1, InspectorAccess.Get(reloaded, reloaded.FindByUid(keys[1].Uid)!, KeyField("path_segment")));

        // Back to straight / no easing is Alpine's default: the record goes, and undoing both edits restores the bytes.
        InspectorAccess.Set(doc, k1, KeyField("path_segment"), 0);
        InspectorAccess.Set(doc, k1, KeyField("path_ease"), 0);
        Assert.Empty(AlpineAttachedData.MoverPaths(doc.Rfl)!.Keyframes);
        for (int i = 0; i < 4; i++)
        {
            doc.Undo.Undo();
        }

        Assert.Equal(before, doc.SaveToBytes());
        _ = g;
    }

    [Fact]
    public void Leaving_Automatic_Tangents_Keeps_The_Shape_And_Aligned_Handles_Stay_Opposite()
    {
        (EditorDocument doc, Group g, List<Keyframe> keys) = MoverLevel();
        LevelObject k0 = doc.FindByUid(keys[0].Uid)!;
        LevelObject k1 = doc.FindByUid(keys[1].Uid)!;
        InspectorAccess.Set(doc, k0, KeyField("path_segment"), 1);
        InspectorAccess.Set(doc, k1, KeyField("path_segment"), 1);

        // Automatic handles: a third of each neighbouring span, along the neighbour chord (mover_path.h:281-313).
        var autoOut = (Vec3)InspectorAccess.Get(doc, k1, KeyField("out_handle"))!;
        var autoIn = (Vec3)InspectorAccess.Get(doc, k1, KeyField("in_handle"))!;
        Assert.True(autoOut.Length() > 0.1f);
        Assert.True(MathF.Abs(autoOut.Normalized().Dot(autoIn.Normalized()) + 1f) < 1e-4f, "auto handles are opposite");

        InspectorAccess.Set(doc, k1, KeyField("path_tangent"), 2); // Free: starts where Auto had them
        AlpineMoverPathKeyframe free = AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid);
        Assert.Equal(autoOut, free.OutHandle);
        Assert.Equal(autoIn, free.InHandle);

        InspectorAccess.Set(doc, k1, KeyField("path_tangent"), 1); // Aligned
        InspectorAccess.Set(doc, k1, KeyField("out_handle"), new Vec3(0, 3, 0));
        AlpineMoverPathKeyframe aligned = AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid);
        Assert.Equal(new Vec3(0, 3, 0), aligned.OutHandle);
        Assert.Equal(0f, aligned.InHandle.X, 4);
        Assert.Equal(-autoIn.Length(), aligned.InHandle.Y, 4); // turned opposite, its own length kept

        // The path the viewport draws curves through the handles.
        IReadOnlyList<MoverPathSegment> segs = MoverPathGeometry.Segments(doc.Rfl, g);
        Assert.Equal(2, segs.Count);
        Assert.All(segs, s => Assert.True(s.Curved));
        Assert.Equal(keys[1].Position.Add(new Vec3(0, 3, 0)), segs[1].P1);
    }

    [Fact]
    public void A_Loop_Of_Three_Keyframes_Has_A_Closing_Segment_And_Legacy_Movers_Have_No_Path()
    {
        (EditorDocument doc, Group g, _) = MoverLevel(movementType: 5);
        IReadOnlyList<MoverPathSegment> segs = MoverPathGeometry.Segments(doc.Rfl, g);
        Assert.Equal(3, segs.Count);
        Assert.True(segs[2].Closing);

        LevelPropertyField legacy = AlpineLevelPropertiesSchema.Fields.Single(f => f.Field.EffectiveKey == "legacy_movers");
        LevelPropertyAccess.Set(doc, legacy, true);
        Assert.Empty(MoverPathGeometry.Segments(doc.Rfl, g));
    }

    [Fact]
    public void Mover_Settings_Are_Keyed_By_The_First_Keyframe_And_Deleting_It_Drops_Them()
    {
        (EditorDocument doc, Group g, List<Keyframe> keys) = MoverLevel();
        var movers = new MoverService(doc);
        InspectorField orient = MoverInspectorSchema.MoverPathFields.Single(f => f.EffectiveKey == "path_orientation");
        InspectorField turn = MoverInspectorSchema.MoverPathFields.Single(f => f.EffectiveKey == "riders_turn");
        InspectorAccess.Set(movers, g, orient, 1);
        InspectorAccess.Set(movers, g, turn, true);
        AlpineMoverPathMover m = Assert.Single(AlpineAttachedData.MoverPaths(doc.Rfl)!.Movers);
        Assert.Equal((keys[0].Uid, (byte)1, (byte)1, (ushort)0), (m.FirstKeyframeUid, m.OrientMode, m.RiderFlags, m.Padding));

        InspectorAccess.Set(doc, doc.FindByUid(keys[1].Uid)!, KeyField("path_segment"), 1);
        int deleted = keys[1].Uid;
        doc.SelectMany(new[] { doc.FindByUid(deleted)! });
        doc.DeleteSelection();
        Assert.DoesNotContain(AlpineAttachedData.MoverPaths(doc.Rfl)!.Keyframes, k => k.KeyframeUid == deleted);
        doc.Undo.Undo();
        Assert.Contains(AlpineAttachedData.MoverPaths(doc.Rfl)!.Keyframes, k => k.KeyframeUid == deleted);
    }

    // ---- Liquid emission ----------------------------------------------------------------

    [Fact]
    public void Liquid_Emission_Round_Trips_And_Travels_With_A_Pasted_Room_Effect()
    {
        EditorDocument doc = NewLevel();
        LevelObject fx = doc.PlaceObject(LevelObjectKind.RoomEffect, Vec3.Zero)!;
        InspectorField type = ObjectInspectorCatalog.For(LevelObjectKind.RoomEffect).Single(f => f.Path == "EffectType");
        InspectorAccess.Set(doc, fx, type, RoomEffectsSection.EffectLiquidRoom);
        InspectorField Field(string key) => ObjectInspectorCatalog.For(LevelObjectKind.RoomEffect).Single(f => f.EffectiveKey == key);
        InspectorAccess.Set(doc, fx, Field("liquid_emission"), 2);
        InspectorAccess.Set(doc, fx, Field("emission_color"), new RfColor(10, 200, 30, 255));
        InspectorAccess.Set(doc, fx, Field("emission_distance"), 12.5f);

        AlpineLiquidEmission e = AlpineAttachedData.Emission(doc.Rfl, fx.Uid);
        Assert.Equal((2, 10, 200, 30, 12.5f), ((int)e.Mode, (int)e.ColorR, (int)e.ColorG, (int)e.ColorB, e.Distance));

        doc.SelectMany(new[] { doc.FindByUid(fx.Uid)! });
        doc.CopySelection();
        int copy = Assert.Single(doc.Paste());
        Assert.Equal(2, AlpineAttachedData.Emission(doc.Rfl, copy).Mode);

        var reloaded = EditorDocument.OpenBytes(doc.SaveToBytes());
        Assert.Equal(2, AlpineAttachedData.LiquidEmission(reloaded.Rfl)!.Records.Count);
        Assert.Equal(12.5f, AlpineAttachedData.Emission(reloaded.Rfl, copy).Distance);

        // Out-of-range values clamp to Alpine's limits (liquid_emission.h:35-36).
        InspectorAccess.Set(doc, fx, Field("emission_intensity"), 500f);
        Assert.Equal(100f, AlpineAttachedData.Emission(doc.Rfl, fx.Uid).Intensity);
    }

    // ---- Rope decorations ---------------------------------------------------------------

    [Fact]
    public void Rope_Decoration_Meshes_Create_And_Remove_The_Decoration_Block()
    {
        EditorDocument doc = NewLevel();
        LevelObject rope = doc.PlaceObject(LevelObjectKind.RopeEmitter, Vec3.Zero)!;
        InspectorField Field(string key) => ObjectInspectorCatalog.For(LevelObjectKind.RopeEmitter).Single(f => f.EffectiveKey == key);

        Assert.Throws<InvalidOperationException>(() => InspectorAccess.Set(doc, rope, Field("decoration_count"), 5));
        InspectorAccess.Set(doc, rope, Field("decoration_meshes"), "lamp.v3m; flag.v3m");
        InspectorAccess.Set(doc, rope, Field("decoration_count"), 5);
        var model = (AlpineRopeEmitter)rope.Model;
        Assert.Equal(new[] { "lamp.v3m", "flag.v3m" }, model.Decorations!.Meshes.Select(m => m.Mesh));
        Assert.Equal(5, model.Decorations.Count);

        var reloaded = (AlpineRopeEmitter)EditorDocument.OpenBytes(doc.SaveToBytes()).FindByUid(rope.Uid)!.Model;
        Assert.Equal(2, reloaded.Decorations!.Meshes.Count);
        Assert.Equal("lamp.v3m;flag.v3m", InspectorAccess.Get(doc, rope, Field("decoration_meshes")));

        InspectorAccess.Set(doc, rope, Field("decoration_meshes"), string.Empty);
        Assert.Null(model.Decorations);
        Assert.Throws<InvalidOperationException>(() => InspectorAccess.Set(doc, rope, Field("decoration_meshes"), "a;b;c;d;e;f;g"));
    }

    [Fact]
    public void Rope_Color_Packs_Red_In_The_High_Byte()
    {
        var rope = new AlpineRopeEmitter { Color = new RfColor(0x11, 0x22, 0x33, 0x44) };
        Assert.Equal(0x11223344u, rope.PackedColor);
        Assert.Equal(new RfColor(0x11, 0x22, 0x33, 0x44), rope.Color);
    }

    [Fact]
    public void A_Rope_Hangs_As_A_Catenary_To_Its_Target_And_Straight_Down_Without_One()
    {
        IReadOnlyList<Vec3> dangle = RopeCurve.Polyline(new Vec3(0, 10, 0), null, 0.5f, 3f, 6);
        Assert.Equal(7, dangle.Count);
        Assert.Equal(new Vec3(0, 7, 0), dangle[^1]);

        IReadOnlyList<Vec3> span = RopeCurve.Polyline(new Vec3(0, 10, 0), new Vec3(10, 10, 0), 2f, 3f, 24);
        Assert.Equal(new Vec3(10, 10, 0), span[^1]);
        Assert.True(span.Min(p => p.Y) < 9f, "a slack rope sags below its anchors");
        float length = 0f;
        for (int i = 1; i < span.Count; i++)
        {
            length += span[i].Sub(span[i - 1]).Length();
        }

        Assert.Equal(12f, length, 1); // span 10 + slack 2
    }

    // ---- Level properties -----------------------------------------------------------------

    [Fact]
    public void Alpine_1_5_Level_Properties_Are_Editable_And_Round_Trip()
    {
        EditorDocument doc = NewLevel();
        LevelPropertyField P(string key) => AlpineLevelPropertiesSchema.Alpine15Fields.Single(f => f.Field.EffectiveKey == key);
        LevelPropertyAccess.Set(doc, P("sun_enabled"), true);
        LevelPropertyAccess.Set(doc, P("sun_yaw"), 135f);
        LevelPropertyAccess.Set(doc, P("minimap_world_max"), new Vec3(100, 0, 50));
        LevelPropertyAccess.Set(doc, P("lightmap_compression"), 2);
        LevelPropertyAccess.Set(doc, P("require_d3d11_setting"), true);
        Assert.True(P("props_version").Field.ReadOnly);

        var reloaded = EditorDocument.OpenBytes(doc.SaveToBytes());
        AlpineLevelPropertiesSection props = reloaded.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
        Assert.Equal(1, props.EnableSun);
        Assert.Equal(135f, props.SunYaw);
        Assert.Equal(new Vec3(100, 0, 50), props.MinimapWorldMax);
        Assert.Equal(2, props.LightmapCompression);
        Assert.Equal(AlpineLevelPropertiesSection.RequireD3D11Setting, props.RequireD3D11 & AlpineLevelPropertiesSection.RequireD3D11Setting);
        Assert.Equal(true, LevelPropertyAccess.Get(reloaded, P("require_d3d11_setting")));
    }

    [Fact]
    public void No_Shadow_Cast_Brushes_Are_A_Props_List_Edit_With_Undo()
    {
        EditorDocument doc = NewLevel();
        int box = new BrushEditor(doc).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        BrushShadowProps.SetNoShadowCast(doc, box, true);
        Assert.True(BrushShadowProps.GetNoShadowCast(doc, box));
        Assert.Contains(LevelLinter.Lint(doc.Rfl).Findings, f => f.Category == LintCategory.AlpineObject && f.Uid == box); // not detail: ignored
        doc.Undo.Undo();
        Assert.False(BrushShadowProps.GetNoShadowCast(doc, box));
    }

    // ---- Lint ------------------------------------------------------------------------------

    [Fact]
    public void Lint_Reports_What_Alpine_Refuses_Or_Ignores()
    {
        EditorDocument doc = NewLevel();
        var vf = (AlpineVehicleFactory)doc.PlaceObject(LevelObjectKind.VehicleFactory, Vec3.Zero)!.Model;
        vf.VehicleClass = "Shuttle";
        var vf2 = (AlpineVehicleFactory)doc.PlaceObject(LevelObjectKind.VehicleFactory, Vec3.Zero)!.Model;
        vf2.Orientation = new Mat3(new Vec3(0, 0, 2), new Vec3(1, 0, 0), new Vec3(0, 1, 0)); // not a rotation
        var rope = (AlpineRopeEmitter)doc.PlaceObject(LevelObjectKind.RopeEmitter, Vec3.Zero)!.Model;
        rope.TargetUid = 987654;
        var ev = (RflEvent)doc.PlaceEvent(EventSchemaCatalog.Find("Display_Projection")!, Vec3.Zero)!.Model;

        IReadOnlyList<LintFinding> findings = LevelLinter.Lint(doc.Rfl).Findings.Where(f => f.Category == LintCategory.AlpineObject).ToList();
        Assert.Contains(findings, f => f.Uid == vf.Uid && f.Message.Contains("do not support", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Uid == vf2.Uid && f.Message.Contains("not a proper rotation", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Uid == rope.Uid && f.Message.Contains("free dangle", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Uid == ev.Uid && f.Message.Contains("Projection Camera", StringComparison.Ordinal));
    }

    [Fact]
    public void Lint_Reports_Mover_Path_Settings_Alpine_Ignores()
    {
        (EditorDocument doc, Group g, List<Keyframe> keys) = MoverLevel();
        InspectorAccess.Set(doc, doc.FindByUid(keys[2].Uid)!, KeyField("path_segment"), 1); // the last keyframe of a one-way mover
        Assert.Contains(LevelLinter.Lint(doc.Rfl).Findings, f => f.Category == LintCategory.AlpineObject && f.Message.Contains("starts no segment", StringComparison.Ordinal));

        LevelPropertyAccess.Set(doc, AlpineLevelPropertiesSchema.Fields.Single(f => f.Field.EffectiveKey == "legacy_movers"), true);
        Assert.Contains(LevelLinter.Lint(doc.Rfl).Findings, f => f.Category == LintCategory.AlpineObject && f.Message.Contains("Legacy Movers is on", StringComparison.Ordinal));
        _ = g;
    }

    // ---- Lua -------------------------------------------------------------------------------

    [Fact]
    public void Lua_Places_Queries_And_Sets_Every_Alpine_Object_Kind()
    {
        EditorDocument doc = NewLevel();
        (Ged.Core.Scripting.ScriptRunResult r, _) = ScriptTestFixture.Run(doc, string.Join('\n',
            "local w = level.place{kind = 'weather', pos = {0, 5, 0}, props = {weather = 'Snow', shape = 'Sphere', radius = 12}}",
            "local f = level.place{kind = 'vehicle_factory', pos = {4, 0, 0}, props = {vehicle_class = 'APC', team = 'Blue', respawn_delay = 45}}",
            "local c = level.place{kind = 'projector', pos = {0, 3, 0}}",
            "local r = level.place{kind = 'rope', pos = {0, 8, 0}, props = {target = c, slack = 1.5, zipline = true}}",
            "local l = level.place{kind = 'directional_light', pos = {0, 20, 0}, props = {shape = 'Box', intensity = 2}}",
            "local h1 = level.place{kind = 'waypoint_helper', pos = {0, 0, 0}, props = {helper_type = 'Air', landing = true}}",
            "local h2 = level.place{kind = 'waypoint_helper', pos = {9, 0, 0}}",
            "h1:link_to(h2)",
            "assert(w:get('weather') == 'Snow' and f:get('team') == 'Blue' and r:get('zipline') == true)",
            "assert(#level.objects_of('WaypointHelper') == 2 and #level.objects_of('rope') == 1)",
            "return f:get('vehicle_class') .. ',' .. tostring(l:get('intensity')) .. ',' .. tostring(#h1:links())"));
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("APC,2,1", r.ReturnValue);

        var factory = doc.Objects.Single(o => o.Kind == LevelObjectKind.VehicleFactory).Model as AlpineVehicleFactory;
        Assert.Equal((byte)1, factory!.Team);
        Assert.Equal(45f, factory.RespawnDelaySeconds);

        // Ranges are enforced like every schema field.
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('VehicleFactory')[1]:set('respawn_delay', 9000)", "must be between 0 and 3600", null);
    }

    [Fact]
    public void Lua_Sets_Mover_Paths_Keyframe_Curves_And_Liquid_Emission()
    {
        (EditorDocument doc, _, List<Keyframe> keys) = MoverLevel();
        (Ged.Core.Scripting.ScriptRunResult r, _) = ScriptTestFixture.Run(doc, string.Join('\n',
            "local m = level.movers[1]",
            "m:set_many{path_orientation = 'Follow Path', riders_turn = true}",
            "local k = m.keyframes[2]",
            "k:set_many{path_segment = 'Curved', path_ease = 'Ease In And Out'}",
            "return m:get('path_orientation') .. ',' .. k:get('path_segment')"));
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("Follow Path,Curved", r.ReturnValue);
        Assert.Equal(1, AlpineAttachedData.KeyframeData(doc.Rfl, keys[1].Uid).SegmentMode);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>A value different from <paramref name="current"/> that the field accepts.</summary>
    private static object SampleValue(InspectorField f, object? current)
    {
        switch (f.Editor)
        {
            case InspectorEditor.Bool:
                return current is not true;
            case InspectorEditor.Enum:
                int n = f.Options?.Count ?? 1;
                return ((current is int i ? i : 0) + 1) % Math.Max(n, 1);
            case InspectorEditor.Float:
            {
                float cur = current is float x ? x : 0f;
                float lo = (float)(f.Min ?? -1000);
                float hi = (float)(f.Max ?? 1000);
                float next = cur + 0.25f;
                return next > hi || next < lo ? (lo + hi) / 2f == cur ? lo : (lo + hi) / 2f : next;
            }

            case InspectorEditor.Int:
            case InspectorEditor.Uid:
            {
                int cur = current is int x ? x : 0;
                int lo = (int)(f.Min ?? int.MinValue);
                int hi = (int)(f.Max ?? int.MaxValue);
                int next = cur + 1;
                return next > hi ? lo : next;
            }

            case InspectorEditor.Color:
                return new RfColor(11, 22, 33, 44);
            case InspectorEditor.Vector:
                return new Vec3(1, 2, 3);
            default:
                return (current as string ?? string.Empty) + "x";
        }
    }

    private static void AssertSameFields(object expected, object actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (System.Reflection.PropertyInfo p in expected.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? a = p.GetValue(expected);
            object? b = p.GetValue(actual);
            if (a is System.Collections.IList la && b is System.Collections.IList lb)
            {
                Assert.Equal(la.Cast<object>(), lb.Cast<object>());
            }
            else if (a is not null && !a.GetType().IsValueType && a is not string)
            {
                AssertSameFields(a, b!);
            }
            else
            {
                Assert.True(Equals(a, b), $"{expected.GetType().Name}.{p.Name}: expected {a}, got {b}");
            }
        }
    }
}
