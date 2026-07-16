using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Audit gate for item 1: every object kind the palette can place must show up in the
/// viewport as a billboard carrying a real (non-fallback) atlas icon. A kind with no
/// <see cref="SceneBuilder"/> emission is placeable yet invisible — the trigger /
/// gas-climb-push-region regression this test locks down. Mesh-rendered kinds still
/// drop a fallback billboard, so a billboard is always present.
/// </summary>
public sealed class PlaceableObjectIconAuditTests
{
    public static TheoryData<LevelObjectKind> PaletteKinds()
    {
        var data = new TheoryData<LevelObjectKind>();
        foreach (PlaceableObjectType t in ObjectFactory.Palette)
        {
            data.Add(t.Kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PaletteKinds))]
    public void Every_Palette_Kind_Emits_A_NonDefault_Icon_Billboard(LevelObjectKind kind)
    {
        RenderScene scene = BuildSingle(kind);
        Assert.NotEmpty(scene.Billboards);
        Assert.All(scene.Billboards, b => Assert.NotEqual((int)EditorIcon.Generic, b.Icon));
    }

    // The kinds that had no emission before item 1: each must now map to its own icon.
    [Theory]
    [InlineData(LevelObjectKind.Trigger, BillboardKind.Trigger, (int)EditorIcon.Trigger)]
    [InlineData(LevelObjectKind.GasRegion, BillboardKind.GasRegion, (int)EditorIcon.GasRegion)]
    [InlineData(LevelObjectKind.ClimbRegion, BillboardKind.ClimbRegion, (int)EditorIcon.ClimbRegion)]
    [InlineData(LevelObjectKind.PushRegion, BillboardKind.PushRegion, (int)EditorIcon.PushRegion)]
    public void Previously_Missing_Kind_Emits_Its_Distinct_Icon(LevelObjectKind kind, BillboardKind expectedKind, int expectedIcon)
    {
        RenderScene scene = BuildSingle(kind);
        Billboard bb = Assert.Single(scene.Billboards, b => b.Kind == expectedKind);
        Assert.Equal(expectedIcon, bb.Icon);
    }

    private static RenderScene BuildSingle(LevelObjectKind kind)
    {
        bool needsClass = ObjectFactory.Palette.First(p => p.Kind == kind).NeedsClassName;
        ObjectBlueprint bp = ObjectFactory.Build(kind, uid: 100, new Vec3(1, 2, 3), needsClass ? "TestClass" : null);
        IRflSectionContent content = bp.CreateSection();
        bp.Append(content);

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)bp.Section, Array.Empty<byte>()) { Content = content, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return SceneBuilder.Build(rfl, new SceneBuildOptions { IncludeStaticGeometry = false, IncludeMovers = false });
    }
}
