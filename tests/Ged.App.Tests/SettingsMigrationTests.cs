using System.IO;
using Ged.App;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Settings-key migration: the "Show Clipped Brush Faces" toggle was renamed to
/// "Draw unmerged brushwork", moving its persisted JSON key
/// <c>ShowClippedBrushFaces</c> → <c>DrawUnmergedBrushwork</c>. Loading an older
/// settings.cfg must carry the user's choice forward instead of silently resetting it.
/// </summary>
public sealed class SettingsMigrationTests
{
    private static string WriteTemp(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ged-settings-{System.Guid.NewGuid():N}.cfg");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Legacy_ShowClippedBrushFaces_True_Migrates_To_DrawUnmergedBrushwork()
    {
        string path = WriteTemp("{ \"ShowClippedBrushFaces\": true }");
        try
        {
            AppSettings loaded = SettingsStore.Load(path);
            Assert.True(loaded.DrawUnmergedBrushwork);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Legacy_ShowClippedBrushFaces_False_Migrates_As_False()
    {
        string path = WriteTemp("{ \"ShowClippedBrushFaces\": false }");
        try
        {
            Assert.False(SettingsStore.Load(path).DrawUnmergedBrushwork);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void New_Key_Wins_Over_Legacy_Key_When_Both_Present()
    {
        string path = WriteTemp("{ \"ShowClippedBrushFaces\": true, \"DrawUnmergedBrushwork\": false }");
        try
        {
            Assert.False(SettingsStore.Load(path).DrawUnmergedBrushwork);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void New_Key_Round_Trips()
    {
        string path = WriteTemp("{ \"DrawUnmergedBrushwork\": true }");
        try
        {
            Assert.True(SettingsStore.Load(path).DrawUnmergedBrushwork);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Dead default brush texture (the white-brush repeat-class bug) ---------
    // Builds that shipped "Rck_Default01.tga" (nonexistent in stock RF) persisted it into
    // settings.cfg; the code-side constant fix alone left upgraded installs creating white
    // brushes because the stale persisted name overrode the corrected default on load.

    [Fact]
    public void Dead_Default_Texture_Migrates_To_The_Stock_Name_On_Load()
    {
        string path = WriteTemp(
            "{ \"DefaultFloorTexture\": \"Rck_Default01.tga\"," +
            " \"DefaultWallTexture\": \"Rck_Default01.tga\"," +
            " \"DefaultCeilingTexture\": \"Rck_Default01.tga\" }");
        try
        {
            AppSettings loaded = SettingsStore.Load(path);
            Assert.Equal(Ged.Core.Editing.BrushCreateParams.StockFloorTexture, loaded.DefaultFloorTexture);
            Assert.Equal(Ged.Core.Editing.BrushCreateParams.StockWallTexture, loaded.DefaultWallTexture);
            Assert.Equal(Ged.Core.Editing.BrushCreateParams.StockCeilingTexture, loaded.DefaultCeilingTexture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dead_Default_Texture_Migration_Is_Case_Insensitive()
    {
        string path = WriteTemp("{ \"DefaultWallTexture\": \"rck_default01.TGA\" }");
        try
        {
            Assert.Equal(
                Ged.Core.Editing.BrushCreateParams.StockWallTexture,
                SettingsStore.Load(path).DefaultWallTexture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Custom_Default_Textures_Are_Not_Touched_By_The_Migration()
    {
        string path = WriteTemp("{ \"DefaultFloorTexture\": \"my_floor.tga\", \"DefaultWallTexture\": \"my_wall.tga\" }");
        try
        {
            AppSettings loaded = SettingsStore.Load(path);
            Assert.Equal("my_floor.tga", loaded.DefaultFloorTexture);
            Assert.Equal("my_wall.tga", loaded.DefaultWallTexture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Alpine-only launch: a legacy stock RF.exe game-exe path migrates ------
    // GED now play-tests only through AlpineFactionLauncher.exe. A settings.cfg from when
    // stock RF.exe could be configured must not break load: adopt the launcher beside it, or
    // clear the path so play-test re-guesses/prompts.

    private static string WriteGameExeSettings(string exePath)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { GameExePath = exePath });
        return WriteTemp(json);
    }

    [Fact]
    public void Legacy_Rf_Exe_Adopts_The_Alpine_Launcher_Beside_It()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ged-exe-mig-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string rf = Path.Combine(dir, "RF.exe");
        string launcher = Path.Combine(dir, "AlpineFactionLauncher.exe");
        File.WriteAllText(rf, string.Empty);
        File.WriteAllText(launcher, string.Empty);
        string cfg = WriteGameExeSettings(rf);
        try
        {
            Assert.Equal(launcher, SettingsStore.Load(cfg).GameExePath);
        }
        finally
        {
            File.Delete(cfg);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Legacy_Rf_Exe_Without_A_Launcher_Beside_It_Is_Cleared()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ged-exe-mig-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string rf = Path.Combine(dir, "RF.exe");
        File.WriteAllText(rf, string.Empty);
        string cfg = WriteGameExeSettings(rf);
        try
        {
            Assert.Equal(string.Empty, SettingsStore.Load(cfg).GameExePath);
        }
        finally
        {
            File.Delete(cfg);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Configured_Alpine_Launcher_Path_Is_Left_Untouched()
    {
        string launcher = Path.Combine(Path.GetTempPath(), "AlpineFactionLauncher.exe");
        string cfg = WriteGameExeSettings(launcher);
        try
        {
            Assert.Equal(launcher, SettingsStore.Load(cfg).GameExePath);
        }
        finally
        {
            File.Delete(cfg);
        }
    }

    [Fact]
    public void Blank_Game_Exe_Path_Stays_Blank()
    {
        string cfg = WriteGameExeSettings(string.Empty);
        try
        {
            Assert.Equal(string.Empty, SettingsStore.Load(cfg).GameExePath);
        }
        finally
        {
            File.Delete(cfg);
        }
    }
}
