using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Ged.Core.IO.Mesh.Import;

namespace Ged.App.Dialogs;

/// <summary>
/// The File &gt; Import Mesh options modal: target (brushes vs mesh object), uniform
/// scale (with cm/inch presets), axis conversion (defaulted from the source format)
/// and a winding-flip toggle. Returns a populated <see cref="MeshImportOptions"/>
/// or null on cancel.
/// </summary>
internal sealed class ImportMeshDialog : Window
{
    private readonly ComboBox _target = new() { MinWidth = 220 };
    private readonly ComboBox _axis = new() { MinWidth = 220 };
    private readonly ComboBox _scalePreset = new() { MinWidth = 220 };
    private readonly TextBox _scale = new() { Text = "1.0", MinWidth = 100 };
    private readonly CheckBox _flip = new() { Content = "Flip triangle winding" };
    private MeshImportOptions? _result;

    public ImportMeshDialog(ImportedFormat format, string fileName)
    {
        Title = "Import Mesh";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _target.Items.Add("Brushes (one per material)");
        _target.Items.Add("Alpine Mesh Object (.v3m)");
        _target.SelectedIndex = 0;

        _axis.Items.Add("RF native (no conversion)");
        _axis.Items.Add("glTF / Y-up, -Z forward → RF");
        _axis.Items.Add("Z-up (Blender / FBX) → RF");
        _axis.SelectedIndex = MeshAxis.DefaultFor(format) switch
        {
            MeshAxisConversion.GltfYUp => 1,
            MeshAxisConversion.ZUp => 2,
            _ => 0,
        };

        _scalePreset.Items.Add("Custom");
        _scalePreset.Items.Add("Meters (×1.0)");
        _scalePreset.Items.Add("Centimeters (×0.01)");
        _scalePreset.Items.Add("Inches (×0.0254)");
        _scalePreset.SelectedIndex = 1;
        _scalePreset.SelectionChanged += (_, _) =>
        {
            switch (_scalePreset.SelectedIndex)
            {
                case 1: _scale.Text = "1.0"; break;
                case 2: _scale.Text = "0.01"; break;
                case 3: _scale.Text = "0.0254"; break;
                default: break;
            }
        };

        var ok = new Button { Content = "Import", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { _result = Build(); Close(); };
        cancel.Click += (_, _) => { _result = null; Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(14),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Importing {fileName}", FontWeight = Avalonia.Media.FontWeight.Bold },
                Label("Target"), _target,
                Label("Scale preset"), _scalePreset,
                Label("Scale factor"), _scale,
                Label("Axis conversion"), _axis,
                _flip,
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

    public static async Task<MeshImportOptions?> ShowAsync(Window owner, ImportedFormat format, string fileName)
    {
        var dlg = new ImportMeshDialog(format, fileName);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private static TextBlock Label(string t) => new() { Text = t, FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray };

    private MeshImportOptions Build()
    {
        float scale = float.TryParse(_scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s != 0f ? s : 1f;
        return new MeshImportOptions
        {
            Target = _target.SelectedIndex == 1 ? MeshImportTarget.MeshObject : MeshImportTarget.Brushes,
            Scale = scale,
            Axis = _axis.SelectedIndex switch
            {
                1 => MeshAxisConversion.GltfYUp,
                2 => MeshAxisConversion.ZUp,
                _ => MeshAxisConversion.RfNative,
            },
            FlipWinding = _flip.IsChecked == true,
        };
    }
}
