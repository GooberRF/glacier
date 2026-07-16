using System;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>ged</c> meta-global: API versioning (plan §5.8), scoped undo groups (§5.2), a Vec3
/// constructor, and the run-mode flags scripts can branch on. Version negotiation refuses a
/// higher-major request than the build supports with a clear message.
/// </summary>
public sealed class ScriptGed
{
    private readonly ScriptContext _ctx;

    internal ScriptGed(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>ged.api_version</c> — the API major version this build implements.</summary>
    public int ApiVersion => ScriptApiV1.Version;

    /// <summary>Lua: <c>ged.dry_run</c> — true while previewing (all changes will be rolled back).</summary>
    public bool DryRun => _ctx.DryRun;

    /// <summary>Lua: <c>ged.allow_destructive</c> — whether destructive ops are pre-authorized.</summary>
    public bool AllowDestructive => _ctx.Options.AllowDestructive;

    /// <summary>Lua: <c>ged.level_name</c> — the open document's file name, or "untitled".</summary>
    public string LevelName => _ctx.Document.Path is { Length: > 0 } p
        ? System.IO.Path.GetFileName(p)
        : "untitled";

    /// <summary>Lua: <c>ged.require_api(n)</c> — asserts the build supports API major <paramref name="major"/>.</summary>
    public void RequireApi(int major)
    {
        if (major > ScriptApiV1.Version)
        {
            throw new ScriptApiException(
                $"This script requires script API v{major}, but this build of Glacier only provides v{ScriptApiV1.Version}.",
                "Update Glacier, or lower the script's --@api requirement.");
        }
    }

    /// <summary>Lua: <c>ged.vec(x, y, z)</c> — constructs a position vector.</summary>
    public Vec3 Vec(double x, double y, double z) => new((float)x, (float)y, (float)z);

    /// <summary>
    /// Lua: <c>ged.group("name", function() … end)</c> — runs <paramref name="body"/> and labels its
    /// mutations as a scoped sub-step. Under the run's single top-level transaction this reads as a
    /// meaningful description rather than an opaque blob (plan §5.2). The body's changes are part of
    /// the same undo entry as the rest of the run.
    /// </summary>
    public void Group(string name, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _ctx.Log.Emit(ScriptLogLevel.Info, $"▸ {name}");
        body();
    }

    /// <summary>Lua: <c>ged.confirm("message")</c> — asks the confirmation gate (true/false).</summary>
    public bool Confirm(string message) => _ctx.Confirmation.Confirm("Script", message ?? string.Empty);
}
