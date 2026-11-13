using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Ged.Core.Scripting;

/// <summary>
/// Reflects the <c>ScriptApiV1</c> facade into a human-readable Markdown reference, a Lua stub for
/// external editors (VS Code completion), and a canonical surface snapshot. The
/// generator is the single source of truth, so the docs cannot drift from the surface — the
/// API-surface snapshot test regenerates and compares, failing CI on any accidental change.
/// Member documentation comes from <see cref="ScriptDocAttribute"/>; the same model backs
/// <c>ged.describe</c> (<see cref="Describe(Type)"/>).
/// </summary>
public static class ScriptApiReference
{
    /// <summary>The bound Lua globals and the facade type behind each.</summary>
    private static readonly (string Lua, Type Type, string Summary)[] Globals =
    {
        ("ged", typeof(ScriptGed), "Meta: API versioning, module loading (import), scoped undo groups, run-mode flags, vec() / rot() constructors, describe() and the conventions text."),
        ("level", typeof(ScriptLevel), "The open level: enumerate, look up, and place objects/brushes/events; create brushes with level.brush{…} or as convex hulls of points with level.brush_from_points{…}; import models with level.import_mesh{…}; spatial and predicate brush/object queries."),
        ("selection", typeof(ScriptSelection), "Query and mutate the current selection (objects, brushes, faces)."),
        ("assets", typeof(ScriptAssets), "Texture/asset lookup, where-used (where_used_by), texture provenance of the level (texture_sources), and the bulk replace_texture op."),
        ("ops", typeof(ScriptOps), "Heavy operations: build, light, check_holes / holes, save, package, playtest. Builds use the same options as the editor's own Build / Calculate Lighting (the user's build method, Repair Holes When Building setting and lightmap method in the editor; the editor defaults elsewhere); hole checks always measure a sealed build and count what remains after that build's repair (they never repair on their own). Build, light, save and package first refresh the mover copies of member brushes the script edited. `playtest` saves the level and launches it through the editor's Play flow once the script finishes; outside the interactive editor it raises an error."),
        ("lint", typeof(ScriptLint), "Run the level linter and contribute custom findings."),
        ("log", typeof(ScriptLog), "Write to the Script Log (info/warn/error). Lua print() also lands here."),
        ("rng", typeof(ScriptRng), "Seeded, deterministic random source (reproducible procedural scripts). Re-seed with rng.set_seed(n); rng.seed reads the current seed."),
        ("catalog", typeof(ScriptCatalog), "Discover what can be placed: kinds, classes (from the game tables when an RF install is mounted, and the event catalog), each class's fields, textures with their provenance (stock, origin, source), and nearest-name lookups."),
        ("prefab", typeof(ScriptPrefabs), "The prefab library (the editor's prefab folder): list the .gedprefab files and place tracked instances — the same placement as dropping a prefab in the editor. Files are only ever read from the library folder."),
        ("geo", typeof(ScriptGeo), "Geometry tools: check, roughen and deform brushes (results stay convex and planar), extrude/bevel faces, weld/snap vertices, and builders for organic brush chains (tube, passage, rock_row, scatter, pit). Build convex shapes with level.brush_from_points{…}."),
        ("spatial", typeof(ScriptSpatial), "Spatial queries on the compiled geometry (build first): raycast, los, point_info and clearance (instant), and the measurements behind the design grader: walk (reachable floor, movement links), routes (k node-disjoint routes), graph (cell / space graphs with loops, chokepoints, pockets), sightlines and exposure, cover, cells (undressed / bare), area_report (vs the exemplar targets), spawn_report, item_report, locations, setpieces (bridge spans, landmark heights, atria). Every result carries geometry freshness; measurements are cached and budgeted (max_samples, timeout_s)."),
        ("design", typeof(ScriptDesign), "The self-review grader: design.grade{} returns PASS / REVIEW / FAIL with each failing check's values, fix hint and locations (the same code as glacier-cli grade and the grade_level tool); design.checks() lists the checks."),
    };

