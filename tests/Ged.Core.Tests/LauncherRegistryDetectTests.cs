using System;
using System.IO;
using Ged.Core.Playtest;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Auto-detecting AlpineFactionLauncher.exe from the Windows af:// protocol registration
/// (item 6). The command-string parse is pure and unit-tested with fakes; the live registry
/// read sits behind <see cref="IAlpineProtocolReader"/> so detection resolves deterministically
/// in tests, and is probed BEFORE the beside-the-install filesystem fallback.
/// </summary>
public sealed class LauncherRegistryDetectTests : IDisposable
{
    private readonly string _dir;

    public LauncherRegistryDetectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ged-afreg-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeReader : IAlpineProtocolReader
    {
        private readonly string? _command;

        public FakeReader(string? command) => _command = command;

        public string? ReadAfShellOpenCommand() => _command;
    }

    [Theory]
    [InlineData("\"C:\\Games\\Alpine\\AlpineFactionLauncher.exe\" \"%1\"", @"C:\Games\Alpine\AlpineFactionLauncher.exe")]
    [InlineData("\"C:\\Games\\Alpine\\AlpineFactionLauncher.exe\" -uri \"%1\"", @"C:\Games\Alpine\AlpineFactionLauncher.exe")]
    [InlineData("\"C:\\Program Files\\Alpine Faction\\AlpineFactionLauncher.exe\" \"%1\"", @"C:\Program Files\Alpine Faction\AlpineFactionLauncher.exe")]
    [InlineData(@"C:\Games\Alpine\Launcher.exe %1", @"C:\Games\Alpine\Launcher.exe")]
    [InlineData(@"C:\Games\Alpine\Launcher.exe", @"C:\Games\Alpine\Launcher.exe")]
    public void ParseLauncherPath_Extracts_The_Exe(string command, string expected)
    {
        Assert.Equal(expected, GameLauncher.ParseLauncherPath(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\unterminated.exe %1")] // an unterminated quote is malformed
    public void ParseLauncherPath_Returns_Null_For_Blank_Or_Malformed(string? command)
    {
        Assert.Null(GameLauncher.ParseLauncherPath(command));
    }

    [Fact]
    public void DetectFromRegistry_Returns_The_Exe_Only_When_It_Exists()
    {
        string launcher = Touch("AlpineFactionLauncher.exe");
        var present = new FakeReader($"\"{launcher}\" \"%1\"");
        Assert.Equal(launcher, GameLauncher.DetectFromRegistry(present));

        // Parses fine, but the file is gone → no detection.
        var missing = new FakeReader($"\"{Path.Combine(_dir, "gone.exe")}\" \"%1\"");
        Assert.Null(GameLauncher.DetectFromRegistry(missing));

        Assert.Null(GameLauncher.DetectFromRegistry(new FakeReader(null)));
    }

    [Fact]
    public void GuessExe_Prefers_The_Registry_Over_The_Beside_Install_Probe()
    {
        // A launcher beside the install…
        string beside = Touch("AlpineFactionLauncher.exe");

        // …and a different launcher registered via af:// in its own dir.
        string regDir = Path.Combine(_dir, "reg");
        Directory.CreateDirectory(regDir);
        string registered = Path.Combine(regDir, "AlpineFactionLauncher.exe");
        File.WriteAllText(registered, string.Empty);

        var reader = new FakeReader($"\"{registered}\" \"%1\"");

        // Registry wins (item 6: probed before the beside-the-install check).
        Assert.Equal(registered, GameLauncher.GuessExe(_dir, reader));

        // With no reader it falls back to the beside-the-install launcher.
        Assert.Equal(beside, GameLauncher.GuessExe(_dir, null));
        Assert.Equal(beside, GameLauncher.GuessExe(_dir));
    }

    [Fact]
    public void GuessExe_Falls_Back_To_Filesystem_When_Registry_Absent()
    {
        string beside = Touch("AlpineFactionLauncher.exe");
        var noKey = new FakeReader(null);

        Assert.Equal(beside, GameLauncher.GuessExe(_dir, noKey));
    }

    [Fact]
    public void GuessExe_Finds_Registry_Launcher_Even_With_No_Install_Dir()
    {
        string registered = Touch("AlpineFactionLauncher.exe");
        var reader = new FakeReader($"\"{registered}\" \"%1\"");

        // The wizard prefill path: no install dir set, but the af:// registration locates it.
        Assert.Equal(registered, GameLauncher.GuessExe(null, reader));
    }
}
