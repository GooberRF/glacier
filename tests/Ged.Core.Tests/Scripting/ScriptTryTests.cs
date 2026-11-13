using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// <c>ged.try(fn, ...)</c>: a failed fn leaves no trace in the level (byte-identical, same UIDs for what follows),
/// a successful one keeps its changes and returns its values, aborts (budget, time, Stop, nesting) are never caught,
/// tries nest, and the run is still exactly one undo step.
/// </summary>
public sealed class ScriptTryTests
{
    private static ScriptRunResult Run(EditorDocument doc, string lua, ScriptExecutionLimits? limits = null) =>
        ScriptTestFixture.Runner().Run(
            ScriptTestFixture.Services(doc, new BrushEditor(doc)),
            lua,
            new ScriptRunOptions { ChunkName = "try_test", AllowDestructive = true, Limits = limits ?? ScriptExecutionLimits.Default },
            new ScriptLog());

    private static EditorDocument Doc() => LevelTemplates.CreateNew(KitTestHarness.FixedDate);

    private const string Before = "level.brush{ shape = \"box\", pos = {0, 2, 0}, size = {8, 4, 8}, air = true }\n";
    private const string After = "level.place{ kind = \"light\", name = \"after\", pos = {0, 3, 0} }\n";

    [Fact]
    public void A_Failed_Try_Leaves_The_Level_Byte_Identical()
    {
        EditorDocument untouched = Doc();
        byte[] original = untouched.SaveToBytes(updateTimestamp: false);
        ScriptRunResult nothing = Run(untouched, @"
local ok, err = ged.try(function()
  level.brush{ shape = ""box"", pos = {0, 2, 0}, size = {4, 4, 4} }
  level.place{ kind = ""light"", name = ""doomed"", pos = {0, 3, 0} }
  error(""boom"")
end)
assert(not ok)
return err.message");
        Assert.True(nothing.Success, nothing.Error?.ToDisplayString());
        Assert.Contains("boom", nothing.ReturnValue, StringComparison.Ordinal);
        Assert.Equal(0, nothing.UndoNodesAdded);
        Assert.Equal(original, untouched.SaveToBytes(updateTimestamp: false));

        // Around other changes: the same bytes (and UIDs) as the run without the failed try.
        EditorDocument withTry = Doc(), plain = Doc();
        ScriptRunResult a = Run(withTry, Before + @"
local ok = ged.try(function()
  level.brush{ shape = ""box"", pos = {0, 1, 0}, size = {1, 1, 1}, detail = true }
  level.place{ kind = ""light"", name = ""doomed"", pos = {1, 3, 0} }
  level.props:set(""level_name"", ""Changed"")
  ged.fail(""nope"", ""a hint"")
end)
assert(not ok)
" + After);
        ScriptRunResult b = Run(plain, Before + After);
        Assert.True(a.Success, a.Error?.ToDisplayString());
        Assert.True(b.Success, b.Error?.ToDisplayString());
        Assert.Equal(plain.SaveToBytes(updateTimestamp: false), withTry.SaveToBytes(updateTimestamp: false));
        Assert.Equal(b.CreatedUids, a.CreatedUids);
        Assert.Equal(b.Changes.OrderBy(k => k.Key), a.Changes.OrderBy(k => k.Key));
        Assert.Equal(1, a.UndoNodesAdded);
    }

    [Fact]
    public void A_Successful_Try_Keeps_Its_Changes_And_Returns_Its_Values()
    {
        EditorDocument doc = Doc();
        int brushes = new BrushEditor(doc).Brushes.Count;
        ScriptRunResult r = Run(doc, @"
local ok, sum, word = ged.try(function(a, b) return a + b, ""x"" end, 2, 3)
assert(ok == true and sum == 5 and word == ""x"")
local ok2, brush = ged.try(level.brush, { shape = ""box"", pos = {0, 2, 0}, size = {4, 4, 4}, air = true })
assert(ok2 and brush.air)
local ok3 = ged.try(function() end)
assert(ok3 == true)
return ""ok""");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.Equal(brushes + 1, new BrushEditor(doc).Brushes.Count);
    }

    [Fact]
    public void The_Error_Is_Structured_And_Prints_Its_Message_And_Hint()
    {
        ScriptRunResult r = Run(Doc(), @"
local ok, err = ged.try(ged.fail, ""no room here"", ""move it"")
assert(not ok)
assert(err.kind == ""api"" and err.message == ""no room here"" and err.hint == ""move it"", tostring(err.kind))
local ok2, err2 = ged.try(function() local t = nil; return t.x end)
assert(not ok2 and err2.kind == ""runtime"" and err2.line == 5, tostring(err2.line))
local ok3, err3 = ged.try(function() local kit = ged.import(""kit""); kit.room{ name = ""r"", pos = {0, 0, 0}, size = {4, 4, 4}, widht = 3 } end)
assert(not ok3 and err3.kind == ""api"" and err3.message:find(""widht""), err3.message)
return tostring(err)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("no room here  Hint: move it", r.ReturnValue);

        ScriptRunResult notFn = Run(Doc(), "ged.try(42)");
        Assert.False(notFn.Success);
        Assert.Contains("expected a function", notFn.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tries_Nest()
    {
        EditorDocument doc = Doc(), expected = Doc();
        ScriptRunResult r = Run(doc, @"
local ok_outer = ged.try(function()
  level.brush{ shape = ""box"", pos = {0, 2, 0}, size = {8, 4, 8}, air = true }
  local ok_inner = ged.try(function()
    level.place{ kind = ""light"", name = ""inner"", pos = {0, 3, 0} }
    error(""inner fails"")
  end)
  assert(not ok_inner)
end)
assert(ok_outer)
local ok_both = ged.try(function()
  ged.try(function() level.place{ kind = ""light"", name = ""kept_then_dropped"", pos = {1, 3, 0} } end)
  error(""outer fails after a successful inner try"")
end)
assert(not ok_both)
return #level.objects_named(""inner"") + #level.objects_named(""kept_then_dropped"")");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("0", r.ReturnValue);
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.True(Run(expected, Before).Success);
        Assert.Equal(expected.SaveToBytes(updateTimestamp: false), doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Aborts_Inside_A_Try_Still_End_The_Run()
    {
        EditorDocument doc = Doc();
        byte[] original = doc.SaveToBytes(updateTimestamp: false);
        ScriptRunResult budget = Run(doc, @"
level.brush{ shape = ""box"", pos = {0, 2, 0}, size = {8, 4, 8}, air = true }
local ok = ged.try(function() while true do end end)
level.place{ kind = ""light"", name = ""never"", pos = {0, 3, 0} }",
            new ScriptExecutionLimits { InstructionBudget = 2_000_000, Timeout = TimeSpan.Zero, YieldEvery = 1_000 });
        Assert.False(budget.Success);
        Assert.Equal(ScriptErrorKind.Aborted, budget.Error!.Kind);
        Assert.Equal(original, doc.SaveToBytes(updateTimestamp: false));

        // A try that keeps retrying itself hits the callback nesting cap, which is an abort too.
        ScriptRunResult deep = Run(doc, "local function f(n) return ged.try(f, n + 1) end\nreturn f(1)");
        Assert.False(deep.Success);
        Assert.Equal(ScriptErrorKind.Aborted, deep.Error!.Kind);
        Assert.Contains("nesting", deep.Error.Message, StringComparison.Ordinal);
        Assert.Equal(original, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Retrying_Seeds_Keeps_Only_The_Attempt_That_Succeeded()
    {
        EditorDocument doc = Doc();
        ScriptRunResult r = Run(doc, @"
local made = 0
local function attempt(seed)
  level.brush{ shape = ""box"", pos = {seed * 10, 2, 0}, size = {4, 4, 4}, air = true }
  made = made + 1
  if seed < 3 then error(""seed "" .. seed .. "" refused"") end
  return seed
end
for seed = 1, 5 do
  local ok, got = ged.try(attempt, seed)
  if ok then return string.format(""seed %d after %d attempts, %d brushes"", got, made, level.brush_count) end
end");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("seed 3 after 3 attempts, 1 brushes", r.ReturnValue);
        Assert.Equal(1, r.UndoNodesAdded);
    }
}
