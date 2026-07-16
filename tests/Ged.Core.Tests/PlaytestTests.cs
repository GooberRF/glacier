using System;
using System.IO;
using Ged.Core.Playtest;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for playtest launch: exe-kind detection by filename, the staging
/// destination directory by mode, argument construction per exe kind (-level vs
/// -levelm), the stock-can't-do-multi guard, and file staging into a temp dir
/// (never a real install).
/// </summary>
public sealed class PlaytestTests
{
    [Theory]
    [InlineData(@"C:\RF\AlpineFactionLauncher.exe", GameKind.AlpineLauncher)]
    [InlineData(@"C:\RF\AlpineFaction.exe", GameKind.AlpineLauncher)]
    [InlineData(@"C:\RF\RF.exe", GameKind.StockRf)]
    [InlineData(@"C:\RF\rf.exe", GameKind.StockRf)]
    public void DetectKind_By_Filename(string exe, GameKind expected)
    {
        Assert.Equal(expected, GameLauncher.DetectKind(exe));
        Assert.Equal(expected == GameKind.AlpineLauncher, GameLauncher.SupportsMulti(GameLauncher.DetectKind(exe)));
    }

    [Fact]
    public void DestinationDir_By_Mode()
    {
        Assert.Equal(Path.Combine(@"C:\RF", "user_maps", "single"), GameLauncher.DestinationDir(@"C:\RF", PlaytestMode.Single));
        Assert.Equal(Path.Combine(@"C:\RF", "user_maps", "multi"), GameLauncher.DestinationDir(@"C:\RF", PlaytestMode.Multi));
    }

    [Fact]
    public void BuildCommand_Stock_Single_Uses_Level_Flag()
    {
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "dm-arena.rfl", PlaytestMode.Single, fromCamera: false);

