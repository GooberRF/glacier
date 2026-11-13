using System;

namespace Ged.Automation;

/// <summary>The stable error codes an automation command reports (<c>error.code</c> in JSON).</summary>
public static class AutomationErrorCodes
{
    /// <summary>Bad or missing arguments.</summary>
    public const string Usage = "usage";

    /// <summary>The command name is not known.</summary>
    public const string UnknownCommand = "unknown_command";

    /// <summary>The command needs an open level and there is none.</summary>
    public const string NoDocument = "no_document";

    /// <summary>A file could not be read or written.</summary>
    public const string Io = "io";

    /// <summary>A path lies outside the allowed folders.</summary>
    public const string PathNotAllowed = "path_not_allowed";

    /// <summary>The operation would discard or overwrite work and was not explicitly allowed.</summary>
    public const string DestructiveNotAllowed = "destructive_not_allowed";

    /// <summary>The script failed (syntax, runtime, API misuse, limits).</summary>
    public const string ScriptFailed = "script_failed";

    /// <summary>A build failed or reported errors.</summary>
    public const string BuildFailed = "build_failed";

    /// <summary><c>undo</c> refused: nothing to undo, or a step to undo is not a command-line run.</summary>
    public const string UndoRefused = "undo_refused";

    /// <summary>The save did not write (e.g. the pre-save re-seal could not run).</summary>
    public const string SaveFailed = "save_failed";

    /// <summary>The editor is busy with a conflicting operation (e.g. a build in flight).</summary>
    public const string Busy = "busy";

    /// <summary>The bridge request was malformed, too large, or the wrong protocol version.</summary>
    public const string Protocol = "protocol";

    /// <summary>An unexpected internal error.</summary>
    public const string Internal = "internal";

    /// <summary>The command was canceled (by the caller, or because it ran past its time limit) before it finished.</summary>
    public const string Canceled = "canceled";

    /// <summary><c>redo</c> refused: the next redo step is not one this session undid through <c>undo</c>.</summary>
    public const string RedoRefused = "redo_refused";

    /// <summary>The command or tool is not enabled for this session (read-only session, or not on the allow-list).</summary>
    public const string ToolNotAllowed = "tool_not_allowed";

    /// <summary>A rendered image stayed over the inline size cap after one smaller re-render.</summary>
    public const string ImageTooLarge = "image_too_large";

    /// <summary>A session attached to the editor found no running editor accepting connections (it retries on every call).</summary>
    public const string EditorUnavailable = "editor_unavailable";

    /// <summary>A session attached to the editor lost its connection (the editor closed or restarted); the next call reconnects if it can.</summary>
    public const string EditorDisconnected = "editor_disconnected";
}

/// <summary>A command failure with a stable <see cref="Code"/> (see <see cref="AutomationErrorCodes"/>).</summary>
public sealed class AutomationException : Exception
{
    public AutomationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public AutomationException(string code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>The stable error code.</summary>
    public string Code { get; }
}
