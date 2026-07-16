using Ged.Core.Editor;

namespace Ged.Core.Editing;

/// <summary>
/// Enforces that a selection only ever contains kinds the current selection-filter
/// allows. On any mode / filter change the shell calls <see cref="ClearInvalid"/> so a
/// selection made under one granularity (a brush in Brush mode, a face in Face mode, an
/// object in Object mode …) cannot linger — and be transformed — under a mode that does
/// not allow that kind. Selections of kinds still enabled, including several at once via
/// the Ctrl+chip multi-kind filter, survive.
/// </summary>
public static class SelectionScope
{
    /// <summary>
    /// Clears every selection whose kind is not in <paramref name="active"/>: brush /
    /// face / vertex sub-selections via <paramref name="brushes"/>, and the object/group
    /// selection via <paramref name="objects"/>. Null editors are ignored.
    /// </summary>
    public static void ClearInvalid(SelectKinds active, BrushEditor? brushes, EditorDocument? objects)
    {
        brushes?.RetainSelectionKinds(
            (active & SelectKinds.Brushes) != 0,
            (active & SelectKinds.Faces) != 0,
            (active & SelectKinds.Vertices) != 0,
            (active & SelectKinds.Edges) != 0);

        // The object selection backs both Object and Group picking; it survives while
        // either kind is enabled and clears otherwise (Brush/Face/Vertex modes).
        if ((active & (SelectKinds.Objects | SelectKinds.Groups)) == 0)
        {
            objects?.ClearSelection();
        }
    }
}
