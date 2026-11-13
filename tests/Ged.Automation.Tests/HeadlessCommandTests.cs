using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.Automation.Tests;

/// <summary>The shared command handlers on a headless session: open/new/run/build/holes/lint/summary/save.</summary>
public sealed class HeadlessCommandTests
{
    private static (HeadlessAutomationHost Host, AutomationCommands Commands) Session(Func<DateTime>? clock = null)
    {
        var host = new HeadlessAutomationHost(EditorSettingsSnapshot.Defaults, rfInstallDir: null, clock ?? (() => TestLevels.Fixed));
        return (host, new AutomationCommands(host));
    }

    private static AutomationRequest Req(string command, JsonObject? args = null) => new(command, args);

    private static async Task<JsonNode> OkResult(AutomationCommands c, AutomationRequest r)
    {
        AutomationResponse resp = await c.ExecuteAsync(r);
        Assert.True(resp.Ok, $"{r.Command}: {resp.Error?.Code} {resp.Error?.Message}");
        return resp.Result!;
    }

    [Fact]
    public async Task Info_Opens_The_Level_Argument_And_Reports_Counts()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode info = await OkResult(c, Req("info", new JsonObject { ["level"] = level }));
            Assert.Equal("room.rfl", info["name"]!.GetValue<string>());
            Assert.Equal(2, info["brushes"]!.GetValue<int>());
            Assert.Equal(3, info["objects"]!.GetValue<int>()); // player start + light + respawn
            Assert.Equal("none", info["geometry"]!.GetValue<string>());
            Assert.False(info["dirty"]!.GetValue<bool>());
            Assert.Equal("headless", info["mode"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Commands_Without_A_Level_Report_No_Document()
    {
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            AutomationResponse r = await c.ExecuteAsync(Req("lint"));
            Assert.False(r.Ok);
            Assert.Equal(AutomationErrorCodes.NoDocument, r.Error!.Code);

            r = await c.ExecuteAsync(Req("frobnicate"));
            Assert.Equal(AutomationErrorCodes.UnknownCommand, r.Error!.Code);
        }
    }

    [Fact]
    public async Task Run_Is_One_Undo_Step_Labelled_For_The_Cli_With_Changes_And_Log()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode r = await OkResult(c, Req("run", new JsonObject
            {
                ["level"] = level,
                ["name"] = "blockout.lua",
                ["script"] = "local b = level.place_box(20, 2, 0, 6, 4, 6)\nb:set_air(true)\nlevel.place('Light', 20, 3, 0)\nprint('hi')\nreturn level.brush_count",
            }));

            Assert.True(r["success"]!.GetValue<bool>());
            Assert.True(r["committed"]!.GetValue<bool>());
            Assert.Equal("CLI: blockout.lua", r["undoLabel"]!.GetValue<string>());
            Assert.Equal(2, r["changes"]!["placed"]!.GetValue<int>());
            Assert.Equal("3", r["returnValue"]!.GetValue<string>());
            Assert.Contains(r["log"]!.AsArray(), l => l!["message"]!.GetValue<string>() == "hi" && l["level"]!.GetValue<string>() == "output");

            EditorDocument doc = host.Document!;
            Assert.Equal(1, doc.Undo.Position);
            Assert.Equal("CLI: blockout.lua", doc.Undo.Current.Description);
            Assert.True(doc.IsDirty);
            Assert.True(host.BuildState.GeometryDirty);
        }
    }

    [Fact]
    public async Task Dry_Run_Changes_Nothing_And_A_Failing_Script_Reports_Its_Line()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode dry = await OkResult(c, Req("run", new JsonObject
            {
                ["level"] = level, ["dryRun"] = true, ["script"] = "level.place_box(0,0,0,1,1,1)",
            }));
            Assert.True(dry["dryRun"]!.GetValue<bool>());
            Assert.False(dry["committed"]!.GetValue<bool>());
            Assert.Equal(1, dry["changeCount"]!.GetValue<int>());
            Assert.Equal(2, LevelOperations.Brushes(host.Document!).Count);
            Assert.False(host.Document!.IsDirty);

            AutomationResponse bad = await c.ExecuteAsync(Req("run", new JsonObject
            {
                ["name"] = "bad.lua", ["script"] = "local x = 1\nlocal y = nil\nprint(y.z)",
            }));
            Assert.False(bad.Ok);
            Assert.Equal(AutomationErrorCodes.ScriptFailed, bad.Error!.Code);
            JsonNode err = bad.Result!["error"]!;
            Assert.Equal(3, err["line"]!.GetValue<int>());
            Assert.Equal("bad.lua", err["chunk"]!.GetValue<string>());
            Assert.Equal(0, host.Document!.Undo.Position); // rolled back, no undo entry
        }
    }

    [Fact]
    public async Task Script_Save_Outside_The_Allowed_Folders_Is_Refused()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            string outside = other.File("stolen.rfl").Replace("\\", "/");
            AutomationResponse r = await c.ExecuteAsync(Req("run", new JsonObject
            {
                ["level"] = level, ["allowDestructive"] = true, ["script"] = $"ops.save('{outside}')",
            }));
            Assert.False(r.Ok);
            Assert.Contains("Refusing to save", r.Result!["error"]!["message"]!.GetValue<string>());
            Assert.False(File.Exists(other.File("stolen.rfl")));

            // Inside the level's folder is fine.
            string inside = dir.File("copy.rfl").Replace("\\", "/");
            JsonNode ok = await OkResult(c, Req("run", new JsonObject { ["allowDestructive"] = true, ["script"] = $"ops.save('{inside}')" }));
            Assert.True(ok["success"]!.GetValue<bool>());
            Assert.True(File.Exists(dir.File("copy.rfl")));
        }
    }

    [Fact]
    public async Task Save_Command_Enforces_The_Allow_List_And_Overwrite_Protection()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            AutomationResponse r = await c.ExecuteAsync(Req("save", new JsonObject { ["level"] = level, ["out"] = other.File("x.rfl") }));
            Assert.Equal(AutomationErrorCodes.PathNotAllowed, r.Error!.Code);

            File.WriteAllText(dir.File("existing.rfl"), "keep me");
            r = await c.ExecuteAsync(Req("save", new JsonObject { ["out"] = dir.File("existing.rfl") }));
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, r.Error!.Code);
            Assert.Equal("keep me", File.ReadAllText(dir.File("existing.rfl")));

            // An explicit command-line path may be overwritten.
            host.PathPolicy.AllowExplicit(other.File("x.rfl"));
            await OkResult(c, Req("save", new JsonObject { ["out"] = other.File("x.rfl") }));
            Assert.True(File.Exists(other.File("x.rfl")));

            r = await c.ExecuteAsync(Req("save", new JsonObject { ["out"] = dir.File("notalevel.txt") }));
            Assert.Equal(AutomationErrorCodes.Usage, r.Error!.Code);
        }
    }

    [Fact]
    public async Task Opening_Over_Unsaved_Changes_Needs_Allow_Destructive()
    {
        using var dir = new TempDir();
        string a = TestLevels.WriteRoomLevel(dir.File("a.rfl"));
        string b = TestLevels.WriteRoomLevel(dir.File("b.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            await OkResult(c, Req("run", new JsonObject { ["level"] = a, ["script"] = "level.place_box(0,0,0,1,1,1)" }));
            AutomationResponse r = await c.ExecuteAsync(Req("info", new JsonObject { ["level"] = b }));
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, r.Error!.Code);
            Assert.EndsWith("a.rfl", host.Document!.Path);

            await OkResult(c, Req("info", new JsonObject { ["level"] = b, ["allowDestructive"] = true }));
            Assert.EndsWith("b.rfl", host.Document!.Path);
        }
    }

    [Fact]
    public async Task Build_Holes_Lint_On_A_Sealed_Room()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode build = await OkResult(c, Req("build", new JsonObject { ["level"] = level, ["sealed"] = true }));
            Assert.True(build["sealed"]!.GetValue<bool>());
            Assert.Equal("Shared BSP", build["method"]!.GetValue<string>());
            Assert.True(build["rooms"]!.GetValue<int>() >= 1);
            Assert.True(build["faces"]!.GetValue<int>() > 0);
            Assert.False(host.BuildState.GeometryDirty);
            Assert.False(host.BuildState.GeometryIsPreview);

            JsonNode holes = await OkResult(c, Req("holes"));
            Assert.True(holes["watertight"]!.GetValue<bool>());
            Assert.Equal(0, holes["count"]!.GetValue<int>());

            JsonNode lint = await OkResult(c, Req("lint"));
            Assert.Equal(0, lint["errors"]!.GetValue<int>());
            Assert.Contains(lint["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("No RF install"));

            JsonNode info = await OkResult(c, Req("info"));
            Assert.Equal("built", info["geometry"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Holes_Reports_Positions_On_A_Leaky_Level()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteLeakyLevel(dir.File("leak.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode holes = await OkResult(c, Req("holes", new JsonObject { ["level"] = level }));
            Assert.False(holes["watertight"]!.GetValue<bool>());
            int count = holes["count"]!.GetValue<int>();
            Assert.True(count > 0);
            Assert.Equal(count, holes["positions"]!.AsArray().Count);
            Assert.Equal(3, holes["positions"]![0]!.AsArray().Count);
        }
    }

    [Theory]
    [InlineData("save")]
    [InlineData("run")]
    public async Task Saving_An_Unbuilt_Level_Builds_Its_Geometry_First(string command)
    {
        // A level with brushes and no compiled geometry crashes Red Faction on load: a command-line save
        // never writes one unless --no-build is given.
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            var args = new JsonObject { ["level"] = level };
            if (command == "run")
            {
                args["name"] = "add.lua";
                args["script"] = "level.place('Light', 0, 3, 0)";
                args["save"] = true;
            }

            JsonNode r = await OkResult(c, Req(command, args));
            JsonNode saved = command == "run" ? r["saved"]! : r;
            Assert.True(saved["built"]!.GetValue<bool>());
            Assert.Contains("lighting is not baked", saved["notice"]!.GetValue<string>());
            Assert.Null(saved["warning"]);
            Assert.True(Ged.Core.Compiler.CompiledGeometryState.HasCompiledGeometry(EditorDocument.Open(level).Rfl));
        }
    }

    [Fact]
    public async Task No_Build_Writes_The_Unbuilt_Level_With_A_Warning()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode saved = await OkResult(c, Req("save", new JsonObject { ["level"] = level, ["noBuild"] = true }));
            Assert.False(saved["built"]!.GetValue<bool>());
            Assert.Contains("crashes loading this level", saved["warning"]!.GetValue<string>());
            EditorDocument written = EditorDocument.Open(level);
            Assert.True(Ged.Core.Compiler.CompiledGeometryState.IsMissing(written.Rfl));
            Assert.Contains(Ged.Core.Linting.LevelLinter.Lint(written.Rfl).Findings,
                f => f.Category == Ged.Core.Linting.LintCategory.MissingGeometry && f.Severity == Ged.Core.Linting.LintSeverity.Error);
        }
    }
    [Fact]
    public async Task Save_After_A_Preview_Build_Reseals_Like_File_Save()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode build = await OkResult(c, Req("build", new JsonObject { ["level"] = level, ["save"] = true }));
            Assert.False(build["sealed"]!.GetValue<bool>());
            JsonNode saved = build["saved"]!;
            Assert.Contains("re-sealed", saved["notice"]!.GetValue<string>());
            Assert.True(saved["stamped"]!.GetValue<bool>());
            Assert.Equal(306, saved["rflVersion"]!.GetValue<int>());
            Assert.False(host.BuildState.GeometryIsPreview);

            // The written level carries sealed geometry identical to a direct sealed build.
            (HeadlessAutomationHost h2, AutomationCommands c2) = Session();
            using (h2)
            {
                h2.PathPolicy.AddRoot(dir.Path);
                string copy = TestLevels.WriteRoomLevel(dir.File("room2.rfl"));
                await OkResult(c2, Req("build", new JsonObject { ["level"] = copy, ["sealed"] = true }));
                Geometry written = LevelOperations.CompiledGeometry(EditorDocument.Open(level))!;
                Geometry direct = LevelOperations.CompiledGeometry(h2.Document!)!;
                Assert.Equal(direct.Faces.Count, written.Faces.Count);
                Assert.Equal(direct.Vertices.Count, written.Vertices.Count);
            }

            Assert.True(File.Exists(Path.Combine(dir.Path, "room.gedlayout.json"))); // sidecar written like the GUI
        }
    }

    [Fact]
    public async Task No_Op_Save_Of_An_Untouched_V305_File_Is_Byte_Identical()
    {
        using var dir = new TempDir();
        string level = TestLevels.WriteRoomLevel(dir.File("room.rfl"));

        // A built level (an unbuilt one is built by the save, which is a real change).
        (HeadlessAutomationHost builder, AutomationCommands b) = Session();
        using (builder)
        {
            builder.PathPolicy.AddRoot(dir.Path);
            await OkResult(b, Req("build", new JsonObject { ["level"] = level, ["sealed"] = true, ["save"] = true }));
        }

        byte[] before = File.ReadAllBytes(level);
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode saved = await OkResult(c, Req("save", new JsonObject { ["level"] = level }));
            Assert.False(saved["stamped"]!.GetValue<bool>());
            Assert.False(saved["saveAs"]!.GetValue<bool>());
        }

        Assert.Equal(before, File.ReadAllBytes(level));
    }

    [Fact]
    public async Task Save_Reports_Where_The_Level_Went_And_The_Version_Written()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            JsonNode fresh = await OkResult(c, Req("new"));
            Assert.Equal(306, fresh["rflVersion"]!.GetValue<int>()); // the File > New template's version (the save version)

            // The first save of a never-saved level binds it to a new file: a Save As, with no previous path.
            JsonNode first = await OkResult(c, Req("save", new JsonObject { ["out"] = dir.File("a.rfl") }));
            Assert.True(first["saveAs"]!.GetValue<bool>());
            Assert.Null(first["previousPath"]);
            Assert.Equal(306, first["rflVersion"]!.GetValue<int>()); // saves always write the current Alpine version
            Assert.Equal(dir.File("a.rfl"), host.Document!.Path);

            // Back to its own file: a plain save.
            JsonNode again = await OkResult(c, Req("save"));
            Assert.False(again["saveAs"]!.GetValue<bool>());
            Assert.Equal(dir.File("a.rfl"), again["previousPath"]!.GetValue<string>());

            // To another file: a Save As that names the file it came from.
            JsonNode other = await OkResult(c, Req("save", new JsonObject { ["out"] = dir.File("b.rfl") }));
            Assert.True(other["saveAs"]!.GetValue<bool>());
            Assert.Equal(dir.File("a.rfl"), other["previousPath"]!.GetValue<string>());
            Assert.Equal(dir.File("b.rfl"), host.Document.Path);
        }
    }

    [Fact]
    public async Task New_Writes_The_File_New_Template()
    {
        using var dir = new TempDir();
        (HeadlessAutomationHost host, AutomationCommands c) = Session();
        using (host)
        {
            host.PathPolicy.AddRoot(dir.Path);
            host.PathPolicy.AllowExplicit(dir.File("fresh.rfl"));
            JsonNode info = await OkResult(c, Req("new", new JsonObject { ["out"] = dir.File("fresh.rfl") }));
            Assert.Equal("fresh.rfl", info["name"]!.GetValue<string>());
            Assert.Equal(1, info["objects"]!.GetValue<int>()); // the template's Player Start
        }

        // Identical to File ▸ New → File ▸ Save of the same template (an untouched new level saves unstamped).
        EditorDocument gui = LevelTemplates.CreateNew(TestLevels.Fixed);
        Assert.False(gui.IsDirty);
        LevelSavePolicy.Write(gui, dir.File("gui.rfl"), saveAs: false, stampTimestamp: false, levelLighting: null);
        Assert.Equal(File.ReadAllBytes(dir.File("gui.rfl")), File.ReadAllBytes(dir.File("fresh.rfl")));
    }
}
