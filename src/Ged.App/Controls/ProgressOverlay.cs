using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ged.App.Services;

namespace Ged.App.Controls;

/// <summary>
/// A small, unobtrusive progress overlay pinned to the bottom-right of the viewport area
/// (item 3). It shows one card per in-flight <see cref="OperationProgress"/> — the operation
/// name plus a determinate bar (with count) or an indeterminate spinner-bar — stacking when
/// several operations overlap and hiding entirely when none are running. It never takes focus
/// or blocks viewport input (<see cref="InputElement.IsHitTestVisible"/> is false), so it only
/// ever informs.
/// </summary>
internal sealed class ProgressOverlay : UserControl
{
    private readonly OperationProgressService _service;
    private readonly StackPanel _stack = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 14, 14),
    };

    private readonly Dictionary<OperationProgress, Card> _cards = new();
    private bool _syncQueued;

    public ProgressOverlay(OperationProgressService service)
    {
        _service = service;
        IsHitTestVisible = false; // informational only — never steals focus / blocks input
        Content = _stack;
        IsVisible = false;
        _service.Changed += OnChanged;
        Sync();
    }

    private void OnChanged()
    {
        // On the UI thread (the common case — progress is reported via Dispatcher.Post) sync now,
        // so the overlay tracks operations immediately. Off-thread reports coalesce into one Post.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Sync();
            return;
        }

        if (_syncQueued)
        {
            return;
        }

        _syncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncQueued = false;
            Sync();
        }, DispatcherPriority.Background);
    }

    /// <summary>Number of operation cards currently shown (test hook).</summary>
    internal int ActiveCardCount => _cards.Count;

    private void Sync()
    {
        IReadOnlyList<OperationProgress> ops = _service.Operations;

        // Drop cards whose operation finished.
        var live = new HashSet<OperationProgress>(ops);
        var stale = new List<OperationProgress>();
        foreach (KeyValuePair<OperationProgress, Card> kv in _cards)
        {
            if (!live.Contains(kv.Key))
            {
                stale.Add(kv.Key);
            }
        }

        foreach (OperationProgress op in stale)
        {
            _stack.Children.Remove(_cards[op].Root);
            _cards.Remove(op);
        }

        // Add / update cards, preserving stack order.
        foreach (OperationProgress op in ops)
        {
            if (!_cards.TryGetValue(op, out Card? card))
            {
                card = new Card();
                _cards[op] = card;
                _stack.Children.Add(card.Root);
            }

            card.Update(op);
        }

        IsVisible = ops.Count > 0;
    }

    /// <summary>The visual for one operation card (title + progress bar + detail).</summary>
    private sealed class Card
    {
        private readonly TextBlock _title = new()
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)),
        };

        private readonly TextBlock _detail = new()
        {
            FontSize = 11,
            Opacity = 0.8,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        private readonly ProgressBar _bar = new()
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
        };

        public Card()
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 5 };
            panel.Children.Add(_title);
            panel.Children.Add(_bar);
            panel.Children.Add(_detail);

            Root = new Border
            {
                MinWidth = 190,
                MaxWidth = 300,
                Padding = new Thickness(11, 8),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x20, 0x25)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 2, Color = Color.FromArgb(0x60, 0, 0, 0) }),
                Child = panel,
            };
        }

        public Border Root { get; }

        public void Update(OperationProgress op)
        {
            _title.Text = op.Name;

            if (op.Fraction is { } f)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = f;
            }
            else
            {
                _bar.IsIndeterminate = true;
            }

            _detail.Text = op.Detail ?? (op.Fraction is null ? "Working…" : string.Empty);
            _detail.IsVisible = !string.IsNullOrEmpty(_detail.Text);
        }
    }
}
