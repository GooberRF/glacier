using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Tools;
using Ged.Cli;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>
/// The tools and the CLI verbs run the same handlers: the same request through <c>glacier-cli --json</c> yields the
/// same <c>result</c> as the tool (modulo the documented MCP shaping: the budget, and the AI/CLI label prefix).
/// Plus the CLI side of the catalog (generated help options, accepted flags) and the stdout purity of the real
/// <c>glacier-cli mcp</c> process.
/// </summary>
[Collection(GpuRenderCollection.Name)]
public sealed class McpCliParityTests
{
    private static CliEnvironment Env() => McpTestSession.Env();

    private static async Task<JsonNode> CliJson(params string[] args)
    {
        (int code, string o, string e) = await CliTests.Cli(Env(), args.Append("--json").ToArray());
        Assert.True(code == 0, $"glacier-cli {string.Join(' ', args)} exited {code}: {e}{o}");
        return JsonNode.Parse(o)!["result"]!;
    }

    [Fact]
    public async Task Read_Tools_And_Cli_Verbs_Return_The_Same_Result()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        string level = s.WriteRoomLevel();
        await s.OkAsync("open_level", new { path = level });
        int light = s.Document!.Objects.First(o => o.Kind == Ged.Core.Editor.LevelObjectKind.Light).Uid;
        int brush = Ged.Automation.LevelOperations.Brushes(s.Document).First().Uid;

        var cases = new (string Tool, object? Args, string[] Cli)[]
        {
            ("level_summary", new { detail = "brief" }, new[] { "summary", level, "--detail", "brief" }),
            ("level_summary", null, new[] { "summary", level }),
            ("inspect", new { uid = light }, new[] { "inspect", light.ToString(), level }),
            ("inspect", new { uid = brush }, new[] { "inspect", brush.ToString(), level }),
            ("query", new { kind = "Light" }, new[] { "query", level, "--kind", "Light" }),
            ("query", new { what = "brushes", region = new[] { -1.0, 0, -1, 1, 4, 1 } }, new[] { "query", level, "--what", "brushes", "--region", "-1,0,-1,1,4,1" }),
            ("lint", new { min_severity = "warning" }, new[] { "lint", level, "--min-severity", "warning" }),
            ("check_holes", null, new[] { "holes", level }),
            ("list_classes", new { kind = "Event", filter = "Mess" }, new[] { "catalog", "classes", "--kind", "Event", "--filter", "Mess" }),
            ("describe_class", new { kind = "Event", @class = "Message" }, new[] { "catalog", "describe", "--kind", "Event", "--class", "Message" }),
            ("list_textures", null, new[] { "catalog", "textures" }),
            ("grade_level", new { gametype = "dm" }, new[] { "grade", level, "--gametype", "dm" }),
            ("spatial_query", new { op = "walk" }, new[] { "spatial", "walk", level }),
            ("spatial_query", new { op = "raycast", p = new[] { 0.0, 1, 0 }, dir = new[] { 1.0, 0, 0 } }, new[] { "spatial", "raycast", level, "--from", "0,1,0", "--dir", "1,0,0" }),
            ("spatial_query", new { op = "area_report", p = new[] { 0.0, 0, 0 }, radius = 6 }, new[] { "spatial", "area_report", level, "--around", "0,0,0", "--radius", "6" }),
        };

        foreach ((string tool, object? args, string[] cli) in cases)
        {
            JsonObject viaTool = await s.OkAsync(tool, args);
            JsonNode viaCli = await CliJson(cli);
            Assert.True(JsonNode.DeepEquals(viaCli, viaTool), $"{tool}: CLI {viaCli.ToJsonString()} vs tool {viaTool.ToJsonString()}");
        }

