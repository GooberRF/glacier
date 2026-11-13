using System;
using System.Diagnostics;
using System.Threading;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// A Lua function called back from C# (a query predicate, an <c>each</c> action, a
/// <c>ged.group</c> body, a <c>table.sort</c> comparator, a <c>string.gsub</c> replacement) is
/// budgeted exactly like the script itself: an infinite loop inside it times out, exhausts the
/// instruction budget, or is cancelled with the normal Aborted diagnostic — it can never hang the
/// host (the editor's UI thread in attach mode). Normal callbacks still work, deterministically, and
/// their instructions count toward the same budget.
/// </summary>
public sealed class ScriptCallbackBudgetTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>Every callback path a runaway loop could hide in.</summary>
    public static TheoryData<string> RunawayCallbacks => new()
    {
        "level.all_brushes():where(function(b) while true do end end)",
        "level.all_brushes():each(function(b) while true do end end)",
        "level.brushes_where(function(b) while true do end end)",
        "local q = level.objects_in{min = {-100, -100, -100}, max = {100, 100, 100}} q:where(function(o) while true do end end)",
        "local q = level.objects_in{min = {-100, -100, -100}, max = {100, 100, 100}} q:each(function(o) while true do end end)",
        "ged.group('loop', function() while true do end end)",
        "level.all_brushes():each(function(b) level.all_brushes():where(function(x) while true do end end) end)",
        "local t = {3, 1, 2} table.sort(t, function(a, b) while true do end end)",
        "local s = string.gsub('abc', '%w', function(c) while true do end end)",
        "local s = ('abc'):gsub('%w', function(c) while true do end end)",
    };

    private static EditorDocument Doc() => ScriptTestFixture.NewDoc(entities: 2, boxes: 2);

    private static ScriptRunResult Run(EditorDocument doc, string lua, ScriptExecutionLimits limits, CancellationToken cancel = default) =>
        ScriptTestFixture.Runner().Run(ScriptTestFixture.Services(doc), lua,
            new ScriptRunOptions { ChunkName = "budget", AllowDestructive = true, Limits = limits }, new ScriptLog(), cancel);

    [Theory]
    [MemberData(nameof(RunawayCallbacks))]
    public void An_Infinite_Loop_Inside_A_Callback_Times_Out(string lua)
    {
        EditorDocument doc = Doc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        var sw = Stopwatch.StartNew();
        ScriptRunResult r = Run(doc, lua, new ScriptExecutionLimits { InstructionBudget = 0, Timeout = Limit, YieldEvery = 1_000 });
        sw.Stop();

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Contains("timed out", r.Error.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    [Theory]
    [MemberData(nameof(RunawayCallbacks))]
    public void An_Infinite_Loop_Inside_A_Callback_Exhausts_The_Instruction_Budget(string lua)
    {
        ScriptRunResult r = Run(Doc(), lua, new ScriptExecutionLimits { InstructionBudget = 2_000_000, Timeout = TimeSpan.Zero, YieldEvery = 1_000 });

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Contains("Instruction budget", r.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Infinite_Loop_Inside_A_Callback_Is_Cancellable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        ScriptRunResult r = Run(Doc(), "level.all_brushes():where(function(b) while true do end end)",
            new ScriptExecutionLimits { InstructionBudget = 0, Timeout = TimeSpan.Zero, YieldEvery = 1_000 }, cts.Token);

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Equal("Script canceled.", r.Error.Message);
    }

    [Fact]
    public void An_Abort_Swallowed_By_Library_Code_Still_Ends_The_Run()
    {
        // table.sort catches what its comparator throws; the abort is sticky, so the loop after the
        // sort is stopped at its first budget check and the run still fails as Aborted.
        ScriptRunResult r = Run(Doc(),
            "local t = {} for i = 1, 50 do t[i] = 50 - i end\n" +
            "table.sort(t, function(a, b) while true do end end)\n" +
            "local n = 0 for i = 1, 10 do n = n + i end return 'survived ' .. n",
            new ScriptExecutionLimits { InstructionBudget = 0, Timeout = Limit, YieldEvery = 1_000 });

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Contains("timed out", r.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normal_Callbacks_Still_Work_And_Are_Deterministic()
    {
        const string Lua =
            "local q = level.all_brushes():where(function(b) local n = 0 for i = 1, 5000 do n = n + i end return b.pos.x > 1 end)\n" +
            "local seen = {} q:each(function(b) seen[#seen + 1] = b.uid end)\n" +
            "local t = {5, 3, 9, 1} table.sort(t, function(a, b) return a > b end)\n" +
            "local s = string.gsub('abc', '%w', function(c) return c:upper() end)\n" +
            "local g = 0 ged.group('g', function() g = g + 1 end)\n" +
            "return q.count .. ':' .. table.concat(seen, ',') .. ':' .. table.concat(t, ',') .. ':' .. s .. ':' .. g";
        string? first = null;
        for (int i = 0; i < 2; i++)
        {
            ScriptRunResult r = Run(Doc(), Lua, ScriptExecutionLimits.Default);
            Assert.True(r.Success, r.Error?.ToDisplayString());
            Assert.StartsWith("1:", r.ReturnValue, StringComparison.Ordinal);
            Assert.EndsWith(":9,5,3,1:ABC:1", r.ReturnValue, StringComparison.Ordinal);
            first ??= r.ReturnValue;
            Assert.Equal(first, r.ReturnValue);
        }
    }

    [Fact]
    public void Callback_Instructions_Count_Toward_The_Run_Budget()
    {
        // Calibrate: the smallest power-of-two budget a top-level burn fits in (one burn costs C
        // instructions, so C <= B < 2C); 1.5 B (between 1.5C and 3C) must admit one predicate call
        // and refuse four, which can only happen when every callback charges the run's shared
        // instruction counter.
        const string Body = "local n = 0 for i = 1, 100000 do n = n + 1 end";
        const string Burn = "function(b) " + Body + " return true end";
        long budget = 1_000;
        while (!Run(ScriptTestFixture.NewDoc(entities: 0), Body,
                   new ScriptExecutionLimits { InstructionBudget = budget, Timeout = TimeSpan.Zero, YieldEvery = 1_000 }).Success)
        {
            budget *= 2;
            Assert.True(budget < 100_000_000, "calibration did not converge");
        }

        var limits = new ScriptExecutionLimits { InstructionBudget = budget * 3 / 2, Timeout = TimeSpan.Zero, YieldEvery = 1_000 };

        ScriptRunResult once = Run(ScriptTestFixture.NewDoc(entities: 0, boxes: 1), $"return level.all_brushes():where({Burn}).count", limits);
        Assert.True(once.Success, once.Error?.ToDisplayString());
        Assert.Equal("1", once.ReturnValue);

        ScriptRunResult four = Run(ScriptTestFixture.NewDoc(entities: 0, boxes: 4), $"return level.all_brushes():where({Burn}).count", limits);
        Assert.False(four.Success);
        Assert.Contains("Instruction budget", four.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Repl_Session_Recovers_After_A_Callback_Abort()
    {
        EditorDocument doc = Doc();
        using ScriptReplSession repl = ScriptTestFixture.Runner().CreateRepl(ScriptTestFixture.Services(doc),
            new ScriptRunOptions { ChunkName = "console", Limits = new ScriptExecutionLimits { InstructionBudget = 0, Timeout = Limit, YieldEvery = 1_000 } },
            new ScriptLog());

        ScriptRunResult aborted = repl.Eval("level.all_brushes():where(function(b) while true do end end)");
        Assert.False(aborted.Success);
        Assert.Equal(ScriptErrorKind.Aborted, aborted.Error!.Kind);

        ScriptRunResult next = repl.Eval("return level.all_brushes():where(function(b) return true end).count");
        Assert.True(next.Success, next.Error?.ToDisplayString());
        Assert.Equal("2", next.ReturnValue);
    }

    /// <summary>Recursion that goes through the API (each hop is a C# frame the instruction budget never sees).</summary>
    public static TheoryData<string> RecursiveCallbacks => new()
    {
        "local function f() ged.group('x', f) end f()",
        "local function g() level.all_brushes():where(function(b) g() return true end) end g()",
        "local function h() local t = {2, 1} table.sort(t, function(a, b) h() return a < b end) end h()",
        "local function k() string.gsub('a', '%w', function(c) k() return c end) end k()",
    };

    /// <summary>
    /// Runs <paramref name="body"/> on a thread with the 1 MB stack of a desktop UI thread — the thread an
    /// attached editor runs scripts on — so a stack overflow here would be the editor process dying.
    /// </summary>
    private static T OnEditorSizedThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, maxStackSize: 1024 * 1024);
        t.Start();
        t.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("the script thread threw", error);
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(RecursiveCallbacks))]
    public void Recursion_Through_Callbacks_Aborts_Instead_Of_Overflowing_The_Stack(string lua)
    {
        EditorDocument doc = Doc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        ScriptRunResult r = OnEditorSizedThread(() => Run(doc, lua, ScriptExecutionLimits.Default));

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Equal("Callback nesting too deep (limit 64).", r.Error.Message);
        Assert.False(string.IsNullOrEmpty(r.Error.Hint));
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));
    }

    [Fact]
    public void Deep_But_Bounded_Callback_Nesting_Still_Works()
    {
        ScriptRunResult r = Run(Doc(),
            "local depth = 0\n" +
            "local function f(n) if n > 0 then ged.group('x', function() depth = depth + 1 f(n - 1) end) end end\n" +
            "f(60) return depth", ScriptExecutionLimits.Default);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("60", r.ReturnValue);
    }

    [Fact]
    public void Plain_Lua_Recursion_Fails_Cleanly_On_An_Editor_Sized_Thread()
    {
        // Lua-to-Lua calls run on MoonSharp's own VM stack, not the CLR stack; runaway recursion ends as
        // an ordinary failed run (and rolls back), never a process crash.
        EditorDocument doc = Doc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        ScriptRunResult r = OnEditorSizedThread(() => Run(doc,
            "level.place{kind = 'light', pos = {0, 3, 0}}\nlocal function r(n) return r(n + 1) + 1 end\nreturn r(1)",
            ScriptExecutionLimits.Default));
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Runtime, r.Error!.Kind);
        Assert.Equal("Stack overflow: Lua recursion went too deep.", r.Error.Message);
        Assert.True(before.AsSpan().SequenceEqual(doc.SaveToBytes(updateTimestamp: false)));

        // Deep but finite plain recursion is fine.
        ScriptRunResult ok = OnEditorSizedThread(() => Run(Doc(),
            "local function s(n) if n == 0 then return 0 end return 1 + s(n - 1) end return s(5000)", ScriptExecutionLimits.Default));
        Assert.True(ok.Success, ok.Error?.ToDisplayString());
        Assert.Equal("5000", ok.ReturnValue);
    }

    [Fact]
    public void A_Repl_Session_Recovers_After_Too_Deep_Nesting()
    {
        EditorDocument doc = Doc();
        using ScriptReplSession repl = ScriptTestFixture.Runner().CreateRepl(ScriptTestFixture.Services(doc),
            new ScriptRunOptions { ChunkName = "console" }, new ScriptLog());
        ScriptRunResult aborted = OnEditorSizedThread(() => repl.Eval("local function f() ged.group('x', f) end f()"));
        Assert.False(aborted.Success);
        Assert.Equal(ScriptErrorKind.Aborted, aborted.Error!.Kind);

        ScriptRunResult next = repl.Eval("local n = 0 ged.group('g', function() n = n + 1 end) return n");
        Assert.True(next.Success, next.Error?.ToDisplayString());
        Assert.Equal("1", next.ReturnValue);
    }

    [Fact]
    public void Errors_Inside_Callbacks_Still_Report_Normally()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(Doc(), "level.all_brushes():where(function(b) error('bad predicate') end)");
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Runtime, r.Error!.Kind);
        Assert.Contains("bad predicate", r.Error.Message, StringComparison.Ordinal);

        (ScriptRunResult api, _) = ScriptTestFixture.Run(Doc(), "level.all_brushes():each(function(b) b:scale(0, 1, 1) end)");
        Assert.False(api.Success);
        Assert.Equal(ScriptErrorKind.Api, api.Error!.Kind);
    }

    [Fact]
    public void A_Budget_Abort_Names_The_Line_And_How_To_Raise_The_Limit()
    {
        ScriptRunResult r = Run(Doc(), "local n = 0\nlocal function spin()\n  while true do\n    n = n + 1\n  end\nend\nspin()\n",
            new ScriptExecutionLimits { InstructionBudget = 2_000_000, Timeout = TimeSpan.Zero, YieldEvery = 1_000 });

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.InRange(r.Error.Line, 3, 4);                                   // inside the loop, not 0
        Assert.Contains("--budget", r.Error.Hint ?? string.Empty, StringComparison.Ordinal);
    }
}
