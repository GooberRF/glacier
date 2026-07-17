using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Ged.App.Services;

namespace Ged.App.Controls;

/// <summary>
/// The bottom-right toast stack (VS Code style). Its OWN surface has no background, so it never
/// intercepts a pointer event — only the toast cards (which carry a background) are hit-testable.
/// Cards stack newest-nearest-the-corner, colour-code by severity, auto-dismiss (errors linger
/// longer), fade out, dismiss on click, coalesce identical messages into a "×N" counter, and cap the
/// simultaneous count (the oldest collapses). Theme-aware: each card resolves its palette from the
/// current light/dark variant when it is shown.
/// <para>
/// The shell rehosts this stack in a native top-level <c>Popup</c> anchored to the window's
/// content area so toasts stay visible over a maximized Direct3D 11 viewport pane (a native child
/// HWND that would otherwise own the corner) — see <c>MainWindow.BuildLayout</c>. This control stays
/// the bare card stack so it can be mounted and tested directly; it raises
/// <see cref="VisualCardsChanged"/> whenever a card enters or leaves the visual tree so the host can
/// open the popup while <see cref="HasVisibleCards"/> and close it once the stack empties. The popup
/// is non-activating and never takes focus, so a click still dismisses a card without pulling focus
/// off the main window.
/// </para>
/// </summary>
internal sealed class ToastHost : Grid
{
    /// <summary>Most cards shown at once; a new card past this collapses the oldest.</summary>
    internal const int MaxCards = 5;

    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private readonly StackPanel _stack;
    private readonly List<Toast> _toasts = new();
    private readonly DispatcherTimer _pruneTimer;

    public ToastHost()
    {
        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Children.Add(_stack);

        // Expiry is polled on a coarse timer (a per-card wall clock is unnecessary); tests drive
        // expiry deterministically through the injectable Clock + PruneExpired.
        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pruneTimer.Tick += (_, _) => PruneExpired();
        _pruneTimer.Start();
    }

    /// <summary>
    /// Raised whenever a toast card enters or leaves the visual stack (an add, or a removal once its
    /// fade-out completes). The popup host uses it to open the overlay while cards are present and
    /// close it after the last one fades — see <see cref="HasVisibleCards"/>.
    /// </summary>
    internal event Action? VisualCardsChanged;

    /// <summary>True while at least one toast card is in the visual tree (including one mid-fade-out).</summary>
    internal bool HasVisibleCards => _stack.Children.Count > 0;

    /// <summary>Clock used for auto-dismiss timing; overridable in tests for deterministic expiry.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Number of live toast cards (test hook).</summary>
    internal int ActiveCardCount => _toasts.Count;

    /// <summary>The live toasts as (severity, message, coalesced count) triples (test hook).</summary>
    internal IReadOnlyList<(NotificationSeverity Severity, string Message, int Count)> ActiveToasts =>
        _toasts.Select(t => (t.Severity, t.Message, t.Count)).ToList();

    /// <summary>
    /// Shows a toast. An identical (same severity + text) toast already on screen is refreshed in place
    /// — its dismiss timer resets and its "×N" counter increments — rather than stacking a duplicate.
    /// Otherwise a new card is added; if the cap is already reached, the oldest card collapses first.
    /// </summary>
    public void Show(NotificationSeverity severity, string message)
    {
        message ??= string.Empty;

        Toast? existing = _toasts.FirstOrDefault(t => t.Severity == severity && t.Message == message);
        if (existing is not null)
        {
            existing.Count++;
            existing.ExpiresAt = Clock() + DurationFor(severity);
            UpdateBadge(existing);
            return;
        }

        if (_toasts.Count >= MaxCards)
        {
            Dismiss(_toasts[0]); // collapse the oldest to make room
        }

        var toast = BuildToast(severity, message);
        toast.ExpiresAt = Clock() + DurationFor(severity);
        _toasts.Add(toast);
        _stack.Children.Add(toast.Card);
        VisualCardsChanged?.Invoke();
    }

