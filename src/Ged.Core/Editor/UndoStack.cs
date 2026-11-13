using System;
using System.Collections.Generic;

namespace Ged.Core.Editor;

/// <summary>
/// A node in the undo <b>tree</b>. The root is the clean baseline (no
/// command); every other node holds the command that transforms its parent's state into
/// its own. Children are ordered by creation; <see cref="ActiveChild"/> is the branch
/// redo follows (the most recently visited child — set on fork and on undo).
/// </summary>
public sealed class UndoNode
{
    internal UndoNode(int id, IDocumentCommand? command, UndoNode? parent, DateTime timestamp)
    {
        Id = id;
        Command = command;
        Parent = parent;
        Timestamp = timestamp;
        Description = command?.Description ?? "open";
    }

    /// <summary>Stable, monotonically increasing id (root = 0). Identifies the node for the panel.</summary>
    public int Id { get; }

    /// <summary>Human-readable label (the command description, or "open" for the root).</summary>
    public string Description { get; internal set; }

    /// <summary>When this node was created (branch labelling / oldest-branch pruning).</summary>
    public DateTime Timestamp { get; }

    /// <summary>The command from parent→this, or null for the root baseline.</summary>
    public IDocumentCommand? Command { get; internal set; }

    /// <summary>The parent node, or null for the root.</summary>
    public UndoNode? Parent { get; internal set; }

    private readonly List<UndoNode> _children = new();

    /// <summary>Child branches in creation order (the last-created is the newest).</summary>
    public IReadOnlyList<UndoNode> Children => _children;

    /// <summary>The child redo follows by default (most recently created/visited), or null.</summary>
    public UndoNode? ActiveChild { get; internal set; }

    /// <summary>Depth from the root (root = 0).</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    internal void AddChild(UndoNode child) => _children.Add(child);

    internal void RemoveChild(UndoNode child) => _children.Remove(child);

    /// <summary>The child redo would step into: the active child, else the newest child, else null.</summary>
    public UndoNode? RedoChild => ActiveChild is not null && _children.Contains(ActiveChild)
        ? ActiveChild
        : (_children.Count > 0 ? _children[^1] : null);
}

/// <summary>
/// Unlimited-depth <b>tree-backed</b> undo/redo. Commands are executed through
/// <see cref="Execute"/> (which runs <see cref="IDocumentCommand.Do"/> and records the
/// command), consecutive commands sharing a <see cref="IDocumentCommand.CoalesceKey"/>
/// collapse into one node, and <see cref="BeginTransaction"/> groups a burst of edits (a
/// drag) into a single node. The stack never clears on save.
/// <para>
/// Unlike a linear stack, performing a new edit after undos <b>forks</b> a new branch off
/// the current node instead of discarding the redo tail — the alternate future is retained
/// in the tree and reachable via <see cref="MoveToNode"/>. Undo/redo and the linear
/// <see cref="UndoEntries"/>/<see cref="RedoEntries"/>/<see cref="Position"/>/<see cref="MoveTo"/>
/// surface behave identically to the old linear stack along the current branch, so every
/// existing call site and test keeps working; the tree only becomes observable once a fork
/// exists. Total retained nodes are capped (<see cref="MaxNodes"/>) with oldest-branch-first
/// pruning to bound memory.
/// </para>
/// </summary>
public sealed class UndoStack
{
    /// <summary>Upper bound on retained tree nodes; oldest branches are pruned past this.</summary>
    public const int MaxNodes = 2000;

    private UndoNode _root;
    private UndoNode _current;
    private int _nextId;
    private int _nodeCount; // nodes excluding the root baseline
    private Transaction? _transaction;
    private bool _coalesceBarrier;

    public UndoStack()
    {
        _root = new UndoNode(0, command: null, parent: null, DateTime.Now);
        _current = _root;
        _nextId = 1;
    }

    /// <summary>Raised after any change to the undo/redo state.</summary>
    public event Action? Changed;

    /// <summary>
    /// Optional scope opened around the command application of a SINGLE atomic Undo / Redo / MoveToNode
    /// (the "Instant" path), so a multi-command entry — a gizmo <see cref="CompositeCommand"/> drag, or a
    /// coalesced M-N node — coalesces the model notifications its sub-commands raise into one refresh
    /// instead of animating the change frame by frame. Null = no coalescing (each sub-command notifies as
    /// it applies). <see cref="StepToward"/> (the Replay path) never uses this, so a replay steps visibly.
    /// </summary>
    public Func<IDisposable>? AtomicApplyScope { get; set; }

