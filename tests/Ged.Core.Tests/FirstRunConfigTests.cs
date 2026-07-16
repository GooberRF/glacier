using System;
using System.IO;
using Ged.Core.Playtest;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The first-run wizard's install + game-executable model: choosing an RF directory
/// auto-guesses the Alpine Faction launcher (AlpineFactionLauncher.exe only — a stock
/// RF.exe is never auto-adopted); an explicit pick wins and survives later install-dir
/// changes.
/// </summary>
public class FirstRunConfigTests : IDisposable
{
    private readonly string _dir;

    public FirstRunConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ged-firstrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Touch(string name)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, string.Empty);
        return p;
    }

    [Fact]
    public void Install_Dir_Guesses_The_Alpine_Launcher_When_Present()
    {
        string launcher = Touch("AlpineFactionLauncher.exe");
        Touch("RF.exe"); // the launcher must win over stock RF.exe

        var cfg = new FirstRunConfig();
        cfg.SetInstallDir(_dir);

        Assert.Equal(_dir, cfg.RfInstallDir);
        Assert.Equal(launcher, cfg.GameExePath);
        Assert.False(cfg.GameExePickedManually);
    }

    [Fact]
    public void Install_Dir_With_Only_Stock_Rf_Leaves_Exe_Unset()
    {
        // GED play-tests exclusively through AlpineFactionLauncher.exe; a stock RF.exe
        // beside the install is not auto-adopted, so the guess stays null.
        Touch("RF.exe");

        var cfg = new FirstRunConfig(_dir);

        Assert.Null(cfg.GameExePath);
    }

    [Fact]
    public void No_Executable_In_Dir_Leaves_Exe_Unset()
    {
        var cfg = new FirstRunConfig(_dir);
        Assert.Null(cfg.GameExePath);

        cfg.SetInstallDir(null);
        Assert.Null(cfg.RfInstallDir);
        Assert.Null(cfg.GameExePath);
    }

    [Fact]
    public void Manual_Pick_Wins_And_Survives_An_Install_Dir_Change()
    {
        Touch("RF.exe");
        string custom = Touch("MyAlpine.exe");

        var cfg = new FirstRunConfig(_dir); // no launcher present → no guess
        cfg.SetGameExe(custom);

        Assert.Equal(custom, cfg.GameExePath);
        Assert.True(cfg.GameExePickedManually);

        // Changing the install dir must NOT clobber the manual pick with a re-guess.
        string other = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "AlpineFactionLauncher.exe"), string.Empty);
        cfg.SetInstallDir(other);

        Assert.Equal(other, cfg.RfInstallDir);
        Assert.Equal(custom, cfg.GameExePath); // still the manual pick
    }

    [Fact]
    public void Preconfigured_Exe_Is_Treated_As_A_Manual_Pick()
    {
        Touch("RF.exe");
        var cfg = new FirstRunConfig(_dir, initialGameExe: @"C:\games\AlpineFactionLauncher.exe");

        // The already-configured exe is kept, not overwritten by the install-dir guess.
        Assert.Equal(@"C:\games\AlpineFactionLauncher.exe", cfg.GameExePath);
        Assert.True(cfg.GameExePickedManually);
    }
}
