using System;

namespace Ged.App.Services;

/// <summary>How important a notification is — drives colour/icon and the toast threshold.</summary>
public enum NotificationSeverity
{
    /// <summary>An operation failed (build/relight/save/playtest error). Lingers longer as a toast.</summary>
    Error,

    /// <summary>A refusal or recoverable problem the user should notice (build in flight, save aborted).</summary>
    Warning,

    /// <summary>A completed action / neutral status worth surfacing.</summary>
    Info,

    /// <summary>A gentle nudge (e.g. an out-of-mode selection drop). The lowest severity.</summary>
    Hint,
}

/// <summary>
/// The user-configurable toast threshold (Settings ▸ "Toast notifications"). A notification raises a
/// bottom-right toast only when its severity is at or above the configured level; every notification
/// still reaches the status bar and the Log panel regardless. The integer values are the persisted
/// setting AND the Settings-combo indices, so keep them contiguous from 0.
/// </summary>
public enum ToastLevel
{
    /// <summary>Never toast (status bar + Log only).</summary>
    Off = 0,

    /// <summary>Toast only errors.</summary>
    ErrorsOnly = 1,

    /// <summary>Toast errors and warnings.</summary>
    Warnings = 2,

    /// <summary>Toast errors, warnings and info (the default).</summary>
    Info = 3,

    /// <summary>Toast everything, including hints.</summary>
    Everything = 4,
}

/// <summary>
/// The unified notification layer. Every <see cref="Notify"/> ALWAYS writes to the status bar and the
/// Log output panel (preserving the pre-existing feedback), and ADDITIONALLY raises a bottom-right
/// toast when the severity passes the user's configured <see cref="ToastLevel"/>. The three sinks are
/// injected callbacks so the service carries no UI dependency and stays unit-testable headless: the
/// shell wires status → the command dispatcher, log → the Log panel, toast → the
/// <see cref="Ged.App.Controls.ToastHost"/>.
/// </summary>
public sealed class NotificationService
{
    private readonly Func<ToastLevel> _level;
    private readonly Action<NotificationSeverity, string> _status;
    private readonly Action<NotificationSeverity, string> _log;
    private readonly Action<NotificationSeverity, string> _toast;

    /// <summary>Builds the service over its three sinks and the live threshold provider.</summary>
    /// <param name="level">Reads the current toast threshold (typically off <see cref="AppSettings"/>).</param>
    /// <param name="status">Writes the message to the status bar (always invoked).</param>
    /// <param name="log">Appends the message to the Log panel (always invoked).</param>
    /// <param name="toast">Raises a toast card (invoked only when the severity passes the threshold).</param>
    public NotificationService(
        Func<ToastLevel> level,
        Action<NotificationSeverity, string> status,
        Action<NotificationSeverity, string> log,
        Action<NotificationSeverity, string> toast)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(toast);
        _level = level;
        _status = status;
        _log = log;
        _toast = toast;
    }

    /// <summary>Emits a notification: status bar + Log always, plus a toast when it passes the threshold.</summary>
    public void Notify(NotificationSeverity severity, string message)
    {
        message ??= string.Empty;
        _status(severity, message);
        _log(severity, message);
        if (ShouldToast(severity, _level()))
        {
            _toast(severity, message);
        }
    }

    /// <summary>
    /// True when a notification of <paramref name="severity"/> should raise a toast at the given
    /// <paramref name="level"/>. Errors clear at "Errors only" and above, warnings at "Warnings" and
    /// above, info at "Info" and above, and hints only at "Everything".
    /// </summary>
    public static bool ShouldToast(NotificationSeverity severity, ToastLevel level)
    {
        int required = severity switch
        {
            NotificationSeverity.Error => (int)ToastLevel.ErrorsOnly,
            NotificationSeverity.Warning => (int)ToastLevel.Warnings,
            NotificationSeverity.Info => (int)ToastLevel.Info,
            NotificationSeverity.Hint => (int)ToastLevel.Everything,
            _ => (int)ToastLevel.Everything,
        };
        return (int)level >= required;
    }

    /// <summary>The Log-panel tag used for a notification of this severity.</summary>
    public static string Tag(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => "Error",
        NotificationSeverity.Warning => "Warning",
        NotificationSeverity.Info => "Info",
        NotificationSeverity.Hint => "Hint",
        _ => "Notice",
    };
}
