using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ged.Core.Diagnostics;

namespace Ged.App;

/// <summary>
/// Process-wide crash + background-failure handling. Installs the AppDomain,
/// dispatcher (Avalonia UI thread) and unobserved-task hooks: a fatal exception
/// writes a crash log and attempts an emergency autosave of the open document
/// before the process exits (recovered by the autosave prompt on the next launch);
/// a background-task fault is logged to the session log instead of crashing.
/// </summary>
internal static class CrashHandler
{
    private static readonly CrashLogger Logger = new();
    private static Func<(byte[]? Bytes, string? Path)>? _documentProvider;
    private static bool _fatalHandled;
    private static readonly object Gate = new();

    /// <summary>The shared logger — background tasks route non-fatal failures here.</summary>
    public static CrashLogger Log => Logger;

    /// <summary>
    /// Installs the global handlers. <paramref name="documentProvider"/> returns the
    /// current document's serialized bytes and file path (or nulls) for the emergency
    /// save; it must never throw.
    /// </summary>
    public static void Install(Func<(byte[]?, string?)> documentProvider)
    {
        _documentProvider = documentProvider;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                HandleFatal(ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogNonFatal("unobserved-task", e.Exception);
            e.SetObserved(); // a background task fault must not escalate to a crash
        };

        // Avalonia UI-thread exceptions: log + emergency-save, then let it propagate
        // (Handled stays false) so the process still exits with the report written.
        Dispatcher.UIThread.UnhandledException += (_, e) => HandleFatal(e.Exception);
    }

    /// <summary>Records a non-fatal background failure (thumbnails, builds, autosave).</summary>
    public static void LogNonFatal(string context, Exception ex) => Logger.LogNonFatal(context, ex);

    /// <summary>Records an informational startup/diagnostic line to the session log.</summary>
    public static void LogInfo(string context, string message) => Logger.LogInfo(context, message);

    private static void HandleFatal(Exception ex)
    {
        lock (Gate)
        {
            if (_fatalHandled)
            {
                return; // the same crash can surface on both the dispatcher and the domain
            }

            _fatalHandled = true;
        }

        (byte[]? bytes, string? path) = SafeDocument();
        Logger.WriteCrashLog(ex, AppVersion.Informational, path);

        if (bytes is not null)
        {
            try
            {
                string recoveryDir = Ged.Core.AppPaths.RecoveryDirectory;
                Directory.CreateDirectory(recoveryDir);
                string target = CrashLogger.EmergencySavePath(path, recoveryDir);
                File.WriteAllBytes(target, bytes);
            }
            catch (Exception saveEx)
            {
                Logger.LogNonFatal("emergency-save", saveEx);
            }
        }
    }

    private static (byte[]?, string?) SafeDocument()
    {
        try
        {
            return _documentProvider?.Invoke() ?? (null, null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }
}
