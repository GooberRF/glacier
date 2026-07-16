using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.Rendering;

namespace Ged.App.Viewport;

/// <summary>
/// A viewport pane: a toolbar (view-type dropdown, render-mode dropdown with global
/// render-option toggles) over a live <see cref="ViewportSurface"/>, wrapped in a
/// border that turns red when the pane is the active (under-mouse) one — stock parity.
/// The camera scheme is a single GLOBAL setting (View ▸ Camera Scheme), not a per-pane
/// control, and the grid/rotation increment pickers live on the main top bar.
/// </summary>
public sealed class ViewportPane : Border
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromRgb(0xE0, 0x40, 0x40));
    private static readonly SolidColorBrush InactiveBrush = new(Color.FromRgb(0x30, 0x33, 0x3A));

    private readonly ComboBox _viewCombo;
    private readonly DropDownButton _modeButton;
    private readonly Flyout _modeFlyout;
    private readonly RenderOptionsModel? _renderOptions;

    public ViewportPane(
        CommandDispatcher dispatcher, ViewType viewType, RenderMode mode, CameraSchemeKind scheme,
        RenderOptionsModel? renderOptions = null, bool useOpenGl = false)
    {
        _renderOptions = renderOptions;
        Surface = useOpenGl
            ? new GlViewportSurface(dispatcher, scheme, viewType) { Mode = mode }
            : new ViewportSurface(dispatcher, scheme, viewType) { Mode = mode };

        _viewCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<ViewType>(),
            SelectedIndex = (int)viewType,
            MinWidth = 96,
            FontSize = 11,
        };
        _viewCombo.SelectionChanged += (_, _) =>
        {
            if (_viewCombo.SelectedItem is ViewType v)
            {
                Surface.SetViewType(v);
            }
        };

        // Render-mode dropdown: modes (radio; choosing one closes the flyout) + the
        // relocated global render-option checkboxes (toggling keeps the flyout open —
        // it light-dismisses on outside click).
        _modeFlyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _modeFlyout.Opening += (_, _) => _modeFlyout.Content = BuildModeMenu();
        _modeButton = new DropDownButton
        {
            Content = ModeName(mode),
            FontSize = 11,
            MinWidth = 130,
            Flyout = _modeFlyout,
        };
        Surface.ModeChanged += m => _modeButton.Content = ModeName(m);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4, 2),
        };
        toolbar.Children.Add(_viewCombo);
        toolbar.Children.Add(_modeButton);

        var toolbarBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2B)),
            Child = toolbar,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Control surfaceControl = Surface.AsControl();
        Grid.SetRow(toolbarBar, 0);
        Grid.SetRow(surfaceControl, 1);
        grid.Children.Add(toolbarBar);
        grid.Children.Add(surfaceControl);

        Child = grid;
        BorderThickness = new Thickness(2);
        BorderBrush = InactiveBrush;
    }

    public IViewportSurface Surface { get; }

    public bool IsActivePane
    {
        set => BorderBrush = value ? ActiveBrush : InactiveBrush;
    }

    /// <summary>Applies the global camera scheme (View ▸ Camera Scheme) to this pane's surface.</summary>
    public void SyncScheme(CameraSchemeKind kind) => Surface.SetScheme(kind);

    /// <summary>Friendly render-mode names (matches the View-menu wording).</summary>
    internal static string ModeName(RenderMode mode) => mode switch
    {
        RenderMode.JustTextures => "Just Textures",
        RenderMode.TexturesAndLightmaps => "Textures w Lightmaps",
        RenderMode.JustLightmaps => "Just Lightmaps",
        RenderMode.RoomColors => "Rooms in Different Colors",
        RenderMode.Wireframe => "Wireframe",
        RenderMode.SeeThrough => "Everything See-through",
        _ => mode.ToString(),
    };

    private Control BuildModeMenu()
    {
        var root = new StackPanel { Spacing = 2, MinWidth = 210 };

        foreach (RenderMode mode in Enum.GetValues<RenderMode>())
        {
            RenderMode captured = mode;
            var rb = new RadioButton
            {
                Content = ModeName(mode),
                FontSize = 11,
                IsChecked = Surface.Mode == mode,
                GroupName = $"pane-mode-{GetHashCode()}",
            };

            // Choosing a render mode applies it and CLOSES the dropdown.
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked == true && Surface.Mode != captured)
                {
                    Surface.Mode = captured;
                    _modeFlyout.Hide();
                }
            };
            rb.Click += (_, _) => _modeFlyout.Hide();
            root.Children.Add(rb);
        }

        // Global render-option toggles + radio groups relocated from the View menu (item 4).
        // Perspective-only options (fog, sky, room culling, portal faces) are shown only when
        // this pane is a perspective view — filtered by the model against the current view type.
        if (_renderOptions is { } options)
        {
            List<RenderOptionToggle> toggles = options.VisibleToggles(Surface.ViewType).ToList();
            List<RenderOptionRadioGroup> groups = options.VisibleRadioGroups(Surface.ViewType).ToList();

            if (toggles.Count > 0 || groups.Count > 0)
            {
                root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
            }

            bool syncing = false;
            var syncers = new List<Action>();

            foreach (RenderOptionToggle toggle in toggles)
            {
                RenderOptionToggle captured = toggle;
                var cb = new CheckBox { Content = toggle.Label, FontSize = 11, IsChecked = toggle.Value };

                // Toggling a render option keeps the dropdown OPEN (light-dismiss on outside
                // click / mode choice only) so several can be flipped in a row.
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (!syncing)
                    {
                        options.SetValue(captured, cb.IsChecked == true);
                    }
                };

                syncers.Add(() => cb.IsChecked = captured.Value);
                root.Children.Add(cb);
            }

            foreach (RenderOptionRadioGroup group in groups)
            {
                root.Children.Add(new TextBlock
                {
                    Text = group.Label,
                    FontSize = 11,
                    Opacity = 0.6,
                    Margin = new Thickness(2, 6, 0, 0),
                });

                string groupName = $"pane-{group.Label}-{GetHashCode()}";
                foreach (RenderOptionRadioOption option in group.Options)
                {
                    RenderOptionRadioOption captured = option;
                    var rb = new RadioButton
                    {
                        Content = option.Label,
                        FontSize = 11,
                        GroupName = groupName,
                        IsChecked = option.IsChecked,
                        Margin = new Thickness(8, 0, 0, 0),
                    };
                    rb.IsCheckedChanged += (_, _) =>
                    {
                        if (!syncing && rb.IsChecked == true)
                        {
                            options.SelectRadio(captured);
                        }
                    };
                    syncers.Add(() => rb.IsChecked = captured.IsChecked);
                    root.Children.Add(rb);
                }
            }

            // The state is global — reflect changes made from another pane/command while this
            // flyout is open. One handler drives every checkbox/radio; detached on close.
            if (syncers.Count > 0)
            {
                void OnChanged()
                {
                    syncing = true;
                    foreach (Action s in syncers)
                    {
                        s();
                    }

                    syncing = false;
                }

                options.Changed += OnChanged;
                root.DetachedFromVisualTree += (_, _) => options.Changed -= OnChanged;
            }
        }

        return root;
    }
}
