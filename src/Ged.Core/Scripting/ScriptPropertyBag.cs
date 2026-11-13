using System.Collections.Generic;
using Ged.Core.Tables;

namespace Ged.Core.Scripting;

/// <summary>
/// A level property set (Lua type <c>props</c>): <c>level.props</c> (level_info +
/// level_properties + the read-only RFL version) or <c>level.alpine</c> (alpine_level_properties).
/// Reads and writes go through <see cref="LevelPropertyAccess"/> — the same path as the Level
/// Properties dialog — so a script edit is identical to the dialog's and joins the run's undo step.
/// </summary>
[ScriptDoc("Level properties (level.props / level.alpine): get, set, set_many, fields, all. Edits are undoable; the Alpine section is created on the first change.", Since = 2)]
public sealed class ScriptPropertyBag
{
    private readonly ScriptContext _ctx;
    private readonly string _name;
    private readonly IReadOnlyList<LevelPropertyField> _schema;

    internal ScriptPropertyBag(ScriptContext ctx, string name, IReadOnlyList<LevelPropertyField> schema)
    {
        _ctx = ctx;
        _name = name;
        _schema = schema;
    }

    private ScriptFieldSet Bound() => ScriptFieldCatalog.ForLevel(_ctx, _name == "level.alpine" ? "Alpine level" : "Level", _schema);

    [ScriptDoc("The value of a property by key or label (colours are {r,g,b,a}, flags are booleans).", Example = "print(level.props:get(\"level_name\"))", Since = 2)]
    public object? Get(string name) => Bound().Get($"{_name}:get", name);

    [ScriptDoc("Sets one property (validated; equal values change nothing). Returns the bag.", Example = "level.props:set(\"fog_far\", 200)", Since = 2)]
    public ScriptPropertyBag Set(string name, object? value)
    {
        Bound().Set($"{_name}:set", name, value);
        _ctx.Changes.Record("level property");
        return this;
    }

    [ScriptDoc("Sets several properties from {key = value, …}, applied in schema order. Returns the bag.", Example = "level.props:set_many{level_name = \"Arena\", multiplayer = true}", Since = 2)]
    public ScriptPropertyBag SetMany(ScriptArgs values)
    {
        Bound().SetMany($"{_name}:set_many", values);
        _ctx.Changes.Record("level property");
        return this;
    }

    [ScriptDoc("Every property's description (field_info[]), in dialog order.", Example = "for _, f in ipairs(level.alpine:fields()) do print(f.name, f.type) end", Since = 2)]
    public ScriptFieldInfo[] Fields() => Bound().Infos();

    [ScriptDoc("A table {key = value} of every property that has a value.", Example = "local p = level.props:all() print(p.author)", Since = 2)]
    public Dictionary<string, object?> All() => Bound().Props();

    public override string ToString() => _name;
}
