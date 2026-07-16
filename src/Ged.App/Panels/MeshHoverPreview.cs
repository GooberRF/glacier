using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Ged.Core.Editor;

namespace Ged.App.Panels;

/// <summary>
/// The palette's floating mesh-preview popover (owner ask): hovering a class row's small
/// mesh-preview box pops a larger (<see cref="PreviewSize"/>px) render of that mesh beside the
/// row, after a short dwell (<see cref="Delay"/>) so a quick scroll past a row does not flash
/// popups. It never takes keyboard focus — a <see cref="Popup"/> with a non-focusable child, so
/// the tree keeps focus and arrow-key navigation is unaffected — and it closes as soon as the
/// pointer leaves the box. The larger render is produced lazily on first hover through the same
/// cache-backed loader as the inline row thumbnail; the thumbnail cache is size-keyed, so the
/// 384px preview caches independently of the 24px row icon and is paid for only once per mesh.
/// </summary>
internal sealed class MeshHoverPreview
{
    /// <summary>Edge length of the popped preview render, in pixels.</summary>
    public const int PreviewSize = 384;

    /// <summary>Hover dwell before the popover opens (so scrolling past a row does not flash it).</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    private readonly Popup _popup;
    private readonly DispatcherTimer _timer;
    private Control? _pendingAnchor;
    private LevelObjectKind _pendingKind;
    private string? _pendingClass;
    private Image? _image;
    private bool _showing;

    public MeshHoverPreview()
    {
        _popup = new Popup
        {
            // Beside the row (top-aligned) so the popover never covers the row it previews.
            Placement = PlacementMode.RightEdgeAlignedTop,
            HorizontalOffset = 6,
            // We drive open/close from pointer enter/exit; light-dismiss would add a
            // pointer-capturing overlay we do not want.
            IsLightDismissEnabled = false,
            // The popover is passive: it must never pull focus off the palette tree.
            Focusable = false,
        };

        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += (_, _) => ShowNow();
    }

    /// <summary>Renders a class's mesh into the target image (wired by the panel to the host loader).</summary>
    public Action<LevelObjectKind, string?, Image>? RenderInto { get; set; }

    /// <summary>The popup control the panel adds to its own visual tree.</summary>
    public Popup Popup => _popup;

    /// <summary>True while the popover is open.</summary>
    public bool IsShowing => _showing;

    /// <summary>True while a show is scheduled but the dwell delay has not yet elapsed.</summary>
    public bool HasPendingShow => _timer.IsEnabled;

    /// <summary>The class whose preview is pending/showing (introspection / tests).</summary>
    public string? PendingClass => _pendingClass;

    /// <summary>The kind whose preview is pending/showing (introspection / tests).</summary>
    public LevelObjectKind PendingKind => _pendingKind;

    /// <summary>The image inside the open popover, or null when closed (introspection / tests).</summary>
    public Image? CurrentImage => _image;

    /// <summary>Schedules the popover for a class row's preview box after the dwell delay.</summary>
    public void Schedule(Control anchor, LevelObjectKind kind, string? className)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        _pendingAnchor = anchor;
        _pendingKind = kind;
        _pendingClass = className;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Opens the popover immediately for the pending row (the dwell timer's action).</summary>
    public void ShowNow()
    {
        _timer.Stop();
        if (_pendingAnchor is null)
        {
            return;
        }

        // A fresh image per show so a late (posted) render for a previous row can never
        // contaminate the one on screen.
        _image = new Image
        {
            Width = PreviewSize,
            Height = PreviewSize,
            Stretch = Stretch.Uniform,
        };
        RenderInto?.Invoke(_pendingKind, _pendingClass, _image);

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

    /// <summary>Cancels any pending show and closes the popover (the pointer left the box).</summary>
    public void Cancel()
    {
        _timer.Stop();
        _pendingAnchor = null;
        _showing = false;
        _popup.IsOpen = false;
        _image = null;
    }
}
