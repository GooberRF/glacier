using System.IO;
using System.Text;
using Ged.Core.Editor;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The tree-backed undo stack. A new edit after undos FORKS a branch (instead
/// of discarding the redo tail), any node is reachable by time-travel, and the tree is
/// bounded by oldest-branch-first pruning. (The linear behaviour is covered by
/// <see cref="UndoStackTests"/>, which must stay green — the tree is invisible until a fork.)
/// </summary>
public sealed class UndoTreeTests
{
    private static RelayCommand Set(int[] cell, int value, string? coalesceKey = null)
    {
        int previous = cell[0];
        return new RelayCommand($"set {value}", () => cell[0] = value, () => cell[0] = previous, coalesceKey);
    }

    private static RelayCommand Cmd(int[] cell, int value, string description)
    {
        int previous = cell[0];
        return new RelayCommand(description, () => cell[0] = value, () => cell[0] = previous);
    }

    [Fact]
    public void New_Edit_After_Undo_Forks_And_Retains_The_Old_Branch()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();

        stack.Execute(Set(cell, 1)); // node A: 0 -> 1
        UndoNode a = stack.Current;
        stack.Undo(); // back to the baseline (0)
        stack.Execute(Set(cell, 2)); // fork: node B: 0 -> 2
        UndoNode b = stack.Current;

        Assert.Equal(2, cell[0]);
        // The redo tail (branch A) is NOT discarded — the root now has two children.
        Assert.Equal(2, stack.Root.Children.Count);
        Assert.False(stack.CanRedo); // B has no child, so there is nothing to redo from here

        // The old branch is still reachable by time-travel.
        stack.MoveToNode(a);
        Assert.Equal(1, cell[0]);
        Assert.Same(a, stack.Current);

        stack.MoveToNode(b);
        Assert.Equal(2, cell[0]);
    }

    [Fact]
    public void Redo_After_Fork_Follows_The_Most_Recent_Child()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Set(cell, 1)); // A
        stack.Undo();
        stack.Execute(Set(cell, 2)); // B (newest child of root)
        stack.Undo(); // back to baseline; redo should follow the newest child (B)

        Assert.True(stack.CanRedo);
        stack.Redo();
        Assert.Equal(2, cell[0]); // B, not A
    }

    [Fact]
    public void Time_Travel_Across_Branches_Equals_Replaying_The_Path()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();

        stack.Execute(Set(cell, 1)); // A: baseline -> 1
        UndoNode a = stack.Current;
        stack.Execute(Set(cell, 3)); // C: A -> 3
        UndoNode c = stack.Current;

        stack.MoveToNode(a); // rewind to A (1)
        stack.Execute(Set(cell, 2)); // B: A -> 2 (forks A; A now has children C then B)
        UndoNode b = stack.Current;

        Assert.Equal(2, a.Children.Count);

        // Jump from B to C: their LCA is A; the stack undoes B then redoes C.
        stack.MoveToNode(c);
        Assert.Equal(3, cell[0]);
        Assert.Same(c, stack.Current);

        // And back to B.
        stack.MoveToNode(b);
        Assert.Equal(2, cell[0]);

        // Down to the baseline.
        stack.MoveToNode(stack.Root);
        Assert.Equal(0, cell[0]);
    }

    [Fact]
    public void Linear_Spine_Is_Bounded_By_Root_Advancement()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        int n = UndoStack.MaxNodes + 25;
        for (int i = 1; i <= n; i++)
        {
            stack.Execute(Set(cell, i));
        }

        Assert.Equal(n, cell[0]);
        Assert.True(stack.NodeCount <= UndoStack.MaxNodes, $"node count {stack.NodeCount} must be bounded by {UndoStack.MaxNodes}");

        // The bounded tail of history still undoes correctly.
        stack.Undo();
        Assert.Equal(n - 1, cell[0]);
        stack.Redo();
        Assert.Equal(n, cell[0]);
    }

    [Fact]
    public void Oldest_Branches_Are_Pruned_First_And_The_Current_Spine_Survives()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();

        // Fork a fresh child off the baseline many times over the cap; each previous current
        // becomes an off-spine leaf, so pruning removes the OLDEST such leaves first.
        int forks = UndoStack.MaxNodes + 30;
        UndoNode? last = null;
        for (int i = 0; i < forks; i++)
        {
            stack.MoveToNode(stack.Root);
            stack.Execute(Set(cell, 1000 + i));
            last = stack.Current;
        }

        Assert.True(stack.NodeCount <= UndoStack.MaxNodes);
        // The current node (newest fork) is always retained and reflects the level state.
        Assert.Same(last, stack.Current);
        Assert.Equal(1000 + forks - 1, cell[0]);
    }

    /// <summary>
    /// Builds a small forked history and dumps the History-panel tree layout (the same DFS the
    /// panel renders: first child on the trunk, later forks indented) as the acceptance artifact.
    /// </summary>
    [Fact]
    public void History_Tree_Panel_State_Dump_Artifact()
    {
        var cell = new[] { 0 };
        var stack = new UndoStack();
        stack.Execute(Cmd(cell, 1, "Move brush"));
        stack.Execute(Cmd(cell, 2, "Extrude face"));
        UndoNode afterExtrude = stack.Current;
        stack.Execute(Cmd(cell, 3, "Apply texture"));

        // Fork off "Extrude face" with an alternate future.
        stack.MoveToNode(afterExtrude);
        stack.Execute(Cmd(cell, 4, "Bevel face"));
        stack.Execute(Cmd(cell, 5, "Rotate brush"));

        // Second fork off the same node.
        stack.MoveToNode(afterExtrude);
        stack.Execute(Cmd(cell, 6, "Delete face"));

        string dump = Dump(stack);

        // Sanity: the forked branches are present in the layout.
        Assert.Contains("Apply texture", dump);
        Assert.Contains("Bevel face", dump);
        Assert.Contains("Delete face", dump);
        Assert.Contains("[*]", dump); // the current-node marker

        if (TestPaths.RepoRoot is string root)
        {
            string dir = Path.Combine(root, "tests", "artifacts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "history_tree_dump.txt"), dump);
        }
    }

    private static string Dump(UndoStack stack)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Undo history tree (nodes={stack.NodeCount}, current='{stack.Current.Description}')");
        sb.AppendLine("[*] = current node, indented rows = forked branches");
        sb.AppendLine();

        void Emit(UndoNode node, int depth)
        {
            string marker = ReferenceEquals(node, stack.Current) ? "[*] " : "    ";
            string label = ReferenceEquals(node, stack.Root) ? "(baseline)" : node.Description;
            sb.Append(marker).Append(' ', depth * 4).AppendLine(label);
            var kids = node.Children;
            for (int i = 0; i < kids.Count; i++)
            {
                Emit(kids[i], depth + (i == 0 ? 0 : 1));
            }
        }

        Emit(stack.Root, 0);
        return sb.ToString();
    }
}
