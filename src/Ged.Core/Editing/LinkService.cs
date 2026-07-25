using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;

namespace Ged.Core.Editing;

/// <summary>
/// Undo-able link operations over the document's originator objects: one-to-many
/// link (K), reverse back-link (Ctrl+K), break-all (Shift+K), and the add/remove
/// primitives used by the links dialog (Ctrl+L). Every edit validates through
/// <see cref="LinkRules"/>, rejects duplicates/invalid pairs with a message, and
/// dirties only the affected originator sections.
/// </summary>
public sealed class LinkService
{
    private readonly EditorDocument _doc;

    public LinkService(EditorDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    /// <summary>K: link the primary object to each of the given targets (one-to-many).</summary>
    public LinkResult LinkOneToMany(LevelObject origin, IEnumerable<LevelObject> targets)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (LinkModel.LinksOf(origin) is not { } links)
        {
            return LinkResult.Reject(LinkRules.OriginatorMessage);
        }

        var toAdd = new List<int>();
        string? firstError = null;
        foreach (LevelObject t in targets)
        {
            LinkResult r = LinkRules.Validate(origin, t);
            if (r.Ok)
            {
                // A link to a mover brush must store the mover's START KEYFRAME uid — that is the uid
                // RF.exe resolves a trigger/event link to (see MoverLinkResolver). A link straight to
                // a keyframe or any non-mover object is returned unchanged.
                int stored = MoverLinkResolver.ResolveTarget(_doc, t.Uid);
                if (stored != origin.Uid && !toAdd.Contains(stored) && !links.Contains(stored))
                {
                    toAdd.Add(stored);
                }
            }
            else
            {
                firstError ??= r.Message;
            }
        }

        if (toAdd.Count == 0)
        {
            return LinkResult.Reject(firstError ?? "No valid link targets.");
        }

        var next = links.Concat(toAdd).ToList();
        Commit($"Link {toAdd.Count} object(s)", new() { (links, origin.Section, next) });
        return LinkResult.Allow();
    }

    /// <summary>Ctrl+K: reverse link — each selected target that can originate links back to the primary.</summary>
    public LinkResult BackLink(LevelObject primary, IEnumerable<LevelObject> targets)
    {
        ArgumentNullException.ThrowIfNull(primary);
        var edits = new List<(List<int>, RflSection, List<int>)>();
        string? firstError = null;
        foreach (LevelObject origin in targets)
        {
            if (LinkModel.LinksOf(origin) is not { } links)
            {
                firstError ??= LinkRules.OriginatorMessage;
                continue;
            }

            LinkResult r = LinkRules.Validate(origin, primary);
            if (r.Ok)
            {
                // Same mover-brush → start-keyframe redirect as the forward link (Ctrl+K links the
                // selected originators back TO the primary, which may be a mover).
                int stored = MoverLinkResolver.ResolveTarget(_doc, primary.Uid);
                if (stored != origin.Uid && !links.Contains(stored))
                {
                    edits.Add((links, origin.Section, links.Append(stored).ToList()));
                }
            }
            else
            {
                firstError ??= r.Message;
            }
        }

        if (edits.Count == 0)
        {
            return LinkResult.Reject(firstError ?? "No valid back-links.");
        }

        Commit($"Back-link {edits.Count} object(s)", edits);
        return LinkResult.Allow();
    }

    /// <summary>Shift+K: break all links touching any of the given objects (outgoing and incoming).</summary>
    public bool BreakAllLinks(IReadOnlyCollection<LevelObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var uids = objects.Select(o => o.Uid).ToHashSet();
        var edits = new List<(List<int>, RflSection, List<int>)>();
        foreach (LevelObject o in _doc.Objects)
        {
            if (LinkModel.LinksOf(o) is not { } links || links.Count == 0)
            {
                continue;
            }

            // Selected originators lose all links; every originator loses links pointing at a selected object.
            List<int> next = uids.Contains(o.Uid)
                ? new List<int>()
                : links.Where(u => !uids.Contains(u)).ToList();
            if (next.Count != links.Count)
            {
                edits.Add((links, o.Section, next));
            }
        }

        if (edits.Count == 0)
        {
            return false;
        }

        Commit("Break links", edits);
        return true;
    }

    /// <summary>Ctrl+L dialog: add one link by target UID (validated).</summary>
    public LinkResult AddLink(LevelObject origin, int targetUid)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (_doc.FindByUid(targetUid) is not { } target)
        {
            return LinkResult.Reject($"No object with UID {targetUid}.");
        }

        return LinkOneToMany(origin, new[] { target });
    }

    /// <summary>Ctrl+L dialog: remove one link by target UID.</summary>
    public bool RemoveLink(LevelObject origin, int targetUid)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (LinkModel.LinksOf(origin) is not { } links || !links.Contains(targetUid))
        {
            return false;
        }

        Commit("Remove link", new() { (links, origin.Section, links.Where(u => u != targetUid).ToList()) });
        return true;
    }

    private void Commit(string description, List<(List<int> List, RflSection Section, List<int> New)> edits)
    {
        var snap = edits
            .Select(e => (e.List, e.Section, Old: e.List.ToList(), e.New))
            .ToList();

        _doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                foreach (var s in snap)
                {
                    s.List.Clear();
                    s.List.AddRange(s.New);
                    s.Section.Dirty = true;
                }

                _doc.NotifyLinksChanged();
            },
            () =>
            {
                foreach (var s in snap)
                {
                    s.List.Clear();
                    s.List.AddRange(s.Old);
                    s.Section.Dirty = true;
                }

                _doc.NotifyLinksChanged();
            }));
    }
}
