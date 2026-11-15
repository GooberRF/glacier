using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Modules beside a script run from a file: <see cref="ScriptModuleResolver.CollectLocal"/> gathers the literal
/// <c>ged.import("name")</c> modules of the script's own folder (transitively, never shipped names, never a parent or
/// sub-folder, never the script itself), and a run carrying them resolves them after the shipped modules and before
/// the scripts folder's lib/. Also <c>level.brush{name}</c> and <c>ged.names</c>.
/// </summary>
public sealed class ScriptLocalModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ged-local-modules-" + Guid.NewGuid().ToString("N"));

    public ScriptLocalModuleTests() => Directory.CreateDirectory(Path.Combine(_dir, "sub"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, string source)
    {
        string path = Path.Combine(_dir, relative);
        File.WriteAllText(path, source);
        return path;
    }

    private static (ScriptRunResult Result, ScriptLog Log) Run(string source, IReadOnlyDictionary<string, string>? modules, EditorDocument? doc = null, string? moduleDir = null)
    {
        doc ??= ScriptTestFixture.NewDoc(entities: 0);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), LocalModules = modules, ModuleDirectory = moduleDir ?? Path.Combine(Path.GetTempPath(), "ged-no-lib-" + Guid.NewGuid().ToString("N")) },
            source,
            new ScriptRunOptions { ChunkName = "main.lua", AllowDestructive = true, Limits = ScriptTestFixture.LoadTolerantLimits },
            log);
        return (r, log);
    }

    [Fact]
    public void Collect_Finds_Literal_Imports_Beside_The_Script_Transitively()
    {
        Write("graph.lua", "local u = ged.import('util') return { n = function() return u.k * 2 end }");
        Write("util.lua", "return { k = 21 }");
        Write("unused.lua", "return {}");
        Write("kit.lua", "return { version = 'stale' }");
        Write(Path.Combine("sub", "deep.lua"), "return {}");
        string main = Write("main.lua", "local kit = ged.import(\"kit\")\nlocal g = ged.import \"graph\"\nlocal d = ged.import('deep')\nlocal m = ged.import('main')\nreturn g.n()");
        Dictionary<string, string> mods = ScriptModuleResolver.CollectLocal(main, File.ReadAllText(main));
        Assert.Equal(new[] { "graph", "util" }, mods.Keys.OrderBy(k => k, StringComparer.Ordinal));

        (ScriptRunResult r, _) = Run(File.ReadAllText(main).Replace("local d = ged.import('deep')\nlocal m = ged.import('main')\n", string.Empty, StringComparison.Ordinal), mods);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("42", r.ReturnValue);
    }

    [Fact]
    public void Local_Modules_Win_Over_Lib_But_Never_Over_Shipped_Modules()
    {
        string lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "graph.lua"), "return { from = 'lib' }");
        var mods = new Dictionary<string, string> { ["graph"] = "return { from = 'beside' }", ["kit"] = "return { version = 'stale' }" };
        (ScriptRunResult r, _) = Run("return ged.import('graph').from .. ':' .. ged.import('kit').version", mods, moduleDir: lib);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("beside:3", r.ReturnValue);
    }

    [Fact]
    public void Unknown_Module_Hint_Names_The_Script_Folder()
    {
        (ScriptRunResult r, _) = Run("return ged.import('nothere')", new Dictionary<string, string> { ["graph"] = "return {}" });
        Assert.False(r.Success);
        Assert.Contains("beside a script", r.Error!.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("graph", r.Error.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Brush_Name_Is_Its_Kit_Name_Tag()
    {
        (ScriptRunResult r, _) = Run("""
            local b = level.brush{name = "pillar", pos = {0, 2, 0}, size = {1, 4, 1}}
            local h = level.brush_from_points{name = "rock", points = {{0,0,0}, {4,0,0}, {0,0,4}, {1,3,1}}}
            assert(b:tags()["kit.name"] == "pillar")
            assert(h:tags()["kit.name"] == "rock")
            assert(level.brushes_tagged("kit.name", "pillar").count == 1)
            local ok, err = ged.try(level.brush, {name = "a", tags = {["kit.name"] = "b"}})
            assert(not ok and tostring(err):find("contradicts"), tostring(err))
            return "ok"
            """, null);
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }

    [Fact]
    public void Names_Turns_Records_And_Handles_Into_Strings()
    {
        (ScriptRunResult r, _) = Run("""
            local o = level.place{kind = "light", pos = {0, 2, 0}}
            local s = table.concat(ged.names(o:fields()), ",")
            assert(s:find("pos", 1, true) or s:find("name", 1, true), s)
            return table.concat(ged.names({"a", 2, 2.5, true, {name = "t"}}), ",")
            """, null);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("a,2,2.5,true,t", r.ReturnValue);
    }
}
