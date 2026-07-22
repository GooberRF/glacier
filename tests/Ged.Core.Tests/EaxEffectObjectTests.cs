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
/// B3: EAX effect zones as first-class level objects. The eax_effects section projects each
/// effect as a selectable, UID-resolvable <see cref="LevelObject"/> (so it shows in the outliner,
/// resolves from a viewport pick, and drives the dedicated inspector), hide/position edits route
/// through its <see cref="ObjectHeader"/>, and read-only touching (find + select) never dirties
/// the section — an untouched level still round-trips byte-identically.
/// </summary>
public sealed class EaxEffectObjectTests
{
    private static RflFile SyntheticLevel()
    {
        var effects = new List<EaxEffect>
        {
            new()
            {
                EffectType = "GENERIC",
                Header = new ObjectHeader { Uid = 6001, ClassName = "EAX Effect", ScriptName = "EAX Effect", Position = new Vec3(1, 2, 3) },
            },
            new()
            {
                EffectType = "PADDEDCELL",
                Header = new ObjectHeader { Uid = 6002, ClassName = "EAX Effect", ScriptName = "EAX Effect", Position = new Vec3(4, 5, 6) },
            },
        };

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "eax.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.EaxEffects, Array.Empty<byte>())
        {
            Content = new EaxEffectsSection { Effects = effects },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    [Fact]
    public void Eax_Effects_Project_As_Resolvable_Level_Objects()
    {
        var doc = new EditorDocument(SyntheticLevel());

        LevelObject[] eax = doc.Objects.Where(o => o.Kind == LevelObjectKind.Eax).ToArray();
        Assert.Equal(2, eax.Length);

        // Each is resolvable by its own UID (the pick -> FindByUid -> select path that made it
        // unclickable before the projection existed).
        LevelObject? first = doc.FindByUid(6001);
        Assert.NotNull(first);
        Assert.Equal(LevelObjectKind.Eax, first!.Kind);
        Assert.Equal(new Vec3(1, 2, 3), first.Position);
        Assert.IsType<EaxEffect>(first.Model); // drives the dedicated inspector

        Assert.NotNull(doc.FindByUid(6002));
    }

    [Fact]
    public void Hiding_An_Eax_Effect_Sets_Its_Header_Byte_And_Dirties_The_Section()
    {
        var doc = new EditorDocument(SyntheticLevel());
        LevelObject eax = doc.FindByUid(6001)!;
        var model = (EaxEffect)eax.Model;

        Assert.Equal(0, model.Header.HiddenInEditor);
        eax.Hidden = true;
        Assert.Equal(1, model.Header.HiddenInEditor);
        Assert.True(eax.Section.Dirty);
    }

    [Fact]
    public void Eax_Position_Edit_Is_Undoable_And_Dirties_The_Section()
    {
        var doc = new EditorDocument(SyntheticLevel());
        LevelObject eax = doc.FindByUid(6002)!;
        Vec3 old = eax.Position;
        var next = new Vec3(11, 12, 13);

        doc.EditValue(eax.Section, "Move EAX effect", old, next, v => eax.Position = v);
        Assert.Equal(next, eax.Position);
        Assert.True(eax.Section.Dirty);

        doc.Undo.Undo();
        Assert.Equal(old, eax.Position);
    }

    [Fact]
    public void Selecting_An_Eax_Effect_Does_Not_Dirty_The_File_On_No_Op_Save()
    {
        RflFile built = SyntheticLevel();
        byte[] original = built.Save(updateTimestamp: false);

        // Fresh load -> the enumerator projects the EAX effects; touching them read-only (find +
        // select) must not dirty the section, so the resave is byte-identical (B3 round-trip gate).
        var doc = EditorDocument.OpenBytes(original);
        LevelObject eax = doc.FindByUid(6002)!;
        doc.Select(eax);
        _ = eax.Position;
        _ = ((EaxEffect)eax.Model).EffectType;

        byte[] resaved = doc.SaveToBytes(updateTimestamp: false);
        Assert.True(original.AsSpan().SequenceEqual(resaved), "no-op resave after selecting an EAX effect was not byte-identical.");
    }

    [Fact]
    public void Corpus_Eax_Effects_Are_All_Projected_And_Resolvable()
    {
        if (!Corpus.Available)
        {
            return;
        }

        // Every EAX effect in every corpus level must project as an Eax object resolvable by its
        // own UID (the outliner listing + pick -> select path). ctf06 carries EAX effects, so this
        // is not a vacuous pass.
        int levelsWithEax = 0;
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

            EaxEffectsSection? section = file.Sections
                .Select(s => s.Content).OfType<EaxEffectsSection>().FirstOrDefault();
            if (section is null || section.Effects.Count == 0)
            {
                continue;
            }

            levelsWithEax++;
            var doc = new EditorDocument(file, path);
            LevelObject[] projected = doc.Objects.Where(o => o.Kind == LevelObjectKind.Eax).ToArray();

            Assert.Equal(section.Effects.Count, projected.Length);
            foreach (EaxEffect e in section.Effects)
            {
                Assert.NotNull(doc.FindByUid(e.Header.Uid));
            }
        }

        Assert.True(levelsWithEax > 0, "corpus has no EAX effects to exercise projection.");
    }
}
