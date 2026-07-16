using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// DIAGNOSTIC (temporary): dm04 brushes 1 / 11 / 14 — the coordinator's trace case (two air + one solid;
/// user screenshot shows a gaping hole AND overlapping coexisting faces on the extraction path). Dumps the
/// brush geometry, both paths' compiled faces, holes, and coplanar-overlap pairs, to locate the first
/// pipeline divergence vs RED semantics.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class TraceDm04Diag
{
    private readonly ITestOutputHelper _out;

    public TraceDm04Diag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_Brushes_1_11_14()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection b)
            {
                bs = b;
                break;
            }
        }

        Assert.NotNull(bs);
        List<Brush> all = bs!.Brushes.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"dm04 total brushes = {all.Count}");

        // Interpretation A: document-order indices 1, 11, 14.
        foreach (int idx in new[] { 1, 11, 14 })
        {
            Brush b = all[idx];
            DumpBrush(sb, $"index {idx}", b);
        }

        // Interpretation B: UIDs 1, 11, 14 (if they exist and differ).
        foreach (int uid in new[] { 1, 11, 14 })
        {
            Brush? b = all.FirstOrDefault(x => x.Uid == uid);
            if (b is not null && all.IndexOf(b) != uid)
            {
                DumpBrush(sb, $"uid {uid} (doc index {all.IndexOf(b)})", b);
            }
        }

        // The coordinator's trace case: UIDs 1 / 11 / 14 = two air terrain brushes + one solid
        // (doc indices 0 / 7 / 10). Preserve document (time) order.
        var scene = all.Where(b => b.Uid is 1 or 11 or 14).ToList();
        sb.AppendLine($"scene: {string.Join(", ", scene.Select(b => $"uid{b.Uid}({(BrushFlags)b.Flags})"))}");
        foreach (bool extract in new[] { false, true })
        {
            CompiledLevel c = GeometryCompiler.Compile(
                scene, null, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = extract });
            Geometry g = c.Geometry;
            List<Vec3> holes = HoleDetector.Detect(g);
            sb.AppendLine();
            sb.AppendLine($"=== path={(extract ? "extract" : "perbrush")} used={c.Report.LeafExtractionUsed} " +
                $"faces={g.Faces.Count} rooms={c.Report.Rooms} holes={holes.Count}");
            for (int i = 0; i < g.Faces.Count; i++)
            {
                Face f = g.Faces[i];
                var fsb = new StringBuilder();
                fsb.Append($"  face {i}: tex={f.Texture} n=({f.Plane.Normal.X:F4},{f.Plane.Normal.Y:F4},{f.Plane.Normal.Z:F4}) " +
                    $"off={f.Plane.Offset:F4} verts=");
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    fsb.Append($"({p.X:F3},{p.Y:F3},{p.Z:F3}) ");
                }

                sb.AppendLine(fsb.ToString());
            }

            foreach (Vec3 h in holes.Take(40))
            {
                sb.AppendLine($"  HOLE at ({h.X:F4},{h.Y:F4},{h.Z:F4})");
            }

            // Overlapping coplanar pairs: same plane (either orientation), 2D-projected overlap area > eps.
            List<(int A, int B, float Area)> overlaps = FindOverlaps(g);
            sb.AppendLine($"  overlapping coplanar pairs: {overlaps.Count}");
            foreach ((int a, int b, float ar) in overlaps.Take(30))
            {
                sb.AppendLine($"    faces {a} x {b} overlapArea={ar:F5} texA={g.Faces[a].Texture} texB={g.Faces[b].Texture}");
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("trace_dm04_1_11_14.txt", report);
    }

    /// <summary>
    /// Blocker 3 diagnosis: WHERE does the extraction-path room flood cross the dm04 doorways (24/9 → 19-room
    /// over-merge, portals 10 → 1)? Compiles per-brush (reference portal records = the doorway AABBs), then
    /// extraction with join capture, and reports every flood join whose edge midpoint lands inside a doorway
    /// AABB — the exact leak edges, with their join kind (exact-manifold vs collinear-overlap).
    /// </summary>
    [Fact]
    public void Dump_Dm04_Room_Merge_Diagnosis()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        Assert.NotNull(bs);
        List<Brush> brushes = bs!.Brushes.ToList();
        List<RoomEffect> effects = es?.Effects.ToList() ?? new List<RoomEffect>();

        var sb = new StringBuilder();

        // Ground truth: RED's ORIGINAL compiled portal records (the 9 records extraction must reproduce).
        Geometry? orig = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                orig = gs.Geometry;
                break;
            }
        }

        if (orig is not null)
        {
            int mains = orig.Rooms.Count(r => r.IsSubroom == 0);
            sb.AppendLine($"RED orig: rooms={orig.Rooms.Count} (main {mains}) portals={orig.Portals.Count}");
            for (int i = 0; i < orig.Portals.Count; i++)
            {
                Portal p = orig.Portals[i];
                sb.AppendLine($"  RED portal {i}: rooms {p.RoomIndex1}-{p.RoomIndex2} aabb=({p.Point1.X:F2},{p.Point1.Y:F2},{p.Point1.Z:F2})..({p.Point2.X:F2},{p.Point2.Y:F2},{p.Point2.Z:F2})");
            }
        }

        RoomBuilder.CaptureJoins = true;
        CompiledLevel pb;
        try
        {
            pb = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false });
        }
        finally
        {
            RoomBuilder.CaptureJoins = false;
        }

        sb.AppendLine($"perbrush: rooms={pb.Report.Rooms}({pb.Report.Subrooms}) portals={pb.Report.Portals}");

        // Where does the PB flood connect uid=27's two sides (the wrap-around path extraction lacks)?
        List<(int A, int B, Vec3 CA, Vec3 CB, Vec3 Mid, int Kind)> pbJoins = RoomBuilder.CapturedJoins ?? new();
        sb.AppendLine("  --- PB BFS across membrane uid=27:");
        var probe27 = new Vec3(37.152f, -55.395f, -0.396f);
        BfsPath(sb, pbJoins, probe27.Add(new Vec3(0.5f, 0, 0)), probe27.Sub(new Vec3(0.5f, 0, 0)));

        RoomBuilder.CaptureJoins = true;
        CompiledLevel ex;
        try
        {
            ex = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
        }
        finally
        {
            RoomBuilder.CaptureJoins = false;
        }

        sb.AppendLine($"extract:  rooms={ex.Report.Rooms}({ex.Report.Subrooms}) portals={ex.Report.Portals} joins={RoomBuilder.CapturedJoins?.Count}");

        // Label each captured join's two faces with the PER-BRUSH room at their centroids (nearest pb main-room
        // face). A join whose two faces sit in DIFFERENT pb main rooms is a cross-doorway edge the pb flood kept
        // apart — the exact over-merge points. Cluster them by pb room pair.
        Geometry pg = pb.Geometry;
        var pbCentroids = new List<(Vec3 C, int Room)>();
        for (int i = 0; i < pg.Faces.Count; i++)
        {
            Face f = pg.Faces[i];
            if (f.RoomIndex < 0 || f.RoomIndex >= pg.Rooms.Count || f.Vertices.Count < 3 || f.PortalIndexPlus2 >= 2)
            {
                continue;
            }

            if (pg.Rooms[f.RoomIndex].IsSubroom != 0)
            {
                continue; // label against MAIN rooms only
            }

            var c = new Vec3(0, 0, 0);
            foreach (FaceVertex v in f.Vertices)
            {
                c = c.Add(pg.Vertices[v.Index]);
            }

            pbCentroids.Add((c.Scale(1f / f.Vertices.Count), f.RoomIndex));
        }

        int PbRoomOf(Vec3 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            foreach ((Vec3 c, int room) in pbCentroids)
            {
                float d = c.Sub(p).LengthSquared();
                if (d < bestD)
                {
                    bestD = d;
                    best = room;
                }
            }

            return best;
        }

        List<(int A, int B, Vec3 CA, Vec3 CB, Vec3 Mid, int Kind)> joins = RoomBuilder.CapturedJoins ?? new();
        var byPair = new Dictionary<(int, int), List<(Vec3 Mid, int Kind)>>();
        foreach ((int _, int _, Vec3 ca, Vec3 cb, Vec3 mid, int kind) in joins)
        {
            int ra = PbRoomOf(ca);
            int rb = PbRoomOf(cb);
            if (ra < 0 || rb < 0 || ra == rb)
            {
                continue;
            }

            (int, int) key = ra < rb ? (ra, rb) : (rb, ra);
            if (!byPair.TryGetValue(key, out List<(Vec3, int)>? list))
            {
                byPair[key] = list = new List<(Vec3, int)>();
            }

            list.Add((mid, kind));
        }

        foreach (((int ra, int rb), List<(Vec3 Mid, int Kind)> list) in byPair.OrderByDescending(kv => kv.Value.Count))
        {
            sb.AppendLine($"  pbRooms {ra}~{rb}: {list.Count} cross-joins");
            foreach ((Vec3 m, int k) in list.Take(10))
            {
                sb.AppendLine($"    {(k == 0 ? "exact" : "overlap")} at ({m.X:F4},{m.Y:F4},{m.Z:F4})");
            }
        }

        // Membrane side assignments (extraction build): which membranes failed to divide, and why.
        sb.AppendLine("  membranes (extract): uid probe front|back");
        foreach ((int uid, Vec3 probe, int fr, int br, List<Vec3>? opening) in RoomBuilder.CapturedMembranes ?? new())
        {
            sb.AppendLine($"    uid={uid} probe=({probe.X:F3},{probe.Y:F3},{probe.Z:F3}) rooms={fr}|{br}{(fr == br ? "  << UNDIVIDED" : string.Empty)}");
            if (fr == br && opening is not null)
            {
                var osb = new StringBuilder("      opening: ");
                foreach (Vec3 p in opening)
                {
                    osb.Append($"({p.X:F3},{p.Y:F3},{p.Z:F3}) ");
                }

                sb.AppendLine(osb.ToString());
            }
        }

        // Ground truth for the still-merged doorways: BFS the join graph across each undivided membrane.
        // The path's edge midpoints ARE the leak trail; dump each path face's vertex loop (centroid-matched
        // in the compacted geometry) to see the unchopped spanning face.
        foreach ((int uid, Vec3 probe, int fr, int br, List<Vec3>? _) in RoomBuilder.CapturedMembranes ?? new())
        {
            if (fr != br)
            {
                continue;
            }

            sb.AppendLine($"  --- BFS across undivided membrane uid={uid}:");
            Vec3 axis = uid == 294 || uid == 27 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
            List<Vec3> pathCentroids = BfsPath(sb, joins, probe.Add(axis.Scale(0.4f)), probe.Sub(axis.Scale(0.4f)));
            foreach (Vec3 pc in pathCentroids)
            {
                DumpFaceByCentroid(sb, ex.Geometry, pc);
            }
        }

        string reportOut = sb.ToString();
        _out.WriteLine(reportOut);
        Artifact("trace_dm04_room_merge.txt", reportOut);
    }

    /// <summary>Dumps the vertex loop of the geometry face whose centroid matches <paramref name="c"/>.</summary>
    private static void DumpFaceByCentroid(StringBuilder sb, Geometry g, Vec3 c)
    {
        for (int i = 0; i < g.Faces.Count; i++)
        {
            Face f = g.Faces[i];
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            var fc = new Vec3(0, 0, 0);
            foreach (FaceVertex v in f.Vertices)
            {
                fc = fc.Add(g.Vertices[v.Index]);
            }

            fc = fc.Scale(1f / f.Vertices.Count);
            if (fc.Sub(c).LengthSquared() > 1e-5f)
            {
                continue;
            }

            var fsb = new StringBuilder($"      face[{i}] tex={f.Texture} flags=0x{f.Flags:X} room={f.RoomIndex} verts=");
            foreach (FaceVertex v in f.Vertices)
            {
                Vec3 p = g.Vertices[v.Index];
                fsb.Append($"({p.X:F3},{p.Y:F3},{p.Z:F3}) ");
            }

            sb.AppendLine(fsb.ToString());
            return;
        }

        sb.AppendLine($"      (no geometry face at centroid ({c.X:F3},{c.Y:F3},{c.Z:F3}))");
    }

    /// <summary>BFS over the captured join graph from the joined face nearest <paramref name="from"/> to the
    /// joined face nearest <paramref name="to"/>; prints the connecting path's join midpoints and returns the
    /// path faces' centroids.</summary>
    private static List<Vec3> BfsPath(
        StringBuilder sb,
        List<(int A, int B, Vec3 CA, Vec3 CB, Vec3 Mid, int Kind)> joins,
        Vec3 from,
        Vec3 to)
    {
        var centroid = new Dictionary<int, Vec3>();
        var adj = new Dictionary<int, List<(int Other, Vec3 Mid, int Kind)>>();
        foreach ((int a, int b, Vec3 ca, Vec3 cb, Vec3 mid, int kind) in joins)
        {
            centroid[a] = ca;
            centroid[b] = cb;
            if (!adj.TryGetValue(a, out List<(int, Vec3, int)>? la))
            {
                adj[a] = la = new List<(int, Vec3, int)>();
            }

            la.Add((b, mid, kind));
            if (!adj.TryGetValue(b, out List<(int, Vec3, int)>? lb))
            {
                adj[b] = lb = new List<(int, Vec3, int)>();
            }

            lb.Add((a, mid, kind));
        }

        // Seed strictly on opposite sides of the from→to axis midpoint plane, so BFS start/goal cannot
        // collapse to one face when one side's walls are further away than the other's.
        Vec3 axisDir = to.Sub(from).Normalized();
        Vec3 mid0 = from.Add(to).Scale(0.5f);
        int start = -1, goal = -1;
        float ds = float.MaxValue, dg = float.MaxValue;
        foreach ((int f, Vec3 c) in centroid)
        {
            float side = c.Sub(mid0).Dot(axisDir); // <0 ⇒ from side, >0 ⇒ to side
            if (side < -0.1f)
            {
                float d1 = c.Sub(from).LengthSquared();
                if (d1 < ds)
                {
                    ds = d1;
                    start = f;
                }
            }
            else if (side > 0.1f)
            {
                float d2 = c.Sub(to).LengthSquared();
                if (d2 < dg)
                {
                    dg = d2;
                    goal = f;
                }
            }
        }

        if (start < 0 || goal < 0)
        {
            sb.AppendLine("  BFS: no joined faces on one side");
            return new List<Vec3>();
        }

        sb.AppendLine($"  BFS start=f{start} at ({centroid[start].X:F3},{centroid[start].Y:F3},{centroid[start].Z:F3})  goal=f{goal} at ({centroid[goal].X:F3},{centroid[goal].Y:F3},{centroid[goal].Z:F3})");
        var prev = new Dictionary<int, (int From, Vec3 Mid, int Kind)>();
        var q = new Queue<int>();
        q.Enqueue(start);
        prev[start] = (start, default, 0);
        bool found = false;
        while (q.Count > 0 && !found)
        {
            int cur = q.Dequeue();
            foreach ((int other, Vec3 mid, int kind) in adj[cur])
            {
                if (prev.ContainsKey(other))
                {
                    continue;
                }

                prev[other] = (cur, mid, kind);
                if (other == goal)
                {
                    found = true;
                    break;
                }

                q.Enqueue(other);
            }
        }

        if (!found)
        {
            sb.AppendLine("  BFS: NO PATH (the two sides are already separate rooms)");
            return new List<Vec3>();
        }

        var path = new List<string>();
        var centroids = new List<Vec3> { centroid[start] };
        int node = goal;
        while (node != start)
        {
            (int fromF, Vec3 mid, int kind) = prev[node];
            Vec3 c = centroid[node];
            path.Add($"    f{node} c=({c.X:F3},{c.Y:F3},{c.Z:F3}) via {(kind == 0 ? "exact" : "overlap")} join at ({mid.X:F4},{mid.Y:F4},{mid.Z:F4})");
            centroids.Add(c);
            node = fromF;
        }

        path.Reverse();
        sb.AppendLine($"  BFS path ({path.Count} hops):");
        foreach (string s in path)
        {
            sb.AppendLine(s);
        }

        return centroids;
    }

    /// <summary>Room of the extraction face whose centroid is nearest the probe (diagnosis-grade lookup).</summary>
    private static int NearestRoom(Geometry g, Vec3 probe)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < g.Faces.Count; i++)
        {
            Face f = g.Faces[i];
            if (f.RoomIndex < 0 || f.Vertices.Count < 3 || f.PortalIndexPlus2 >= 2)
            {
                continue;
            }

            var c = new Vec3(0, 0, 0);
            foreach (FaceVertex v in f.Vertices)
            {
                c = c.Add(g.Vertices[v.Index]);
            }

            c = c.Scale(1f / f.Vertices.Count);
            float d = c.Sub(probe).LengthSquared();
            if (d < bestD)
            {
                bestD = d;
                best = f.RoomIndex;
            }
        }

        return best;
    }

    /// <summary>Blocker 3, portal 1 deep dive: dump every extraction face near the x=−13.46 doorway that
    /// SPANS the membrane plane (should have been chopped) plus the leak-join faces' vertex loops.</summary>
    [Fact]
    public void Dump_Portal1_Spanning_Faces()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dm04.rfl");
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        BrushesSection? bs = null;
        RoomEffectsSection? es = null;
        foreach (RflSection s in rfl.Sections)
        {
            bs ??= s.Content as BrushesSection;
            es ??= s.Content as RoomEffectsSection;
        }

        List<Brush> brushes = bs!.Brushes.ToList();
        List<RoomEffect> effects = es?.Effects.ToList() ?? new List<RoomEffect>();
        CompiledLevel ex = GeometryCompiler.Compile(
            brushes, effects, new CompileOptions { BuildSurfaces = false, UseLeafExtraction = true });
        Geometry g = ex.Geometry;

        // The per-brush portal-1 record: plane x=-13.46, window y -50.9..-23.1, z -43.1..-29.5.
        const float planeX = -13.46f;
        var sb = new StringBuilder();
        sb.AppendLine($"faces spanning x={planeX} within the doorway window (y -51..-23, z -43.2..-29.4):");
        for (int i = 0; i < g.Faces.Count; i++)
        {
            Face f = g.Faces[i];
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            float mnx = float.MaxValue, mxx = float.MinValue;
            float mny = float.MaxValue, mxy = float.MinValue;
            float mnz = float.MaxValue, mxz = float.MinValue;
            foreach (FaceVertex v in f.Vertices)
            {
                Vec3 p = g.Vertices[v.Index];
                mnx = MathF.Min(mnx, p.X);
                mxx = MathF.Max(mxx, p.X);
                mny = MathF.Min(mny, p.Y);
                mxy = MathF.Max(mxy, p.Y);
                mnz = MathF.Min(mnz, p.Z);
                mxz = MathF.Max(mxz, p.Z);
            }

            bool spans = mnx < planeX - 0.02f && mxx > planeX + 0.02f;
            bool inWindow = mxy > -52f && mny < -22f && mxz > -43.5f && mnz < -29.0f;
            if (spans && inWindow)
            {
                var fsb = new StringBuilder();
                fsb.Append($"  face {i}: tex={f.Texture} flags=0x{f.Flags:X} portalIdx={f.PortalIndexPlus2} room={f.RoomIndex} n=({f.Plane.Normal.X:F3},{f.Plane.Normal.Y:F3},{f.Plane.Normal.Z:F3}) verts=");
                foreach (FaceVertex v in f.Vertices)
                {
                    Vec3 p = g.Vertices[v.Index];
                    fsb.Append($"({p.X:F4},{p.Y:F3},{p.Z:F3}) ");
                }

                sb.AppendLine(fsb.ToString());
            }
        }

        string report = sb.ToString();
        _out.WriteLine(report);
        Artifact("trace_portal1_spanning.txt", report);
    }

    private static void DumpBrush(StringBuilder sb, string label, Brush b)
    {
        var flags = (BrushFlags)b.Flags;
        sb.AppendLine();
        sb.AppendLine($"--- brush {label}: uid={b.Uid} flags=0x{b.Flags:X} ({flags}) pos=({b.Position.X:F3},{b.Position.Y:F3},{b.Position.Z:F3}) " +
            $"faces={b.Geometry.Faces.Count} verts={b.Geometry.Vertices.Count} life={b.Life}");
        List<CsgFace> wf = BrushWorld.ToWorldFaces(b, 0, out _);
        foreach (CsgFace f in wf)
        {
            var fsb = new StringBuilder();
            fsb.Append($"    plane n=({f.Plane.Normal.X:F4},{f.Plane.Normal.Y:F4},{f.Plane.Normal.Z:F4}) off={f.Plane.Offset:F4} tex={f.Texture} verts=");
            foreach (CsgVertex v in f.Vertices)
            {
                fsb.Append($"({v.Position.X:F3},{v.Position.Y:F3},{v.Position.Z:F3}) ");
            }

            sb.AppendLine(fsb.ToString());
        }
    }

    /// <summary>Coplanar overlapping-area face pairs (the overlap detector: z-fighting duplicates).</summary>
    internal static List<(int A, int B, float Area)> FindOverlaps(Geometry g)
    {
        var result = new List<(int, int, float)>();
        int n = g.Faces.Count;
        for (int i = 0; i < n; i++)
        {
            Face fi = g.Faces[i];
            if (fi.Texture < 0 || fi.Vertices.Count < 3)
            {
                continue;
            }

            for (int j = i + 1; j < n; j++)
            {
                Face fj = g.Faces[j];
                if (fj.Texture < 0 || fj.Vertices.Count < 3)
                {
                    continue;
                }

                float dot = fi.Plane.Normal.Dot(fj.Plane.Normal);
                if (MathF.Abs(dot) < 0.9999f)
                {
                    continue; // not parallel
                }

                // Same physical plane within 3 mm?
                float off = dot > 0 ? fj.Plane.Offset : -fj.Plane.Offset;
                if (MathF.Abs(fi.Plane.Offset - off) > 3e-3f)
                {
                    continue;
                }

                float area = OverlapArea2D(g, fi, fj);
                if (area > 1e-3f)
                {
                    result.Add((i, j, area));
                }
            }
        }

        return result;
    }

    /// <summary>Approximate 2D overlap area: clip polygon j by polygon i's edges (Sutherland–Hodgman) in the
    /// dominant-axis projection and return the clipped area.</summary>
    private static float OverlapArea2D(Geometry g, Face fi, Face fj)
    {
        Vec3 n = fi.Plane.Normal;
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);

        List<(float U, float V)> pi = Project(g, fi, drop);
        List<(float U, float V)> pj = Project(g, fj, drop);
        if (SignedArea(pi) < 0)
        {
            pi.Reverse();
        }

        List<(float U, float V)> clip = pj;
        int m = pi.Count;
        for (int e = 0; e < m && clip.Count > 2; e++)
        {
            (float ux, float vx) = pi[e];
            (float uy, float vy) = pi[(e + 1) % m];
            var next = new List<(float, float)>();
            int k = clip.Count;
            for (int c = 0; c < k; c++)
            {
                (float cu, float cv) = clip[c];
                (float du, float dv) = clip[(c + 1) % k];
                float sc = Cross(ux, vx, uy, vy, cu, cv);
                float sd = Cross(ux, vx, uy, vy, du, dv);
                if (sc >= 0)
                {
                    next.Add((cu, cv));
                }

                if ((sc >= 0) != (sd >= 0))
                {
                    float t = sc / (sc - sd);
                    next.Add((cu + ((du - cu) * t), cv + ((dv - cv) * t)));
                }
            }

            clip = next;
        }

        return clip.Count < 3 ? 0f : MathF.Abs(SignedArea(clip));
    }

    private static float Cross(float ax, float ay, float bx, float by, float px, float py) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

    private static float SignedArea(List<(float U, float V)> poly)
    {
        float s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            (float u0, float v0) = poly[i];
            (float u1, float v1) = poly[(i + 1) % poly.Count];
            s += (u0 * v1) - (u1 * v0);
        }

        return s * 0.5f;
    }

    private static List<(float, float)> Project(Geometry g, Face f, int drop)
    {
        var list = new List<(float, float)>(f.Vertices.Count);
        foreach (FaceVertex v in f.Vertices)
        {
            Vec3 p = g.Vertices[v.Index];
            list.Add(drop switch
            {
                0 => (p.Y, p.Z),
                1 => (p.X, p.Z),
                _ => (p.X, p.Y),
            });
        }

        return list;
    }

    private static void Artifact(string file, string content)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, file), content);
    }
}
