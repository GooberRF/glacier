using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Ged.Automation.Tools;

/// <summary>How a tool call becomes an automation request (set by the server, never by the call).</summary>
public sealed class ToolRequestOptions
{
    /// <summary>The request origin (labels "AI: …" for "mcp").</summary>
    public string Origin { get; init; } = "mcp";

    /// <summary>The largest image side a render may use (the server's <c>--max-image</c>).</summary>
    public int MaxImageSide { get; init; } = ToolCatalog.DefaultImageSide;

    /// <summary>Supplies the script name of a <c>run_lua</c> call that names none (e.g. "mcp-3.lua").</summary>
    public Func<string>? DefaultScriptName { get; init; }

    /// <summary>
    /// Reads the script file of a <c>run_lua</c> call's <c>file</c> argument (the server's path rules apply): its source,
    /// its file name, and the modules it imports from its own folder. Null = the file argument is refused.
    /// </summary>
    public Func<string, (string Source, string Name, IReadOnlyDictionary<string, string> Modules)>? ReadScriptFile { get; init; }

    /// <summary>Supplies the PNG path a render writes (inside the session's scratch folder).</summary>
    public Func<string>? RenderOutPath { get; init; }
}

/// <summary>
/// Validates a tool call's arguments against its <see cref="ToolSpec"/> and maps them onto the automation
/// request the tool runs. Unknown or ill-typed arguments are rejected with a <c>usage</c> error naming the
/// argument; nothing a call sends can add an authorisation argument (the catalog has none to map).
/// </summary>
public static class ToolRequests
{
    /// <summary>The request for a call of <paramref name="spec"/> with <paramref name="arguments"/>, plus notes about adjusted values.</summary>
    public static (AutomationRequest Request, List<string> Notes) Build(ToolSpec spec, JsonObject? arguments, ToolRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);
        JsonObject given = arguments ?? new JsonObject();
        var notes = new List<string>();
        var args = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in given)
        {
            if (spec.Args.All(a => a.Name != kv.Key))
            {
                string known = spec.Args.Count == 0 ? "it takes no arguments" : "arguments: " + string.Join(", ", spec.Args.Select(a => a.Name));
                throw Usage($"Unknown argument '{kv.Key}' for {spec.Name} ({known}).");
            }
        }

        foreach (ToolArg a in spec.Args)
        {
            if (!given.TryGetPropertyValue(a.Name, out JsonNode? value) || value is null)
            {
                if (a.Required)
                {
                    throw Usage($"{spec.Name} needs '{a.Name}': {a.Doc}");
                }

                continue;
            }

            JsonNode checkedValue = Check(spec, a, value);
            if (a.CommandName.Length > 0)
            {
                args[a.CommandName] = checkedValue;
            }
        }

        foreach ((string key, string v) in spec.FixedArgs ?? new Dictionary<string, string>())
        {
            args[key] = v;
        }

        switch (spec.Name)
        {
            case "run_lua":
                if ((Str(given, "file") is null) == (given["code"] is null))
                {
                    throw Usage("run_lua needs one of 'code' (the Lua source) or 'file' (a Lua script file).");
                }

                if (Str(given, "file") is { } file)
                {
                    if (options.ReadScriptFile is not { } read)
                    {
                        throw Usage("run_lua: this server does not read script files; send the source as 'code'.");
                    }

                    (string source, string fileName, IReadOnlyDictionary<string, string> modules) = read(file);
                    args["script"] = source;
                    if (!args.ContainsKey("name"))
                    {
                        args["name"] = fileName;
                    }

                    if (modules.Count > 0)
                    {
                        var m = new JsonObject();
                        foreach (KeyValuePair<string, string> kv in modules.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        {
                            m[kv.Key] = kv.Value;
                        }

                        args["modules"] = m;
                    }
                }

                if (!args.ContainsKey("name") && options.DefaultScriptName is { } next)
                {
                    args["name"] = next();
                }

                switch (Str(given, "build"))
                {
                    case "sealed":
                        args["build"] = true;
                        break;
                    case "lit":
                        args["build"] = true;
                        args["light"] = true;
                        break;
                }

                if (Str(given, "build") is "sealed" or "lit" && given["dry_run"]?.GetValue<bool>() == true)
                {
                    throw Usage("run_lua: build cannot be combined with dry_run (a dry run changes nothing to build).");
                }

                break;

            case "align_textures":
            {
                var lua = new System.Text.StringBuilder("local q = level.all_brushes()\n");
                if (Str(given, "tag") is { } tag)
                {
                    int eq = tag.IndexOf('=', StringComparison.Ordinal);
                    lua.Append(eq < 0
                        ? $"q = level.brushes_tagged({Lua(tag)})\n"
                        : $"q = level.brushes_tagged({Lua(tag[..eq])}, {Lua(tag[(eq + 1)..])})\n");
                }

                bool report = given["report"] is JsonValue rv && rv.TryGetValue(out bool r) && r;
                lua.Append(ResultAsJson);
                lua.Append(report ? "return json(level.uv_report{brushes = q})\n" : "return json(level.uv_align{brushes = q})\n");
                if (report)
                {
                    args["dryRun"] = true;
                }

                args["script"] = lua.ToString();
                if (options.DefaultScriptName is { } nextName)
                {
                    args["name"] = nextName();
                }

                break;
            }

            case "terrain":
                args["script"] = TerrainLua(Str(given, "op") ?? "query", given);
                if (Str(given, "op") == "query")
                {
                    args["dryRun"] = true;
                }

                if (options.DefaultScriptName is { } terrainScript)
                {
                    args["name"] = terrainScript();
                }

                break;
            case "build":
                if (!args.ContainsKey("sealed"))
                {
                    args["sealed"] = true; // MCP default: holes and lint need a sealed build
                }

                break;

            case "render_plan":
            case "render_view":
            {
                int cap = Math.Clamp(options.MaxImageSide, 16, ToolCatalog.MaxImageSide);
                int w = Math.Min(ToolCatalog.DefaultImageSide, cap), h = w;
                if (Str(given, "size") is not { } size)
                {
                    // No fixed size: the longest side, so a region framing can take the region's proportions.
                    args["maxSide"] = (double)w;
                }
                else
                {
                    string[] parts = size.ToLowerInvariant().Split('x');
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out w)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out h) || w < 16 || h < 16)
                    {
                        throw Usage($"{spec.Name}: size must be WIDTHxHEIGHT with each side at least 16, e.g. 1024x768 (got '{size}').");
                    }

                    if (w > cap || h > cap)
                    {
                        double scale = Math.Min((double)cap / w, (double)cap / h);
                        int cw = Math.Max(16, (int)Math.Floor(w * scale)), ch = Math.Max(16, (int)Math.Floor(h * scale));
                        notes.Add($"The requested size {w}x{h} exceeds this server's image cap ({cap} per side): rendered at {cw}x{ch}.");
                        (w, h) = (cw, ch);
                    }

                    args["width"] = (double)w;
                    args["height"] = (double)h;
                }

                if (options.RenderOutPath is { } outPath)
                {
                    args["out"] = outPath();
                }

                break;
            }
        }

        args["origin"] = options.Origin;
        return (new AutomationRequest(spec.Command, args), notes);
    }

    private static JsonNode Check(ToolSpec spec, ToolArg a, JsonNode value)
    {
        string where = $"{spec.Name}: '{a.Name}'";
        switch (a.Type)
        {
            case "string":
            {
                if (value is not JsonValue v || !v.TryGetValue(out string? s))
                {
                    throw Usage($"{where} must be a string.");
                }

                if (a.Enum is { } options && !options.Contains(s, StringComparer.Ordinal))
                {
                    throw Usage($"{where} must be one of {string.Join(", ", options)} (got '{s}').");
                }

                if (a.MaxLength is int max && s.Length > max)
                {
                    throw Usage($"{where} is longer than {max} characters.");
                }

                return JsonValue.Create(s)!;
            }

            case "boolean":
                return value is JsonValue b && b.TryGetValue(out bool flag)
                    ? JsonValue.Create(flag)
                    : throw Usage($"{where} must be true or false.");

            case "number":
            case "integer":
            {
                double d = Number(value) ?? throw Usage($"{where} must be a number.");
                if (a.Type == "integer" && d != Math.Floor(d))
                {
                    throw Usage($"{where} must be a whole number.");
                }

                Range(where, a, d);
                return JsonValue.Create(d);
            }

            case "array":
            {
                if (value is not JsonArray arr)
                {
                    throw Usage($"{where} must be an array.");
                }

                if (a.Items is int n && arr.Count != n)
                {
                    throw Usage($"{where} needs exactly {n} numbers (got {arr.Count}).");
                }

                var copy = new JsonArray();
                foreach (JsonNode? item in arr)
                {
                    double d = (item is null ? null : Number(item)) ?? throw Usage($"{where} must contain only numbers.");
                    copy.Add(d);
                }

                return copy;
            }

            default:
                return value.DeepClone();
        }
    }

    private static void Range(string where, ToolArg a, double d)
    {
        if (!double.IsFinite(d) || (a.Min is double min && d < min) || (a.Max is double max && d > max))
        {
            string range = (a.Min, a.Max) switch
            {
                (double lo, double hi) => $"from {lo.ToString(CultureInfo.InvariantCulture)} to {hi.ToString(CultureInfo.InvariantCulture)}",
                (double lo, null) => $"at least {lo.ToString(CultureInfo.InvariantCulture)}",
                (null, double hi) => $"at most {hi.ToString(CultureInfo.InvariantCulture)}",
                _ => "finite",
            };
            throw Usage($"{where} must be {range} (got {d.ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    private static double? Number(JsonNode node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out double d))
        {
            return d;
        }

        if (v.TryGetValue(out long l))
        {
            return l;
        }

        return v.TryGetValue(out int i) ? i : null;
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static AutomationException Usage(string message) => new(AutomationErrorCodes.Usage, message);

    /// <summary>A Lua function <c>info(t)</c>: the facts of a terrain handle the terrain tools return.</summary>
    private const string TerrainInfo =
        "local function info(t)\n" +
        "  return {uid = t.uid, name = t.name, origin = {t.origin.x, t.origin.y, t.origin.z}, nx = t.nx, nz = t.nz,\n" +
        "    cell_size = t.cell_size, size = {t.size_x, t.size_z}, height_min = t.height_min, height_range = t.height_range,\n" +
        "    chunk_cells = t.chunk_cells, chunk_count = t.chunk_count, layers = t.layers, built = t.built, geoable = t.geoable, skirts = t.skirts,\n" +
        "    lighting = t.lighting, decorations = #t:decorations(), overlays = t.overlays}\n" +
        "end\n";

    /// <summary>The Lua a terrain tool runs (the same API a script calls: level.terrain, terrain:*).</summary>
    private static string TerrainLua(string tool, JsonObject given)
    {
        var lua = new System.Text.StringBuilder(ResultAsJson).Append(TerrainInfo);
        string? key = Str(given, "terrain");
        string Pick() => key is null
            ? "level.terrain.all()[1]"
            : $"level.terrain.get({(int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid) ? uid.ToString(CultureInfo.InvariantCulture) : Lua(key))})";
        string Need() => $"local t = {Pick()}\nif not t then ged.fail({Lua(key is null ? "The level has no terrain." : $"No terrain '{key}'.")}, \"terrain_query lists the level's terrains; op = create makes one.\") end\n";
        string Num(string name) => given[name] is JsonValue v && Number(v) is double d ? d.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        string Flag(string name) => given[name] is JsonValue v && v.TryGetValue(out bool b) ? (b ? "true" : "false") : string.Empty;
        string Layers(string name) => "{" + string.Join(", ", (Str(given, name) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Lua)) + "}";
        double[] Pairs(string name) => given[name] is JsonArray arr ? arr.Select(n => n is null ? 0 : Number(n) ?? 0).ToArray() : Array.Empty<double>();
        void Opt(List<string> into, string name)
        {
            string v = given[name] switch
            {
                JsonValue jv when jv.TryGetValue(out bool _) => Flag(name),
                JsonValue jv when Number(jv) is not null => Num(name),
                JsonValue jv when jv.TryGetValue(out string? s) => Lua(s!),
                _ => string.Empty,
            };
            if (v.Length > 0)
            {
                into.Add($"{name} = {v}");
            }
        }

        switch (tool)
        {
            case "create":
            {
                var o = new List<string>();
                if (given["center"] is JsonArray c && c.Count == 3)
                {
                    o.Add("center = {" + string.Join(", ", c.Select(n => (n is null ? 0 : Number(n) ?? 0).ToString("R", CultureInfo.InvariantCulture))) + "}");
                }

                foreach (string n in new[] { "nx", "nz", "cell_size", "name", "height_range" })
                {
                    Opt(o, n);
                }

                if (Str(given, "layers") is not null)
                {
                    o.Add("layers = " + Layers("layers"));
                }

                lua.Append("local t = level.terrain.create{").Append(string.Join(", ", o)).Append("}\nreturn json(info(t))\n");
                break;
            }

            case "generate":
            {
                var o = new List<string>();
                foreach (string n in new[] { "seed", "type", "feature_size", "height_range", "falloff" })
                {
                    Opt(o, n);
                }

                lua.Append(Need()).Append("t:generate{").Append(string.Join(", ", o)).Append("}\nreturn json(info(t))\n");
                break;
            }

            case "edit":
            {
                lua.Append(Need());
                if (given.ContainsKey("nx") || given.ContainsKey("nz"))
                {
                    var o = new List<string>();
                    Opt(o, "nx");
                    Opt(o, "nz");
                    lua.Append("t:resize{").Append(string.Join(", ", o)).Append("}\n");
                }

                if (Str(given, "layers") is not null)
                {
                    lua.Append("t:set_layers(").Append(Layers("layers")).Append(")\n");
                }


                double[] holes = Pairs("holes");
                for (int i = 0; i + 1 < holes.Length; i += 2)
                {
                    lua.Append(CultureInfo.InvariantCulture, $"t:set_hole({(int)holes[i]}, {(int)holes[i + 1]}, true)\n");
                }

                if (Str(given, "heightmap") is { } path)
                {
                    lua.Append("t:import_heightmap(").Append(Lua(path)).Append(")\n");
                }

                lua.Append("return json(info(t))\n");
                break;
            }

            case "sculpt":
            {
                double[] pts = Pairs("points");
                if (pts.Length < 2)
                {
                    throw Usage("terrain sculpt needs 'points': x, z, ... (one point dabs once; more make a stroke).");
                }

                // layers = "mode[:radius:strength:layer]" (the sculpt op reuses the layers argument)
                string[] spec = (Str(given, "layers") ?? "raise").Split(':', StringSplitOptions.TrimEntries);
                var o = new List<string> { "tool = " + Lua(spec[0]) };
                string[] keys = { "radius", "strength", "layer" };
                for (int k = 1; k < spec.Length && k <= keys.Length; k++)
                {
                    if (!double.TryParse(spec[k], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    {
                        throw Usage($"terrain sculpt: '{spec[k]}' in layers is not a number (mode[:radius:strength:layer]).");
                    }

                    o.Add($"{keys[k - 1]} = {v.ToString("R", CultureInfo.InvariantCulture)}");
                }

                Opt(o, "seed");
                var path = new System.Text.StringBuilder("{");
                for (int i = 0; i + 1 < pts.Length; i += 2)
                {
                    path.Append(CultureInfo.InvariantCulture, $"{{{pts[i]:R}, {pts[i + 1]:R}}}, ");
                }

                o.Add("path = " + path.Append('}'));
                lua.Append(Need()).Append("local r = t:sculpt{").Append(string.Join(", ", o))
                    .Append("}\nlocal i = info(t)\ni.changed = r.changed\ni.geometry = r.geometry\ni.note = r.note\nreturn json(i)\n");
                break;
            }

            case "query":
            {
                double[] points = Pairs("points");
                var sample = new System.Text.StringBuilder("{");
                for (int i = 0; i + 1 < points.Length; i += 2)
                {
                    sample.Append(CultureInfo.InvariantCulture, $"{{{points[i]:R}, {points[i + 1]:R}}}, ");
                }

                sample.Append('}');
                lua.Append(key is null ? "local list = level.terrain.all()\n" : $"local list = {{{Pick()}}}\n")
                    .Append("local out = {}\nlocal pts = ").Append(sample).Append('\n')
                    .Append("for _, t in ipairs(list) do\n  local i = info(t)\n  i.lint = t:lint()\n  i.heights = {}\n")
                    .Append("  for _, p in ipairs(pts) do i.heights[#i.heights + 1] = t:height_at(p[1], p[2]) end\n")
                    .Append("  out[#out + 1] = i\nend\nreturn json(out)\n");
                break;
            }

            case "to_brushes":
            {
                string keep = Flag("keep");
                lua.Append(Need()).Append("local q = t:to_brushes{keep = ").Append(keep.Length > 0 ? keep : "false").Append("}\nreturn json({brushes = q.count})\n");
                break;
            }
        }

        return lua.ToString();
    }

    /// <summary>A Lua function <c>json(value)</c> that renders a result table as JSON text (the run's return value).</summary>
    private const string ResultAsJson =
        "local function json(v)\n" +
        "  local t = type(v)\n" +
        "  if t == 'table' then\n" +
        "    local o = {}\n" +
        "    if #v > 0 or next(v) == nil then for i, x in ipairs(v) do o[i] = json(x) end return '[' .. table.concat(o, ',') .. ']' end\n" +
        "    local keys = {} for k in pairs(v) do keys[#keys + 1] = tostring(k) end table.sort(keys)\n" +
        "    for _, k in ipairs(keys) do o[#o + 1] = string.format('%q', k) .. ':' .. json(v[k]) end\n" +
        "    return '{' .. table.concat(o, ',') .. '}'\n" +
        "  elseif t == 'number' then return string.format('%.4g', v)\n" +
        "  elseif t == 'string' then return string.format('%q', v)\n" +
        "  elseif t == 'boolean' then return tostring(v)\n" +
        "  elseif t == 'userdata' then return string.format('%q', tostring(v))\n" +
        "  end\n" +
        "  return 'null'\n" +
        "end\n";

    /// <summary>A Lua string literal of <paramref name="s"/> (quotes, backslashes and control characters escaped).</summary>
    private static string Lua(string s)
    {
        var b = new System.Text.StringBuilder("\"");
        foreach (char c in s)
        {
            b.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                _ when char.IsControl(c) => "\\" + ((int)c).ToString("000", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return b.Append('"').ToString();
    }
}
