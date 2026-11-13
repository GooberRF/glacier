using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Cli;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>glacier-cli in-process: argument handling, JSON shape, and exit codes.</summary>
public sealed class CliTests
{
    /// <summary>An isolated environment: no settings file, no RF install, a pipe nobody serves.</summary>
    internal static CliEnvironment Env(string? pipe = null) => new()
    {
        PipeName = pipe ?? "glacier-test-" + Guid.NewGuid().ToString("N"),
        SettingsPath = Path.Combine(Path.GetTempPath(), "ged-no-such-settings-" + Guid.NewGuid().ToString("N") + ".cfg"),
        GetEnvironmentVariable = _ => null,
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        Clock = () => TestLevels.Fixed,
    };

    internal static async Task<(int Code, string Out, string Err)> Cli(CliEnvironment env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = await CliApp.RunAsync(args, o, e, env);
        return (code, o.ToString(), e.ToString());
    }

    private static JsonNode Json(string stdout) => JsonNode.Parse(stdout)!;

    [Fact]
    public async Task Version_And_Help()
    {
        (int code, string o, _) = await Cli(Env(), "--version");
        Assert.Equal(0, code);
        Assert.StartsWith("glacier-cli ", o);
        Assert.Equal(AutomationCommands.ProductVersion, o.Trim()["glacier-cli ".Length..]);

        (code, o, _) = await Cli(Env(), "help");
        Assert.Equal(0, code);
        Assert.Contains("Exit codes", o);
        Assert.Contains("--attach", o);

        (code, o, _) = await Cli(Env(), "help", "run");
        Assert.Equal(0, code);
        Assert.Contains("one undo step", o, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("frobnicate")]
    [InlineData("info")]
    [InlineData("new")]
    [InlineData("lint a.rfl b.rfl")]
    [InlineData("lint a.rfl --bogus")]
    [InlineData("summary a.rfl --detail huge")]
    [InlineData("lint a.rfl --dry-run")]
    [InlineData("open a.rfl")]
    [InlineData("run")]
    [InlineData("build a.rfl --timeout soon")]
    public async Task Usage_Errors_Exit_2(string commandLine)
    {
        string[] args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        (int code, _, _) = await Cli(Env(), args);
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Missing_Level_Is_An_Io_Error()
    {
        using var dir = new TempDir();
        (int code, string o, _) = await Cli(Env(), "info", dir.File("missing.rfl"), "--json");
        Assert.Equal(4, code);
        JsonNode j = Json(o);
        Assert.False(j["ok"]!.GetValue<bool>());
        Assert.Equal("io", j["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task New_Run_Build_Holes_Lint_Summary_Save_Json_Pipeline()
    {
        using var dir = new TempDir();
        string level = dir.File("t.rfl");
        CliEnvironment env = Env();

        (int code, string o, string e) = await Cli(env, "new", "--out", level, "--json");
        Assert.True(code == 0, e);
        Assert.Equal("new", Json(o)["command"]!.GetValue<string>());
        Assert.True(File.Exists(level));

        File.WriteAllText(dir.File("room.lua"), "local b = level.place_box(0, 2, 0, 10, 4, 10)\nb:set_air(true)\nprint('room')");
        (code, o, e) = await Cli(env, "run", dir.File("room.lua"), level, "--save", "--json");
        Assert.True(code == 0, e + o);
        JsonNode run = Json(o);
        Assert.True(run["ok"]!.GetValue<bool>());
        Assert.Equal("CLI: room.lua", run["result"]!["undoLabel"]!.GetValue<string>());
        Assert.Equal(level, run["result"]!["saved"]!["path"]!.GetValue<string>());

        (code, o, e) = await Cli(env, "build", level, "--sealed", "--save", "--json");
        Assert.True(code == 0, e + o);
        Assert.True(Json(o)["result"]!["rooms"]!.GetValue<int>() >= 1);

        (code, o, _) = await Cli(env, "holes", level, "--json", "--fail-on-holes");
        Assert.Equal(0, code);
        Assert.True(Json(o)["result"]!["watertight"]!.GetValue<bool>());

        (code, o, _) = await Cli(env, "lint", level, "--json");
        Assert.Equal(0, code);
        Assert.Equal(0, Json(o)["result"]!["errors"]!.GetValue<int>());

        (code, o, _) = await Cli(env, "summary", level, "--detail", "brief", "--json");
        Assert.Equal(0, code);
        JsonNode summary = Json(o)["result"]!;
        Assert.Equal("built", summary["rooms"]!["status"]!.GetValue<string>());
        Assert.Equal(1, summary["brushes"]!["air"]!.GetValue<int>());

        (code, o, _) = await Cli(env, "info", level);
        Assert.Equal(0, code);
        Assert.Contains("Geometry: built", o); // human-readable by default

        (code, o, e) = await Cli(env, "save", level, "--out", dir.File("copy.rfl"));
        Assert.True(code == 0, e);
        Assert.True(File.Exists(dir.File("copy.rfl")));
    }

    [Fact]
    public async Task Run_Without_A_Level_Builds_A_New_One_Into_Out()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("gen.lua"), "level.place_box(0, 2, 0, 6, 4, 6):set_air(true)");
        (int code, string o, string e) = await Cli(Env(), "run", dir.File("gen.lua"), "--out", dir.File("gen.rfl"), "--json");
        Assert.True(code == 0, e + o);
        Assert.True(File.Exists(dir.File("gen.rfl")));
    }

    [Fact]
    public async Task Script_Errors_Exit_1_With_Located_Diagnostics()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        File.WriteAllText(dir.File("bad.lua"), "print('a')\nlocal t = nil\nt.x = 1\n");
        (int code, string o, _) = await Cli(Env(), "run", dir.File("bad.lua"), level, "--json");
        Assert.Equal(1, code);
        JsonNode j = Json(o);
        Assert.Equal("script_failed", j["error"]!["code"]!.GetValue<string>());
        Assert.Equal(3, j["result"]!["error"]!["line"]!.GetValue<int>());
        Assert.Equal("a", j["result"]!["log"]![0]!["message"]!.GetValue<string>());

        (code, _, string err) = await Cli(Env(), "run", dir.File("bad.lua"), level);
        Assert.Equal(1, code);
        Assert.Contains("bad.lua:3", err);
    }

    [Fact]
    public async Task Fail_On_Holes_Exits_5_When_The_Level_Leaks()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteLeakyLevel(dir.File("leak.rfl"));
        (int code, string o, _) = await Cli(Env(), "holes", level);
        Assert.Equal(0, code); // leaks are data by default
        Assert.Contains("hole(s)", o);

        (code, _, _) = await Cli(Env(), "holes", level, "--fail-on-holes");
        Assert.Equal(5, code);
    }

    [Fact]
    public async Task Attach_Without_A_Running_Editor_Exits_3()
    {
        (int code, string o, string e) = await Cli(Env(), "info", "--attach", "--json");
        Assert.Equal(3, code);
        Assert.Contains("running Glacier editor", e);
        Assert.Equal("no_editor", Json(o)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bad_Rf_Dir_Flag_Is_An_Io_Error()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (int code, _, _) = await Cli(Env(), "lint", level, "--rf-dir", dir.File("no-such-install"));
        Assert.Equal(4, code);
    }
    [Fact]
    public async Task Verbose_Names_The_Rf_Install_Source_In_Precedence_Order()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        string fromSettings = Directory.CreateDirectory(dir.File("rf-settings")).FullName;
        string fromEnv = Directory.CreateDirectory(dir.File("rf-env")).FullName;
        string fromFlag = Directory.CreateDirectory(dir.File("rf-flag")).FullName;
        string settings = dir.File("settings.cfg");
        File.WriteAllText(settings, new JsonObject { ["RfInstallDir"] = fromSettings }.ToJsonString());

        CliEnvironment With(string? settingsPath, string? env) => new()
        {
            PipeName = "glacier-test-" + Guid.NewGuid().ToString("N"),
            SettingsPath = settingsPath ?? dir.File("no-such-settings.cfg"),
            GetEnvironmentVariable = k => k == "GED_RF_DIR" ? env : null,
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            Clock = () => TestLevels.Fixed,
        };

        (int code, _, string e) = await Cli(With(settings, null), "lint", level, "--verbose");
        Assert.True(code == 0, e);
        Assert.Contains($"RF install: {fromSettings} (from editor settings: {settings})", e);

        (code, _, e) = await Cli(With(settings, fromEnv), "lint", level, "--verbose");
        Assert.True(code == 0, e);
        Assert.Contains($"RF install: {fromEnv} (from GED_RF_DIR)", e);

        (code, _, e) = await Cli(With(settings, fromEnv), "lint", level, "--verbose", "--rf-dir", fromFlag);
        Assert.True(code == 0, e);
        Assert.Contains($"RF install: {fromFlag} (from --rf-dir)", e);

        (code, _, e) = await Cli(With(null, null), "lint", level, "--verbose");
        Assert.True(code == 0, e);
        Assert.Contains("RF install: none (no --rf-dir, no GED_RF_DIR, and no editor settings file was found)", e);

        // Without --verbose nothing is reported.
        (code, _, e) = await Cli(With(settings, null), "lint", level);
        Assert.True(code == 0, e);
        Assert.DoesNotContain("RF install:", e);
    }

    [Theory]
    [InlineData("--out", "--out cannot be combined with --dry-run")]
    [InlineData("--save", "--save cannot be combined with --dry-run")]
    [InlineData("--save=", "--save=<path> cannot be combined with --dry-run")]
    public async Task Dry_Run_With_A_Save_Names_The_Flag_Actually_Given(string flag, string expected)
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        File.WriteAllText(dir.File("s.lua"), "print('x')");
        string[] save = flag switch
        {
            "--out" => new[] { "--out", dir.File("o.rfl") },
            "--save=" => new[] { "--save=" + dir.File("o.rfl") },
            _ => new[] { "--save" },
        };
        string[] args = new[] { "run", dir.File("s.lua"), level, "--dry-run" }.Concat(save).ToArray();
        (int code, _, string e) = await Cli(Env(), args);
        Assert.Equal(2, code);
        Assert.Contains(expected, e);
        Assert.Contains("drop --dry-run", e, StringComparison.OrdinalIgnoreCase);
        if (flag != "--save")
        {
            Assert.DoesNotContain("--save cannot", e);
        }

        Assert.False(File.Exists(dir.File("o.rfl")));
    }

