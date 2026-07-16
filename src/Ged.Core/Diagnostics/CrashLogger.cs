using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ged.Core.Diagnostics;

/// <summary>
/// Crash + session logging and emergency-save path resolution. The App wires the
/// AppDomain / dispatcher / unobserved-task hooks to <see cref="WriteCrashLog"/> and
/// routes background-task failures (thumbnails, builds) to <see cref="LogNonFatal"/>;
/// this type is UI-free so the log formatting and path logic are unit-testable.
///
/// Logs live in the portable <c>logs\</c> directory beside the executable by default
/// (see <see cref="AppPaths.LogsDirectory"/>; the profile fallback is
/// <c>%LOCALAPPDATA%\Glacier\logs</c>): one <c>crash-&lt;timestamp&gt;.log</c> per
/// fatal crash and a rolling <c>session.log</c> for non-fatal background failures.
/// </summary>
public sealed class CrashLogger
{
    private readonly string _logDir;
    private readonly object _sessionLock = new();

    public CrashLogger(string? logDir = null) =>
        _logDir = logDir ?? DefaultLogDirectory();

    /// <summary>The portable <c>logs\</c> directory beside the exe (or the profile fallback).</summary>
    public static string DefaultLogDirectory() => AppPaths.LogsDirectory;

    /// <summary>The directory crash/session logs are written to.</summary>
    public string LogDirectory => _logDir;

    /// <summary>
    /// Writes a timestamped crash log with the product version, the open file (or
    /// "(none)"), and the full exception chain (type / message / stack). Returns the
    /// path written, or null if even the log write failed.
    /// </summary>
    public string? WriteCrashLog(Exception exception, string version, string? openFile)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(_logDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(_logDir, $"crash-{stamp}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"{GedCoreInfo.ProductName} crash report");
            sb.AppendLine($"Time:      {DateTimeOffset.Now:O}");
            sb.AppendLine($"Version:   {version}");
            sb.AppendLine($"OS:        {Environment.OSVersion}");
            sb.AppendLine($"CLR:       {Environment.Version}");
            sb.AppendLine($"Open file: {openFile ?? "(none)"}");
            sb.AppendLine();
            AppendException(sb, exception);

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception)
        {
            return null; // never throw from the crash path
        }
    }

    /// <summary>
    /// Appends a non-fatal background failure (thumbnail render, build, autosave …)
    /// to the rolling session log instead of crashing the editor. Best-effort.
    /// </summary>
    public void LogNonFatal(string context, Exception exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDir);
            string line = $"[{DateTimeOffset.Now:O}] {context}: {exception.GetType().Name}: {exception.Message}"
                + Environment.NewLine;
            lock (_sessionLock)
            {
                File.AppendAllText(Path.Combine(_logDir, "session.log"), line);
            }
        }
        catch (Exception)
        {
            // Logging must never itself throw.
        }
    }

    /// <summary>
    /// Appends an informational line (startup stamps, backend selection …) to the rolling
    /// session log. Best-effort; never throws.
    /// </summary>
    public void LogInfo(string context, string message)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            string line = $"[{DateTimeOffset.Now:O}] {context}: {message}" + Environment.NewLine;
            lock (_sessionLock)
            {
                File.AppendAllText(Path.Combine(_logDir, "session.log"), line);
            }
        }
        catch (Exception)
        {
            // Logging must never itself throw.
        }
    }

    /// <summary>
    /// The emergency-autosave target for a crash. A saved level writes next to itself
    /// as <c>&lt;name&gt;.autosave.rfl</c> so the existing recovery prompt offers it on the
    /// next open; an unsaved level writes into <paramref name="recoveryDir"/> as a
    /// timestamped file so it is not lost.
    /// </summary>
    public static string EmergencySavePath(string? documentPath, string recoveryDir)
    {
        if (!string.IsNullOrEmpty(documentPath))
        {
            return documentPath + ".autosave.rfl";
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(recoveryDir, $"untitled-{stamp}.autosave.rfl");
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth = 0)
    {
        string indent = new(' ', depth * 2);
        sb.AppendLine($"{indent}{ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.AppendLine(ex.StackTrace);
        }

        if (ex.InnerException is Exception inner)
        {
            sb.AppendLine($"{indent}--- inner exception ---");
            AppendException(sb, inner, depth + 1);
        }
    }
}
