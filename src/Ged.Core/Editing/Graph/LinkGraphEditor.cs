using System;
using Ged.Core.Editor;

namespace Ged.Core.Editing.Graph;

/// <summary>
/// The undo-safe editing surface behind the interactive Link Graph 2.0 panel:
/// build the filtered graph, validate a live drag (for red drop feedback), create a
/// link by dragging one node's output port to another, and break the link an edge
/// represents. Every mutation routes through <see cref="LinkService"/> so it is
/// validated against <see cref="LinkRules"/> and fully undo/redo-able. Framework-free
/// so the panel's gesture logic is unit-testable end to end.
/// </summary>
public sealed class LinkGraphEditor
{
    private readonly EditorDocument _doc;
    private readonly LinkService _links;

    public LinkGraphEditor(EditorDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _links = new LinkService(doc);
    }

    /// <summary>Builds the filtered graph for the current document state.</summary>
    public LinkGraph Build(LinkGraphFilter filter) => LinkGraphModel.Build(_doc, filter);

    /// <summary>
    /// Validates a proposed drag from <paramref name="originUid"/>'s output port to
    /// <paramref name="targetUid"/> without committing — the panel calls this while
    /// dragging to show accept (green) or refuse (red + reason) feedback.
    /// </summary>
    public LinkResult ValidateDrop(int originUid, int targetUid)
    {
        if (_doc.FindByUid(originUid) is not { } origin)
        {
            return LinkResult.Reject($"No object with UID {originUid}.");
        }

        if (_doc.FindByUid(targetUid) is not { } target)
        {
            return LinkResult.Reject($"No object with UID {targetUid}.");
        }

        return LinkRules.Validate(origin, target);
    }

    /// <summary>
    /// Creates the link <paramref name="originUid"/> → <paramref name="targetUid"/>
    /// (undo-able). Returns the validation result; on refusal nothing is committed
    /// and the message is the reason to surface as a toast.
    /// </summary>
    public LinkResult CreateLink(int originUid, int targetUid)
    {
        if (_doc.FindByUid(originUid) is not { } origin)
        {
            return LinkResult.Reject($"No object with UID {originUid}.");
        }

        return _links.AddLink(origin, targetUid);
    }

    /// <summary>Breaks the single link an edge represents (origin → target), undo-able.</summary>
    public bool BreakLink(int originUid, int targetUid)
    {
        if (_doc.FindByUid(originUid) is not { } origin)
        {
            return false;
        }

        return _links.RemoveLink(origin, targetUid);
    }
}
