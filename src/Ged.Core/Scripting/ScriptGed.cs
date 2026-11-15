using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>ged</c> meta-global: API versioning, scoped undo groups, a Vec3
/// constructor, and the run-mode flags scripts can branch on. Version negotiation refuses a
/// higher-major request than the build supports with a clear message.
/// </summary>
public sealed class ScriptGed
{
    private readonly ScriptContext _ctx;

    internal ScriptGed(ScriptContext ctx) => _ctx = ctx;

    /// <summary>Lua: <c>ged.api_version</c> — the API major version this build implements.</summary>
    [ScriptDoc("The script API major version this build implements (2).", Example = "if ged.api_version < 2 then error(\"needs API 2\") end")]
    public int ApiVersion => ScriptApiV1.Version;

    /// <summary>Lua: <c>ged.dry_run</c> — true while previewing (all changes will be rolled back).</summary>
    [ScriptDoc("True while previewing (Dry-Run): every change is rolled back afterwards and destructive ops are refused.", Example = "if ged.dry_run then log.info(\"preview only\") end")]
    public bool DryRun => _ctx.DryRun;

    /// <summary>Lua: <c>ged.allow_destructive</c> — whether destructive ops are pre-authorized.</summary>
    [ScriptDoc("True when destructive ops (delete, save, package, playtest) are pre-authorized for this run (the --@allow-destructive header).", Example = "if not ged.allow_destructive then return end")]
    public bool AllowDestructive => _ctx.Options.AllowDestructive;

    /// <summary>Lua: <c>ged.level_name</c> — the open document's file name, or "untitled".</summary>
    [ScriptDoc("The open level's file name, or \"untitled\" for a level that has never been saved.", Example = "log.info(ged.level_name)")]
    public string LevelName => _ctx.Document.Path is { Length: > 0 } p
        ? System.IO.Path.GetFileName(p)
        : "untitled";

    /// <summary>Lua: <c>ged.require_api(n)</c> — asserts the build supports API major <paramref name="major"/>.</summary>
    [ScriptDoc("Errors (with an update hint) unless this build supports script API major version n.", Example = "ged.require_api(2)")]
    public void RequireApi(int major)
    {
        if (major > ScriptApiV1.Version)
        {
            throw new ScriptApiException(
                $"This script requires script API v{major}, but this build of Glacier only provides v{ScriptApiV1.Version}.",
                "Update Glacier, or lower the script's --@api requirement.");
        }
    }

    /// <summary>
    /// Lua: <c>ged.fail(message [, hint])</c> — raises an API error, exactly like a misused built-in
    /// function: the run fails with an Api diagnostic located at the calling script's line (the first
    /// caller outside an imported module) and carrying <paramref name="hint"/> in its own field. This is
    /// how Lua modules such as the design kit report misuse.
    /// </summary>
    [ScriptDoc("Raises an API error with an optional one-line hint; the error is reported at the line of your script that called into the module (like a built-in function's error). Never returns.",
        Example = "if w <= 0 then ged.fail(\"mykit.room: 'w' must be > 0.\", \"Pass a positive width.\") end", Since = 2)]
    public void Fail(string message, string? hint = null)
    {
        string text = string.IsNullOrWhiteSpace(message) ? "ged.fail: a message is required." : message;
        throw new ScriptApiException(text, string.IsNullOrWhiteSpace(hint) ? null : hint);
    }

    /// <summary>Lua: <c>ged.vec(x, y, z)</c> — constructs a position vector.</summary>
    [ScriptDoc("Constructs a vec in metres (x right, y up, z forward). Returns the vec.", Example = "local p = ged.vec(0, 2, 8)")]
    public Vec3 Vec(double x, double y, double z) => new((float)x, (float)y, (float)z);

    /// <summary>
    /// Lua: <c>ged.group("name", function() … end)</c> — runs <paramref name="body"/> and labels its
    /// mutations as a scoped sub-step. Under the run's single top-level transaction this reads as a
    /// meaningful description rather than an opaque blob. The body's changes are part of
    /// the same undo entry as the rest of the run.
    /// </summary>
    [ScriptDoc("Runs fn and labels its changes as a named step in the Script Log; they stay part of the run's single undo step.", Example = "ged.group(\"walls\", function() level.brush{pos = {0, 2, 0}} end)")]
    public void Group(string name, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _ctx.Log.Emit(ScriptLogLevel.Info, $"▸ {name}");
        body();
    }

