using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace Ged.App.Dialogs;

/// <summary>
/// A tiny modal single-choice picker: a scrollable list of options with OK / Cancel.
/// Double-clicking (or Enter on) an item accepts it. Returns the chosen string, or null on
/// cancel. Used where the user must choose from an existing set rather than type free text
/// (e.g. picking which prefab to overwrite).
/// </summary>
internal sealed class PickerDialog : Window
{
    private readonly ListBox _list = new() { MinHeight = 160, MaxHeight = 360 };
    private string? _result;

    public PickerDialog(string title, string prompt, IReadOnlyList<string> choices)
    {
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _list.ItemsSource = choices;
        _list.SelectedIndex = choices.Count > 0 ? 0 : -1;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { _result = null; Close(); };
        _list.DoubleTapped += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(14),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt },
                new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };

        Opened += (_, _) => _list.Focus();
    }

    private void Accept()
    {
        _result = _list.SelectedItem as string;
        Close();
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string prompt, IReadOnlyList<string> choices)
    {
        var dlg = new PickerDialog(title, prompt, choices);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
}