    [Fact]
    public async Task Budget_Limits_Lua_Instructions_And_Zero_Lifts_It()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        File.WriteAllText(dir.File("loop.lua"), "local n = 0 for i = 1, 200000 do n = n + i end return n");

        (int code, string o, _) = await Cli(Env(), "run", dir.File("loop.lua"), level, "--dry-run", "--budget", "50000", "--json");
        Assert.Equal(1, code);
        JsonNode j = Json(o);
        Assert.False(j["result"]!["success"]!.GetValue<bool>());
        Assert.Contains("budget", j["result"]!["error"]!["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        (code, o, string e) = await Cli(Env(), "run", dir.File("loop.lua"), level, "--dry-run", "--budget", "0", "--json");
        Assert.True(code == 0, e + o);
        Assert.True(Json(o)["result"]!["success"]!.GetValue<bool>());

        // --timeout 0 lifts the budget too, unless --budget is given.
        (code, o, e) = await Cli(Env(), "run", dir.File("loop.lua"), level, "--dry-run", "--timeout", "0", "--json");
        Assert.True(code == 0, e + o);
        (code, _, _) = await Cli(Env(), "run", dir.File("loop.lua"), level, "--dry-run", "--timeout", "0", "--budget", "50000", "--json");
        Assert.Equal(1, code);

        (code, _, _) = await Cli(Env(), "run", dir.File("loop.lua"), level, "--budget", "-5");
        Assert.Equal(2, code);
        (code, _, e) = await Cli(Env(), "lint", level, "--budget", "5");
        Assert.Equal(2, code);
        Assert.Contains("--budget only applies to 'run'", e);
        Assert.Contains("--budget <n>", CliHelp.Text("run"));
    }

    [Fact]
    public void Script_Limits_Follow_Timeout_And_Budget()
    {
        Ged.Core.Scripting.ScriptExecutionLimits d = Ged.Core.Scripting.ScriptExecutionLimits.Default;
        Assert.Equal(200_000_000, AutomationCommands.Limits(null, null).InstructionBudget);
        Assert.Equal(d.Timeout, AutomationCommands.Limits(null, null).Timeout);
        Assert.Equal(200_000_000, AutomationCommands.Limits(60, null).InstructionBudget);
        Assert.Equal(TimeSpan.FromSeconds(60), AutomationCommands.Limits(60, null).Timeout);
        Assert.Equal(0, AutomationCommands.Limits(0, null).InstructionBudget);
        Assert.Equal(TimeSpan.Zero, AutomationCommands.Limits(0, null).Timeout);
        Assert.Equal(5_000, AutomationCommands.Limits(0, 5_000).InstructionBudget);
        Assert.Equal(0, AutomationCommands.Limits(null, 0).InstructionBudget);
        Assert.Equal(d.Timeout, AutomationCommands.Limits(null, 0).Timeout);
        Assert.Equal(900_000_000, AutomationCommands.Limits(null, 900_000_000).InstructionBudget);
        Assert.Throws<AutomationException>(() => AutomationCommands.Limits(null, -1));
        Assert.Throws<AutomationException>(() => AutomationCommands.Limits(null, 1.5));
    }
}
