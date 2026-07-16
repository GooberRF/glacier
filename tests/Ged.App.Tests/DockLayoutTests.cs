using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Ged.App.Docking;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 10 (amended) regression coverage: the DEFAULT dock layout (which Reset
/// Layout also restores) places the Asset Browser in the LEFT tool dock, the Link
/// Graph + Dependency Graph tabbed at the BOTTOM alongside Build Output/Linter,
/// and the Outliner in the RIGHT tool dock alongside Properties (which stays right).
/// </summary>
public class DockLayoutTests
{
    private static IRootDock BuildDefaultLayout()
    {
        Control C() => new Border();
        var factory = new DockFactory(C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C());
        return factory.CreateLayout();
    }

    private static ToolDock Dock(IRootDock root, string id)
    {
        ToolDock? found = Walk(root).OfType<ToolDock>().FirstOrDefault(d => d.Id == id);
        Assert.NotNull(found);
        return found!;
    }

    private static IEnumerable<IDockable> Walk(IDockable dockable)
    {
        yield return dockable;
        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (IDockable child in children)
            {
                foreach (IDockable nested in Walk(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string[] Ids(ToolDock dock) =>
        (dock.VisibleDockables ?? new List<IDockable>()).Select(d => d.Id).ToArray();

    [AvaloniaFact]
    public void Default_Layout_Puts_The_Asset_Browser_On_The_Left()
    {
        IRootDock root = BuildDefaultLayout();
        string[] left = Ids(Dock(root, "left"));

        Assert.Contains("assets", left);
    }

    [AvaloniaFact]
    public void Default_Layout_Tabs_The_Graphs_At_The_Bottom()
    {
        IRootDock root = BuildDefaultLayout();
        string[] bottom = Ids(Dock(root, "bottom"));

        Assert.Contains("links", bottom);
        Assert.Contains("deps", bottom);
        Assert.Contains("build", bottom); // alongside the existing console row
        Assert.Contains("scriptConsole", bottom); // the Script Console joins the bottom row
    }

    [AvaloniaFact]
    public void Default_Layout_Puts_Outliner_With_Properties_On_The_Right()
    {
        IRootDock root = BuildDefaultLayout();
        string[] right = Ids(Dock(root, "right"));

        Assert.Contains("outliner", right);
        Assert.Contains("properties", right);
    }

    [AvaloniaFact]
    public void Default_Layout_Puts_The_Layers_Panel_On_The_Right()
    {
        IRootDock root = BuildDefaultLayout();
        string[] right = Ids(Dock(root, "right"));
        Assert.Contains("layers", right); // item 9
    }

    [AvaloniaFact]
    public void Default_Layout_Puts_Statistics_On_The_Right()
    {
        IRootDock root = BuildDefaultLayout();
        string[] right = Ids(Dock(root, "right"));
        string[] bottom = Ids(Dock(root, "bottom"));

        // Item 5: Statistics defaults to the RIGHT tool dock (it left the bottom row).
        Assert.Contains("statistics", right);
        Assert.DoesNotContain("statistics", bottom);
    }

    [AvaloniaFact]
    public void Moved_Panels_Are_Not_Duplicated_Elsewhere()
    {
        IRootDock root = BuildDefaultLayout();
        string[] bottom = Ids(Dock(root, "bottom"));
        string[] left = Ids(Dock(root, "left"));

        // The asset browser left the bottom row; the outliner left the left dock;
        // the graphs live at the bottom, not the left.
        Assert.DoesNotContain("assets", bottom);
        Assert.DoesNotContain("links", left);
        Assert.DoesNotContain("deps", left);
        Assert.DoesNotContain("outliner", left);

        // Every tool appears exactly once across the layout.
        var all = Walk(root).OfType<ToolHost>().Select(t => t.Id).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
