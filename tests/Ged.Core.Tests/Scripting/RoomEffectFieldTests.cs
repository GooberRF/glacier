using System;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Room effects through the shared Core field model: the type decides which block the room_effects
/// section stores (the ambient colour for Ambient Light, the liquid block for Liquid Room), a type
/// switch provisions that block in the same undo step, and fields of a block that does not apply read
/// nil and refuse writes with a hint — for scripts and the Properties panel alike.
/// </summary>
public sealed class RoomEffectFieldTests
{
    private static EditorDocument NewLevel() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    [Fact]
    public void Scripts_Place_And_Switch_Every_Room_Effect_Type()
    {
        EditorDocument doc = NewLevel();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local fx = level.place{kind = 'room_effect', pos = {0, 2, 0}, name = 'sky', props = {type = 'sky_room'}}\n" +
            "local out = {fx:get('type'), tostring(fx:get('liquid_depth')), tostring(fx:get('ambient_color'))}\n" +
            "fx:set('type', 'liquid_room')\n" +
            "out[#out + 1] = fx:get('liquid_type') .. '/' .. fx:get('waveform')\n" +
            "fx:set_many{liquid_depth = 2, liquid_type = 'Lava', room_is_cold = true}\n" +
            "fx:set('type', 'Ambient Light')\n" +
            "local c = fx:get('ambient_color') out[#out + 1] = c.r .. ',' .. c.g .. ',' .. c.b\n" +
            "out[#out + 1] = tostring(fx:get('liquid_depth'))\n" +
            "fx:set('type', 'Liquid Room')\n" +
            "out[#out + 1] = fx:get('liquid_depth') .. '/' .. fx:get('liquid_type') .. '/' .. tostring(fx:get('room_is_cold'))\n" +
            "return table.concat(out, ';')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        // Sky Room has neither block; Liquid Room starts as still water; switching away and back keeps the liquid values.
        Assert.Equal("Sky Room;nil;nil;Water/None;128,128,128;nil;2/Lava/true", r.ReturnValue);

        RoomEffect fx = doc.Rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>().Single().Effects.Single();
        Assert.Equal(RoomEffectsSection.EffectLiquidRoom, fx.EffectType);
        EditorDocument reloaded = EditorDocument.OpenBytes(doc.SaveToBytes(updateTimestamp: false));
        Assert.Equal(doc.SaveToBytes(updateTimestamp: false), reloaded.SaveToBytes(updateTimestamp: false));

        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void A_Field_Of_A_Block_That_Does_Not_Apply_Refuses_With_A_Hint()
    {
        ScriptApiV2ContractTests.AssertApiError(NewLevel(),
            "level.place{kind = 'room_effect', pos = {0, 2, 0}, props = {type = 'sky_room'}}:set('liquid_depth', 3)",
            "liquid_depth", "Set type to \"Liquid Room\" first.");
        ScriptApiV2ContractTests.AssertApiError(NewLevel(),
            "level.place{kind = 'room_effect', pos = {0, 2, 0}, props = {type = 'skyroom'}}",
            "has no option 'skyroom'", "Sky Room");
    }

    [Theory]
    [InlineData(RoomEffectsSection.EffectSkyRoom)]
    [InlineData(RoomEffectsSection.EffectLiquidRoom)]
    [InlineData(RoomEffectsSection.EffectAmbientLight)]
    [InlineData(RoomEffectsSection.EffectNone)]
    public void Each_Type_Switch_Is_One_Undo_Step_On_The_Panels_Path(int type)
    {
        EditorDocument doc = NewLevel();
        LevelObject lo = doc.PlaceObject(LevelObjectKind.RoomEffect, new Vec3(0, 2, 0))!;
        doc.MarkSaved();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        int nodes = doc.Undo.NodeCount;
        InspectorField typeField = ObjectInspectorCatalog.For(LevelObjectKind.RoomEffect).Single(f => f.Path == "EffectType");

        InspectorAccess.Set(doc, lo, typeField, type);
        var fx = (RoomEffect)lo.Model;
        Assert.Equal(type, fx.EffectType);
        Assert.Equal(type == RoomEffectsSection.EffectAmbientLight, fx.AmbientLightColor is not null);
        Assert.Equal(type == RoomEffectsSection.EffectLiquidRoom, fx.LiquidProperties is not null);
        Assert.Equal(type == RoomEffectsSection.EffectNone ? nodes : nodes + 1, doc.Undo.NodeCount); // None is the blueprint's type
        _ = doc.SaveToBytes(updateTimestamp: false); // serializes in every state

        if (type != RoomEffectsSection.EffectNone)
        {
            doc.Undo.Undo();
            Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
        }
    }

    [Fact]
    public void Stock_Room_Effects_Read_Through_The_Field_Model_Without_Changing_A_Byte()
    {
        string? root = ScriptTestFixture.RepoRoot();
        string dir = root is null ? string.Empty : Path.Combine(root, "research", "example_rfls");
        if (!Directory.Exists(dir))
        {
            return; // research corpus absent: skip
        }

        foreach (string level in new[] { "dm01.rfl", "dm04.rfl", "ctf02.rfl" }.Select(f => Path.Combine(dir, f)).Where(File.Exists))
        {
            byte[] original = File.ReadAllBytes(level);
            EditorDocument doc = EditorDocument.OpenBytes(original, level);
            byte[] baseline = doc.SaveToBytes(updateTimestamp: false);
            (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
                "local n = 0 for _, o in ipairs(level.objects_of('RoomEffect')) do for _, f in ipairs(o:fields()) do local v = o:get(f.name) n = n + 1 end end\n" +
                "return level.objects_of('RoomEffect')[1]:get('type') .. ':' .. n");
            Assert.True(r.Success, r.Error?.ToDisplayString());
            Assert.StartsWith("Sky Room:", r.ReturnValue, StringComparison.Ordinal);
            Assert.Equal(baseline, doc.SaveToBytes(updateTimestamp: false));
        }
    }
}
