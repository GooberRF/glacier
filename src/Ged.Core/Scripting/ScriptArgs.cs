using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// An engine-neutral Lua table argument. The facade has no engine dependency, so the
/// host converts every Lua table passed where a <see cref="ScriptArgs"/> is expected: string keys
/// land in <see cref="Named"/>, the array part (keys 1..n) in <see cref="Items"/> in order. Values
/// are <c>double</c>, <c>bool</c>, <c>string</c>, a nested <see cref="ScriptArgs"/>, or the CLR
/// object behind a userdata (a <c>vec</c>, a handle, a record). The typed readers validate and
/// throw <see cref="ScriptApiException"/> with actionable messages — nothing is silently coerced.
/// </summary>
public sealed class ScriptArgs
{
    private static readonly IReadOnlyDictionary<string, object?> NoNamed = new Dictionary<string, object?>(StringComparer.Ordinal);

    public ScriptArgs(IReadOnlyDictionary<string, object?>? named = null, IReadOnlyList<object?>? items = null)
    {
        Named = named ?? NoNamed;
        Items = items ?? Array.Empty<object?>();
    }

    /// <summary>String-keyed entries.</summary>
    public IReadOnlyDictionary<string, object?> Named { get; }

    /// <summary>The array part, 1..n in order.</summary>
    public IReadOnlyList<object?> Items { get; }

    /// <summary>True when the table has a (non-nil) value for <paramref name="key"/>.</summary>
    public bool Has(string key) => Named.TryGetValue(key, out object? v) && v is not null;

    /// <summary>A string option, or <paramref name="fallback"/> when absent.</summary>
    public string? String(string api, string key, string? fallback = null)
    {
        if (!Named.TryGetValue(key, out object? v) || v is null)
        {
            return fallback;
        }

        return v as string ?? throw TypeError(api, key, "string", v);
    }

    /// <summary>A finite number option, or <paramref name="fallback"/> when absent.</summary>
    public double Number(string api, string key, double fallback) => NumberOrNull(api, key) ?? fallback;