    /// <summary>
    /// Optional runner for a command's FIRST application in <see cref="Execute"/>: it must call the
    /// command's <see cref="IDocumentCommand.Do"/> exactly once and return the command to record —
    /// the same one, or a wrapper whose Do / Undo also re-apply / reverse state the runner derived
    /// from the edit. The document uses it to keep linked data in the same undo step as the edit that
    /// moved it (a whole-mover transform carrying its member brush — see
    /// <see cref="Ged.Core.Editing.MoverObjectTransformLink"/>). Null = the command runs as-is. Undo and redo
    /// never pass through it: they replay the recorded command.
    /// </summary>
    public Func<IDocumentCommand, IDocumentCommand>? CommandRunner { get; set; }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private IDisposable EnterApply(bool coalesce) =>
        coalesce ? AtomicApplyScope?.Invoke() ?? NullScope.Instance : NullScope.Instance;

    /// <summary>The tree root (the clean baseline, no command).</summary>
    public UndoNode Root => _root;

    /// <summary>The node the document currently sits at.</summary>
    public UndoNode Current => _current;

    /// <summary>Total number of edit nodes retained in the tree (excludes the root baseline).</summary>
    public int NodeCount => _nodeCount;

    /// <summary>Undo entries along the current branch, oldest first; the last is the next to undo.</summary>
    public IReadOnlyList<IDocumentCommand> UndoEntries
    {
        get
        {
            var list = new List<IDocumentCommand>();
            for (UndoNode? n = _current; n is not null && n != _root; n = n.Parent)
            {
                if (n.Command is not null)
                {
                    list.Add(n.Command);
                }
            }

            list.Reverse();
            return list;
        }
    }

    /// <summary>Redo entries along the current branch; the last is the next to redo.</summary>
    public IReadOnlyList<IDocumentCommand> RedoEntries
    {
        get
        {
            var chain = new List<IDocumentCommand>();
            for (UndoNode? n = _current.RedoChild; n is not null; n = n.RedoChild)
            {
                if (n.Command is not null)
                {
                    chain.Add(n.Command);
                }
            }

            // Existing contract: RedoEntries[^1] is the NEXT to redo, [0] the furthest.
            chain.Reverse();
            return chain;
        }
    }

    public bool CanUndo => _current != _root;

    public bool CanRedo => _current.RedoChild is not null;

    public bool InTransaction => _transaction is not null;

    /// <summary>Position in the current-branch timeline (number of applied commands = current depth).</summary>
    public int Position => _current.Depth;

    /// <summary>Executes a command and records it (coalescing / transaction aware).</summary>
    public void Execute(IDocumentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CommandRunner is { } run)
        {
            command = run(command);
        }
        else
        {
            command.Do();
        }

