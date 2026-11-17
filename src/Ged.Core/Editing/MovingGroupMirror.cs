using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Keeps every mover listed in the editor <c>groups</c> section (0x3000000) as well as in
/// <c>moving_groups</c> (0x3000), exactly as RED writes them. Without the editor copy RED does not see
/// the mover at all: it rebuilds the level with the mover's brushes folded into the static world.
///
/// <para>
/// <b>The recovered rule (RED.exe).</b> RED's level loader <c>FUN_0042f030</c> (@0x0042f030) has a case for
/// the <c>brushes</c> section 0x2000000 (@0x004306fc..) and the <c>groups</c> section 0x3000000
/// (cmp @0x00430799) but none for <c>movers</c> 0x2000 or <c>moving_groups</c> 0x3000: both fall to the
/// default branch, which skips the section body (LAB_004307ad). The editor's moving groups
/// (<c>CDedLevel::moving_groups</c>, user-defined and moving alike) come ONLY from the 0x3000000 records,
/// read by <c>FUN_00451fe0</c> (@0x00451fe0): name, flags, the moving data when the moving byte is set, the
/// object UIDs and the brush UIDs, each brush resolved against the brushes already loaded
/// (<c>BrushList_find_by_uid</c>). A brush is a mover's brush exactly when one of those groups lists it,
/// which is what the build tests (<c>FUN_0042a630</c>) before it folds a brush into the static solid.
/// RED's saver <c>FUN_00430bf0</c> (@0x00430bf0) writes the 0x3000 section from the moving subset of that
/// list (push 0x3000 @0x00431736) and then, with the editor data, the 0x3000000 section from the WHOLE list
/// (push 0x3000000 @0x00431c65), both through the same group writer <c>FUN_00451fe0</c>. So in every
/// RED-authored level the moving entries of 0x3000000, in order, are byte-identical to the 0x3000 records
/// (verified on all 130 mover levels of the corpus, stock and community, v180 to v306).
/// </para>
///
/// <para>
/// Glacier edits movers through <c>moving_groups</c> (<see cref="MoverService"/>), the section the game
/// reads, so that list is the source of truth and this class derives the editor copy from it:
/// <see cref="Link"/> shares the identical instances when a document opens (nothing is written), and
/// <see cref="Reconcile"/> rewrites the editor copy when it no longer mirrors <c>moving_groups</c> (every
/// save, and the load-time repair of levels saved before this rule was known). A RED-authored level already
/// mirrors, so it is never changed and stays byte-identical.
/// </para>
/// </summary>
public static class MovingGroupMirror
{
    /// <summary>
    /// Makes each moving entry of the editor <c>groups</c> section that is byte-identical to a
    /// <c>moving_groups</c> record the SAME <see cref="Group"/> instance, so an edit to the mover is an edit
    /// to both. Changes no bytes and marks nothing dirty. Called when a document opens.
    /// </summary>
    public static void Link(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        (GroupsSection? moving, _) = Find(rfl, SectionType.MovingGroups);
        (GroupsSection? editor, _) = Find(rfl, SectionType.Groups);
        if (moving is null || editor is null || moving.Groups.Count == 0)
        {
            return;
        }

        RflContext ctx = rfl.Context;
        var unused = moving.Groups.Select(g => (Group: g, Bytes: Serialize(g, ctx))).ToList();
        for (int i = 0; i < editor.Groups.Count && unused.Count > 0; i++)
        {
            Group e = editor.Groups[i];
            if (e.IsMoving == 0)
            {
                continue;
            }

            int hit = unused.FindIndex(u => ReferenceEquals(u.Group, e));
            if (hit < 0)
            {
                byte[] bytes = Serialize(e, ctx);
                hit = unused.FindIndex(u => u.Bytes.AsSpan().SequenceEqual(bytes));
            }

            if (hit >= 0)
            {
                editor.Groups[i] = unused[hit].Group;
                unused.RemoveAt(hit);
            }
        }
    }

    /// <summary>
    /// Rewrites the editor <c>groups</c> section so its moving entries are exactly the <c>moving_groups</c>
    /// records, in that order, as RED writes them: user-defined groups keep their places; each moving entry
    /// that still corresponds to a mover (same instance, identical bytes, a shared keyframe UID, or the same
    /// name, in that preference) takes the current record; entries of movers that no longer exist are
    /// dropped; movers the editor list lacks are appended (where RED appends a new group). The section is
    /// created after <c>brushes</c> when a level has movers but no editor groups. Nothing is marked dirty
    /// when the result serializes to the bytes already stored, so a RED-authored level stays byte-identical.
    /// Returns the number of movers whose editor entry was added, rewritten or removed.
    /// </summary>
    public static int Reconcile(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        rfl.ParseAllKnownSections();
        (GroupsSection? moving, _) = Find(rfl, SectionType.MovingGroups);
        (GroupsSection? editor, RflSection? editorHost) = Find(rfl, SectionType.Groups);
        List<Group> movers = moving?.Groups ?? new List<Group>();
        if (editor is null || editorHost is null)
        {
            if (movers.Count == 0)
            {
                return 0;
            }

            editorHost = rfl.GetOrCreateSection(SectionType.Groups, () => new GroupsSection(SectionType.Groups));
            editor = (GroupsSection)editorHost.Content!;
        }

        RflContext ctx = rfl.Context;
        List<Group> desired = Desired(editor.Groups, movers, ctx, out int changed);

        if (!desired.SequenceEqual(editor.Groups))
        {
            editor.Groups.Clear();
            editor.Groups.AddRange(desired);
        }

        // RED writes the groups section only when it has groups (FUN_00430bf0, the count test before
        // push 0x3000000 @0x00431c65), so a section this pass emptied goes away.
        if (changed > 0 && editor.Groups.Count == 0)
        {
            rfl.Sections.Remove(editorHost);
            return changed;
        }

        if (editorHost.Dirty)
        {
            return changed;
        }

        var writer = new RfWriter(editorHost.RawBytes.Length);
        editor.Write(writer, ctx);
        if (!writer.ToArray().AsSpan().SequenceEqual(editorHost.RawBytes))
        {
            editorHost.Dirty = true;
        }

        return changed;
    }