    /// <summary>Handle / query / result types returned into Lua.</summary>
    private static readonly (string Lua, Type Type, string Summary)[] Types =
    {
        ("object", typeof(ScriptObjectHandle), "A level object handle (returned by level/selection queries). Generic fields: o:get/set/set_many/fields/props; V2 setters return the handle."),
        ("brush", typeof(ScriptBrushHandle), "A brush handle. V2 setters return the handle, so edits chain: b:set_pos(0, 2, 0):set_life(50)."),
        ("face", typeof(ScriptFaceHandle), "A brush face handle: world geometry reads and UV mapping (UV ops return the face)."),
        ("object_query", typeof(ScriptObjectQuery), "A chainable object set: where/each/select/move/delete."),
        ("brush_query", typeof(ScriptBrushQuery), "A chainable brush set: where/each/select/move/set_texture/delete, plus bulk UV, texture-role, flag and time-order edits (one undo node each)."),
        ("asset_usage", typeof(ScriptAssetUsage), "One where-used record."),
        ("lint_report", typeof(ScriptLintReport), "A lint run's merged findings + counts."),
        ("lint_finding", typeof(ScriptLintFinding), "One lint finding."),
        ("op_report", typeof(ScriptOpReport), "The outcome of a heavy operation."),
        ("hole_report", typeof(ScriptHoleReport), "The result of ops.holes(): leak count and world-space positions from a sealed build."),
        ("repair_report", typeof(ScriptRepairReport), "The hole-repair counters of a build (op_report.repair): Alpine RED's build-repair figures."),
        ("rotation", typeof(ScriptRotation), DocSummary(typeof(ScriptRotation))),
        ("aabb", typeof(ScriptAabb), DocSummary(typeof(ScriptAabb))),
        ("uv", typeof(ScriptUv), DocSummary(typeof(ScriptUv))),
        ("type_info", typeof(ScriptTypeInfo), DocSummary(typeof(ScriptTypeInfo))),
        ("member_info", typeof(ScriptMemberInfo), DocSummary(typeof(ScriptMemberInfo))),
        ("option_info", typeof(ScriptOptionInfo), DocSummary(typeof(ScriptOptionInfo))),
        ("field_info", typeof(ScriptFieldInfo), DocSummary(typeof(ScriptFieldInfo))),
        ("class_info", typeof(ScriptClassInfo), DocSummary(typeof(ScriptClassInfo))),
        ("link_report", typeof(ScriptLinkReport), DocSummary(typeof(ScriptLinkReport))),
        ("link_rejection", typeof(ScriptLinkRejection), DocSummary(typeof(ScriptLinkRejection))),
        ("props", typeof(ScriptPropertyBag), DocSummary(typeof(ScriptPropertyBag))),
        ("mover", typeof(ScriptMoverHandle), DocSummary(typeof(ScriptMoverHandle))),
        ("group", typeof(ScriptGroupHandle), DocSummary(typeof(ScriptGroupHandle))),
        ("prefab_info", typeof(ScriptPrefabInfo), DocSummary(typeof(ScriptPrefabInfo))),
        ("prefab_instance", typeof(ScriptPrefabInstance), DocSummary(typeof(ScriptPrefabInstance))),
        ("geo_report", typeof(ScriptGeoReport), DocSummary(typeof(ScriptGeoReport))),
        ("texture_info", typeof(ScriptTextureInfo), DocSummary(typeof(ScriptTextureInfo))),
        ("norm_info", typeof(ScriptNormInfo), DocSummary(typeof(ScriptNormInfo))),
        ("mesh_import", typeof(ScriptMeshImport), DocSummary(typeof(ScriptMeshImport))),
        ("tube", typeof(ScriptTube), DocSummary(typeof(ScriptTube))),
        ("tube_station", typeof(ScriptTubeStation), DocSummary(typeof(ScriptTubeStation))),
        ("tube_climb", typeof(ScriptTubeClimb), DocSummary(typeof(ScriptTubeClimb))),
        ("rocks", typeof(ScriptRocks), DocSummary(typeof(ScriptRocks))),
        ("pit", typeof(ScriptPit), DocSummary(typeof(ScriptPit))),
    };

    /// <summary>Value types that are describable but are not part of the pinned surface listing.</summary>
    private static readonly (string Lua, Type Type, string Summary)[] ValueTypes =
    {
        ("vec", typeof(Model.Vec3), "A 3D vector in metres: x, y, z. Construct with ged.vec(x, y, z) or pass {x, y, z} to table arguments."),
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
        sb.AppendLine("Members introduced in API version 2 are marked *(v2)*.");
        sb.AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine();
        sb.AppendLine("The same text is available to scripts as `ged.conventions`.");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.Append(ScriptConventions.Text);
        sb.AppendLine("```");
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

        AppendDesignKit(sb);
        return sb.ToString();
    }

    /// <summary>
    /// One documented design-kit function, parsed from its <c>---@kit</c> line in <c>kit.lua</c>, with the
    /// option table keys from the <c>---@opt</c> lines that follow it.
    /// </summary>
    public sealed record KitFunctionDoc(string Name, string Signature, string Description, IReadOnlyList<ScriptOption> Options);

