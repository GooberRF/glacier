using Ged.Core.Editor;
using Xunit;

namespace Ged.Core.Tests;

public sealed class UndoStackTests
{
    private static RelayCommand Set(int[] cell, int value, string? coalesceKey = null)
    {
        int previous = cell[0];
        return new RelayCommand($"set {value}", () => cell[0] = value, () => cell[0] = previous, coalesceKey);
    }

    [Fact]
    public void Execute_Applies_And_Records()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();

        stack.Execute(Set(cell, 5));

        Assert.Equal(5, cell[0]);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal(1, stack.Position);
    }

    [Fact]
    public void Undo_And_Redo_Restore_State()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 5));
        stack.Execute(Set(cell, 9));

        stack.Undo();
        Assert.Equal(5, cell[0]);
        stack.Undo();
        Assert.Equal(0, cell[0]);
        Assert.False(stack.CanUndo);

        stack.Redo();
        Assert.Equal(5, cell[0]);
        stack.Redo();
        Assert.Equal(9, cell[0]);
    }

    [Fact]
    public void New_Execute_Clears_Redo()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Execute(Set(cell, 2));
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Coalesced_Commands_Are_One_Entry_With_Earliest_Undo_And_Latest_Redo()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();

        stack.Execute(Set(cell, 10, "drag"));
        stack.Execute(Set(cell, 20, "drag"));
        stack.Execute(Set(cell, 30, "drag"));

        Assert.Equal(30, cell[0]);
        Assert.Equal(1, stack.Position); // collapsed into a single entry

        stack.Undo();
        Assert.Equal(0, cell[0]); // restores pre-drag state, not an intermediate one
        Assert.False(stack.CanUndo);

        stack.Redo();
        Assert.Equal(30, cell[0]); // reapplies the final value
    }

    [Fact]
    public void Different_Coalesce_Keys_Do_Not_Merge()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1, "a"));
        stack.Execute(Set(cell, 2, "b"));

        Assert.Equal(2, stack.Position);
    }

    [Fact]
    public void Undo_Then_Same_Key_Does_Not_Coalesce_Into_Restored_Entry()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1, "k"));
        stack.Undo();
        stack.Execute(Set(cell, 2, "k"));

        Assert.Equal(1, stack.Position);
        Assert.Equal(2, cell[0]);
    }

    [Fact]
    public void Null_Coalesce_Key_Never_Merges()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1));
        stack.Execute(Set(cell, 2));

        Assert.Equal(2, stack.Position);
    }

    [Fact]
    public void Transaction_Collapses_To_Single_Entry()
    {
        var a = new[] { 0 };
        var b = new[] { 0 };
        var stack = new UndoStack();

        using (stack.BeginTransaction("move"))
        {
            stack.Execute(Set(a, 1));
            stack.Execute(Set(b, 2));
        }

        Assert.Equal(1, stack.Position);
        Assert.Equal(1, a[0]);
        Assert.Equal(2, b[0]);

        stack.Undo();
        Assert.Equal(0, a[0]);
        Assert.Equal(0, b[0]);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Transaction_Rollback_Undoes_All_Without_Recording()
    {
        var a = new[] { 0 };
        var stack = new UndoStack();

        UndoStack.Transaction tx = stack.BeginTransaction("drag");
        stack.Execute(Set(a, 7));
        stack.Execute(Set(a, 8));
        tx.Rollback();

        Assert.Equal(0, a[0]);
        Assert.Equal(0, stack.Position);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Single_Command_Transaction_Is_Not_Wrapped_In_Composite()
    {
        var a = new[] { 0 };
        var stack = new UndoStack();
        using (stack.BeginTransaction("one"))
        {
            stack.Execute(Set(a, 3));
        }

        Assert.Equal(1, stack.Position);
        Assert.Single(stack.UndoEntries);
    }

    [Fact]
    public void MoveTo_Jumps_Forward_And_Back()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1));
        stack.Execute(Set(cell, 2));
        stack.Execute(Set(cell, 3));

        stack.MoveTo(1);
        Assert.Equal(1, cell[0]);

        stack.MoveTo(3);
        Assert.Equal(3, cell[0]);

        stack.MoveTo(0);
        Assert.Equal(0, cell[0]);
    }

    [Fact]
    public void Nested_Transaction_Throws()
    {
        var stack = new UndoStack();
        stack.BeginTransaction("outer");
        Assert.Throws<System.InvalidOperationException>(() => stack.BeginTransaction("inner"));
    }

    // ---- Q4: Replay (StepToward) reaches the same state as the Instant (MoveToNode) jump ----

    [Fact]
    public void StepToward_Walks_One_Entry_Per_Step_To_The_Same_State_As_MoveToNode()
    {
        // Replay undo: StepToward applied to completion reaches the identical document state as the
        // Instant MoveToNode jump — it just applies one history entry at a time so the view can refresh
        // between them. Here: root → 1 → 2 → 3, then jump back to the tip from the baseline.
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1));
        stack.Execute(Set(cell, 2));
        stack.Execute(Set(cell, 3));
        UndoNode tip = stack.Current;

        // Instant reference: undo to baseline, then one MoveToNode → value 3.
        stack.MoveTo(0);
        Assert.Equal(0, cell[0]);
        stack.MoveToNode(tip);
        int instant = cell[0];
        Assert.Equal(3, instant);

        // Replay: undo to baseline, then StepToward one entry at a time → 3 steps, identical end state.
        stack.MoveTo(0);
        Assert.Equal(0, cell[0]);
        int steps = 0;
        while (stack.StepToward(tip))
        {
            steps++;
        }

        Assert.Equal(3, steps);            // root→1, 1→2, 2→3
        Assert.Equal(instant, cell[0]);    // same final state as the Instant jump
        Assert.Same(tip, stack.Current);
        Assert.False(stack.StepToward(tip)); // already there → no further step
    }

    [Fact]
    public void StepToward_Crosses_Branches_To_The_Same_State_As_MoveToNode()
    {
        // A fork at value 1: branch A tip = 2, branch B tip = 9. Jumping between the tips must undo
        // across the common ancestor and redo down the other branch. Replay does it one entry at a time.
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1));
        UndoNode fork = stack.Current;
        stack.Execute(Set(cell, 2));
        UndoNode branchA = stack.Current;

        stack.MoveToNode(fork);
        stack.Execute(Set(cell, 9));
        UndoNode branchB = stack.Current;

        // Instant reference: from B's tip, MoveToNode to A's tip → value 2.
        stack.MoveToNode(branchA);
        Assert.Equal(2, cell[0]);

        // Replay: from A's tip, StepToward B's tip one entry at a time → undo 2, redo 9 (2 steps).
        int steps = 0;
        while (stack.StepToward(branchB))
        {
            steps++;
        }

        Assert.Equal(2, steps);
        Assert.Same(branchB, stack.Current);
        Assert.Equal(9, cell[0]); // the same state MoveToNode(branchB) would produce
    }
}
