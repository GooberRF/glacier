using Ged.Core.Editor;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// A run started from outside the editor is one undo step with an explicit label
/// ("CLI: &lt;script&gt;"), even when it contains a single command, and every run reports what it
/// changed by kind.
/// </summary>
public sealed class ScriptUndoLabelTests
{
    private static ScriptRunResult Run(EditorDocument doc, string lua, string? label, bool dryRun = false) =>
        ScriptTestFixture.Runner().Run(
            ScriptTestFixture.Services(doc),
            lua,
            new ScriptRunOptions { ChunkName = "gen.lua", UndoLabel = label, DryRun = dryRun },
            new ScriptLog());

    [Fact]
    public void Explicit_Label_Names_Even_A_Single_Command_Run()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        ScriptRunResult r = Run(doc, "level.place_box(0, 0, 0, 2, 2, 2)", "CLI: gen.lua");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.Equal("CLI: gen.lua", doc.Undo.Current.Description);
        Assert.Equal(1, r.Changes["placed"]);
        Assert.Equal(1, r.ChangeCount);

        doc.Undo.Undo();
        Assert.Equal(0, doc.Undo.Position);
    }

    [Fact]
    public void Default_Labelling_Is_Unchanged_And_Multi_Command_Runs_Use_The_Label()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        ScriptRunResult single = Run(doc, "level.place_box(0, 0, 0, 2, 2, 2)", label: null);
        Assert.True(single.Success);
        Assert.NotEqual("CLI: gen.lua", doc.Undo.Current.Description); // one-command runs keep the command's name

        ScriptRunResult multi = Run(doc, "level.place_box(0, 0, 0, 2, 2, 2)\nlevel.place_box(5, 0, 0, 2, 2, 2)", "CLI: gen.lua");
        Assert.True(multi.Success);
        Assert.Equal("CLI: gen.lua", doc.Undo.Current.Description);
        Assert.Equal(2, multi.Changes["placed"]);
    }

    [Fact]
    public void Dry_Run_Reports_Would_Be_Changes_Without_An_Undo_Step()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0);
        ScriptRunResult r = Run(doc, "level.place_box(0, 0, 0, 2, 2, 2)", "CLI: gen.lua", dryRun: true);
        Assert.True(r.Success);
        Assert.Equal(1, r.ChangeCount);
        Assert.Equal(0, doc.Undo.Position);
    }
}