    private static readonly System.Text.RegularExpressions.Regex KitOptLine =
        new(@"^---@opt\s+(?<name>[a-z_]+):\s+(?<type>.+?)(?:\s+=\s+(?<def>.+?))?\s+—\s+(?<doc>.+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the design kit's doc comments: a <c>---@kit &lt;signature&gt; — &lt;description&gt;</c> line documents
    /// the <c>function kit.&lt;name&gt;(</c> that follows it, and each <c>---@opt name: type [= default] — meaning</c>
    /// line between them documents one key of its option table. Functions without a <c>---@kit</c> line are
    /// left out (the documentation test asserts every function has one, and that the <c>---@opt</c> keys are
    /// exactly the keys the function accepts).
    /// </summary>
    public static IReadOnlyList<KitFunctionDoc> ParseKitDocs(string source)
    {
        var docs = new List<KitFunctionDoc>();
        string? pending = null;
        var options = new List<ScriptOption>();
        foreach (string raw in (source ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("---@kit ", StringComparison.Ordinal))
            {
                pending = line.Substring("---@kit ".Length).Trim();
                options.Clear();
                continue;
            }

            if (pending is not null && KitOptLine.Match(line) is { Success: true } opt)
            {
                options.Add(new ScriptOption(opt.Groups["name"].Value, opt.Groups["type"].Value.Trim(),
                    opt.Groups["def"].Success ? opt.Groups["def"].Value.Trim() : null, opt.Groups["doc"].Value.Trim()));
                continue;
            }

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"^function kit\.([a-z_]+)\(");
            if (!m.Success)
            {
                continue;
            }

            string name = m.Groups[1].Value;
            if (pending is not null && pending.StartsWith("kit." + name, StringComparison.Ordinal))
            {
                int dash = pending.IndexOf(" — ", StringComparison.Ordinal);
                ScriptOption[] opts = options.ToArray();
                docs.Add(dash < 0
                    ? new KitFunctionDoc(name, pending, string.Empty, opts)
                    : new KitFunctionDoc(name, pending.Substring(0, dash).Trim(), pending.Substring(dash + 3).Trim(), opts));
            }

            pending = null;
            options.Clear();
        }

        return docs;
    }

    /// <summary>The design kit described like a global: one member per kit function, its option keys included; null when no kit is shipped.</summary>
    private static ScriptTypeInfo? DescribeKit()
    {
        if (ScriptKitSource.Combined() is not { } source)
        {
            return null;
        }

        const string Summary = "The design kit (local kit = ged.import(\"kit\")): floor-anchored level-building functions; each takes one option table and rejects unknown keys. Tunables in kit.defaults, limits in kit.limits.";
        var members = new List<ScriptMemberInfo>();
        var text = new StringBuilder();
        text.Append("kit — ").AppendLine(Summary);
        foreach (KitFunctionDoc doc in ParseKitDocs(source))
        {
            members.Add(new ScriptMemberInfo(doc.Name, "function", doc.Signature, "table", doc.Description, string.Empty, 2,
                doc.Options.Select(o => new ScriptOptionInfo(o)).ToArray()));
            text.Append("  ").Append(doc.Signature).Append(" — ").AppendLine(doc.Description);
            if (doc.Options.Count > 0)
            {
                AppendOptionText(text, new[] { new ScriptOptionSet("kit." + doc.Name, doc.Options.ToArray()) }, "      ");
            }
        }

        return new ScriptTypeInfo("kit", Summary, members.ToArray(), text.ToString().TrimEnd('\r', '\n'));
    }

