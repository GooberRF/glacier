using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;
using Ged.Core.Scripting;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using MoonSharp.Interpreter.Interop.BasicDescriptors;

namespace Ged.Scripting;

/// <summary>
/// One-time, process-wide MoonSharp registration for the GED facade: every facade type is exposed
/// as UserData with <b>snake_case aliases</b> for its PascalCase C# members (so Lua reads
/// <c>obj.uid</c> / <c>obj:set_pos(…)</c> while the C# stays idiomatic), and each Lua-callback
/// delegate type gets a <c>function → delegate</c> converter (MoonSharp cannot auto-marshal these).
/// The facade classes themselves live in Ged.Core with no engine dependency.
/// </summary>
internal static class MoonSharpRegistry
{
    private static readonly object Gate = new();
    private static bool _done;

    internal static void EnsureRegistered()
    {
        if (_done)
        {
            return;
        }

        lock (Gate)
        {
            if (_done)
            {
                return;
            }

            // Element handles + queries + result records + globals + the Vec3 value type.
            RegisterSnake<Vec3>();
            RegisterSnake<ScriptGed>();
            RegisterSnake<ScriptLevel>();
            RegisterSnake<ScriptSelection>();
            RegisterSnake<ScriptAssets>();
            RegisterSnake<ScriptOps>();
            RegisterSnake<ScriptLint>();
            RegisterSnake<ScriptLog>();
            RegisterSnake<ScriptRng>();
            RegisterSnake<ScriptObjectHandle>();
            RegisterSnake<ScriptBrushHandle>();
            RegisterSnake<ScriptFaceHandle>();
            RegisterSnake<ScriptObjectQuery>();
            RegisterSnake<ScriptBrushQuery>();
            RegisterSnake<ScriptAssetUsage>();
            RegisterSnake<ScriptLintReport>();
            RegisterSnake<ScriptLintFinding>();
            RegisterSnake<ScriptOpReport>();
            RegisterSnake<ScriptHoleReport>();
            RegisterSnake<ScriptRepairReport>();
            RegisterSnake<ScriptRotation>();
            RegisterSnake<ScriptAabb>();
            RegisterSnake<ScriptUv>();
            RegisterSnake<ScriptTypeInfo>();
            RegisterSnake<ScriptMemberInfo>();
            RegisterSnake<ScriptOptionInfo>();
            RegisterSnake<ScriptFieldInfo>();
            RegisterSnake<ScriptClassInfo>();
            RegisterSnake<ScriptLinkReport>();
            RegisterSnake<ScriptLinkRejection>();
            RegisterSnake<ScriptPropertyBag>();
            RegisterSnake<ScriptCatalog>();
            RegisterSnake<ScriptMoverHandle>();
            RegisterSnake<ScriptGroupHandle>();
            RegisterSnake<ScriptPrefabs>();
            RegisterSnake<ScriptPrefabInfo>();
            RegisterSnake<ScriptPrefabInstance>();
            RegisterSnake<ScriptGeo>();
            RegisterSnake<ScriptGeoReport>();
            RegisterSnake<ScriptTextureInfo>();
            RegisterSnake<ScriptNormInfo>();
            RegisterSnake<ScriptMeshImport>();
            RegisterSnake<ScriptTube>();
            RegisterSnake<ScriptTubeStation>();
            RegisterSnake<ScriptTubeClimb>();
            RegisterSnake<ScriptRocks>();
            RegisterSnake<ScriptPit>();
            RegisterSnake<ScriptSpatial>();
            RegisterSnake<ScriptDesign>();
            RegisterSnake<ScriptKitCore>();
            RegisterSnake<ScriptKitProbe>();
            RegisterSnake<ScriptKitLattice>();
            RegisterSnake<ScriptKitDressSpace>();

            BindTry();
            RegisterCallbackConverters();
            RegisterTableConverters();

            _done = true;
        }
    }

