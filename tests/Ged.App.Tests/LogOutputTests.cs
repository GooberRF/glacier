using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Panels;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 4: the unified "Log output" panel accumulates tagged entries from every operation type
/// (build, lighting bake, hole check, asset reports), and the build controller routes those paths
/// to the log / report sinks with the operation named.
/// </summary>
public sealed class LogOutputTests
{
    [AvaloniaFact]
    public void Panel_Appends_Tagged_Entries_And_Retains_Prior_Ones()
    {
        var panel = new LogOutputPanel();
        Assert.Equal(string.Empty, panel.LogText);

        panel.Append("Build", "Build complete in 12 ms");
        panel.Append("Holes", "Check for holes: no leaks found.");
        panel.Append("Lighting", "Relit 40 surfaces.");

        string log = panel.LogText;
        // Every operation is tagged and earlier entries are retained (append, not replace).
        Assert.Contains("Build", log);
        Assert.Contains("Holes", log);
        Assert.Contains("Lighting", log);
        Assert.Contains("no leaks", log);

        panel.Clear();
        Assert.Equal(string.Empty, panel.LogText);
    }

    [AvaloniaFact]
    public async Task Controller_Routes_Build_Holes_And_Bake_To_The_Log_Sinks()
    {
        var session = new EditorSession();
        session.NewLevel();

        var reports = new List<(string Op, BuildReport Report)>();
        var logs = new List<(string Op, string Msg)>();
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (op, r) => reports.Add((op, r)))
        {
            Log = (op, msg) => logs.Add((op, msg)),
        };
        controller.Attach();
        session.BrushEditor!.CreateBrush(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 4f, Height = 4f, Depth = 4f },
            default, Mat3.Identity);

        // Hole check: forces a full (sealed) build, then reports leaks — a "Build" report + a
        // "Holes" log entry.
        await controller.CheckHolesAsync();

        // Lighting bake: a full build + bake — reported under the "Lighting" operation.
        await controller.CalculateMapsAndLightAsync(shadows: false);

        Assert.Contains(logs, e => e.Op == "Holes");
        Assert.Contains(reports, e => e.Op == "Build");
        Assert.Contains(reports, e => e.Op == "Lighting");
    }

    [AvaloniaFact]
    public void Default_Dock_Tool_Is_Titled_Log_Output()
    {
        Avalonia.Controls.Control C() => new Avalonia.Controls.Border();
        var factory = new Ged.App.Docking.DockFactory(C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C(), C());
        Dock.Model.Controls.IRootDock root = factory.CreateLayout();

        Dock.Model.Mvvm.Controls.Tool? build = Walk(root)
            .OfType<Ged.App.Docking.ToolHost>()
            .FirstOrDefault(t => t.Id == "build");

        Assert.NotNull(build);
        Assert.Equal("Log output", build!.Title);
    }

    private static IEnumerable<Dock.Model.Core.IDockable> Walk(Dock.Model.Core.IDockable dockable)
    {
        yield return dockable;
        if (dockable is Dock.Model.Core.IDock { VisibleDockables: { } children })
        {
            foreach (Dock.Model.Core.IDockable child in children)
            {
                foreach (Dock.Model.Core.IDockable nested in Walk(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
