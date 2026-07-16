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

            RegisterCallbackConverters();

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

        foreach (KeyValuePair<string, IMemberDescriptor> member in std.Members.ToList())
        {
            string snake = ScriptNaming.ToSnakeCase(member.Key);
            if (!string.Equals(snake, member.Key, StringComparison.Ordinal) && !std.HasMember(snake))
            {
                std.AddMember(snake, member.Value);
            }
        }
    }

    private static void RegisterCallbackConverters()
    {
        Convert<Action>(dv => { Closure c = dv.Function; return new Action(() => c.Call()); });
        Convert<ScriptObjectPredicate>(dv => { Closure c = dv.Function; return new ScriptObjectPredicate(o => c.Call(UserData.Create(o)).CastToBool()); });
        Convert<ScriptObjectAction>(dv => { Closure c = dv.Function; return new ScriptObjectAction(o => c.Call(UserData.Create(o))); });
        Convert<ScriptBrushPredicate>(dv => { Closure c = dv.Function; return new ScriptBrushPredicate(b => c.Call(UserData.Create(b)).CastToBool()); });
        Convert<ScriptBrushAction>(dv => { Closure c = dv.Function; return new ScriptBrushAction(b => c.Call(UserData.Create(b))); });
        Convert<ScriptFacePredicate>(dv => { Closure c = dv.Function; return new ScriptFacePredicate(f => c.Call(UserData.Create(f)).CastToBool()); });
    }

    private static void Convert<T>(Func<DynValue, object> converter) =>
        Script.GlobalOptions.CustomConverters.SetScriptToClrCustomConversion(DataType.Function, typeof(T), converter);
}
