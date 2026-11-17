using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The Alpine Faction 1.5 settings that hang off stock objects by UID rather than living in the object's own record:
/// mover paths (chunk 0x0AFBAE0E — per-keyframe curve settings keyed by keyframe UID, per-mover settings keyed by the
/// group's first keyframe UID; common/include/common/mover_path.h:51-66, 124-151) and liquid emission (chunk
/// 0x0AFBAE0F — the glow of a Liquid Room room effect, keyed by its UID; editor_patch/liquid_emission.h:18-31).
///
/// <para>
/// Reads return the stored record or Alpine's defaults. Every edit is one undoable command that creates the chunk on
/// first use (and removes it again on undo) and stores only non-default records, the way Alpine RED saves them
/// (mover_path.cpp:1413-1440 writes a keyframe or mover only when <c>is_default</c> is false; liquid_emission.cpp:394-410
/// writes a room effect only when its emission differs from the defaults). A chunk Glacier keeps raw
/// (<see cref="IRetainedChunk"/>) refuses edits. Unmodified chunks are never rewritten, so a loaded level re-saves
/// byte for byte.
/// </para>
/// </summary>
public static class AlpineAttachedData
{
    /// <summary>Alpine's handle clamp (mover_path.h:19, sanitize_handle).</summary>
    public const float MaxHandle = 100000f;

    // ---- Lookup -------------------------------------------------------------------

    /// <summary>The level's mover-path chunk content, or null.</summary>
    public static AlpineMoverPathsSection? MoverPaths(RflFile rfl) => First<AlpineMoverPathsSection>(rfl);

    /// <summary>The level's liquid-emission chunk content, or null.</summary>
    public static AlpineLiquidEmissionSection? LiquidEmission(RflFile rfl) => First<AlpineLiquidEmissionSection>(rfl);

