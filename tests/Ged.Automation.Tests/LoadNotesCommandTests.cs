using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>
/// <c>info</c> / <c>open</c> (and so the MCP <c>open_level</c> tool) carry the document's load notes:
/// an additive <c>notes</c> array that is absent for a normal level.
/// </summary>
public sealed class LoadNotesCommandTests
{
    [Fact]
    public async Task Info_Reports_An_Interrupted_Save_And_Omits_Notes_For_A_Normal_Level()
    {
        using var dir = new TempDir();
        string normal = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        string interrupted = dir.File("interrupted.rfl");
        File.WriteAllBytes(interrupted, InterruptedSave());

        JsonNode normalInfo = await Info(dir, normal);
        Assert.Null(normalInfo["notes"]);

        JsonNode info = await Info(dir, interrupted);
        JsonArray notes = Assert.IsType<JsonArray>(info["notes"]);
        Assert.Contains("interrupted save", Assert.Single(notes)!.GetValue<string>());
    }

    [Fact]
    public async Task A_Level_Newer_Than_306_Opens_Read_Only_With_A_Note_And_Every_Save_Is_Refused()
    {
        using var dir = new TempDir();
        string newer = TestLevels.WriteRoomLevel(dir.File("newer.rfl"));
        byte[] bytes = File.ReadAllBytes(newer);
        BitConverter.GetBytes(307).CopyTo(bytes, 4); // a future Alpine Faction level version
        File.WriteAllBytes(newer, bytes);

        JsonNode info = await Info(dir, newer);
        Assert.True(info["readOnly"]!.GetValue<bool>());
        string note = Assert.Single(Assert.IsType<JsonArray>(info["notes"]))!.GetValue<string>();
        Assert.Contains("RFL version 307", note, StringComparison.Ordinal);
        Assert.Contains("read-only", note, StringComparison.Ordinal);

        using var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, () => TestLevels.Fixed);
        host.PathPolicy.AddRoot(dir.Path);
        var commands = new AutomationCommands(host);
        AutomationResponse open = await commands.ExecuteAsync(new AutomationRequest("open", new JsonObject { ["path"] = newer }));
        Assert.True(open.Ok, open.Error?.Message);
        Assert.True(open.Result!["readOnly"]!.GetValue<bool>());

        foreach (JsonObject args in new[]
        {
            new JsonObject(),
            new JsonObject { ["out"] = dir.File("copy.rfl") },
        })
        {
            AutomationResponse save = await commands.ExecuteAsync(new AutomationRequest("save", args));
            Assert.False(save.Ok);
            Assert.Equal(AutomationErrorCodes.SaveFailed, save.Error!.Code);
            Assert.Contains("RFL version 307", save.Error.Message, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(dir.File("copy.rfl")));
        Assert.Equal(bytes, File.ReadAllBytes(newer));
    }

    [Fact]
    public async Task A_Normal_Level_Is_Not_Read_Only()
    {
        using var dir = new TempDir();
        JsonNode info = await Info(dir, TestLevels.WriteRoomLevel(dir.File("room.rfl")));
        Assert.Null(info["readOnly"]);
        Assert.Equal(306, info["rflVersion"]!.GetValue<int>());
    }

    private static async Task<JsonNode> Info(TempDir dir, string level)
    {
        using var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, () => TestLevels.Fixed);
        host.PathPolicy.AddRoot(dir.Path);
        AutomationResponse r = await new AutomationCommands(host).ExecuteAsync(
            new AutomationRequest("info", new JsonObject { ["level"] = level }));
        Assert.True(r.Ok, r.Error?.Message);
        return r.Result!;
    }

    /// <summary>A v304 file whose header says it holds no sections and whose table has no End marker.</summary>
    private static byte[] InterruptedSave()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true))
        {
            w.Write(0xD4BADA55u);
            w.Write(0x130); // version 304
            w.Write(0u); // timestamp
            w.Write(0); // player start offset
            w.Write(0); // level info offset
            w.Write(0); // section count (never patched)
            w.Write(0); // sections total size
            w.Write((ushort)0); // level name
            w.Write((ushort)0); // mod name
            w.Write(0x7000u); // tga file list
            w.Write(4);
            w.Write(0);
        }

        return ms.ToArray();
    }
}
