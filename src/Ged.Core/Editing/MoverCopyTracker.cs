using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The document-level record of WHEN each mover's <c>movers</c>-section copy was taken. A mover is
/// stored twice — its member brush in <c>brushes</c> (what every editing path changes) and a copy in
/// <c>movers</c> (what the build bakes and the game animates) — so an edited member leaves its copy
/// stale. For every mover member brush this keeps a snapshot of the member as it was when its copy
/// was last taken: when the document was opened, when the mover was created, or when the copy was
/// last synced. A member is <b>touched</b> when it no longer matches that snapshot in anything an
/// author edits (transform, flags, life, vertices, face corners, UVs, textures, face flags).
/// <para>
/// The check is state-based rather than an edit log, so every path that changes a brush — viewport
/// gizmos, vertex/face/UV edits, scripts, the automation bridge, prefab propagation — is covered
/// without each one reporting in, and an edit that is undone (or dragged back exactly) is untouched
/// again, so its mover keeps its original bytes. A mover whose members were never touched is never
/// synced: stock levels opened and saved stay byte-identical even where their copies differ from
/// their members. Snapshots change only inside undoable commands (<see cref="MoverService.CreateMover"/>,
/// <see cref="MoverService.SyncMemberCopies"/>), so undo and redo keep them consistent with the copies.
/// </para>
/// </summary>
public sealed class MoverCopyTracker
{
    private readonly Dictionary<int, Brush> _taken = new();

    /// <summary>The number of member brushes being tracked.</summary>
    public int TrackedCount => _taken.Count;

    /// <summary>
    /// Snapshots every moving-group member brush (present in the <c>brushes</c> section) that has no
    /// snapshot yet — called when a document is opened and after the stored-twice repair. Snapshots
    /// already held are kept.
    /// </summary>
    public void TrackUntracked(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        BrushesSection? brushes = null;
        var members = new HashSet<int>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                brushes ??= bs;
            }
            else if (s.TypeId == (uint)SectionType.MovingGroups && s.Content is GroupsSection gs)
            {
                foreach (Group g in gs.Groups.Where(g => g.IsMoving != 0))
                {
                    members.UnionWith(g.Brushes);
                }
            }
        }

        if (brushes is null || members.Count == 0)
        {
            return;
        }

        foreach (Brush b in brushes.Brushes)
        {
            if (members.Contains(b.Uid) && !_taken.ContainsKey(b.Uid))
            {
                _taken[b.Uid] = GeometryClone.Deep(b);
            }
        }
    }

    /// <summary>True when <paramref name="member"/> changed since its mover copy was taken.</summary>
    public bool IsTouched(Brush member) =>
        _taken.TryGetValue(member.Uid, out Brush? taken) && !MoverService.SameContent(taken, member);

    /// <summary>The snapshot held for <paramref name="uid"/>, or null.</summary>
    internal Brush? Snapshot(int uid) => _taken.GetValueOrDefault(uid);

    /// <summary>Sets (or, with null, clears) the snapshot for <paramref name="uid"/>. Called only from inside undoable commands.</summary>
    internal void SetSnapshot(int uid, Brush? snapshot)
    {
        if (snapshot is null)
        {
            _taken.Remove(uid);
        }
        else
        {
            _taken[uid] = snapshot;
        }
    }
}
