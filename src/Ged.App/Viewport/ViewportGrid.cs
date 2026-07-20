using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.Core.Assets;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Scene;

namespace Ged.App.Viewport;

/// <summary>
/// The central document area: four persistent viewport panes arranged as 1 / 2 /
/// 4-pane presets (stock default 4 = Top + Perspective + Front + Left, ortho panes
/// wireframe), with active-under-mouse focus, the TAB maximize/restore toggle and a
/// (menu/palette) Reset Viewport Layout that restores the 4-pane default. The panes
/// are kept alive across layout changes; their camera pose and scene survive the
/// native re-parent.
/// </summary>
public sealed class ViewportGrid : UserControl
{
    private readonly ViewportPane[] _panes;
    private int _layout = 4;
    // First-launch default (item 1): the perspective viewport starts MAXIMIZED. The
    // underlying layout stays the 4-pane grid (LayoutMode == 4), so TAB restores it.
    private bool _maximized = true;
    private IViewportSurface? _active;
    private IViewportSurface? _lastPerspective;

    public ViewportGrid(
        CommandDispatcher dispatcher, CameraSchemeKind scheme, RenderMode perspectiveMode,
        RenderOptionsModel? renderOptions = null, bool useOpenGl = false)
    {
        _panes = new[]
        {
            new ViewportPane(dispatcher, ViewType.Top, RenderMode.Wireframe, scheme, renderOptions, useOpenGl),
            new ViewportPane(dispatcher, ViewType.Perspective, perspectiveMode, scheme, renderOptions, useOpenGl),
            new ViewportPane(dispatcher, ViewType.Front, RenderMode.Wireframe, scheme, renderOptions, useOpenGl),
            new ViewportPane(dispatcher, ViewType.Left, RenderMode.Wireframe, scheme, renderOptions, useOpenGl),
        };

        foreach (ViewportPane p in _panes)
        {
            p.Surface.Activated += OnSurfaceActivated;
        }

        _active = _panes[1].Surface;
        Rebuild();
    }

    /// <summary>Raised when the active (under-mouse) pane changes.</summary>
    public event Action<IViewportSurface>? ActivePaneChanged;

    public IReadOnlyList<ViewportPane> Panes => _panes;

    public IViewportSurface ActiveSurface => _active ?? _panes[1].Surface;

    /// <summary>
    /// The surface whose camera "place at camera" / snap-to-camera should use: the
    /// active pane when it is perspective, else the most recently active perspective
    /// pane. An ortho pane's camera is a pan center on a fixed axis — not an eye —
    /// so placing 'at camera' relative to it lands somewhere unrelated to the view.
    /// </summary>
    public IViewportSurface CameraSurface
    {
        get
        {
            if (ActiveSurface.ViewType == ViewType.Perspective)
            {
                return ActiveSurface;
            }

            if (_lastPerspective is { ViewType: ViewType.Perspective } last)
            {
                return last;
            }

            foreach (ViewportPane p in _panes)
            {
                if (p.Surface.ViewType == ViewType.Perspective)
                {
                    return p.Surface;
                }
            }

            return ActiveSurface;
        }
    }

    /// <summary>"Place at camera": a few metres in front of the pane's live camera (stock 4 m).</summary>
    public static Vector3 PlaceAtCameraPoint(IViewportSurface s) =>
        s.Camera is { } cam ? PlaceAtCameraPoint(cam) : s.CameraPosition;

    /// <summary>"Place at camera" for a live camera: position + forward × 4 m.</summary>
    public static Vector3 PlaceAtCameraPoint(Ged.Rendering.Camera cam) =>
        cam.Position + (cam.Forward * 4f);

