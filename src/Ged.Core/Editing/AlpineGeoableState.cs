using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Bridges the Alpine <c>alpine_level_properties</c> geoable table (the on-disk source of truth,
/// a <c>brush_uid → room_uid</c> list) to the editor-visible per-brush geoable state (the
/// in-memory <see cref="BrushFlags.Geoable"/> bit that the Properties "Is Geoable" checkbox and
/// the Layers "G" badge read).
///
/// <para>Alpine's editor (editor_patch/main.cpp) keeps geoable ONLY in the table: the
/// "Is Geoable" checkbox is checked iff the selected brush's UID is in <c>geoable_brush_uids</c>,
/// and checking/unchecking pushes/erases that UID. GED mirrors that membership onto the
/// <see cref="BrushFlags.Geoable"/> bit for its flag-driven UI, but — matching RED/Alpine — never
/// persists that bit to the brush record (<see cref="Brush.Write"/> masks it out).</para>
///
/// <list type="bullet">
/// <item><see cref="SyncBrushFlagsFromTable"/> — on LOAD, sets the geoable bit on exactly the
/// brushes the table lists. Without this a brush that is geoable in the file (e.g. dmabrupt
/// brush 10992) shows unchecked in the editor.</item>
/// <item><see cref="ReconcileTableFromBrushFlags"/> — on SAVE, rewrites the table membership to
/// the current geoable-flagged live brushes (add newly-marked, drop unmarked/deleted), preserving
/// every surviving entry's room UID and order. Because LOAD populates the flags first, a brush
/// that is geoable in the file keeps its flag and is never silently dropped; only a deliberate
/// editor un-mark (or a brush deletion) removes an entry.</item>
/// </list>
/// </summary>
public static class AlpineGeoableState
{
    private const uint GeoableBit = (uint)BrushFlags.Geoable;

    /// <summary>Minimum RFL version that carries alpine_level_properties (Alpine v300).</summary>
    private const int AlpineMinVersion = 0x12C;

    /// <summary>
    /// Mirrors the geoable table onto each brush's <see cref="BrushFlags.Geoable"/> bit: sets the
    /// bit on every brush whose UID is in the table, clears it on every other brush. Idempotent,
    /// and does NOT mark the brush section dirty (the flag is a pure in-memory mirror — the brush
    /// record round-trips verbatim). No-op when the level has no geoable table.
    /// </summary>
    public static void SyncBrushFlagsFromTable(RflFile rfl)
    {
        System.ArgumentNullException.ThrowIfNull(rfl);

        BrushesSection? brushes = FindBrushes(rfl);
        if (brushes is null)
        {
            return;
        }

        AlpineLevelPropertiesSection? alp = FindAlpine(rfl, create: false, out _);
        var geoableUids = alp is null
            ? new HashSet<int>()
            : new HashSet<int>(alp.GeoableEntries.Select(e => e.BrushUid));

        foreach (Brush b in brushes.Brushes)
        {
            bool inTable = geoableUids.Contains(b.Uid);
            b.Flags = inTable ? b.Flags | GeoableBit : b.Flags & ~GeoableBit;
        }
    }

    /// <summary>
    /// Rewrites the geoable table to exactly the set of live brushes currently carrying the
    /// <see cref="BrushFlags.Geoable"/> bit, in brush order: surviving entries keep their room UID
    /// and relative position, newly-marked brushes are appended (room UID 0, filled by the next
    /// build), and entries for un-marked or deleted brushes are dropped. Marks the section dirty
    /// only when the membership actually changed, so an untouched level round-trips byte-identically.
    /// No-op on pre-Alpine levels, and creates the section only when a geoable brush needs it.
    /// </summary>
    public static void ReconcileTableFromBrushFlags(RflFile rfl)
    {
        System.ArgumentNullException.ThrowIfNull(rfl);

        BrushesSection? brushes = FindBrushes(rfl);
        if (brushes is null)
        {
            return;
        }

        // Live brushes carrying the geoable bit, in brush (build) order.
        var flagged = brushes.Brushes.Where(b => (b.Flags & GeoableBit) != 0).Select(b => b.Uid).ToList();

        AlpineLevelPropertiesSection? alp = FindAlpine(rfl, create: false, out RflSection? host);
        if (alp is null)
        {
            // Nothing marked geoable and no table → leave the file untouched.
            if (flagged.Count == 0 || rfl.Header.Version < AlpineMinVersion)
            {
                return;
            }

            // A geoable brush exists in an Alpine level that lacks the section — create it.
            alp = FindAlpine(rfl, create: true, out host);
            if (alp is null)
            {
                return;
            }
        }

        var flaggedSet = new HashSet<int>(flagged);
        var liveUids = new HashSet<int>(brushes.Brushes.Select(b => b.Uid));

        // Keep, in existing order (preserving room UID): every entry whose brush is still geoable-
        // flagged, PLUS every entry whose brush is not a live brush. The latter (stale entries for
        // deleted brushes) are left for the build's compute_geoable_room_uids to prune — leaving
        // them untouched here keeps an unedited load→save byte-identical. The only entries dropped
        // are those for a LIVE brush the user deliberately un-marked (flag cleared).
        var rebuilt = alp.GeoableEntries
            .Where(e => flaggedSet.Contains(e.BrushUid) || !liveUids.Contains(e.BrushUid))
            .ToList();
        var present = new HashSet<int>(rebuilt.Select(e => e.BrushUid));

        // Append newly-marked brushes (room UID resolved by the next build).
        foreach (int uid in flagged)
        {
            if (present.Add(uid))
            {
                rebuilt.Add(new AlpineGeoableEntry { BrushUid = uid, RoomUid = 0 });
            }
        }

        // Only touch the section (and dirty it) when the UID membership/order actually changed.
        if (rebuilt.Select(e => e.BrushUid).SequenceEqual(alp.GeoableEntries.Select(e => e.BrushUid)))
        {
            return;
        }

        alp.GeoableEntries = rebuilt;
        if (host is not null)
        {
            host.Dirty = true;
        }
    }

    private static BrushesSection? FindBrushes(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Brushes)
            {
                EnsureParsed(rfl, s);
                return s.Content as BrushesSection;
            }
        }

        return null;
    }

    private static AlpineLevelPropertiesSection? FindAlpine(RflFile rfl, bool create, out RflSection? host)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.AlpineLevelProperties)
            {
                EnsureParsed(rfl, s);
                host = s;
                return s.Content as AlpineLevelPropertiesSection;
            }
        }

        if (create)
        {
            host = rfl.GetOrCreateSection(
                SectionType.AlpineLevelProperties, () => new AlpineLevelPropertiesSection { Version = 4 });
            return host.Content as AlpineLevelPropertiesSection;
        }

        host = null;
        return null;
    }

    private static void EnsureParsed(RflFile rfl, RflSection section)
    {
        if (section.Content is null &&
            RflSectionRegistry.TryParse(section, rfl.Context, out IRflSectionContent? content))
        {
            section.Content = content;
        }
    }
}
