namespace Ged.Core.Scripting;

/// <summary>
/// Engine-neutral delegate types the facade accepts for Lua callbacks (predicates + iterators).
/// MoonSharp cannot auto-marshal a Lua function to an arbitrary <c>Func&lt;&gt;</c>, so the host
/// registers a custom <c>DataType.Function → delegate</c> conversion for each of these once at
/// startup. Keeping them in Core lets the facade stay engine-agnostic while still taking closures.
/// </summary>
public delegate bool ScriptObjectPredicate(ScriptObjectHandle obj);

/// <summary>An action applied to each object in an <see cref="ScriptObjectQuery"/> (<c>each</c>).</summary>
public delegate void ScriptObjectAction(ScriptObjectHandle obj);

/// <summary>A brush predicate for <c>level.brushes_where</c> / query filtering.</summary>
public delegate bool ScriptBrushPredicate(ScriptBrushHandle brush);

/// <summary>An action applied to each brush in a <see cref="ScriptBrushQuery"/> (<c>each</c>).</summary>
public delegate void ScriptBrushAction(ScriptBrushHandle brush);

/// <summary>A face predicate for face queries.</summary>
public delegate bool ScriptFacePredicate(ScriptFaceHandle face);

/// <summary>A lint-finding contributor callback (plan §5.4 / §2.5).</summary>
public delegate void ScriptLintVisitor(ScriptObjectHandle obj);
