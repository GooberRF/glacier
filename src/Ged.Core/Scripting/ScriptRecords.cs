using System.Globalization;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>An axis-aligned world box (Lua type <c>aabb</c>).</summary>
[ScriptDoc("An axis-aligned world-space box in metres: min, max, size and center.", Since = 2)]
public sealed class ScriptAabb
{
    internal ScriptAabb(Vec3 min, Vec3 max)
    {
        Min = min;
        Max = max;
    }

    [ScriptDoc("The minimum corner (vec).", Since = 2)]
    public Vec3 Min { get; }

    [ScriptDoc("The maximum corner (vec).", Since = 2)]
    public Vec3 Max { get; }

    [ScriptDoc("The extents max - min (vec, metres).", Since = 2)]
    public Vec3 Size => Max.Sub(Min);

    [ScriptDoc("The midpoint of min and max (vec).", Since = 2)]
    public Vec3 Center => Min.Add(Max).Scale(0.5f);

    public override string ToString() => $"aabb(min={Min}, max={Max})";
}

/// <summary>One texture coordinate in tile units (Lua type <c>uv</c>).</summary>
[ScriptDoc("A texture coordinate in tile units (1.0 = one full repeat of the texture).", Since = 2)]
public sealed class ScriptUv
{
    internal ScriptUv(double u, double v)
    {
        U = u;
        V = v;
    }

    [ScriptDoc("Horizontal texture coordinate, tile units.", Since = 2)]
    public double U { get; }

    [ScriptDoc("Vertical texture coordinate, tile units (+V points down the texture).", Since = 2)]
    public double V { get; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"uv({ScriptArgs.Format(U)}, {ScriptArgs.Format(V)})");
}

/// <summary>A described script type (Lua type <c>type_info</c>), returned by <c>ged.describe</c>.</summary>
[ScriptDoc("The description of a script type or global, returned by ged.describe.", Since = 2)]
public sealed class ScriptTypeInfo
{
    internal ScriptTypeInfo(string name, string summary, ScriptMemberInfo[] members, string text)
    {
        Name = name;
        Summary = summary;
        Members = members;
        Text = text;
    }

    [ScriptDoc("The Lua type or global name, e.g. \"brush\" or \"level\".", Since = 2)]
    public string Name { get; }

    [ScriptDoc("One-line description of the type.", Since = 2)]
    public string Summary { get; }

    [ScriptDoc("Every member of the type, sorted by name (member_info[]).", Since = 2)]
    public ScriptMemberInfo[] Members { get; }

    [ScriptDoc("A formatted multi-line listing of the type and its members.", Since = 2)]
    public string Text { get; }

    public override string ToString() => Text;
}

/// <summary>One member of a described type (Lua type <c>member_info</c>).</summary>
[ScriptDoc("One member of a described type: name, kind, signature, return type and documentation.", Since = 2)]
public sealed class ScriptMemberInfo
{
    internal ScriptMemberInfo(string name, string kind, string signature, string returns, string doc, string example, int since,
        ScriptOptionInfo[]? options = null)
    {
        Name = name;
        Kind = kind;
        Signature = signature;
        Returns = returns;
        Doc = doc;
        Example = example;
        Since = since;
        Options = options ?? System.Array.Empty<ScriptOptionInfo>();
    }

    [ScriptDoc("The keys its table argument accepts (option_info[]: name, type, default, doc); empty when it takes no option table.", Since = 2)]
    public ScriptOptionInfo[] Options { get; }

    [ScriptDoc("The Lua member name (snake_case).", Since = 2)]
    public string Name { get; }

    [ScriptDoc("\"property\" or \"method\".", Since = 2)]
    public string Kind { get; }

    [ScriptDoc("The call signature, e.g. \"set_pos(x: number, y: number, z: number)\"; the name for a property.", Since = 2)]
    public string Signature { get; }

    [ScriptDoc("The Lua type returned (or the property type).", Since = 2)]
    public string Returns { get; }

    [ScriptDoc("The member's one-line documentation (\"\" when undocumented).", Since = 2)]
    public string Doc { get; }

    [ScriptDoc("A Lua example (\"\" when none).", Since = 2)]
    public string Example { get; }

    [ScriptDoc("The API version that introduced the member.", Since = 2)]
    public int Since { get; }

    public override string ToString() => $"{Signature} -> {Returns}";
}

/// <summary>One key of a table argument (Lua type <c>option_info</c>), listed by <c>member_info.options</c>.</summary>
[ScriptDoc("One key of a table argument: name, type, default and what it does.", Since = 2)]
public sealed class ScriptOptionInfo
{
    internal ScriptOptionInfo(ScriptOption option)
    {
        Name = option.Name;
        Type = option.Type;
        Default = option.Default ?? string.Empty;
        Doc = option.Doc;
    }

    [ScriptDoc("The key as written in the table, e.g. \"size\".", Since = 2)]
    public string Name { get; }

    [ScriptDoc("The accepted Lua type(s), e.g. \"number | vec\".", Since = 2)]
    public string Type { get; }

    [ScriptDoc("What an omitted key means (\"\" when the key is required or has no default).", Since = 2)]
    public string Default { get; }

    [ScriptDoc("What the key does, with units and ranges.", Since = 2)]
    public string Doc { get; }

    public override string ToString() => ScriptApiReference.FormatOption(new ScriptOption(Name, Type, Default.Length > 0 ? Default : null, Doc));
}
