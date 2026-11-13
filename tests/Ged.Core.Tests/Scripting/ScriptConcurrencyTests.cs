using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Scripting;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using MoonSharp.Interpreter.Interop.BasicDescriptors;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Scripts running concurrently in one process (parallel tests; a headless or MCP host beside other sessions) share
/// MoonSharp's process-wide member descriptors. MoonSharp's overload-resolution cache is not thread-safe (a cache
/// slot's method is rewritten before its argument signature), so concurrent first calls to an overloaded method could
/// run the wrong overload — e.g. a table reaching <c>brush:box_map(ppm)</c>: "cannot convert a table to a clr type
/// System.Double". Every overloaded facade method therefore dispatches through a per-thread descriptor.
/// </summary>
public sealed class ScriptConcurrencyTests
{
    /// <summary>A two-overload method shaped like <c>box_map(ppm = …)</c> / <c>box_map{…}</c>.</summary>
    public sealed class Target
    {
        public string M(double x = 1) => "number";

        public string M(Table t) => "table";
    }

    [Fact]
    public void Every_Overloaded_Facade_Method_Dispatches_Per_Thread()
    {
        _ = new MoonSharpHost(); // registers the facade types
        IUserDataDescriptor? descriptor = UserData.GetDescriptorForType<ScriptBrushHandle>(true);
        var std = Assert.IsType<StandardUserDataDescriptor>(descriptor);
        Assert.True(Assert.IsType<CountingMethodDescriptor>(std.Members.First(m => m.Key == "box_map").Value).DispatchesPerThread);

        foreach (Type t in new[] { typeof(ScriptBrushHandle), typeof(ScriptBrushQuery), typeof(ScriptLevel), typeof(ScriptObjectQuery), typeof(ScriptGed) })
        {
            var d = (StandardUserDataDescriptor)UserData.GetDescriptorForType(t, true)!;
            foreach (KeyValuePair<string, IMemberDescriptor> m in d.Members)
            {
                if (m.Value is CountingMethodDescriptor counting && t.GetMethods().Count(x => x.Name == counting.Name) > 1)
                {
                    Assert.True(counting.DispatchesPerThread, $"{t.Name}.{m.Key} has overloads but shares MoonSharp's resolution cache.");
                }
            }
        }
    }

    [Fact]
    public async Task Concurrent_First_Calls_To_An_Overloaded_Method_Pick_The_Right_Overload()
    {
        // MoonSharp's own descriptor, wrapped exactly like a registered facade member. Each iteration starts from
        // a fresh descriptor (empty cache, unsorted overloads), the state in which concurrent calls used to go wrong.
        IOverloadableMemberDescriptor[] overloads = typeof(Target).GetMethods().Where(m => m.Name == "M")
            .Select(m => (IOverloadableMemberDescriptor)new MethodMemberDescriptor(m, InteropAccessMode.Reflection)).ToArray();
        const int threads = 6, iterations = 150;
        Script[] scripts = Enumerable.Range(0, threads).Select(_ => new Script(CoreModules.Preset_HardSandbox)).ToArray();
        DynValue[] chunks = scripts.Select((s, i) => s.LoadString((i % 3) switch { 0 => "return f({})", 1 => "return f(1)", _ => "return f()" })).ToArray();
        var target = new Target();
        int wrong = 0;
        for (int it = 0; it < iterations; it++)
        {
            var wrapped = new CountingMethodDescriptor(new OverloadedMethodMemberDescriptor("M", typeof(Target), overloads));
            Assert.True(wrapped.DispatchesPerThread);
            for (int i = 0; i < threads; i++)
            {
                scripts[i].Globals["f"] = wrapped.GetValue(scripts[i], target);
            }

            using var barrier = new Barrier(threads);
            Task[] tasks = Enumerable.Range(0, threads).Select(i => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                for (int rep = 0; rep < 4; rep++)
                {
                    try
                    {
                        if (scripts[i].Call(chunks[i]).String != (i % 3 == 0 ? "table" : "number"))
                        {
                            Interlocked.Increment(ref wrong);
                        }
                    }
                    catch (ScriptRuntimeException)
                    {
                        Interlocked.Increment(ref wrong);
                    }
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(tasks);
        }

        Assert.Equal(0, wrong);
    }

    [Fact]
    public async Task Kit_Examples_Run_Concurrently_With_The_Same_Result_As_Alone()
    {
        string dir = ScriptTestFixture.ExamplesDir() ?? throw new InvalidOperationException("scripts/examples not found");
        string[] names = { "kit-tiny-dm", "kit-cave", "kit-tiny-dm", "kit-canyon", "kit-tiny-dm", "kit-cave" };
        var sources = names.Distinct().ToDictionary(n => n, n => File.ReadAllText(Path.Combine(dir, n + ".lua")));

        byte[] Run(string name)
        {
            EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
            ScriptRunResult r = ScriptTestFixture.Runner().Run(
                new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), BuildSettings = new BuildSettings { UseSharedBsp = true } },
                sources[name],
                new ScriptRunOptions { ChunkName = name + ".lua", AllowDestructive = true, DeclaredApiVersion = ScriptMetadata.Parse(sources[name]).ApiVersion, Limits = ScriptTestFixture.LoadTolerantLimits },
                new ScriptLog());
            Assert.True(r.Success, $"{name}: {r.Error?.ToDisplayString()}");
            return doc.SaveToBytes(updateTimestamp: false);
        }

        Dictionary<string, byte[]> alone = sources.Keys.ToDictionary(n => n, Run);
        using var barrier = new Barrier(names.Length);
        Task<byte[]>[] runs = names.Select(n => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return Run(n);
        }, TaskCreationOptions.LongRunning)).ToArray();
        byte[][] together = await Task.WhenAll(runs);
        for (int i = 0; i < names.Length; i++)
        {
            Assert.True(alone[names[i]].AsSpan().SequenceEqual(together[i]), $"{names[i]} differs when run concurrently.");
        }
    }
}
