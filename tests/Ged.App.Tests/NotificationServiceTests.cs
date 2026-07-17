using System.Collections.Generic;
using Ged.App.Services;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Feature 1: the unified notification layer. Every <see cref="NotificationService.Notify"/> always
/// reaches the status bar + Log, and additionally raises a toast only when the severity passes the
/// user's configured <see cref="ToastLevel"/>. These cover the full severity × level threshold matrix
/// and the always-status/always-log guarantee.
/// </summary>
public sealed class NotificationServiceTests
{
    // Expected toast for each (level, severity): Off toasts nothing; ErrorsOnly toasts errors;
    // Warnings adds warnings; Info adds info; Everything adds hints.
    [Theory]
    [InlineData(ToastLevel.Off, NotificationSeverity.Error, false)]
    [InlineData(ToastLevel.Off, NotificationSeverity.Warning, false)]
    [InlineData(ToastLevel.Off, NotificationSeverity.Info, false)]
    [InlineData(ToastLevel.Off, NotificationSeverity.Hint, false)]
    [InlineData(ToastLevel.ErrorsOnly, NotificationSeverity.Error, true)]
    [InlineData(ToastLevel.ErrorsOnly, NotificationSeverity.Warning, false)]
    [InlineData(ToastLevel.ErrorsOnly, NotificationSeverity.Info, false)]
    [InlineData(ToastLevel.ErrorsOnly, NotificationSeverity.Hint, false)]
    [InlineData(ToastLevel.Warnings, NotificationSeverity.Error, true)]
    [InlineData(ToastLevel.Warnings, NotificationSeverity.Warning, true)]
    [InlineData(ToastLevel.Warnings, NotificationSeverity.Info, false)]
    [InlineData(ToastLevel.Warnings, NotificationSeverity.Hint, false)]
    [InlineData(ToastLevel.Info, NotificationSeverity.Error, true)]
    [InlineData(ToastLevel.Info, NotificationSeverity.Warning, true)]
    [InlineData(ToastLevel.Info, NotificationSeverity.Info, true)]
    [InlineData(ToastLevel.Info, NotificationSeverity.Hint, false)]
    [InlineData(ToastLevel.Everything, NotificationSeverity.Error, true)]
    [InlineData(ToastLevel.Everything, NotificationSeverity.Warning, true)]
    [InlineData(ToastLevel.Everything, NotificationSeverity.Info, true)]
    [InlineData(ToastLevel.Everything, NotificationSeverity.Hint, true)]
    public void ShouldToast_Matches_The_Threshold_Matrix(ToastLevel level, NotificationSeverity severity, bool expected)
    {
        Assert.Equal(expected, NotificationService.ShouldToast(severity, level));
    }

    [Fact]
    public void Default_Level_Is_Info()
    {
        // The persisted default (AppSettings.ToastLevel) is Info: errors/warnings/info toast, hints do not.
        Assert.Equal(3, (int)ToastLevel.Info);
        var settings = new AppSettings();
        Assert.Equal((int)ToastLevel.Info, settings.ToastLevel);
    }

    [Fact]
    public void Notify_Always_Hits_Status_And_Log_Regardless_Of_Level()
    {
        var status = new List<(NotificationSeverity, string)>();
        var log = new List<(NotificationSeverity, string)>();
        var toast = new List<(NotificationSeverity, string)>();
        var svc = new NotificationService(() => ToastLevel.Off, // toasts suppressed
            (s, m) => status.Add((s, m)), (s, m) => log.Add((s, m)), (s, m) => toast.Add((s, m)));

        svc.Notify(NotificationSeverity.Error, "boom");
        svc.Notify(NotificationSeverity.Hint, "tip");

        Assert.Equal(2, status.Count);
        Assert.Equal(2, log.Count);
        Assert.Empty(toast); // Off → nothing toasts, but status + log still fired
        Assert.Contains((NotificationSeverity.Error, "boom"), status);
        Assert.Contains((NotificationSeverity.Hint, "tip"), log);
    }

    [Fact]
    public void Notify_Toasts_Only_When_The_Severity_Passes_The_Level()
    {
        ToastLevel level = ToastLevel.Warnings;
        var toast = new List<(NotificationSeverity, string)>();
        var svc = new NotificationService(() => level,
            (_, _) => { }, (_, _) => { }, (s, m) => toast.Add((s, m)));

        svc.Notify(NotificationSeverity.Error, "err");   // toasts (>= Warnings)
        svc.Notify(NotificationSeverity.Warning, "warn"); // toasts
        svc.Notify(NotificationSeverity.Info, "info");   // does NOT toast at Warnings
        svc.Notify(NotificationSeverity.Hint, "hint");   // does NOT toast

        Assert.Equal(2, toast.Count);
        Assert.Contains((NotificationSeverity.Error, "err"), toast);
        Assert.Contains((NotificationSeverity.Warning, "warn"), toast);
        Assert.DoesNotContain((NotificationSeverity.Info, "info"), toast);
    }

    [Fact]
    public void Level_Is_Read_Live_On_Each_Notify()
    {
        ToastLevel level = ToastLevel.Off;
        int toasts = 0;
        var svc = new NotificationService(() => level, (_, _) => { }, (_, _) => { }, (_, _) => toasts++);

        svc.Notify(NotificationSeverity.Error, "a");
        Assert.Equal(0, toasts); // Off

        level = ToastLevel.ErrorsOnly; // user raises the threshold
        svc.Notify(NotificationSeverity.Error, "b");
        Assert.Equal(1, toasts); // now toasts
    }

    [Theory]
    [InlineData(NotificationSeverity.Error, "Error")]
    [InlineData(NotificationSeverity.Warning, "Warning")]
    [InlineData(NotificationSeverity.Info, "Info")]
    [InlineData(NotificationSeverity.Hint, "Hint")]
    public void Tag_Maps_Severity_To_A_Log_Tag(NotificationSeverity severity, string expected)
    {
        Assert.Equal(expected, NotificationService.Tag(severity));
    }
}
