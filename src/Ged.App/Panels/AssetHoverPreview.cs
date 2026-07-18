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
/// is airspace-safe over the native viewport panes) and never takes keyboard focus.
/// <para>
/// Moving directly from one tile to another while the popover is open re-keys it to the new tile and
/// re-renders IMMEDIATELY (no re-dwell). The dwell gate is only for opening from cold. Because an
/// open Avalonia <see cref="Popup"/> ignores <see cref="Popup.Child"/> / <see cref="Popup.PlacementTarget"/>
/// changes, the popover is force-refreshed by closing and re-opening it on a tile switch. A short
/// close delay bridges the inter-tile gap, and close is keyed to the tracked tile so an out-of-order
/// leave-old-after-enter-new can never close the freshly-shown preview.
/// </para>
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
    private Control? _currentAnchor;
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

        // A short close delay bridges the gap between adjacent tiles: leaving a tile starts it,
        // entering another cancels it, so movement across the inter-tile margin never closes/flickers.
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); CloseNow(); };
    }

    /// <summary>The popup control the panel adds to its own visual tree so it can open.</summary>
    public Popup Popup => _popup;

    /// <summary>True while the popover is open (introspection / tests).</summary>
    public bool IsShowing => _showing;

    /// <summary>True while a show is scheduled but the dwell delay has not yet elapsed (tests).</summary>
    public bool HasPendingShow => _openTimer.IsEnabled;

    /// <summary>The tile the popover is currently showing (its content key), or null when closed (tests).</summary>
    public Control? CurrentKey => _currentAnchor;

    /// <summary>
    /// Schedules (from cold, after the dwell) or, if already open, IMMEDIATELY re-keys the preview to
    /// <paramref name="anchor"/>, filling its image via <paramref name="render"/>. Re-entering the tile
    /// already shown is a no-op (no flicker).
    /// </summary>
    public void Schedule(Control anchor, Action<Image> render)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        _closeTimer.Stop();

        // Already showing THIS tile → nothing to do (a spurious re-enter must not churn the popup).
        if (_showing && ReferenceEquals(anchor, _currentAnchor))
        {
            return;
        }

        _pendingAnchor = anchor;
        _pendingRender = render;
        if (_showing)
        {
            ShowNow(); // open → snap to the new tile immediately (re-key + re-render, no re-dwell)
            return;
        }

        _openTimer.Stop();
        _openTimer.Start();
    }

    /// <summary>
    /// Starts the short close delay because the pointer left <paramref name="tile"/>. Only reacts when
    /// <paramref name="tile"/> is the tile we are actually tracking (shown or pending): an out-of-order
    /// leave of the OLD tile arriving after we already re-keyed to the NEW one must not close it.
    /// </summary>
    public void ScheduleClose(Control tile)
    {
        if (!ReferenceEquals(tile, _currentAnchor) && !ReferenceEquals(tile, _pendingAnchor))
        {
            return;
        }

        _openTimer.Stop();
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    /// <summary>Closes immediately and drops any pending show (e.g. a tab switch or grid rebuild).</summary>
    public void Cancel() => CloseNow();

    /// <summary>Opens (or re-keys) the popover for the pending tile — the dwell timer's action / a live switch.</summary>
    public void ShowNow()
    {
        _openTimer.Stop();
        if (_pendingAnchor is null || _pendingRender is null)
        {
            return;
        }

        // An open Avalonia Popup ignores Child/PlacementTarget changes — close it first so the
        // re-open picks up the new content and placement (the "stale previous tile" fix).
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
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
        _currentAnchor = _pendingAnchor;
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
        _currentAnchor = null;
    }
}
