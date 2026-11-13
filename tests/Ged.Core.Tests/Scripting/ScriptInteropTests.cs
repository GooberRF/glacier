using System;
using System.Collections.Generic;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using MoonSharp.Interpreter;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Pins the MoonSharp interop behaviours the V2 surface is built on:
/// a Lua table converts to <see cref="ScriptArgs"/> through the registered custom conversion and
/// that conversion wins overload resolution (while V1 positional call shapes keep their
/// overload); number vs string and table vs userdata overloads dispatch correctly; CLR
/// dictionaries / lists come back to Lua as tables; and callbacks invoked from C# run as nested
/// calls that MoonSharp does NOT auto-yield (so their instructions escape the run's budget),
/// whereas an inner coroutine created from C# can be budgeted.
/// </summary>
public sealed class ScriptInteropTests
{
    private static (ScriptRunResult Result, ScriptLog Log) Run(EditorDocument doc, string src) => ScriptTestFixture.Run(doc, src);

    [Fact]
    public void V1_Positional_Place_Still_Selects_The_String_Overload()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = Run(doc, "local o = level.place('Light', 1, 2, 3) return o.kind .. ':' .. o.x .. ',' .. o.y .. ',' .. o.z");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("Light:1,2,3", r.ReturnValue);
    }

    [Theory]
    [InlineData("return level.brush{size = 2}.uid > 0", "true")]          // dot call, table arg
    [InlineData("return level:brush{size = 2}.uid > 0", "true")]          // colon call, table arg
    [InlineData("local b = level.brush{} return b:reorder('start').time_index", "0")]   // string overload
    [InlineData("level.brush{} local b = level.brush{} return b:reorder(0).time_index", "0")]  // number overload
    [InlineData("local b = level.brush{} return b:set_rot{yaw = 90}.rot.yaw", "90")]          // table overload
    [InlineData("local b = level.brush{} return b:set_rot(ged.rot(-90)).rot.yaw", "-90")]     // userdata overload
    [InlineData("local b = level.brush{} return b:copy().uid ~= b.uid", "true")]             // optional args omitted
    [InlineData("local t = ged.describe('brush') return type(t.members) .. ':' .. tostring(#t.members > 10)", "table:true")]
    [InlineData("local q = level.all_brushes() return type(q.uids)", "table")]
    public void Overloads_And_Conversions_Dispatch(string src, string expected)
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = Run(doc, src);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(expected, r.ReturnValue);
    }

    [Fact]
    public void Nested_Tables_Userdata_And_Lists_Convert()
    {
        Script s = NewBareScript();
        ScriptArgs a = Convert(s, "return { pos = ged_vec, list = {1, 2, 3}, nested = { flag = true, name = 'x' }, n = 1.5 }");
        Assert.Equal(new Model.Vec3(1, 2, 3), a.Named["pos"]);
        ScriptArgs list = Assert.IsType<ScriptArgs>(a.Named["list"]);
        Assert.Equal(new object?[] { 1.0, 2.0, 3.0 }, list.Items);
        ScriptArgs nested = Assert.IsType<ScriptArgs>(a.Named["nested"]);
        Assert.Equal(true, nested.Named["flag"]);
        Assert.Equal("x", nested.Named["name"]);
        Assert.Equal(1.5, a.Named["n"]);
    }

    [Theory]
    [InlineData("return { [1] = 1, [3] = 3 }", "missing")]          // holes in the list part
    [InlineData("return { [1.5] = 1 }", "string keys")]             // non-integer number key
    [InlineData("return { [true] = 1 }", "string keys")]            // boolean key
    [InlineData("return { f = function() end }", "functions")]      // function value
    public void Invalid_Tables_Are_Rejected(string src, string fragment)
    {
        Script s = NewBareScript();
        ScriptApiException ex = Assert.Throws<ScriptApiException>(() => Convert(s, src));
        Assert.Contains(fragment, ex.Message + " " + ex.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Converter_Errors_Surface_As_Api_Diagnostics_And_Apply_Nothing()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = Run(doc, "local x = 1\nlevel.brush{ [3] = 1 }");
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Contains("1..n", r.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.Content is IO.Rfl.Sections.BrushesSection);
    }

    [Fact]
    public void Nil_Table_Argument_Is_Treated_As_Empty_Options()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        (ScriptRunResult r, _) = Run(doc, "return level.brush(nil).face_count");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("6", r.ReturnValue);
    }

    [Fact]
    public void Clr_Dictionaries_And_Lists_Become_Lua_Tables()
    {
        Script s = NewBareScript();
        s.Globals["make"] = (Func<Dictionary<string, object?>>)(() => new Dictionary<string, object?>
        {
            ["a"] = 1,
            ["nested"] = new Dictionary<string, object?> { ["c"] = true },
            ["list"] = new List<int> { 3, 4 },
        });
        DynValue v = s.DoString("local d = make() return tostring(d.a) .. tostring(d.nested.c) .. d.list[1] .. #d.list");
        Assert.Equal("1true32", v.ToPrintString());
    }

    [Fact]
    public void Nested_Callbacks_Are_Not_Auto_Yielded_But_An_Inner_Coroutine_Can_Be_Budgeted()
    {
        // A Lua function called back from C# (where/each predicates, and later module top-levels)
        // runs as a nested call with yielding disabled: the outer run sees zero auto-yields, so its
        // instruction budget / timeout cannot interrupt that callback.
        Script s = NewBareScript();
        s.Globals["callback"] = (Func<Closure, DynValue>)(c => c.Call());
        Assert.Equal(0, CountYields(s, "local n = 0 callback(function() for i = 1, 100000 do n = n + 1 end end) return n", out string r1));
        Assert.Equal("100000", r1);

        // The workable mechanism if a callback must be budgeted: run it as its own coroutine from C#.
        int inner = 0;
        s.Globals["budgeted"] = (Func<Closure, DynValue>)(c =>
        {
            DynValue co = c.OwnerScript.CreateCoroutine(DynValue.NewClosure(c));
            co.Coroutine.AutoYieldCounter = 1000;
            DynValue res = co.Coroutine.Resume();
            while (res.Type == DataType.YieldRequest)
            {
                inner++;
                res = co.Coroutine.Resume();
            }

            return res;
        });
        CountYields(s, "local n = 0 budgeted(function() for i = 1, 100000 do n = n + 1 end end) return n", out string r2);
        Assert.Equal("100000", r2);
        Assert.True(inner > 50, $"expected the inner coroutine to auto-yield, got {inner}");
    }

    private static int CountYields(Script s, string src, out string result)
    {
        DynValue co = s.CreateCoroutine(s.LoadString(src));
        co.Coroutine.AutoYieldCounter = 1000;
        int yields = 0;
        DynValue res = co.Coroutine.Resume();
        while (res.Type == DataType.YieldRequest)
        {
            yields++;
            res = co.Coroutine.Resume();
        }

        result = res.ToPrintString();
        return yields;
    }

    private static Script NewBareScript()
    {
        _ = new Ged.Scripting.MoonSharpHost(); // ensures the facade registration (incl. the table converter)
        var s = new Script(CoreModules.Preset_HardSandbox);
        s.Globals["ged_vec"] = new Model.Vec3(1, 2, 3);
        return s;
    }

    private static ScriptArgs Convert(Script s, string src)
    {
        DynValue t = s.DoString(src);
        return (ScriptArgs)t.ToObject(typeof(ScriptArgs));
    }
}
