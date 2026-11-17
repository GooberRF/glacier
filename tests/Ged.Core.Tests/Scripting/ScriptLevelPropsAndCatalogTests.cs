using System;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// <c>level.props</c> / <c>level.alpine</c> over the Core level-property schemas and the
/// <c>catalog</c> global over <see cref="GameCatalogs"/>: every property's get/set, the
/// Alpine section created only by a real change (and removed again by undo), the schema labels the
/// Level Properties dialog shows, class lists / describe / textures / nearest with fake tables.
/// </summary>
public sealed class ScriptLevelPropsAndCatalogTests
{
    private static EditorDocument NewLevel() => ScriptObjectFieldTests.NewLevel();

    /// <summary>A level without the alpine_level_properties chunk (a stock level; a new level carries one).</summary>
    private static EditorDocument StockLevel()
    {
        EditorDocument doc = NewLevel();
        doc.Rfl.Sections.RemoveAll(s => s.TypeId == (uint)Ged.Core.IO.Rfl.SectionType.AlpineLevelProperties);
        return doc;
    }

    // ---- level properties -----------------------------------------------------------

    [Fact]
    public void Schema_Labels_Match_The_Level_Properties_Dialog()
    {
        Assert.Equal(new[]
        {
            "Level Name", "Author", "Date", "Multiplayer Level", "Default Geomod Texture", "Hardness (0–100, 0 allowed [Alpine])",
            "Ambient Light", "Fog Color", "Fog Near Plane (PS2 only)", "Fog Far Plane", "RFL Version",
        }, LevelPropertiesSchema.Fields.Select(f => f.Field.Label));
        Assert.Equal(new[]
        {
            "level_name", "author", "date", "multiplayer", "geomod_texture", "hardness", "ambient_color", "fog_color", "fog_near", "fog_far", "rfl_version",
        }, LevelPropertiesSchema.Fields.Select(f => f.Field.EffectiveKey));
        Assert.Equal(new[]
        {
            "Legacy Cyclic Timers", "Legacy Movers", "Player Starts With Headlamp", "Override Static-Mesh Ambient",
            "Static-Mesh Ambient Value", "RF2-Style (brush) Geomod",
            "Payload Push Speed (m/s)", "Payload Rollback (x push speed)", "Payload Acceleration (m/s/s)", "Payload Checkpoint Bonus (s)",
            "Payload Heal + Ammo (HP/s)", "Payload Overtime Grace (s)",
            "Payload Attacker Respawn: Base (s)", "Payload Attacker Respawn: Per Checkpoint (s)", "Payload Attacker Respawn: Final Stretch (s)",
            "Payload Defender Respawn: Base (s)", "Payload Defender Respawn: Per Checkpoint (s)", "Payload Defender Respawn: Final Stretch (s)",
        }, AlpineLevelPropertiesSchema.Fields.Select(f => f.Field.Label));
        Assert.Equal(new[]
        {
            "legacy_cyclic_timers", "legacy_movers", "starts_with_headlamp", "override_static_mesh_ambient", "static_mesh_ambient", "rf2_style_geomod",
            "payload_push_speed", "payload_rollback_speed", "payload_cart_accel", "payload_checkpoint_time_bonus", "payload_heal_per_second",
            "payload_overtime_grace", "payload_attacker_respawn_base", "payload_attacker_respawn_per_checkpoint",
            "payload_attacker_respawn_final_stretch", "payload_defender_respawn_base", "payload_defender_respawn_per_checkpoint",
            "payload_defender_respawn_final_stretch",
        }, AlpineLevelPropertiesSchema.Fields.Select(f => f.Field.EffectiveKey));
    }

