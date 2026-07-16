using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Ged.App.Panels;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The per-face editor (<see cref="FacePropsControl"/>) exposes only the genuinely-authored face
/// flags as editable checkboxes (full-bright / show-sky / mirrored). The five build-derived flags
/// (has-alpha / has-holes / invisible / liquid-surface / detail) — which RED generates at build
/// time from the texture and brush, not as user-set attributes — are rendered as READ-ONLY
/// indicators (disabled checkboxes with no change handler), so the user cannot set a value the
/// build owns.
/// </summary>
public sealed class FacePropsEditabilityTests
{
    private static readonly string[] Editable = { "Full-bright", "Show Sky", "Mirrored" };
    private static readonly string[] Derived = { "Has Alpha", "Has Holes", "Invisible", "Liquid Surface", "Detail" };

    private static FacePropsControl BindSelectedFace()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;

        var editor = new BrushEditor(doc);
        int uid = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        editor.SelectFace(uid, 0);

        var control = new FacePropsControl();
        control.Bind(editor, () => { });
        return control;
    }

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
            case ContentPresenter cp when cp.Content is Control inner:
                foreach (Control d in Walk(inner))
                {
                    yield return d;
                }

                break;
        }
    }

    private static Dictionary<string, CheckBox> FlagCheckBoxes(Control root) =>
        Walk(root).OfType<CheckBox>()
            .Where(cb => cb.Content is string)
            .GroupBy(cb => (string)cb.Content!)
            .ToDictionary(gp => gp.Key, gp => gp.First());

    [AvaloniaFact]
    public void Authored_Flags_Are_Editable_And_Build_Derived_Flags_Are_Read_Only()
    {
        Dictionary<string, CheckBox> boxes = FlagCheckBoxes(BindSelectedFace());

        foreach (string label in Editable)
        {
            Assert.True(boxes.ContainsKey(label), $"missing editable flag '{label}'");
            Assert.True(boxes[label].IsEnabled, $"'{label}' is authored and must stay user-editable");
        }

        foreach (string label in Derived)
        {
            Assert.True(boxes.ContainsKey(label), $"missing read-only indicator '{label}'");
            Assert.False(boxes[label].IsEnabled, $"'{label}' is build-derived and must be read-only");
        }
    }
}
