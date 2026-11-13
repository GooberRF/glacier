using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Cli;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// The editor bridge wire protocol over a real per-test named pipe, with an in-process server and a
/// window-less host standing in for the editor (the live editor runs the same server + handlers).
/// </summary>
public sealed class BridgeTests
{
    private static string NewPipe() => "glacier-test-" + Guid.NewGuid().ToString("N");

    private static async Task<(BridgeServer Server, HeadlessAutomationHost Host)> StartAsync(string pipe, int maxBytes = BridgeProtocol.MaxMessageBytes)
    {
        var host = new HeadlessAutomationHost(clock: () => TestLevels.Fixed);
        var commands = new AutomationCommands(host);
        var server = new BridgeServer(pipe, (req, ct) => commands.ExecuteAsync(req, ct), maxBytes);
        Assert.True(server.TryStart(out string? reason), reason);
        await Task.Yield();
        return (server, host);
    }

    private static async Task<NamedPipeClientStream> RawConnectAsync(string pipe)
    {
        var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await c.ConnectAsync(5000);
        return c;
    }

    private static async Task<JsonNode> RawExchangeAsync(Stream s, FrameReader reader, string line)
    {
        await BridgeProtocol.WriteLineAsync(s, line, CancellationToken.None);
        (FrameStatus status, string? reply) = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(FrameStatus.Line, status);
        return JsonNode.Parse(reply!)!;
    }

    [Fact]
    public void Pipe_Name_Is_Stable_Per_User_And_Differs_Between_Users()
    {
        Assert.Equal(BridgeProtocol.PipeName("alice"), BridgeProtocol.PipeName("ALICE"));
        Assert.NotEqual(BridgeProtocol.PipeName("alice"), BridgeProtocol.PipeName("bob"));
        Assert.StartsWith("glacier-bridge-", BridgeProtocol.PipeName());
    }

    [Fact]
    public async Task Requests_Round_Trip_Sequentially_On_One_Connection()
    {
        string pipe = NewPipe();
        (BridgeServer server, HeadlessAutomationHost host) = await StartAsync(pipe);
        await using (server)
        using (host)
        {
            using BridgeClient client = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
            Assert.NotNull(client);

            AutomationResponse hello = await client.SendAsync(new AutomationRequest("hello"));
            Assert.True(hello.Ok);
            Assert.Equal(BridgeProtocol.Version, hello.Result!["protocol"]!.GetValue<int>());
            Assert.False(hello.Result!["hasDocument"]!.GetValue<bool>());

            AutomationResponse none = await client.SendAsync(new AutomationRequest("lint"));
            Assert.False(none.Ok);
            Assert.Equal(AutomationErrorCodes.NoDocument, none.Error!.Code);

            using var dir = new TempDir();
            string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
            File.WriteAllText(dir.File("secret.txt"), "not a level");
            AutomationResponse open = await client.SendAsync(new AutomationRequest("open", new JsonObject { ["path"] = dir.File("secret.txt") }));
            Assert.False(open.Ok); // only level files can be read
            Assert.Equal(AutomationErrorCodes.Usage, open.Error!.Code);

            open = await client.SendAsync(new AutomationRequest("open", new JsonObject { ["path"] = level }));
            Assert.True(open.Ok, open.Error?.Message);

            // Writes stay allow-listed: the opened level's folder is fine, anywhere else is not.
            using var elsewhere = new TempDir();
            AutomationResponse save = await client.SendAsync(new AutomationRequest("save", new JsonObject { ["out"] = elsewhere.File("x.rfl") }));
            Assert.Equal(AutomationErrorCodes.PathNotAllowed, save.Error!.Code);

            AutomationResponse run = await client.SendAsync(new AutomationRequest("run", new JsonObject
            {
                ["name"] = "a.lua",
                ["script"] = "level.place_box(0, 9, 0, 1, 1, 1)",
            }));
            Assert.True(run.Ok, run.Error?.Message);
            Assert.Equal("CLI: a.lua", host.Document!.Undo.Current.Description);
            Assert.Equal(3, LevelOperations.Brushes(host.Document).Count);
        }
    }