    /// <summary>
    /// Lua: <c>ok, result = ged.try(fn, ...)</c> — calls <paramref name="fn"/> with the extra arguments and returns
    /// <c>true</c> plus fn's return values; when fn raises an error (a Lua <c>error</c>, a failed API call,
    /// <c>ged.fail</c>), every level change fn made is undone and it returns <c>false</c> plus an error table
    /// <c>{message, hint, kind, chunk, line}</c> (<c>tostring(err)</c> gives the message and hint). A time limit,
    /// instruction budget, Stop or callback-nesting abort is never caught: it still ends the whole run. The Lua
    /// engine binds its own multi-value version (see <c>MoonSharpSession</c>); this engine-neutral body runs
    /// <paramref name="fn"/> under the same rollback scope and recovers from API errors.
    /// </summary>
    [ScriptDoc("Calls fn(...) and returns true and fn's results, or false and an error {message, hint, kind} after undoing every level change fn made. Time limits and Stop still end the run.",
        Example = "local ok, err = ged.try(kit.cave, { name = \"c\", path = pts, seed = s })", Since = 2)]
    public bool Try(Action fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return _ctx.RunRecoverable(fn, ex => ex is ScriptApiException, out _);
    }

    /// <summary>
    /// Lua: <c>ged.names(list)</c> — a list as plain strings, so <c>table.concat</c> and string functions work on lists of
    /// records and handles (texture_info, field_info, class_info, objects, ...), which Lua cannot concatenate: a string
    /// stays itself, a number or boolean is written out, a record or handle gives its <c>name</c> (a class_info its
    /// <c>class_name</c>), a table its <c>name</c> field, anything else its <c>tostring</c> text.
    /// </summary>
    [ScriptDoc("A list as plain strings for table.concat: strings stay, numbers and booleans are written out, records and handles give their name (class_info its class_name), tables their name field.",
        Example = "print(table.concat(ged.names(catalog.textures{filter = \"rock\", stock = true}), \", \"))", Since = 2)]
    public string[] Names(ScriptArgs list)
    {
        if (list is null)
        {
            throw new ScriptApiException("ged.names: a list is required.", "For example ged.names(o:fields()).");
        }

        return list.Items.Select(NameOf).ToArray();
    }

    private static string NameOf(object? v)
    {
        switch (v)
        {
            case null:
                return "nil";
            case string s:
                return s;
            case double d:
                return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture) : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case bool b:
                return b ? "true" : "false";
            case ScriptArgs t:
                return t.Named.TryGetValue("name", out object? n) && n is string ns ? ns : "table";
        }

        foreach (string prop in new[] { "Name", "ClassName" })
        {
            if (v.GetType().GetProperty(prop, typeof(string)) is { CanRead: true } p && p.GetValue(v) is string s && s.Length > 0)
            {
                return s;
            }
        }