    private static void RegisterSnake<T>()
    {
        if (UserData.IsTypeRegistered(typeof(T)))
        {
            return;
        }

        IUserDataDescriptor descriptor = UserData.RegisterType<T>();
        if (descriptor is not StandardUserDataDescriptor std)
        {
            return;
        }

        // Every facade METHOD counts one call on the run's context when Lua fetches it to call it (the
        // corpus budget of ScriptRunResult.FacadeCalls); property reads stay uncounted.
        foreach (KeyValuePair<string, IMemberDescriptor> member in std.Members.ToList())
        {
            if (member.Value is OverloadedMethodMemberDescriptor or MethodMemberDescriptor)
            {
                std.RemoveMember(member.Key);
                std.AddMember(member.Key, new CountingMethodDescriptor(member.Value));
            }
        }

        foreach (KeyValuePair<string, IMemberDescriptor> member in std.Members.ToList())
        {
            string snake = ScriptNaming.ToSnakeCase(member.Key);
            if (!string.Equals(snake, member.Key, StringComparison.Ordinal) && !std.HasMember(snake))
            {
                std.AddMember(snake, member.Value);
            }
        }
    }

    /// <summary>
    /// <c>ged.try(fn, ...)</c> returns several values (<c>ok, …</c>), which a facade method cannot, so the
    /// documented <see cref="ScriptGed.Try"/> member is bound to <see cref="MoonSharpSession.Try"/> instead.
    /// </summary>
    private static void BindTry()
    {
        if (UserData.GetDescriptorForType<ScriptGed>(true) is not StandardUserDataDescriptor std)
        {
            return;
        }

        var bound = new GedTryDescriptor();
        foreach (string key in new[] { nameof(ScriptGed.Try), ScriptNaming.ToSnakeCase(nameof(ScriptGed.Try)) })
        {
            if (std.HasMember(key))
            {
                std.RemoveMember(key);
            }

            std.AddMember(key, bound);
        }
    }

    /// <summary>
    /// Lua function → facade delegate. Every delegate calls back through
    /// <see cref="MoonSharpSession.Invoke"/>, which runs the Lua function under the run's instruction
    /// budget, script-time limit and cancellation (a plain nested call would not auto-yield, so an
    /// infinite loop inside a predicate could never be stopped).
    /// </summary>
    private static void RegisterCallbackConverters()
    {
        Convert<Action>(dv => { Closure c = dv.Function; return new Action(() => MoonSharpSession.Invoke(c)); });
        Convert<ScriptObjectPredicate>(dv => { Closure c = dv.Function; return new ScriptObjectPredicate(o => MoonSharpSession.Invoke(c, UserData.Create(o)).CastToBool()); });
        Convert<ScriptObjectAction>(dv => { Closure c = dv.Function; return new ScriptObjectAction(o => MoonSharpSession.Invoke(c, UserData.Create(o))); });
        Convert<ScriptBrushPredicate>(dv => { Closure c = dv.Function; return new ScriptBrushPredicate(b => MoonSharpSession.Invoke(c, UserData.Create(b)).CastToBool()); });
        Convert<ScriptBrushAction>(dv => { Closure c = dv.Function; return new ScriptBrushAction(b => MoonSharpSession.Invoke(c, UserData.Create(b))); });
        Convert<ScriptFacePredicate>(dv => { Closure c = dv.Function; return new ScriptFacePredicate(f => MoonSharpSession.Invoke(c, UserData.Create(f)).CastToBool()); });
        Convert<ScriptLintVisitor>(dv => { Closure c = dv.Function; return new ScriptLintVisitor(o => MoonSharpSession.Invoke(c, UserData.Create(o))); });
    }

    private static void Convert<T>(Func<DynValue, object> converter) =>
        Script.GlobalOptions.CustomConverters.SetScriptToClrCustomConversion(DataType.Function, typeof(T), converter);

