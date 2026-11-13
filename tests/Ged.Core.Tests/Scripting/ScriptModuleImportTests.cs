using System;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// <c>ged.import</c>: shipped modules come from the embedded resource and
/// always win over a disk file of the same name; user modules load from the module folder; imports
/// are cached per session; circular imports, invalid and unknown names fail clearly; a module runs in
/// the same sandbox and budget as the script; the repo's <c>scripts/lib</c> files are the embedded bytes.
/// </summary>
public sealed class ScriptModuleImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ged-modules-" + Guid.NewGuid().ToString("N"));

    public ScriptModuleImportTests() => Directory.CreateDirectory(_dir);

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

    private void Module(string name, string source) => File.WriteAllText(Path.Combine(_dir, name + ".lua"), source);

    private (ScriptRunResult Result, ScriptLog Log) Run(string source, EditorDocument? doc = null, ScriptExecutionLimits? limits = null)
    {
        doc ??= ScriptTestFixture.NewDoc(entities: 0);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), ModuleDirectory = _dir },
            source,
            new ScriptRunOptions { ChunkName = "main", AllowDestructive = true, Limits = limits ?? ScriptExecutionLimits.Default },
            log);
        return (r, log);
    }

    [Fact]
    public void Shipped_Kit_Loads_From_The_Embedded_Resource()
    {
        Assert.Contains("kit", ScriptModuleResolver.ShippedNames());
        (ScriptRunResult r, _) = Run("local kit = ged.import('kit') return type(kit) .. ':' .. type(kit.room) .. ':' .. kit.version");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("table:function:3", r.ReturnValue);
    }

    [Fact]
    public void Shipped_Name_Is_Not_Shadowed_By_A_Disk_File()
    {
        Module("kit", "return { version = 'stale disk copy' }");
        (ScriptRunResult r, _) = Run("return ged.import('kit').version");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("3", r.ReturnValue);
    }

    [Fact]
    public void User_Module_Loads_From_The_Module_Directory()
    {
        Module("mymod", "local M = {} function M.twice(x) return 2 * x end return M");
        (ScriptRunResult r, _) = Run("return ged.import('mymod').twice(21)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("42", r.ReturnValue);
    }

    [Fact]
    public void Import_Is_Cached_Per_Session()
    {
        // The module body bumps a counter on a global table each time it runs.
        Module("counted", "counter = (counter or 0) + 1 return { n = counter }");
        (ScriptRunResult r, _) = Run("local a = ged.import('counted') local b = ged.import('counted') return tostring(a == b) .. ':' .. counter");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("true:1", r.ReturnValue);

        // A REPL keeps its cache across lines.
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        using ScriptReplSession repl = ScriptTestFixture.Runner().CreateRepl(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), ModuleDirectory = _dir },
            new ScriptRunOptions { ChunkName = "console" }, new ScriptLog());
        Assert.True(repl.Eval("m1 = ged.import('counted')").Success);
        ScriptRunResult second = repl.Eval("return tostring(ged.import('counted') == m1) .. ':' .. counter");
        Assert.True(second.Success, second.Error?.ToDisplayString());
        Assert.Equal("true:1", second.ReturnValue);
    }

    [Fact]
    public void Circular_Import_Fails_With_The_Chain()
    {
        Module("ca", "local b = ged.import('cb') return {}");
        Module("cb", "local a = ged.import('ca') return {}");
        (ScriptRunResult r, _) = Run("ged.import('ca')");
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Contains("Circular import: ca → cb → ca.", r.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Kit")]
    [InlineData("lib/kit")]
    [InlineData("kit.lua")]
    [InlineData("")]
    [InlineData("9lives")]
    public void Invalid_Names_Are_Rejected(string name)
    {
        (ScriptRunResult r, _) = Run($"ged.import('{name}')");
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Contains($"Invalid module name '{name}'.", r.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_Module_Lists_The_Available_Ones()
    {
        Module("helpers", "return {}");
        (ScriptRunResult r, _) = Run("ged.import('nosuch')");
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Contains("Unknown module 'nosuch'.", r.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Available modules: helpers, kit", r.Error.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_Runs_In_The_Same_Sandbox()
    {
        Module("probe", "return { io = type(io), os = type(os), require = type(require), load = type(load), dofile = type(dofile), level = type(level) }");
        (ScriptRunResult r, _) = Run("local p = ged.import('probe') return p.io .. p.os .. p.require .. p.load .. p.dofile .. ':' .. p.level");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("nilnilnilnilnil:userdata", r.ReturnValue);
    }

    [Fact]
    public void Module_Mutations_Join_The_Run_Undo_Step()
    {
        Module("builder", "local M = {} function M.make() return level.brush{pos = {0, 2, 0}, size = 4, air = true} end level.brush{pos = {10, 2, 0}, size = 2} return M");
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        (ScriptRunResult r, _) = Run("ged.import('builder').make() return level.brush_count", doc);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("2", r.ReturnValue);
        Assert.Equal(1, r.UndoNodesAdded);
        doc.Undo.Undo();
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));
    }

    [Fact]
    public void Runaway_Module_Top_Level_Is_Stopped_By_The_Budget()
    {
        Module("spin", "while true do end return {}");
        (ScriptRunResult r, _) = Run("ged.import('spin')", limits: new ScriptExecutionLimits { InstructionBudget = 200_000, Timeout = TimeSpan.FromSeconds(20) });
        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
        Assert.Contains("Instruction budget", r.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_Inside_A_Module_Report_The_Module_Chunk_And_Line()
    {
        Module("thrower", "local M = {}\nfunction M.go()\n  error('bad thing')\nend\nreturn M");
        (ScriptRunResult r, _) = Run("local t = ged.import('thrower')\n\nt.go()");
        Assert.False(r.Success);
        // The primary location is the caller's line; the module's own location stays in the message.
        Assert.Equal("main", r.Error!.Chunk);
        Assert.Equal(3, r.Error.Line);
        Assert.Contains("bad thing", r.Error.Message, StringComparison.Ordinal);
        Assert.Contains("(in lib/thrower.lua:3)", r.Error.Message, StringComparison.Ordinal);

        Module("broken", "return {");
        (ScriptRunResult syntax, _) = Run("ged.import('broken')");
        Assert.False(syntax.Success);
        Assert.Equal(ScriptErrorKind.Syntax, syntax.Error!.Kind);
        Assert.Equal("lib/broken.lua", syntax.Error.Chunk);
    }

    [Fact]
    public void Repo_Lib_Files_Equal_The_Embedded_Resources()
    {
        string root = ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        string lib = Path.Combine(root, "scripts", "lib");
        string[] files = Directory.GetFiles(lib, "*.lua").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        Assert.Equal(files.Select(f => Path.GetFileNameWithoutExtension(f)).ToArray(), ScriptModuleResolver.ShippedNames().ToArray());

        var asm = typeof(ScriptModuleResolver).Assembly;
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            using Stream s = asm.GetManifestResourceStream(ScriptModuleResolver.ResourcePrefix + name + ".lua")!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(ms.ToArray()), $"{name}.lua differs from its embedded copy");
        }
    }

    [Fact]
    public void Library_Hides_Modules_From_Runnable_Scripts()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        Directory.CreateDirectory(Path.Combine(_dir, "examples"));
        File.WriteAllText(Path.Combine(_dir, "lib", "mykit.lua"), "--@name Not a script\n--@id not-a-script\nreturn {}");
        File.WriteAllText(Path.Combine(_dir, "examples", "e.lua"), "--@name Example\n--@id ex\nreturn 1");
        var library = new ScriptLibrary(_dir);
        library.Rescan();
        Assert.Equal(2, library.Entries.Count);
        ScriptLibraryEntry module = Assert.Single(library.Entries, e => e.IsModule);
        Assert.Equal("lib/mykit.lua", module.RelativePath);
        Assert.DoesNotContain(library.Scripts, e => e.IsModule);
        Assert.Equal(new[] { ScriptLibrary.CommandIdPrefix + "ex" }, library.CommandDefinitions().Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Seeding_Copies_The_Lib_Folder_With_The_Examples()
    {
        string root = ScriptTestFixture.RepoRoot() ?? throw new InvalidOperationException("repo root not found");
        string target = Path.Combine(_dir, "seeded");
        AppPaths.SeedScriptsIfAbsent(Path.Combine(root, "scripts"), target);
        Assert.True(File.Exists(Path.Combine(target, "lib", "kit.lua")));
        Assert.True(File.Exists(Path.Combine(target, "examples", "kit-tiny-dm.lua")));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("nul")]
    [InlineData("aux")]
    [InlineData("prn")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    public void Windows_Device_Names_Are_Never_Module_Names(string name)
    {
        Assert.False(ScriptModuleResolver.IsValidName(name));
        Assert.True(ScriptModuleResolver.IsValidName(name + "_kit"));
    }
}