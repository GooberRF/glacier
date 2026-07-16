using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ged.App.Dialogs;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The brush "To Mesh" options dialog (alpine-gap-inventory: To-Mesh options). The two toggles the
/// conversion previously hard-wired on — replace-with-mesh-object and reset-origin — are present and
/// default on, matching Alpine mesh_export.cpp:507-508.
/// </summary>
public sealed class ToMeshOptionsDialogTests
{
    private static IEnumerable<Control> Walk(Control? c)
    {
        if (c is null)
        {
            yield break;
        }

        yield return c;
        switch (c)
        {
            case Panel p:
                foreach (Control child in p.Children.OfType<Control>())
                {
                    foreach (Control d in Walk(child))
                    {
                        yield return d;
                    }
                }

                break;
            case Decorator dec:
                foreach (Control d in Walk(dec.Child))
                {
                    yield return d;
                }

                break;
            case ContentControl cc when cc.Content is Control inner:
                foreach (Control d in Walk(inner))
                {
                    yield return d;
                }

                break;
        }
    }

    [AvaloniaFact]
    public void Dialog_Presents_Both_Toggles_Defaulting_On()
    {
        var dlg = new ToMeshOptionsDialog(3);
        var checks = Walk(dlg.Content as Control).OfType<CheckBox>().ToList();

        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(c.IsChecked));

        var labels = checks.Select(c => c.Content as string).ToList();
        Assert.Contains(labels, s => s!.Contains("Replace"));
        Assert.Contains(labels, s => s!.Contains("Reset origin"));
    }

    [AvaloniaFact]
    public void Result_Record_Carries_Both_Options()
    {
        var replaceOnly = new ToMeshOptionsDialog.Result(ReplaceWithMeshObject: true, ResetOrigin: false);
        Assert.True(replaceOnly.ReplaceWithMeshObject);
        Assert.False(replaceOnly.ResetOrigin);
    }
}
