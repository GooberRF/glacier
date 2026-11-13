using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ged.App.Services;
using Ged.Core.Model;

namespace Ged.App;

/// <summary>
/// Command-line / agent control of the open level (glacier-cli --attach): the shell side of the
/// <see cref="EditorAutomationHost"/> and the <see cref="EditorBridge"/> lifecycle — started when the
/// window opens (if Settings allows it), started/stopped live when the setting changes, and shut down
/// with the window. Commands never show dialogs; their activity is logged one line each.
/// </summary>
public sealed partial class MainWindow : IEditorAutomationShell
{
    private EditorBridge? _bridge;
    private bool _bridgeUnavailableLogged;

    EditorSession IEditorAutomationShell.Session => _session;

    GeometryBuildController? IEditorAutomationShell.BuildController => _buildController;

    ScriptingService IEditorAutomationShell.Scripting => _scripting;

    Task IEditorAutomationShell.OpenLevelAsync(string path) => OpenLevelFileAsync(path, interactive: false);

    Task IEditorAutomationShell.NewLevelAsync() => NewLevelAsync(interactive: false);

    Task<EditorSaveResult> IEditorAutomationShell.SaveToPathAsync(string path, bool saveAs) => SaveToPathAsync(path, saveAs);

    void IEditorAutomationShell.ShowHoleMarkers(IReadOnlyList<Vec3> holes) => ShowScriptHoleMarkers(holes);

    void IEditorAutomationShell.AfterExternalChange()
    {
        AfterMutation();
        RefreshStatistics();
    }

    void IEditorAutomationShell.LogActivity(string message) => LogAutomation(message);

    /// <summary>One line to the Log panel ("Command line") and the status bar; safe from any thread.</summary>
    private void LogAutomation(string message)
    {
        LogOperation("Command line", message);
        Dispatcher.UIThread.Post(() => _statusMessage.Text = message);
    }

    /// <summary>Starts or stops the bridge to match <see cref="AppSettings.EnableAutomationBridge"/>.</summary>
    private void ApplyAutomationBridgeSetting()
    {
        if (_settings.EnableAutomationBridge)
        {
            if (_bridge is not null)
            {
                return;
            }

            var bridge = new EditorBridge(new EditorAutomationHost(this), LogAutomation,
                allowAiDestructive: () => _settings.AllowDestructiveAutomation);
            if (bridge.Start())
            {
                _bridge = bridge;
                _bridgeUnavailableLogged = false;
            }
            else
            {
                _ = bridge.DisposeAsync();
                _bridgeUnavailableLogged = true; // Start() already logged the reason once
            }
        }
        else if (_bridge is { } running)
        {
            _bridge = null;
            _ = running.DisposeAsync();
            LogAutomation("Command-line connections turned off.");
        }
    }

    /// <summary>Stops the bridge (window closing).</summary>
    private void ShutdownAutomationBridge()
    {
        if (_bridge is { } running)
        {
            _bridge = null;
            try
            {
                running.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // shutting down
            }
        }
    }

    /// <summary>True when a second window found the pipe taken (test/diagnostic hook).</summary>
    internal bool AutomationBridgeUnavailable => _bridgeUnavailableLogged;
}
