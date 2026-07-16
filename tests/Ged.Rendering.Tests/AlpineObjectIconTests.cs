using System;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Alpine point objects (Note / Corona / Bag) must emit a billboard with their atlas
/// icon. Before the fix the emitter had no case for these three sections, so they were
/// placeable yet invisible in the viewport.
/// </summary>
public sealed class AlpineObjectIconTests
{
    private static RenderScene BuildScene()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "alpine";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        rfl.Sections.Insert(0, new RflSection((uint)SectionType.AlpineNoteObjects, Array.Empty<byte>())
        {
            Content = new AlpineNoteObjectsSection { Notes = { new AlpineNoteObject { Uid = 10, Position = new Vec3(1, 2, 3) } } },
            Dirty = true,
        });
        rfl.Sections.Insert(1, new RflSection((uint)SectionType.AlpineCoronaObjects, Array.Empty<byte>())
        {
            Content = new AlpineCoronaObjectsSection { Coronas = { new AlpineCoronaObject { Uid = 11, Position = new Vec3(4, 5, 6) } } },
            Dirty = true,
        });
        rfl.Sections.Insert(2, new RflSection((uint)SectionType.AlpineBagObjects, Array.Empty<byte>())
        {
            Content = new AlpineBagObjectsSection { Bags = { new AlpineBagObject { Uid = 12, Position = new Vec3(7, 8, 9) } } },
            Dirty = true,
        });
        return SceneBuilder.Build(rfl, new SceneBuildOptions { IncludeStaticGeometry = false, IncludeMovers = false });
    }

    [Theory]
    [InlineData(BillboardKind.Note, (int)EditorIcon.Note)]
    [InlineData(BillboardKind.Corona, (int)EditorIcon.Corona)]
    [InlineData(BillboardKind.Bag, (int)EditorIcon.Bag)]
    public void Alpine_Object_Emits_Its_Icon_Billboard(BillboardKind kind, int icon)
    {
        RenderScene scene = BuildScene();
        Billboard bb = Assert.Single(scene.Billboards, b => b.Kind == kind);
        Assert.Equal(icon, bb.Icon);
    }
}
