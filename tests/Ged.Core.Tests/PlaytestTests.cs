using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using Ged.Core.Playtest;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for playtest launch: exe-kind detection by filename, the staging
/// destination directory by mode, argument construction per exe kind (-level vs
/// -levelm), the RED-authentic <c>-startpos</c>/<c>-startdir</c> from-camera switches,
/// the stock-can't-do-multi guard, and file staging into a temp dir (never a real install).
/// </summary>
public sealed class PlaytestTests
{
    // A camera pose reused across the from-camera argument gates. Eye in the document's
    // own left-handed world units; forward is already a unit vector.
    private static readonly Vector3 Eye = new(12f, 4.5f, 30.25f);
    private static readonly Vector3 Forward = new(0f, 0f, 1f);

    // The exact encodings of Eye / Forward (sign,magnitude pairs joined by ';').
    private const string EyeEncoded = "1,12.00;1,4.50;1,30.25";
    private const string ForwardEncoded = "0,0.00;0,0.00;1,1.00";

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

    // ---- From-camera: RED's real -startpos / -startdir spawn-override switches ----

    [Fact]
    public void Plain_Play_Emits_No_Startpos_Or_Startdir_Switch()
    {
        // Shape 1: plain Play Level (single). Shape 2: plain Play in Multi. Neither emits
        // a position switch — matches RED's plain-play handler FUN_004478b0.
        PlaytestCommand single = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: false, cameraEye: Eye, cameraForward: Forward);
        Assert.Equal("-level arena.rfl", single.Arguments);

