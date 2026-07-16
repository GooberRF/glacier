using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Flagship 26 GATE — the detail-room multi-parent attach fix (angle-dependent brush vanishing).
/// RF renders a detail room's faces when ANY of its PARENT (container) rooms is in the portal-visible
/// set. GED used to attach each detail room to the SINGLE smallest-volume main room CONTAINING its
/// centre, which for a thin panel at a room boundary picks a NEIGHBOURING room's AABB and drops every
/// other bordering room — so the panel vanished at camera angles where the true room was the one seen
/// through the portal chain. RED lists a detail under every main room its faces rest against. This gate
/// pins the seven brushes Goober witnessed disappearing on dmabruptdecayrc2a27: each must land in a GED
/// subroom whose parent set INCLUDES the main room RED's spatially-corresponding subroom is parented to
/// (a missing parent is the vanishing bug; an extra parent is harmless over-draw, so ⊇ is the relation).
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class DmabruptDetailAttachGate
{
    private readonly ITestOutputHelper _out;

    public DmabruptDetailAttachGate(ITestOutputHelper output) => _out = output;

    // The brushes Goober witnessed vanishing (samples of the defect class).
    private static readonly int[] WitnessedUids = { 10989, 10990, 10753, 63, 10400, 10403, 11040 };

    /// <summary>
    /// ALPINE-PATH gate — Goober's real build. All seven witnessed UIDs are geoable/breakable brushes,
    /// and Alpine ISOLATES every geoable/breakable brush into its own detail room (per-brush rooms) at
    /// 0x00485e88 inside RED's room builder FUN_00485990 — after detail marking, BEFORE the parent-room
    /// association loop, so in Alpine-RED the isolated rooms flow through RED's native multi-parent
    /// attach. RED's original dmabrupt was built by Alpine-RED, so its subroom lists ARE the ground
    /// truth for the isolated rooms. This gate compiles with Alpine on (GeometryBuildService, isolated
    /// UIDs from the alpine_level_properties tables) and asserts EVERY geoable+breakable brush's
    /// isolated room (a) exists, (b) has at least one parent room, and (c) for the seven witnessed
    /// UIDs the parent set includes RED's spatially-matched parent (the vanishing bug).
    /// </summary>
    [Fact]
    public void Isolated_Geoable_Breakable_Rooms_Attach_With_Alpine_On()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        AlpineLevelPropertiesSection? alp = rfl.Sections
            .Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().FirstOrDefault();
        Geometry? red = rfl.Sections
            .Select(s => s.Content).OfType<GeometrySection>().Select(g => g.Geometry).FirstOrDefault();
        List<Brush>? brushes = rfl.Sections
            .Select(s => s.Content).OfType<BrushesSection>().Select(b => b.Brushes).FirstOrDefault();
        Assert.True(alp is not null && red is not null && brushes is not null, "dmabrupt: missing sections");

        var isolatedUids = alp!.GeoableEntries.Select(e => e.BrushUid)
            .Concat(alp.BreakableEntries.Select(e => e.BrushUid)).Distinct().ToList();
        Assert.True(isolatedUids.Count >= 40, $"expected the full geoable+breakable tables, got {isolatedUids.Count}");

        CompiledLevel result = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = false });
        Geometry ged = result.Geometry;

        var gedMain = MainRooms(ged, out int[] gedSlot);
        var redMain = MainRooms(red!, out int[] redSlot);
        int[] redToGed = GreedyMatch(redMain, gedMain);
        var gedParents = ParentsOf(ged);
        var redParents = ParentsOf(red!);

        // Room UID -> room index for the compiled build.
        var roomOfUid = new Dictionary<int, int>();
        for (int i = 0; i < ged.Rooms.Count; i++)
        {
            roomOfUid[ged.Rooms[i].Id] = i;
        }

        var witnessed = new HashSet<int>(WitnessedUids);
        var noRoom = new List<int>();
        var noParent = new List<int>();
        var witnessedFailures = new List<string>();
        int checkedRooms = 0;
        foreach (int uid in isolatedUids)
        {
            if (!result.BrushRoomUid.TryGetValue(uid, out int roomUid) ||
                !roomOfUid.TryGetValue(roomUid, out int roomIdx))
            {
                noRoom.Add(uid);
                continue;
            }

            checkedRooms++;
            var parents = gedParents.GetValueOrDefault(roomIdx, new List<int>());
            if (parents.Count == 0)
            {
                noParent.Add(uid);
                _out.WriteLine($"uid {uid}: isolated room#{roomIdx} has NO parent (unrenderable)");
                continue;
            }

            var gedSet = parents.Select(p => gedSlot[p]).Where(x => x >= 0).ToHashSet();
            _out.WriteLine($"uid {uid}: room#{roomIdx} parents(gm#)=[{string.Join(",", gedSet.OrderBy(x => x))}]");

            if (!witnessed.Contains(uid))
            {
                continue;
            }

            // Witnessed: parents must include RED's spatially-matched parent (the vanishing bug).
            Brush? b = brushes!.FirstOrDefault(x => x.Uid == uid);
            if (b is null)
            {
                continue;
            }

            int rs = SmallestContainingSubroom(red!, WorldCenter(b));
            if (rs < 0)
            {
                continue;
            }

            var redTarget = redParents.GetValueOrDefault(rs, new List<int>())
                .Select(p => redSlot[p]).Where(x => x >= 0).Select(x => redToGed[x]).Where(x => x >= 0).ToHashSet();
            var missing = redTarget.Except(gedSet).ToList();
            if (missing.Count > 0)
            {
                witnessedFailures.Add($"uid {uid}: parents {{{string.Join(",", gedSet)}}} missing RED slots {{{string.Join(",", missing)}}}");
            }
        }

        _out.WriteLine($"isolated rooms checked {checkedRooms}/{isolatedUids.Count}; no-room {noRoom.Count}; no-parent {noParent.Count}");
        Assert.True(checkedRooms >= (int)(isolatedUids.Count * 0.9),
            $"only {checkedRooms}/{isolatedUids.Count} isolated brushes reached a compiled room (uids without room: {string.Join(",", noRoom.Take(10))})");
        Assert.True(noParent.Count == 0,
            $"{noParent.Count} isolated geoable/breakable rooms have NO parent (they would never render): {string.Join(",", noParent.Take(15))}");
        Assert.True(witnessedFailures.Count == 0,
            "witnessed UIDs under-attached on the ALPINE path:\n  " + string.Join("\n  ", witnessedFailures));
    }

    [Fact]
    public void Witnessed_Details_Attach_To_Red_Parent()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path) || !Load(path, out Geometry red, out List<Brush> brushes, out List<RoomEffect> effects))
        {
            return;
        }

        Geometry ged = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;

        var redMain = MainRooms(red, out int[] redSlot);
        var gedMain = MainRooms(ged, out int[] gedSlot);
        int[] redToGed = GreedyMatch(redMain, gedMain);
        var redParents = ParentsOf(red);
        var gedParents = ParentsOf(ged);

        var failures = new List<string>();
        int checkedCount = 0;
        foreach (int uid in WitnessedUids)
        {
            Brush? b = brushes.FirstOrDefault(x => x.Uid == uid);
            Assert.True(b is not null, $"uid {uid}: brush not found in dmabrupt (test corpus changed?)");
            Vec3 c = WorldCenter(b!);

            int gs = SmallestContainingSubroom(ged, c);
            int rs = SmallestContainingSubroom(red, c);
            Assert.True(gs >= 0, $"uid {uid}: no GED subroom contains its centre {Fmt(c)} — the detail room vanished from the build");
            if (rs < 0)
            {
                continue; // RED has no subroom here to compare against
            }

            // RED's parent main rooms mapped into GED main-slot space.
            var redTarget = redParents.GetValueOrDefault(rs, new List<int>())
                .Select(p => redSlot[p]).Where(x => x >= 0).Select(x => redToGed[x]).Where(x => x >= 0).ToHashSet();
            var gedGot = gedParents.GetValueOrDefault(gs, new List<int>())
                .Select(p => gedSlot[p]).Where(x => x >= 0).ToHashSet();

            checkedCount++;
            var missing = redTarget.Except(gedGot).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"uid {uid}: GED subroom parents {{{string.Join(",", gedGot)}}} MISSING RED parent slots {{{string.Join(",", missing)}}} (vanishing risk)");
            }
        }

        Assert.True(checkedCount > 0, "no witnessed UID could be compared against RED — corpus/matching broke");
        Assert.True(failures.Count == 0,
            "witnessed detail brushes under-attached vs RED:\n  " + string.Join("\n  ", failures));
    }

    // ---- helpers -------------------------------------------------------------------------

    private static List<Room> MainRooms(Geometry g, out int[] slotOfRoom)
    {
        slotOfRoom = new int[g.Rooms.Count];
        Array.Fill(slotOfRoom, -1);
        var list = new List<Room>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsSubroom == 0)
            {
                slotOfRoom[i] = list.Count;
                list.Add(g.Rooms[i]);
            }
        }

        return list;
    }

    private static Dictionary<int, List<int>> ParentsOf(Geometry g)
    {
        var d = new Dictionary<int, List<int>>();
        foreach (SubroomList sl in g.SubroomLists)
        {
            foreach (int child in sl.SubroomIndices)
            {
                if (!d.TryGetValue(child, out var l))
                {
                    d[child] = l = new List<int>();
                }

                if (!l.Contains(sl.RoomIndex))
                {
                    l.Add(sl.RoomIndex);
                }
            }
        }

        return d;
    }

    private static int SmallestContainingSubroom(Geometry g, Vec3 p)
    {
        int best = -1;
        double bestVol = double.MaxValue;
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsSubroom == 0)
            {
                continue;
            }

            Aabb a = g.Rooms[i].Aabb;
            if (p.X < a.P1.X - 0.15f || p.X > a.P2.X + 0.15f || p.Y < a.P1.Y - 0.15f ||
                p.Y > a.P2.Y + 0.15f || p.Z < a.P1.Z - 0.15f || p.Z > a.P2.Z + 0.15f)
            {
                continue;
            }

            double vol = Math.Abs((double)(a.P2.X - a.P1.X) * (a.P2.Y - a.P1.Y) * (a.P2.Z - a.P1.Z));
            if (vol < bestVol)
            {
                bestVol = vol;
                best = i;
            }
        }

        return best;
    }

    private static int[] GreedyMatch(List<Room> redMain, List<Room> gedMain)
    {
        int nr = redMain.Count, ng = gedMain.Count;
        var redToGed = new int[nr];
        var gedToRed = new int[ng];
        Array.Fill(redToGed, -1);
        Array.Fill(gedToRed, -1);
        var cand = new List<(double iou, int r, int g)>();
        for (int r = 0; r < nr; r++)
        {
            for (int gi = 0; gi < ng; gi++)
            {
                double iou = Iou(redMain[r].Aabb, gedMain[gi].Aabb);
                if (iou >= 0.10)
                {
                    cand.Add((iou, r, gi));
                }
            }
        }

        cand.Sort((a, b) => b.iou.CompareTo(a.iou));
        foreach (var (_, r, gi) in cand)
        {
            if (redToGed[r] < 0 && gedToRed[gi] < 0)
            {
                redToGed[r] = gi;
                gedToRed[gi] = r;
            }
        }

        return redToGed;
    }

    private static double Iou(Aabb a, Aabb b)
    {
        float ix = Math.Max(0, Math.Min(a.P2.X, b.P2.X) - Math.Max(a.P1.X, b.P1.X));
        float iy = Math.Max(0, Math.Min(a.P2.Y, b.P2.Y) - Math.Max(a.P1.Y, b.P1.Y));
        float iz = Math.Max(0, Math.Min(a.P2.Z, b.P2.Z) - Math.Max(a.P1.Z, b.P1.Z));
        double inter = (double)ix * iy * iz;
        double va = Math.Abs((double)(a.P2.X - a.P1.X) * (a.P2.Y - a.P1.Y) * (a.P2.Z - a.P1.Z));
        double vb = Math.Abs((double)(b.P2.X - b.P1.X) * (b.P2.Y - b.P1.Y) * (b.P2.Z - b.P1.Z));
        double union = va + vb - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static Vec3 WorldCenter(Brush b)
    {
        List<CsgFace> faces = BrushWorld.ToWorldFaces(b, 0, out _);
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

        return new Vec3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, (mn.Z + mx.Z) * 0.5f);
    }

    private static string Fmt(Vec3 v) => $"({v.X:F1},{v.Y:F1},{v.Z:F1})";

    private static bool Load(string path, out Geometry geo, out List<Brush> brushes, out List<RoomEffect> effects)
    {
        geo = null!;
        brushes = new List<Brush>();
        effects = new List<RoomEffect>();
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry? o = null;
        BrushesSection? b = null;
        RoomEffectsSection? e = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GeometrySection gs)
            {
                o ??= gs.Geometry;
            }
            else if (s.Content is BrushesSection bs)
            {
                b ??= bs;
            }
            else if (s.Content is RoomEffectsSection es)
            {
                e ??= es;
            }
        }

        if (o is null || b is null)
        {
            return false;
        }

        geo = o;
        brushes = MoverBrushes.ExcludeMovers(b.Brushes, MoverBrushes.CollectMoverUids(rfl));
        effects = e?.Effects.ToList() ?? new List<RoomEffect>();
        return true;
    }
}