        return v.ToString() ?? string.Empty;
    }

    /// <summary>Lua: <c>ged.confirm("message")</c> — asks the confirmation gate (true/false).</summary>
    [ScriptDoc("Asks the user to confirm message and returns true or false; always false in Dry-Run, always true with --@allow-destructive.", Example = "if ged.confirm(\"Delete all lights?\") then log.info(\"ok\") end")]
    public bool Confirm(string message) => _ctx.Confirmation.Confirm("Script", message ?? string.Empty);

    /// <summary>Lua: <c>ged.rot(yaw [, pitch [, roll]])</c> — constructs a rotation (degrees).</summary>
    [ScriptDoc("Constructs a rotation from degrees (pitch and roll default to 0). Returns a rotation record.", Example = "local r = ged.rot(90) -- faces +X", Since = 2)]
    public ScriptRotation Rot(double yaw, double pitch = 0, double roll = 0)
    {
        if (!double.IsFinite(yaw) || !double.IsFinite(pitch) || !double.IsFinite(roll))
        {
            throw new ScriptApiException("ged.rot: angles must be finite numbers (degrees).");
        }

        return new ScriptRotation(ScriptRotation.FromEuler(yaw, pitch, roll));
    }

    /// <summary>
    /// Lua: <c>ged.import(name)</c> — loads a Lua module: a module shipped with Glacier
    /// (e.g. <c>kit</c>), else a module beside the script when it was run from a file, else
    /// <c>&lt;scripts&gt;/lib/&lt;name&gt;.lua</c>. The module runs once per session in the
    /// same sandbox, budget and undo step as the script; later imports return the cached value.
    /// </summary>
    [ScriptDoc("Loads a Lua module once per session and returns its value: shipped modules (\"kit\") first, then <name>.lua beside a script run from a file (literal names), then <scripts>/lib/<name>.lua.",
        Example = "local kit = ged.import(\"kit\")", Since = 2)]
    public object? Import(string name)
    {
        if (!ScriptModuleResolver.IsValidName(name))
        {
            throw new ScriptApiException($"Invalid module name '{name}'.",
                "Module names are lower-case letters, digits and '_' (starting with a letter), with no path or extension — e.g. ged.import(\"kit\").");
        }

        if (name == "kit_core")
        {
            return _ctx.KitCore; // the design kit's native engine (used by the shipped kit modules)
        }

        if (_ctx.ModuleCache.TryGetValue(name, out object? cached))
        {
            return cached;
        }

        if (_ctx.ImportStack.Contains(name, StringComparer.Ordinal))
        {
            var chain = _ctx.ImportStack.SkipWhile(n => !string.Equals(n, name, StringComparison.Ordinal)).Append(name);
            throw new ScriptApiException($"Circular import: {string.Join(" → ", chain)}.",
                "Move the shared code into a third module that both import, or import lazily inside a function.");
        }

        IScriptModuleHost host = _ctx.ModuleHost
            ?? throw new ScriptApiException("ged.import: this script host cannot load modules.");
        ScriptModuleSource module = ScriptModuleResolver.Resolve(name, _ctx.Services.ModuleDirectory, _ctx.Services.LocalModules);

        _ctx.ImportStack.Add(name);
        object? value;
        try
        {
            value = host.Import(module.Name, module.Source, module.ChunkName);
        }
        finally
        {
            _ctx.ImportStack.RemoveAt(_ctx.ImportStack.Count - 1);
        }

        _ctx.ModuleCache[name] = value;
        return value;
    }

    /// <summary>Lua: <c>ged.conventions</c> — units, axes, rotation, CSG and undo rules in plain text.</summary>
    [ScriptDoc("The scripting conventions as plain text: units, axes, rotation, CSG time order, texture roles, undo, hole checks.", Example = "print(ged.conventions)", Since = 2)]
    public string Conventions => ScriptConventions.Text;

    /// <summary>
    /// Lua: <c>ged.describe(x)</c> — the members of a type or global: <paramref name="x"/> is a type
    /// or global name (<c>"brush"</c>, <c>"level"</c>) or any handle/record/global value.
    /// </summary>
    [ScriptDoc("Describes a type or global (a name like \"brush\", or any handle/record/global value): members, signatures and docs; a handle's text also lists its fields and values. Returns type_info.", Example = "print(ged.describe(\"brush\").text)", Since = 2)]
    public ScriptTypeInfo Describe(object? x)
    {
        ScriptTypeInfo? info = x switch
        {
            null => null,
            string name => ScriptApiReference.Describe(name),
            _ => ScriptApiReference.Describe(x.GetType()),
        };

        if (info is not null)
        {
            // A live handle also lists its own fields with their current values.
            ScriptFieldSet? fields = x switch
            {
                ScriptObjectHandle o => o.FieldSet(),
                ScriptBrushHandle b when _ctx.Brushes.FindBrush(b.Uid) is not null => ScriptFieldCatalog.ForBrush(_ctx, b.Uid),
                ScriptMoverHandle m when m.Exists => m.FieldSet(),
                _ => null,
            };
            return fields is null
                ? info
                : new ScriptTypeInfo(info.Name, info.Summary, info.Members, info.Text + "\n" + fields.Listing());
        }

        IReadOnlyList<string> names = ScriptApiReference.DescribableNames();
        string what = x is string s ? $"'{s}'" : $"a {ScriptArgs.LuaTypeOf(x)} value";
        throw new ScriptApiException($"ged.describe: unknown type or global {what}.",
            x is string input ? ScriptSuggest.Hint(input, names, "name") : $"Valid names: {string.Join(", ", names)}.");
    }
}
