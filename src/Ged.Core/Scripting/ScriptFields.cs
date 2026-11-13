using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Scripting;

/// <summary>The description of one generic field (Lua type <c>field_info</c>), returned by <c>h:fields()</c> and <c>catalog.describe</c>.</summary>
[ScriptDoc("One settable/readable field of an object, brush or level: name (the key for get/set), label, type, editor, options and range.", Since = 2)]
public sealed class ScriptFieldInfo
{
    internal ScriptFieldInfo(string name, string label, string type, string editor, string[] options,
        double? min, double? max, string doc, bool readOnly, bool editorOnly, string slot)
    {
        Name = name;
        Label = label;
        Type = type;
        Editor = editor;
        Options = options;
        Min = min;
        Max = max;
        Doc = doc;
        ReadOnly = readOnly;
        EditorOnly = editorOnly;
        Slot = slot;
    }

    [ScriptDoc("The key used with get/set (snake_case), e.g. \"sphere_radius\".", Since = 2)]
    public string Name { get; }

    [ScriptDoc("The Properties-panel label, e.g. \"Sphere Radius\".", Since = 2)]
    public string Label { get; }

    [ScriptDoc("Value type: \"string\", \"int\", \"float\", \"bool\", \"enum\", \"color\", \"vec\" or \"uid\".", Since = 2)]
    public string Type { get; }

    [ScriptDoc("The source editor kind, e.g. \"Float\", \"Bits\", \"IntAsFloat\", \"FilePicker\".", Since = 2)]
    public string Editor { get; }

    [ScriptDoc("The valid option names (enum fields and text dropdowns); empty otherwise.", Since = 2)]
    public string[] Options { get; }

    [ScriptDoc("Inclusive minimum for numbers, or nil.", Since = 2)]
    public double? Min { get; }

    [ScriptDoc("Inclusive maximum for numbers, or nil.", Since = 2)]
    public double? Max { get; }

    [ScriptDoc("A note about the field (\"\" when none).", Since = 2)]
    public string Doc { get; }

    [ScriptDoc("True when set() is refused (display-only fields).", Since = 2)]
    public bool ReadOnly { get; }

    [ScriptDoc("True for fields the editor shows but the game ignores.", Since = 2)]
    public bool EditorOnly { get; }

    [ScriptDoc("For event fields, the raw record slot (\"int1\", \"str2\", …); \"\" otherwise.", Since = 2)]
    public string Slot { get; }

    public override string ToString() => $"{Name}: {Type}{(ReadOnly ? " (read-only)" : string.Empty)}";
}

/// <summary>
/// One bound generic field: its <see cref="ScriptFieldInfo"/> plus accessors over the canonical
/// editor value (<see cref="InspectorAccess"/> forms). Built per target by <see cref="ScriptFieldCatalog"/>.
/// </summary>
internal sealed class ScriptField
{
    public required ScriptFieldInfo Info { get; init; }

    /// <summary>Model path for name lookup (null for pseudo/virtual fields).</summary>
    public string? Path { get; init; }

    /// <summary>Reads the canonical value (null when unbound or not applicable).</summary>
    public Func<object?>? Read { get; init; }

    /// <summary>Writes a validated canonical value (one undoable command).</summary>
    public Action<object?>? Write { get; init; }

    /// <summary>Refuses a write that cannot apply now: (message, hint), or null when writable.</summary>
    public Func<(string Message, string? Hint)?>? Guard { get; init; }

    /// <summary>True when an inapplicable field reads nil and accepts set(nil) as a no-op (conditional members).</summary>
    public Func<bool>? Absent { get; init; }

    /// <summary>The inclusive range an int/uid field can store (storage type ∩ min/max).</summary>
    public (double Min, double Max)? IntRange { get; init; }

    /// <summary>Why the field is read-only (hint), when it is.</summary>
    public string? ReadOnlyHint { get; init; }

    public string Key => Info.Name;
}

