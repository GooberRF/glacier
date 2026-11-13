using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The Alpine 1.5 data reaches scripts and the generic object paths read-only (chunk C3): the new objects list as
/// level objects with read-only inspector schemas, the 1.5 level properties read through <c>level.alpine</c>,
/// edits are refused, and the linter / link rules know the 1.5 events (chunk C4).
/// </summary>
public sealed class Alpine306ExposureTests
{
    private static EditorDocument SyntheticDoc() => new(RflFile.Load(Alpine306Synthetic.Level()));

    [Theory]
    [InlineData(LevelObjectKind.WeatherRegion, 2)]
    [InlineData(LevelObjectKind.VehicleFactory, 2)]
    [InlineData(LevelObjectKind.ProjectionCamera, 2)]
    [InlineData(LevelObjectKind.RopeEmitter, 2)]
    [InlineData(LevelObjectKind.DirectionalLight, 2)]
    [InlineData(LevelObjectKind.WaypointHelper, 3)]
    [InlineData(LevelObjectKind.Terrain, 1)]
    public void New_Objects_List_Read_Only(LevelObjectKind kind, int count)
    {
        EditorDocument doc = SyntheticDoc();
        List<LevelObject> objects = doc.Objects.Where(o => o.Kind == kind).ToList();
        Assert.Equal(count, objects.Count);
        Assert.All(objects, o =>
        {
            Assert.True(o.IsReadOnly);
            Assert.False(o.CanRemove);
        });
        Assert.NotEmpty(ObjectInspectorCatalog.For(kind));
        Assert.All(ObjectInspectorCatalog.For(kind), f => Assert.True(f.ReadOnly));

        // A setter never changes the stored record: the chunk stays byte-identical.
        LevelObject first = objects[0];
        Vec3 before = first.Position;
        first.Position = new Vec3(1000, 1000, 1000);
        Assert.Equal(before, first.Position);
    }

    [Fact]
    public void Scripts_Read_The_New_Objects_And_Properties_And_Cannot_Edit_Them()
    {
        EditorDocument doc = SyntheticDoc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local w = level.objects_of('WeatherRegion') " +
            "local d = level.objects_of('DirectionalLight')[2] " +
            "local v = level.objects_of('VehicleFactory')[1] " +
            "return #w .. ',' .. w[2]:get('weather') .. ',' .. d:get('shape') .. ',' .. v:get('vehicle_class') .. ',' .. " +
            "tostring(level.alpine:get('sun_enabled')) .. ',' .. level.alpine:get('minimap_bitmap') .. ',' .. level.alpine:get('props_version')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("2,Snow,Cylinder,jeep,true,synthetic_minimap.tga,6", r.ReturnValue);

        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('RopeEmitter')[1]:set_pos(0, 0, 0)", "is read-only", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('Terrain')[1]:set_name('x')", "is read-only", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.objects_of('DirectionalLight')[1]:set('intensity', 2)", "read-only", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.alpine:set('legacy_lighting', true)", "read-only", null);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    [Fact]
    public void Alpine_Properties_Read_Alpines_Defaults_When_The_Chunk_Is_Absent()
    {
        EditorDocument doc = LevelTemplates.CreateNew(Alpine306Synthetic.FixedDate);
        doc.Rfl.Sections.RemoveAll(s => s.TypeId == (uint)SectionType.AlpineLevelProperties);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "return tostring(level.alpine:get('legacy_lighting')) .. ',' .. level.alpine:get('sun_pitch') .. ',' .. level.alpine:get('lightmap_density') " +
            ".. ',' .. tostring(level.alpine:get('legacy_movers')) .. ',' .. level.alpine:get('payload_push_speed')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("false,90,0,true,1.5", r.ReturnValue); // Alpine lighting (Q6); legacy movers on without the chunk
    }

    // ---- events, links and the linter ---------------------------------------------------------

