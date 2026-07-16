using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// Cross-surface lightmap seam blend across coplanar surfaces that a portal split
/// into DIFFERENT rooms (e.g. a floor under a doorway). Glacier already merges
/// coplanar, edge-adjacent faces WITHIN a room into one surface
/// (<see cref="Ged.Core.Compiler.SurfaceBuilder"/>), so no same-room seam survives;
/// the residual seam is the cross-ROOM one, where the room boundary blocks the merge
/// and each fragment gets an independently-lit lightmap whose abutting edge texels do
/// not agree — the visible discontinuity under doorways.
///
/// SPECIFICATION (MPL, cited never copied): Alpine's <c>-smoothlights</c> cross-room
/// blend — <c>editor_patch/lightmap.cpp:81-110</c> (the
/// <c>lightmap_cross_room_blend_injection</c>) with its install at <c>:596</c>. RED's
/// native cross-surface edge blend (<c>FUN_004aae80</c>) is same-room only; Alpine
/// removes the room-boundary gate for surfaces that are COPLANAR — normals aligned
/// (<c>dot &gt; 0.999</c> and <c>|dA-dB| &lt; 0.001</c>) OR opposite (<c>dot &lt; -0.999</c>
/// and <c>|dA+dB| &lt; 0.001</c>), the exact test at <c>lightmap.cpp:100-103</c>. On the
/// shared world-space edge the two fragments' abutting texels are averaged so the encoded
/// atlas is continuous across the boundary. Alpine gates the whole feature behind the
/// <c>-smoothlights</c> command line (<c>lightmap.cpp:544</c>) — it is OPT-IN, not stock
/// RED — so <see cref="Lightmapper"/> runs this only off the RED-Classic parity path,
/// leaving stock-RED bakes byte-identical to RED's own (seam-carrying) references.
/// </summary>
internal static class CrossSurfaceBlend
{
    // Alpine lightmap.cpp:100-103 coplanarity thresholds (verbatim constants).
    private const float CoplanarDot = 0.999f;
    private const float OffsetEps = 0.001f;

    // Plane-offset bucket cell (m): portal-split fragments share the SAME plane exactly, so
    // any reasonable cell co-buckets them; the exact per-pair test below does the real work.
    private const float BucketCell = 0.25f;

    /// <summary>
    /// Blends the abutting edge texels of coplanar, cross-room, edge-adjacent static
    /// surfaces in place on their pre-encode float buffers. Returns the number of unique
    /// shared-edge texel pairs blended (0 when nothing qualifies). Single-threaded: it
    /// touches only boundary texels and writes shared buffers.
    /// </summary>
    public static int Apply(
        IReadOnlyList<SurfaceBake> surfaces, float[][] buffers,
        SurfaceTexelMapper[] mappers, int[] widths, int[] heights)
    {
        // Candidate surfaces: static (non-mover), with a live direct-lit buffer and a real grid.
        var cand = new List<int>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (buffers[i] is not null && surfaces[i].MoverTransform is null && widths[i] > 0 && heights[i] > 0)
            {
                cand.Add(i);
            }
        }

        if (cand.Count < 2)
        {
            return 0;
        }

        // Bucket by (dropped axis, quantized canonical plane offset) so only genuinely coplanar
        // candidates are ever compared. Canonicalize the plane sign (dropped/dominant-normal axis
        // component made non-negative) so aligned AND opposite normals of one geometric plane
        // land in the same bucket.
        var buckets = new Dictionary<(int Drop, int Cell), List<int>>();
        foreach (int i in cand)
        {
            Surface s = surfaces[i].Surface;
            int drop = s.DroppedCoefficient;
            float cd = Canonical(s.Plane.Normal, s.Plane.Offset, drop);
            var key = (drop, (int)MathF.Round(cd / BucketCell));
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                buckets[key] = list;
            }

