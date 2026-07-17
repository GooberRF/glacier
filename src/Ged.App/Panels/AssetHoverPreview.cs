using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ged.App.Panels;

/// <summary>
/// The Asset Browser's floating large-preview popover (item D): hovering a tile pops a larger
/// (<see cref="PreviewSize"/>px) view of that asset — a rendered mesh, a texture, or an enlarged
/// prefab thumbnail — beside the tile after a short dwell (<see cref="Delay"/>), so a quick scan
/// past tiles does not flash popups. It reuses ONE <see cref="Popup"/> (a real popup window, so it
/// is airspace-safe over the native viewport panes) and never takes keyboard focus. Moving between
/// adjacent tiles snaps the SAME popup to the new tile (no churn / no flicker); a short close delay
/// bridges the gap between tiles so passing over the margin between them does not close it. The
/// content is filled by a caller-supplied render callback, so mesh / texture / prefab previews all
/// share this one mechanism.
/// </summary>
internal sealed class AssetHoverPreview
{
    /// <summary>Edge length of the popped preview, in pixels.</summary>
    public const int PreviewSize = 320;

    /// <summary>Hover dwell before the popover opens (so scanning past tiles does not flash it).</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);

    private readonly Popup _popup;
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private Control? _pendingAnchor;
    private Action<Image>? _pendingRender;
    private Image? _image;
    private bool _showing;

    public AssetHoverPreview()
    {
        _popup = new Popup
        {
            Placement = PlacementMode.RightEdgeAlignedTop,
            HorizontalOffset = 8,
            IsLightDismissEnabled = false, // driven from pointer enter/leave, not a dismiss overlay
            Focusable = false,             // must never pull focus off the browser
        };

        _openTimer = new DispatcherTimer { Interval = Delay };
        _openTimer.Tick += (_, _) => ShowNow();

        // A short close delay bridges the gap between adjacent tiles: leaving tile A starts it,
        // entering tile B cancels it, so movement across the inter-tile margin never closes/flickers.
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); CloseNow(); };
    }

    /// <summary>The popup control the panel adds to its own visual tree so it can open.</summary>
    public Popup Popup => _popup;

    /// <summary>True while the popover is open (introspection / tests).</summary>
    public bool IsShowing => _showing;

    /// <summary>True while a show is scheduled but the dwell delay has not yet elapsed (tests).</summary>
    public bool HasPendingShow => _openTimer.IsEnabled;

    /// <summary>
    /// Schedules (or, if already open, immediately moves) the preview to <paramref name="anchor"/>,
    /// filling its image via <paramref name="render"/>.
    /// </summary>
    public void Schedule(Control anchor, Action<Image> render)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        _closeTimer.Stop();
        _pendingAnchor = anchor;
        _pendingRender = render;
        if (_showing)
        {
            ShowNow(); // already open: snap to the new tile with no close (no window churn / flicker)
            return;
        }

        _openTimer.Stop();
        _openTimer.Start();
    }

    /// <summary>Starts the short close delay (the pointer left a tile); a new <see cref="Schedule"/> cancels it.</summary>
    public void ScheduleClose()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    /// <summary>Closes immediately and drops any pending show (e.g. a tab switch or grid rebuild).</summary>
    public void Cancel() => CloseNow();

    private void ShowNow()
    {
        _openTimer.Stop();
        if (_pendingAnchor is null || _pendingRender is null)
        {
            return;
        }

        // A fresh image per show so a late (posted) render for a previous tile can never
        // contaminate the one on screen.
        _image = new Image { Width = PreviewSize, Height = PreviewSize, Stretch = Stretch.Uniform };
        _pendingRender(_image);

        _popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x56, 0x60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Child = _image,
        };
        _popup.PlacementTarget = _pendingAnchor;
        _showing = true;
        _popup.IsOpen = true;
    }

    private void CloseNow()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _showing = false;
        _popup.IsOpen = false;
        _image = null;
        _pendingAnchor = null;
        _pendingRender = null;
    }
}