    /// <summary>A finite number option, or null when absent.</summary>
    public double? NumberOrNull(string api, string key)
    {
        if (!Named.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        if (v is not double d)
        {
            throw TypeError(api, key, "number", v);
        }

        if (!double.IsFinite(d))
        {
            throw new ScriptApiException($"{api}: '{key}' must be a finite number (got {Format(d)}).");
        }

        return d;
    }

    /// <summary>An integral number option, or <paramref name="fallback"/> when absent.</summary>
    public int Int(string api, string key, int fallback)
    {
        double? d = NumberOrNull(api, key);
        return d is null ? fallback : ToInt(api, key, d.Value);
    }

    /// <summary>A boolean option, or <paramref name="fallback"/> when absent (numbers/strings are rejected).</summary>
    public bool Bool(string api, string key, bool fallback)
    {
        if (!Named.TryGetValue(key, out object? v) || v is null)
        {
            return fallback;
        }

        return v is bool b ? b : throw TypeError(api, key, "boolean", v);
    }

    /// <summary>A vec-like option (vec, <c>{x,y,z}</c> or <c>{x=,y=,z=}</c>), or <paramref name="fallback"/>.</summary>
    public Vec3 Vec(string api, string key, Vec3 fallback) => VecOrNull(api, key) ?? fallback;

    /// <summary>A vec-like option, or null when absent.</summary>
    public Vec3? VecOrNull(string api, string key)
    {
        if (!Named.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        return ToVec(api, key, v);
    }

    /// <summary>A nested table option, or null when absent.</summary>
    public ScriptArgs? Table(string api, string key)
    {
        if (!Named.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        return v as ScriptArgs ?? throw TypeError(api, key, "table", v);
    }

    /// <summary>Rejects any key the documented option set does not declare (see <see cref="ScriptOptionSet"/>).</summary>
    public void RejectUnknown(ScriptOptionSet options) => RejectUnknown(options.Api, options.Keys);

    /// <summary>As <see cref="RejectUnknown(ScriptOptionSet)"/>, naming the function <paramref name="api"/> in the error.</summary>
    public void RejectUnknown(string api, ScriptOptionSet options) => RejectUnknown(api, options.Keys);

    /// <summary>
    /// Rejects any key not in <paramref name="allowedKeys"/> (and any positional value): strict
    /// table args. The hint names the nearest valid key and lists the valid ones.
    /// </summary>
    public void RejectUnknown(string api, params string[] allowedKeys)
    {
        allowedKeys ??= Array.Empty<string>();
        string valid = string.Join(", ", allowedKeys);
        foreach (string key in Named.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (Array.IndexOf(allowedKeys, key) >= 0)
            {
                continue;
            }

            IReadOnlyList<string> near = ScriptSuggest.Nearest(key, allowedKeys, 1);
            string hint = near.Count > 0
                ? $"Did you mean '{near[0]}'? Valid options: {valid}."
                : $"Valid options: {valid}.";
            throw new ScriptApiException($"{api}: unknown option '{key}'.", hint);
        }

        if (Items.Count > 0)
        {
            throw new ScriptApiException(
                $"{api}: unexpected positional value #1 — this function takes named options.",
                $"Valid options: {valid}.");
        }
    }

    /// <summary>
    /// Converts a vec-like value: a <c>vec</c> userdata, an array <c>{x, y, z}</c>, or a record
    /// <c>{x=, y=, z=}</c>. Components must be finite numbers.
    /// </summary>
    public static Vec3 ToVec(string api, string what, object? value)
    {
        switch (value)
        {
            case Vec3 v:
                return v;
            case ScriptArgs t when t.Items.Count == 3 && t.Named.Count == 0:
                return new Vec3(Component(api, what, t.Items[0]), Component(api, what, t.Items[1]), Component(api, what, t.Items[2]));
            case ScriptArgs t when t.Items.Count == 0 && t.Named.Count == 3 && t.Has("x") && t.Has("y") && t.Has("z"):
                return new Vec3(Component(api, what, t.Named["x"]), Component(api, what, t.Named["y"]), Component(api, what, t.Named["z"]));
            default:
                throw new ScriptApiException($"{api}: '{what}' must be a vec, {{x,y,z}} or {{x=,y=,z=}}.",
                    $"For example {what} = {{0, 2, 0}} or {what} = ged.vec(0, 2, 0).");
        }
    }

    /// <summary>Converts an integral number (throws when fractional or out of <c>int</c> range).</summary>
    internal static int ToInt(string api, string what, double d)
    {
        if (!double.IsFinite(d) || Math.Floor(d) != d || d < int.MinValue || d > int.MaxValue)
        {
            throw new ScriptApiException($"{api}: '{what}' must be an integer (got {Format(d)}).");
        }

        return (int)d;
    }

    /// <summary>The Lua-facing type name of a converted value (for error messages).</summary>
    internal static string LuaTypeOf(object? value) => value switch
    {
        null => "nil",
        double or float or int or long => "number",
        bool => "boolean",
        string => "string",
        ScriptArgs => "table",
        _ => ScriptApiReference.LuaTypeName(value.GetType()),
    };

    internal static string Format(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    private static float Component(string api, string what, object? v)
    {
        if (v is double d && double.IsFinite(d))
        {
            return (float)d;
        }

        throw new ScriptApiException($"{api}: '{what}' must be a vec, {{x,y,z}} or {{x=,y=,z=}}.",
            $"Every component must be a finite number (got {LuaTypeOf(v)}).");
    }

    private static ScriptApiException TypeError(string api, string key, string type, object? got) =>
        new($"{api}: '{key}' must be a {type} (got {LuaTypeOf(got)}).");

    public override string ToString() =>
        $"table({Items.Count} items, {Named.Count} keys)";
}
