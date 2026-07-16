using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ged.Core.Editing;

namespace Ged.App.Dialogs;

/// <summary>
/// The "Recover unsaved changes?" dialog (item 18): a proper recovery chooser that shows both
/// files side by side (original: name / last-saved / size · autosave: written / size) with a
/// diff hint, and three clearly-labelled actions — Open Autosave (recommended, default focus),
/// Open Original (secondary), Delete Autosave &amp; Open Original (destructive). Keyboard-navigable
/// (Enter = Open Autosave, Esc = Open Original), theme-correct, and a normal Avalonia dialog
/// (no ambiguous Yes/No, no OS-modal), matching the SaveTarget/LevelProperties style.
/// </summary>
internal sealed class RecoveryDialog : Window
{
    public RecoveryDialog(string originalPath, string autosavePath)
    {
        Title = "Recover unsaved changes?";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        DateTime origUtc = SafeWriteTime(originalPath);
        DateTime autoUtc = SafeWriteTime(autosavePath);
        long origSize = SafeSize(originalPath);
        long autoSize = SafeSize(autosavePath);

        var intro = new TextBlock
        {
            Text = "A newer autosave was found for this level. Choose which version to open.",
            TextWrapping = TextWrapping.Wrap,
        };
        var hint = new TextBlock
        {
            Text = char.ToUpperInvariant(RecoveryDecision.DescribeAgeDifference(origUtc, autoUtc)[0])
                   + RecoveryDecision.DescribeAgeDifference(origUtc, autoUtc)[1..] + ".",
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.MediumSeaGreen,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var compare = new Grid { Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        compare.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        compare.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Control original = FileCard(
            "Original file",
            Path.GetFileName(originalPath),
            $"Last saved: {Local(origUtc)}",
            $"Size: {RecoveryDecision.DescribeSize(origSize)}",
            accent: false);
        Control autosave = FileCard(
            "Autosave (unsaved changes)",
            Path.GetFileName(autosavePath),
            $"Written: {Local(autoUtc)}",
            $"Size: {RecoveryDecision.DescribeSize(autoSize)}",
            accent: true);
        ((Border)autosave).Margin = new Avalonia.Thickness(12, 0, 0, 0); // gap between the two cards
        Grid.SetColumn(original, 0);
        Grid.SetColumn(autosave, 1);
        compare.Children.Add(original);
        compare.Children.Add(autosave);

        var openAutosave = new Button { Content = "Open Autosave", IsDefault = true, MinWidth = 130 };
        var openOriginal = new Button { Content = "Open Original", IsCancel = true, MinWidth = 110 };
        var deleteOpen = new Button
        {
            Content = "Delete Autosave & Open Original",
            MinWidth = 210,
            Foreground = Brushes.White,
            Background = Brushes.IndianRed,
        };
        openAutosave.Click += (_, _) => Finish(RecoveryChoice.OpenAutosave);
        openOriginal.Click += (_, _) => Finish(RecoveryChoice.OpenOriginal);
        deleteOpen.Click += (_, _) => Finish(RecoveryChoice.DeleteAutosaveAndOpenOriginal);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            Children = { deleteOpen, openOriginal, openAutosave },
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children = { intro, hint, compare, buttons },
        };

        // Default focus on the recommended action so Enter opens the autosave.
        Opened += (_, _) => openAutosave.Focus();
    }

    /// <summary>The chosen action. Defaults to Open Original (also the Esc / window-close result).</summary>
    public RecoveryChoice Result { get; private set; } = RecoveryChoice.OpenOriginal;

    public static async Task<RecoveryChoice> ShowAsync(Window owner, string originalPath, string autosavePath)
    {
        var dlg = new RecoveryDialog(originalPath, autosavePath);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    private void Finish(RecoveryChoice choice)
    {
        Result = choice;
        Close();
    }

    private static Control FileCard(string title, string name, string when, string size, bool accent)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = accent ? Brushes.MediumSeaGreen : null });
        panel.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = when, FontSize = 11, Foreground = Brushes.Gray });
        panel.Children.Add(new TextBlock { Text = size, FontSize = 11, Foreground = Brushes.Gray });
        return new Border
        {
            BorderBrush = accent ? Brushes.MediumSeaGreen : Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10),
            Child = panel,
        };
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow;
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }

    private static long SafeSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}
