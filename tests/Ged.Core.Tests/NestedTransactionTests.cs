using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Nested undo transactions: a <c>BeginTransaction</c> inside an open
/// transaction returns a joined child — no extra undo node, child commit is a no-op, child rollback
/// restores exactly the child's starting state without corrupting the outer transaction — so
/// self-contained "one undo step" operations (prefab placement, multi-brush face edits) work inside
/// a script run.
/// </summary>
public sealed class NestedTransactionTests
{
    /// <summary>A tiny document-free state the commands mutate: an append-only list of tokens.</summary>
    private sealed class State
    {
        public List<string> Items { get; } = new();

        public IDocumentCommand Add(string token) =>
            new RelayCommand($"add {token}", () => Items.Add(token), () => Items.RemoveAt(Items.Count - 1));
    }

    [Fact]
    public void Nested_Commit_Produces_Exactly_One_Node_And_Undo_Restores_The_Pre_Outer_State()
    {
        var undo = new UndoStack();
        var st = new State();
        undo.Execute(st.Add("base"));
        int nodesBefore = undo.NodeCount;

        using (UndoStack.Transaction outer = undo.BeginTransaction("outer"))
        {
            undo.Execute(st.Add("a"));
            using (UndoStack.Transaction child = undo.BeginTransaction("child"))
            {
                Assert.True(child.IsNested);
                Assert.False(outer.IsNested);
                undo.Execute(st.Add("b"));
                child.Commit();
            }

            Assert.True(undo.InTransaction); // the child commit did not close the outer
            Assert.Equal(nodesBefore, undo.NodeCount);
            undo.Execute(st.Add("c"));
            outer.Commit();
        }

        Assert.False(undo.InTransaction);
        Assert.Equal(nodesBefore + 1, undo.NodeCount);
        Assert.Equal("outer", undo.Current.Description);
        Assert.Equal(new[] { "base", "a", "b", "c" }, st.Items);

        undo.Undo();
        Assert.Equal(new[] { "base" }, st.Items);
        undo.Redo();
        Assert.Equal(new[] { "base", "a", "b", "c" }, st.Items);
    }

    [Fact]
    public void Child_Rollback_Restores_The_Child_Start_And_The_Outer_Still_Commits_The_Rest()
    {
        var undo = new UndoStack();
        var st = new State();
        int nodesBefore = undo.NodeCount;

        UndoStack.Transaction outer = undo.BeginTransaction("outer");
        undo.Execute(st.Add("a"));
        UndoStack.Transaction child = undo.BeginTransaction("child");
        undo.Execute(st.Add("b"));
        undo.Execute(st.Add("c"));
        child.Rollback();

        Assert.Equal(new[] { "a" }, st.Items); // exactly the state when the child began
        Assert.True(undo.InTransaction);

        child.Commit(); // closed handle: inert
        undo.Execute(st.Add("d"));
        outer.Commit();

        Assert.Equal(nodesBefore + 1, undo.NodeCount);
        Assert.Equal(new[] { "a", "d" }, st.Items);
        undo.Undo();
        Assert.Empty(st.Items);
        undo.Redo();
        Assert.Equal(new[] { "a", "d" }, st.Items); // the rolled-back child commands are not replayed
    }

    [Fact]
    public void Outer_Rollback_After_Child_Commit_Undoes_Everything_And_Adds_No_Node()
    {
        var undo = new UndoStack();
        var st = new State();
        undo.Execute(st.Add("base"));
        int nodesBefore = undo.NodeCount;

        UndoStack.Transaction outer = undo.BeginTransaction("outer");
        undo.Execute(st.Add("a"));
        using (undo.BeginTransaction("child"))
        {
            undo.Execute(st.Add("b"));
        }

        outer.Rollback();
        Assert.False(undo.InTransaction);
        Assert.Equal(nodesBefore, undo.NodeCount);
        Assert.Equal(new[] { "base" }, st.Items);
    }

