using System;
using System.Globalization;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 3: a new document must contain initialized level_properties and level_info sections
/// with the researched RED defaults, and those defaults must survive a save/reload round-trip.
/// </summary>
public sealed class NewLevelDefaultsTests
{
    [Fact]
    public void LevelProperties_Default_Matches_The_Researched_Values()
    {
        LevelPropertiesSection lp = LevelPropertiesSection.CreateDefault();

        Assert.Equal("rock02.tga", lp.GeomodTexture);
        Assert.Equal(50, lp.Hardness); // RED constructor: mov [level+0x8c], 0x32
        Assert.Equal(new RfColor(40, 40, 40, 255), lp.AmbientColor);
        Assert.Equal(0, lp.DirectionalAmbientLight);
        Assert.Equal(new RfColor(0, 0, 0, 255), lp.FogColor); // black (Alpine editor_patch)
    }

    [Fact]
    public void LevelInfo_Default_Has_Empty_Name_Author_Todays_Date_And_Four_Views()
    {
        var now = new DateTime(2026, 7, 8, 9, 5, 3);
        LevelInfoSection info = LevelInfoSection.CreateDefault(now);

        Assert.Equal(string.Empty, info.LevelName);
        Assert.Equal(string.Empty, info.Author);
        Assert.Equal(now.ToString(LevelInfoSection.DateFormat, CultureInfo.InvariantCulture), info.Date);
        Assert.Equal("Wednesday, July 8, 2026 09:05:03", info.Date);
        Assert.Equal(0, info.HasMovers);
        Assert.Equal(0, info.MultiplayerLevel);

        // RED's four panes, in its write order: Top(1), Front(3), Free Look(0), Left(5).
        Assert.Equal(new[] { 1, 3, 0, 5 }, info.ViewConfigs.Select(v => v.ViewType).ToArray());
        Assert.All(info.ViewConfigs, v => Assert.Equal(Mat3.Identity, v.Rotation));
    }

    [Fact]
    public void New_Document_Sections_Round_Trip_Through_Save_And_Reload()
    {
        var now = new DateTime(2026, 7, 8, 9, 5, 3);
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "untitled.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelProperties, Array.Empty<byte>())
        { Content = LevelPropertiesSection.CreateDefault(), Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelInfo, Array.Empty<byte>())
        { Content = LevelInfoSection.CreateDefault(now), Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        byte[] bytes = rfl.Save();
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        // The header's level_info offset must point at the emitted section.
        Assert.True(reloaded.Header.LevelInfoOffset > 0);

        LevelPropertiesSection lp = reloaded.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().Single();
        Assert.Equal("rock02.tga", lp.GeomodTexture);
        Assert.Equal(50, lp.Hardness);
        Assert.Equal(new RfColor(40, 40, 40, 255), lp.AmbientColor);
        Assert.Equal(new RfColor(0, 0, 0, 255), lp.FogColor);

        LevelInfoSection info = reloaded.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single();
        Assert.Equal(string.Empty, info.LevelName);
        Assert.Equal(string.Empty, info.Author);
        Assert.Equal("Wednesday, July 8, 2026 09:05:03", info.Date);
        Assert.Equal(4, info.ViewConfigs.Count);
        Assert.Equal(new[] { 1, 3, 0, 5 }, info.ViewConfigs.Select(v => v.ViewType).ToArray());

        // The free-look view keeps a 3D position; the ortho views keep four floats.
        EditorViewConfig freeLook = info.ViewConfigs[2];
        Assert.NotNull(freeLook.Position3d);
        Assert.Null(freeLook.Position2d);
        Assert.All(info.ViewConfigs.Where(v => v.ViewType != 0), v => Assert.NotNull(v.Position2d));
    }
}
