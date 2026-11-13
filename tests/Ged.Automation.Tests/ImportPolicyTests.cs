using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ged.Cli;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The file policy of <c>level.import_mesh</c> on the command line (ORGANIC-GEOMETRY-SPEC §2.5): a model
/// outside the allowed folders is refused unless named with <c>--allow-import</c>, which grants READ
/// access only (never a save target), is headless-only and applies only to <c>run</c>.
/// </summary>
public sealed class ImportPolicyTests
{
    private const string Cube =
        "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\nv -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\n" +
        "f 1 4 3\nf 1 3 2\nf 5 6 7\nf 5 7 8\nf 1 5 8\nf 1 8 4\nf 2 3 7\nf 2 7 6\nf 4 8 7\nf 4 7 3\nf 1 2 6\nf 1 6 5\n";

    private static CliEnvironment Env() => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        Clock = () => TestLevels.Fixed,
    };

    [Fact]
    public void Explicit_Read_Paths_Are_Readable_But_Never_Writable()
    {
        using var dir = new TempDir();
        string root = Directory.CreateDirectory(dir.File("maps")).FullName;
        var policy = new PathAllowList(() => new[] { root });
        string model = dir.File("model.obj");

        Assert.False(policy.IsReadAllowed(model));
        policy.AllowExplicitRead(model);
        Assert.True(policy.IsReadAllowed(model));
        Assert.False(policy.IsAllowed(model)); // not a save / package target
        Assert.Equal(Path.GetFullPath(model), policy.RequireScriptRead(model, "level.import_mesh"));
        Assert.Throws<ScriptApiException>(() => policy.RequireScriptWrite(model, "level.import_mesh"));

        Assert.True(policy.IsReadAllowed(Path.Combine(root, "a.obj")));
        ScriptApiException ex = Assert.Throws<ScriptApiException>(() => policy.RequireScriptRead(Path.Combine(root, "..", "x.obj"), "level.import_mesh"));
        Assert.StartsWith("level.import_mesh: refusing to read", ex.Message);
        Assert.Contains(root, ex.Message);
    }

    [Fact]
    public async Task Headless_Run_Reads_A_Model_Only_With_Allow_Import()
    {
        using var dir = new TempDir();
        using var models = new TempDir();
        string model = models.File("cube.obj");
        File.WriteAllText(model, Cube);
        File.WriteAllText(dir.File("imp.lua"), $"local m = level.import_mesh{{path = '{model.Replace('\\', '/')}'}}\nassert(m.brushes.count == 1)");

        (int code, string o, string e) = await CliTests.Cli(Env(), "run", dir.File("imp.lua"), "--out", dir.File("a.rfl"));
        Assert.NotEqual(0, code);
        Assert.Contains("refusing to read", o + e);
        Assert.False(File.Exists(dir.File("a.rfl")));

        (code, o, e) = await CliTests.Cli(Env(), "run", dir.File("imp.lua"), "--out", dir.File("b.rfl"), "--allow-import", model);
        Assert.True(code == 0, e + o);
        EditorDocument saved = EditorDocument.Open(dir.File("b.rfl"));
        Assert.Single(new BrushEditor(saved).Brushes, b => (b.Flags & (uint)Ged.Core.Model.BrushFlags.Detail) != 0);
        Assert.Equal(Cube, File.ReadAllText(model)); // read, never written
    }

    [Theory]
    [InlineData("lint a.rfl --allow-import m.obj")]
    [InlineData("run a.lua --attach --allow-import m.obj")]
    public async Task Allow_Import_Is_Headless_Run_Only(string commandLine)
    {
        (int code, _, string e) = await CliTests.Cli(Env(), commandLine.Split(' '));
        Assert.Equal(2, code);
        Assert.Contains("--allow-import", e);
    }
}
