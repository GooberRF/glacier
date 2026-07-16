using Ged.Core.Editing;
using Ged.Rendering.Picking;

namespace Ged.App.Services;

/// <summary>
/// The strict mode-scoped pick gate (item 5): given the selection-filter chips
/// (which each mode switch resets to its strict default kind, Ctrl+chip opts into
/// multi-kind picking), decides whether an id-buffer hit may select. Out-of-mode
/// kinds are ignored even when they are the topmost hit — a click on a disallowed
/// kind is a no-op, never a cross-mode selection.
/// </summary>
public static class PickGate
{
    /// <summary>
    /// True when a brush-editor pick (brush / brush face / brush vertex) of
    /// <paramref name="kind"/> may select under the active chips.
    /// </summary>
    public static bool AllowsBrushEditor(SelectKinds active, PickKind kind) => kind switch
    {
        PickKind.Brush => (active & SelectKinds.Brushes) != 0,
        PickKind.BrushFace => (active & SelectKinds.Faces) != 0,
        PickKind.BrushVertex => (active & SelectKinds.Vertices) != 0,
        _ => false,
    };

    /// <summary>
    /// True when a document-level pick may select under the active chips.
    /// Object/Mesh picks are level objects (Objects or Groups chip). A
    /// <see cref="PickKind.Brush"/> hit document-selects only as a group member
    /// (Groups chip) — except a mover's geometry, which IS a level object and
    /// stays clickable in Object mode (<paramref name="isMoverObject"/>).
    /// </summary>
    public static bool AllowsDocumentSelect(SelectKinds active, PickKind kind, bool isMoverObject) => kind switch
    {
        PickKind.Object or PickKind.Mesh => (active & (SelectKinds.Objects | SelectKinds.Groups)) != 0,
        PickKind.Brush => (active & SelectKinds.Groups) != 0
            || ((active & SelectKinds.Objects) != 0 && isMoverObject),
        _ => false,
    };
}
