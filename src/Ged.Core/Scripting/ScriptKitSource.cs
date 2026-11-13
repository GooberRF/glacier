using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ged.Core.Scripting;

/// <summary>
/// The design kit's shipped Lua source as one text: <c>kit</c> itself followed by every shipped
/// <c>kit_*</c> module it installs functions from (e.g. <c>kit_uv</c>: palettes, trims, UV helpers), each
/// after a <c>-- ==== lib/&lt;name&gt;.lua ====</c> header. The kit reference (<c>ged.describe("kit")</c>, the
/// generated docs, the MCP kit resources) reads this, so a <c>---@kit</c> function documented in any kit module
/// is listed with the rest.
/// </summary>
public static class ScriptKitSource
{
    /// <summary>The kit's module names in reference order: <c>kit</c>, then the shipped <c>kit_*</c> modules by name.</summary>
    public static IReadOnlyList<string> ModuleNames()
    {
        IReadOnlyList<string> shipped = ScriptModuleResolver.ShippedNames();
        if (!shipped.Contains("kit", StringComparer.Ordinal))
        {
            return Array.Empty<string>();
        }

        return new[] { "kit" }.Concat(shipped.Where(n => n.StartsWith("kit_", StringComparison.Ordinal))).ToList();
    }

    /// <summary>The combined kit source, or null when this build ships no design kit.</summary>
    public static string? Combined()
    {
        IReadOnlyList<string> names = ModuleNames();
        if (names.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (string name in names)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n').Append("-- ==== ").Append(ScriptModuleResolver.ChunkNameFor(name)).Append(" ====").Append('\n');
            }

            sb.Append(ScriptModuleResolver.ShippedSource(name));
        }

        return sb.ToString();
    }
}