    [Fact]
    public async Task Malformed_Wrong_Version_And_Oversize_Requests_Get_Protocol_Errors_And_The_Connection_Survives()
    {
        string pipe = NewPipe();
        (BridgeServer server, HeadlessAutomationHost host) = await StartAsync(pipe, maxBytes: 1024);
        await using (server)
        using (host)
        {
            using NamedPipeClientStream s = await RawConnectAsync(pipe);
            var reader = new FrameReader(s);

            JsonNode bad = await RawExchangeAsync(s, reader, "{ this is not json");
            Assert.False(bad["ok"]!.GetValue<bool>());
            Assert.Equal("protocol", bad["error"]!["code"]!.GetValue<string>());

            JsonNode notObject = await RawExchangeAsync(s, reader, "[1,2,3]");
            Assert.Equal("protocol", notObject["error"]!["code"]!.GetValue<string>());

            JsonNode version = await RawExchangeAsync(s, reader, "{\"protocol\":99,\"id\":\"v\",\"command\":\"hello\"}");
            Assert.Equal("protocol", version["error"]!["code"]!.GetValue<string>());
            Assert.Equal("v", version["id"]!.GetValue<string>()); // id echoed back

            JsonNode big = await RawExchangeAsync(s, reader,
                "{\"protocol\":1,\"id\":3,\"command\":\"run\",\"args\":{\"script\":\"" + new string('x', 4096) + "\"}}");
            Assert.Equal("protocol", big["error"]!["code"]!.GetValue<string>());
            Assert.Contains("exceeds", big["error"]!["message"]!.GetValue<string>());

            JsonNode hello = await RawExchangeAsync(s, reader, "{\"protocol\":1,\"id\":42,\"command\":\"hello\"}");
            Assert.True(hello["ok"]!.GetValue<bool>());
            Assert.Equal(42, hello["id"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task A_Second_Server_On_The_Same_Name_Does_Not_Start()
    {
        string pipe = NewPipe();
        (BridgeServer first, HeadlessAutomationHost host) = await StartAsync(pipe);
        await using (first)
        using (host)
        {
            await using var second = new BridgeServer(pipe, (r, _) => Task.FromResult(new AutomationResponse { Ok = true, Command = r.Command }));
            Assert.False(second.TryStart(out string? reason));
            Assert.False(string.IsNullOrEmpty(reason));

            // The first still serves.
            using BridgeClient client = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
            Assert.True((await client.SendAsync(new AutomationRequest("hello"))).Ok);
        }
    }

    [Fact]
    public async Task Server_Accepts_A_New_Client_After_One_Disconnects_And_Stops_On_Dispose()
    {
        string pipe = NewPipe();
        (BridgeServer server, HeadlessAutomationHost host) = await StartAsync(pipe);
        using (host)
        {
            await using (server)
            {
                for (int i = 0; i < 2; i++)
                {
                    using BridgeClient c = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
                    Assert.NotNull(c);
                    Assert.True((await c.SendAsync(new AutomationRequest("hello"))).Ok);
                }
            }

            Assert.Null(await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromMilliseconds(300)));
        }
    }

    [Fact]
    public async Task Cli_Attach_Runs_On_The_Served_Document()
    {
        string pipe = NewPipe();
        (BridgeServer server, HeadlessAutomationHost host) = await StartAsync(pipe);
        await using (server)
        using (host)
        {
            using var dir = new TempDir();
            var env = new CliEnvironment
            {
                PipeName = pipe,
                GetEnvironmentVariable = _ => null,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            (int code, string o, string e) = await CliTests.Cli(env, "new", "--attach", "--json");
            Assert.True(code == 0, e + o);
            Assert.NotNull(host.Document);

            File.WriteAllText(dir.File("box.lua"), "level.place_box(0, 2, 0, 4, 4, 4):set_air(true)");
            (code, o, e) = await CliTests.Cli(env, "run", dir.File("box.lua"), "--attach", "--json");
            Assert.True(code == 0, e + o);
            Assert.Equal("CLI: box.lua", host.Document!.Undo.Current.Description);

            // An unsaved attached level has no path: save needs one, and the bridge does not allow-list
            // arbitrary command-line paths.
            (code, o, _) = await CliTests.Cli(env, "save", "--attach", "--json");
            Assert.Equal(2, code);
            (code, o, _) = await CliTests.Cli(env, "save", "--attach", "--out", dir.File("x.rfl"), "--json");
            Assert.Equal(4, code);
            Assert.Equal("path_not_allowed", JsonNode.Parse(o)!["error"]!["code"]!.GetValue<string>());

            (code, o, _) = await CliTests.Cli(env, "summary", "--attach", "--detail", "brief", "--json");
            Assert.Equal(0, code);
            Assert.Equal(1, JsonNode.Parse(o)!["result"]!["brushes"]!["total"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Frame_Reader_Splits_Lines_And_Flags_Oversize()
    {
        byte[] data = Encoding.UTF8.GetBytes("a\r\n" + new string('b', 100) + "\nc\n");
        var reader = new FrameReader(new MemoryStream(data), maxBytes: 50);
        Assert.Equal((FrameStatus.Line, "a"), await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(FrameStatus.TooLarge, (await reader.ReadAsync(CancellationToken.None)).Status);
        Assert.Equal((FrameStatus.Line, "c"), await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(FrameStatus.EndOfStream, (await reader.ReadAsync(CancellationToken.None)).Status);
    }
}