    [Fact]
    public void Grandchild_Rollback_Leaves_Its_Parent_Childs_Work_Intact()
    {
        var undo = new UndoStack();
        var st = new State();

        UndoStack.Transaction outer = undo.BeginTransaction("outer");
        UndoStack.Transaction child = undo.BeginTransaction("child");
        undo.Execute(st.Add("a"));
        UndoStack.Transaction grandchild = undo.BeginTransaction("grandchild");
        undo.Execute(st.Add("b"));
        grandchild.Rollback();
        Assert.Equal(new[] { "a" }, st.Items);

        undo.Execute(st.Add("c"));
        child.Rollback(); // back to the child's start: nothing
        Assert.Empty(st.Items);

        undo.Execute(st.Add("e"));
        outer.Commit();
        Assert.Equal(1, undo.NodeCount);
        Assert.Equal(new[] { "e" }, st.Items);
    }

    [Fact]
    public void Child_Used_After_The_Outer_Closed_Is_Inert()
    {
        var undo = new UndoStack();
        var st = new State();

        UndoStack.Transaction outer = undo.BeginTransaction("outer");
        UndoStack.Transaction child = undo.BeginTransaction("child");
        undo.Execute(st.Add("a"));
        outer.Commit();

        child.Rollback(); // the commands now belong to a committed node — must not be undone
        Assert.Equal(new[] { "a" }, st.Items);
        Assert.Equal(1, undo.NodeCount);
        undo.Undo();
        Assert.Empty(st.Items);
    }

    [Fact]
    public void Unnested_Transaction_Behavior_Is_Unchanged()
    {
        var undo = new UndoStack();
        var st = new State();
        using (UndoStack.Transaction tx = undo.BeginTransaction("one"))
        {
            Assert.False(tx.IsNested);
            undo.Execute(st.Add("a"));
            undo.Execute(st.Add("b"));
        }

        Assert.Equal(1, undo.NodeCount);
        UndoStack.Transaction rb = undo.BeginTransaction("two");
        undo.Execute(st.Add("c"));
        rb.Rollback();
        Assert.Equal(1, undo.NodeCount);
        Assert.Equal(new[] { "a", "b" }, st.Items);
    }

    // ---- Inside a script run -------------------------------------------------

    /// <summary>A script host whose "script" is a C# callback — runs Core operations inside a real
    /// <see cref="ScriptRunner"/> run (its one-transaction-per-run wrapper) without needing a Lua API.</summary>
    private sealed class CallbackHost : IScriptHost
    {
        private readonly Action<ScriptContext> _body;

        public CallbackHost(Action<ScriptContext> body) => _body = body;

        public string EngineName => "callback";

        public string EngineVersion => "1";

        public IScriptSession CreateSession(ScriptContext context) => new Session(context, _body);

        private sealed class Session : IScriptSession
        {
            private readonly ScriptContext _ctx;
            private readonly Action<ScriptContext> _body;

            public Session(ScriptContext ctx, Action<ScriptContext> body)
            {
                _ctx = ctx;
                _body = body;
            }

            public ScriptExecution Execute(string source, string chunkName, ScriptExecutionLimits limits, CancellationToken cancellation)
            {
                _body(_ctx);
                return ScriptExecution.Ok(null);
            }

            public void Dispose()
            {
            }
        }
    }

    private static ScriptRunResult RunInScript(EditorDocument doc, BrushEditor? brushes, Action<ScriptContext> body) =>
        new ScriptRunner(new CallbackHost(body)).Run(
            new ScriptServices { Document = doc, Brushes = brushes, Confirmation = new AllowAllConfirmation() },
            "-- callback",
            new ScriptRunOptions { ChunkName = "nested", AllowDestructive = true },
            new ScriptLog());

