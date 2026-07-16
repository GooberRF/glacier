using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>Gates for the editor-only ⇄ runtime light section relocation (undo-safe).</summary>
public sealed class LightRelocationTests
{
    [Fact]
    public void Toggle_Moves_Light_Between_Sections_And_Is_Undoable()
    {
        EditorDocument doc = LevelWithLight(5);
        Assert.False(LightRelocation.IsEditorOnly(doc, 5));

        Assert.True(LightRelocation.Toggle(doc, 5));
        Assert.True(LightRelocation.IsEditorOnly(doc, 5));
        Assert.Empty(Lights(doc, SectionType.Lights));
        Assert.Single(Lights(doc, SectionType.EditorOnlyLights));

        // Undo returns it to the runtime section.
        doc.Undo.Undo();
        Assert.False(LightRelocation.IsEditorOnly(doc, 5));
        Assert.Single(Lights(doc, SectionType.Lights));

        // Redo + toggle back to runtime.
        doc.Undo.Redo();
        Assert.True(LightRelocation.Toggle(doc, 5));
        Assert.False(LightRelocation.IsEditorOnly(doc, 5));
        Assert.Single(Lights(doc, SectionType.Lights));
    }

    [Fact]
    public void Toggle_Unknown_Uid_Returns_False()
    {
        EditorDocument doc = LevelWithLight(5);
        Assert.False(LightRelocation.Toggle(doc, 999));
    }

    private static System.Collections.Generic.List<Light> Lights(EditorDocument doc, SectionType type) =>
        doc.Rfl.Sections.FirstOrDefault(s => s.TypeId == (uint)type)?.Content is LightsSection s ? s.Lights : new();

    private static EditorDocument LevelWithLight(int uid)
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D;
        rfl.Header.LevelName = "l.rfl";
        var lights = new LightsSection(SectionType.Lights) { Lights = { new Light { Uid = uid } } };
        rfl.Sections.Add(new RflSection((uint)SectionType.Lights, Array.Empty<byte>()) { Content = lights, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }
}
