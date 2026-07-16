using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ged.Core.Scripting;

/// <summary>
/// Reflects the <c>ScriptApiV1</c> facade into a human-readable Markdown reference, a Lua stub for
/// external editors (VS Code completion), and a canonical surface snapshot (plan §5.8, §6.3). The
/// generator is the single source of truth, so the docs cannot drift from the surface — the
/// API-surface snapshot test regenerates and compares, failing CI on any accidental change.
/// </summary>
public static class ScriptApiReference
{
    /// <summary>The bound Lua globals and the facade type behind each.</summary>
    private static readonly (string Lua, Type Type, string Summary)[] Globals =
    {
        ("ged", typeof(ScriptGed), "Meta: API versioning, scoped undo groups, run-mode flags, vec()."),
        ("level", typeof(ScriptLevel), "The open level: enumerate, look up, and place objects/brushes/events."),
        ("selection", typeof(ScriptSelection), "Query and mutate the current selection."),
        ("assets", typeof(ScriptAssets), "Texture/asset lookup, where-used, and the bulk replace_texture op."),
        ("ops", typeof(ScriptOps), "Heavy operations: build, light, check_holes, save, package, playtest. `playtest` is editor-only: it drives the interactive editor's Alpine launch flow and is a no-op outside a running editor session."),
        ("lint", typeof(ScriptLint), "Run the level linter and contribute custom findings."),
        ("log", typeof(ScriptLog), "Write to the Script Log (info/warn/error). Lua print() also lands here."),
        ("rng", typeof(ScriptRng), "Seeded, deterministic random source (reproducible procedural scripts)."),
    };

    /// <summary>Handle / query / result types returned into Lua.</summary>
    private static readonly (string Lua, Type Type, string Summary)[] Types =
    {
        ("object", typeof(ScriptObjectHandle), "A level object handle (returned by level/selection queries)."),
        ("brush", typeof(ScriptBrushHandle), "A brush handle."),
        ("face", typeof(ScriptFaceHandle), "A brush face handle."),
        ("object_query", typeof(ScriptObjectQuery), "A chainable object set: where/each/select/move/delete."),
        ("brush_query", typeof(ScriptBrushQuery), "A chainable brush set: where/each/select/move/set_texture/delete."),
        ("asset_usage", typeof(ScriptAssetUsage), "One where-used record."),
        ("lint_report", typeof(ScriptLintReport), "A lint run's merged findings + counts."),
        ("lint_finding", typeof(ScriptLintFinding), "One lint finding."),
        ("op_report", typeof(ScriptOpReport), "The outcome of a heavy operation."),
    };

    private static readonly Dictionary<Type, string> LuaTypeNames = BuildLuaTypeNames();

    /// <summary>Renders the full Markdown API reference.</summary>
    public static string GenerateMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Glacier Scripting API Reference");
        sb.AppendLine();
        sb.AppendLine($"Generated from the `ScriptApiV1` facade — **API version {ScriptApiV1.Version}**.");
        sb.AppendLine("Do not edit by hand; regenerate with the API-surface test (it is the source of truth).");
        sb.AppendLine();
        sb.AppendLine("Scripts are **Lua** (MoonSharp). The whole run is one undo step; a thrown script or a");
        sb.AppendLine("Dry-Run leaves the level untouched. The sandbox has no file/network/process access.");
        sb.AppendLine();
        sb.AppendLine("## Globals");
        sb.AppendLine();
        foreach ((string lua, Type type, string summary) in Globals)
        {
            sb.AppendLine($"### `{lua}`");
            sb.AppendLine();
            sb.AppendLine(summary);
            sb.AppendLine();
            AppendMembers(sb, type);
        }

        sb.AppendLine("## Handle & result types");
        sb.AppendLine();
        foreach ((string lua, Type type, string summary) in Types)
        {
            sb.AppendLine($"### `{lua}`");
            sb.AppendLine();
            sb.AppendLine(summary);
            sb.AppendLine();
            AppendMembers(sb, type);
        }