    private static RfgFile PrefabPayload()
    {
        var group = new RfgGroup { Name = "sample" };
        group.Events.Events.Add(new RflEvent { Uid = 1, ClassName = "Delay", Position = new Vec3(5, 0, 0) });
        group.Triggers.Triggers.Add(new Trigger { Uid = 2, Position = new Vec3(6, 0, 0), Links = { 1 } });
        var file = new RfgFile { Version = 0xC8 };
        file.Groups.Add(group);
        return file;
    }

    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Prefab_Placement_Inside_A_Script_Run_Is_One_Undo_Node()
    {
        EditorDocument doc = EmptyDoc();
        PrefabInstanceRecord? record = null;

        ScriptRunResult result = RunInScript(doc, null, _ =>
        {
            var svc = new PrefabInstanceService(doc);
            record = svc.PlaceInstance(PrefabPayload(), "sample", "h1", new Vec3(10, 0, 0), Mat3.Identity);
            svc.PlaceInstance(PrefabPayload(), "sample", "h1", new Vec3(20, 0, 0), Mat3.Identity);
        });

        Assert.True(result.Success, result.Error?.ToDisplayString());
        Assert.Equal(1, result.UndoNodesAdded);
        Assert.Equal(1, doc.Undo.NodeCount);
        Assert.NotNull(record);
        Assert.All(record!.MemberUids, uid => Assert.NotNull(doc.FindByUid(uid)));
        Assert.Equal(2, new PrefabInstanceService(doc).Instances.Count);

        doc.Undo.Undo();
        Assert.Empty(new PrefabInstanceService(doc).Instances);
        Assert.All(record.MemberUids, uid => Assert.Null(doc.FindByUid(uid)));
        Assert.Empty(doc.Objects);
    }

    [Fact]
    public void Failed_Script_Run_Rolls_Back_A_Nested_Prefab_Placement()
    {
        EditorDocument doc = EmptyDoc();

        ScriptRunResult result = RunInScript(doc, null, _ =>
        {
            new PrefabInstanceService(doc).PlaceInstance(PrefabPayload(), "sample", "h1", Vec3.Zero, Mat3.Identity);
            throw new ScriptApiException("boom");
        });

        Assert.False(result.Success);
        Assert.Equal(0, doc.Undo.NodeCount);
        Assert.Empty(doc.Objects);
        Assert.Empty(new PrefabInstanceService(doc).Instances);
    }

    [Fact]
    public void Multi_Brush_Face_Uv_Edits_Inside_A_Script_Run_Are_One_Undo_Node()
    {
        EditorDocument doc = EmptyDoc();
        var ed = new BrushEditor(doc);
        int a = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, Vec3.Zero, Mat3.Identity);
        int b = ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2 }, new Vec3(4, 0, 0), Mat3.Identity);
        doc.MarkSaved();
        int nodesBefore = doc.Undo.NodeCount;
        Uv OriginalUv(int brush) => ed.FindBrush(brush)!.Geometry.Faces[0].Vertices[0].TextureCoords;
        Uv uvA = OriginalUv(a);
        Uv uvB = OriginalUv(b);

        ScriptRunResult result = RunInScript(doc, ed, _ =>
        {
            // Multi-brush selected-face edit (opens its own transaction for >1 brush) …
            ed.SelectFace(a, 0);
            ed.SelectFace(b, 0, additive: true);
            Assert.True(ed.EditSelectedFaces("Smooth", (g, fi) => g.Faces[fi].SmoothingGroups = 0x5u));

            // … and a multi-brush per-corner UV commit (likewise).
            Assert.True(ed.SetFaceUvs("UVs", new[]
            {
                (a, 0, 0, new Uv(0.25f, 0.5f)),
                (b, 0, 0, new Uv(0.75f, 0.125f)),
            }));
        });

        Assert.True(result.Success, result.Error?.ToDisplayString());
        Assert.Equal(nodesBefore + 1, doc.Undo.NodeCount);
        Assert.Equal(new Uv(0.25f, 0.5f), OriginalUv(a));
        Assert.Equal(new Uv(0.75f, 0.125f), OriginalUv(b));
        Assert.Equal(0x5u, ed.FindBrush(a)!.Geometry.Faces[0].SmoothingGroups);

        doc.Undo.Undo();
        Assert.Equal(uvA, OriginalUv(a));
        Assert.Equal(uvB, OriginalUv(b));
        Assert.NotEqual(0x5u, ed.FindBrush(b)!.Geometry.Faces[0].SmoothingGroups);
    }
}
