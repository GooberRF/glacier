using System;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// End-to-end scripting-engine tests: read/query APIs, the load-bearing undo-integrity + rollback
/// invariants (plan §5.2), dry-run (§5.7), determinism (§5.7), the sandbox (§5.10), and the error
/// model (§5.6). Driven through the real MoonSharp host.
/// </summary>
public sealed class ScriptEngineTests
{
    // ---- Read / query --------------------------------------------------------

    [Fact]
    public void Query_Reads_Level_State_Without_Undo_Entry()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);
        int before = doc.Undo.NodeCount;

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "return level.count");

        Assert.True(r.Success);
        Assert.Equal("3", r.ReturnValue);
        Assert.Equal(0, r.UndoNodesAdded);
        Assert.Equal(before, doc.Undo.NodeCount);
    }

    [Fact]
    public void Find_Uid_And_Object_Fields_Bind()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "local o = level.find_uid(2) return o.kind .. ':' .. o.name .. ':' .. tostring(o.x)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("Entity:e2:1", r.ReturnValue);
    }

    // ---- Undo integrity (one run = one undo step) ----------------------------

    [Fact]
    public void Mutation_Is_Exactly_One_Undo_Node_And_Reverts()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "level.place('Light', 5, 6, 7)");

        Assert.True(r.Success);
        Assert.True(r.Committed);
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.Equal(4, doc.Objects.Count);

        doc.Undo.Undo();
        Assert.Equal(3, doc.Objects.Count); // one Ctrl+Z reverts the whole run
    }

    [Fact]
    public void Batch_Of_Many_Edits_Collapses_To_One_Undo_Node()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "for i = 1, 20 do level.place('Light', i, 0, 0) end");

        Assert.True(r.Success);
        Assert.Equal(1, r.UndoNodesAdded); // 20 placements → ONE undo node
        Assert.Equal(23, doc.Objects.Count);

        doc.Undo.Undo();
        Assert.Equal(3, doc.Objects.Count);
    }

    // ---- Rollback on error (thrown script leaves the document untouched) ------

    [Fact]
    public void Error_Midscript_Rolls_Back_Everything()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "level.place('Light', 0, 0, 0)\nlevel.place('Light', 1, 0, 0)\nerror('boom')");

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Runtime, r.Error!.Kind);
        Assert.Equal(3, doc.Objects.Count);   // both placements rolled back
        Assert.Equal(0, r.UndoNodesAdded);
        Assert.False(doc.Undo.CanUndo);        // nothing left on the stack
    }

    // ---- Dry-run (always rolled back; reports what would change) --------------

    [Fact]
    public void DryRun_Reports_Changes_But_Applies_Nothing()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);

        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc,
            "level.place('Light', 0, 0, 0) level.place('Light', 1, 0, 0)", dryRun: true);

        Assert.True(r.Success);
        Assert.True(r.WasDryRun);
        Assert.False(r.Committed);
        Assert.Equal(3, doc.Objects.Count); // nothing applied
        Assert.Contains(log.Entries, e => e.Message.Contains("Dry-run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DryRun_Disables_Destructive_Delete()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "level.find_uid(1):delete()", dryRun: true);

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Equal(3, doc.Objects.Count);
    }

    // ---- Determinism (seeded RNG reproducible) -------------------------------

    [Fact]
    public void Seeded_Rng_Is_Reproducible()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);
        const string src = "local s='' for i=1,5 do s = s .. rng.int(1,1000000) .. ',' end return s";

        (ScriptRunResult a, _) = ScriptTestFixture.Run(doc, src, seed: 42);
        (ScriptRunResult b, _) = ScriptTestFixture.Run(doc, src, seed: 42);
        (ScriptRunResult c, _) = ScriptTestFixture.Run(doc, src, seed: 7);

        Assert.Equal(a.ReturnValue, b.ReturnValue);
        Assert.NotEqual(a.ReturnValue, c.ReturnValue);
    }

    // ---- Sandbox (io / os.execute denied) ------------------------------------

    [Fact]
    public void Sandbox_Denies_Io_And_Process()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);

        (ScriptRunResult io, _) = ScriptTestFixture.Run(doc, "return tostring(io)");
        Assert.True(io.Success);
        Assert.Equal("nil", io.ReturnValue); // no io module at all

        // The hard sandbox strips the entire os table (also withholds os.time nondeterminism, §5.7).
        (ScriptRunResult os, _) = ScriptTestFixture.Run(doc, "return tostring(os)");
        Assert.True(os.Success);
        Assert.Equal("nil", os.ReturnValue);

        (ScriptRunResult call, _) = ScriptTestFixture.Run(doc, "os.execute('calc')");
        Assert.False(call.Success); // indexing/calling the missing module throws
    }

    // ---- Execution limits (runaway loop is interrupted) ----------------------

    [Fact]
    public void Runaway_Loop_Is_Aborted_By_Budget()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            ScriptTestFixture.Services(doc),
            "while true do end",
            new ScriptRunOptions
            {
                ChunkName = "loop",
                Limits = new ScriptExecutionLimits { InstructionBudget = 2_000_000, YieldEvery = 10_000, Timeout = TimeSpan.FromSeconds(10) },
            },
            log);

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Aborted, r.Error!.Kind);
    }

    // ---- Error model (source coordinates) ------------------------------------

    [Fact]
    public void Runtime_Error_Carries_Line_Number()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local t = nil\nreturn t.x");

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Runtime, r.Error!.Kind);
        Assert.Equal(2, r.Error.Line);
        Assert.Contains("nil", r.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Syntax_Error_Is_Classified()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc, "local x = = 3");

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Syntax, r.Error!.Kind);
    }

    [Fact]
    public void Api_Version_Guard_Refuses_Future_Major()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            ScriptTestFixture.Services(doc),
            "return 1",
            new ScriptRunOptions { ChunkName = "t", DeclaredApiVersion = ScriptApiV1.Version + 1 },
            log);

        Assert.False(r.Success);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
    }

    // ---- Bulk ops (vectorized apply, one undo node) --------------------------

    [Fact]
    public void Replace_Texture_Is_Bulk_And_One_Undo_Node()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 0, boxes: 3);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "return assets.replace_texture('metal01.tga', 'rock.tga')");

        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("18", r.ReturnValue); // 3 boxes × 6 faces
        Assert.Equal(1, r.UndoNodesAdded);

        (ScriptRunResult after, _) = ScriptTestFixture.Run(doc, "return assets.replace_texture('metal01.tga', 'rock.tga')");
        Assert.Equal("0", after.ReturnValue); // none left on the old texture
    }

    [Fact]
    public void Selection_Where_Then_Bulk_Move_Is_One_Undo_Node()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 3);

        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "selection.where(function(o) return o.kind == 'Entity' end):move(0, 10, 0)");

        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal(1, r.UndoNodesAdded);
        Assert.All(doc.Objects, o => Assert.Equal(10f, o.Position.Y));

        doc.Undo.Undo();
        Assert.All(doc.Objects, o => Assert.Equal(0f, o.Position.Y));
    }

    // ---- ged meta ------------------------------------------------------------

    [Fact]
    public void Ged_Api_Version_And_Group_Work()
    {
        EditorDocument doc = ScriptTestFixture.NewDoc(entities: 1);
        (ScriptRunResult r, _) = ScriptTestFixture.Run(doc,
            "ged.require_api(1) ged.group('setup', function() level.place('Light', 0,0,0) end) return ged.api_version");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("1", r.ReturnValue);
        Assert.Equal(2, doc.Objects.Count);
    }
}