    [Theory]
    [InlineData("level.props", "level_name", "'Arena'")]
    [InlineData("level.props", "author", "'Someone'")]
    [InlineData("level.props", "multiplayer", "true")]
    [InlineData("level.props", "geomod_texture", "'rock03.tga'")]
    [InlineData("level.props", "hardness", "55")]
    [InlineData("level.props", "ambient_color", "{60, 61, 62}")]
    [InlineData("level.props", "fog_color", "{r = 1, g = 2, b = 3, a = 4}")]
    [InlineData("level.props", "fog_near", "12.5")]
    [InlineData("level.props", "fog_far", "250")]
    [InlineData("level.alpine", "legacy_cyclic_timers", "false")]
    [InlineData("level.alpine", "legacy_movers", "false")]
    [InlineData("level.alpine", "starts_with_headlamp", "false")]
    [InlineData("level.alpine", "override_static_mesh_ambient", "true")]
    [InlineData("level.alpine", "static_mesh_ambient", "0.5")]
    [InlineData("level.alpine", "rf2_style_geomod", "true")]
    [InlineData("level.alpine", "payload_push_speed", "3")]
    [InlineData("level.alpine", "payload_defender_respawn_final_stretch", "-2.5")]
    public void Every_Level_Property_Sets_And_Undoes_Exactly(string bag, string key, string value)
    {
        // On a stock level (no Alpine chunk), so the Alpine rows also cover creating the chunk inside the edit.
        ScriptMutationAssert.AssertMutation(StockLevel,
            $"local before = {bag}:get('{key}') {bag}:set('{key}', {value}) local after = {bag}:get('{key}')\n" +
            "assert(after ~= before, 'unchanged')",
            (d, _) =>
            {
                if (bag == "level.alpine")
                {
                    // An edit writes the chunk version Alpine RED writes (6), not the old v4 baseline (RFL 306 save policy).
                    Assert.Contains(d.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection { Version: 6 });
                }
            });
    }