        // api_reference vs the api command (the CLI's api verb prints the same text).
        JsonObject api = await s.OkAsync("api_reference", new { member = "level.place" });
        JsonNode apiCli = await CliJson("api", "--member", "level.place");
        Assert.Equal(apiCli["text"]!.GetValue<string>(), api["text"]!.GetValue<string>());
    }

    [Fact]
    public void Every_Cli_Flag_Of_A_Tool_Is_Documented_And_Accepted()
    {
        foreach (ToolSpec spec in ToolCatalog.All.Where(t => t.CliVerb is not null))
        {
            string help = CliHelp.Text(spec.CliVerb);
            foreach (ToolArg a in spec.Args.Where(a => a.CliFlag is not null))
            {
                Assert.True(help.Contains(a.CliFlag!, StringComparison.Ordinal), $"help {spec.CliVerb} does not mention {a.CliFlag} ({spec.Name}.{a.Name})");
                string[] argv = a.Type == "boolean" ? new[] { spec.CliVerb!, a.CliFlag! } : new[] { spec.CliVerb!, a.CliFlag!, a.Type == "array" ? "1,2,3" : "1" };
                CliArguments parsed = CliArguments.Parse(argv); // throws UsageException for an unknown flag
                Assert.Equal(spec.CliVerb, parsed.Verb);
            }
        }

        // The generated option lines carry the tool argument's own documentation.
        Assert.Contains(ToolCatalog.Find("query")!.Args.First(a => a.Name == "tag").Doc, CliHelp.Text("query"));
        Assert.Contains("--max-changes", CliHelp.Text("diff"));
    }

    [Fact]
    public async Task Stdout_Carries_Only_Json_Rpc_And_Progress_Precedes_The_Result()
    {
        string? dll = Path.Combine(AppContext.BaseDirectory, "glacier-cli.dll");
        Assert.True(File.Exists(dll), dll);
        using var scratch = new TempDir();
        string level = TestLevels.WriteRoomLevel(scratch.File("room.rfl"));
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("mcp");
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--scratch");
        psi.ArgumentList.Add(scratch.Path);
        psi.Environment["GED_RF_DIR"] = string.Empty;
        using Process p = Process.Start(psi)!;
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }
        };
        p.BeginErrorReadLine();
        var lines = new List<string>();

        async Task<JsonObject> Request(int id, string method, JsonObject? @params)
        {
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (@params is not null)
            {
                msg["params"] = @params;
            }

            await p.StandardInput.WriteLineAsync(msg.ToJsonString());
            await p.StandardInput.FlushAsync();
            while (true)
            {
                string? line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120));
                Assert.True(line is not null, "the server closed standard output early; stderr: " + stderr);
                lines.Add(line!);
                JsonObject parsed = JsonNode.Parse(line!)!.AsObject(); // every stdout line must be JSON
                Assert.Equal("2.0", parsed["jsonrpc"]?.GetValue<string>());
                if (parsed["id"]?.GetValue<int>() == id)
                {
                    return parsed;
                }
            }
        }

        JsonObject init = await Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "purity-test", ["version"] = "1" },
        });
        Assert.NotNull(init["result"]!["serverInfo"]);
        await p.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString());
        Assert.Equal(ToolCatalog.All.Count, (await Request(2, "tools/list", null))["result"]!["tools"]!.AsArray().Count);
        JsonObject Call(string name, JsonObject args, string? progressToken = null)
        {
            var o = new JsonObject { ["name"] = name, ["arguments"] = args };
            if (progressToken is not null)
            {
                o["_meta"] = new JsonObject { ["progressToken"] = progressToken };
            }

            return o;
        }

        Assert.False((await Request(3, "tools/call", Call("open_level", new JsonObject { ["path"] = level })))["result"]!["isError"]?.GetValue<bool>() == true);
        JsonObject failing = await Request(4, "tools/call", Call("run_lua", new JsonObject { ["code"] = "print('to the log, not stdout')\nerror('boom')" }));
        Assert.True(failing["result"]!["isError"]!.GetValue<bool>());

        int before = lines.Count;
        JsonObject built = await Request(5, "tools/call", Call("build", new JsonObject(), progressToken: "b1"));
        Assert.False(built["result"]!["isError"]?.GetValue<bool>() == true);
        List<string> buildLines = lines.Skip(before).ToList();
        Assert.True(buildLines.Count >= 2, "no progress notification preceded the build result");
        Assert.All(buildLines.Take(buildLines.Count - 1), l => Assert.Equal("notifications/progress", JsonNode.Parse(l)!["method"]?.GetValue<string>()));

        JsonObject render = await Request(6, "tools/call", Call("render_plan", new JsonObject { ["size"] = "64x64" }));
        Assert.NotNull(render["result"]);

        p.StandardInput.Close();
        Assert.True(p.WaitForExit(30_000), "the server did not exit when its input closed");
        Assert.Equal(0, p.ExitCode);
        Assert.DoesNotContain(lines, l => l.Contains("to the log, not stdout", StringComparison.Ordinal) && !l.StartsWith("{", StringComparison.Ordinal));
        lock (stderr)
        {
            Assert.Contains("glacier-cli mcp:", stderr.ToString());
        }
    }
}
