using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Preserves AUTHORED per-room state across a rebuild. <c>is_airlock</c> (+0x43) is set by RED's build
/// ONLY from a room effect that carries the flag (GeoBuild_Driver 0x43a26a: effect+0x9e → room+0x43, one
/// room per effect) — yet levels like dmabrupt ship 17 airlock rooms with ZERO airlock effects: the flags
/// are authored through RED's room-property UI and PRESERVED in the serialized room table, never recomputed
/// (flagship 29, <c>AirlockRuleDiag</c>). GED's fresh room list would silently drop them on every rebuild,
/// so this pass carries each source room's airlock flag onto the spatially-matching compiled room
/// (best-IoU ≥ <see cref="IouThreshold"/>): rooms that survive a rebuild keep their authored flag, exactly
/// the preservation semantics RED exhibits; rooms the edit destroyed lose it, like any authored room state.
/// Additive only — a flag GED itself set (from a real airlock room effect) is never cleared.
/// </summary>
public static class RoomFlagPreservation
{
    /// <summary>Minimum AABB IoU for a compiled room to count as the same room as a source room. Matches
    /// the RoomFlagParity gate's confident-correspondence threshold.</summary>
    private const double IouThreshold = 0.30;

    /// <summary>Copies <c>is_airlock</c> from each flagged room of <paramref name="source"/> (the level's
    /// previously serialized static geometry) onto its best-IoU room in <paramref name="compiled"/>.</summary>
    public static void PreserveAirlock(Geometry source, Geometry compiled)
    {
        var flagged = new List<Room>();
        foreach (Room r in source.Rooms)
        {
            if (r.IsAirlock != 0)
            {
                flagged.Add(r);
            }
        }

        if (flagged.Count == 0)
        {
            return;
        }

        foreach (Room src in flagged)
        {
            int best = -1;
            double bestIou = IouThreshold;
            for (int i = 0; i < compiled.Rooms.Count; i++)
            {
                double iou = Iou(src.Aabb, compiled.Rooms[i].Aabb);
                if (iou >= bestIou)
                {
                    bestIou = iou;
                    best = i;
                }
            }

            if (best >= 0)
            {
                compiled.Rooms[best].IsAirlock = 1;
            }
        }
    }

    private static double Iou(Aabb a, Aabb b)
    {
        double ix = Overlap(a.P1.X, a.P2.X, b.P1.X, b.P2.X);
        double iy = Overlap(a.P1.Y, a.P2.Y, b.P1.Y, b.P2.Y);
        double iz = Overlap(a.P1.Z, a.P2.Z, b.P1.Z, b.P2.Z);
        double inter = ix * iy * iz;
        if (inter <= 0)
        {
            return 0;
        }

        double va = Volume(a);
        double vb = Volume(b);
        double union = va + vb - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static double Overlap(float a1, float a2, float b1, float b2)
    {
        double lo = a1 > b1 ? a1 : b1;
        double hi = a2 < b2 ? a2 : b2;
        return hi > lo ? hi - lo : 0;
    }

    private static double Volume(Aabb a) =>
        (double)(a.P2.X - a.P1.X) * (a.P2.Y - a.P1.Y) * (a.P2.Z - a.P1.Z);
}
