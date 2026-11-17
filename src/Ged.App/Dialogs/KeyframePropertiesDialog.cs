using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ged.App.Panels;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.App.Dialogs;

/// <summary>
/// Keyframe Properties as a window (double-click a keyframe, as RED opens its dialog): the same two-part content as
/// the docked Keyframe panel, editing live with one undo step per change.
/// </summary>
internal sealed class KeyframePropertiesDialog : Window
{
    public KeyframePropertiesDialog(EditorDocument doc, Group mover, IReadOnlyList<Keyframe> selected, MoverPanelServices services)
    {
        Title = "Keyframe Properties";
        Width = 470;
        Height = 760;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Panel = new MoverKeyframePanel(doc, mover, selected, services);
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
        close.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = Panel });
        Content = root;
    }

    /// <summary>The keyframe panel this window hosts.</summary>
    public MoverKeyframePanel Panel { get; }
}
