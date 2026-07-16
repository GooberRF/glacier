using Avalonia.Controls;

namespace Ged.App.Panels;

/// <summary>
/// The docked, mode-scoped tool panel. Its content is swapped by the shell when
/// the editing mode changes (Brush cookie-cutter + operators, Face operations,
/// Vertex operations, or a placeholder for Object/Group/Texture).
/// </summary>
internal sealed class ModeToolPanel : UserControl
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    public ModeToolPanel()
    {
        Content = _scroll;
    }

    public void SetContent(Control? content) => _scroll.Content = content;
}