    /// <summary>
    /// Lua table → <see cref="ScriptArgs"/>. MoonSharp weighs a registered custom
    /// conversion above any other match, so a table argument always selects a <c>ScriptArgs</c>
    /// overload (e.g. <c>level.place{…}</c>) while a string first argument keeps selecting the V1
    /// positional overload; verified by <c>ScriptInteropTests</c>.
    /// </summary>
    private static void RegisterTableConverters()
    {
        Script.GlobalOptions.CustomConverters.SetScriptToClrCustomConversion(DataType.Table, typeof(ScriptArgs), dv => ToArgs(dv.Table, 0));

        // A facade parameter typed `object` (generic field values: o:set("color", {255, 0, 0}),
        // ged.describe(x)) receives a table as the same engine-neutral ScriptArgs, never a MoonSharp Table.
        Script.GlobalOptions.CustomConverters.SetScriptToClrCustomConversion(DataType.Table, typeof(object), dv => ToArgs(dv.Table, 0));
    }

    private const int MaxTableDepth = 32;

    /// <summary>Recursively converts a table: string keys → Named, the 1..n array part → Items.</summary>
    internal static ScriptArgs ToArgs(Table table, int depth)
    {
        if (depth > MaxTableDepth)
        {
            throw new ScriptApiException($"Table arguments may nest at most {MaxTableDepth} levels deep.");
        }

        var named = new Dictionary<string, object?>(StringComparer.Ordinal);
        var indexed = new SortedDictionary<int, object?>();
        foreach (TablePair pair in table.Pairs)
        {
            object? value = ToClr(pair.Value, depth);
            switch (pair.Key.Type)
            {
                case DataType.String:
                    named[pair.Key.String] = value;
                    break;
                case DataType.Number when pair.Key.Number >= 1 && pair.Key.Number <= int.MaxValue && Math.Floor(pair.Key.Number) == pair.Key.Number:
                    indexed[(int)pair.Key.Number] = value;
                    break;
                default:
                    throw new ScriptApiException(
                        $"Table arguments may only use string keys or a 1..n list (found a {pair.Key.Type.ToString().ToLowerInvariant()} key '{pair.Key.ToPrintString()}').");
            }
        }

        var items = new List<object?>(indexed.Count);
        int expected = 1;
        foreach (KeyValuePair<int, object?> kv in indexed)
        {
            if (kv.Key != expected)
            {
                throw new ScriptApiException(
                    $"Table arguments may only use string keys or a 1..n list (index {expected} is missing before {kv.Key}).",
                    "Lists must not have holes (nil entries).");
            }

            items.Add(kv.Value);
            expected++;
        }

        return new ScriptArgs(named, items);
    }

    private static object? ToClr(DynValue v, int depth) => v.Type switch
    {
        DataType.Nil or DataType.Void => null,
        DataType.Boolean => v.Boolean,
        DataType.Number => v.Number,
        DataType.String => v.String,
        DataType.Table => ToArgs(v.Table, depth + 1),
        DataType.UserData => v.UserData?.Object,
        _ => throw new ScriptApiException(
            $"Table arguments may hold numbers, booleans, strings, tables and handles (found a {v.Type.ToString().ToLowerInvariant()}).",
            "Pass functions as direct arguments, not inside an option table. To read a module's documentation, describe it by name, e.g. ged.describe(\"kit\")."),
    };
}

/// <summary>The <c>ged.try</c> member: a function value that runs <see cref="MoonSharpSession.Try"/> (counted like a facade call).</summary>
internal sealed class GedTryDescriptor : IMemberDescriptor
{
    public bool IsStatic => false;

    public string Name => "try";

    public MemberDescriptorAccess MemberAccess => MemberDescriptorAccess.CanRead | MemberDescriptorAccess.CanExecute;

    public DynValue GetValue(Script script, object obj) => DynValue.NewCallback((ctx, args) =>
    {
        MoonSharpSession.ContextOf(script)?.CountFacadeCall();
        return MoonSharpSession.Try(script, args);
    }, "try");

    public void SetValue(Script script, object obj, DynValue value) =>
        throw new ScriptRuntimeException("ged.try cannot be assigned.");
}

