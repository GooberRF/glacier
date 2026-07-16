using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.App.Services;
using Ged.Core;
using Ged.Core.Input;
using Ged.Core.Scripting;

namespace Ged.App;

/// <summary>
/// Scripting UI wiring (plan §6): the Script Console panel, Script Editor window, the Scripts menu,
/// palette/keymap registration of user scripts, and the bridge that hands the runner the live
/// document services (<see cref="IScriptEnvironment"/>). Built as a partial so the shared
/// <c>MainWindow.cs</c> only gains three one-line touch points (init call, dock arg, menu insert).
/// </summary>
public sealed partial class MainWindow : IScriptEnvironment
{
    private ScriptingService _scripting = null!;
    private Panels.ScriptConsolePanel _scriptConsole = null!;
    private ScriptLibrary? _scriptLibrary;
    private readonly HashSet<string> _scriptCommands = new(StringComparer.Ordinal);

    /// <summary>Called from the ctor before <c>BuildLayout()</c> — the dock references the console.</summary>
    private void InitScripting()
    {
        _scripting = new ScriptingService(this, _progress);
        _scriptConsole = new Panels.ScriptConsolePanel(_scripting, () => OpenScriptEditor());

        // Static Scripts commands.
        _dispatcher.Bind(CommandIds.ScriptConsole, () => _scriptConsole.FocusInput());
        _dispatcher.Bind(CommandIds.ScriptEditor, () => OpenScriptEditor(), () => Document is not null);
        _dispatcher.Bind(CommandIds.ScriptNew, OpenNewScript, () => Document is not null);
        _dispatcher.Bind(CommandIds.ScriptRunFile, () => _ = RunScriptFileAsync(), () => Document is not null);
        _dispatcher.Bind(CommandIds.ScriptReload, ReloadScriptLibrary);
        _dispatcher.Bind(CommandIds.ScriptApiReference, ShowApiReference);

        LoadScriptLibrary();
    }

    // ---- Scripts menu ---------------------------------------------------------

    private MenuItem BuildScriptsMenu()
    {
        var scripts = new MenuItem { Header = "_Scripts" };
        scripts.Items.Add(Cmd("Script Console", CommandIds.ScriptConsole));
        scripts.Items.Add(Cmd("Script Editor…", CommandIds.ScriptEditor));
        scripts.Items.Add(Cmd("New Script…", CommandIds.ScriptNew));
        scripts.Items.Add(Cmd("Run Script File…", CommandIds.ScriptRunFile));
        scripts.Items.Add(new Separator());

        _examplesMenu = new MenuItem { Header = "Examples" };
        scripts.Items.Add(_examplesMenu);
        _userScriptsMenu = new MenuItem { Header = "User Scripts" };
        scripts.Items.Add(_userScriptsMenu);

        scripts.Items.Add(new Separator());
        scripts.Items.Add(Cmd("Reload Scripts Library", CommandIds.ScriptReload));
        scripts.Items.Add(Cmd("Scripting API Reference", CommandIds.ScriptApiReference));
        RefreshScriptMenus();
        return scripts;
    }

    private MenuItem? _examplesMenu;
    private MenuItem? _userScriptsMenu;

    private void RefreshScriptMenus()
    {
        if (_examplesMenu is null || _userScriptsMenu is null)
        {
            return;
        }

        _examplesMenu.Items.Clear();
        _userScriptsMenu.Items.Clear();
        if (_scriptLibrary is null)
        {
            return;
        }

        foreach (ScriptLibraryEntry e in _scriptLibrary.Entries)
        {
            var item = new MenuItem { Header = e.Title };
            ScriptLibraryEntry captured = e;
            item.Click += (_, _) => RunLibraryScript(captured);
            (e.IsExample ? _examplesMenu : _userScriptsMenu).Items.Add(item);
        }

        if (_examplesMenu.Items.Count == 0)
        {
            _examplesMenu.Items.Add(new MenuItem { Header = "(none found)", IsEnabled = false });
        }

        if (_userScriptsMenu.Items.Count == 0)
        {
            _userScriptsMenu.Items.Add(new MenuItem { Header = "(add .lua files to the scripts folder)", IsEnabled = false });
        }
    }

    // ---- Library load + command/keymap registration ---------------------------

    private void LoadScriptLibrary()
    {
        try
        {
            _scriptLibrary = new ScriptLibrary(AppPaths.ScriptsDirectory);
            _scriptLibrary.Rescan();
            RegisterScriptCommands();
        }
        catch (Exception ex)
        {
            _dispatcher.ShowMessage($"Scripts library failed to load: {ex.Message}");
        }
    }