            list.Add(i);
        }

        // Collect unique shared-edge texel pairs across all coplanar cross-room adjacent
        // surface pairs, then average each once (order-independent, no double-blending).
        var pairs = new List<(int A, int AOff, int B, int BOff)>();
        var seen = new HashSet<(long, long)>();
        foreach (List<int> group in buckets.Values)
        {
            for (int a = 0; a < group.Count; a++)
            {
                for (int b = a + 1; b < group.Count; b++)
                {
                    CollectPair(surfaces, mappers, widths, heights, group[a], group[b], pairs, seen);
                }
            }
        }

        foreach ((int a, int aOff, int b, int bOff) in pairs)
        {
            float[] ba = buffers[a], bb = buffers[b];
            for (int k = 0; k < 3; k++)
            {
                float avg = 0.5f * (ba[aOff + k] + bb[bOff + k]);
                ba[aOff + k] = avg;
                bb[bOff + k] = avg;
            }
        }

        return pairs.Count;
    }

    /// <summary>Canonical plane offset with the dropped-axis normal component forced non-negative.</summary>
    private static float Canonical(Vec3 n, float d, int drop) => n.Component(drop) < 0f ? -d : d;

    private static void CollectPair(
        IReadOnlyList<SurfaceBake> surfaces, SurfaceTexelMapper[] mappers, int[] widths, int[] heights,
        int ia, int ib, List<(int, int, int, int)> pairs, HashSet<(long, long)> seen)
    {
        Surface sa = surfaces[ia].Surface;
        Surface sb = surfaces[ib].Surface;

        // Cross-room only: same-room coplanar adjacency is already one merged surface in Glacier
        // (Alpine's injection short-circuits room_a == room_b at lightmap.cpp:88 for the same reason).
        if (sa.RoomIndex == sb.RoomIndex)
        {
            return;
        }

        // Alpine coplanarity test on the ORIGINAL normals (lightmap.cpp:100-103).
        Vec3 na = sa.Plane.Normal, nb = sb.Plane.Normal;
        float da = sa.Plane.Offset, db = sb.Plane.Offset;
        float dot = na.Dot(nb);
        bool aligned = dot > CoplanarDot && MathF.Abs(da - db) < OffsetEps;
        bool opposite = dot < -CoplanarDot && MathF.Abs(da + db) < OffsetEps;
        if (!aligned && !opposite)
        {
            return;
        }

        SurfaceTexelMapper ma = mappers[ia], mb = mappers[ib];
        int wa = widths[ia], ha = heights[ia];
        int wb = widths[ib], hb = heights[ib];

        // World-space texel size of each surface (max of the two in-plane axis steps).
        float texel = MathF.Max(TexelSize(ma, wa, ha), TexelSize(mb, wb, hb));
        if (texel <= 0f)
        {
            return;
        }

        // Edge-adjacency: the two world bounding boxes must touch (share the portal cut edge).
        if (!BoxesAdjacent(sa.BoundingBox, sb.BoundingBox, 2f * texel))
        {
            return;
        }

        // Two abutting edge texels are "the same seam location" when within ~1.5 texels.
        float matchDistSq = 1.5f * texel * (1.5f * texel);

        // Match both directions (so higher-resolution edges on either side are all paired),
        // deduping to one unordered pair per shared-edge texel couple.
        MatchRing(ma, mb, ia, ib, wa, ha, wb, hb, sa.BoundingBox, sb.BoundingBox, matchDistSq, pairs, seen);
        MatchRing(mb, ma, ib, ia, wb, hb, wa, ha, sb.BoundingBox, sa.BoundingBox, matchDistSq, pairs, seen);
    }

    /// <summary>
    /// For each outer-ring texel of the SOURCE surface, find the nearest DEST texel; when they
    /// coincide in world space (within <paramref name="matchDistSq"/>) record the (deduped) texel
    /// pair to average — giving C0 continuity across the shared edge.
    /// </summary>
    private static void MatchRing(
        SurfaceTexelMapper src, SurfaceTexelMapper dst, int srcIdx, int dstIdx,
        int sw, int sh, int dw, int dh, Aabb srcBox, Aabb dstBox, float matchDistSq,
        List<(int, int, int, int)> pairs, HashSet<(long, long)> seen)
    {
        foreach ((int col, int row) in Ring(sw, sh))
        {
            Vec3 pS = Clamp(src.World(col, row), srcBox);
            dst.TexelAt(pS, dw, dh, out int dc, out int dr);
            Vec3 pD = Clamp(dst.World(dc, dr), dstBox);
            if (pS.Sub(pD).LengthSquared() > matchDistSq)
            {
                continue;
            }

            int so = ((row * sw) + col) * 3;
            int doff = ((dr * dw) + dc) * 3;
            long gs = ((long)srcIdx << 32) | (uint)so;
            long gd = ((long)dstIdx << 32) | (uint)doff;
            (long, long) key = gs <= gd ? (gs, gd) : (gd, gs);
            if (seen.Add(key))
            {
                pairs.Add((srcIdx, so, dstIdx, doff));
            }
        }
    }

    /// <summary>Outer 1-texel ring coordinates of a w×h grid, each border texel once.</summary>
    private static IEnumerable<(int Col, int Row)> Ring(int w, int h)
    {
        for (int col = 0; col < w; col++)
        {
            yield return (col, 0);
            if (h > 1)
            {
                yield return (col, h - 1);
            }
        }

        for (int row = 1; row < h - 1; row++)
        {
            yield return (0, row);
            if (w > 1)
            {
                yield return (w - 1, row);
            }
        }
    }

    /// <summary>World distance covered by one texel step (max of the two in-plane axes).</summary>
    private static float TexelSize(SurfaceTexelMapper m, int w, int h)
    {
        Vec3 o = m.World(0, 0);
        float du = w > 1 ? m.World(1, 0).Sub(o).Length() : 0f;
        float dv = h > 1 ? m.World(0, 1).Sub(o).Length() : 0f;
        return MathF.Max(du, dv);
    }

    /// <summary>True when box <paramref name="a"/> expanded by <paramref name="eps"/> overlaps box b.</summary>
    private static bool BoxesAdjacent(Aabb a, Aabb b, float eps) =>
        a.P1.X - eps <= b.P2.X && a.P2.X + eps >= b.P1.X &&
        a.P1.Y - eps <= b.P2.Y && a.P2.Y + eps >= b.P1.Y &&
        a.P1.Z - eps <= b.P2.Z && a.P2.Z + eps >= b.P1.Z;

    private static Vec3 Clamp(Vec3 p, Aabb box) => new(
        Math.Clamp(p.X, box.P1.X, box.P2.X),
        Math.Clamp(p.Y, box.P1.Y, box.P2.Y),
        Math.Clamp(p.Z, box.P1.Z, box.P2.Z));
}
