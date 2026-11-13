using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Option tables are declared once (<see cref="ScriptOptionSets"/>) and that declaration both decides
/// which keys a function accepts and documents them in <c>ged.describe</c>, the generated reference
/// and the Lua stub — every key a table-argument function accepts is documented and vice versa.
/// </summary>
public sealed class ScriptOptionDocsTests
{
    /// <summary>Table arguments that are not option tables: field maps, member lists, link lists, UV lists.</summary>
    private static readonly HashSet<string> NotOptionTables = new(StringComparer.Ordinal)
    {
        "ScriptObjectHandle.SetMany", "ScriptBrushHandle.SetMany", "ScriptMoverHandle.SetMany", "ScriptPropertyBag.SetMany",
        "ScriptObjectHandle.LinkMany", "ScriptObjectHandle.BackLink", "ScriptGroupHandle.Remove", "ScriptFaceHandle.SetUvs",
    };

    private static IEnumerable<MethodInfo> TableArgMethods() =>
        ScriptApiReference.SurfaceTypes()
            .SelectMany(t => t.Type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(ScriptArgs)));

    [Fact]
    public void Every_Option_Table_Function_Declares_Its_Options()
    {
        string[] undeclared = TableArgMethods()
            .Where(m => m.GetCustomAttribute<ScriptOptionsAttribute>() is null && !NotOptionTables.Contains($"{m.DeclaringType!.Name}.{m.Name}"))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}").ToArray();
        Assert.True(undeclared.Length == 0, "table-argument functions without [ScriptOptions]: " + string.Join(", ", undeclared));

        // Every attribute resolves, and every option is fully documented.
        foreach (MethodInfo m in TableArgMethods())
        {
            foreach (ScriptOptionSet set in ScriptOptionSets.Of(m))
            {
                Assert.NotEmpty(set.Options);
                Assert.Equal(set.Keys.Length, set.Keys.Distinct(StringComparer.Ordinal).Count());
                Assert.All(set.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Type) || string.IsNullOrWhiteSpace(o.Doc), $"{set.Api}.{o.Name}"));
            }
        }
    }

    [Theory]
    [InlineData("level.brush{zz_probe = 1}", nameof(ScriptOptionSets.Brush))]
    [InlineData("level.brush_from_points{zz_probe = 1}", nameof(ScriptOptionSets.BrushFromPoints))]
    [InlineData("level.brushes_from_points{zz_probe = 1}", nameof(ScriptOptionSets.BrushesFromPoints))]
    [InlineData("level.place{zz_probe = 1}", nameof(ScriptOptionSets.Place))]
    [InlineData("level.player_start{zz_probe = 1}", nameof(ScriptOptionSets.PlayerStart))]
    [InlineData("level.create_mover{zz_probe = 1}", nameof(ScriptOptionSets.CreateMover))]
    [InlineData("level.create_group{zz_probe = 1}", nameof(ScriptOptionSets.CreateGroup))]
    [InlineData("level.brushes_in{zz_probe = 1}", nameof(ScriptOptionSets.Box))]
    [InlineData("prefab.place{zz_probe = 1}", nameof(ScriptOptionSets.PrefabPlace))]
    [InlineData("level.brush{}:clip{zz_probe = 1}", nameof(ScriptOptionSets.Clip))]
    [InlineData("level.brush{}:set_textures{zz_probe = 1}", nameof(ScriptOptionSets.RoleTextures))]
    [InlineData("level.brush{}:box_map{zz_probe = 1}", nameof(ScriptOptionSets.BoxMap))]
    [InlineData("level.all_brushes():set_flags{zz_probe = 1}", nameof(ScriptOptionSets.Flags))]
    [InlineData("level.brush{}:set_rot{zz_probe = 1}", nameof(ScriptOptionSets.EulerRotation))]
    [InlineData("level.create_group{name = 'g', brushes = {level.brush{}}}:add{zz_probe = 1}", nameof(ScriptOptionSets.GroupMembers))]
    [InlineData("level.create_mover{brushes = {level.brush{}}}:add_keyframe{zz_probe = 1}", nameof(ScriptOptionSets.AddKeyframe))]
    public void The_Rejection_Lists_Exactly_The_Declared_Keys(string call, string set)
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(KitTestHarness.FixedDate), call);
        Assert.False(r.Success);
        Assert.Contains("unknown option 'zz_probe'", r.Error!.Message, StringComparison.Ordinal);
        string expected = "Valid options: " + string.Join(", ", ScriptOptionSets.Find(set)!.Keys) + ".";
        Assert.EndsWith(expected, r.Error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Lists_The_Options_With_Types_And_Defaults()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(KitTestHarness.FixedDate),
            "local info = ged.describe('level')\n" +
            "for _, m in ipairs(info.members) do\n" +
            "  if m.name == 'brush' then\n" +
            "    local out = {}\n" +
            "    for _, o in ipairs(m.options) do out[#out + 1] = o.name .. ':' .. o.type .. '=' .. o.default end\n" +
            "    return table.concat(out, ';') .. '\\n##\\n' .. info.text\n" +
            "  end\n" +
            "end");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] parts = r.ReturnValue!.Split("\n##\n", 2);
        string[] options = parts[0].Split(';');
        Assert.Equal(ScriptOptionSets.Brush.Keys, options.Select(o => o.Split(':')[0]).ToArray());
        Assert.Contains("shape:string=\"box\"", options);
        Assert.Contains("sides:integer=12", options);
        Assert.Contains("      options:", parts[1], StringComparison.Ordinal);
        Assert.Contains("        size: number | vec = {4, 4, 4} — Full extents", parts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_Reference_And_Stub_List_The_Options()
    {
        string md = ScriptApiReference.GenerateMarkdown();
        Assert.Contains("  - `stacks` (integer, default 1 (sphere 6)) — ", md, StringComparison.Ordinal);
        Assert.Contains("  Options (one form):", md, StringComparison.Ordinal); // set_rot's two forms
        Assert.Contains("  - `button_class` (string, default none) — ", md, StringComparison.Ordinal); // the kit's ---@opt lines

        string stub = ScriptApiReference.GenerateLuaStub();
        Assert.Contains("---   pos: vec = {0, 0, 0} — The brush centre in world metres.", stub, StringComparison.Ordinal);
    }
}
