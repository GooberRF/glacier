using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using Dock.Model.Controls;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Ged.App.Docking;

/// <summary>A dockable that carries the Avalonia control to display for it.</summary>
internal interface IViewHost
{
    Control? View { get; }
}

/// <summary>A tool-window dockable (Outliner, Properties, …) hosting a control.</summary>
internal sealed class ToolHost : Tool, IViewHost
{
    public Control? View { get; init; }
}

/// <summary>A document dockable (the viewport grid) hosting a control.</summary>
internal sealed class DocHost : Document, IViewHost
{
    public Control? View { get; init; }
}

/// <summary>Resolves an <see cref="IViewHost"/> dockable to its stored control.</summary>
internal sealed class PanelViewLocator : IDataTemplate
{
    public bool Match(object? data) => data is IViewHost;

    public Control? Build(object? data) => (data as IViewHost)?.View;
}

/// <summary>
/// Builds the editor's dock layout with Dock.Avalonia: a central document area
/// (the viewport grid) flanked by tool docks — Palette/Tools + Asset Browser +
/// Link Graph + Dependency Graph (left, tabbed), Properties + Outliner + History
/// (right), and a bottom console row. The layout is rearrangeable/floatable/
/// pinnable at runtime (and persisted); this method defines the DEFAULT
/// arrangement, which is also what Reset Layout restores.
/// </summary>
internal sealed class DockFactory : Factory
{
    private readonly Control _viewports;
    private readonly Control _outliner;
    private readonly Control _properties;
    private readonly Control _history;
    private readonly Control _tools;
    private readonly Control _palette;
    private readonly Control _assetBrowser;
    private readonly Control _logOutput;
    private readonly Control _linkGraph;
    private readonly Control _dependencyGraph;
    private readonly Control _lint;
    private readonly Control _statistics;
    private readonly Control _layers;
    private readonly Control _scriptConsole;

    public DockFactory(
        Control viewports, Control outliner, Control properties, Control history, Control tools,
        Control palette, Control assetBrowser, Control logOutput, Control linkGraph,
        Control dependencyGraph, Control lint, Control statistics, Control layers, Control scriptConsole)
    {
        _viewports = viewports;
        _outliner = outliner;
        _properties = properties;
        _history = history;
        _tools = tools;
        _palette = palette;
        _assetBrowser = assetBrowser;
        _logOutput = logOutput;
        _linkGraph = linkGraph;
        _dependencyGraph = dependencyGraph;
        _lint = lint;
        _statistics = statistics;
        _layers = layers;
        _scriptConsole = scriptConsole;
    }

    public override IRootDock CreateLayout()
    {
        var viewportDoc = new DocHost
        {
            Id = "viewports",
            Title = "Viewports",
            View = _viewports,
            CanClose = false,
            CanFloat = false,
        };

        var documentDock = new DocumentDock
        {
            Id = "documents",
            Title = "Documents",
            IsCollapsable = false,
            Proportion = 0.6,
            ActiveDockable = viewportDoc,
            VisibleDockables = CreateList<IDockable>(viewportDoc),
            CanCreateDocument = false,
        };

        var outliner = Tool("outliner", "Outliner", _outliner);
        var properties = Tool("properties", "Properties", _properties);
        var history = Tool("history", "History", _history);
        var tools = Tool("tools", "Tools", _tools);
        var palette = Tool("palette", "Palette", _palette);
        var assets = Tool("assets", "Asset Browser", _assetBrowser);
        var build = Tool("build", "Log output", _logOutput);
        var links = Tool("links", "Link Graph", _linkGraph);
        var deps = Tool("deps", "Dependencies", _dependencyGraph);
        var lint = Tool("lint", "Linter", _lint);
        var statistics = Tool("statistics", "Statistics", _statistics);
        var layers = Tool("layers", "Layers", _layers);
        var scripts = Tool("scriptConsole", "Script Console", _scriptConsole);

        // Default sides (item 10, amended): the Asset Browser tabs on the LEFT (with
        // the palette/tools); the Link Graph + Dependency Graph tab together at the
        // BOTTOM alongside Log output/Linter; the Outliner + Layers + Statistics join
        // Properties on the RIGHT. Users can rearrange freely — this is what a fresh
        // start and Reset Layout produce (saved user layouts are respected instead).
        var leftDock = new ToolDock
        {
            Id = "left",
            Alignment = Alignment.Left,
            Proportion = 0.2,
            ActiveDockable = palette,
            VisibleDockables = CreateList<IDockable>(palette, tools, assets),
        };

        var rightDock = new ToolDock
        {
            Id = "right",
            Alignment = Alignment.Right,
            Proportion = 0.22,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties, outliner, layers, statistics, history),
        };

        var bottomDock = new ToolDock
        {
            Id = "bottom",
            Alignment = Alignment.Bottom,
            Proportion = 0.25,
            ActiveDockable = build,
            VisibleDockables = CreateList<IDockable>(build, scripts, links, deps, lint),
        };

        var centerColumn = new ProportionalDock
        {
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                bottomDock),
        };

        var mainLayout = new ProportionalDock
        {
            Id = "main",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                leftDock,
                new ProportionalDockSplitter(),
                centerColumn,
                new ProportionalDockSplitter(),
                rightDock),
        };

        var root = CreateRootDock();
        root.Id = "root";
        root.Title = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(mainLayout);
        root.ActiveDockable = mainLayout;
        root.DefaultDockable = mainLayout;
        return root;
    }

    private static ToolHost Tool(string id, string title, Control view) => new()
    {
        Id = id,
        Title = title,
        View = view,
        CanClose = false,
    };
}
