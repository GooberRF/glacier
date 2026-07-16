using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Room effects as first-class level objects: the room_effects section projects each marker
/// as a selectable, UID-resolvable <see cref="LevelObject"/> (so it shows in the outliner,
/// resolves from a viewport pick, and drives the properties inspector), hide/show and delete
/// route through its <see cref="ObjectHeader"/>, and selecting one never dirties the section
/// (an untouched level still round-trips byte-identically).
/// </summary>
public sealed class RoomEffectObjectTests
{
    private static RflFile SyntheticLevel()
    {
        var effects = new List<RoomEffect>
        {
            new()
            {
                EffectType = RoomEffectsSection.EffectSkyRoom,
                Header = new ObjectHeader { Uid = 5001, ClassName = "Room Effect", ScriptName = "Room Effect", Position = new Vec3(1, 2, 3) },
            },
            new()
            {
                EffectType = RoomEffectsSection.EffectLiquidRoom,
                LiquidProperties = new RoomEffectLiquidProperties
                {
                    Waveform = 2, Depth = 4f, SurfaceTexture = "water.tga", LiquidType = 1,
                    Visibility = 8f, TexturePixelsPerMeterU = 256, TexturePixelsPerMeterV = 256,
                    TextureScrollRate = new Uv(0.1f, 0f),
                },
                Header = new ObjectHeader { Uid = 5002, ClassName = "Room Effect", ScriptName = "Room Effect", Position = new Vec3(4, 5, 6) },
            },
            new()
            {
                EffectType = RoomEffectsSection.EffectAmbientLight,
                AmbientLightColor = new RfColor(64, 96, 128, 255),
                Header = new ObjectHeader { Uid = 5003, ClassName = "Room Effect", ScriptName = "Room Effect", Position = new Vec3(7, 8, 9) },
            },
        };

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "roomfx.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.RoomEffects, Array.Empty<byte>())
        {
            Content = new RoomEffectsSection { Effects = effects },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    [Fact]
    public void Room_Effects_Project_As_Resolvable_Level_Objects()
    {
        var doc = new EditorDocument(SyntheticLevel());

        LevelObject[] fx = doc.Objects.Where(o => o.Kind == LevelObjectKind.RoomEffect).ToArray();
        Assert.Equal(3, fx.Length);

        // Each is resolvable by its own UID (the pick -> FindByUid -> select path).
        LevelObject? sky = doc.FindByUid(5001);
        Assert.NotNull(sky);
        Assert.Equal(LevelObjectKind.RoomEffect, sky!.Kind);
        Assert.Equal(new Vec3(1, 2, 3), sky.Position);

        Assert.NotNull(doc.FindByUid(5002));
        Assert.NotNull(doc.FindByUid(5003));

        // The model is the parsed RoomEffect (drives the dedicated inspector).
        Assert.IsType<RoomEffect>(sky.Model);
    }

    [Fact]
    public void Hiding_A_Room_Effect_Sets_Its_Header_Byte_And_Dirties_The_Section()
    {
        var doc = new EditorDocument(SyntheticLevel());
        LevelObject fx = doc.FindByUid(5001)!;
        var model = (RoomEffect)fx.Model;

        Assert.Equal(0, model.Header.HiddenInEditor);
        fx.Hidden = true;

        Assert.Equal(1, model.Header.HiddenInEditor);
        Assert.True(fx.Section.Dirty);
    }

    [Fact]
    public void Room_Effect_Position_Edit_Is_Undoable_And_Dirties_The_Section()
    {
        var doc = new EditorDocument(SyntheticLevel());
        LevelObject fx = doc.FindByUid(5002)!;
        Vec3 old = fx.Position;
        var next = new Vec3(11, 12, 13);

        doc.EditValue(fx.Section, "Move room effect", old, next, v => fx.Position = v);
        Assert.Equal(next, fx.Position);
        Assert.True(fx.Section.Dirty);

        doc.Undo.Undo();
        Assert.Equal(old, fx.Position);
    }

    [Fact]
    public void Changing_Effect_Type_And_Provisioning_Liquid_Block_Is_One_Undo_Step()
    {
        // Mirrors what the inspector does: switch a sky room to a liquid room and provision the
        // liquid block in a single transaction, so undo reverts both together.
        var doc = new EditorDocument(SyntheticLevel());
        LevelObject fx = doc.FindByUid(5001)!;
        var model = (RoomEffect)fx.Model;
        Assert.Null(model.LiquidProperties);

        using (doc.Undo.BeginTransaction("Change room-effect type"))
        {
            doc.EditValue(fx.Section, "Effect type", model.EffectType, RoomEffectsSection.EffectLiquidRoom, v => model.EffectType = v);
            doc.EditValue(fx.Section, "Init liquid props", model.LiquidProperties,
                (RoomEffectLiquidProperties?)new RoomEffectLiquidProperties { Waveform = 1, LiquidType = 1 },
                v => model.LiquidProperties = v);
        }

        Assert.Equal(RoomEffectsSection.EffectLiquidRoom, model.EffectType);
        Assert.NotNull(model.LiquidProperties);

        doc.Undo.Undo();
        Assert.Equal(RoomEffectsSection.EffectSkyRoom, model.EffectType);
        Assert.Null(model.LiquidProperties);
    }

    [Fact]
    public void Delete_And_Undo_Round_Trips_The_Room_Effect()
    {
        var doc = new EditorDocument(SyntheticLevel());
        LevelObject fx = doc.FindByUid(5003)!;
        Assert.True(fx.CanRemove);

        doc.Select(fx);
        doc.DeleteSelection();
        Assert.Null(doc.FindByUid(5003));

        doc.Undo.Undo();
        Assert.NotNull(doc.FindByUid(5003));
    }

    [Fact]
    public void Selecting_A_Room_Effect_Does_Not_Dirty_The_File_On_No_Op_Save()
    {
        RflFile built = SyntheticLevel();
        byte[] original = built.Save(updateTimestamp: false);

        // Fresh load -> the enumerator projects the room effects; touching them read-only
        // (find + select) must not dirty the section, so the resave is byte-identical.
        var doc = EditorDocument.OpenBytes(original);
        LevelObject fx = doc.FindByUid(5002)!;
        doc.Select(fx);
        _ = fx.Position;
        _ = ((RoomEffect)fx.Model).LiquidProperties?.Depth;

        byte[] resaved = doc.SaveToBytes(updateTimestamp: false);
        Assert.True(original.AsSpan().SequenceEqual(resaved), "no-op resave after selecting a room effect was not byte-identical.");
    }

    // ---- Placement (palette flow: ObjectFactory blueprint -> PlaceObject) -----

    [Fact]
    public void Palette_Lists_Room_Effect_As_A_Stock_Placeable()
    {
        Editing.PlaceableObjectType? entry =
            Editing.ObjectFactory.Palette.FirstOrDefault(p => p.Kind == LevelObjectKind.RoomEffect);
        Assert.NotNull(entry);
        Assert.False(entry!.NeedsClassName);
        Assert.False(entry.Alpine); // stock section (rfl.ksy has no version gate on room_effect)
        Assert.Contains(LevelObjectKind.RoomEffect, Editing.ObjectFactory.RoundTripKinds);
    }

    [Fact]
    public void Placing_A_Room_Effect_Creates_RED_Defaults_With_A_Fresh_Uid_And_Undoes_Cleanly()
    {
        // An empty level: placement must create the room_effects section too.
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "place.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        var doc = new EditorDocument(rfl);

        var pos = new Vec3(3, 4, 5);
        LevelObject? placed = doc.PlaceObject(LevelObjectKind.RoomEffect, pos);
        Assert.NotNull(placed);
        Assert.Equal(LevelObjectKind.RoomEffect, placed!.Kind);
        Assert.Equal(pos, placed.Position);
        Assert.Same(placed, doc.FindByUid(placed.Uid));

        // RED's own new-Room-Effect defaults (RED.exe ctor 0x4548b0): type None, flags clear,
        // no nested blocks; class and script name are both "Room Effect".
        var model = (RoomEffect)placed.Model;
        Assert.Equal(RoomEffectsSection.EffectNone, model.EffectType);
        Assert.Equal("Room Effect", model.Header.ClassName);
        Assert.Equal("Room Effect", model.Header.ScriptName);
        Assert.Equal(0, model.RoomIsCold);
        Assert.Equal(0, model.RoomIsOutside);
        Assert.Equal(0, model.RoomIsAirLock);
        Assert.Null(model.AmbientLightColor);
        Assert.Null(model.LiquidProperties);
        Assert.True(placed.Section.Dirty);

        int uid = placed.Uid;
        doc.Undo.Undo();
        Assert.Null(doc.FindByUid(uid));
        doc.Undo.Redo();
        Assert.NotNull(doc.FindByUid(uid));
    }

    [Fact]
    public void Placed_Room_Effect_Round_Trips_Through_The_Parser()
    {
        // Place into a level that already HAS room effects: the dirtied section re-serializes
        // both the existing effects and the new one, and the parser reads them all back.
        RflFile built = SyntheticLevel();
        var doc = new EditorDocument(built);
        LevelObject placed = doc.PlaceObject(LevelObjectKind.RoomEffect, new Vec3(9, 9, 9))!;
        int uid = placed.Uid;

        byte[] saved = doc.SaveToBytes(updateTimestamp: false);
        var reloaded = EditorDocument.OpenBytes(saved);

        LevelObject[] fx = reloaded.Objects.Where(o => o.Kind == LevelObjectKind.RoomEffect).ToArray();
        Assert.Equal(4, fx.Length); // 3 originals + the placed one

        LevelObject? back = reloaded.FindByUid(uid);
        Assert.NotNull(back);
        var model = (RoomEffect)back!.Model;
        Assert.Equal(RoomEffectsSection.EffectNone, model.EffectType);
        Assert.Equal("Room Effect", model.Header.ClassName);
        Assert.Equal(new Vec3(9, 9, 9), back.Position);

        // Serialization fixpoint: a no-op re-save of the reloaded bytes is byte-identical.
        Assert.True(saved.AsSpan().SequenceEqual(reloaded.SaveToBytes(updateTimestamp: false)));
    }

    [Fact]
    public void Corpus_Room_Effects_Are_All_Projected_And_Resolvable()
    {
        if (!Corpus.Available)
        {
            return;
        }

        // Every room effect in every corpus level must project as a RoomEffect object that
        // resolves by its own UID (the outliner listing + pick -> select path). At least one
        // corpus level has room effects, so this is not a vacuous pass.
        int levelsWithEffects = 0;
        foreach (string path in Corpus.RflFiles)
        {
            RflFile file;
            try
            {
                file = RflFile.Load(path);
                file.ParseAllKnownSections();
            }
            catch
            {
                continue;
            }

            RoomEffectsSection? section = file.Sections
                .Select(s => s.Content).OfType<RoomEffectsSection>().FirstOrDefault();
            if (section is null || section.Effects.Count == 0)
            {
                continue;
            }

            levelsWithEffects++;
            var doc = new EditorDocument(file, path);
            LevelObject[] projected = doc.Objects.Where(o => o.Kind == LevelObjectKind.RoomEffect).ToArray();

            Assert.Equal(section.Effects.Count, projected.Length);
            foreach (RoomEffect e in section.Effects)
            {
                Assert.NotNull(doc.FindByUid(e.Header.Uid));
            }
        }

        Assert.True(levelsWithEffects > 0, "corpus has no room effects to exercise projection.");
    }
}
