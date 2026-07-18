using System;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 3 — a newly-placed object defaults its script name with NO placeholders: a CLASS-BASED object
/// (entity / item / clutter) uses exactly its class name; every other kind uses its canonical palette
/// DISPLAY NAME (e.g. "Bolt Emitter", "Light"). Always freely renamable afterward. Every creation path
/// routes through <see cref="ObjectFactory.Build"/>.
/// </summary>
public sealed class ObjectScriptNameDefaultTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Theory]
    [InlineData(LevelObjectKind.Entity, "APC")]
    [InlineData(LevelObjectKind.Clutter, "officechair")]
    [InlineData(LevelObjectKind.Item, "Medical Kit")]
    public void Class_Based_Object_Defaults_Script_Name_To_Class_Name(LevelObjectKind kind, string className)
    {
        EditorDocument doc = EmptyDoc();
        LevelObject o = doc.PlaceObject(kind, new Vec3(1, 2, 3), className)!;

        Assert.Equal(className, o.ClassName);
        Assert.Equal(className, o.ScriptName);

        // Still renamable afterward (editing the script name works normally).
        o.ScriptName = "custom_name";
        Assert.Equal("custom_name", o.ScriptName);
    }

    [Fact]
    public void Class_Based_Object_With_No_Class_Uses_The_Kind_Fallback_As_Both_Class_And_Script()
    {
        EditorDocument doc = EmptyDoc();
        LevelObject e = doc.PlaceObject(LevelObjectKind.Entity, Vec3.Zero, className: null)!;
        Assert.Equal("Guard", e.ClassName);
        Assert.Equal("Guard", e.ScriptName); // script name mirrors the class name even for the fallback
    }

    [Theory]
    [InlineData(LevelObjectKind.Light, "Light")]
    [InlineData(LevelObjectKind.BoltEmitter, "Bolt Emitter")]
    [InlineData(LevelObjectKind.ParticleEmitter, "Particle Emitter")]
    [InlineData(LevelObjectKind.Trigger, "Trigger")]
    [InlineData(LevelObjectKind.MpRespawnPoint, "MP Respawn Point")]
    [InlineData(LevelObjectKind.MeshObject, "Mesh Object")]
    public void Non_Class_Object_Defaults_Script_Name_To_Its_Display_Name(LevelObjectKind kind, string display)
    {
        EditorDocument doc = EmptyDoc();
        LevelObject o = doc.PlaceObject(kind, Vec3.Zero)!;
        Assert.Equal(display, o.ScriptName); // the palette / Outliner display name, no placeholder

        o.ScriptName = "renamed";
        Assert.Equal("renamed", o.ScriptName);
    }
}
