using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Ged.App.Dialogs;

/// <summary>A tiny modal prompt for a single line of text (by-UID, teleport XYZ, …).</summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _box = new() { FontSize = 13, MinWidth = 240 };
    private string? _result;

    public InputDialog(string title, string prompt, string initial = "")
    {
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box.Text = initial;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };
        ok.Click += (_, _) => { _result = _box.Text; Close(); };
        cancel.Click += (_, _) => { _result = null; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, ok },
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(14),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt },
                _box,
                buttons,
            },
        };

        Opened += (_, _) => { _box.Focus(); _box.SelectAll(); };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _result = _box.Text; Close(); } };
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
}
