using System.IO;
using Ged.App;
using Xunit;

namespace Ged.App.Tests;

/// <summary>Q4 — the "Undo application" (Instant / Replay) setting plumbing: default + persistence.</summary>
public sealed class UndoReplaySettingTests
{
    [Fact]
    public void Default_Is_Instant()
    {
        // Default preserves the current behaviour: an Instant, single-rebuild history jump.
        Assert.False(new AppSettings().UndoReplay);
    }

    [Fact]
    public void UndoReplay_Round_Trips_Through_The_Settings_File()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ged-undoreplay-{System.Guid.NewGuid():N}.cfg");
        try
        {
            var settings = new AppSettings { UndoReplay = true };
            SettingsStore.Save(settings, path);
            AppSettings loaded = SettingsStore.Load(path);
            Assert.True(loaded.UndoReplay);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