    [Fact]
    public void The_Alpine_15_Events_Are_In_The_Catalog_With_Their_Link_Targets()
    {
        Assert.Equal(EventLinkTarget.WeatherRegion, Assert.Single(EventSchemaCatalog.Find("Weather_Region_State")!.LinkTargets));
        Assert.Equal(EventLinkTarget.ProjectionCamera, Assert.Single(EventSchemaCatalog.Find("Display_Projection")!.LinkTargets));
        Assert.Contains(EventLinkTarget.DetailBrush, EventSchemaCatalog.Find("When_Destroyed")!.LinkTargets);
        Assert.Equal(EventLinkTarget.Mover, Assert.Single(EventSchemaCatalog.Find("Payload_Handler")!.LinkTargets));
        Assert.Equal(306, EventSchemaCatalog.Find("Payload_Checkpoint")!.MinVersion);
        Assert.Equal(16, EventSchemaCatalog.Find("Gametype_Gate")!.Fields.Single().Options!.Count);
        Assert.True(Editing.LinkRules.TargetKindAllowed(EventSchemaCatalog.Find("Rope_State")!, LevelObjectKind.RopeEmitter));
        Assert.False(Editing.LinkRules.TargetKindAllowed(EventSchemaCatalog.Find("Rope_State")!, LevelObjectKind.Light));
    }

    [Fact]
    public void When_Destroyed_May_Link_To_Detail_Brushes_And_Other_Events_Are_Warned()
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(Alpine306Synthetic.FixedDate);
        var brushes = new BrushesSection();
        brushes.Brushes.Add(new Brush { Uid = 300, Flags = (uint)BrushFlags.Detail });
        brushes.Brushes.Add(new Brush { Uid = 301 });
        rfl.InsertSection(new RflSection((uint)SectionType.Brushes, Array.Empty<byte>()) { Content = brushes, Dirty = true });
        var events = new EventsSection();
        events.Events.Add(new RflEvent { Uid = 400, ClassName = "When_Destroyed", Links = { 300 } });
        events.Events.Add(new RflEvent { Uid = 401, ClassName = "When_Destroyed", Links = { 301 } });
        events.Events.Add(new RflEvent { Uid = 402, ClassName = "Delay", Links = { 300 } });
        rfl.InsertSection(new RflSection((uint)SectionType.Events, Array.Empty<byte>()) { Content = events, Dirty = true });

        List<LintFinding> links = LevelLinter.Lint(rfl, new LintOptions { CheckGeometryLeaks = false })
            .Findings.Where(f => f.Category == LintCategory.BrokenLink).ToList();
        Assert.DoesNotContain(links, f => f.Uid == 400);
        Assert.Contains(links, f => f.Uid == 401 && f.Message.Contains("not a detail brush", StringComparison.Ordinal));
        Assert.Contains(links, f => f.Uid == 402 && f.Message.Contains("does not act on", StringComparison.Ordinal));
        Assert.DoesNotContain(links, f => f.Message.Contains("missing UID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData((byte)1, true)]
    [InlineData((byte)0, false)]
    public void Payload_Handler_Needs_Legacy_Movers_Off(byte? legacyMovers, bool warned)
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(Alpine306Synthetic.FixedDate);
        rfl.Sections.RemoveAll(s => s.TypeId == (uint)SectionType.AlpineLevelProperties); // null = a level without the chunk
        if (legacyMovers is byte lm)
        {
            rfl.InsertSection(new RflSection((uint)SectionType.AlpineLevelProperties, Array.Empty<byte>())
            {
                Content = new AlpineLevelPropertiesSection { Version = 6, LegacyMovers = lm },
                Dirty = true,
            });
        }

        var events = new EventsSection();
        events.Events.Add(new RflEvent { Uid = 500, ClassName = "Payload_Handler" });
        rfl.InsertSection(new RflSection((uint)SectionType.Events, Array.Empty<byte>()) { Content = events, Dirty = true });

        bool found = LevelLinter.Lint(rfl, new LintOptions { CheckGeometryLeaks = false })
            .Findings.Any(f => f.Category == LintCategory.AlpinePrerequisite && f.Uid == 500
                && f.Message.Contains("needs Legacy Movers off", StringComparison.Ordinal));
        Assert.Equal(warned, found);
    }
}
