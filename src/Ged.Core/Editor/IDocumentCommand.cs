namespace Ged.Core.Editor;

/// <summary>
/// A reversible edit to an <see cref="EditorDocument"/>. Every mutation the user
/// can make flows through one of these so undo/redo, coalescing (for drags) and
/// transactions work uniformly. Implementations must make <see cref="Do"/> and
/// <see cref="Undo"/> exact inverses.
/// </summary>
public interface IDocumentCommand
{
    /// <summary>Human-readable label shown in the history panel.</summary>
    string Description { get; }

    /// <summary>
    /// When non-null, consecutive commands pushed with the same key collapse into
    /// a single undo entry (e.g. a continuous drag). Null disables coalescing.
    /// </summary>
    string? CoalesceKey { get; }

    /// <summary>Applies the edit (also used for redo).</summary>
    void Do();

    /// <summary>Reverses the edit, restoring the prior state exactly.</summary>
    void Undo();
}
