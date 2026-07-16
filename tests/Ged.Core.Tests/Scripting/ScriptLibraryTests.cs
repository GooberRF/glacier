using System;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>Tests for the portable scripts library + the bundled example gallery (plan §6.4/§6.5).</summary>
public sealed class ScriptLibraryTests
{
    [Fact]
    public void Rescan_Parses_Metadata_And_Projects_Commands()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "examples"));
        File.WriteAllText(Path.Combine(dir, "cmd.lua"), "--@name My Cmd\n--@id my-cmd\n--@key Ctrl+K\nlog.info('hi')");
        File.WriteAllText(Path.Combine(dir, "examples", "plain.lua"), "return 1"); // no header → not a command

        try
        {
            var lib = new ScriptLibrary(dir);
            lib.Rescan();

            Assert.Equal(2, lib.Entries.Count);
            ScriptLibraryEntry cmd = lib.Entries.First(e => e.RelativePath == "cmd.lua");
            Assert.True(cmd.Metadata.IsCommand);
            Assert.Equal("script.user.my-cmd", cmd.CommandId);

            ScriptLibraryEntry ex = lib.Entries.First(e => e.RelativePath.StartsWith("examples/"));
            Assert.True(ex.IsExample);
            Assert.False(ex.Metadata.IsCommand);

            var defs = lib.CommandDefinitions().ToList();
            Assert.Single(defs);
            Assert.Equal("script.user.my-cmd", defs[0].Id);
            Assert.Equal("My Cmd", defs[0].DisplayName);
            Assert.Equal("Scripts", defs[0].Category);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_Root_Yields_Empty_Without_Throwing()
    {
        var lib = new ScriptLibrary(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));
        lib.Rescan();
        Assert.Empty(lib.Entries);
        Assert.Empty(lib.CommandDefinitions());
    }

    [Fact]
    public void Bundled_Examples_All_Declare_Command_Headers()
    {
        string? dir = ScriptTestFixture.ExamplesDir();
        if (dir is null)
        {
            return; // checkout without the scripts folder; nothing to assert
        }

        var lib = new ScriptLibrary(Path.GetDirectoryName(dir)!); // scripts/ root
        lib.Rescan();

        var examples = lib.Entries.Where(e => e.IsExample).ToList();
        Assert.NotEmpty(examples);
        Assert.All(examples, e => Assert.True(e.Metadata.IsCommand, $"{e.RelativePath} lacks a --@name/--@id header"));

        // Command ids are unique.
        var ids = examples.Select(e => e.CommandId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