    /// <summary>
    /// True when the editor <c>groups</c> section is missing a mover or holds a stale or extra one, i.e.
    /// <see cref="Reconcile"/> would change the file. Read-only.
    /// </summary>
    public static bool NeedsRepair(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        rfl.ParseAllKnownSections();
        (GroupsSection? moving, _) = Find(rfl, SectionType.MovingGroups);
        (GroupsSection? editor, _) = Find(rfl, SectionType.Groups);
        List<Group> movers = moving?.Groups ?? new List<Group>();
        List<Group> editorMoving = editor?.Groups.Where(g => g.IsMoving != 0).ToList() ?? new List<Group>();
        if (movers.Count != editorMoving.Count)
        {
            return true;
        }

        RflContext ctx = rfl.Context;
        for (int i = 0; i < movers.Count; i++)
        {
            if (!ReferenceEquals(movers[i], editorMoving[i]) &&
                !Serialize(movers[i], ctx).AsSpan().SequenceEqual(Serialize(editorMoving[i], ctx)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="section"/> is the editor <c>groups</c> section of a level that has a
    /// <c>moving_groups</c> section: its moving entries are then the editor copies of those records (see
    /// <see cref="Link"/>), so views that walk every groups section skip them and each mover's keyframes are
    /// projected, drawn and picked once, from <c>moving_groups</c>.
    /// </summary>
    public static bool IsEditorCopy(RflFile rfl, RflSection section)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(section);
        return section.TypeId == (uint)SectionType.Groups && rfl.Sections.Exists(s => s.TypeId == (uint)SectionType.MovingGroups);
    }

    private static List<Group> Desired(List<Group> editorGroups, List<Group> movers, RflContext ctx, out int changed)
    {
        changed = 0;
        var moverBytes = movers.Select(m => Serialize(m, ctx)).ToList();
        var used = new bool[movers.Count];

        // 1) Assign each moving entry of the editor list to the mover it stands for, best evidence first.
        var editorMovingIndices = Enumerable.Range(0, editorGroups.Count).Where(i => editorGroups[i].IsMoving != 0).ToList();
        var assignment = new Dictionary<int, int>();
        var entryBytes = editorMovingIndices.ToDictionary(i => i, i => Serialize(editorGroups[i], ctx));
        Func<int, int, bool>[] rules =
        {
            (e, m) => ReferenceEquals(editorGroups[e], movers[m]),
            (e, m) => entryBytes[e].AsSpan().SequenceEqual(moverBytes[m]),
            (e, m) => SharesKeyframe(editorGroups[e], movers[m]),
            (e, m) => string.Equals(editorGroups[e].Name, movers[m].Name, StringComparison.Ordinal),
        };
        foreach (Func<int, int, bool> rule in rules)
        {
            foreach (int e in editorMovingIndices)
            {
                if (assignment.ContainsKey(e))
                {
                    continue;
                }

                for (int m = 0; m < movers.Count; m++)
                {
                    if (!used[m] && rule(e, m))
                    {
                        assignment[e] = m;
                        used[m] = true;
                        break;
                    }
                }
            }
        }

        // 2) User groups keep their places; so does each moving entry that still stands for a mover. An entry
        //    whose mover no longer exists is dropped.
        var result = new List<Group>(editorGroups.Count + movers.Count);
        var slots = new List<(int Position, byte[] Before)>();
        for (int i = 0; i < editorGroups.Count; i++)
        {
            Group g = editorGroups[i];
            if (g.IsMoving == 0)
            {
                result.Add(g);
            }
            else if (assignment.ContainsKey(i))
            {
                slots.Add((result.Count, entryBytes[i]));
                result.Add(g);
            }
            else
            {
                changed++;
            }
        }

        // 3) The moving entries are the moving_groups records in moving_groups order (RED writes 0x3000 as the
        //    ordered moving subset of this same list): fill the kept slots in that order and append the
        //    movers left over, where RED appends a new group.
        for (int m = 0; m < movers.Count; m++)
        {
            if (m < slots.Count)
            {
                result[slots[m].Position] = movers[m];
                if (!slots[m].Before.AsSpan().SequenceEqual(moverBytes[m]))
                {
                    changed++;
                }
            }
            else
            {
                result.Add(movers[m]);
                changed++;
            }
        }

        return result;
    }

    private static bool SharesKeyframe(Group a, Group b)
    {
        if (a.MovingData is not { } da || b.MovingData is not { } db)
        {
            return false;
        }

        var uids = new HashSet<int>(da.Keyframes.Select(k => k.Uid));
        return db.Keyframes.Any(k => uids.Contains(k.Uid));
    }

    private static byte[] Serialize(Group g, RflContext ctx)
    {
        var w = new RfWriter(256);
        new GroupsSection(SectionType.Groups) { Groups = new List<Group> { g } }.Write(w, ctx);
        return w.ToArray();
    }

    private static (GroupsSection? Content, RflSection? Host) Find(RflFile rfl, SectionType type)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)type)
            {
                if (s.Content is null)
                {
                    rfl.ParseAllKnownSections();
                }

                return s.Content is GroupsSection g ? (g, s) : (null, null);
            }
        }

        return (null, null);
    }
}
