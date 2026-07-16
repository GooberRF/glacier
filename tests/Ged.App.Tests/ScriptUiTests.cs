using System;
using Avalonia.Headless.XUnit;
using Ged.App.Dialogs;
using Ged.App.Panels;
using Ged.App.Services;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Headless UI coverage for the scripting shell (plan §6): the console evaluates through the
/// service, the console panel builds, and — the key AvaloniaEdit integration check — the Script
/// Editor window opens with its Lua syntax highlighting + theme include loaded, without throwing.
/// </summary>
public class ScriptUiTests
{
    private sealed class FakeEnv : IScriptEnvironment
    {
        private readonly EditorDocument _doc;
        public FakeEnv(EditorDocument doc) => _doc = doc;
        public int Applied { get; private set; }

        public ScriptServices? BuildServices(IScriptProgressSink progress, IScriptConfirmation confirmation) =>
            new() { Document = _doc, Progress = progress, Confirmation = confirmation };

        public void OnScriptApplied() => Applied++;
    }

    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "test.rfl";
        var es = new EntitiesSection();
        es.Entities.Add(new Entity { Uid = 1, ClassName = "Guard", ScriptName = "e1", Position = new Vec3(0, 0, 0) });
        es.Entities.Add(new Entity { Uid = 2, ClassName = "Guard", ScriptName = "e2", Position = new Vec3(1, 0, 0) });
        rfl.Sections.Add(new RflSection((uint)SectionType.Entities, Array.Empty<byte>()) { Content = es });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl, "test.rfl");
    }

    private static ScriptingService NewService(out FakeEnv env)
    {
        env = new FakeEnv(NewDoc());
        return new ScriptingService(env, new OperationProgressService());
    }

    [AvaloniaFact]
    public void Console_Evaluates_A_Query()
    {
        ScriptingService svc = NewService(out _);
        ScriptRunResult r = svc.EvalConsole("return 40 + 2", default);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("42", r.ReturnValue);
    }

    [AvaloniaFact]
    public void Console_Mutation_Applies_And_Refreshes()
    {
        ScriptingService svc = NewService(out FakeEnv env);
        ScriptRunResult r = svc.EvalConsole("level.place('Light', 0, 0, 0)", default);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.True(r.Committed);
        Assert.True(env.Applied >= 1);
    }

    [AvaloniaFact]
    public void Console_Panel_Builds()
    {
        ScriptingService svc = NewService(out _);
        var panel = new ScriptConsolePanel(svc, () => { });
        Assert.NotNull(panel);
    }

    [AvaloniaFact]
    public void Script_Editor_Opens_With_Highlighting()
    {
        ScriptingService svc = NewService(out _);
        var win = new ScriptEditorWindow(svc);
        win.Show();   // exercises AvaloniaEdit + the Lua xshd + the theme StyleInclude
        win.Close();
    }
}