        Record(command);
    }

    private void Record(IDocumentCommand command)
    {
        if (_transaction is not null)
        {
            _transaction.Commands.Add(command);
            return;
        }

        // Coalesce a continuous drag into the current node (keep the earliest Undo, adopt the
        // latest Do). This never happens across an Undo (barrier) or from the baseline.
        if (!_coalesceBarrier && command.CoalesceKey is not null &&
            _current != _root && _current.Command is { } top && top.CoalesceKey == command.CoalesceKey)
        {
            _current.Command = new RelayCommand(command.Description, command.Do, top.Undo, command.CoalesceKey);
            _current.Description = command.Description;
            _coalesceBarrier = false;
            Changed?.Invoke();
            return;
        }

        AddChildNode(command);
        _coalesceBarrier = false;
        Changed?.Invoke();
    }

    private void AddChildNode(IDocumentCommand command)
    {
        // A new edit after undos FORKS: it becomes a new child of the current node without
        // discarding the existing children (the redo tail / alternate branches are retained).
        var node = new UndoNode(_nextId++, command, _current, DateTime.Now);
        _current.AddChild(node);
        _current.ActiveChild = node;
        _current = node;
        _nodeCount++;
        PruneIfNeeded();
    }

    /// <summary>
    /// Undoes the last command on the current branch. When <paramref name="coalesce"/> (the default,
    /// "Instant" behaviour), the sub-command notifications of a multi-command entry are coalesced into
    /// one refresh via <see cref="AtomicApplyScope"/>; pass false ("Replay") to let each apply notify so
    /// the change steps visibly.
    /// </summary>
    public void Undo(bool coalesce = true)
    {
        if (_current == _root)
        {
            return;
        }

        using (EnterApply(coalesce))
        {
            StepUp();
        }

        _coalesceBarrier = true;
        Changed?.Invoke();
    }

    /// <summary>Redoes the next command on the current branch. See <see cref="Undo(bool)"/> for <paramref name="coalesce"/>.</summary>
    public void Redo(bool coalesce = true)
    {
        UndoNode? target = _current.RedoChild;
        if (target is null)
        {
            return;
        }

        using (EnterApply(coalesce))
        {
            StepDown(target);
        }

        _coalesceBarrier = true;
        Changed?.Invoke();
    }

    /// <summary>Undoes the current node's command and moves to its parent (recording the return path).</summary>
    private void StepUp()
    {
        UndoNode node = _current;
        UndoNode parent = node.Parent!;
        parent.ActiveChild = node; // redo returns to where we came from
        node.Command?.Undo();
        _current = parent;
    }

    /// <summary>Applies a child's command and descends into it.</summary>
    private void StepDown(UndoNode child)
    {
        _current.ActiveChild = child;
        child.Command?.Do();
        _current = child;
    }

    /// <summary>
    /// Jumps to a point in the current-branch timeline by undoing or redoing as needed.
    /// <paramref name="targetPosition"/> is a value of <see cref="Position"/>.
    /// </summary>
    public void MoveTo(int targetPosition)
    {
        int total = _current.Depth + RedoEntries.Count;
        targetPosition = Math.Clamp(targetPosition, 0, total);
        while (_current.Depth > targetPosition)
        {
            Undo();
        }

        while (_current.Depth < targetPosition && _current.RedoChild is not null)
        {
            Redo();
        }
    }

    /// <summary>
    /// Time-travels to an arbitrary tree node (across branches): undoes up to the lowest common
    /// ancestor of the current node and <paramref name="target"/>, then redoes down to the
    /// target. The resulting document state equals replaying the root→target command path.
    /// </summary>
    public void MoveToNode(UndoNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target == _current)
        {
            return;
        }

        // Ancestor chain of the target (target … root) so we can find the LCA.
        var targetPath = new HashSet<UndoNode>();
        for (UndoNode? n = target; n is not null; n = n.Parent)
        {
            targetPath.Add(n);
        }

        // The whole jump is ONE atomic (Instant) application — coalesce every crossed node's sub-command
        // notifications into a single refresh (Replay steps via StepToward, which does not use this).
        using (EnterApply(coalesce: true))
        {
            // Undo up until the current node lies on the target's root-path (that node is the LCA).
            while (!targetPath.Contains(_current))
            {
                StepUp();
            }

            // Redo down the LCA→target path.
            var down = new List<UndoNode>();
            for (UndoNode n = target; n != _current; n = n.Parent!)
            {
                down.Add(n);
            }

            down.Reverse();
            foreach (UndoNode c in down)
            {
                StepDown(c);
            }
        }

        _coalesceBarrier = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Performs a SINGLE step toward <paramref name="target"/> (one command's Do or Undo) and returns
    /// true if it moved, false when already there. Looping this to completion reaches the same document
    /// state as <see cref="MoveToNode"/> — the LCA→target path applied one entry at a time — but lets a
    /// caller REPLAY the jump visibly, refreshing the view between entries (the "Replay" undo mode).
    /// Each step: if the current node is an ancestor of the target, redo one child toward it; otherwise
    /// undo up toward the lowest common ancestor. Fires <see cref="Changed"/> per step so a history view
    /// can track the walk.
    /// </summary>
    public bool StepToward(UndoNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, _current))
        {
            return false;
        }

        // Walk target→root. If we pass through the current node, it is an ancestor of the target, and
        // `child` (trailing one behind) is the current node's child on the path down to the target.
        UndoNode? child = null;
        for (UndoNode? n = target; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, _current))
            {
                StepDown(child!); // redo one entry toward the target
                _coalesceBarrier = true;
                Changed?.Invoke();
                return true;
            }

            child = n;
        }

        // The current node is not on the target's root-path — undo one entry toward the common ancestor.
        StepUp();
        _coalesceBarrier = true;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Begins a transaction. Commands executed until the returned handle is committed or
    /// disposed are grouped into one undo node; disposing without an explicit call commits.
    /// <see cref="Transaction.Rollback"/> undoes them.
    /// <para>
    /// <b>Nesting.</b> Calling this while a transaction is already open returns a <b>joined child</b>
    /// handle (<see cref="Transaction.IsNested"/>) instead of throwing: its commands are recorded into
    /// the outermost transaction, so the whole nest still produces exactly ONE undo node (named by the
    /// outermost description) when the outermost transaction commits. On the child,
    /// <see cref="Transaction.Commit"/> / <see cref="Transaction.Dispose"/> are no-ops toward the stack
    /// (the outer transaction decides), and <see cref="Transaction.Rollback"/> undoes — in reverse —
    /// only the commands recorded since the child began, restoring the document to exactly the state
    /// it had when the child began, and drops them from the outer transaction, which stays open and
    /// valid (a later outer commit records only what remains; a later outer rollback undoes the rest).
    /// A child used after its outermost transaction has closed is inert. This lets self-contained
    /// "one undo step" operations (prefab placement, multi-brush face edits) run inside a larger
    /// transaction such as a script run.
    /// </para>
    /// </summary>
    public Transaction BeginTransaction(string description)
    {
        if (_transaction is not null)
        {
            return new Transaction(this, description, _transaction);
        }

        _transaction = new Transaction(this, description, root: null);
        return _transaction;
    }

    /// <summary>
    /// Begins a transaction whose commands, on commit, are FOLDED into the current undo node instead
    /// of becoming a node of their own: the node's command becomes "its original command, then these",
    /// so one Undo reverses both and one Redo re-applies both. Used for derived-data upkeep that an
    /// operation performs on the author's behalf (refreshing stale mover copies before a save or build)
    /// so it rides along with the latest edit rather than adding an extra step the author must undo
    /// first. At the baseline (no node to fold into) the commands become a normal node. Opened inside
    /// another transaction it joins that one, exactly like <see cref="BeginTransaction"/>.
    /// </summary>
    public Transaction BeginFoldedTransaction(string description)
    {
        if (_transaction is not null)
        {
            return new Transaction(this, description, _transaction);
        }

        _transaction = new Transaction(this, description, root: null) { FoldIntoCurrent = true };
        return _transaction;
    }

    /// <summary>Forgets all history (used when a document is closed/replaced).</summary>
    public void Clear()
    {
        _root = new UndoNode(0, command: null, parent: null, DateTime.Now);
        _current = _root;
        _nextId = 1;
        _nodeCount = 0;
        _transaction = null;
        _coalesceBarrier = false;
        Changed?.Invoke();
    }

    private void CommitTransaction(Transaction t)
    {
        if (!ReferenceEquals(_transaction, t))
        {
            return;
        }

        _transaction = null;
        if (t.Commands.Count == 0)
        {
            return;
        }

        IDocumentCommand entry = t.Commands.Count == 1
            ? t.Commands[0]
            : new CompositeCommand(t.Description, t.Commands.ToArray());
        if (t.FoldIntoCurrent && _current != _root && _current.Command is { } top)
        {
            // Folded: the current node now applies its own command and then these. The composite has no
            // coalesce key, and the barrier below stops a continuing drag from coalescing into (and so
            // dropping the Do of) the folded commands.
            _current.Command = new CompositeCommand(_current.Description, new[] { top, entry });
        }
        else
        {
            AddChildNode(entry);
        }

        _coalesceBarrier = true;
        Changed?.Invoke();
    }

    private void RollbackTransaction(Transaction t)
    {
        if (!ReferenceEquals(_transaction, t))
        {
            return;
        }

        _transaction = null;
        for (int i = t.Commands.Count - 1; i >= 0; i--)
        {
            t.Commands[i].Undo();
        }

        _coalesceBarrier = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Rolls back a joined child: undoes (newest first) the commands its root transaction recorded
    /// since the child began and removes them from the root, leaving the root open. Inert once the
    /// root has closed (its commands then belong to a committed node or were already undone).
    /// </summary>
    private void RollbackNested(Transaction root, int startIndex)
    {
        if (!ReferenceEquals(_transaction, root))
        {
            return;
        }

        List<IDocumentCommand> commands = root.Commands;
        int start = Math.Clamp(startIndex, 0, commands.Count);
        for (int i = commands.Count - 1; i >= start; i--)
        {
            commands[i].Undo();
        }

        commands.RemoveRange(start, commands.Count - start);
        Changed?.Invoke();
    }

    // ---- Pruning --------------------------------------------------------------

    /// <summary>
    /// Bounds retained nodes to <see cref="MaxNodes"/>. Prefers pruning the oldest leaf that is
    /// not on the current undo spine (root→current) — i.e. the tips of the oldest alternate
    /// branches. If the current spine alone exceeds the cap, advances the root (drops the oldest
    /// undo step and its sibling branches), matching a bounded linear history.
    /// </summary>
    private void PruneIfNeeded()
    {
        if (_nodeCount <= MaxNodes)
        {
            return;
        }

        // Nodes we must never remove: the current node and its ancestors (the undo spine).
        var spine = new HashSet<UndoNode>();
        for (UndoNode? n = _current; n is not null; n = n.Parent)
        {
            spine.Add(n);
        }

        while (_nodeCount > MaxNodes)
        {
            UndoNode? victim = OldestPrunableLeaf(spine);
            if (victim is not null)
            {
                UndoNode parent = victim.Parent!;
                parent.RemoveChild(victim);
                if (ReferenceEquals(parent.ActiveChild, victim))
                {
                    parent.ActiveChild = parent.Children.Count > 0 ? parent.Children[^1] : null;
                }

                _nodeCount--;
                continue;
            }

            // Everything left is on the undo spine — advance the root to bound it.
            if (!AdvanceRoot(spine))
            {
                break;
            }
        }
    }

    /// <summary>The lowest-id leaf not protected by <paramref name="spine"/>, or null.</summary>
    private UndoNode? OldestPrunableLeaf(HashSet<UndoNode> spine)
    {
        UndoNode? best = null;
        var stack = new Stack<UndoNode>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            UndoNode n = stack.Pop();
            if (n.Children.Count == 0)
            {
                if (n != _root && !spine.Contains(n) && (best is null || n.Id < best.Id))
                {
                    best = n;
                }

                continue;
            }

            foreach (UndoNode c in n.Children)
            {
                stack.Push(c);
            }
        }

        return best;
    }

    /// <summary>
    /// Drops the oldest undo step: promotes the root's child that leads toward the current node
    /// to be the new root (baking that command into the baseline) and discards the root's other
    /// branches. Returns false when the root has no child on the spine (nothing to advance).
    /// </summary>
    private bool AdvanceRoot(HashSet<UndoNode> spine)
    {
        UndoNode? next = null;
        foreach (UndoNode c in _root.Children)
        {
            if (spine.Contains(c))
            {
                next = c;
                break;
            }
        }

        if (next is null)
        {
            return false;
        }

        // Count the branches we're discarding (root's other children + their subtrees).
        foreach (UndoNode c in _root.Children)
        {
            if (!ReferenceEquals(c, next))
            {
                _nodeCount -= CountSubtree(c);
            }
        }

        next.Parent = null;
        next.Command = null; // it is now the baked-in baseline
        next.Description = "open";
        next.ActiveChild = next.Children.Count > 0 ? next.ActiveChild : null;
        _root = next;
        _nodeCount--; // the promoted node is no longer an edit node
        return true;
    }

    private static int CountSubtree(UndoNode n)
    {
        int count = 1;
        foreach (UndoNode c in n.Children)
        {
            count += CountSubtree(c);
        }

        return count;
    }

    /// <summary>
    /// A group of commands recorded as a single undo entry. A handle returned while another
    /// transaction was open is a joined child (<see cref="IsNested"/>); see
    /// <see cref="UndoStack.BeginTransaction"/> for the nesting semantics.
    /// </summary>
    public sealed class Transaction : IDisposable
    {
        private readonly UndoStack _owner;
        private readonly Transaction? _root;
        private readonly int _startIndex;
        private bool _closed;

        internal Transaction(UndoStack owner, string description, Transaction? root)
        {
            _owner = owner;
            Description = description;
            _root = root;
            _startIndex = root?.Commands.Count ?? 0;
        }

        internal List<IDocumentCommand> Commands { get; } = new();

        public string Description { get; }

        /// <summary>True for an outermost <see cref="UndoStack.BeginFoldedTransaction"/>: commit folds into the current node.</summary>
        internal bool FoldIntoCurrent { get; init; }

        /// <summary>True for a joined child opened inside another transaction (records into the outermost one).</summary>
        public bool IsNested => _root is not null;

        /// <summary>
        /// Outermost: collapses the recorded commands into one undo entry. Joined child: closes the
        /// handle only — its commands stay in the outer transaction, which decides their fate.
        /// </summary>
        public void Commit()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_root is null)
            {
                _owner.CommitTransaction(this);
            }
        }

        /// <summary>
        /// Outermost: undoes every recorded command and discards the transaction. Joined child: undoes
        /// only the commands recorded since the child began (the document returns to its state at the
        /// child's start) and leaves the outer transaction open.
        /// </summary>
        public void Rollback()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_root is null)
            {
                _owner.RollbackTransaction(this);
            }
            else
            {
                _owner.RollbackNested(_root, _startIndex);
            }
        }

        public void Dispose() => Commit();
    }
}
