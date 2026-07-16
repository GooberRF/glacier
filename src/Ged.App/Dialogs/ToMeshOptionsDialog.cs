using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Ged.App.Dialogs;

/// <summary>
/// The brush "To Mesh" options modal (alpine-gap-inventory: To-Mesh options dialog, per Alpine
/// mesh_export.cpp:503-524). Two toggles the Glacier conversion previously hard-wired on:
/// <b>Replace with mesh object</b> (delete the source brushes and drop a Mesh object where they were)
/// and <b>Reset origin</b> (recentre the exported geometry on its own origin vs. keep world
/// coordinates). Returns the chosen options, or null on cancel.
/// </summary>
internal sealed class ToMeshOptionsDialog : Window
{
    private readonly CheckBox _replace = new()
    {
        Content = "Replace the selection with a Mesh object",
        IsChecked = true,
    };

    private readonly CheckBox _resetOrigin = new()
    {
        Content = "Reset origin (recentre the mesh on its own origin)",
        IsChecked = true,
    };

    private Result? _result;

    public ToMeshOptionsDialog(int brushCount)
    {
        Title = "To Mesh — Options";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "Export", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { _result = new Result(_replace.IsChecked == true, _resetOrigin.IsChecked == true); Close(); };
        cancel.Click += (_, _) => { _result = null; Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(14),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"Convert {brushCount} brush(es) to a V3M mesh.",
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                },
                _replace,
                _resetOrigin,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
    }

    /// <summary>The chosen To-Mesh options.</summary>
    public readonly record struct Result(bool ReplaceWithMeshObject, bool ResetOrigin);

    public static async Task<Result?> ShowAsync(Window owner, int brushCount)
    {
        var dlg = new ToMeshOptionsDialog(brushCount);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
}