    private void RegisterScriptCommands()
    {
        if (_scriptLibrary is null)
        {
            return;
        }

        foreach (CommandDefinition def in _scriptLibrary.CommandDefinitions())
        {
            if (_scriptCommands.Contains(def.Id) || _registry.Contains(def.Id))
            {
                continue;
            }

            _registry.Register(def);
            _scriptCommands.Add(def.Id);

            ScriptLibraryEntry entry = FindEntry(def.Id);
            _dispatcher.Bind(def.Id, () => RunLibraryScript(entry), () => Document is not null);

            // Apply the script's --@key as a default binding (best-effort, no clobber of an existing one).
            if (entry.Metadata.Key is { Length: > 0 } key &&
                _keymap.Resolve(def.Id) is null &&
                KeyGesture.TryParse(key, out KeyGesture gesture))
            {
                _keymap.Rebind(def.Id, gesture);
            }
        }
    }

    private ScriptLibraryEntry FindEntry(string commandId)
    {
        foreach (ScriptLibraryEntry e in _scriptLibrary!.Entries)
        {
            if (e.Metadata.IsCommand && e.CommandId == commandId)
            {
                return e;
            }
        }

        throw new InvalidOperationException($"No script entry for command '{commandId}'.");
    }

    private void ReloadScriptLibrary()
    {
        LoadScriptLibrary();
        RefreshScriptMenus();
        _dispatcher.ShowMessage($"Scripts reloaded ({_scriptLibrary?.Entries.Count ?? 0} found).");
    }

    private void RunLibraryScript(ScriptLibraryEntry entry)
    {
        if (Document is null)
        {
            _dispatcher.ShowMessage("Open a level first.");
            return;
        }

        ScriptRunResult result = _scripting.Run(entry.Source, Path.GetFileName(entry.Path), dryRun: false, allowDestructive: false, System.Threading.CancellationToken.None);
        ReportScriptResult(entry.Title, result);
    }

    private void ReportScriptResult(string title, ScriptRunResult result)
    {
        if (result.Success)
        {
            string suffix = result.Committed ? " — 1 undo step" : string.Empty;
            _dispatcher.ShowMessage($"{title}: done{suffix}.");
        }
        else if (result.Error is { } err)
        {
            _dispatcher.ShowMessage($"{title}: {err.Message}");
        }
    }

    // ---- Editor / run-file / reference ----------------------------------------

    private void OpenScriptEditor(string? path = null, string? source = null) =>
        new Dialogs.ScriptEditorWindow(_scripting, path, source).Show(this);

    private void OpenNewScript() => OpenScriptEditor();

    private async System.Threading.Tasks.Task RunScriptFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Run Lua Script",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Lua script") { Patterns = new[] { "*.lua" } } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } local)
        {
            return;
        }

        string source = await File.ReadAllTextAsync(local);
        ScriptRunResult result = _scripting.Run(source, Path.GetFileName(local), dryRun: false, allowDestructive: false, System.Threading.CancellationToken.None);
        ReportScriptResult(Path.GetFileName(local), result);
    }

    private void ShowApiReference()
    {
        var text = new TextBox
        {
            Text = ScriptApiReference.GenerateMarkdown(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo, monospace"),
            FontSize = 12,
        };
        var win = new Window
        {
            Title = "Scripting API Reference",
            Width = 760,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = text },
        };
        win.Show(this);
    }

    // ---- IScriptEnvironment ---------------------------------------------------

    ScriptServices? IScriptEnvironment.BuildServices(IScriptProgressSink progress, IScriptConfirmation confirmation)
    {
        if (Document is null)
        {
            return null;
        }

        return new ScriptServices
        {
            Document = Document,
            Brushes = BrushEd,
            Links = _links,
            Groups = _groups,
            Assets = _session.Vfs,
            InstallDirectory = _session.RfInstallDir,
            ScanOptionsFactory = _session.BuildScanOptions,
            Progress = progress,
            Confirmation = confirmation,
            // White-brush fix for scripted brushes: level.place_box applies the editor's
            // configured per-orientation defaults through the same guard the Draw Brush uses.
            DefaultFloorTexture = _settings.DefaultFloorTexture,
            DefaultWallTexture = _settings.DefaultWallTexture,
            DefaultCeilingTexture = _settings.DefaultCeilingTexture,
        };
    }

    void IScriptEnvironment.OnScriptApplied()
    {
        RebuildScene();
        RefreshSelectionOverlay();
    }
}
