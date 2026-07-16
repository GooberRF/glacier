using System;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// The range-visualization gate (light-range / region spheres off by default): a
/// range is drawn only when its object is selected, its "Always Show Range" flag is
/// set, or the global "Show all ranges" toggle is on. A minimal level with a single
/// ranged light emits the sphere into <see cref="RenderScene.Lines"/> only, so the
/// gate is observable as lines present vs absent.
/// </summary>
public sealed class RangeVisibilityTests
{
    private const int LightUid = 4242;

    private static RflFile LevelWithLight(uint flags)
    {
        var lights = new LightsSection(SectionType.Lights);
        lights.Lights.Add(new Light
        {
            Uid = LightUid,
            Position = new Vec3(0f, 0f, 0f),
            Range = 10f,
            Color = new RfColor(255, 255, 255, 255),
            Flags = flags,
        });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "rangetest";
        rfl.Sections.Add(new RflSection((uint)SectionType.Lights, Array.Empty<byte>())
        {
            Content = lights,
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static bool RangeDrawn(uint flags, bool showAll, bool selected)
    {
        RenderScene scene = SceneBuilder.Build(LevelWithLight(flags), new SceneBuildOptions
        {
            IncludeMovers = false,
            ShowAllRanges = showAll,
            SelectedUids = selected ? new HashSet<int> { LightUid } : null,
        });

        // No links/paths/bounding-boxes in this level, so any line is the range sphere.
        return scene.Lines.Count > 0;
    }

    [Fact]
    public void Range_Hidden_By_Default()
    {
        Assert.False(RangeDrawn(flags: 0, showAll: false, selected: false));
    }

    [Fact]
    public void Range_Shown_When_Selected()
    {
        Assert.True(RangeDrawn(flags: 0, showAll: false, selected: true));
    }

    [Fact]
    public void Range_Shown_When_Always_Show_Range_Flag_Set()
    {
        // light_flags bit 0x80 = "Always Show Range".
        Assert.True(RangeDrawn(flags: 0x80, showAll: false, selected: false));
    }

    [Fact]
    public void Range_Shown_When_Show_All_Ranges_On()
    {
        Assert.True(RangeDrawn(flags: 0, showAll: true, selected: false));
    }
}
