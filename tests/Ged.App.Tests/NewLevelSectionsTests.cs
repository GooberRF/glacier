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
}
