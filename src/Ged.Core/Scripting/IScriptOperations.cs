using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The backend for the heavy <c>ops</c> operations (plan §5.5). The Core default
/// (<see cref="CoreScriptOperations"/>) drives the pure-Core pipeline directly with progress +
/// cancellation; the App may supply an implementation that routes through its
/// <c>GeometryBuildController</c> so a script build shows the same progress overlay + Build
/// Output the toolbar buttons do. Scripts never touch <c>WorldBsp</c>/<c>LightKernel</c> — these
/// are opaque, cancelable operations (§2.9).
/// </summary>
public interface IScriptOperations
{
    /// <summary>Compiles geometry (optionally baking lighting) and applies it to the document.</summary>
    ScriptOpReport Build(bool bakeLighting, bool shadows);

    /// <summary>Compiles a sealed build and returns the world-space midpoints of any leaks.</summary>
    IReadOnlyList<Vec3> CheckHoles();

    /// <summary>Saves the document to <paramref name="path"/> (or its current path). GED always
    /// writes Alpine v305 (<c>SaveTarget</c> is a compatibility reference only, not a file target).</summary>
    void Save(string? path);

    /// <summary>Reports Alpine-only features that would not run on stock RF (compatibility check).</summary>
    string CompatibilitySummary();

    /// <summary>Packages the level + its dependencies into a .vpp at <paramref name="path"/>.</summary>
    ScriptOpReport Package(string? path, bool multiplayer);

    /// <summary>Launches the level in-game (single or multiplayer).</summary>
    void Playtest(bool multiplayer);
}

/// <summary>A compact, Lua-friendly outcome for a heavy operation.</summary>
public sealed class ScriptOpReport
{
    public ScriptOpReport(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    /// <summary>Lua: <c>report.ok</c>.</summary>
    public bool Success { get; }

    /// <summary>Lua: <c>report.message</c>.</summary>
    public string Message { get; }

    /// <summary>Lua: <c>report.count</c> — an operation-specific tally (faces built, files packed, …).</summary>
    public int Count { get; init; }

    /// <summary>Lua: <c>report.path</c> — an output path when the op produced a file.</summary>
    public string? Path { get; init; }

    public override string ToString() => Message;
}
