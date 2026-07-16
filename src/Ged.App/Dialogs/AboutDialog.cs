using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Ged.App.Dialogs;

/// <summary>
/// About box: product name + version (with git short-sha), a one-line description,
/// credits, and a "Third-party licenses" toggle that shows the shipped
/// <c>licensing-info.txt</c> content in-app (scrollable, monospace).
/// </summary>
internal sealed class AboutDialog : Window
{
    public AboutDialog()
    {
        Title = "About Glacier";
        Width = 560;
        Height = 460;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var licenseBox = new TextBox
        {
            Text = LoadLicenseText(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo, monospace"),
            FontSize = 11,
            IsVisible = false,
        };
        var licenseScroll = new ScrollViewer
        {
            Content = licenseBox,
            IsVisible = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var licensesButton = new Button { Content = "Third-party licenses", MinWidth = 150 };
        licensesButton.Click += (_, _) =>
        {
            bool show = !licenseScroll.IsVisible;
            licenseScroll.IsVisible = show;
            licenseBox.IsVisible = show;
            licensesButton.Content = show ? "Hide licenses" : "Third-party licenses";
        };

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();

        var headerText = new StackPanel { Spacing = 4, Children =
        {
            new TextBlock { Text = "Glacier", FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = $"Version {AppVersion.Informational}", Opacity = 0.8, TextWrapping = TextWrapping.Wrap },
            new TextBlock
            {
                Text = "A modern level editor for Red Faction (2001), built for compatibility with the Alpine Faction modernization patch.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            },
        } };

        // Glacier logo in the top-right corner, reusing the embedded app icon
        // (AvaloniaResource, so it survives single-file publish). Best-effort: a
        // missing/undecodable resource must not stop the About box from opening.
        var logo = new Image
        {
            Width = 112,
            Height = 112,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(12, 0, 0, 0),
            Source = TryLoadLogo(),
        };

        // Text column takes the remaining width (and wraps); the logo column is Auto
        // so the header text never runs under or gets clipped by the logo.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(headerText, 0);
        Grid.SetColumn(logo, 1);
        header.Children.Add(headerText);
        header.Children.Add(logo);

        var credits = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children =
        {
            new TextBlock { Text = "Credits", FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "• Chris \"Goober\" Parsons — project owner & design", Opacity = 0.85 },
            new TextBlock { Text = "• Relies upon format research from many community members, including rafalh (Open Faction, RF Reversed), wardd64 (Unity Faction), and Goober (Alpine Faction, REDUX).", TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
            new TextBlock { Text = "• Red Faction is © Volition / THQ Nordic.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11, Margin = new Avalonia.Thickness(0, 4, 0, 0) },
            new TextBlock { Text = "• Glacier is © Chris \"Goober\" Parsons. See LICENSE and licensing-info.txt.", Opacity = 0.7, FontSize = 11 },
        } };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { licensesButton, close },
        };

        var layout = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(credits, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(credits);
        layout.Children.Add(buttons);
        layout.Children.Add(licenseScroll); // fills the remaining space when shown
        Content = layout;
    }

    public static void ShowFor(Window owner) => new AboutDialog().ShowDialog(owner);

    /// <summary>
    /// The Glacier logo for the About header, decoded from the embedded app icon
    /// (the same <c>AppIcon.ico</c> AvaloniaResource that <see cref="MainWindow"/> uses
    /// for the window icon). Best-effort — returns null if the resource is missing or
    /// cannot be decoded, so the About box still opens without a logo.
    /// </summary>
    private static Bitmap? TryLoadLogo()
    {
        try
        {
            return new Bitmap(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://Glacier/Assets/AppIcon.ico")));
        }
        catch (Exception ex)
        {
            CrashHandler.LogNonFatal("about-logo", ex);
            return null;
        }
    }

    private static string LoadLicenseText()
    {
        // Shipped next to the exe (Ged.App.csproj copies licensing-info.txt to output);
        // fall back to walking up to the repo root when running from a dev tree.
        try
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "licensing-info.txt");
            if (File.Exists(beside))
            {
                return File.ReadAllText(beside);
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "licensing-info.txt");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            CrashHandler.LogNonFatal("about-licenses", ex);
        }

        return "licensing-info.txt was not found next to the application.";
    }
}