/// <summary>
/// Wraps a facade method descriptor so that each time Lua CALLS the method, the owning session's
/// <see cref="ScriptContext.CountFacadeCall"/> is incremented (the fetched function is wrapped; fetching a
/// method without calling it counts nothing). Everything else delegates.
/// <para>
/// <b>Overloaded methods dispatch per thread.</b> MoonSharp registers one descriptor per member for the whole
/// process, and its <see cref="OverloadedMethodMemberDescriptor"/> keeps a mutable overload-resolution cache
/// (and sorts its overload list on first use) with no synchronisation: <c>Cache</c> publishes a cache slot and
/// then rewrites its method before its argument signature, so a concurrent call from another script can match
/// the old signature and run the new method — a table passed to <c>brush:box_map{…}</c> reaching the
/// <c>box_map(ppm)</c> overload ("cannot convert a table to a clr type System.Double"). Scripts run
/// concurrently in one process (parallel tests, a headless host beside the editor), so a member with more than
/// one overload is called through a per-thread copy of its descriptor: the same overload list (shared,
/// stateless method descriptors), with each thread's own sort and cache. When the overload list cannot be read
/// (a MoonSharp update renamed the internal field), calls to that member are serialised instead.
/// </para>
/// </summary>
internal sealed class CountingMethodDescriptor : IMemberDescriptor
{
    private static readonly System.Reflection.FieldInfo? OverloadsField =
        typeof(OverloadedMethodMemberDescriptor).GetField("m_Overloads", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private readonly IMemberDescriptor _inner;

    /// <summary>The per-thread dispatchers of an overloaded member (null for a single-overload method).</summary>
    private readonly System.Threading.ThreadLocal<OverloadedMethodMemberDescriptor>? _perThread;

    /// <summary>Serialises calls to an overloaded member whose overload list could not be read (null otherwise).</summary>
    private readonly object? _serial;

    internal CountingMethodDescriptor(IMemberDescriptor inner)
    {
        _inner = inner;
        if (inner is OverloadedMethodMemberDescriptor { OverloadCount: > 1 } overloaded)
        {
            if (OverloadsField?.GetValue(overloaded) is List<IOverloadableMemberDescriptor> list && list.Count == overloaded.OverloadCount)
            {
                IOverloadableMemberDescriptor[] overloads = list.ToArray();
                string name = overloaded.Name;
                Type declaring = overloaded.DeclaringType;
                _perThread = new System.Threading.ThreadLocal<OverloadedMethodMemberDescriptor>(
                    () => new OverloadedMethodMemberDescriptor(name, declaring, overloads) { IgnoreExtensionMethods = overloaded.IgnoreExtensionMethods });
            }
            else
            {
                _serial = new object();
            }
        }
    }

    /// <summary>True when calls dispatch through per-thread overload resolution (tests).</summary>
    internal bool DispatchesPerThread => _perThread is not null;

    public bool IsStatic => _inner.IsStatic;

    public string Name => _inner.Name;

    public MemberDescriptorAccess MemberAccess => _inner.MemberAccess;

    public DynValue GetValue(Script script, object obj)
    {
        DynValue value = _inner.GetValue(script, obj);
        if (value.Type != DataType.ClrFunction)
        {
            MoonSharpSession.ContextOf(script)?.CountFacadeCall();
            return value;
        }

        CallbackFunction inner = value.Callback;
        System.Threading.ThreadLocal<OverloadedMethodMemberDescriptor>? perThread = _perThread;
        object? serial = _serial;
        return DynValue.NewCallback((ctx, args) =>
        {
            MoonSharpSession.ContextOf(script)?.CountFacadeCall();
            if (perThread is not null)
            {
                return perThread.Value!.GetCallback(script, obj)(ctx, args);
            }

            if (serial is not null)
            {
                lock (serial)
                {
                    return inner.ClrCallback(ctx, args);
                }
            }

            return inner.ClrCallback(ctx, args);
        }, inner.Name);
    }

    public void SetValue(Script script, object obj, DynValue value) => _inner.SetValue(script, obj, value);
}
