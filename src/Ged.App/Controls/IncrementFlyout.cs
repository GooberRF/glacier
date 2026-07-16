using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.App.Viewport;

namespace Ged.App.Controls;

/// <summary>
/// Builds the increment-picker popover (item 4) shared by the status-bar readouts and
/// the per-pane toolbar pickers: a grid of preset buttons (current value highlighted)
/// plus a validated free-entry field. Choosing a preset or committing a valid free
/// entry closes the popover; invalid entries stay open with an error hint.
/// </summary>
internal static class IncrementFlyout
{
    private static readonly SolidColorBrush CurrentBrush = new(Color.FromRgb(0x2E, 0x62, 0x9E));

    public static Flyout Build(IncrementSetting setting)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        // Rebuilt on every open so the "current value" highlight is always fresh.
        flyout.Opening += (_, _) => flyout.Content = BuildContent(setting, flyout);
        return flyout;
    }

    /// <summary>
    /// A compact dropdown-style picker (top-bar control): a label+value button whose
    /// flyout is the preset/free-entry popover. The label tracks the live value.
    /// </summary>
    public static DropDownButton MakeDropDown(IncrementSetting setting, double minWidth)
    {
        var button = new DropDownButton
        {
            Content = $"{setting.Label} {setting.Format(setting.Value)}",
            FontSize = 12,
            Padding = new Thickness(8, 3),
            MinWidth = minWidth,
            Flyout = Build(setting),
        };
        ToolTip.SetTip(button, $"{setting.Label} increment — presets or free entry");
        setting.Changed += () => button.Content = $"{setting.Label} {setting.Format(setting.Value)}";
        return button;
    }

    private static Control BuildContent(IncrementSetting setting, Flyout flyout)
    {
        var root = new StackPanel { Spacing = 6, MinWidth = 190 };
        root.Children.Add(new TextBlock
        {
            Text = $"{setting.Label} increment",
            FontSize = 11,
            Opacity = 0.7,
        });

        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (float preset in setting.Presets)
        {
            float captured = preset;
            bool isCurrent = MathF.Abs(setting.Value - preset) <= MathF.Abs(preset) * 1e-4f;
            var btn = new Button
            {
                Content = setting.Format(preset),
                FontSize = 11,
                Margin = new Thickness(0, 0, 4, 4),
                MinWidth = 52,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
                Background = isCurrent ? CurrentBrush : null,
            };
            btn.Click += (_, _) =>
            {
                setting.SetValue(captured);
                flyout.Hide();
            };
            presets.Children.Add(btn);
        }

        root.Children.Add(presets);

        var entry = new TextBox
        {
            Text = setting.Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 11,
            Watermark = $"Custom ({setting.Unit.Trim()})",
            MinWidth = 110,
        };
        var apply = new Button { Content = "Set", FontSize = 11 };
        var error = new TextBlock
        {
            Text = "Enter a positive number.",
            FontSize = 10,
            Foreground = Brushes.IndianRed,
            IsVisible = false,
        };

        void Commit()
        {
            if (setting.TrySetFromText(entry.Text))
            {
                flyout.Hide();
            }
            else
            {
                error.IsVisible = true;
            }
        }

        apply.Click += (_, _) => Commit();
        entry.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };

        var entryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        entryRow.Children.Add(entry);
        entryRow.Children.Add(apply);
        root.Children.Add(entryRow);
        root.Children.Add(error);
        return root;
    }
}