        Assert.Equal("-level dm-arena.rfl", cmd.Arguments);
        Assert.Equal(Path.Combine(@"C:\RF", "user_maps", "single", "dm-arena.rfl"), cmd.DestinationRflPath);
        Assert.Equal(@"C:\RF", cmd.WorkingDirectory);
    }

    [Fact]
    public void BuildCommand_Adds_Rfl_Extension_And_Quotes_Spaces_And_ExtraArgs()
    {
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "my level", PlaytestMode.Single, fromCamera: false, extraArgs: "-windowed");

        Assert.Equal("-level \"my level.rfl\" -windowed", cmd.Arguments);
    }

    [Fact]
    public void BuildCommand_Alpine_Multi_Uses_Levelm_Flag_And_Multi_Dir()
    {
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\AlpineFactionLauncher.exe", @"C:\RF", "ctf-tower.rfl", PlaytestMode.Multi, fromCamera: false);

        Assert.Equal("-levelm ctf-tower.rfl", cmd.Arguments);
        Assert.Equal(Path.Combine(@"C:\RF", "user_maps", "multi", "ctf-tower.rfl"), cmd.DestinationRflPath);
    }

    [Fact]
    public void BuildCommand_Stock_Multi_Is_Rejected()
    {
        Assert.Throws<NotSupportedException>(() => GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "ctf-tower.rfl", PlaytestMode.Multi, fromCamera: false));
    }

    [Fact]
    public void StageLevel_Writes_To_The_Mode_Directory()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ged_playtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            PlaytestCommand cmd = GameLauncher.BuildCommand(
                Path.Combine(temp, "RF.exe"), temp, "arena.rfl", PlaytestMode.Single, fromCamera: false);

            byte[] payload = { 0xD4, 0xBA, 0xDA, 0x55 };
            string written = GameLauncher.StageLevel(cmd, payload);

            Assert.Equal(Path.Combine(temp, "user_maps", "single", "arena.rfl"), written);
            Assert.Equal(payload, File.ReadAllBytes(written));
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildCommand_Alpine_Multi_From_Camera_Composes_Levelm_With_Camera_Flag()
    {
        // The F10 "Play in Multi (Camera)" path: the F8 from-camera mechanism (staged
        // copy carries a Player-Start relocated to the active camera) composed with
        // the -levelm multiplayer launch. The command carries both facts.
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\AlpineFactionLauncher.exe", @"C:\RF", "ctf-tower.rfl", PlaytestMode.Multi, fromCamera: true);

        Assert.Equal("-levelm ctf-tower.rfl", cmd.Arguments);
        Assert.Equal(PlaytestMode.Multi, cmd.Mode);
        Assert.True(cmd.FromCamera);
        Assert.Equal(Path.Combine(@"C:\RF", "user_maps", "multi", "ctf-tower.rfl"), cmd.DestinationRflPath);
        Assert.Equal(@"C:\RF", cmd.WorkingDirectory);
    }

    [Fact]
    public void StageLevel_Multi_From_Camera_Writes_Camera_Payload_To_Multi_Dir()
    {
        // The staging composition: the camera-spawn bytes (not the on-disk save) land
        // in user_maps\multi. Staged into a temp dir — never a real install.
        string temp = Path.Combine(Path.GetTempPath(), "ged_playtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            PlaytestCommand cmd = GameLauncher.BuildCommand(
                Path.Combine(temp, "AlpineFactionLauncher.exe"), temp, "ctf-tower.rfl",
                PlaytestMode.Multi, fromCamera: true);

            byte[] cameraSpawnBytes = { 0xCA, 0x3E, 0x2A, 0x11 }; // stands in for SaveBytesWithCameraSpawn()
            string written = GameLauncher.StageLevel(cmd, cameraSpawnBytes);

            Assert.Equal(Path.Combine(temp, "user_maps", "multi", "ctf-tower.rfl"), written);
            Assert.Equal(cameraSpawnBytes, File.ReadAllBytes(written));
            Assert.True(cmd.FromCamera);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public void ComposeProcess_Blank_Template_Launches_Exe_Directly()
    {
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: false);

        GameLauncher.LaunchProcess p = GameLauncher.ComposeProcess(cmd, template: null);
        Assert.Equal(@"C:\RF\RF.exe", p.FileName);
        Assert.Equal("-level arena.rfl", p.Arguments);

        // Whitespace-only template is treated as blank (direct launch).
        GameLauncher.LaunchProcess p2 = GameLauncher.ComposeProcess(cmd, "   ");
        Assert.Equal(@"C:\RF\RF.exe", p2.FileName);
        Assert.Equal("-level arena.rfl", p2.Arguments);
    }

    [Fact]
    public void ComposeProcess_Wine_Template_Wraps_Exe_And_Args()
    {
        // The Linux default: the wrapper (wine) is the launched program; {exe} expands to
        // the (quoted-if-needed) exe path and {args} to the level arguments.
        GameLauncher.LaunchProcess p = GameLauncher.ComposeProcess(
            "/games/rf/RF.exe", "-level arena.rfl", "wine {exe} {args}");

        Assert.Equal("wine", p.FileName);
        Assert.Equal("/games/rf/RF.exe -level arena.rfl", p.Arguments);
    }

    [Fact]
    public void ComposeProcess_Quotes_Exe_Path_With_Spaces()
    {
        GameLauncher.LaunchProcess p = GameLauncher.ComposeProcess(
            "/home/user/.wine/drive_c/Red Faction/RF.exe", "-level arena.rfl", "wine {exe} {args}");

        Assert.Equal("wine", p.FileName);
        Assert.Equal("\"/home/user/.wine/drive_c/Red Faction/RF.exe\" -level arena.rfl", p.Arguments);
    }

    [Fact]
    public void GuessExe_Finds_Only_The_Alpine_Launcher()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ged_guess_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.Null(GameLauncher.GuessExe(temp));

            // A stock RF.exe is never auto-adopted — GED play-tests only through the Alpine launcher.
            File.WriteAllText(Path.Combine(temp, "RF.exe"), string.Empty);
            Assert.Null(GameLauncher.GuessExe(temp));

            File.WriteAllText(Path.Combine(temp, "AlpineFactionLauncher.exe"), string.Empty);
            Assert.Equal(Path.Combine(temp, "AlpineFactionLauncher.exe"), GameLauncher.GuessExe(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