    [Fact]
    public void Alpine_Section_Appears_Only_On_A_Real_Change()
    {
        EditorDocument doc = StockLevel();
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        // Reading, and writing the absent-section defaults (the stock behaviour the game assumes without the chunk,
        // editor_patch/level.h:1133-1140), never create it.
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local a = level.alpine:all() level.alpine:set('legacy_movers', true) level.alpine:set('static_mesh_ambient', 2.0) " +
            "level.alpine:set('payload_push_speed', 1.5) " +
            "return tostring(a.legacy_movers) .. ',' .. tostring(a.starts_with_headlamp) .. ',' .. a.static_mesh_ambient .. ',' .. #level.alpine:fields()");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        // 18 fields (6 level flags + 12 payload rules) + 34 Alpine 1.5 fields (Alpine15Fields).
        Assert.Equal("true,true,2,52", r.ReturnValue);
        Assert.Equal(0, r.UndoNodesAdded);
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));

        // A real change creates it — at the existing-level defaults, so nothing else changes behaviour — and undo
        // removes it again (byte-identical).
        (ScriptRunResult set, _) = ScriptTestFixture.Run(doc, "level.alpine:set_many{starts_with_headlamp = false, rf2_style_geomod = true}");
        Assert.True(set.Success, set.Error?.ToDisplayString());
        AlpineLevelPropertiesSection created = doc.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
        Assert.Equal((1, 1, 0, 2f, 1), (created.LegacyCyclicTimers, created.LegacyMovers, created.StartsWithHeadlamp,
            created.StaticMeshAmbientLightModifier, created.Rf2StyleGeomod));
        doc.Undo.Undo();
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is AlpineLevelPropertiesSection);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    [Fact]
    public void Level_Property_Errors()
    {
        EditorDocument doc = NewLevel();
        ScriptApiV2ContractTests.AssertApiError(doc, "level.props:set('date', 'x')", "Field 'date' is read-only.", "automatically");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.props:set('rfl_version', 200)", "Field 'rfl_version' is read-only.", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.props:set('hardness', 101)", "must be between 0 and 100", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.props:set('multiplayer', 1)", "must be a boolean (got number)", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.props:set('level_nme', 'x')", "Level has no field 'level_nme'", "level_name");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.alpine:set_many{legacy_movers = true, headlamp = true}", "Alpine level has no field 'headlamp'", "starts_with_headlamp");

        // A level without level_info / level_properties reads nil and refuses writes.
        EditorDocument bare = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult nil, _) = ScriptTestFixture.Run(bare, "return tostring(level.props:get('level_name')) .. ',' .. level.props:get('rfl_version')");
        Assert.True(nil.Success, nil.Error?.ToDisplayString());
        Assert.Equal("nil,300", nil.ReturnValue);
        ScriptApiV2ContractTests.AssertApiError(bare, "level.props:set('level_name', 'x')", "This level has no level_info section.", null);
        ScriptApiV2ContractTests.AssertApiError(bare, "level.props:set('fog_far', 10)", "This level has no level_properties section.", null);
    }

    [Fact]
    public void Level_Props_Match_The_Dialog_Accessor()
    {
        EditorDocument doc = NewLevel();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "level.props:set_many{level_name = 'Kit', multiplayer = true, ambient_color = {60, 60, 60}, hardness = 0}");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        LevelInfoSection info = doc.Rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();
        LevelPropertiesSection props = doc.Rfl.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().Single();
        Assert.Equal(("Kit", (byte)1), (info.LevelName, info.MultiplayerLevel));
        Assert.Equal((byte)60, props.AmbientColor.R);
        Assert.Equal(0, props.Hardness);
        foreach (LevelPropertyField f in LevelPropertiesSchema.Fields)
        {
            (ScriptRunResult g, _) = ScriptTestFixture.Run(doc, $"local v = level.props:get('{f.Field.EffectiveKey}') return type(v)");
            Assert.True(g.Success, g.Error?.ToDisplayString());
            object? core = LevelPropertyAccess.Get(doc, f);
            string expected = core switch { bool => "boolean", string => "string", RfColor => "table", null => "nil", _ => "number" };
            Assert.Equal(expected, g.ReturnValue);
        }
    }

    // ---- catalog --------------------------------------------------------------------

    private static GameCatalogs Fake() => new()
    {
        Items = ItemCatalog.Load("$Class Name: \"Shotgun\"\n$HUD Msg Name: \"Shotgun\"\n$Gives Weapon: \"shotgun\"\n\n$Class Name: \"assault rifle ammo\"\n$Ammo For: \"Assault Rifle\"\n\n$Class Name: \"Medical Kit\"\n"),
        Clutter = ClutterCatalog.Load("$Class Name: \"crate\"\n$V3D Filename: \"crate.v3m\"\n$Life: 40\n\n$Class Name: \"Barrel\"\n"),
    };

    private static (ScriptRunResult Result, ScriptLog Log) Run(string lua, GameCatalogs? catalogs = null, AssetVfs? vfs = null)
    {
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = NewLevel(), Catalogs = catalogs, Assets = vfs, Confirmation = new AllowAllConfirmation() },
            lua, new ScriptRunOptions { ChunkName = "test" }, log);
        return (r, log);
    }

    [Fact]
    public void Catalog_Classes_Are_Sorted_And_Describe_Digests_The_Tables()
    {
        (ScriptRunResult r, _) = Run(
            "local i = catalog.classes('Item') local c = catalog.classes('clutter')\n" +
            "local d = catalog.describe('Item', 'shotgun') local cd = catalog.describe('Clutter', 'Crate')\n" +
            "return tostring(catalog.available) .. '|' .. table.concat(i, ',') .. '|' .. table.concat(c, ',') .. '|' .. d.class_name .. '=' .. d.description .. '|' .. cd.description .. '|' .. #catalog.classes('Entity') .. '|' .. #catalog.classes('Light')",
            Fake());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("true|assault rifle ammo,Medical Kit,Shotgun|Barrel,crate|Shotgun=HUD name Shotgun; gives weapon shotgun|mesh crate.v3m; life 40|0|0", r.ReturnValue);

        (ScriptRunResult unknown, _) = Run("catalog.describe('Item', 'Shotgn')", Fake());
        Assert.False(unknown.Success);
        Assert.Contains("unknown Item class 'Shotgn'", unknown.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Shotgun", unknown.Error.Hint ?? string.Empty, StringComparison.Ordinal);

        (ScriptRunResult near, _) = Run("return table.concat(catalog.nearest('Item', 'medcal kit', 2), ',')", Fake());
        Assert.True(near.Success, near.Error?.ToDisplayString());
        Assert.StartsWith("Medical Kit", near.ReturnValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_Describes_Events_And_Kinds_With_Fields()
    {
        (ScriptRunResult r, _) = Run(
            "local m = catalog.describe('Event', 'message') local t = catalog.describe('trigger')\n" +
            "local names = {} for _, f in ipairs(m.fields) do names[#names + 1] = f.name end\n" +
            "local tn = {} for _, f in ipairs(t.fields) do tn[#tn + 1] = f.name .. ':' .. f.type end\n" +
            "local tele = catalog.describe('Event', 'Teleport')\n" +
            "local ev = catalog.events()\n" +
            "return m.class_name .. '|' .. m.category .. '|' .. table.concat(names, ',') .. '|' .. tostring(tele.has_rotation) .. ',' .. tostring(m.has_rotation) .. '|' .. table.concat(tn, ',') .. '|' .. #ev .. '|' .. tostring(#catalog.classes('Event') == #ev)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] p = r.ReturnValue!.Split('|');
        Assert.Equal("Message", p[0]);
        Assert.Equal("Level", p[1]);
        Assert.Equal("name,hidden,pos,class,uid,kind,delay,color,message_table_index_0_63,voice_extra_id,flag,persona", p[2]);
        Assert.Equal("true,false", p[3]);
        Assert.StartsWith("name:string,hidden:bool,pos:vec,class:string,uid:uid,kind:string,script:string,shape:enum,sphere_radius:float", p[4], StringComparison.Ordinal);
        Assert.Equal(EventSchemaCatalog.Placeable.Count().ToString(System.Globalization.CultureInfo.InvariantCulture), p[5]);
        Assert.Equal("true", p[6]);

        // Enum options and ranges flow through field_info.
        (ScriptRunResult opts, _) = Run(
            "for _, f in ipairs(catalog.describe('Light').fields) do if f.name == 'type' then return table.concat(f.options, ',') .. '|' .. f.editor end end");
        Assert.True(opts.Success, opts.Error?.ToDisplayString());
        Assert.Equal("(unused),Point,Spot,Tube|Bits", opts.ReturnValue);
        (ScriptRunResult range, _) = Run(
            "for _, f in ipairs(catalog.describe('CoronaObject').fields) do if f.name == 'color_r' then return f.min .. ',' .. f.max end end");
        Assert.True(range.Success, range.Error?.ToDisplayString());
        Assert.Equal("0,255", range.ReturnValue);
    }

    [Fact]
    public void Catalog_Without_Tables_Or_Vfs()
    {
        (ScriptRunResult r, _) = Run(
            "return tostring(catalog.available) .. ',' .. #catalog.classes('Item') .. ',' .. #catalog.textures() .. ',' .. catalog.describe('Item', 'Anything').class_name");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("false,0,0,Anything", r.ReturnValue);

        (ScriptRunResult bad, _) = Run("catalog.classes('ligth')");
        Assert.False(bad.Success);
        Assert.Contains("unknown kind 'ligth'", bad.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Light", bad.Error.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_Textures_Filter_Glob_Limit_And_Tables_From_The_Vfs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string n in new[] { "rock01.tga", "Rock02.tga", "metal01.tga", "floor_rock.tga", "wall.tga" })
            {
                File.WriteAllBytes(Path.Combine(dir, n), new byte[] { 0 });
            }

            File.WriteAllText(Path.Combine(dir, "items.tbl"), "$Class Name: \"Shotgun\"\n");
            using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(dir) });
            (ScriptRunResult r, _) = Run(
                "return table.concat(catalog.textures('rock'), ',') .. '|' .. table.concat(catalog.textures('rock*'), ',') .. '|' .. " +
                "#catalog.textures(nil, 2) .. '|' .. table.concat(catalog.classes('Item'), ',')",
                catalogs: null, vfs: vfs);
            Assert.True(r.Success, r.Error?.ToDisplayString());
            Assert.Equal("floor_rock.tga,rock01.tga,Rock02.tga|rock01.tga,Rock02.tga|2|Shotgun", r.ReturnValue);

            (ScriptRunResult bad, _) = Run("catalog.textures('x', 0)", vfs: vfs);
            Assert.False(bad.Success);
            Assert.Contains("limit must be 1 to 5000", bad.Error!.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Game_Catalogs_Load_Tolerates_Missing_Tables()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-tables-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "clutter.tbl"), "$Class Name: \"crate\"\n");
            using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(dir) });
            GameCatalogs c = GameCatalogs.Load(vfs);
            Assert.True(c.Any);
            Assert.Null(c.Items);
            Assert.Null(c.Entities);
            Assert.Equal(new[] { "crate" }, c.ClassNames(LevelObjectKind.Clutter));
            Assert.False(GameCatalogs.Empty.Any);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
