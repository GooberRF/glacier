using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Model;

namespace Ged.App.Dialogs;

/// <summary>A small colour chooser for the Change color buttons: red, green and blue (0–255) with a live swatch.</summary>
internal sealed class RgbColorDialog : Window
{
    private readonly NumericUpDown[] _channels = new NumericUpDown[3];
    private readonly Border _swatch = new() { Width = 64, Height = 40, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private RfColor _color;
    private bool _accepted;

    public RgbColorDialog(RfColor initial)
    {
        Title = "Color";
        Width = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _color = initial;

        var rows = new StackPanel { Spacing = 6 };
        string[] names = { "Red:", "Green:", "Blue:" };
        byte[] values = { initial.R, initial.G, initial.B };
        for (int i = 0; i < 3; i++)
        {
            int ch = i;
            var slider = new Slider { Minimum = 0, Maximum = 255, Value = values[i], Width = 140 };
            var box = new NumericUpDown { Minimum = 0, Maximum = 255, Increment = 1, FormatString = "0", Value = values[i], Width = 90 };
            slider.ValueChanged += (_, _) => box.Value = (decimal)System.Math.Round(slider.Value);
            box.ValueChanged += (_, _) =>
            {
                slider.Value = (double)(box.Value ?? 0);
                Update();
            };
            _channels[ch] = box;
            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new TextBlock { Text = names[i], Width = 44, VerticalAlignment = VerticalAlignment.Center }, slider, box },
            });
        }

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };
        ok.Click += (_, _) => { _accepted = true; Close(); };
        cancel.Click += (_, _) => Close();
        Update();
        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children =
            {
                rows,
                _swatch,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
    }

    /// <summary>Shows the chooser; the picked colour (alpha kept), or null when cancelled.</summary>
    public static async Task<RfColor?> ShowAsync(Window owner, RfColor initial)
    {
        var dlg = new RgbColorDialog(initial);
        await dlg.ShowDialog(owner);
        return dlg._accepted ? dlg._color : null;
    }

    private void Update()
    {
        byte C(int i) => (byte)System.Math.Clamp((int)(_channels[i]?.Value ?? 0), 0, 255);
        if (_channels[0] is null || _channels[1] is null || _channels[2] is null)
        {
            return;
        }

        _color = new RfColor(C(0), C(1), C(2), _color.A);
        _swatch.Background = new SolidColorBrush(Color.FromRgb(_color.R, _color.G, _color.B));
    }
}
