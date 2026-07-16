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
/// Pass 23A investigation dumps (movers / room-flags / water-room portals). Pure diagnostics; no asserts.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class Pass23Diag
{
    private readonly ITestOutputHelper _out;

    public Pass23Diag(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Dump_Mover_Structure(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();

        var sb = new StringBuilder();
        sb.AppendLine($"MOVER STRUCTURE — {file}");

        BrushesSection? brushesSec = null;
        MoversSection? moversSec = null;
        var groupSecs = new List<GroupsSection>();
        foreach (RflSection s in rfl.Sections)
        {
            switch (s.Content)
            {
                case BrushesSection bs: brushesSec = bs; break;
                case MoversSection ms: moversSec = ms; break;
                case GroupsSection gs: groupSecs.Add(gs); break;
            }
        }

        var brushes = brushesSec?.Brushes ?? new List<Brush>();
        var brushUids = new HashSet<int>(brushes.Select(b => b.Uid));
        sb.AppendLine($"brushes section: {brushes.Count} brushes; uid range [{(brushes.Count > 0 ? brushes.Min(b => b.Uid) : 0)}..{(brushes.Count > 0 ? brushes.Max(b => b.Uid) : 0)}]");
        sb.AppendLine($"movers section: {(moversSec?.Movers.Count ?? -1)} mover brushes");
        if (moversSec is not null)
        {
            var moverUids = new HashSet<int>(moversSec.Movers.Select(m => m.Uid));
            int inBrushes = moverUids.Count(u => brushUids.Contains(u));
            sb.AppendLine($"  mover uids also present in brushes section: {inBrushes}/{moverUids.Count}");
            foreach (Brush m in moversSec.Movers.Take(10))
            {
                sb.AppendLine($"    mover uid={m.Uid} flags=0x{m.Flags:X} faces={m.Geometry.Faces.Count} pos=({m.Position.X:F1},{m.Position.Y:F1},{m.Position.Z:F1})");
            }
        }

        foreach (GroupsSection gs in groupSecs)
        {
            sb.AppendLine($"group section type={gs.Type}: {gs.Groups.Count} groups");
            foreach (Group g in gs.Groups)
            {
                int inBrushSec = g.Brushes.Count(u => brushUids.Contains(u));
                sb.AppendLine($"  group '{g.Name}' moving={g.IsMoving} objects={g.Objects.Count} brushes={g.Brushes.Count} (of those in brushes-section: {inBrushSec})  brushUids=[{string.Join(",", g.Brushes.Take(12))}]");
            }
        }

        // Which brushes-section brushes are members of a MOVING group?
        var movingGroupBrushUids = new HashSet<int>();
        foreach (GroupsSection gs in groupSecs)
        {
            foreach (Group g in gs.Groups)
            {
                if (g.IsMoving != 0)
                {
                    foreach (int u in g.Brushes)
                    {
                        movingGroupBrushUids.Add(u);
                    }
                }
            }
        }

        int movingMembersInBrushSection = brushes.Count(b => movingGroupBrushUids.Contains(b.Uid));
        sb.AppendLine();
        sb.AppendLine($"moving-group brush UIDs total: {movingGroupBrushUids.Count}");
        sb.AppendLine($"brushes-section brushes that ARE moving-group members: {movingMembersInBrushSection}");
        int movingFaces = brushes.Where(b => movingGroupBrushUids.Contains(b.Uid)).Sum(b => b.Geometry.Faces.Count);
        sb.AppendLine($"  their total face count: {movingFaces}");
        foreach (Brush b in brushes.Where(b => movingGroupBrushUids.Contains(b.Uid)).Take(20))
        {
            sb.AppendLine($"    brush uid={b.Uid} flags=0x{b.Flags:X} faces={b.Geometry.Faces.Count} pos=({b.Position.X:F1},{b.Position.Y:F1},{b.Position.Z:F1})");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass23_movers_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Dump_Room_Flags_And_Portals(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        List<Brush> brushes = new();
        List<RoomEffect> effects = new();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                red ??= gs.Geometry;
            }
            else if (s.Content is BrushesSection bs)
            {
                brushes = bs.Brushes;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                effects = es.Effects;
            }
        }

        if (red is null)
        {
            return;
        }

        var options = new CompileOptions { Alpine = rfl.Context.IsAlpine, BuildSurfaces = false };
        Geometry ged = GeometryCompiler.Compile(brushes, effects, options).Geometry;

        var sb = new StringBuilder();
        sb.AppendLine($"ROOM FLAGS — {file}");
        sb.AppendLine($"RED rooms={red.Rooms.Count}  GED rooms={ged.Rooms.Count}");
        sb.AppendLine();

        // Per-room flag table for RED.
        sb.AppendLine("== RED rooms with HasAlpha != 0 ==");
        foreach (Room r in red.Rooms.Where(r => r.HasAlpha != 0))
        {
            sb.AppendLine($"  id=0x{r.Id:X} sub={r.IsSubroom} liq={r.IsLiquidRoom} alpha={r.HasAlpha} center={Center(r.Aabb)} size={Size(r.Aabb)}");
        }

        sb.AppendLine();
        sb.AppendLine("== GED rooms with HasAlpha != 0 ==");
        foreach (Room r in ged.Rooms.Where(r => r.HasAlpha != 0))
        {
            sb.AppendLine($"  id=0x{r.Id:X} sub={r.IsSubroom} liq={r.IsLiquidRoom} alpha={r.HasAlpha} center={Center(r.Aabb)} size={Size(r.Aabb)}");
        }

        // Spatially-matched per-room flag diff (greedy IoU).
        sb.AppendLine();
        sb.AppendLine("== per-room flag diff (RED room -> best-IoU GED room) ==");
        int alphaDiff = 0, skyDiff = 0, coldDiff = 0, outDiff = 0, airDiff = 0, ambDiff = 0, liqDiff = 0;
        foreach (Room rr in red.Rooms)
        {
            Room? best = null;
            double bestIou = 0;
            foreach (Room gr in ged.Rooms)
            {
                double iou = Iou(rr.Aabb, gr.Aabb);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    best = gr;
                }
            }

            if (best is null || bestIou < 0.10)
            {
                continue;
            }

            if (rr.HasAlpha != best.HasAlpha)
            {
                alphaDiff++;
                sb.AppendLine($"  ALPHA red=0x{rr.Id:X}({rr.HasAlpha}) ged=0x{best.Id:X}({best.HasAlpha}) iou={bestIou:F2} center={Center(rr.Aabb)} size={Size(rr.Aabb)}");
            }

            if (rr.IsSkyroom != best.IsSkyroom) skyDiff++;
            if (rr.IsCold != best.IsCold) coldDiff++;
            if (rr.IsOutside != best.IsOutside) outDiff++;
            if (rr.IsAirlock != best.IsAirlock) airDiff++;
            if (rr.HasAmbientLight != best.HasAmbientLight) ambDiff++;
            if (rr.IsLiquidRoom != best.IsLiquidRoom) liqDiff++;
        }

        sb.AppendLine();
        sb.AppendLine($"flag diffs on matched rooms: alpha={alphaDiff} sky={skyDiff} cold={coldDiff} outside={outDiff} airlock={airDiff} ambient={ambDiff} liquid={liqDiff}");

        // Water-room portal comparison.
        sb.AppendLine();
        sb.AppendLine("== WATER ROOM portals ==");
        DumpWaterPortals(sb, "RED", red);
        DumpWaterPortals(sb, "GED", ged);

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass23_roomflags_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Correlate_RoomAlpha_With_AlphaFaces(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                red ??= gs.Geometry;
            }
        }

        if (red is null)
        {
            return;
        }

        // Per-room: count faces, alpha-flagged faces (0x40), alpha faces that are detail (0x08).
        int nRooms = red.Rooms.Count;
        var faceCount = new int[nRooms];
        var alphaFaceCount = new int[nRooms];
        foreach (Face f in red.Faces)
        {
            if (f.RoomIndex < 0 || f.RoomIndex >= nRooms)
            {
                continue;
            }

            faceCount[f.RoomIndex]++;
            if (((FaceFlags)f.Flags & FaceFlags.HasAlpha) != 0)
            {
                alphaFaceCount[f.RoomIndex]++;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"ROOM ALPHA CORRELATION (RED original) — {file}");
        int alphaRoomsWithAlphaFace = 0, alphaRoomsNoAlphaFace = 0;
        int nonAlphaRoomsWithAlphaFace = 0, nonAlphaRoomsNoAlphaFace = 0;
        for (int i = 0; i < nRooms; i++)
        {
            bool roomAlpha = red.Rooms[i].HasAlpha != 0;
            bool hasAlphaFace = alphaFaceCount[i] > 0;
            if (roomAlpha && hasAlphaFace) alphaRoomsWithAlphaFace++;
            else if (roomAlpha && !hasAlphaFace) alphaRoomsNoAlphaFace++;
            else if (!roomAlpha && hasAlphaFace) nonAlphaRoomsWithAlphaFace++;
            else nonAlphaRoomsNoAlphaFace++;
        }

        sb.AppendLine($"rooms={nRooms}");
        sb.AppendLine($"  room.HasAlpha=1 AND contains >=1 alpha-flagged face : {alphaRoomsWithAlphaFace}");
        sb.AppendLine($"  room.HasAlpha=1 AND contains NO alpha-flagged face  : {alphaRoomsNoAlphaFace}");
        sb.AppendLine($"  room.HasAlpha=0 AND contains >=1 alpha-flagged face : {nonAlphaRoomsWithAlphaFace}  (would over-set if rule is any-alpha-face)");
        sb.AppendLine($"  room.HasAlpha=0 AND contains NO alpha-flagged face  : {nonAlphaRoomsNoAlphaFace}");
        sb.AppendLine();
        sb.AppendLine("== rooms where room.HasAlpha=0 but a face IS alpha-flagged (subroom/detail?) ==");
        for (int i = 0; i < nRooms; i++)
        {
            if (red.Rooms[i].HasAlpha == 0 && alphaFaceCount[i] > 0)
            {
                Room r = red.Rooms[i];
                sb.AppendLine($"  room#{i} id=0x{r.Id:X} sub={r.IsSubroom} liq={r.IsLiquidRoom} faces={faceCount[i]} alphaFaces={alphaFaceCount[i]} center={Center(r.Aabb)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("== rooms where room.HasAlpha=1 but NO face alpha-flagged (rule must be something else) ==");
        for (int i = 0; i < nRooms; i++)
        {
            if (red.Rooms[i].HasAlpha != 0 && alphaFaceCount[i] == 0)
            {
                Room r = red.Rooms[i];
                sb.AppendLine($"  room#{i} id=0x{r.Id:X} sub={r.IsSubroom} liq={r.IsLiquidRoom} faces={faceCount[i]} center={Center(r.Aabb)}");
            }
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass23_alphacorr_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    public void Measure_Mover_Faces_In_Static(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        List<Brush> brushes = new();
        List<RoomEffect> effects = new();
        MoversSection? movers = null;
        var groups = new List<GroupsSection>();
        foreach (RflSection s in rfl.Sections)
        {
            switch (s.Content)
            {
                case GeometrySection gs: red ??= gs.Geometry; break;
                case BrushesSection bs: brushes = bs.Brushes; break;
                case RoomEffectsSection es: effects = es.Effects; break;
                case MoversSection ms: movers = ms; break;
                case GroupsSection grs: groups.Add(grs); break;
            }
        }

        if (red is null)
        {
            return;
        }

        // Mover UID set (movers section + IsMoving group members).
        var moverUids = new HashSet<int>();
        if (movers is not null)
        {
            foreach (Brush m in movers.Movers)
            {
                moverUids.Add(m.Uid);
            }
        }

        foreach (GroupsSection grs in groups)
        {
            foreach (Group g in grs.Groups)
            {
                if (g.IsMoving != 0)
                {
                    foreach (int u in g.Brushes)
                    {
                        moverUids.Add(u);
                    }
                }
            }
        }

        var moverBrushes = brushes.Where(b => moverUids.Contains(b.Uid)).ToList();

        var staticBrushes = MoverBrushes.ExcludeMovers(brushes, moverUids);

        // GED current (movers NOT excluded) vs GED shipping (movers excluded).
        Geometry gedWith = GeometryCompiler.Compile(brushes, effects, new CompileOptions { Alpine = rfl.Context.IsAlpine, BuildSurfaces = false }).Geometry;
        Geometry gedShip = GeometryCompiler.Compile(staticBrushes, effects, new CompileOptions { Alpine = rfl.Context.IsAlpine, BuildSurfaces = false }).Geometry;

        var sb = new StringBuilder();
        sb.AppendLine($"MOVER FACES IN STATIC — {file}");
        sb.AppendLine($"mover brushes: {moverBrushes.Count}  uids=[{string.Join(",", moverUids)}]");
        sb.AppendLine($"GED faces: withMovers={gedWith.Faces.Count} shipping(excluded)={gedShip.Faces.Count} RED={red.Faces.Count}");
        sb.AppendLine($"GED rooms: withMovers={gedWith.Rooms.Count} shipping(excluded)={gedShip.Rooms.Count} RED={red.Rooms.Count}");
        sb.AppendLine();
        sb.AppendLine("coincident = faces coplanar+near a mover world face; inside = centroid strictly inside mover AABB");
        sb.AppendLine($"{"mover uid",-10} {"mFaces",7} {"REDcoin",8} {"GEDwith",8} {"GEDship",8}   {"REDin",6} {"GEDwithIn",10} {"GEDshipIn",10}");
        int redCoinT = 0, withCoinT = 0, shipCoinT = 0, moverFaceTotal = 0;
        int redInT = 0, withInT = 0, shipInT = 0;
        foreach (Brush m in moverBrushes)
        {
            List<CsgFace> wf = BrushWorld.ToWorldFaces(m, 0, out _);
            (Vec3 mn, Vec3 mx) = WorldAabb(wf);
            int redCoin = CountCoincident(red, wf), withCoin = CountCoincident(gedWith, wf), shipCoin = CountCoincident(gedShip, wf);
            int redIn = CountInside(red, mn, mx), withIn = CountInside(gedWith, mn, mx), shipIn = CountInside(gedShip, mn, mx);
            redCoinT += redCoin; withCoinT += withCoin; shipCoinT += shipCoin;
            redInT += redIn; withInT += withIn; shipInT += shipIn;
            moverFaceTotal += wf.Count;
            sb.AppendLine($"{m.Uid,-10} {wf.Count,7} {redCoin,8} {withCoin,8} {shipCoin,8}   {redIn,6} {withIn,10} {shipIn,10}");
        }

        sb.AppendLine();
        sb.AppendLine($"TOTAL moverFaces={moverFaceTotal}");
        sb.AppendLine($"  coincident: RED={redCoinT}  GED-withMovers={withCoinT}  GED-shipping={shipCoinT}");
        sb.AppendLine($"  inside-AABB: RED={redInT}  GED-withMovers={withInT}  GED-shipping={shipInT}");
        sb.AppendLine($"=> excluding movers drops GED coincident {withCoinT}->{shipCoinT} (RED={redCoinT}); inside {withInT}->{shipInT} (RED={redInT})");

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass23_moverfaces_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    private static (Vec3 Min, Vec3 Max) WorldAabb(List<CsgFace> faces)
    {
        var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgFace f in faces)
        {
            foreach (CsgVertex v in f.Vertices)
            {
                mn = Vec3Math.Min(mn, v.Position);
                mx = Vec3Math.Max(mx, v.Position);
            }
        }

        return (mn, mx);
    }

    /// <summary>Counts faces whose centroid lies strictly inside the mover AABB (shrunk 0.05m each axis) —
    /// faces that can only exist if the mover was baked, not the surrounding static walls.</summary>
    private static int CountInside(Geometry g, Vec3 mn, Vec3 mx)
    {
        const float shrink = 0.05f;
        int count = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3 || f.IsPortalFace)
            {
                continue;
            }

            var c = new Vec3(0, 0, 0);
            int nv = 0;
            foreach (FaceVertex v in f.Vertices)
            {
                if (v.Index >= 0 && v.Index < g.Vertices.Count)
                {
                    c = c.Add(g.Vertices[v.Index]);
                    nv++;
                }
            }

            if (nv == 0)
            {
                continue;
            }

            c = c.Scale(1f / nv);
            if (c.X > mn.X + shrink && c.X < mx.X - shrink &&
                c.Y > mn.Y + shrink && c.Y < mx.Y - shrink &&
                c.Z > mn.Z + shrink && c.Z < mx.Z - shrink)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Counts faces in <paramref name="g"/> coplanar with and centroid-near one of the mover world faces.</summary>
    private static int CountCoincident(Geometry g, List<CsgFace> moverFaces)
    {
        // Precompute mover face planes + centroids.
        var mf = new List<(Vec3 N, float D, Vec3 C)>();
        foreach (CsgFace f in moverFaces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            Vec3 n = f.Plane.Normal;
            Vec3 c = f.Centroid();
            float d = n.Dot(c);
            mf.Add((n, d, c));
        }

        int count = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            Vec3 gn = f.Plane.Normal;
            var gc = new Vec3(0, 0, 0);
            int nv = 0;
            foreach (FaceVertex v in f.Vertices)
            {
                if (v.Index >= 0 && v.Index < g.Vertices.Count)
                {
                    gc = gc.Add(g.Vertices[v.Index]);
                    nv++;
                }
            }

            if (nv == 0)
            {
                continue;
            }

            gc = gc.Scale(1f / nv);
            float gd = gn.Dot(gc);
            foreach ((Vec3 N, float D, Vec3 C) in mf)
            {
                float dot = Math.Abs(gn.Dot(N));
                if (dot > 0.999f && Math.Abs(gd - (gn.Dot(N) >= 0 ? D : -D)) < 0.05f && gc.Sub(C).Length() < 0.35f)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    public void Dump_All_Portals_Shipping(string file)
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, file);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? red = null;
        List<RoomEffect> effects = new();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                red ??= gs.Geometry;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                effects = es.Effects;
            }
        }

        if (red is null)
        {
            return;
        }

        List<Brush> brushes = MoverBrushes.StaticWorldBrushes(rfl);
        Geometry ged = GeometryCompiler.Compile(brushes, effects, new CompileOptions { Alpine = rfl.Context.IsAlpine, BuildSurfaces = false }).Geometry;

        var sb = new StringBuilder();
        sb.AppendLine($"ALL PORTALS (shipping) — {file}");
        sb.AppendLine($"RED portals={red.Portals.Count} GED portals={ged.Portals.Count}");
        sb.AppendLine();

        int redLiq = FirstLiquid(red), gedLiq = FirstLiquid(ged);
        sb.AppendLine($"RED liquid room idx={redLiq} aabb={(redLiq >= 0 ? Center(red.Rooms[redLiq].Aabb) + " " + Size(red.Rooms[redLiq].Aabb) : "-")}");
        sb.AppendLine($"GED liquid room idx={gedLiq} aabb={(gedLiq >= 0 ? Center(ged.Rooms[gedLiq].Aabb) + " " + Size(ged.Rooms[gedLiq].Aabb) : "-")}");
        sb.AppendLine();

        sb.AppendLine("== RED portals touching liquid room ==");
        DumpPortalsTouching(sb, red, redLiq);
        sb.AppendLine("== GED portals touching liquid room ==");
        DumpPortalsTouching(sb, ged, gedLiq);
        sb.AppendLine();

        // For each GED portal touching liquid, show the neighbor rooms' AABBs (to see over-segmentation).
        sb.AppendLine("== GED liquid-neighbor rooms ==");
        if (gedLiq >= 0)
        {
            var neigh = new HashSet<int>();
            foreach (Portal p in ged.Portals)
            {
                if (p.RoomIndex1 == gedLiq) neigh.Add(p.RoomIndex2);
                if (p.RoomIndex2 == gedLiq) neigh.Add(p.RoomIndex1);
            }

            foreach (int r in neigh.OrderBy(x => x))
            {
                Room rm = ged.Rooms[r];
                sb.AppendLine($"  ged room#{r} sub={rm.IsSubroom} liq={rm.IsLiquidRoom} id=0x{rm.Id:X} center={Center(rm.Aabb)} size={Size(rm.Aabb)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("== RED liquid-neighbor rooms ==");
        if (redLiq >= 0)
        {
            var neigh = new HashSet<int>();
            foreach (Portal p in red.Portals)
            {
                if (p.RoomIndex1 == redLiq) neigh.Add(p.RoomIndex2);
                if (p.RoomIndex2 == redLiq) neigh.Add(p.RoomIndex1);
            }

            foreach (int r in neigh.OrderBy(x => x))
            {
                Room rm = red.Rooms[r];
                sb.AppendLine($"  red room#{r} sub={rm.IsSubroom} liq={rm.IsLiquidRoom} id=0x{rm.Id:X} center={Center(rm.Aabb)} size={Size(rm.Aabb)}");
            }
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass23_allportals_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());
    }

    private static int FirstLiquid(Geometry g)
    {
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsLiquidRoom != 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static void DumpPortalsTouching(StringBuilder sb, Geometry g, int room)
    {
        for (int pi = 0; pi < g.Portals.Count; pi++)
        {
            Portal p = g.Portals[pi];
            if (p.RoomIndex1 == room || p.RoomIndex2 == room)
            {
                var sz = new Vec3(Math.Abs(p.Point2.X - p.Point1.X), Math.Abs(p.Point2.Y - p.Point1.Y), Math.Abs(p.Point2.Z - p.Point1.Z));
                sb.AppendLine($"  portal#{pi} rooms {p.RoomIndex1}<->{p.RoomIndex2} center=({(p.Point1.X + p.Point2.X) / 2:F2},{(p.Point1.Y + p.Point2.Y) / 2:F2},{(p.Point1.Z + p.Point2.Z) / 2:F2}) size=({sz.X:F2}x{sz.Y:F2}x{sz.Z:F2})");
            }
        }
    }

    private static void DumpWaterPortals(StringBuilder sb, string tag, Geometry g)
    {
        var liquidRooms = new List<int>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsLiquidRoom != 0)
            {
                liquidRooms.Add(i);
            }
        }

        sb.AppendLine($"  {tag}: liquid room indices=[{string.Join(",", liquidRooms)}] totalPortals={g.Portals.Count}");
        for (int pi = 0; pi < g.Portals.Count; pi++)
        {
            Portal p = g.Portals[pi];
            if (liquidRooms.Contains(p.RoomIndex1) || liquidRooms.Contains(p.RoomIndex2))
            {
                var sz = new Vec3(Math.Abs(p.Point2.X - p.Point1.X), Math.Abs(p.Point2.Y - p.Point1.Y), Math.Abs(p.Point2.Z - p.Point1.Z));
                sb.AppendLine($"    portal#{pi} rooms {p.RoomIndex1}<->{p.RoomIndex2} p1=({p.Point1.X:F2},{p.Point1.Y:F2},{p.Point1.Z:F2}) p2=({p.Point2.X:F2},{p.Point2.Y:F2},{p.Point2.Z:F2}) size=({sz.X:F2}x{sz.Y:F2}x{sz.Z:F2})");
            }
        }
    }

    private static double Iou(Aabb a, Aabb b)
    {
        float ix = Math.Max(0, Math.Min(a.P2.X, b.P2.X) - Math.Max(a.P1.X, b.P1.X));
        float iy = Math.Max(0, Math.Min(a.P2.Y, b.P2.Y) - Math.Max(a.P1.Y, b.P1.Y));
        float iz = Math.Max(0, Math.Min(a.P2.Z, b.P2.Z) - Math.Max(a.P1.Z, b.P1.Z));
        double inter = (double)ix * iy * iz;
        double union = Volume(a) + Volume(b) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static double Volume(Aabb a) =>
        Math.Abs((double)(a.P2.X - a.P1.X) * (a.P2.Y - a.P1.Y) * (a.P2.Z - a.P1.Z));

    private static string Center(Aabb a) =>
        $"({(a.P1.X + a.P2.X) * 0.5f:F1},{(a.P1.Y + a.P2.Y) * 0.5f:F1},{(a.P1.Z + a.P2.Z) * 0.5f:F1})";

    private static string Size(Aabb a) =>
        $"({a.P2.X - a.P1.X:F1}x{a.P2.Y - a.P1.Y:F1}x{a.P2.Z - a.P1.Z:F1})";

    private static void WriteArtifact(string name, string text)
    {
        DirectoryInfo? dir = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
        if (dir is null)
        {
            return;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, name), text);
    }

    private static DirectoryInfo? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        return dir;
    }
}
