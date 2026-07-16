using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// One portal brush's membrane after chopping: the opening polygon (projected to
/// the brush centre plane and clipped to open space) as front/back face pairs,
/// plus the plane used by the room bipartition.
/// </summary>
public sealed class PortalMembrane
{
    public CsgPlane Plane { get; init; }

    public int BrushUid { get; init; }

    /// <summary>
    /// The AUTHORED opening polygon (the portal brush's largest face projected to the centre plane), BEFORE
    /// clipping to the open cross-section. RED's phase-3 chops spanning world faces against the portal
    /// BRUSH's own extent and votes room adjacency at its plane, so the doorway divider is the authored
    /// slab — wider than the clipped fragments. GED uses this polygon for the world chop test and the
    /// flood-blocking sheet: the clipped fragments alone leave an open annulus at the rim wherever the
    /// recompiled walls tessellate a few cm off the fragment boundary, and the room flood sneaks around
    /// the doorway there (dm04's extraction-path room over-merge). Still a bounded polygon — no
    /// infinite-plane action-at-a-distance (the dmabrupt gap-ribbon protection that motivated the
    /// sheet gate stays).
    /// </summary>
    public CsgFace? Opening { get; set; }

    /// <summary>Clipped fragments facing the plane's front side.</summary>
    public List<CsgFace> FrontFaces { get; } = new();

    /// <summary>Flipped duplicates facing the back side.</summary>
    public List<CsgFace> BackFaces { get; } = new();

    /// <summary>All vertices of the clipped opening (for side-signatures + record AABB).</summary>
    public IEnumerable<CsgVertex> Vertices
    {
        get
        {
            foreach (CsgFace f in FrontFaces)
            {
                foreach (CsgVertex v in f.Vertices)
                {
                    yield return v;
                }
            }
        }
    }

    public Vec3 Center()
    {
        var sum = new Vec3(0, 0, 0);
        int n = 0;
        foreach (CsgVertex v in Vertices)
        {
            sum = sum.Add(v.Position);
            n++;
        }

        return n == 0 ? sum : sum.Scale(1f / n);
    }
}

/// <summary>
/// Portal handling per RED's model. "Chopping" clips each portal brush's opening
/// polygon to open space (mode-4/5 equivalent: the world faces stay whole, the
/// portal faces are cut to the open cross-section) and inserts the fragments as
/// front/back membrane pairs (texture −1). Room building then splits each open
/// cell at the membrane planes (majority plane-side vote — RED's FUN_004861d0
/// adjacency vote), so a portal divides its room regardless of rim closure.
/// After rooms exist, one portal record per divided membrane's room pair is
/// emitted (dedup, union AABB over the pair's fragments); membranes that divide
/// nothing are discarded along with their faces.
/// </summary>
public sealed class PortalBuilder
{
    private readonly BuildReport _report;
    private readonly List<PortalMembrane> _membranes = new();

    public PortalBuilder(BuildReport report)
    {
        _report = report;
    }

    public List<Portal> Portals { get; } = new();

    public IReadOnlyList<PortalMembrane> Membranes => _membranes;