    /// <summary>The curve settings of keyframe <paramref name="keyframeUid"/>: a copy of the stored record, or the defaults.</summary>
    public static AlpineMoverPathKeyframe KeyframeData(RflFile rfl, int keyframeUid)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        AlpineMoverPathKeyframe? stored = MoverPaths(rfl)?.Keyframes.LastOrDefault(k => k.KeyframeUid == keyframeUid);
        return stored is null ? new AlpineMoverPathKeyframe { KeyframeUid = keyframeUid } : Clone(stored);
    }

    /// <summary>The mover-wide path settings of a moving group (keyed by its first keyframe), or the defaults.</summary>
    public static AlpineMoverPathMover MoverSettings(RflFile rfl, Group group)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(group);
        int key = FirstKeyframeUid(group);
        AlpineMoverPathMover? stored = key < 0 ? null : MoverPaths(rfl)?.Movers.LastOrDefault(m => m.FirstKeyframeUid == key);
        return stored is null ? new AlpineMoverPathMover { FirstKeyframeUid = key } : Clone(stored);
    }

    /// <summary>The liquid emission of room effect <paramref name="roomEffectUid"/>: a copy of the stored record, or the defaults.</summary>
    public static AlpineLiquidEmission Emission(RflFile rfl, int roomEffectUid)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        AlpineLiquidEmission? stored = LiquidEmission(rfl)?.Records.LastOrDefault(e => e.RoomEffectUid == roomEffectUid);
        return stored is null ? new AlpineLiquidEmission { RoomEffectUid = roomEffectUid } : Clone(stored);
    }

    /// <summary>
    /// The UID Alpine keys a moving group's path settings by: its first keyframe (Alpine's first ACTIVE keyframe,
    /// mover_path.cpp:110-120; every Glacier keyframe is active). -1 for a group without keyframes.
    /// </summary>
    public static int FirstKeyframeUid(Group group) =>
        group.MovingData is { Keyframes.Count: > 0 } data ? data.Keyframes[0].Uid : -1;

    /// <summary>Alpine's <c>is_default(KeyframeData)</c> (mover_path.h:146-151): the handles do not count.</summary>
    public static bool IsDefault(AlpineMoverPathKeyframe k) =>
        k.SegmentMode == 0 && k.TangentMode == 0 && k.Ease == 0 && k.SegmentOrient == 0;

    /// <summary>Alpine's <c>is_default(MoverSettings)</c> (mover_path.h:63-66).</summary>
    public static bool IsDefault(AlpineMoverPathMover m) => m.OrientMode == 0 && m.RiderFlags == 0;

    /// <summary>A liquid emission equal to Alpine's <c>LiquidEmission{}</c> (liquid_emission.h:25-33).</summary>
    public static bool IsDefault(AlpineLiquidEmission e) =>
        e.Mode == 0 && e.ColorR == 255 && e.ColorG == 96 && e.ColorB == 32
        && BitConverter.SingleToInt32Bits(e.Intensity) == BitConverter.SingleToInt32Bits(1f)
        && BitConverter.SingleToInt32Bits(e.Distance) == BitConverter.SingleToInt32Bits(8f);

    /// <summary>Alpine's handle sanitiser: non-finite components become 0, the rest clamp to ±100000 (mover_path.h:153-161).</summary>
    public static Vec3 SanitizeHandle(Vec3 v) => new(Axis(v.X), Axis(v.Y), Axis(v.Z));

    // ---- Edits --------------------------------------------------------------------

    /// <summary>
    /// Changes keyframe <paramref name="keyframeUid"/>'s curve settings as one undoable command. A result equal to the
    /// current settings is a no-op; a result Alpine treats as default removes the record.
    /// </summary>
    /// <exception cref="InvalidOperationException">The level's mover-path chunk is kept raw.</exception>
    public static void EditKeyframe(EditorDocument doc, int keyframeUid, string description, Func<AlpineMoverPathKeyframe, AlpineMoverPathKeyframe> change)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(change);
        AlpineMoverPathKeyframe before = KeyframeData(doc.Rfl, keyframeUid);
        AlpineMoverPathKeyframe after = change(Clone(before));
        after.KeyframeUid = keyframeUid;
        after.InHandle = SanitizeHandle(after.InHandle);
        after.OutHandle = SanitizeHandle(after.OutHandle);
        if (Same(before, after))
        {
            return;
        }

        EditMoverPaths(doc, description, s => Replace(s.Keyframes, k => k.KeyframeUid == keyframeUid, IsDefault(after) ? null : after));
    }

    /// <summary>Changes a moving group's path settings (orient mode, rider flags) as one undoable command.</summary>
    /// <exception cref="InvalidOperationException">The group has no keyframes, or the mover-path chunk is kept raw.</exception>
    public static void EditMover(EditorDocument doc, Group group, string description, Func<AlpineMoverPathMover, AlpineMoverPathMover> change)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(change);
        int key = FirstKeyframeUid(group);
        if (key < 0)
        {
            throw new InvalidOperationException($"Mover '{group.Name}' has no keyframes, so it has no path settings.");
        }

        AlpineMoverPathMover before = MoverSettings(doc.Rfl, group);
        AlpineMoverPathMover after = change(Clone(before));
        after.FirstKeyframeUid = key;
        after.Padding = 0; // Alpine writes the two reserved bytes as 0 (mover_path.cpp:242-244)
        if (before.OrientMode == after.OrientMode && before.RiderFlags == after.RiderFlags)
        {
            return;
        }

        EditMoverPaths(doc, description, s => Replace(s.Movers, m => m.FirstKeyframeUid == key, IsDefault(after) ? null : after));
    }

    /// <summary>Changes room effect <paramref name="roomEffectUid"/>'s liquid emission as one undoable command.</summary>
    /// <exception cref="InvalidOperationException">The level's liquid-emission chunk is kept raw.</exception>
    public static void EditEmission(EditorDocument doc, int roomEffectUid, string description, Func<AlpineLiquidEmission, AlpineLiquidEmission> change)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(change);
        AlpineLiquidEmission before = Emission(doc.Rfl, roomEffectUid);
        AlpineLiquidEmission after = change(Clone(before));
        after.RoomEffectUid = roomEffectUid;
        if (Same(before, after))
        {
            return;
        }

        EditChunk(doc, SectionType.AlpineLiquidEmission, () => new AlpineLiquidEmissionSection(), description,
            (AlpineLiquidEmissionSection s) => Replace(s.Records, e => e.RoomEffectUid == roomEffectUid, IsDefault(after) ? null : after),
            s => s.Records.Count == 0);
    }

    // ---- Maintenance (folded into the document's delete / paste commands) -----------

    /// <summary>
    /// The apply/revert pair that drops the mover-path and liquid-emission records of deleted objects (Alpine saves
    /// records only for keyframes, movers and room effects that exist, mover_path.cpp:1413-1440,
    /// liquid_emission.cpp:394-410), or null when no record names a deleted UID or the chunks are kept raw.
    /// </summary>
    internal static (Action Apply, Action Revert)? PruneDeleted(RflFile rfl, IReadOnlyCollection<int> deletedUids)
    {
        var gone = new HashSet<int>(deletedUids);
        var edits = new List<(Action Apply, Action Revert)>();
        if (FirstHost<AlpineMoverPathsSection>(rfl) is { } mp && mp.Content is AlpineMoverPathsSection paths && !paths.IsReadOnly
            && (paths.Movers.Any(m => gone.Contains(m.FirstKeyframeUid)) || paths.Keyframes.Any(k => gone.Contains(k.KeyframeUid))))
        {
            AlpineMoverPathMover[] moversBefore = paths.Movers.ToArray();
            AlpineMoverPathKeyframe[] keysBefore = paths.Keyframes.ToArray();
            edits.Add((
                () =>
                {
                    paths.Movers.RemoveAll(m => gone.Contains(m.FirstKeyframeUid));
                    paths.Keyframes.RemoveAll(k => gone.Contains(k.KeyframeUid));
                    mp.Dirty = true;
                },
                () =>
                {
                    Restore(paths.Movers, moversBefore);
                    Restore(paths.Keyframes, keysBefore);
                    mp.Dirty = true;
                }));
        }

        if (FirstHost<AlpineLiquidEmissionSection>(rfl) is { } le && le.Content is AlpineLiquidEmissionSection glow && !glow.IsReadOnly
            && glow.Records.Any(e => gone.Contains(e.RoomEffectUid)))
        {
            AlpineLiquidEmission[] before = glow.Records.ToArray();
            edits.Add((
                () =>
                {
                    glow.Records.RemoveAll(e => gone.Contains(e.RoomEffectUid));
                    le.Dirty = true;
                },
                () =>
                {
                    Restore(glow.Records, before);
                    le.Dirty = true;
                }));
        }

        return Combine(edits);
    }

    /// <summary>
    /// The apply/revert pair that gives each pasted room effect its source's liquid emission (Alpine copies it with
    /// every room-effect clone, liquid_emission.cpp:73-83), or null when no source has one.
    /// </summary>
    internal static (Action Apply, Action Revert)? CopyEmissions(RflFile rfl, IReadOnlyList<(int From, int To)> pasted)
    {
        if (LiquidEmission(rfl) is not { IsReadOnly: false } glow)
        {
            return null;
        }

        var copies = new List<AlpineLiquidEmission>();
        foreach ((int from, int to) in pasted)
        {
            if (glow.Records.LastOrDefault(e => e.RoomEffectUid == from) is { } src)
            {
                AlpineLiquidEmission copy = Clone(src);
                copy.RoomEffectUid = to;
                copies.Add(copy);
            }
        }

        if (copies.Count == 0)
        {
            return null;
        }

        RflSection host = FirstHost<AlpineLiquidEmissionSection>(rfl)!;
        return (
            () =>
            {
                glow.Records.AddRange(copies);
                host.Dirty = true;
            },
            () =>
            {
                foreach (AlpineLiquidEmission c in copies)
                {
                    glow.Records.Remove(c);
                }

                host.Dirty = true;
            });
    }

    // ---- Helpers ------------------------------------------------------------------

    private static void EditMoverPaths(EditorDocument doc, string description, Action<AlpineMoverPathsSection> mutate) =>
        EditChunk(doc, SectionType.AlpineMoverPaths, () => new AlpineMoverPathsSection(), description, mutate,
            s => s.Movers.Count == 0 && s.Keyframes.Count == 0);

    /// <summary>
    /// One undoable edit of an attached-data chunk: creates the chunk inside the command when the level lacks it (and
    /// removes it on undo), refuses a chunk kept raw, and restores the exact prior record lists on undo.
    /// </summary>
    private static void EditChunk<TSection>(
        EditorDocument doc, SectionType type, Func<TSection> create, string description, Action<TSection> mutate, Func<TSection, bool> empty)
        where TSection : class, IRflSectionContent, IRetainedChunk
    {
        RflSection host = doc.Rfl.FindOrPrepareSection(type, () => create(), out bool created);
        if (host.Content is not TSection content)
        {
            throw new InvalidOperationException($"This level's {SectionIds.Describe((uint)type)} chunk could not be read, so it cannot be edited.");
        }

        if (content.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"This level's {SectionIds.Describe((uint)type)} chunk is kept unchanged ({content.ReadOnlyReason}), so its settings cannot be edited.");
        }

        Func<object[]> capture = () => Capture(content);
        object[] before = capture();
        mutate(content);
        object[] after = capture();
        Apply(content, before);

        bool removeWhenEmpty = created;
        doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                if (created && !doc.Rfl.Sections.Contains(host))
                {
                    doc.Rfl.InsertSection(host);
                }

                Apply(content, after);
                host.Dirty = true;
            },
            () =>
            {
                Apply(content, before);
                host.Dirty = true;
                if (removeWhenEmpty && empty(content))
                {
                    doc.Rfl.Sections.Remove(host);
                }
            }));
    }

    private static object[] Capture(IRflSectionContent content) => content switch
    {
        AlpineMoverPathsSection p => new object[] { p.Movers.ToArray(), p.Keyframes.ToArray() },
        AlpineLiquidEmissionSection e => new object[] { e.Records.ToArray() },
        _ => Array.Empty<object>(),
    };

    private static void Apply(IRflSectionContent content, object[] image)
    {
        switch (content)
        {
            case AlpineMoverPathsSection p:
                Restore(p.Movers, (AlpineMoverPathMover[])image[0]);
                Restore(p.Keyframes, (AlpineMoverPathKeyframe[])image[1]);
                break;
            case AlpineLiquidEmissionSection e:
                Restore(e.Records, (AlpineLiquidEmission[])image[0]);
                break;
        }
    }

    /// <summary>Replaces the last record matching <paramref name="match"/> (or appends), or removes it when <paramref name="next"/> is null.</summary>
    private static void Replace<T>(List<T> list, Predicate<T> match, T? next)
        where T : class
    {
        int index = list.FindLastIndex(match);
        if (next is null)
        {
            list.RemoveAll(match);
            return;
        }

        if (index >= 0)
        {
            list[index] = next;
        }
        else
        {
            list.Add(next);
        }
    }

    private static void Restore<T>(List<T> list, T[] image)
    {
        list.Clear();
        list.AddRange(image);
    }

    private static (Action Apply, Action Revert)? Combine(List<(Action Apply, Action Revert)> edits) =>
        edits.Count == 0
            ? null
            : (() => edits.ForEach(e => e.Apply()), () => Enumerable.Reverse(edits).ToList().ForEach(e => e.Revert()));

    private static float Axis(float v) => float.IsFinite(v) ? Math.Clamp(v, -MaxHandle, MaxHandle) : 0f;

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool Same(Vec3 a, Vec3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

    private static bool Same(AlpineMoverPathKeyframe a, AlpineMoverPathKeyframe b) =>
        a.SegmentMode == b.SegmentMode && a.TangentMode == b.TangentMode && a.Ease == b.Ease && a.SegmentOrient == b.SegmentOrient
        && Same(a.InHandle, b.InHandle) && Same(a.OutHandle, b.OutHandle);

    private static bool Same(AlpineLiquidEmission a, AlpineLiquidEmission b) =>
        a.Mode == b.Mode && a.ColorR == b.ColorR && a.ColorG == b.ColorG && a.ColorB == b.ColorB
        && Same(a.Intensity, b.Intensity) && Same(a.Distance, b.Distance);

    internal static AlpineMoverPathKeyframe Clone(AlpineMoverPathKeyframe k) => new()
    {
        KeyframeUid = k.KeyframeUid,
        SegmentMode = k.SegmentMode,
        TangentMode = k.TangentMode,
        Ease = k.Ease,
        SegmentOrient = k.SegmentOrient,
        InHandle = k.InHandle,
        OutHandle = k.OutHandle,
    };

    private static AlpineMoverPathMover Clone(AlpineMoverPathMover m) => new()
    {
        FirstKeyframeUid = m.FirstKeyframeUid,
        OrientMode = m.OrientMode,
        RiderFlags = m.RiderFlags,
        Padding = m.Padding,
    };

    private static AlpineLiquidEmission Clone(AlpineLiquidEmission e) => new()
    {
        RoomEffectUid = e.RoomEffectUid,
        Mode = e.Mode,
        ColorR = e.ColorR,
        ColorG = e.ColorG,
        ColorB = e.ColorB,
        Intensity = e.Intensity,
        Distance = e.Distance,
    };

    private static T? First<T>(RflFile rfl)
        where T : class, IRflSectionContent =>
        FirstHost<T>(rfl)?.Content as T;

    private static RflSection? FirstHost<T>(RflFile rfl)
        where T : class, IRflSectionContent
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections.FirstOrDefault(s => s.Content is T);
    }
}
