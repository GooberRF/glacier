using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ged.App.Dialogs;

/// <summary>A tiny modal Yes/No confirmation prompt.</summary>
internal sealed class ConfirmDialog : Window
{
    private bool _result;

    public ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yes = new Button { Content = "Yes", IsDefault = true, MinWidth = 72 };
        var no = new Button { Content = "No", IsCancel = true, MinWidth = 72 };
        yes.Click += (_, _) => { _result = true; Close(); };
        no.Click += (_, _) => { _result = false; Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(14),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
}