        return sb.ToString();
    }

    /// <summary>Renders a Lua stub (EmmyLua-style) external editors consume for completion.</summary>
    public static string GenerateLuaStub()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Glacier scripting API stub (generated). For editor completion only.");
        sb.AppendLine($"--- API version {ScriptApiV1.Version}. Do not require() this file.");
        sb.AppendLine();
        foreach ((string lua, Type type, string summary) in Globals)
        {
            sb.AppendLine($"--- {summary}");
            sb.AppendLine($"{lua} = {{}}");
            foreach (Member m in MembersOf(type))
            {
                sb.AppendLine(m.IsMethod
                    ? $"function {lua}:{m.Name}({string.Join(", ", m.Params.Select(p => p.Name))}) end  --- returns {m.ReturnType}"
                    : $"{lua}.{m.Name} = nil  --- {m.ReturnType}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>A deterministic, sorted signature of the whole surface — the drift-guard snapshot.</summary>
    public static string GenerateSurfaceSnapshot()
    {
        var lines = new List<string>();
        foreach ((string lua, Type type, _) in Globals.Concat(Types))
        {
            foreach (Member m in MembersOf(type))
            {
                string sig = m.IsMethod
                    ? $"{lua}.{m.Name}({string.Join(",", m.Params.Select(p => p.Type + (p.Optional ? "?" : string.Empty)))}):{m.ReturnType}"
                    : $"{lua}.{m.Name}:{m.ReturnType}";
                lines.Add(sig);
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The top-level Lua global names, for editor word completion.</summary>
    public static IReadOnlyList<string> GlobalNames() => Globals.Select(g => g.Lua).ToList();

    /// <summary>Maps each global (and handle/result type) name to its member names, for editor
    /// member completion after a <c>.</c> (plan §6.3, same static model that feeds the docs).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> MemberNamesByReceiver()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach ((string lua, Type type, _) in Globals.Concat(Types))
        {
            map[lua] = MembersOf(type).Select(m => m.Name).Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        return map;
    }

    private static void AppendMembers(StringBuilder sb, Type type)
    {
        foreach (Member m in MembersOf(type))
        {
            if (m.IsMethod)
            {
                string ps = string.Join(", ", m.Params.Select(p => $"{p.Name}: {p.Type}{(p.Optional ? "?" : string.Empty)}"));
                sb.AppendLine($"- `{m.Name}({ps})` → `{m.ReturnType}`");
            }
            else
            {
                sb.AppendLine($"- `{m.Name}` : `{m.ReturnType}`{(m.ReadOnly ? " *(read-only)*" : string.Empty)}");
            }
        }

        sb.AppendLine();
    }

    private static IEnumerable<Member> MembersOf(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var members = new List<Member>();

        foreach (PropertyInfo p in type.GetProperties(flags))
        {
            if (p.GetIndexParameters().Length > 0)
            {
                continue;
            }

            members.Add(new Member(ScriptNaming.ToSnakeCase(p.Name), false, LuaType(p.PropertyType),
                Array.Empty<Param>(), !p.CanWrite));
        }

        foreach (MethodInfo mi in type.GetMethods(flags))
        {
            if (mi.IsSpecialName || IsObjectMethod(mi.Name))
            {
                continue;
            }

            Param[] ps = mi.GetParameters()
                .Select(pi => new Param(ScriptNaming.ToSnakeCase(pi.Name ?? "arg"), LuaType(pi.ParameterType), pi.HasDefaultValue || pi.IsOptional))
                .ToArray();
            members.Add(new Member(ScriptNaming.ToSnakeCase(mi.Name), true, LuaType(mi.ReturnType), ps, false));
        }

        return members.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Params.Length);
    }

    private static bool IsObjectMethod(string name) =>
        name is "ToString" or "Equals" or "GetHashCode" or "GetType" or "Deconstruct";

    private static string LuaType(Type t)
    {
        Type? nullable = Nullable.GetUnderlyingType(t);
        if (nullable is not null)
        {
            return LuaType(nullable) + "?";
        }

        if (t.IsArray)
        {
            return LuaType(t.GetElementType()!) + "[]";
        }

        if (t.IsGenericType)
        {
            Type def = t.GetGenericTypeDefinition();
            if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(List<>) ||
                def == typeof(IEnumerable<>) || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
            {
                return LuaType(t.GetGenericArguments()[0]) + "[]";
            }
        }

        if (LuaTypeNames.TryGetValue(t, out string? name))
        {
            return name;
        }

        if (typeof(Delegate).IsAssignableFrom(t))
        {
            return "function";
        }

        return t.Name;
    }

    private static Dictionary<Type, string> BuildLuaTypeNames()
    {
        var map = new Dictionary<Type, string>
        {
            [typeof(void)] = "nil",
            [typeof(bool)] = "boolean",
            [typeof(string)] = "string",
            [typeof(int)] = "number",
            [typeof(long)] = "number",
            [typeof(short)] = "number",
            [typeof(byte)] = "number",
            [typeof(uint)] = "number",
            [typeof(float)] = "number",
            [typeof(double)] = "number",
            [typeof(decimal)] = "number",
            [typeof(object)] = "any",
            [typeof(Model.Vec3)] = "vec",
            [typeof(ScriptLogEntry)] = "log_entry",
        };

        foreach ((string lua, Type type, _) in Types)
        {
            map[type] = lua;
        }

        return map;
    }

    private readonly record struct Param(string Name, string Type, bool Optional);

    private readonly record struct Member(string Name, bool IsMethod, string ReturnType, Param[] Params, bool ReadOnly);
}