    private static void AppendDesignKit(StringBuilder sb)
    {
        if (ScriptKitSource.Combined() is not { } kit)
        {
            return;
        }

        sb.AppendLine("## Design kit");
        sb.AppendLine();
        sb.AppendLine("`local kit = ged.import(\"kit\")` — level-building functions written in Lua over this API");
        sb.AppendLine("(`scripts/lib/kit.lua`). Every `pos` is a point on the floor; sizes are interior clear sizes;");
        sb.AppendLine("each function takes one table, rejects unknown keys, tags its brushes `kit` / `kit.name` /");
        sb.AppendLine("`kit.part`, names its objects, and groups multi-brush parts. Tunables live in `kit.defaults`,");
        sb.AppendLine("hard limits in `kit.limits`.");
        sb.AppendLine();
        foreach (KitFunctionDoc doc in ParseKitDocs(kit))
        {
            sb.AppendLine($"- `{doc.Signature}` — {doc.Description}");
            if (doc.Options.Count > 0)
            {
                AppendOptionMarkdown(sb, new[] { new ScriptOptionSet("kit." + doc.Name, doc.Options.ToArray()) });
            }
        }

        sb.AppendLine();
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
                if (m.Doc is { } doc)
                {
                    sb.AppendLine($"--- {doc.Summary}");
                }

                foreach (ScriptOptionSet set in m.Options)
                {
                    foreach (ScriptOption o in set.Options)
                    {
                        sb.AppendLine($"---   {FormatOption(o)}");
                    }
                }

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
                lines.Add(SnapshotLine(lua, m));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The top-level Lua global names, for editor word completion.</summary>
    public static IReadOnlyList<string> GlobalNames() => Globals.Select(g => g.Lua).ToList();

    /// <summary>Every describable global and type name (globals first, then types), for <c>ged.describe</c>.</summary>
    public static IReadOnlyList<string> DescribableNames()
    {
        var names = Globals.Concat(Types).Concat(ValueTypes).Select(g => g.Lua).ToList();
        if (ScriptModuleResolver.ShippedSource("kit") is not null)
        {
            names.Add("kit");
        }

        return names;
    }

    /// <summary>Maps each global (and handle/result type) name to its member names, for editor
    /// member completion after a <c>.</c> (the same static model that feeds the docs).</summary>
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

    /// <summary>
    /// Describes a registered global/type by its Lua name (<c>"brush"</c>, <c>"level"</c>, …),
    /// matched case-insensitively; null when the name is unknown.
    /// </summary>
    public static ScriptTypeInfo? Describe(string name)
    {
        if (string.Equals(name?.Trim(), "kit", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeKit();
        }

        foreach ((string lua, Type type, string summary) in Globals.Concat(Types).Concat(ValueTypes))
        {
            if (string.Equals(lua, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Build(lua, type, summary);
            }
        }

        return null;
    }

    /// <summary>Describes the registered global/type backed by CLR type <paramref name="type"/>; null when unregistered.</summary>
    public static ScriptTypeInfo? Describe(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach ((string lua, Type t, string summary) in Globals.Concat(Types).Concat(ValueTypes))
        {
            if (t == type)
            {
                return Build(lua, t, summary);
            }
        }

        return null;
    }

    /// <summary>
    /// The documented members of every surface type as (receiver, member name, snapshot line, doc),
    /// for the documentation-coverage test.
    /// </summary>
    public static IReadOnlyList<(string Receiver, string Member, string SnapshotLine, ScriptDocAttribute? Doc)> DocumentedSurface()
    {
        var list = new List<(string, string, string, ScriptDocAttribute?)>();
        foreach ((string lua, Type type, _) in Globals.Concat(Types))
        {
            foreach (Member m in MembersOf(type))
            {
                list.Add((lua, m.Name, SnapshotLine(lua, m), m.Doc));
            }
        }

        return list;
    }

    /// <summary>The CLR types behind every surface entry, with their Lua names (for type-level doc coverage).</summary>
    public static IReadOnlyList<(string Lua, Type Type)> SurfaceTypes() =>
        Globals.Concat(Types).Select(g => (g.Lua, g.Type)).ToList();

    /// <summary>The Lua-facing name of a CLR type as the reference documents it.</summary>
    public static string LuaTypeName(Type t) => LuaType(t);

    private static ScriptTypeInfo Build(string lua, Type type, string summary)
    {
        var members = new List<ScriptMemberInfo>();
        var text = new StringBuilder();
        text.Append(lua).Append(" — ").AppendLine(summary);
        foreach (Member m in MembersOf(type))
        {
            string signature = m.IsMethod
                ? $"{m.Name}({string.Join(", ", m.Params.Select(p => $"{p.Name}: {p.Type}{(p.Optional ? "?" : string.Empty)}"))})"
                : m.Name;
            string doc = m.Doc?.Summary ?? string.Empty;
            members.Add(new ScriptMemberInfo(m.Name, m.IsMethod ? "method" : "property", signature, m.ReturnType,
                doc, m.Doc?.Example ?? string.Empty, m.Doc?.Since ?? 1, OptionInfos(m.Options)));

            text.Append("  ").Append(signature);
            text.Append(m.IsMethod ? " -> " : " : ").Append(m.ReturnType);
            if (!m.IsMethod && m.ReadOnly)
            {
                text.Append(" (read-only)");
            }

            if (doc.Length > 0)
            {
                text.Append(" — ").Append(doc);
            }

            text.AppendLine();
            if (m.Doc?.Example is { Length: > 0 } ex)
            {
                text.Append("      e.g. ").AppendLine(ex);
            }

            AppendOptionText(text, m.Options, "      ");
        }

        return new ScriptTypeInfo(lua, summary, members.ToArray(), text.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>One option as a line: <c>name: type = default — doc</c> (the default part only when there is one).</summary>
    public static string FormatOption(ScriptOption o) =>
        $"{o.Name}: {o.Type}{(o.Default is { Length: > 0 } d ? $" = {d}" : string.Empty)} — {o.Doc}";

    private static ScriptOptionInfo[] OptionInfos(IReadOnlyList<ScriptOptionSet> sets) =>
        sets.SelectMany(s => s.Options).Select(o => new ScriptOptionInfo(o)).ToArray();

    /// <summary>Lists the option sets under a member; alternative forms (two sets) are labelled.</summary>
    private static void AppendOptionText(StringBuilder text, IReadOnlyList<ScriptOptionSet> sets, string indent)
    {
        for (int i = 0; i < sets.Count; i++)
        {
            text.Append(indent).AppendLine(sets.Count == 1 ? "options:" : (i == 0 ? "options (one form):" : "or (the other form):"));
            foreach (ScriptOption o in sets[i].Options)
            {
                text.Append(indent).Append("  ").AppendLine(FormatOption(o));
            }
        }
    }

    private static string SnapshotLine(string lua, Member m) => m.IsMethod
        ? $"{lua}.{m.Name}({string.Join(",", m.Params.Select(p => p.Type + (p.Optional ? "?" : string.Empty)))}):{m.ReturnType}"
        : $"{lua}.{m.Name}:{m.ReturnType}";

    private static void AppendMembers(StringBuilder sb, Type type)
    {
        foreach (Member m in MembersOf(type))
        {
            string doc = m.Doc is { } d ? $" — {d.Summary}" : string.Empty;
            string since = m.Doc is { Since: >= 2 } ? " *(v2)*" : string.Empty;
            if (m.IsMethod)
            {
                string ps = string.Join(", ", m.Params.Select(p => $"{p.Name}: {p.Type}{(p.Optional ? "?" : string.Empty)}"));
                sb.AppendLine($"- `{m.Name}({ps})` → `{m.ReturnType}`{doc}{since}");
            }
            else
            {
                sb.AppendLine($"- `{m.Name}` : `{m.ReturnType}`{(m.ReadOnly ? " *(read-only)*" : string.Empty)}{doc}{since}");
            }

            if (m.Doc?.Example is { Length: > 0 } example)
            {
                sb.AppendLine($"  Example: `{example}`");
            }

            AppendOptionMarkdown(sb, m.Options);
        }

        sb.AppendLine();
    }

    private static void AppendOptionMarkdown(StringBuilder sb, IReadOnlyList<ScriptOptionSet> sets)
    {
        for (int i = 0; i < sets.Count; i++)
        {
            sb.AppendLine(sets.Count == 1 ? "  Options:" : (i == 0 ? "  Options (one form):" : "  Or (the other form):"));
            foreach (ScriptOption o in sets[i].Options)
            {
                string def = o.Default is { Length: > 0 } d ? $", default {d}" : string.Empty;
                sb.AppendLine($"  - `{o.Name}` ({o.Type}{def}) — {o.Doc}");
            }
        }
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
                Array.Empty<Param>(), !p.CanWrite || p.SetMethod?.IsPublic != true, p.GetCustomAttribute<ScriptDocAttribute>(), Array.Empty<ScriptOptionSet>()));
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
            members.Add(new Member(ScriptNaming.ToSnakeCase(mi.Name), true, LuaType(mi.ReturnType), ps, false, mi.GetCustomAttribute<ScriptDocAttribute>(), ScriptOptionSets.Of(mi)));
        }

        return members
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Params.Length)
            .ThenBy(m => string.Join(",", m.Params.Select(p => p.Type)), StringComparer.Ordinal);
    }

    private static bool IsObjectMethod(string name) =>
        name is "ToString" or "Equals" or "GetHashCode" or "GetType" or "Deconstruct";

    private static string DocSummary(Type t) => t.GetCustomAttribute<ScriptDocAttribute>()?.Summary ?? string.Empty;

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

            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>) || def == typeof(Dictionary<,>))
            {
                return "table";
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
            [typeof(ScriptArgs)] = "table",
        };

        foreach ((string lua, Type type, _) in Types)
        {
            map[type] = lua;
        }

        return map;
    }

    private readonly record struct Param(string Name, string Type, bool Optional);

    private readonly record struct Member(string Name, bool IsMethod, string ReturnType, Param[] Params, bool ReadOnly, ScriptDocAttribute? Doc, IReadOnlyList<ScriptOptionSet> Options);
}
