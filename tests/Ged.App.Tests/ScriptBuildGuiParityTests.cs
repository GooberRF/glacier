using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.App.Services;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Playtest;
using Ged.Core.Scripting;
using Ged.Scripting;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Inside the editor, a script's <c>ops.build</c> / <c>ops.light</c> goes
/// through the editor-supplied <see cref="AppScriptOperations"/> with the user's build settings, so it
/// produces exactly the bytes the GUI's own <see cref="GeometryBuildController"/> build does, and the
/// controller adopts the result (sealed, not dirty, brush-overlay stash rebuilt, viewport refreshed).
/// </summary>
public sealed class ScriptBuildGuiParityTests
{
    /// <summary>A from-scratch level (Core template) with an air room and a geoable detail block.</summary>
    private static string WriteLevel(string dir)
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 1, 2, 3, 4, 5));
        var ed = new BrushEditor(doc);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 12, Height = 8, Depth = 12, Air = true },
            new Vec3(0, 4, 0), Mat3.Identity);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Detail = true, Geoable = true },
            new Vec3(2, 1, 2), Mat3.Identity);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 6, 0));
        string path = Path.Combine(dir, "parity.rfl");
        File.WriteAllBytes(path, doc.SaveToBytes());
        return path;
    }

    private static (EditorSession Session, GeometryBuildController Controller) Open(string path, Action? refreshed = null)
    {
        var session = new EditorSession();
        session.OpenLevel(path);
        var controller = new GeometryBuildController(session, _ => { }, refreshed ?? (() => { }), (_, _) => { })
        {
            UseSharedBspBuild = true,
            Method = new Ged.Core.Lighting.LightingMethod(), // what MainWindow seeds from fresh settings
        };
        controller.Attach();
        return (session, controller);
    }

    private static ScriptRunResult RunScript(EditorSession session, GeometryBuildController controller, string lua,
        Action<PlaytestMode>? queuePlaytest = null) =>
        new ScriptRunner(new MoonSharpHost()).Run(
            new ScriptServices
            {
                Document = session.Document!,
                Brushes = session.BrushEditor,
                Confirmation = new AllowAllConfirmation(),
                BuildSettings = controller.CurrentBuildSettings,
                TextureTraits = controller.TextureTraits,
                OperationsFactory = ctx => new AppScriptOperations(ctx, controller, null, queuePlaytest),
            },
            lua,
            new ScriptRunOptions { ChunkName = "parity", AllowDestructive = true },
            new ScriptLog());

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Script_Build_In_The_Editor_Matches_The_Gui_Build(bool bake, bool sharedBsp)
    {
        string dir = Directory.CreateTempSubdirectory("ged-parity-").FullName;
        try
        {
            string path = WriteLevel(dir);

            // GUI: the toolbar Build / Calculate Maps and Light path.
            (EditorSession guiSession, GeometryBuildController gui) = Open(path);
            gui.UseSharedBspBuild = sharedBsp;
            if (bake)
            {
                await gui.CalculateMapsAndLightAsync(shadows: true);
            }
            else
            {
                await gui.BuildAsync();
            }

            // Script: ops.build() / ops.light() through the editor-supplied operations.
            int refreshes = 0;
            (EditorSession scriptSession, GeometryBuildController scripted) = Open(path, () => refreshes++);
            scripted.UseSharedBspBuild = sharedBsp;
            ScriptRunResult r = RunScript(scriptSession, scripted, bake ? "ops.light(true)" : "ops.build()");
            Assert.True(r.Success, r.Error?.ToDisplayString());

            Assert.Equal(guiSession.Document!.SaveToBytes(), scriptSession.Document!.SaveToBytes());

            // The editor adopted the script's sealed build exactly like its own.
            Assert.False(scripted.GeometryDirty);
            Assert.False(scripted.GeometryIsPreview);
            Assert.NotNull(scriptSession.BrushFaceSurvival);
            Assert.True(refreshes > 0, "the viewport was not refreshed after the script build");
            if (bake)
            {
                Assert.False(scripted.LightingDirty);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Script_Playtest_Is_Queued_For_After_The_Run_Not_Rejected()
    {
        string dir = Directory.CreateTempSubdirectory("ged-parity-").FullName;
        try
        {
            (EditorSession session, GeometryBuildController controller) = Open(WriteLevel(dir));
            PlaytestMode? queued = null;
            ScriptRunResult r = RunScript(session, controller, "ops.playtest(true)", m => queued = m);
            Assert.True(r.Success, r.Error?.ToDisplayString());
            Assert.Equal(PlaytestMode.Multi, queued);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Headless_Playtest_Still_Reports_It_Needs_The_Editor()
    {
        EditorDocument doc = LevelTemplates.CreateNew();
        ScriptRunResult r = new ScriptRunner(new MoonSharpHost()).Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation() },
            "ops.playtest()",
            new ScriptRunOptions { ChunkName = "headless", AllowDestructive = true },
            new ScriptLog());
        Assert.False(r.Success);
        Assert.Contains("interactive editor", r.Error!.Message);
    }
}
