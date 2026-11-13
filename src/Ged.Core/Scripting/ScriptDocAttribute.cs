using System;

namespace Ged.Core.Scripting;

/// <summary>
/// Agent-first documentation for a script-facing member or type, read by
/// <see cref="ScriptApiReference"/> into the generated Markdown reference, the Lua stub, and
/// <c>ged.describe</c>. <see cref="Summary"/> is one sentence saying what the member does and
/// returns (units and defaults inline, side effects stated); <see cref="Example"/> is valid Lua.
/// Required on every member introduced in API version 2 (test-enforced).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class ScriptDocAttribute : Attribute
{
    public ScriptDocAttribute(string summary) => Summary = summary ?? string.Empty;

    /// <summary>One sentence: what it does and returns (≤ 200 characters).</summary>
    public string Summary { get; }

    /// <summary>An optional one-line Lua example.</summary>
    public string? Example { get; init; }

    /// <summary>The API version that introduced the member.</summary>
    public int Since { get; init; } = 1;
}
