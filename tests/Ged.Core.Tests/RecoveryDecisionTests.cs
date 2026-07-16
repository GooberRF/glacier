using System;
using Ged.Core.Editing;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 18: the recovery-dialog view-model outcomes — which file loads, whether the autosave
/// is retained or deleted, and that Save always targets the ORIGINAL path.
/// </summary>
public sealed class RecoveryDecisionTests
{
    private const string Original = @"C:\levels\dm01.rfl";
    private const string Autosave = @"C:\levels\dm01.rfl.autosave.rfl";

    [Fact]
    public void OpenAutosave_Loads_Autosave_Targets_Original_And_Deletes_On_Save()
    {
        RecoveryOutcome o = RecoveryDecision.Resolve(Original, Autosave, RecoveryChoice.OpenAutosave);
        Assert.Equal(Autosave, o.LoadPath);
        Assert.Equal(Original, o.SaveTargetPath); // save writes the original
        Assert.False(o.DeleteAutosaveNow);         // kept until a successful save
        Assert.True(o.DeleteAutosaveOnSave);
    }

    [Fact]
    public void OpenOriginal_Loads_Original_And_Keeps_The_Autosave()
    {
        RecoveryOutcome o = RecoveryDecision.Resolve(Original, Autosave, RecoveryChoice.OpenOriginal);
        Assert.Equal(Original, o.LoadPath);
        Assert.Equal(Original, o.SaveTargetPath);
        Assert.False(o.DeleteAutosaveNow);
        Assert.False(o.DeleteAutosaveOnSave); // autosave stays on disk
    }

    [Fact]
    public void DeleteAutosave_Loads_Original_And_Deletes_The_Autosave_Now()
    {
        RecoveryOutcome o = RecoveryDecision.Resolve(Original, Autosave, RecoveryChoice.DeleteAutosaveAndOpenOriginal);
        Assert.Equal(Original, o.LoadPath);
        Assert.Equal(Original, o.SaveTargetPath);
        Assert.True(o.DeleteAutosaveNow);
        Assert.False(o.DeleteAutosaveOnSave);
    }

    [Theory]
    [InlineData(0, 12, 0, "12 minutes newer")]
    [InlineData(0, 1, 0, "1 minute newer")]
    [InlineData(2, 0, 0, "2 hours newer")]
    [InlineData(0, 0, 30, "30 seconds newer")]
    public void Age_Difference_Reads_Naturally(int hours, int minutes, int seconds, string expectedSuffix)
    {
        var original = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime autosave = original.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);
        Assert.EndsWith(expectedSuffix, RecoveryDecision.DescribeAgeDifference(original, autosave));
    }

    [Fact]
    public void Age_Difference_Handles_Non_Newer_Autosave()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Contains("same age or older", RecoveryDecision.DescribeAgeDifference(t, t));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1572864, "1.5 MB")]
    public void Size_Formats_Readably(long bytes, string expected)
    {
        Assert.Equal(expected, RecoveryDecision.DescribeSize(bytes));
    }
}
