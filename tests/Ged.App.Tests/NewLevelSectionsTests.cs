using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 3: File &gt; New (EditorSession.NewLevel) must produce a document that already contains
/// initialized level_properties and level_info sections, and a Save/reload must preserve them.
/// </summary>
public sealed class NewLevelSectionsTests
{
    [AvaloniaFact]
    public void New_Level_Contains_Initialized_Level_Property_And_Info_Sections()
    {
        var session = new EditorSession();
        session.NewLevel();

        var lp = session.Document!.Rfl.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().SingleOrDefault();
        var info = session.Document!.Rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().SingleOrDefault();

        Assert.NotNull(lp);
        Assert.NotNull(info);
        Assert.Equal("rock02.tga", lp!.GeomodTexture);
        Assert.Equal(50, lp.Hardness);
        Assert.Equal(new RfColor(40, 40, 40, 255), lp.AmbientColor);
        Assert.Equal(new RfColor(0, 0, 0, 255), lp.FogColor);

        Assert.Equal(string.Empty, info!.LevelName);
        Assert.Equal(string.Empty, info.Author);
        Assert.False(string.IsNullOrWhiteSpace(info.Date)); // today, filled in
        Assert.Equal(4, info.ViewConfigs.Count);
    }

    [AvaloniaFact]
    public void New_Level_Round_Trips_The_Sections_Through_Save()
    {
        var session = new EditorSession();
        session.NewLevel();

        byte[] bytes = session.Document!.SaveToBytes();
        var reloaded = Ged.Core.IO.Rfl.RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        Assert.True(reloaded.Header.LevelInfoOffset > 0);
        Assert.Single(reloaded.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>());
        Assert.Single(reloaded.Sections.Select(s => s.Content).OfType<LevelInfoSection>());
    }

    [AvaloniaFact]
    public void New_Level_Authors_A_Player_Start_Inside_The_Working_Volume()
    {
        var session = new EditorSession();
        session.NewLevel();

        var start = session.Document!.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>().SingleOrDefault();
        Assert.NotNull(start);
        Assert.Equal(new Vec3(0f, 1f, 0f), start!.Position); // one unit above the grid origin
        Assert.Equal(Mat3.Identity, start.Rotation);
    }

    [AvaloniaFact]
    public void New_Level_Player_Start_Round_Trips_And_Sets_The_Header_Offset()
    {
        var session = new EditorSession();
        session.NewLevel();

        byte[] bytes = session.Document!.SaveToBytes();
        var reloaded = Ged.Core.IO.Rfl.RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        // player_start_offset must be non-zero (RF reads the spawn transform from it) and the
        // section must parse back to the authored position.
        Assert.True(reloaded.Header.PlayerStartOffset > 0);
        var start = reloaded.Sections.Select(s => s.Content).OfType<PlayerStartSection>().SingleOrDefault();
        Assert.NotNull(start);
        Assert.Equal(new Vec3(0f, 1f, 0f), start!.Position);
    }
}
