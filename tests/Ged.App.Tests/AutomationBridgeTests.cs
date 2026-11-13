using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Services;
using Ged.Automation;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Attach mode + headless host: the GUI save path and the headless
/// automation save write the same bytes; the editor's automation host runs command-line requests
/// against the live session through the editor's own services (Script Log, build controller, undo);
/// and the bridge setting defaults on and round-trips through the headless settings reader.
/// </summary>
public sealed class AutomationBridgeTests
{
    private static string WriteLevel(string path)
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 1, 2, 3, 4, 5));
        var ed = new BrushEditor(doc);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 12, Height = 6, Depth = 12, Air = true },
            new Vec3(0, 3, 0), Mat3.Identity);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Detail = true, Geoable = true },
            new Vec3(2, 1, 2), Mat3.Identity);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 5, 0));
        doc.Save(path, updateTimestamp: false);
        return path;
    }

    private static (EditorSession Session, GeometryBuildController Controller) OpenGui(string path)
    {
        var session = new EditorSession();
        session.OpenLevel(path);
        var controller = new GeometryBuildController(session, _ => { }, () => { }, (_, _) => { })
        {
            UseSharedBspBuild = true,
            Method = new Ged.Core.Lighting.LightingMethod(), // what MainWindow seeds from fresh settings
        };
        controller.Attach();
        return (session, controller);
    }

    /// <summary>The bytes with the save-time stamps (header timestamp, level_info date) normalized.</summary>
    private static byte[] Normalized(string path)
    {
        RflFile rfl = RflFile.Load(File.ReadAllBytes(path));
        rfl.ParseAllKnownSections();
        rfl.Header.Timestamp = 0;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is LevelInfoSection info)
            {
                info.Date = "normalized";
                s.Dirty = true;
            }
        }

        return rfl.Save(updateTimestamp: false);
    }

    [AvaloniaFact]
    public async Task Gui_Save_And_Headless_Save_Write_Identical_Bytes_After_A_Preview_Build()
    {
        string dir = Directory.CreateTempSubdirectory("ged-saveparity-").FullName;
        try
        {
            string gui = WriteLevel(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "gui")).FullName, "level.rfl"));
            string cli = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "cli")).FullName, "level.rfl");
            File.Copy(gui, cli);

            // GUI: the live-CSG preview build, then File ▸ Save (the seal guard re-seals before writing).
            (EditorSession session, GeometryBuildController controller) = OpenGui(gui);
            await controller.BuildAsync(interactive: false);
            Assert.True(controller.GeometryIsPreview);
            EditorSaveResult guiResult = await EditorSaveFlow.SaveAsync(session.Document!, controller, gui, saveAs: false, levelLighting: null);
            Assert.True(guiResult.Written);
            Assert.Equal(LevelSaveNotice.GeometryResealed, guiResult.Notice);

            // Headless: glacier-cli build (preview) --save.
            using (var host = new HeadlessAutomationHost())
            {
                host.PathPolicy.AllowExplicit(cli);
                AutomationResponse r = await new AutomationCommands(host).ExecuteAsync(
                    new AutomationRequest("build", new JsonObject { ["level"] = cli, ["save"] = true }));
                Assert.True(r.Ok, r.Error?.Message);
                Assert.Contains("re-sealed", r.Result!["saved"]!["notice"]!.GetValue<string>());
            }

            Assert.Equal(Normalized(gui), Normalized(cli));
            Assert.Equal(
                File.ReadAllText(LevelSidecarStore.SidecarPathFor(gui)),
                File.ReadAllText(LevelSidecarStore.SidecarPathFor(cli)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Gui_And_Headless_No_Op_Saves_Are_Both_Byte_Identical_To_The_Source()
    {
        string dir = Directory.CreateTempSubdirectory("ged-saveparity-").FullName;
        try
        {
            string gui = WriteLevel(Path.Combine(dir, "gui.rfl"));
            string cli = Path.Combine(dir, "cli.rfl");
            File.Copy(gui, cli);
            byte[] source = File.ReadAllBytes(gui);

            (EditorSession session, GeometryBuildController controller) = OpenGui(gui);
            EditorSaveResult g = await EditorSaveFlow.SaveAsync(session.Document!, controller, gui, saveAs: false, levelLighting: null);
            Assert.True(g.Written);
            Assert.Equal(LevelSaveNotice.None, g.Notice);

            using (var host = new HeadlessAutomationHost())
            {
                host.PathPolicy.AllowExplicit(cli);
                // The fixture has brushes but was never built: without --no-build the command line builds it
                // before saving (the game cannot load it otherwise); --no-build keeps the GUI's write-as-is.
                AutomationResponse r = await new AutomationCommands(host).ExecuteAsync(
                    new AutomationRequest("save", new JsonObject { ["level"] = cli, ["noBuild"] = true }));
                Assert.True(r.Ok, r.Error?.Message);
                Assert.False(r.Result!["stamped"]!.GetValue<bool>());
            }

            Assert.Equal(source, File.ReadAllBytes(gui));
            Assert.Equal(source, File.ReadAllBytes(cli));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the editor host --------------------------------------------------------------------

    /// <summary>A window-less stand-in for MainWindow's shell side, over a real session + controller.</summary>
    private sealed class FakeShell : IEditorAutomationShell, IScriptEnvironment
    {
        public FakeShell(EditorSession session, GeometryBuildController controller)
        {
            Session = session;
            BuildController = controller;
            Scripting = new ScriptingService(this, new OperationProgressService());
        }

        public EditorSession Session { get; }

        public GeometryBuildController? BuildController { get; }

        public ScriptingService Scripting { get; }

        public List<string> Activity { get; } = new();

        public int ExternalChanges { get; private set; }

        public IReadOnlyList<Vec3>? Markers { get; private set; }

        public Task OpenLevelAsync(string path)
        {
            Session.OpenLevel(path);
            BuildController!.Attach();
            return Task.CompletedTask;
        }

        public Task NewLevelAsync()
        {
            Session.NewLevel();
            BuildController!.Attach();
            return Task.CompletedTask;
        }

        public Task<EditorSaveResult> SaveToPathAsync(string path, bool saveAs) =>
            EditorSaveFlow.SaveAsync(Session.Document!, BuildController, path, saveAs, levelLighting: null);

        public void ShowHoleMarkers(IReadOnlyList<Vec3> holes) => Markers = holes;

        public void AfterExternalChange() => ExternalChanges++;

        public void LogActivity(string message) => Activity.Add(message);

        ScriptServices? IScriptEnvironment.BuildServices(IScriptProgressSink progress, IScriptConfirmation confirmation) =>
            Session.Document is null ? null : new ScriptServices
            {
                Document = Session.Document,
                Brushes = Session.BrushEditor,
                Assets = Session.Vfs,
                Progress = progress,
                Confirmation = confirmation,
                BuildSettings = BuildController!.CurrentBuildSettings,
                TextureTraits = BuildController.TextureTraits,
                OperationsFactory = ctx => new AppScriptOperations(ctx, BuildController, null, null),
            };

        void IScriptEnvironment.OnScriptApplied()
        {
        }
    }

    [AvaloniaFact]
    public async Task Editor_Host_Runs_Scripts_Through_The_Script_Log_As_One_Labelled_Undo_Step()
    {
        string dir = Directory.CreateTempSubdirectory("ged-edhost-").FullName;
        try
        {
            string level = WriteLevel(Path.Combine(dir, "level.rfl"));
            (EditorSession session, GeometryBuildController controller) = OpenGui(level);
            var shell = new FakeShell(session, controller);
            var scriptLog = new List<ScriptLogEntry>();
            shell.Scripting.LogWritten += scriptLog.Add;
            var host = new EditorAutomationHost(shell);
            var commands = new AutomationCommands(host);

            AutomationResponse run = await commands.ExecuteAsync(new AutomationRequest("run", new JsonObject
            {
                ["name"] = "blockout.lua",
                ["script"] = "level.place_box(20, 2, 0, 4, 4, 4):set_air(true)\nprint('from cli')",
            }));
            Assert.True(run.Ok, run.Error?.Message);
            Assert.Equal("CLI: blockout.lua", session.Document!.Undo.Current.Description);
            Assert.Equal(1, session.Document.Undo.Position);
            Assert.Contains(scriptLog, e => e.Message == "from cli"); // shown in the editor's Script Log
            Assert.Contains(run.Result!["log"]!.AsArray(), l => l!["message"]!.GetValue<string>() == "from cli");
            Assert.Equal(1, shell.ExternalChanges);
            Assert.Contains(shell.Activity, a => a.StartsWith("CLI: run blockout.lua", StringComparison.Ordinal));

            // A preview build is adopted as preview (Save / holes re-seal it); a sealed build clears it.
            AutomationResponse preview = await commands.ExecuteAsync(new AutomationRequest("build"));
            Assert.True(preview.Ok, preview.Error?.Message);
            Assert.True(controller.GeometryIsPreview);
            Assert.False(controller.GeometryDirty);
            Assert.NotNull(session.BrushFaceSurvival);

            AutomationResponse sealedBuild = await commands.ExecuteAsync(new AutomationRequest("build", new JsonObject { ["sealed"] = true }));
            Assert.True(sealedBuild.Ok, sealedBuild.Error?.Message);
            Assert.False(controller.GeometryIsPreview);

            AutomationResponse holes = await commands.ExecuteAsync(new AutomationRequest("holes"));
            Assert.True(holes.Ok, holes.Error?.Message);
            Assert.NotNull(shell.Markers);
            Assert.Equal(holes.Result!["count"]!.GetValue<int>(), controller.HoleLocations.Count);

            // Save to its own file goes through the editor's save sequence.
            AutomationResponse save = await commands.ExecuteAsync(new AutomationRequest("save"));
            Assert.True(save.Ok, save.Error?.Message);
            Assert.False(session.Document.IsDirty);

            // Paths outside the allow-list are refused for bridge requests.
            string outside = Path.Combine(Path.GetTempPath(), "ged-not-allowed-" + Guid.NewGuid().ToString("N") + ".rfl");
            AutomationResponse bad = await commands.ExecuteAsync(new AutomationRequest("save", new JsonObject { ["out"] = outside }));
            Assert.Equal(AutomationErrorCodes.PathNotAllowed, bad.Error!.Code);
            Assert.False(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Editor_Host_Undo_Takes_Back_Command_Line_Runs_But_Never_The_Designers_Edits()
    {
        string dir = Directory.CreateTempSubdirectory("ged-edhost-").FullName;
        try
        {
            string level = WriteLevel(Path.Combine(dir, "level.rfl"));
            (EditorSession session, GeometryBuildController controller) = OpenGui(level);
            var shell = new FakeShell(session, controller);
            var commands = new AutomationCommands(new EditorAutomationHost(shell));
            EditorDocument doc = session.Document!;
            int brushes = session.BrushEditor!.Brushes.Count;

            doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 3, 0)); // the designer's edit
            AutomationResponse run = await commands.ExecuteAsync(new AutomationRequest("run", new JsonObject
            {
                ["name"] = "box.lua",
                ["script"] = "level.place_box(20, 2, 0, 4, 4, 4):set_air(true)",
            }));
            Assert.True(run.Ok, run.Error?.Message);
            Assert.Equal(brushes + 1, session.BrushEditor!.Brushes.Count);
            int changes = shell.ExternalChanges;

            AutomationResponse two = await commands.ExecuteAsync(new AutomationRequest("undo", new JsonObject { ["steps"] = 2 }));
            Assert.Equal(AutomationErrorCodes.UndoRefused, two.Error!.Code);
            Assert.Equal(brushes + 1, session.BrushEditor!.Brushes.Count);

            AutomationResponse undo = await commands.ExecuteAsync(new AutomationRequest("undo"));
            Assert.True(undo.Ok, undo.Error?.Message);
            Assert.Equal("CLI: box.lua", undo.Result!["undone"]![0]!.GetValue<string>());
            Assert.Equal(brushes, session.BrushEditor!.Brushes.Count);
            Assert.True(shell.ExternalChanges > changes); // panels refreshed

            AutomationResponse again = await commands.ExecuteAsync(new AutomationRequest("undo"));
            Assert.Equal(AutomationErrorCodes.UndoRefused, again.Error!.Code);
            Assert.Contains(doc.Objects, o => o.Kind == LevelObjectKind.Light && o.Position.Y == 3f);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Editor_Host_Refuses_To_Discard_Unsaved_Work_Without_Allow_Destructive()
    {
        string dir = Directory.CreateTempSubdirectory("ged-edhost-").FullName;
        try
        {
            string level = WriteLevel(Path.Combine(dir, "level.rfl"));
            (EditorSession session, GeometryBuildController controller) = OpenGui(level);
            var commands = new AutomationCommands(new EditorAutomationHost(new FakeShell(session, controller)));
            await commands.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["script"] = "level.place_box(0, 9, 0, 1, 1, 1)" }));
            Assert.True(session.Document!.IsDirty);

            AutomationResponse r = await commands.ExecuteAsync(new AutomationRequest("new"));
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, r.Error!.Code);
            Assert.Equal(level, session.Document.Path);

            r = await commands.ExecuteAsync(new AutomationRequest("new", new JsonObject { ["allowDestructive"] = true }));
            Assert.True(r.Ok, r.Error?.Message);
            Assert.Null(session.Document!.Path); // the new, unsaved level replaced it
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bridge_Setting_Defaults_On_And_Round_Trips_Through_The_Headless_Reader()
    {
        Assert.True(new AppSettings().EnableAutomationBridge);

        string path = Path.Combine(Path.GetTempPath(), "ged-settings-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            var s = new AppSettings
            {
                RfInstallDir = "Z:\\Games\\RF",
                UseSharedBspBuild = false,
                EnableAutomationBridge = false,
                LightingMethodBase = 1,
                LightingMethodBounces = 2,
                LightingAmbientOcclusion = true,
                LightingSoftShadows = true,
                LightingHighRes = true,
                LightingSeamBlend = true,
                LightingCornerLeakFix = true,
                LightingSmoothGutters = true,
                LightingMoverShadows = false,
                DefaultFloorTexture = "floor.tga",
                DefaultWallTexture = "wall.tga",
                DefaultCeilingTexture = "ceil.tga",
            };
            SettingsStore.Save(s, path);
            Assert.True(SettingsStore.Load(path).EnableAutomationBridge == false);

            // The headless reader parses the editor's own file with the same field names.
            EditorSettingsSnapshot r = EditorSettingsReader.Read(path);
            Assert.Equal(s.RfInstallDir, r.RfInstallDir);
            Assert.False(r.UseSharedBspBuild);
            Assert.False(r.EnableAutomationBridge);
            Assert.Equal(Ged.Core.Lighting.LightingBase.Bounced, r.LightingMethod.Base);
            Assert.Equal(2, r.LightingMethod.Bounces);
            Assert.True(r.LightingMethod.AmbientOcclusion && r.LightingMethod.SoftShadows && r.LightingMethod.HighResLightmaps
                && r.LightingMethod.SeamBlend && r.LightingMethod.CornerLeakFix && r.LightingMethod.SmoothGutters);
            Assert.False(r.LightingMethod.MoverShadows);
            Assert.Equal("floor.tga", r.DefaultFloorTexture);
            Assert.Equal("wall.tga", r.DefaultWallTexture);
            Assert.Equal("ceil.tga", r.DefaultCeilingTexture);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Editor_Bridge_Serves_The_Editor_Host_Over_The_Pipe()
    {
        string dir = Directory.CreateTempSubdirectory("ged-edbridge-").FullName;
        try
        {
            string level = WriteLevel(Path.Combine(dir, "level.rfl"));
            (EditorSession session, GeometryBuildController controller) = OpenGui(level);
            var lines = new List<string>();
            string pipe = "glacier-test-" + Guid.NewGuid().ToString("N");
            await using var bridge = new EditorBridge(new EditorAutomationHost(new FakeShell(session, controller)), l => { lock (lines) { lines.Add(l); } }, pipe);
            Assert.True(bridge.Start());

            // A second editor window on the same name runs without a bridge and says so once.
            var second = new List<string>();
            await using var other = new EditorBridge(new EditorAutomationHost(new FakeShell(session, controller)), second.Add, pipe);
            Assert.False(other.Start());
            Assert.Single(second);

            // Round trip from a worker thread; the editor host marshals onto the UI thread.
            AutomationResponse hello = await Task.Run(async () =>
            {
                using BridgeClient c = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
                return await c.SendAsync(new AutomationRequest("hello"));
            });
            Assert.True(hello.Ok);
            Assert.Equal("editor", hello.Result!["mode"]!.GetValue<string>());
            Assert.Equal(level, hello.Result!["document"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Destructive_AI_Setting_Defaults_Off_And_Persists()
    {
        Assert.False(new AppSettings().AllowDestructiveAutomation);
        string path = Path.Combine(Path.GetTempPath(), "ged-settings-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            SettingsStore.Save(new AppSettings { AllowDestructiveAutomation = true }, path);
            Assert.True(SettingsStore.Load(path).AllowDestructiveAutomation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Editor_Bridge_Lets_AI_Requests_Discard_Work_Only_With_The_Destructive_Setting_On()
    {
        string dir = Directory.CreateTempSubdirectory("ged-edbridge-").FullName;
        try
        {
            string level = WriteLevel(Path.Combine(dir, "level.rfl"));
            (EditorSession session, GeometryBuildController controller) = OpenGui(level);
            bool setting = false;
            await using var bridge = new EditorBridge(new EditorAutomationHost(new FakeShell(session, controller)), _ => { },
                "glacier-test-" + Guid.NewGuid().ToString("N"), () => setting);

            async Task MakeDirtyAsync()
            {
                AutomationResponse run = await bridge.ExecuteAsync(new AutomationRequest("run", new JsonObject { ["script"] = "level.place_box(0, 9, 0, 1, 1, 1)" }));
                Assert.True(run.Ok, run.Error?.Message);
                Assert.True(session.Document!.IsDirty);
            }

            AutomationRequest AiNew() => new("new", new JsonObject { ["origin"] = "mcp", ["allowDestructive"] = true });

            // Setting off: an AI agent's request to discard is refused even though it asks.
            await MakeDirtyAsync();
            AutomationResponse refused = await bridge.ExecuteAsync(AiNew());
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, refused.Error!.Code);
            Assert.Equal(level, session.Document!.Path);
            Assert.False((await bridge.ExecuteAsync(new AutomationRequest("hello"))).Result!["destructiveAutomation"]!.GetValue<bool>());

            // A person's own --allow-destructive on the command line is not the AI setting's business.
            AutomationResponse cli = await bridge.ExecuteAsync(new AutomationRequest("new", new JsonObject { ["allowDestructive"] = true }));
            Assert.True(cli.Ok, cli.Error?.Message);
            Assert.Null(session.Document!.Path);

            // Setting on: the AI request goes through (the server's own flag put allowDestructive there).
            setting = true;
            await MakeDirtyAsync();
            Assert.True((await bridge.ExecuteAsync(new AutomationRequest("hello"))).Result!["destructiveAutomation"]!.GetValue<bool>());
            AutomationResponse allowed = await bridge.ExecuteAsync(AiNew());
            Assert.True(allowed.Ok, allowed.Error?.Message);
            Assert.False(session.Document!.IsDirty);

            // Without allowDestructive in the request, the setting alone allows nothing.
            await MakeDirtyAsync();
            AutomationResponse notAsked = await bridge.ExecuteAsync(new AutomationRequest("new", new JsonObject { ["origin"] = "mcp" }));
            Assert.Equal(AutomationErrorCodes.DestructiveNotAllowed, notAsked.Error!.Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Editor_Bridge_Streams_Build_Progress_Over_The_Pipe_Without_Blocking_The_UI_Thread()
    {
        string dir = Directory.CreateTempSubdirectory("ged-edbridge-").FullName;
        try
        {
            string level = WriteLevel(Path.Combine(dir, "level.rfl"));
            (EditorSession session, GeometryBuildController controller) = OpenGui(level);
            string pipe = "glacier-test-" + Guid.NewGuid().ToString("N");
            await using var bridge = new EditorBridge(new EditorAutomationHost(new FakeShell(session, controller)), _ => { }, pipe);
            Assert.True(bridge.Start());

            // The UI thread keeps turning while the editor builds and streams progress.
            int uiTicks = 0;
            var ticker = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(5), Avalonia.Threading.DispatcherPriority.Normal, (_, _) => uiTicks++);
            ticker.Start();
            var stages = new List<string>();
            (AutomationResponse hello, AutomationResponse build) = await Task.Run(async () =>
            {
                using BridgeClient c = (await BridgeClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5)))!;
                AutomationResponse h = await c.HelloAsync();
                AutomationResponse b = await c.SendAsync(new AutomationRequest("build", new JsonObject { ["sealed"] = true }),
                    new Progress(p => { lock (stages) { stages.Add(p.Stage); } }), CancellationToken.None);
                return (h, b);
            });
            ticker.Stop();

            Assert.Contains("progress", hello.Result!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.True(build.Ok, build.Error?.Message);
            Assert.False(controller.GeometryIsPreview);
            lock (stages)
            {
                Assert.NotEmpty(stages);
            }

            Assert.True(uiTicks > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A synchronous progress sink (no captured context).</summary>
    private sealed class Progress : IProgress<AutomationProgress>
    {
        private readonly Action<AutomationProgress> _report;

        public Progress(Action<AutomationProgress> report) => _report = report;

        public void Report(AutomationProgress value) => _report(value);
    }
}