    /// <summary>
    /// True while the pointer is over any viewport pane: over a native render surface
    /// (tracked by the same WM_MOUSEMOVE/WM_MOUSELEAVE plumbing that drives the
    /// active-pane red border) or over a pane's Avalonia chrome (toolbar/border).
    /// </summary>
    public bool IsPointerOverViewport
    {
        get
        {
            foreach (ViewportPane p in _panes)
            {
                if (p.Surface.IsPointerInside || p.IsPointerOver)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// TAB routing: TAB is the maximize/restore toggle when the pointer is over any
    /// viewport pane OR focus sits inside one; otherwise TAB stays a normal
    /// focus-traversal key (text boxes, panels).
    /// </summary>
    public bool TabTargetsViewport(object? focusedElement) =>
        IsPointerOverViewport
        || (focusedElement is Visual v && v.FindAncestorOfType<ViewportPane>(includeSelf: true) is not null);

    public int LayoutMode => _layout;

    public bool IsMaximized => _maximized;

    public void SetLayout(int panes)
    {
        _layout = Math.Clamp(panes, 1, 4);
        _maximized = false;
        Rebuild();
    }

    public void ToggleMaximize()
    {
        _maximized = !_maximized;
        Rebuild();
    }

    public void ResetLayout()
    {
        _panes[0].Surface.SetViewType(ViewType.Top);
        _panes[0].Surface.Mode = RenderMode.Wireframe;
        _panes[1].Surface.SetViewType(ViewType.Perspective);
        _panes[2].Surface.SetViewType(ViewType.Front);
        _panes[2].Surface.Mode = RenderMode.Wireframe;
        _panes[3].Surface.SetViewType(ViewType.Left);
        _panes[3].Surface.Mode = RenderMode.Wireframe;
        _layout = 4;
        // Reset restores the first-launch default: the 4-pane grid with the perspective
        // pane maximized (item 1). TAB then restores the full 4-pane grid as usual.
        _active = _panes[1].Surface;
        _maximized = true;
        Rebuild();
    }

    /// <summary>Applies a freshly built scene + camera framing to every pane.</summary>
    public void LoadScene(RenderScene scene, AssetVfs? vfs, Vector3 cameraPosition, Vector3 cameraTarget)
    {
        foreach (ViewportPane p in _panes)
        {
            p.Surface.LoadScene(scene, vfs, cameraPosition, cameraTarget);
        }
    }

    /// <summary>Re-uploads a rebuilt scene (grid/brightness change) to every pane.</summary>
    public void RefreshScene(RenderScene scene, AssetVfs? vfs)
    {
        foreach (ViewportPane p in _panes)
        {
            p.Surface.RefreshScene(scene, vfs);
        }
    }

    public void SetSelection(IReadOnlyList<Ged.Rendering.Scene.LineSegment> lines)
    {
        foreach (ViewportPane p in _panes)
        {
            p.Surface.SetSelection(lines);
        }
    }

    /// <summary>Sets the manipulator/gizmo overlay (drawn on top of the scene) on every pane (item 12).</summary>
    public void SetGizmoOverlay(IReadOnlyList<Ged.Rendering.Scene.LineSegment> lines)
    {
        foreach (ViewportPane p in _panes)
        {
            p.Surface.SetGizmoOverlay(lines);
        }
    }

    /// <summary>Sets (or clears) the on-top transform-label overlay scene on every pane, so a drag's
    /// live Δ/∠/% readout reads in all panes without a whole-scene rebuild.</summary>
    public void SetOverlayScene(Ged.Rendering.Scene.RenderScene? scene, AssetVfs? vfs)
    {
        foreach (ViewportPane p in _panes)
        {
            p.Surface.SetOverlayScene(scene, vfs);
        }
    }

    public void ForEachSurface(Action<IViewportSurface> action)
    {
        foreach (ViewportPane p in _panes)
        {
            action(p.Surface);
        }
    }

    public void SetScheme(CameraSchemeKind kind)
    {
        foreach (ViewportPane p in _panes)
        {
            p.SyncScheme(kind);
        }
    }

    private void OnSurfaceActivated(IViewportSurface surface)
    {
        if (surface.ViewType == ViewType.Perspective)
        {
            _lastPerspective = surface;
        }

        if (ReferenceEquals(_active, surface))
        {
            return;
        }

        _active = surface;
        foreach (ViewportPane p in _panes)
        {
            p.IsActivePane = ReferenceEquals(p.Surface, surface);
        }

        ActivePaneChanged?.Invoke(surface);
    }

    private void Rebuild()
    {
        // Detach panes from any current parent before re-composing. A maximized (or
        // 1-pane) layout makes a pane THIS control's Content directly, so its parent is
        // the ContentControl — not a Panel/Grid. Clearing Content first releases it;
        // otherwise re-adding that pane into the rebuilt layout throws "control already
        // has a visual parent" — the TAB maximize->restore crash.
        Content = null;
        foreach (ViewportPane p in _panes)
        {
            (p.Parent as Panel)?.Children.Remove(p);
            (p.Parent as Grid)?.Children.Remove(p);
        }

        Content = _maximized || _layout == 1
            ? SinglePane(_maximized ? ActivePaneOf() : _panes[1])
            : _layout == 2
                ? Row(_panes[1], _panes[0])
                : FourPane();

        foreach (ViewportPane p in _panes)
        {
            p.IsActivePane = ReferenceEquals(p.Surface, _active);
        }
    }

    private ViewportPane ActivePaneOf()
    {
        foreach (ViewportPane p in _panes)
        {
            if (ReferenceEquals(p.Surface, _active))
            {
                return p;
            }
        }

        return _panes[1];
    }

    private static Control SinglePane(ViewportPane pane) => pane;

    private Control FourPane()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(4)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        Control top = Row(_panes[0], _panes[1]);
        Control bottom = Row(_panes[2], _panes[3]);
        var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = SplitterBrush };
        Grid.SetRow(top, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(top);
        grid.Children.Add(splitter);
        grid.Children.Add(bottom);
        return grid;
    }

    private static Control Row(ViewportPane left, ViewportPane right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var splitter = new GridSplitter { Width = 4, Background = SplitterBrush };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    private static readonly IBrush SplitterBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x14));
}
