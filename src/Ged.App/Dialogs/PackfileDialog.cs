using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.Core.Packaging;

namespace Ged.App.Dialogs;

/// <summary>
/// The Create-Level-Packfile review dialog: a per-kind tree of the scanned
/// dependencies with include/exclude checkboxes, missing files highlighted with a
/// blocking toggle, a live selected-count/size total, an output path (default
/// user_maps\&lt;mode&gt;\&lt;level&gt;.vpp), and Build — which writes the level
/// .rfl first followed by the checked files via <see cref="PackfileBuilder"/>.
/// </summary>
internal sealed class PackfileDialog : Window
{
    private readonly PackfileBuildPlan _plan;
    private readonly byte[] _levelBytes;
    private readonly TextBox _outPath;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _build = new() { Content = "Build", IsDefault = true, MinWidth = 90 };

    public PackfileDialog(PackfileBuildPlan plan, byte[] levelBytes)
    {
        _plan = plan;
        _levelBytes = levelBytes;

        Title = "Create Level Packfile";
        Width = 620;
        Height = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _outPath = new TextBox { Text = plan.OutputPath, HorizontalAlignment = HorizontalAlignment.Stretch };
        var browse = new Button { Content = "…", MinWidth = 36 };
        browse.Click += async (_, _) => await BrowseAsync();

        var blockMissing = new CheckBox { Content = "Block build while files are missing", IsChecked = plan.BlockOnMissing };
        blockMissing.IsCheckedChanged += (_, _) => { plan.BlockOnMissing = blockMissing.IsChecked == true; UpdateSummary(); };

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        _build.Click += (_, _) => DoBuild();
        cancel.Click += (_, _) => Close();

        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(new TextBlock { Text = "Output packfile:", FontWeight = FontWeight.SemiBold });
        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_outPath);
        top.Children.Add(pathRow);
        top.Children.Add(blockMissing);
        top.Children.Add(_summary);
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, _build },
        };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);

        var root = new DockPanel { Margin = new Avalonia.Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = BuildTree(), Margin = new Avalonia.Thickness(0, 10) });
        Content = root;

        UpdateSummary();
    }

    /// <summary>The build result, or null if cancelled / failed.</summary>
    public PackfileBuildResult? Result { get; private set; }

    public static async Task<PackfileBuildResult?> ShowAsync(Window owner, PackfileBuildPlan plan, byte[] levelBytes)
    {
        var dlg = new PackfileDialog(plan, levelBytes);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    private Control BuildTree()
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (PackfileBuildGroup group in _plan.Groups)
        {
            var items = new StackPanel { Margin = new Avalonia.Thickness(14, 0, 0, 0) };
            foreach (PackfileBuildItem item in group.Items)
            {
                items.Children.Add(BuildRow(item));
            }

            var expander = new Expander
            {
                Header = $"{group.Kind}  ({group.Items.Count})",
                IsExpanded = group.Items.Any(i => i.Status == DependencyStatus.Missing),
                Content = items,
            };
            panel.Children.Add(expander);
        }

        return panel;
    }

    private Control BuildRow(PackfileBuildItem item)
    {
        var cb = new CheckBox
        {
            Content = item.FileName + (item.Size > 0 ? $"  ({item.Size} B)" : string.Empty),
            IsChecked = item.Include,
            IsEnabled = item.CanInclude,
            Foreground = item.Status switch
            {
                DependencyStatus.Missing => Brushes.IndianRed,
                DependencyStatus.BaseGameSkipped => Brushes.Gray,
                _ => Brushes.White,
            },
            [ToolTip.TipProperty] = Describe(item),
        };
        cb.IsCheckedChanged += (_, _) => { item.Include = cb.IsChecked == true; UpdateSummary(); };
        return cb;
    }

    private static string Describe(PackfileBuildItem item)
    {
        string status = item.Status switch
        {
            DependencyStatus.Missing => "MISSING",
            DependencyStatus.BaseGameSkipped => "base-game (skipped)",
            _ => "included",
        };
        string origins = string.Join("\n  ", item.Origins.Take(8));
        return $"{item.FileName}\n{status}\nUsed by:\n  {origins}";
    }

    private void UpdateSummary()
    {
        _plan.OutputPath = _outPath.Text ?? _plan.OutputPath;
        int missing = _plan.Scan.Missing.Count;
        _summary.Text =
            $"Included: {_plan.SelectedCount} file(s), {_plan.SelectedSize:N0} bytes.  " +
            $"Base-game skipped: {_plan.Scan.BaseGameSkipped.Count}.  Missing: {missing}.";
        _summary.Foreground = missing > 0 ? Brushes.Goldenrod : Brushes.MediumSeaGreen;
        _build.IsEnabled = _plan.CanBuild;
    }

    private async Task BrowseAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Level Packfile",
            SuggestedFileName = System.IO.Path.GetFileName(_outPath.Text ?? "level.vpp"),
            DefaultExtension = "vpp",
            FileTypeChoices = new[] { new FilePickerFileType("VPP packfile") { Patterns = new[] { "*.vpp" } } },
        });

        if (file?.TryGetLocalPath() is string path)
        {
            _outPath.Text = path;
            UpdateSummary();
        }
    }

    private void DoBuild()
    {
        _plan.OutputPath = _outPath.Text ?? _plan.OutputPath;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(_plan.OutputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            Result = _plan.Build(_levelBytes);
            Close();
        }
        catch (Exception ex)
        {
            _summary.Text = "Build failed: " + ex.Message;
            _summary.Foreground = Brushes.IndianRed;
        }
    }
}
