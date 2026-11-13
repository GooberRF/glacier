using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Automation.Tools;
using Ged.Cli.Mcp;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ged.Automation.Tests.Mcp;

/// <summary>MCP result shaping: render images (size, cap, mapping JSON) and the text budgets on a large level.</summary>
[Collection(GpuRenderCollection.Name)]
public sealed class McpResultTests
{
    [Fact]
    public async Task Render_Tools_Return_A_Decodable_Png_At_The_Requested_Size_Plus_The_Mapping()
    {
        if (!McpServerTests.HasGpu())
        {
            return; // no GPU device on this machine: covered by the render_unavailable path
        }

        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = s.WriteRoomLevel() });
        (bool isError, JsonObject json, CallToolResult result) = await s.CallAsync("render_plan", new { size = "320x200" });
        Assert.False(isError, json.ToJsonString());
        Assert.Equal(2, result.Content.Count);
        Assert.IsType<TextContentBlock>(result.Content[0]);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        (int w, int h, _) = PngTestReader.Decode(image.DecodedData.ToArray());
        Assert.Equal((320, 200), (w, h));
        Assert.Equal("plan", json["view"]!.GetValue<string>());
        Assert.True(json["worldPerPixel"]!.GetValue<double>() > 0);
        Assert.NotNull(json["origin"]);
        Assert.NotNull(json["legend"]);
        Assert.StartsWith(s.Scratch, json["path"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // The render worked on a private copy: the session's level is untouched (no preview build applied).
        Assert.False(s.Document!.IsDirty);
        Assert.Equal("none", (await s.OkAsync("session_info"))["document"]!["geometry"]!.GetValue<string>());

        // An oversize request is clamped to the server's cap, and says so.
        (isError, json, result) = await s.CallAsync("render_view", new { view = "top", size = "4000x2000" });
        Assert.False(isError, json.ToJsonString());
        (w, h, _) = PngTestReader.Decode(((ImageContentBlock)result.Content[1]).DecodedData.ToArray());
        Assert.Equal((ToolCatalog.DefaultImageSide, ToolCatalog.DefaultImageSide / 2), (w, h));
        Assert.Contains(json["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("exceeds this server's image cap", StringComparison.Ordinal));

        // A lower --max-image cap applies to the default size too.
        await using McpTestSession small = await McpTestSession.StartAsync(new McpLaunchOptions { MaxImage = 256 });
        await small.OkAsync("open_level", new { path = small.WriteRoomLevel() });
        (_, _, result) = await small.CallAsync("render_plan");
        (w, h, _) = PngTestReader.Decode(((ImageContentBlock)result.Content[1]).DecodedData.ToArray());
        Assert.Equal(256, Math.Max(w, h)); // the plan takes the level's proportions on the capped longest side
    }

    [Fact]
    public async Task Render_Without_A_Level_Is_A_Clear_Error()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        JsonObject e = await s.FailAsync("render_plan", AutomationErrorCodes.NoDocument);
        Assert.Contains("open_level", e["message"]!.GetValue<string>());
    }

    /// <summary>A level with 2,000 objects (lights and items in a grid) and one room, written to <paramref name="path"/>.</summary>
    private static string WriteBigLevel(string path)
    {
        EditorDocument doc = LevelTemplates.CreateNew(TestLevels.Fixed);
        var ed = new BrushEditor(doc);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 200, Height = 8, Depth = 200, Air = true }, new Vec3(0, 4, 0), Mat3.Identity);
        for (int i = 0; i < 2000; i++)
        {
            doc.PlaceObject(i % 2 == 0 ? LevelObjectKind.Light : LevelObjectKind.MpRespawnPoint, new Vec3(-90 + (i % 45) * 4, 2, -90 + (i / 45) * 4));
        }

        doc.Save(path, updateTimestamp: false);
        return path;
    }

    [Fact]
    public async Task Every_Result_On_A_Large_Level_Fits_The_Budget_As_Valid_Json()
    {
        await using McpTestSession s = await McpTestSession.StartAsync();
        await s.OkAsync("open_level", new { path = WriteBigLevel(s.Dir.File("big.rfl")) });
        await s.OkAsync("checkpoint", new { name = "start" });
        await s.OkAsync("run_lua", new { code = "for _, o in ipairs(level.objects_of('Light')) do o:set_pos(o.x, 3, o.z) end" });

        var calls = new (string Tool, object? Args)[]
        {
            ("level_summary", new { detail = "full" }),
            ("level_summary", new { detail = "normal" }),
            ("level_summary", new { detail = "brief" }),
            ("query", new { limit = 500 }),
            ("diff_since", new { checkpoint = "start", max_changes = 1000 }),
            ("lint", null),
            ("inspect", new { uid = s.Document!.Objects.Last().Uid }),
            ("api_reference", null),
            ("api_reference", new { member = "level" }),
            ("list_classes", new { kind = "Event" }),
            ("describe_class", new { kind = "Event", @class = "Message" }),
        };
        foreach ((string tool, object? args) in calls)
        {
            (bool isError, JsonObject json, CallToolResult result) = await s.CallAsync(tool, args);
            Assert.False(isError, $"{tool}: {json.ToJsonString()[..Math.Min(400, json.ToJsonString().Length)]}");
            string text = ((TextContentBlock)result.Content[0]).Text;
            Assert.True(text.Length <= ToolCatalog.DefaultResultBudget, $"{tool} {System.Text.Json.JsonSerializer.Serialize(args)}: {text.Length} characters");
            Assert.NotNull(JsonNode.Parse(text)); // never cut mid-JSON
        }

        JsonObject q = await s.OkAsync("query", new { limit = 500 });
        Assert.True(q["truncated"]!.GetValue<bool>());
        Assert.Equal(2001, q["total"]!.GetValue<int>()); // + the Player Start
        Assert.NotNull(q["hint"]);

        JsonObject full = await s.OkAsync("level_summary", new { detail = "full" });
        Assert.True(full["truncated"]?.GetValue<bool>() == true || full["notes"] is not null);

        JsonObject d = await s.OkAsync("diff_since", new { checkpoint = "start", max_changes = 1000 });
        Assert.Equal(1000, d["counts"]!["changed"]!.GetValue<int>());
        Assert.True(d["truncated"]!.GetValue<bool>());

        // A larger budget asked for by the call is honoured, up to the hard cap.
        (_, _, CallToolResult big) = await s.CallAsync("query", new { limit = 500, max_chars = 60000 });
        Assert.InRange(((TextContentBlock)big.Content[0]).Text.Length, ToolCatalog.DefaultResultBudget, ToolCatalog.MaxResultBudget);
    }

    [Fact]
    public void Output_Budget_Cuts_Lists_Structurally()
    {
        var o = new JsonObject
        {
            ["items"] = new JsonArray(Enumerable.Range(0, 5000).Select(i => (JsonNode?)new JsonObject { ["uid"] = i, ["name"] = "object number " + i }).ToArray()),
            ["small"] = new JsonArray(1, 2, 3),
        };
        JsonNode? shaped = OutputBudget.Shape("query", o, 10_000);
        string text = shaped!.ToJsonString();
        Assert.True(text.Length <= 10_000, text.Length.ToString());
        Assert.True(shaped["truncated"]!.GetValue<bool>());
        Assert.Equal(3, shaped["small"]!.AsArray().Count);
        Assert.Contains("items", shaped["budget"]!["cut"]![0]!.GetValue<string>());

        var log = new JsonObject { ["log"] = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode?)new JsonObject { ["level"] = "output", ["message"] = $"line {i}" }).ToArray()) };
        JsonNode shapedLog = OutputBudget.Shape("run_lua", log, 24_000)!;
        Assert.Equal(OutputBudget.LogHead + OutputBudget.LogTail, shapedLog["log"]!.AsArray().Count);
        Assert.Equal("line 0", shapedLog["log"]![0]!["message"]!.GetValue<string>());
        Assert.Equal("line 999", shapedLog["log"]!.AsArray().Last()!["message"]!.GetValue<string>());
        Assert.Equal(1000 - OutputBudget.LogHead - OutputBudget.LogTail, shapedLog["logTruncated"]!.GetValue<int>());
    }
}
