using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ged.App.Controls;
using Ged.App.Services;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Feature 1: the bottom-right toast host. Cards are added, coalesce identical messages into a "×N"
/// counter, cap the simultaneous count (oldest collapses), and auto-dismiss / expire. Expiry is driven
/// deterministically through the injectable <see cref="ToastHost.Clock"/> + <c>PruneExpired</c> so the
/// tests never depend on wall-clock timers. The host layer itself is input-transparent (no background),
/// so only the cards are hit-testable — verified structurally here.
/// </summary>
public sealed class ToastHostTests
{
    private static ToastHost Mount()
    {
        var host = new ToastHost();
        var win = new Window { Content = host };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return host;
    }

    [AvaloniaFact]
    public void Show_Adds_A_Card()
    {
        ToastHost host = Mount();
        Assert.Equal(0, host.ActiveCardCount);

        host.Show(NotificationSeverity.Info, "Hello");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, host.ActiveCardCount);
        Assert.Equal("Hello", host.ActiveToasts[0].Message);
        Assert.Equal(1, host.ActiveToasts[0].Count);
    }

    [AvaloniaFact]
    public void Identical_Message_Coalesces_Into_A_Count_Instead_Of_A_New_Card()
    {
        ToastHost host = Mount();

        host.Show(NotificationSeverity.Warning, "Build failed: boom");
        host.Show(NotificationSeverity.Warning, "Build failed: boom");
        host.Show(NotificationSeverity.Warning, "Build failed: boom");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, host.ActiveCardCount); // one card, not three
        Assert.Equal(3, host.ActiveToasts[0].Count); // ×3
    }

    [AvaloniaFact]
    public void Different_Severity_Or_Text_Does_Not_Coalesce()
    {
        ToastHost host = Mount();

        host.Show(NotificationSeverity.Info, "same");
        host.Show(NotificationSeverity.Warning, "same"); // different severity → separate card
        host.Show(NotificationSeverity.Info, "other");   // different text → separate card
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, host.ActiveCardCount);
        Assert.All(host.ActiveToasts, t => Assert.Equal(1, t.Count));
    }

    [AvaloniaFact]
    public void Cap_Collapses_The_Oldest_Card()
    {
        ToastHost host = Mount();

        for (int i = 0; i < ToastHost.MaxCards + 2; i++)
        {
            host.Show(NotificationSeverity.Info, $"msg {i}");
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ToastHost.MaxCards, host.ActiveCardCount);
        // The two oldest ("msg 0", "msg 1") collapsed; the newest survive.
        Assert.DoesNotContain(host.ActiveToasts, t => t.Message == "msg 0");
        Assert.DoesNotContain(host.ActiveToasts, t => t.Message == "msg 1");
        Assert.Contains(host.ActiveToasts, t => t.Message == $"msg {ToastHost.MaxCards + 1}");
    }

    [AvaloniaFact]
    public void Cards_Auto_Expire_After_Their_Duration()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ToastHost host = Mount();
        host.Clock = () => now;

        host.Show(NotificationSeverity.Info, "info"); // ~4 s
        host.Show(NotificationSeverity.Error, "error"); // ~8 s (errors linger)
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, host.ActiveCardCount);

        // 5 s later: the info card has expired, the error card has not.
        now = now.AddSeconds(5);
        host.PruneExpired();
        Assert.Equal(1, host.ActiveCardCount);
        Assert.Equal("error", host.ActiveToasts[0].Message);

        // 10 s in: the error card expires too.
        now = now.AddSeconds(5);
        host.PruneExpired();
        Assert.Equal(0, host.ActiveCardCount);
    }

    [AvaloniaFact]
    public void Coalescing_Refreshes_The_Expiry_Timer()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ToastHost host = Mount();
        host.Clock = () => now;

        host.Show(NotificationSeverity.Info, "tick"); // expires at +4 s
        now = now.AddSeconds(3);
        host.Show(NotificationSeverity.Info, "tick"); // coalesces, resets expiry to +3+4 = +7 s

        now = now.AddSeconds(2); // +5 s total — past the ORIGINAL expiry, but not the refreshed one
        host.PruneExpired();
        Assert.Equal(1, host.ActiveCardCount);
        Assert.Equal(2, host.ActiveToasts[0].Count);
    }

    [AvaloniaFact]
    public void Clear_Dismisses_Every_Card()
    {
        ToastHost host = Mount();
        host.Show(NotificationSeverity.Info, "a");
        host.Show(NotificationSeverity.Warning, "b");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, host.ActiveCardCount);

        host.Clear();
        Assert.Equal(0, host.ActiveCardCount);
    }

    [AvaloniaFact]
    public void Host_Surface_Is_Input_Transparent_But_Present()
    {
        ToastHost host = Mount();
        // The overlay layer itself has no background, so it never intercepts a click — only the cards
        // (which have a background) are hit-testable. It is not hit-test-DISABLED (the cards must be
        // clickable to dismiss), it simply has no fill of its own.
        Assert.Null(host.Background);
        Assert.True(host.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void Visible_Card_Presence_Tracks_The_Stack_And_Signals_The_Host()
    {
        // The shell rehosts this stack in a native popup over a maximized viewport pane and opens it
        // from VisualCardsChanged while HasVisibleCards is true. Adding a card is synchronous, so both
        // are asserted deterministically here (the close-after-fade path is timer-driven and left to
        // the running app).
        ToastHost host = Mount();
        Assert.False(host.HasVisibleCards);

        int signals = 0;
        host.VisualCardsChanged += () => signals++;

        host.Show(NotificationSeverity.Info, "hello");
        Dispatcher.UIThread.RunJobs();

        Assert.True(host.HasVisibleCards);
        Assert.True(signals >= 1);
    }
}