    /// <summary>Dismisses every live toast (used on document swaps / teardown).</summary>
    internal void Clear()
    {
        foreach (Toast t in _toasts.ToList())
        {
            Dismiss(t);
        }
    }

    /// <summary>Collapses any toast whose dismiss time has passed (polled by the timer; called directly in tests).</summary>
    internal void PruneExpired()
    {
        DateTime now = Clock();
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            if (now >= _toasts[i].ExpiresAt)
            {
                Dismiss(_toasts[i]);
            }
        }
    }

    private static TimeSpan DurationFor(NotificationSeverity severity) =>
        severity == NotificationSeverity.Error ? ErrorDuration : DefaultDuration;

    private void Dismiss(Toast toast)
    {
        if (!_toasts.Remove(toast))
        {
            return;
        }

        // The card leaves the live set immediately (so ActiveCardCount is authoritative and a re-show
        // no longer coalesces onto it), then fades and is pulled from the visual tree once the fade
        // completes. A missed fade tick (e.g. headless) only leaves a transparent, already-dead card.
        toast.Card.Opacity = 0;
        var remove = new DispatcherTimer { Interval = FadeDuration };
        remove.Tick += (_, _) =>
        {
            remove.Stop();
            _stack.Children.Remove(toast.Card);
            VisualCardsChanged?.Invoke();
        };
        remove.Start();
    }

    private static void UpdateBadge(Toast toast)
    {
        toast.Badge.Text = "×" + toast.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        toast.Badge.IsVisible = toast.Count > 1;
    }

    private Toast BuildToast(NotificationSeverity severity, string message)
    {
        (Color accent, string glyph) = StyleFor(severity);
        bool light = (Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark) == ThemeVariant.Light;

        Color background = light ? Color.FromArgb(0xF5, 0xFB, 0xFB, 0xFC) : Color.FromArgb(0xF0, 0x28, 0x2B, 0x31);
        Color textColor = light ? Color.FromRgb(0x1A, 0x1C, 0x21) : Color.FromRgb(0xF2, 0xF3, 0xF5);
        Color borderColor = light ? Color.FromArgb(0x30, 0x00, 0x00, 0x00) : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF);

        var accentBar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(accent),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var label = new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(textColor),
        };

        var badge = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = false,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(accentBar);
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(badge);

        var card = new Border
        {
            MinWidth = 220,
            MaxWidth = 360,
            Padding = new Thickness(12, 9),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, OffsetY = 3, Color = Color.FromArgb(0x66, 0, 0, 0) }),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Click to dismiss",
            Child = row,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = FadeDuration },
            },
        };

        var toast = new Toast { Severity = severity, Message = message, Card = card, Badge = badge };
        card.PointerPressed += (_, _) => Dismiss(toast);
        return toast;
    }

    /// <summary>The accent colour + leading glyph for a severity (theme-independent).</summary>
    private static (Color Accent, string Glyph) StyleFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => (Color.FromRgb(0xE5, 0x48, 0x4D), "✕"),
        NotificationSeverity.Warning => (Color.FromRgb(0xF5, 0xA6, 0x23), "⚠"),
        NotificationSeverity.Info => (Color.FromRgb(0x3E, 0x8E, 0xDE), "ℹ"),
        NotificationSeverity.Hint => (Color.FromRgb(0x9A, 0xA0, 0xA6), "•"),
        _ => (Color.FromRgb(0x9A, 0xA0, 0xA6), "•"),
    };

    /// <summary>One live toast: its identity (severity + message), coalesced count, and visual.</summary>
    private sealed class Toast
    {
        public NotificationSeverity Severity { get; init; }

        public string Message { get; init; } = string.Empty;

        public int Count { get; set; } = 1;

        public DateTime ExpiresAt { get; set; }

        public Border Card { get; init; } = null!;

        public TextBlock Badge { get; init; } = null!;
    }
}
