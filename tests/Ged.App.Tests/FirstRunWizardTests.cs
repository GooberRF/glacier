using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ged.App;
using Ged.App.Dialogs;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 5: the first-run wizard is a fixed-width, non-resizable window, so its labels must wrap
/// (TextWrapping) rather than clip at the edge — in particular the long Alpine-launcher caption
/// that clipped at "needed to play-t" in the owner's screenshot.
/// </summary>
public sealed class FirstRunWizardTests
{
    private static FirstRunWizard NewWizard() =>
        new(new AppSettings(), Keymap.FromPreset(CommandCatalog.RedClassic));

    [AvaloniaFact]
    public void Alpine_Launcher_Label_Wraps_So_It_Never_Clips()
    {
        FirstRunWizard wizard = NewWizard();
        wizard.Show();
        wizard.UpdateLayout();

        var labels = wizard.GetVisualDescendants().OfType<TextBlock>().ToList();

        // The Alpine-launcher caption (the one that clipped) wraps to multiple lines.
        TextBlock alpine = labels.First(t => (t.Text ?? string.Empty).Contains("Alpine Faction launcher"));
        Assert.Equal(TextWrapping.Wrap, alpine.TextWrapping);

        // Sanity-check a sibling section label wraps too (the shared Label helper).
        TextBlock install = labels.First(t => (t.Text ?? string.Empty).Contains("Red Faction install folder"));
        Assert.Equal(TextWrapping.Wrap, install.TextWrapping);

        wizard.Close();
    }
}