        PlaytestCommand multi = GameLauncher.BuildCommand(
            @"C:\RF\AlpineFactionLauncher.exe", @"C:\RF", "ctf-tower.rfl", PlaytestMode.Multi, fromCamera: false, cameraEye: Eye, cameraForward: Forward);
        Assert.Equal("-levelm ctf-tower.rfl", multi.Arguments);
    }

    [Fact]
    public void BuildCommand_Single_From_Camera_Appends_Startpos_And_Startdir()
    {
        // Shape 3: -level "<name>" -startpos <eye> -startdir <forward>. Works the same for
        // stock RF and the Alpine launcher (single player); only the level flag differs.
        PlaytestCommand stock = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: true, cameraEye: Eye, cameraForward: Forward);
        Assert.Equal($"-level arena.rfl -startpos {EyeEncoded} -startdir {ForwardEncoded}", stock.Arguments);

        PlaytestCommand alpine = GameLauncher.BuildCommand(
            @"C:\RF\AlpineFactionLauncher.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: true, cameraEye: Eye, cameraForward: Forward);
        Assert.Equal($"-level arena.rfl -startpos {EyeEncoded} -startdir {ForwardEncoded}", alpine.Arguments);
    }

    [Fact]
    public void BuildCommand_Multi_From_Camera_Appends_The_Same_Switches_After_Levelm()
    {
        // Shape 4: multi from-camera is identical to single from-camera except for -levelm
        // (Alpine launcher). The staged copy still carries the switches via the command line,
        // never a relocated spawn.
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\AlpineFactionLauncher.exe", @"C:\RF", "ctf-tower.rfl", PlaytestMode.Multi, fromCamera: true, cameraEye: Eye, cameraForward: Forward);

        Assert.Equal($"-levelm ctf-tower.rfl -startpos {EyeEncoded} -startdir {ForwardEncoded}", cmd.Arguments);
        Assert.True(cmd.FromCamera);
        Assert.Equal(Path.Combine(@"C:\RF", "user_maps", "multi", "ctf-tower.rfl"), cmd.DestinationRflPath);
    }

    [Fact]
    public void BuildCommand_From_Camera_With_ExtraArgs_Orders_Switches_After_ExtraArgs()
    {
        // RED appends -startpos/-startdir after the optional -mod/-notnl block; extraArgs is
        // GED's equivalent slot, so the switches come last.
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: true, extraArgs: "-windowed",
            cameraEye: Eye, cameraForward: Forward);

        Assert.Equal($"-level arena.rfl -windowed -startpos {EyeEncoded} -startdir {ForwardEncoded}", cmd.Arguments);
    }

    [Fact]
    public void BuildCommand_From_Camera_Encodes_A_Negative_Component_As_Sign_Zero()
    {
        // RED's per-component encoding: sign = c>0?1:0, magnitude = |c| at two decimals.
        // -3.2 → "0,3.20" (the spec's negative-component case).
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: true,
            cameraEye: new Vector3(-3.2f, 4.5f, 30.25f), cameraForward: Forward);

        Assert.Equal($"-level arena.rfl -startpos 0,3.20;1,4.50;1,30.25 -startdir {ForwardEncoded}", cmd.Arguments);
    }

    [Fact]
    public void BuildCommand_From_Camera_Normalizes_The_Forward_Vector()
    {
        // -startdir is a UNIT vector (RED sends the normalized forward row). A (3,0,4) forward
        // normalizes to (0.6, 0, 0.8).
        PlaytestCommand cmd = GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: true,
            cameraEye: Eye, cameraForward: new Vector3(3f, 0f, 4f));

        Assert.Equal($"-level arena.rfl -startpos {EyeEncoded} -startdir 1,0.60;0,0.00;1,0.80", cmd.Arguments);
    }

    [Fact]
    public void BuildCommand_From_Camera_Uses_Invariant_Culture_Under_A_Comma_Decimal_Culture()
    {
        // The magnitude is formatted with the invariant '.' decimal (RED's printf %0.2f),
        // never the current culture's ',' — verified by building under de-DE.
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            PlaytestCommand cmd = GameLauncher.BuildCommand(
                @"C:\RF\RF.exe", @"C:\RF", "arena.rfl", PlaytestMode.Single, fromCamera: true,
                cameraEye: Eye, cameraForward: Forward);

            Assert.Equal($"-level arena.rfl -startpos {EyeEncoded} -startdir {ForwardEncoded}", cmd.Arguments);
            Assert.DoesNotContain(",50", cmd.Arguments); // no comma-decimal leaked (e.g. "4,50")
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void EncodeVector_Encodes_Zero_As_Sign_Zero_Magnitude_Zero()
    {
        // Zero encodes as "0,0.00" (sign is strictly c>0, so zero and negatives are sign 0).
        Assert.Equal("0,0.00;0,0.00;0,0.00", GameLauncher.EncodeVector(Vector3.Zero));
    }

    [Fact]
    public void StageLevel_From_Camera_Is_A_Plain_Copy_And_Never_Modifies_The_Rfl()
    {
        // From-camera no longer mutates the staged bytes (the old Player-Start relocation is
        // gone). Staging is a byte-for-byte copy of the source .rfl in every mode: the staged
        // file equals the source exactly.
        string temp = Path.Combine(Path.GetTempPath(), "ged_playtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            PlaytestCommand cmd = GameLauncher.BuildCommand(
                Path.Combine(temp, "AlpineFactionLauncher.exe"), temp, "ctf-tower.rfl",
                PlaytestMode.Multi, fromCamera: true, cameraEye: Eye, cameraForward: Forward);

            byte[] sourceRfl = { 0xCA, 0x3E, 0x2A, 0x11, 0x00, 0xFF }; // stands in for the on-disk .rfl
            string written = GameLauncher.StageLevel(cmd, sourceRfl);

            Assert.Equal(Path.Combine(temp, "user_maps", "multi", "ctf-tower.rfl"), written);
            Assert.Equal(sourceRfl, File.ReadAllBytes(written)); // staged == source, byte-for-byte
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
    public void BuildCommand_Stock_Multi_From_Camera_Still_Refused()
    {
        // The stock exe can't do multi even with a camera pose — the -levelm guard fires
        // before any switch is appended.
        Assert.Throws<NotSupportedException>(() => GameLauncher.BuildCommand(
            @"C:\RF\RF.exe", @"C:\RF", "ctf-tower.rfl", PlaytestMode.Multi, fromCamera: true, cameraEye: Eye, cameraForward: Forward));
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
