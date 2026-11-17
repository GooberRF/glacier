using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Screenshot evidence of the Keyframe panel and the Level Properties tabs, for side-by-side review against Alpine
/// RED's dialogs. Runs only when asked: set GED_APP_TESTS_RENDER=1 (real Skia frames), GED_DIALOG_EVIDENCE to an
/// output folder and GED_DIALOG_LEVEL to a level with a mover; otherwise each test returns at once.
/// </summary>
public sealed class DialogScreenshotEvidence
{
    private static (string Out, string Level)? Setup()
    {
        string? output = Environment.GetEnvironmentVariable("GED_DIALOG_EVIDENCE");
        string? level = Environment.GetEnvironmentVariable("GED_DIALOG_LEVEL");
        if (Environment.GetEnvironmentVariable(TestAppBuilder.RenderVariable) != "1" || output is null || level is null || !File.Exists(level))
        {
            return null;
        }

        Directory.CreateDirectory(output);
        return (output, level);
    }

    private static void Save(Window w, string path)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(path);
    }

    [AvaloniaFact]
    public void Keyframe_Panel_On_A_Level_With_A_Mover()
    {
        if (Setup() is not (string output, string level))
        {
            return;
        }

        var session = new EditorSession();
        session.OpenLevel(level);
        EditorDocument doc = session.Document!;
        Group mover = new MoverService(doc).Movers[0];
        Keyframe second = mover.MovingData!.Keyframes[1];

        var panel = new MoverKeyframePanel(doc, mover, new[] { second });
        var dock = new Window { Width = 380, Height = 1560, Content = new Border { Padding = new Avalonia.Thickness(6), Child = new ScrollViewer { Content = panel } } };
        dock.Show();
        Save(dock, Path.Combine(output, "keyframe_panel_kf2.png"));
        dock.Close();

        var dlg = new KeyframePropertiesDialog(doc, mover, new[] { second }, new MoverPanelServices());
        dlg.Height = 1560;
        dlg.Show();
        Save(dlg, Path.Combine(output, "keyframe_dialog_kf2.png"));
        dlg.Close();
    }

    [AvaloniaFact]
    public void Level_Properties_Tabs_On_A_Level()
    {
        if (Setup() is not (string output, string level))
        {
            return;
        }

        var session = new EditorSession();
        session.OpenLevel(level);
        var dlg = new LevelPropertiesDialog(session.Document!);
        dlg.Show();
        string[] names = { "general", "lighting", "lightmaps", "gameplay", "minimap", "compatibility" };
        for (int i = 0; i < names.Length; i++)
        {
            dlg.Tabs.SelectedIndex = i;
            Save(dlg, Path.Combine(output, $"level_properties_{i + 1}_{names[i]}.png"));
        }

        dlg.Close();
        Assert.Equal(6, names.Length);
    }
}
