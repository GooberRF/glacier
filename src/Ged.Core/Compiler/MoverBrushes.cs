using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Identifies brushes that belong to a mover (a moving group / keyframe group) so the geometry
/// compiler can EXCLUDE them from the static world fold — the same thing RED.exe does.
/// <para>
/// A mover brush lives in the RFL twice: once in the <c>movers</c> section (0x2000, the list
/// RF.exe animates at runtime) and once in the main <c>brushes</c> section (0x2000000, kept for
/// re-editing in RED), referenced by an <c>is_moving</c> group in <c>moving_groups</c>/<c>groups</c>.
/// If the compiler folds the brushes-section copy into <c>static_geometry</c>, RF.exe then renders
/// BOTH the immovable static copy AND the animated mover — the reported "the original stays in place
/// with black lighting while the mover animates" duplicate. RED excludes mover-owned brushes from the
/// static CSG; the mover data round-trips untouched and RF drives it from the movers section.
/// </para>
/// </summary>
public static class MoverBrushes
{
    /// <summary>
    /// Collects every brush UID owned by a mover: the union of the <c>movers</c> section UIDs and the
    /// brush members of every <c>is_moving</c> group in the groups / moving_groups sections.
    /// </summary>
    public static HashSet<int> CollectMoverUids(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        var uids = new HashSet<int>();
        foreach (RflSection s in rfl.Sections)
        {
            switch (s.Content)
            {
                case MoversSection movers:
                    foreach (Brush m in movers.Movers)
                    {
                        uids.Add(m.Uid);
                    }

                    break;
                case GroupsSection groups:
                    foreach (Group g in groups.Groups)
                    {
                        if (g.IsMoving != 0)
                        {
                            foreach (int uid in g.Brushes)
                            {
                                uids.Add(uid);
                            }
                        }
                    }

                    break;
            }
        }

        return uids;
    }

    /// <summary>Returns <paramref name="brushes"/> with every mover-owned brush removed.</summary>
    public static List<Brush> ExcludeMovers(IReadOnlyList<Brush> brushes, IReadOnlyCollection<int> moverUids)
    {
        if (moverUids.Count == 0)
        {
            return new List<Brush>(brushes);
        }

        var kept = new List<Brush>(brushes.Count);
        foreach (Brush b in brushes)
        {
            if (!moverUids.Contains(b.Uid))
            {
                kept.Add(b);
            }
        }

        return kept;
    }

    /// <summary>
    /// The static-world brush list for <paramref name="rfl"/>: the brushes section minus every
    /// mover-owned brush. This is the exact input RED folds into <c>static_geometry</c>.
    /// </summary>
    public static List<Brush> StaticWorldBrushes(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        List<Brush> brushes = FindBrushes(rfl);
        return ExcludeMovers(brushes, CollectMoverUids(rfl));
    }

    /// <summary>
    /// Renumbers every mover brush's geometry faces to a <c>FaceId</c> range disjoint from (above) the
    /// static geometry's maximum face id, restoring RED's invariant that every compiled face — static
    /// world AND mover — carries a globally-UNIQUE id. RED-authored dmabrupt proves it: not one of its
    /// mover faces shares an id with a static face (they interleave a single global counter across the
    /// whole level). GED compiles only the static world (movers are excluded from the fold, see
    /// <see cref="StaticWorldBrushes"/>), so a mover otherwise keeps its authored local ids 0..n, which
    /// collide head-on with the static world's own 0..n — a GED-authored one-brush lift ships mover
    /// faceIds 0..5 identical to the room's 0..5. Returns true when any id changed (so the caller
    /// re-serialises the movers section). Idempotent: a level whose movers are already numbered above the
    /// static max renumbers to the same ids and reports no change.
    /// </summary>
    public static bool AssignGlobalFaceIds(IReadOnlyList<Brush> movers, Geometry staticGeometry)
    {
        int cursor = -1;
        foreach (Face f in staticGeometry.Faces)
        {
            if (f.FaceId > cursor)
            {
                cursor = f.FaceId;
            }
        }

        cursor++; // first free id above the static world's max
        bool changed = false;
        foreach (Brush m in movers)
        {
            foreach (Face f in m.Geometry.Faces)
            {
                if (f.FaceId != cursor)
                {
                    f.FaceId = cursor;
                    changed = true;
                }

                cursor++;
            }
        }

        return changed;
    }

    private static List<Brush> FindBrushes(RflFile rfl)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                return bs.Brushes;
            }
        }

        return new List<Brush>();
    }
}