    /// <summary>Chops each portal brush into open space and appends the membrane faces.</summary>
    public void InsertPortalFaces(
        List<CsgFace> open,
        List<(Brush Brush, List<CsgFace> Faces)> portalBrushes,
        CsgSolver solver,
        ref int step,
        int total,
        CompileOptions options)
    {
        foreach ((Brush brush, List<CsgFace> faces) in portalBrushes)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            options.Progress?.Invoke(new CompileProgress("Chopping portal", ++step, total));

            CsgFace? opening = LargestFace(faces);
            if (opening is null)
            {
                continue;
            }

            // Project the opening onto the brush centre plane so a slab's two big
            // faces collapse into a single membrane at the doorway plane.
            Vec3 center = BrushCenter(faces);
            var plane = CsgPlane.FromPointNormal(center, opening.Plane.Normal);
            var projected = new List<CsgVertex>(opening.Vertices.Count);
            foreach (CsgVertex v in opening.Vertices)
            {
                float d = plane.Distance(v.Position);
                projected.Add(new CsgVertex(v.Position.Sub(plane.Normal.Scale(d)), v.Uv));
            }

            CsgFace membrane = opening.With(projected);
            membrane.Plane = CsgPlane.FromPolygon(projected);
            membrane.IsPortal = true;
            membrane.Texture = string.Empty;
            membrane.SmoothingGroups = 0;
            membrane.Flags = 0;

            // Chop: clip to the open cross-section (fragments in rock are discarded).
            List<CsgFace> fragments = solver.ClipToOpen(membrane);
            if (fragments.Count == 0)
            {
                continue; // portal buried in rock / outside the level
            }

            // The authored-opening divider is applied on the LEAF-EXTRACTION path only: its walls tessellate
            // a few cm off the per-brush set, opening a rim annulus the clipped fragments do not cover. The
            // per-brush path keeps the fragment-extent divider that its room gates (dm04 24/9 exact) are
            // tuned to — it is slated for deletion at the flip, at which point this conditional collapses.
            var record = new PortalMembrane
            {
                Plane = membrane.Plane,
                BrushUid = brush.Uid,
                Opening = solver.LeafExtractionActive ? membrane : null,
            };
            foreach (CsgFace frag in fragments)
            {
                var back = frag.With(new List<CsgVertex>(frag.Vertices));
                back.Flip();
                record.FrontFaces.Add(frag);
                record.BackFaces.Add(back);
                open.Add(frag);
                open.Add(back);
            }

            _membranes.Add(record);
        }
    }

    /// <summary>
    /// Mode-4 world chopping: splits every world face that crosses a membrane
    /// polygon by that membrane's plane (RED's phase-3 closure splits spanning
    /// faces against the portal brush; op-type-1 portals — the stock default —
    /// do not keep world faces whole). After this no world face spans a doorway
    /// sheet, so the room flood fill can separate the two sides exactly at it.
    /// </summary>
    public void ChopWorldFaces(List<CsgFace> open)
    {
        if (_membranes.Count == 0)
        {
            return;
        }

        foreach (PortalMembrane m in _membranes)
        {
            Vec3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vec3 mx = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (CsgVertex v in m.Vertices)
            {
                mn = Vec3Math.Min(mn, v.Position);
                mx = Vec3Math.Max(mx, v.Position);
            }


            var next = new List<CsgFace>(open.Count + 16);
            foreach (CsgFace f in open)
            {
                if (f.IsPortal || !FaceAabbOverlaps(f, mn, mx) || !CrossesAnyFragment(f, m))
                {
                    next.Add(f);
                    continue;
                }

                int before = next.Count;
                CsgSolver.SplitFace(f, m.Plane, next);
                for (int i = before; i < next.Count; i++)
                {
                    if (next[i].Vertices.Count < 3 || next[i].Area() < 1e-6f)
                    {
                        next.RemoveAt(i);
                        i--;
                    }
                }
            }

            open.Clear();
            open.AddRange(next);
        }
    }

    private static bool CrossesAnyFragment(CsgFace f, PortalMembrane m)
    {
        // Widened divider (extraction path, m.Opening set): a face whose crossing/touch segment on the
        // membrane plane overlaps the AUTHORED OPENING by more than the band is chopped. Same mutual-overlap
        // semantic as FacesCross, but the segment is clipped against the OPENING polygon (convex, authored)
        // instead of against the world face's own loop — FacesCross assumes a strictly convex CCW loop there,
        // and merged extraction faces with collinear runs misfire it, leaving spanning faces unchopped: the
        // room flood then walks THROUGH the face body around the membrane (dm04's uid=27 lintel and uid=20
        // connectors — RED divides both; verified against RED's original portal records). The BLOCK stays
        // fragment-anchored (RoomBuilder.MembraneSheet): chopping is RED's brush-extent semantic, blocking is
        // RED's portal-face semantic, and widening the block was measured to sever real room loops (dm04's
        // canyon ring — RED keeps uid=294's sides ONE room via it; a 0.25 block or the slab block splits it).
        if (m.Opening is not null && StraddleOverlapsPolygon(f, m.Plane, m.Opening))
        {
            return true;
        }

        foreach (CsgFace frag in m.FrontFaces)
        {
            if (CsgSolver.FacesCross(f, frag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="f"/> spans <paramref name="plane"/> (strict crossings and on-plane touch
    /// points both count, as in FacesCross) and its plane segment, clipped to the convex
    /// <paramref name="opening"/> polygon, keeps more than the band of length.
    /// </summary>
    private static bool StraddleOverlapsPolygon(CsgFace f, CsgPlane plane, CsgFace opening)
    {
        const float Band = CsgPlane.OnPlaneEpsilon;
        Vec3 e0 = default, e1 = default;
        int count = 0;
        bool anyFront = false, anyBack = false;
        int n = f.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            Vec3 u = f.Vertices[i].Position;
            Vec3 v = f.Vertices[(i + 1) % n].Position;
            float du = plane.Distance(u);
            float dv = plane.Distance(v);
            anyFront |= du > Band;
            anyBack |= du < -Band;
            Vec3 hit;
            if (MathF.Abs(du) <= Band)
            {
                hit = u; // on-plane vertex counts as a plane touch (FacesCross's rule)
            }
            else if ((du > Band && dv < -Band) || (du < -Band && dv > Band))
            {
                float t = du / (du - dv);
                hit = u.Add(v.Sub(u).Scale(t));
            }
            else
            {
                continue;
            }

            if (count == 0)
            {
                e0 = hit;
                count = 1;
            }
            else if (hit.Sub(e0).LengthSquared() > Band * Band)
            {
                if (count == 1)
                {
                    e1 = hit;
                    count = 2;
                }
                else
                {
                    // Keep the two extreme points (a degenerate loop can touch/cross many times; the span
                    // between the extremes covers every crossing).
                    Vec3 dir0 = e1.Sub(e0);
                    float t0 = hit.Sub(e0).Dot(dir0) / MathF.Max(dir0.LengthSquared(), 1e-12f);
                    if (t0 > 1f)
                    {
                        e1 = hit;
                    }
                    else if (t0 < 0f)
                    {
                        e0 = hit;
                    }
                }
            }
        }

        if (count < 2 || !anyFront || !anyBack)
        {
            return false; // no real span: touching the plane without material on both sides
        }

        // Clip e0→e1 against the opening's convex polygon (coplanar with the membrane plane).
        Vec3 dir = e1.Sub(e0);
        float lenSq = dir.LengthSquared();
        if (lenSq < 1e-10f)
        {
            return false;
        }

        float tMin = 0f, tMax = 1f;
        List<CsgVertex> ov = opening.Vertices;
        int m = ov.Count;
        for (int i = 0; i < m; i++)
        {
            Vec3 a = ov[i].Position;
            Vec3 b = ov[(i + 1) % m].Position;
            Vec3 inward = opening.Plane.Normal.Cross(b.Sub(a)); // points into a CCW polygon
            float d0 = e0.Sub(a).Dot(inward);
            float d1 = e1.Sub(a).Dot(inward);
            if (d0 >= 0 && d1 >= 0)
            {
                continue;
            }

            if (d0 < 0 && d1 < 0)
            {
                return false; // segment entirely outside this opening edge
            }

            float t = d0 / (d0 - d1);
            if (d0 < 0)
            {
                tMin = MathF.Max(tMin, t);
            }
            else
            {
                tMax = MathF.Min(tMax, t);
            }
        }

        return (tMax - tMin) * MathF.Sqrt(lenSq) > Band;
    }

    private static bool FaceAabbOverlaps(CsgFace f, Vec3 mn, Vec3 mx)
    {
        const float Slack = 0.01f;
        Vec3 fmn = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 fmx = new(float.MinValue, float.MinValue, float.MinValue);
        f.GrowAabb(ref fmn, ref fmx);
        return fmn.X <= mx.X + Slack && fmx.X >= mn.X - Slack &&
               fmn.Y <= mx.Y + Slack && fmx.Y >= mn.Y - Slack &&
               fmn.Z <= mx.Z + Slack && fmx.Z >= mn.Z - Slack;
    }

    /// <summary>
    /// Emits one portal record per membrane whose two sides landed in different
    /// rooms (dedup per room pair, union AABB over the pair's fragments) and tags
    /// the faces with portal_index_plus_2. Membrane faces that divide nothing keep
    /// PortalIndexPlus2 == 0 so the compiler can drop them before assembly.
    /// </summary>
    public void BuildRecords(RoomBuildResult rooms)
    {
        var recordByPair = new Dictionary<(int, int), int>();

        // One record per DISTINCT room pair a membrane's fragments border (RoomBuilder now assigns each
        // fragment its own front/back room). A tall doorway whose LOW fragments border the water room and
        // whose HIGH fragments border the air room above thus emits BOTH portals (RED's per-region vote),
        // where the old whole-membrane single pair emitted only one and stranded the other room (dmabrupt's
        // water room fell to 2 portal faces vs RED's 28).
        foreach (PortalMembrane m in _membranes)
        {
            int count = System.Math.Min(m.FrontFaces.Count, m.BackFaces.Count);
            for (int i = 0; i < count; i++)
            {
                CsgFace front = m.FrontFaces[i];
                CsgFace back = m.BackFaces[i];
                int roomFront = front.RoomIndex;
                int roomBack = back.RoomIndex;
                if (roomFront < 0 || roomBack < 0 || roomFront == roomBack)
                {
                    continue; // this fragment separates no two rooms — it stays untagged and is dropped
                }

                var key = roomFront < roomBack ? (roomFront, roomBack) : (roomBack, roomFront);
                if (!recordByPair.TryGetValue(key, out int rec))
                {
                    rec = Portals.Count;
                    recordByPair[key] = rec;
                    Portals.Add(new Portal
                    {
                        RoomIndex1 = roomFront,
                        RoomIndex2 = roomBack,
                        Point1 = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue),
                        Point2 = new Vec3(float.MinValue, float.MinValue, float.MinValue),
                    });
                }

                Portal p = Portals[rec];
                Vec3 mn = p.Point1;
                Vec3 mx = p.Point2;
                front.GrowAabb(ref mn, ref mx);
                p.Point1 = mn;
                p.Point2 = mx;

                front.PortalIndexPlus2 = rec + 2;
                back.PortalIndexPlus2 = rec + 2;

                // Grow each room's AABB by the portal ONLY within that room's existing (member-face) extent.
                // RED's room bbox (FUN_004852d0) includes portal faces, but a legitimate portal is a hole in the
                // room's OWN wall, so it lies within the member extent and never enlarges the bbox (verified: RED's
                // dmabrupt liquid room bbox y[-9..-2] equals its member faces, all 28 of its portals inside).
                // Clamping keeps every correct room unchanged and drops any misassigned overshoot.
                ClampRoomToPortal(rooms, roomFront, mn, mx);
                ClampRoomToPortal(rooms, roomBack, mn, mx);
            }
        }
    }

    private static void ClampRoomToPortal(RoomBuildResult rooms, int room, Vec3 mn, Vec3 mx)
    {
        if (room < 0 || room >= rooms.Rooms.Count)
        {
            return;
        }

        Aabb bb = rooms.Rooms[room].Aabb;
        // Only the portal's in-extent part grows the room; an overshoot beyond the member AABB is clamped away.
        Vec3 pmn = Vec3Math.Max(mn, bb.P1);
        Vec3 pmx = Vec3Math.Min(mx, bb.P2);
        rooms.Rooms[room].Aabb = new Aabb(Vec3Math.Min(bb.P1, pmn), Vec3Math.Max(bb.P2, pmx));
    }

    private static Vec3 BrushCenter(List<CsgFace> faces)
    {
        var sum = new Vec3(0, 0, 0);
        int count = 0;
        foreach (CsgFace f in faces)
        {
            foreach (CsgVertex v in f.Vertices)
            {
                sum = sum.Add(v.Position);
                count++;
            }
        }

        return count == 0 ? sum : sum.Scale(1f / count);
    }

    private static CsgFace? LargestFace(List<CsgFace> faces)
    {
        CsgFace? best = null;
        float bestArea = 0f;
        foreach (CsgFace f in faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            float a = f.Area();
            if (a > bestArea)
            {
                bestArea = a;
                best = f;
            }
        }

        return best;
    }
}
