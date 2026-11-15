using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Cli;
using Ged.Core.Editor;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// <c>glacier-cli run &lt;file&gt;</c> carries the modules its script imports from its own folder (sources, not paths):
/// a headless run imports them by name, a module in a sub-folder is not found, and the run request's <c>modules</c>
/// argument refuses shipped names.
/// </summary>
public sealed class LocalModuleCliTests
{
    private static CliEnvironment Env() => new()
    {
        PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Clock = () => TestLevels.Fixed,
    };

    [Fact]
    public async Task Run_Imports_The_Modules_Beside_The_Script()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("rooms.lua"), "local u = ged.import('util') return { make = function() level.brush{name = 'hall', pos = {0, u.h, 0}, size = 8, air = true} end }");
        File.WriteAllText(dir.File("util.lua"), "return { h = 4 }");
        File.WriteAllText(dir.File("build.lua"), "local rooms = ged.import(\"rooms\")\nrooms.make()");
        (int code, string o, string e) = await CliTests.Cli(Env(), "run", dir.File("build.lua"), "--out", dir.File("out.rfl"), "--json");
        Assert.True(code == 0, e + o);
        EditorDocument saved = EditorDocument.Open(dir.File("out.rfl"));
        Assert.Contains(new Ged.Core.Editing.GedObjectMetadataService(saved).AllTags().Values, t => t.TryGetValue("kit.name", out string? n) && n == "hall");

        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllText(Path.Combine(dir.File("sub"), "deep.lua"), "return {}");
        File.WriteAllText(dir.File("deep_user.lua"), "return ged.import('deep')");
        (code, o, e) = await CliTests.Cli(Env(), "run", dir.File("deep_user.lua"), "--out", dir.File("out2.rfl"), "--json");
        Assert.NotEqual(0, code);
        Assert.Contains("Unknown module 'deep'", o + e, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Request_Refuses_A_Shipped_Module_Name()
    {
        using var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        var commands = new AutomationCommands(host);
        await host.NewAsync(default);
        AutomationResponse r = await commands.ExecuteAsync(new AutomationRequest("run", new JsonObject
        {
            ["script"] = "return 1",
            ["modules"] = new JsonObject { ["kit"] = "return {}" },
        }));
        Assert.False(r.Ok);
        Assert.Equal(AutomationErrorCodes.Usage, r.Error!.Code);
    }
}