/// <summary>
/// An ordered, bound field list for one target with the generic <c>get</c>/<c>set</c>/
/// <c>set_many</c>/<c>fields</c>/<c>props</c> semantics: lookup by key, label or model
/// path (case-insensitive); strict, validate-before-write value marshalling; schema-order
/// <c>set_many</c>; actionable errors with the nearest valid names.
/// </summary>
internal sealed class ScriptFieldSet
{
    public ScriptFieldSet(string owner, IReadOnlyList<ScriptField> fields)
    {
        Owner = owner;
        Fields = fields;
    }

    /// <summary>The noun used in errors ("Trigger", "Brush", "Level").</summary>
    public string Owner { get; }

    public IReadOnlyList<ScriptField> Fields { get; }

    /// <summary>The field named <paramref name="name"/> (key, then label, then model path), or null.</summary>
    public ScriptField? Find(string? name)
    {
        string n = (name ?? string.Empty).Trim();
        if (n.Length == 0)
        {
            return null;
        }

        return Fields.FirstOrDefault(f => string.Equals(f.Key, n, StringComparison.OrdinalIgnoreCase))
            ?? Fields.FirstOrDefault(f => string.Equals(f.Info.Label, n, StringComparison.OrdinalIgnoreCase))
            ?? Fields.FirstOrDefault(f => f.Path is { Length: > 0 } p && string.Equals(p, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The field named <paramref name="name"/>, or a "&lt;Owner&gt; has no field" error with suggestions.</summary>
    public ScriptField Resolve(string api, string? name)
    {
        if (Find(name) is { } f)
        {
            return f;
        }

        throw new ScriptApiException($"{api}: {Owner} has no field '{name}'.",
            ScriptSuggest.Hint(name ?? string.Empty, Fields.Select(x => x.Key), "field"));
    }

    /// <summary>The Lua value of a field.</summary>
    public object? Get(string api, string? name) => ScriptFieldValues.ToLua(Resolve(api, name));

    /// <summary>Validates and writes one value (no-op when equal to the current value).</summary>
    public void Set(string api, string? name, object? value) => SetField(api, Resolve(api, name), value);

    /// <summary>Applies a table of values in SCHEMA order (never Lua table order); unknown keys fail before any write.</summary>
    public void SetMany(string api, ScriptArgs? values)
    {
        if (values is null)
        {
            throw new ScriptApiException($"{api}: a table {{name = value, …}} is required.");
        }

        if (values.Items.Count > 0)
        {
            throw new ScriptApiException($"{api}: expected a table of name = value pairs, not a list.",
                "For example set_many{ resets_after = 2, use_key_required = true }.");
        }

        var chosen = new List<(ScriptField Field, string Name, object? Value)>();
        foreach (KeyValuePair<string, object?> kv in values.Named.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            ScriptField f = Resolve(api, kv.Key);
            if (chosen.Any(c => ReferenceEquals(c.Field, f)))
            {
                throw new ScriptApiException($"{api}: field '{f.Key}' is given twice ('{chosen.First(c => ReferenceEquals(c.Field, f)).Name}' and '{kv.Key}').");
            }

            chosen.Add((f, kv.Key, kv.Value));
        }

        foreach ((ScriptField f, _, object? v) in chosen.OrderBy(c => IndexOf(c.Field)))
        {
            SetField(api, f, v);
        }
    }

    /// <summary>Every field's description, in schema order.</summary>
    public ScriptFieldInfo[] Infos() => Fields.Select(f => f.Info).ToArray();

    /// <summary>A table of every readable field's Lua value (absent conditional members are omitted).</summary>
    public Dictionary<string, object?> Props()
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (ScriptField f in Fields)
        {
            if (ScriptFieldValues.ToLua(f) is { } v)
            {
                map[f.Key] = v;
            }
        }

        return map;
    }

    /// <summary>A "key : type = value" listing (for <c>ged.describe</c> on a handle).</summary>
    public string Listing()
    {
        var sb = new StringBuilder();
        sb.Append("Fields (").Append(Owner).AppendLine("):");
        foreach (ScriptField f in Fields)
        {
            sb.Append("  ").Append(f.Key).Append(" : ").Append(f.Info.Type);
            if (f.Info.ReadOnly)
            {
                sb.Append(" (read-only)");
            }

            sb.Append(" = ").AppendLine(ScriptFieldValues.Format(ScriptFieldValues.ToLua(f)));
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private int IndexOf(ScriptField f)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (ReferenceEquals(Fields[i], f))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private void SetField(string api, ScriptField f, object? value)
    {
        if (f.Info.ReadOnly || f.Write is null)
        {
            throw new ScriptApiException($"{api}: Field '{f.Key}' is read-only.", f.ReadOnlyHint);
        }

        if (f.Absent?.Invoke() == true && value is null)
        {
            return; // set(get()) on a member that does not apply right now
        }

        if (f.Guard?.Invoke() is { } refusal)
        {
            throw new ScriptApiException($"{api}: {refusal.Message}", refusal.Hint);
        }

        object? current = f.Read?.Invoke();
        object canonical = ScriptFieldValues.FromLua(api, f, value, current);
        if (Equals(canonical, current))
        {
            return;
        }

        ScriptFieldValues.Validate(api, f, canonical);
        try
        {
            f.Write(canonical);
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptApiException($"{api}: {ex.Message}");
        }
    }
}

/// <summary>Lua ⇄ canonical editor value conversion and validation for generic fields.</summary>
internal static class ScriptFieldValues
{
    /// <summary>
    /// True when <paramref name="input"/> names option <paramref name="option"/>: case-insensitive, and a
    /// space and an underscore are interchangeable, so "sky_room" picks "Sky Room".
    /// </summary>
    public static bool SameOption(string option, string input) =>
        string.Equals(OptionKey(option), OptionKey(input), StringComparison.Ordinal);

    private static string OptionKey(string s) => (s ?? string.Empty).Trim().Replace(' ', '_').ToLowerInvariant();

    /// <summary>A field's current value as Lua sees it: enum → option name, colour → {r,g,b,a}, float/int → number.</summary>
    public static object? ToLua(ScriptField f) => ToLua(f.Info, f.Read?.Invoke());

    public static object? ToLua(ScriptFieldInfo info, object? canonical) => canonical switch
    {
        null => null,
        int i when info.Type == "enum" => i >= 0 && i < info.Options.Length ? info.Options[i] : i,
        int i => (double)i,
        float fl => (double)fl,
        RfColor c => new Dictionary<string, object?> { ["r"] = (double)c.R, ["g"] = (double)c.G, ["b"] = (double)c.B, ["a"] = (double)c.A },
        _ => canonical,
    };

    /// <summary>Converts a Lua value to the field's canonical type, rejecting anything of the wrong type.</summary>
    public static object FromLua(string api, ScriptField f, object? value, object? current)
    {
        ScriptFieldInfo info = f.Info;
        string key = info.Name;
        if (value is null)
        {
            throw new ScriptApiException($"{api}: field '{key}' needs a {info.Type} value (got nil).");
        }

        switch (info.Type)
        {
            case "string":
            {
                if (value is not string s)
                {
                    throw TypeError(api, key, "string", value);
                }

                if (info.Options.Length > 0 && !Equals(s, current))
                {
                    return info.Options.FirstOrDefault(o => ScriptFieldValues.SameOption(o, s))
                        ?? throw new ScriptApiException($"{api}: field '{key}' has no option '{s}'.",
                            ScriptSuggest.Hint(s, info.Options, "option"));
                }

                return s;
            }

            case "bool":
                return value is bool b ? b : throw TypeError(api, key, "boolean", value);

            case "float":
            {
                if (value is not double d)
                {
                    throw TypeError(api, key, "number", value);
                }

                if (!double.IsFinite(d) || Math.Abs(d) > float.MaxValue)
                {
                    throw new ScriptApiException($"{api}: field '{key}' must be a finite number (got {ScriptArgs.Format(d)}).");
                }

                return (float)d;
            }

            case "int":
            case "uid":
            {
                double d = value switch
                {
                    double n => n,
                    ScriptObjectHandle o when info.Type == "uid" => o.Uid,
                    ScriptBrushHandle bh when info.Type == "uid" => bh.Uid,
                    ScriptMoverHandle mh when info.Type == "uid" => mh.StartKeyframeUid("field '" + key + "'"),
                    _ => throw TypeError(api, key, info.Type == "uid" ? "UID number or handle" : "number", value),
                };
                return ScriptArgs.ToInt(api, key, d);
            }

            case "enum":
            {
                if (value is string name)
                {
                    int idx = Array.FindIndex(info.Options, o => ScriptFieldValues.SameOption(o, name));
                    return idx >= 0
                        ? idx
                        : throw new ScriptApiException($"{api}: field '{key}' has no option '{name}'.",
                            ScriptSuggest.Hint(name, info.Options, "option"));
                }

                if (value is double d)
                {
                    return ScriptArgs.ToInt(api, key, d);
                }

                throw TypeError(api, key, "option name or 0-based index", value);
            }

            case "color":
                return ToColor(api, key, value, current is RfColor cur ? cur.A : (byte)255);

            case "vec":
                return ScriptArgs.ToVec(api, key, value);

            default:
                throw new ScriptApiException($"{api}: field '{key}' has an unsupported type '{info.Type}'.");
        }
    }

    /// <summary>Range / option-index checks on a converted value (only reached when it differs from the current value).</summary>
    public static void Validate(string api, ScriptField f, object canonical)
    {
        ScriptFieldInfo info = f.Info;
        switch (canonical)
        {
            case int i when info.Type == "enum":
                if (i < 0 || i >= info.Options.Length)
                {
                    throw new ScriptApiException($"{api}: field '{info.Name}' option index must be 0 to {info.Options.Length - 1} (got {i}).",
                        $"Valid options: {string.Join(", ", info.Options)}.");
                }

                break;
            case int i:
                if (f.IntRange is { } range && (i < range.Min || i > range.Max))
                {
                    throw new ScriptApiException($"{api}: field '{info.Name}' must be between {ScriptArgs.Format(range.Min)} and {ScriptArgs.Format(range.Max)} (got {i}).");
                }

                break;
            case float fl:
                if ((info.Min is double lo && fl < lo) || (info.Max is double hi && fl > hi))
                {
                    throw new ScriptApiException($"{api}: field '{info.Name}' must be between {ScriptArgs.Format(info.Min ?? double.MinValue)} and {ScriptArgs.Format(info.Max ?? double.MaxValue)} (got {ScriptArgs.Format(fl)}).");
                }

                break;
        }
    }

    /// <summary>A value rendered for a listing.</summary>
    public static string Format(object? lua) => lua switch
    {
        null => "nil",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        double d => ScriptArgs.Format(d),
        Vec3 v => string.Create(CultureInfo.InvariantCulture, $"({ScriptArgs.Format(v.X)}, {ScriptArgs.Format(v.Y)}, {ScriptArgs.Format(v.Z)})"),
        Dictionary<string, object?> c => $"{{r={Format(c.GetValueOrDefault("r"))}, g={Format(c.GetValueOrDefault("g"))}, b={Format(c.GetValueOrDefault("b"))}, a={Format(c.GetValueOrDefault("a"))}}}",
        _ => Convert.ToString(lua, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static RfColor ToColor(string api, string key, object value, byte currentAlpha)
    {
        if (value is not ScriptArgs t)
        {
            throw TypeError(api, key, "colour {r, g, b[, a]}", value);
        }

        object?[] parts;
        if (t.Named.Count == 0 && t.Items.Count is 3 or 4)
        {
            parts = t.Items.ToArray();
        }
        else if (t.Items.Count == 0 && t.Has("r") && t.Has("g") && t.Has("b"))
        {
            t.RejectUnknown($"{api} {key}", "r", "g", "b", "a");
            parts = t.Has("a")
                ? new[] { t.Named["r"], t.Named["g"], t.Named["b"], t.Named["a"] }
                : new[] { t.Named["r"], t.Named["g"], t.Named["b"] };
        }
        else
        {
            throw new ScriptApiException($"{api}: field '{key}' must be a colour {{r, g, b[, a]}} or {{r=, g=, b=[, a=]}}.",
                "Components are 0-255, e.g. {255, 240, 200}.");
        }

        var bytes = new byte[4];
        bytes[3] = currentAlpha;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is not double d || !double.IsFinite(d) || Math.Floor(d) != d || d < 0 || d > 255)
            {
                throw new ScriptApiException($"{api}: field '{key}' colour components must be integers 0-255 (got {Format(parts[i])}).");
            }

            bytes[i] = (byte)d;
        }

        return new RfColor(bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    private static ScriptApiException TypeError(string api, string key, string type, object? got) =>
        new($"{api}: field '{key}' must be a {type} (got {ScriptArgs.LuaTypeOf(got)}).");
}

/// <summary>
/// Script names as a script sees them. A trigger with multiplayer flags stores them in its name — a
/// <c>«</c> plus one flag byte before the real name (Alpine PutFlagsIntoScriptName, trigger.cpp:64-72) —
/// so reads strip that prefix and writes keep it: renaming a trigger never clears its flags.
/// </summary>
internal static class ScriptObjectNames
{
    /// <summary>The name without a trigger's flag prefix.</summary>
    public static string Get(LevelObject obj) =>
        obj.Model is Trigger { IsPureFactionEncoded: true } t ? t.ScriptName[2..] : obj.ScriptName;

    /// <summary>The stored name for <paramref name="name"/>, keeping a trigger's flag prefix.</summary>
    public static string Stored(LevelObject obj, string name) =>
        obj.Model is Trigger { IsPureFactionEncoded: true } t ? t.ScriptName[..2] + name : name;
}

/// <summary>
/// Builds the generic field lists for every target: level objects (common
/// pseudo-fields + the kind's inspector schema, event class fields, or keyframe fields), brushes
/// (<see cref="BrushInspectorCatalog"/>), and level property bags. The same builders, unbound,
/// feed <c>catalog.describe</c>.
/// </summary>
internal static class ScriptFieldCatalog
{
    /// <summary>The common pseudo-field keys every object resolves before its schema.</summary>
    internal static readonly string[] PseudoKeys = { "name", "hidden", "pos", "class", "uid", "kind" };

    /// <summary>The bound fields of a level object.</summary>
    public static ScriptFieldSet ForObject(ScriptContext ctx, LevelObject obj)
    {
        EditorDocument doc = ctx.Document;
        var fields = new List<ScriptField>();
        bool named = HasScriptName(obj.Model);

        fields.Add(new ScriptField
        {
            Info = Info("name", "Script Name", "string", "Text", doc: "The script name (same as set_name).", readOnly: !named),
            Read = () => ScriptObjectNames.Get(obj),
            Write = named ? v => doc.EditValue(obj.Section, "Rename object", obj.ScriptName, ScriptObjectNames.Stored(obj, (string)v!), x => obj.ScriptName = x) : null,
            ReadOnlyHint = named ? null : $"{obj.Kind} objects have no script name.",
        });
        fields.Add(new ScriptField
        {
            Info = Info("hidden", "Hidden", "bool", "Bool", doc: "Hidden in the editor."),
            Read = () => obj.Hidden,
            Write = v => doc.EditValue(obj.Section, (bool)v! ? "Hide object" : "Show object", obj.Hidden, (bool)v!, x => obj.Hidden = x),
        });
        fields.Add(new ScriptField
        {
            Info = Info("pos", "Position", "vec", "Vector", doc: "World position in metres."),
            Read = () => obj.Position,
            Write = v => doc.EditValue(obj.Section, "Set position", obj.Position, (Vec3)v!, x => obj.Position = x),
        });
        fields.Add(ReadOnlyField("class", "Class", "string", () => obj.ClassName, "The object class (set it when placing)."));
        fields.Add(ReadOnlyField("uid", "UID", "uid", () => obj.Uid, "The unique id."));
        fields.Add(ReadOnlyField("kind", "Kind", "string", () => obj.Kind.ToString(), "The object kind."));

        if (obj.Model is RflEvent ev)
        {
            fields.Add(new ScriptField
            {
                Info = Info("delay", "Delay (s)", "float", "Float", doc: "Seconds before the event fires."),
                Read = () => ev.Delay,
                Write = v => doc.EditValue(obj.Section, "Edit Delay", ev.Delay, (float)v!, x => ev.Delay = x),
            });
            fields.Add(new ScriptField
            {
                Info = Info("color", "Color", "color", "Color", doc: "The event colour."),
                Read = () => ev.Color,
                Write = v => doc.EditValue(obj.Section, "Edit Color", ev.Color, (RfColor)v!, x => ev.Color = x),
            });

            if (EventSchemaCatalog.Find(ev.ClassName) is { } schema)
            {
                foreach (EventFieldSpec spec in schema.Fields)
                {
                    if (Shadowed(fields, spec.EffectiveKey))
                    {
                        continue;
                    }

                    fields.Add(EventField(spec, () => InspectorAccess.Get(ev, spec), v => InspectorAccess.Set(doc, obj, spec, v)));
                }
            }

            return new ScriptFieldSet(ev.ClassName.Length > 0 ? ev.ClassName : "Event", fields);
        }

        IReadOnlyList<InspectorField> schemaFields = obj.Kind == LevelObjectKind.Keyframe
            ? MoverInspectorSchema.KeyframeFields
            : ObjectInspectorCatalog.For(obj.Kind);
        foreach (InspectorField f in schemaFields)
        {
            if (Shadowed(fields, f.EffectiveKey))
            {
                continue;
            }

            InspectorField field = f;
            (double Min, double Max) range = InspectorAccess.IntegerRange(obj.Model, field);
            fields.Add(new ScriptField
            {
                Info = InfoFor(field, range),
                Path = field.Virtual ? null : field.Path,
                Read = () => InspectorAccess.Get(doc, obj, field),
                Write = field.ReadOnly ? null : v => InspectorAccess.Set(doc, obj, field, v, ctx.Movers),
                Absent = () => InspectorAccess.Unavailable(obj.Model, field) is not null,
                Guard = () => InspectorAccess.Unavailable(obj.Model, field) is { } hint
                    ? ($"field '{field.EffectiveKey}' does not apply to this {obj.Kind} right now.", hint)
                    : null,
                IntRange = range,
                ReadOnlyHint = field.ReadOnly ? field.Note : null,
            });
        }

        return new ScriptFieldSet(obj.Kind.ToString(), fields);
    }

    /// <summary>The bound fields of a brush (<see cref="BrushInspectorCatalog.Fields"/>).</summary>
    public static ScriptFieldSet ForBrush(ScriptContext ctx, int uid)
    {
        EditorDocument doc = ctx.Document;
        Brush model = ctx.Brushes.FindBrush(uid) ?? throw new ScriptApiException($"Brush {uid} no longer exists.");
        var fields = new List<ScriptField>();
        foreach (InspectorField f in BrushInspectorCatalog.Fields)
        {
            InspectorField field = f;
            (double Min, double Max) range = InspectorAccess.IntegerRange(model, field);
            fields.Add(new ScriptField
            {
                Info = InfoFor(field, range),
                Path = field.Virtual ? null : field.Path,
                Read = () => InspectorAccess.Get(doc, ctx.Brushes, uid, field),
                Write = field.ReadOnly ? null : v =>
                {
                    InspectorAccess.Set(doc, ctx.Brushes, new[] { uid }, field, v);
                    ctx.NoteBrushEdited(uid);
                },
                IntRange = range,
                ReadOnlyHint = field.ReadOnly ? field.Note : null,
            });
        }

        return new ScriptFieldSet("Brush", fields);
    }

    /// <summary>The bound fields of a level property bag.</summary>
    public static ScriptFieldSet ForLevel(ScriptContext ctx, string owner, IReadOnlyList<LevelPropertyField> schema)
    {
        EditorDocument doc = ctx.Document;
        var fields = new List<ScriptField>();
        foreach (LevelPropertyField lp in schema)
        {
            LevelPropertyField p = lp;
            InspectorField field = p.Field;
            (double Min, double Max) range = InspectorAccess.IntegerRange(null, field);
            fields.Add(new ScriptField
            {
                Info = InfoFor(field, range),
                Path = field.Path,
                Read = () => LevelPropertyAccess.Get(doc, p),
                Write = field.ReadOnly ? null : v => LevelPropertyAccess.Set(doc, p, v),

                // The Alpine section is created by the first write; the stock sections never are.
                Guard = () => p.Target == LevelPropertyTarget.AlpineLevelProperties || LevelPropertyAccess.HasSection(doc, p.Target)
                    ? null
                    : ($"This level has no {LevelPropertyAccess.SectionName(p.Target)} section.", "Start from a level created with File ▸ New, which has every section."),
                IntRange = range,
                ReadOnlyHint = field.ReadOnly ? field.Note : null,
            });
        }

        return new ScriptFieldSet(owner, fields);
    }

    /// <summary>
    /// The bound fields of a mover (<see cref="MoverInspectorSchema.MoverFields"/>): plain fields through
    /// <see cref="MoverService.EditMover"/>, movement type (an option name, stored 1-based) and Alpine
    /// hold-open through <see cref="InspectorAccess"/> — the mover inspector's own write path. The
    /// starting keyframe is range-checked against the mover's keyframe count.
    /// </summary>
    public static ScriptFieldSet ForMover(ScriptContext ctx, Group mover)
    {
        var fields = new List<ScriptField>();
        foreach (InspectorField f in MoverInspectorSchema.MoverFields)
        {
            InspectorField field = f;
            (double Min, double Max) range = field.EffectiveKey == "starting_keyframe"
                ? (0, Math.Max(0, (mover.MovingData?.Keyframes.Count ?? 1) - 1))
                : InspectorAccess.IntegerRange(field.Virtual ? null : mover.MovingData, field);
            fields.Add(new ScriptField
            {
                Info = InfoFor(field, range),
                Path = field.Virtual ? null : field.Path,
                Read = () => InspectorAccess.Get(ctx.Movers, mover, field),
                Write = field.ReadOnly ? null : v => InspectorAccess.Set(ctx.Movers, mover, field, v),
                IntRange = range,
                ReadOnlyHint = field.ReadOnly ? field.Note : null,
            });
        }

        return new ScriptFieldSet("Mover", fields);
    }

    /// <summary>The field descriptions of a kind (and event class) without a live object, for <c>catalog.describe</c>.</summary>
    public static ScriptFieldInfo[] Describe(LevelObjectKind kind, EventSchema? eventClass)
    {
        var infos = new List<ScriptFieldInfo>
        {
            Info("name", "Script Name", "string", "Text", doc: "The script name (same as set_name)."),
            Info("hidden", "Hidden", "bool", "Bool", doc: "Hidden in the editor."),
            Info("pos", "Position", "vec", "Vector", doc: "World position in metres."),
            Info("class", "Class", "string", "Text", doc: "The object class (set it when placing).", readOnly: true),
            Info("uid", "UID", "uid", "Uid", doc: "The unique id.", readOnly: true),
            Info("kind", "Kind", "string", "Text", doc: "The object kind.", readOnly: true),
        };

        if (kind == LevelObjectKind.Event)
        {
            infos.Add(Info("delay", "Delay (s)", "float", "Float", doc: "Seconds before the event fires."));
            infos.Add(Info("color", "Color", "color", "Color", doc: "The event colour."));
            foreach (EventFieldSpec spec in eventClass?.Fields ?? Array.Empty<EventFieldSpec>())
            {
                if (!infos.Any(i => i.Name == spec.EffectiveKey))
                {
                    infos.Add(EventField(spec, null, null).Info);
                }
            }

            return infos.ToArray();
        }

        IReadOnlyList<InspectorField> schema = kind == LevelObjectKind.Keyframe ? MoverInspectorSchema.KeyframeFields : ObjectInspectorCatalog.For(kind);
        object? sample = SampleModel(kind);
        foreach (InspectorField f in schema)
        {
            if (!infos.Any(i => i.Name == f.EffectiveKey))
            {
                infos.Add(InfoFor(f, sample is null ? null : InspectorAccess.IntegerRange(sample, f)));
            }
        }

        return infos.ToArray();
    }

    /// <summary>True when the model (or its header) carries a script name.</summary>
    internal static bool HasScriptName(object model) =>
        model.GetType().GetProperty("ScriptName") is not null ||
        model.GetType().GetProperty("Header")?.PropertyType == typeof(ObjectHeader);

    private static object? SampleModel(LevelObjectKind kind)
    {
        try
        {
            return ObjectFactory.Build(kind, 0, Vec3.Zero).Model;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Shadowed(List<ScriptField> fields, string key) => fields.Any(f => f.Key == key);

    private static ScriptField ReadOnlyField(string key, string label, string type, Func<object?> read, string doc) => new()
    {
        Info = Info(key, label, type, type == "uid" ? "Uid" : "Text", doc: doc, readOnly: true),
        Read = read,
    };

    private static ScriptField EventField(EventFieldSpec spec, Func<object?>? read, Action<object?>? write)
    {
        string type = spec.Editor switch
        {
            EventEditor.Text or EventEditor.FilePicker => "string",
            EventEditor.Dropdown => spec.SaveIndex ? "enum" : "string",
            EventEditor.Int or EventEditor.IntAsFloat => "int",
            EventEditor.UidPicker => "uid",
            EventEditor.Float => "float",
            _ => "bool",
        };

        // An int stored in a float slot (IntAsFloat) is exact only within ±2^24.
        (double, double)? range = spec.Editor == EventEditor.IntAsFloat ? (-16777216, 16777216) : (int.MinValue, int.MaxValue);
        string doc = spec.Editor switch
        {
            EventEditor.FilePicker when spec.FileKind != EventFileKind.None => $"A {spec.FileKind.ToString().ToLowerInvariant()} file name.",
            EventEditor.IntAsFloat => "An integer the game stores in a float slot.",
            EventEditor.FlagChar => "A flag the game stores as a one-character string.",
            EventEditor.BoolAsInt => "A flag the game stores as 1/0 in an int slot.",
            _ => string.Empty,
        };

        return new ScriptField
        {
            Info = new ScriptFieldInfo(spec.EffectiveKey, spec.Label, type, spec.Editor.ToString(),
                spec.Options?.ToArray() ?? Array.Empty<string>(), null, null, doc, false, false,
                spec.Slot == EventSlot.None ? string.Empty : spec.Slot.ToString().ToLowerInvariant()),
            Read = read,
            Write = write,
            IntRange = range,
        };
    }

    private static ScriptFieldInfo InfoFor(InspectorField f, (double Min, double Max)? range = null)
    {
        string type = f.Editor switch
        {
            InspectorEditor.Text => "string",
            InspectorEditor.Int => "int",
            InspectorEditor.Float => "float",
            InspectorEditor.Bool => "bool",
            InspectorEditor.Enum => "enum",
            InspectorEditor.Color => "color",
            InspectorEditor.Vector => "vec",
            _ => "uid",
        };
        string editor = f.Mask != 0
            ? (f.Editor == InspectorEditor.Enum ? "Bits" : f.Editor == InspectorEditor.Bool ? "Flag" : f.Editor.ToString())
            : f.Editor.ToString();
        return new ScriptFieldInfo(f.EffectiveKey, f.Label, type, editor,
            f.Options?.ToArray() ?? Array.Empty<string>(),
            f.Min ?? (range is { } r && type is "int" && r.Min > int.MinValue ? r.Min : null),
            f.Max ?? (range is { } r2 && type is "int" && r2.Max < int.MaxValue ? r2.Max : null),
            f.Note ?? string.Empty, f.ReadOnly, f.EditorOnly, string.Empty);
    }

    private static ScriptFieldInfo Info(string key, string label, string type, string editor, string doc = "", bool readOnly = false) =>
        new(key, label, type, editor, Array.Empty<string>(), null, null, doc, readOnly, false, string.Empty);
}
