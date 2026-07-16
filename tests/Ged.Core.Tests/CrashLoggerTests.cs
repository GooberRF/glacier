using System;
using System.IO;
using Ged.Core.Diagnostics;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the crash logger + emergency-save path (crash hardening): a
/// simulated exception produces a crash log carrying the version, open file and
/// exception detail; non-fatal failures append to a session log; and the
/// emergency-save target is the recoverable autosave path.
/// </summary>
public sealed class CrashLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ged-crash-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteCrashLog_Captures_Exception_Version_And_OpenFile()
    {
        var logger = new CrashLogger(_dir);
        Exception ex;
        try
        {
            throw new InvalidOperationException("simulated boom");
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        string? path = logger.WriteCrashLog(ex, "1.0.0+abc1234", @"C:\maps\dm01.rfl");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith("crash-", Path.GetFileName(path));

        string text = File.ReadAllText(path!);
        Assert.Contains("simulated boom", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("1.0.0+abc1234", text);
        Assert.Contains("dm01.rfl", text);
    }

    [Fact]
    public void WriteCrashLog_Includes_Inner_Exception()
    {
        var logger = new CrashLogger(_dir);
        var ex = new Exception("outer", new FormatException("inner cause"));
        string? path = logger.WriteCrashLog(ex, "1.0.0", null);
        string text = File.ReadAllText(path!);
        Assert.Contains("outer", text);
        Assert.Contains("inner cause", text);
        Assert.Contains("(none)", text); // no open file
    }

    [Fact]
    public void LogNonFatal_Appends_To_Session_Log()
    {
        var logger = new CrashLogger(_dir);
        logger.LogNonFatal("thumbnail", new Exception("render failed"));
        logger.LogNonFatal("build", new InvalidOperationException("compile failed"));

        string session = Path.Combine(_dir, "session.log");
        Assert.True(File.Exists(session));
        string[] lines = File.ReadAllLines(session);
        Assert.Equal(2, lines.Length);
        Assert.Contains("thumbnail", lines[0]);
        Assert.Contains("render failed", lines[0]);
        Assert.Contains("compile failed", lines[1]);
    }

    [Fact]
    public void EmergencySavePath_Uses_Recoverable_Autosave_For_Saved_Level()
    {
        // A saved level writes next to itself so the existing recovery prompt finds it.
        string saved = CrashLogger.EmergencySavePath(@"C:\maps\dm01.rfl", _dir);
        Assert.Equal(@"C:\maps\dm01.rfl.autosave.rfl", saved);

        // An unsaved level lands in the recovery dir, timestamped, not lost.
        string untitled = CrashLogger.EmergencySavePath(null, _dir);
        Assert.StartsWith(_dir, untitled);
        Assert.EndsWith(".autosave.rfl", untitled);
        Assert.Contains("untitled-", untitled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
