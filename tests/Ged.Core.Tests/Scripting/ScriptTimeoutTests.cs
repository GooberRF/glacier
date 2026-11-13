using System;
using System.Collections.Generic;
using System.Threading;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The script wall-clock limit measures SCRIPT execution time: time spent inside host operations
/// (<c>ops.build</c>, <c>ops.light</c>, hole checks, save, package, playtest) is excluded, so a script
/// that builds a large level is not stopped by the limit — while a pure-Lua runaway loop still is,
/// and the run's cancellation (Stop) still interrupts a long host operation.
/// </summary>
public sealed class ScriptTimeoutTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>An operations double whose build takes <see cref="BuildTime"/> and honors the run's cancellation.</summary>
    private sealed class SlowOps : IScriptOperations
    {
        private readonly ScriptContext _ctx;

        public SlowOps(ScriptContext ctx) => _ctx = ctx;

        public TimeSpan BuildTime { get; init; }

        public bool Cancelled { get; private set; }

        public ScriptOpReport Build(bool bakeLighting, bool shadows)
        {
            if (_ctx.Cancellation.WaitHandle.WaitOne(BuildTime))
            {
                Cancelled = true;
                _ctx.Cancellation.ThrowIfCancellationRequested();
            }

            return new ScriptOpReport(true, "built");
        }

        public IReadOnlyList<Vec3> CheckHoles()
        {
            Build(false, false);
            return Array.Empty<Vec3>();
        }

        public void Save(string? path)
        {
        }

        public string CompatibilitySummary() => string.Empty;

        public ScriptOpReport Package(string? path, bool multiplayer) => new(true, "packed");

        public void Playtest(bool multiplayer)
        {
        }
    }

    private static ScriptRunResult Run(string lua, TimeSpan buildTime, Action<SlowOps>? seen = null, CancellationToken cancel = default)
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        return ScriptTestFixture.Runner().Run(
            new ScriptServices
            {
                Document = doc,
                Confirmation = new AllowAllConfirmation(),
                OperationsFactory = ctx =>
                {
                    var ops = new SlowOps(ctx) { BuildTime = buildTime };
                    seen?.Invoke(ops);
                    return ops;
                },
            },
            lua,
            new ScriptRunOptions
            {
                ChunkName = "timeout",
                AllowDestructive = true,
                Limits = new ScriptExecutionLimits { InstructionBudget = 0, Timeout = Limit, YieldEvery = 1_000 },
            },
            new ScriptLog(),
            cancel);
    }

    [Fact]
    public void Host_Operations_Longer_Than_The_Limit_Do_Not_Time_Out()
    {
        // Two operations, each ~1.5x the limit and together 3x it: the script itself runs for a moment.
        ScriptRunResult r = Run(
            "ops.build(); local h = ops.holes(); return 'done ' .. h.count",
            buildTime: TimeSpan.FromSeconds(1.5));

        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("done 0", r.ReturnValue);
    }

    [Fact]
    public void A_Pure_Lua_Busy_Loop_Still_Times_Out()
    {
        ScriptRunResult r = Run("while true do end", buildTime: TimeSpan.Zero);

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Contains("timed out", r.Error.Message);
    }

    [Fact]
    public void Lua_Time_After_A_Long_Host_Operation_Still_Counts()
    {
        // The build is excluded, but the runaway loop after it is still measured and stopped.
        ScriptRunResult r = Run("ops.build(); while true do end", buildTime: TimeSpan.FromSeconds(1.5));

        Assert.False(r.Success);
        Assert.Contains("timed out", r.Error!.Message);
    }

    [Fact]
    public void Cancellation_During_A_Host_Operation_Still_Cancels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        SlowOps? ops = null;

        ScriptRunResult r = Run("ops.build(); return 'finished'", TimeSpan.FromSeconds(30), o => ops = o, cts.Token);

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.True(ops!.Cancelled, "the host operation did not observe the cancellation");
    }

    [Fact]
    public void Execution_Clock_Excludes_Host_Operation_Time()
    {
        var ctx = new ScriptContext(
            new ScriptServices { Document = ScriptTestFixture.NewDoc(entities: 0) }, new ScriptRunOptions(), new ScriptLog());
        ctx.StartExecutionClock();
        using (ctx.EnterHostOperation())
        {
            using (ctx.EnterHostOperation()) // nested scopes count once
            {
                Thread.Sleep(300);
            }

            Thread.Sleep(100);
        }

        Assert.True(ctx.HostOperationElapsed >= TimeSpan.FromMilliseconds(380), $"host time {ctx.HostOperationElapsed}");
        Assert.True(ctx.ScriptElapsed < TimeSpan.FromMilliseconds(300), $"script time {ctx.ScriptElapsed}");
        Assert.False(ctx.IsOverTimeout(new ScriptExecutionLimits { Timeout = TimeSpan.FromMilliseconds(300) }));
    }
}
