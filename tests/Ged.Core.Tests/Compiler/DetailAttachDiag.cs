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
/// Flagship 26 DIAGNOSTIC — detail-room (subroom) PARENT-SET parity, RED original vs GED recompile.
/// RF renders a detail room's faces when ANY of its PARENT (container) rooms is in the portal-visible
/// set. RED lists a detail room under EVERY main room it borders/spans (multi-parent); GED currently
/// attaches each detail room to ONE parent (smallest-volume containing main room). Where a detail room
/// borders multiple main rooms, GED's single-parent attach makes it vanish at camera angles where a
/// NON-parent room is the one seen through the portal chain — the reported angle-dependent brush drop.
/// This dumps the FULL enumeration of detail rooms whose GED parent set differs from RED's spatially
/// matched room, and diagnoses Goober's seven witnessed brush UIDs individually.
/// </summary>
public sealed class DetailAttachDiag
{
    private readonly ITestOutputHelper _out;

    public DetailAttachDiag(ITestOutputHelper output) => _out = output;

    // Goober's witnessed vanishing brush UIDs (SAMPLES of the defect class, not exhaustive).
    private static readonly int[] WitnessedUids = { 10989, 10990, 10753, 63, 10400, 10403, 11040 };

    // These are exploratory diagnostics that recompile corpus levels; they contend with the
    // timing-sensitive QaCorpusSweep when run in the parallel suite, so they are opt-in (set
    // GED_DETAIL_ATTACH_MEASURE=1 to regenerate the artifacts). The correctness gate lives in
    // DmabruptDetailAttachGate and always runs.
    private static bool MeasureEnabled => Environment.GetEnvironmentVariable("GED_DETAIL_ATTACH_MEASURE") == "1";

    [Fact]
    public void Dump_Detail_Attach_Dmabrupt()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path) || !Load(path, out Geometry red, out List<Brush> brushes, out List<RoomEffect> effects))
        {
            return;
        }

        Geometry ged = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;
        string report = Analyze("dmabruptdecayrc2a27.rfl", red, ged, brushes, WitnessedUids);
        _out.WriteLine(report);
        WriteArtifact("detail_attach_dmabrupt.txt", report);
    }

    /// <summary>
    /// RULE DISCOVERY — operate ONLY on RED's parsed geometry (rooms + ground-truth subroom lists) and
    /// measure which geometric predicate reproduces RED's parent sets. For each RED subroom, compare its
    /// actual parents to: (a) main rooms whose AABB overlaps the subroom AABB; (b) main rooms whose AABB
    /// contains the subroom center; (c) the single smallest-volume containing main room (current GED). We
    /// report precision/recall so the exact attach predicate is pinned from ground truth.
    /// </summary>
    [Fact]
    public void Discover_Red_Attach_Rule_Dmabrupt()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, "dmabruptdecayrc2a27.rfl");
        if (!File.Exists(path) || !Load(path, out Geometry red, out _, out _))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== RED attach-rule discovery (dmabrupt, RED ground truth only) ===");
        var mainIdx = new List<int>();
        for (int i = 0; i < red.Rooms.Count; i++)
        {
            if (red.Rooms[i].IsSubroom == 0)
            {
                mainIdx.Add(i);
            }
        }

        var subs = SubroomIdx(red);
        var parents = ParentsOf(red);
        // sweep a few epsilons for containment/overlap slack
        foreach (float eps in new[] { 0.0f, 0.05f, 0.25f, 1.0f })
        {
            int pEntries = 0, oRecall = 0, oPred = 0, oHit = 0;
            foreach (int s in subs)
            {
                Aabb sa = red.Rooms[s].Aabb;
                Vec3 c = CenterVec(sa);
                var P = parents.GetValueOrDefault(s, new List<int>()).ToHashSet();
                pEntries += P.Count;
                var over = new HashSet<int>();
                foreach (int m in mainIdx)
                {
                    if (Overlaps(red.Rooms[m].Aabb, sa, eps))
                    {
                        over.Add(m);
                    }
                }

                oRecall += P.Count(p => over.Contains(p));
                oPred += over.Count;
                oHit += over.Count(m => P.Contains(m));
            }

            sb.AppendLine($"  eps={eps,4}: overlap rule -> recall {oRecall}/{pEntries} ({Pct(oRecall, pEntries)}), precision {oHit}/{oPred} ({Pct(oHit, oPred)})");
        }

        sb.AppendLine();
        // containment-of-center rules
        foreach (float eps in new[] { 0.0f, 0.05f, 0.25f })
        {
            int pEntries = 0, cRecall = 0, cPred = 0, cHit = 0, smallestHit = 0, smallestPred = 0;
            foreach (int s in subs)
            {
                Aabb sa = red.Rooms[s].Aabb;
                Vec3 c = CenterVec(sa);
                var P = parents.GetValueOrDefault(s, new List<int>()).ToHashSet();
                pEntries += P.Count;
                var contain = new List<int>();
                foreach (int m in mainIdx)
                {
                    if (ContainsPt(red.Rooms[m].Aabb, c, eps))
                    {
                        contain.Add(m);
                    }
                }

                cRecall += P.Count(p => contain.Contains(p));
                cPred += contain.Count;
                cHit += contain.Count(m => P.Contains(m));
                if (contain.Count > 0)
                {
                    int smallest = contain.OrderBy(m => Volume(red.Rooms[m].Aabb)).First();
                    smallestPred++;
                    if (P.Contains(smallest))
                    {
                        smallestHit++;
                    }
                }
            }

            sb.AppendLine($"  eps={eps,4}: center-contain -> recall {cRecall}/{pEntries} ({Pct(cRecall, pEntries)}), precision {cHit}/{cPred} ({Pct(cHit, cPred)}); smallest-containing hit {smallestHit}/{smallestPred} ({Pct(smallestHit, smallestPred)})");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact("detail_attach_rule_discovery.txt", sb.ToString());
    }

    private static Vec3 CenterVec(Aabb a) =>
        new((a.P1.X + a.P2.X) * 0.5f, (a.P1.Y + a.P2.Y) * 0.5f, (a.P1.Z + a.P2.Z) * 0.5f);

    private static bool Overlaps(Aabb a, Aabb b, float eps) =>
        a.P1.X - eps <= b.P2.X && a.P2.X + eps >= b.P1.X &&
        a.P1.Y - eps <= b.P2.Y && a.P2.Y + eps >= b.P1.Y &&
        a.P1.Z - eps <= b.P2.Z && a.P2.Z + eps >= b.P1.Z;

    private static bool ContainsPt(Aabb box, Vec3 p, float eps) =>
        p.X >= box.P1.X - eps && p.X <= box.P2.X + eps &&
        p.Y >= box.P1.Y - eps && p.Y <= box.P2.Y + eps &&
        p.Z >= box.P1.Z - eps && p.Z <= box.P2.Z + eps;

    private static string Pct(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:F1}%";

    [Fact]
    public void Sweep_Detail_Attach_Corpus()
    {
        if (!Corpus.Available || !MeasureEnabled)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("detail-room parent-set parity corpus sweep (RED original vs GED recompile)");
        sb.AppendLine($"{"level",-32} {"redSub",7} {"gedSub",7} {"redEnt",7} {"gedEnt",7} {"matched",8} {"under",6} {"over",5} {"miss",5}");
        foreach (string p in Corpus.RflFiles)
        {
            string name = Path.GetFileName(p);
            if (name.Contains(".autosave", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ged", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("~.rfl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Load(p, out Geometry red, out List<Brush> brushes, out List<RoomEffect> effects))
            {
                continue;
            }

            Geometry ged;
            try
            {
                ged = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false }).Geometry;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name,-32} EXCEPTION {ex.GetType().Name}");
                continue;
            }

            Stats s = Measure(red, ged);
            sb.AppendLine($"{name,-32} {s.RedSub,7} {s.GedSub,7} {s.RedEntries,7} {s.GedEntries,7} {s.Matched,8} {s.UnderAttached,6} {s.OverAttached,5} {s.Unmatched,5}");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact("detail_attach_corpus.txt", sb.ToString());
    }

    // ---- core analysis -------------------------------------------------------------------

    private sealed record Stats(int RedSub, int GedSub, int RedEntries, int GedEntries, int Matched,
        int UnderAttached, int OverAttached, int Unmatched);

    private static Stats Measure(Geometry red, Geometry ged)
    {
        var redMain = MainRooms(red, out int[] redMainSlot);
        var gedMain = MainRooms(ged, out int[] gedMainSlot);
        int[] redToGed = GreedyMatch(redMain, gedMain);

        var redParents = ParentsOf(red);
        var gedParents = ParentsOf(ged);

        var redSubs = SubroomIdx(red);
        var gedSubs = SubroomIdx(ged);

        int redEntries = redParents.Values.Sum(v => v.Count);
        int gedEntries = gedParents.Values.Sum(v => v.Count);

        int matched = 0, under = 0, over = 0, unmatched = 0;
        foreach (int gs in gedSubs)
        {
            int rs = BestSubroomMatch(ged.Rooms[gs].Aabb, red, redSubs);
            if (rs < 0)
            {
                unmatched++;
                continue;
            }

            matched++;
            var redSet = redParents.GetValueOrDefault(rs, new List<int>())
                .Select(p => redMainSlot[p]).Where(x => x >= 0).Select(x => redToGed[x]).Where(x => x >= 0).ToHashSet();
            var gedSet = gedParents.GetValueOrDefault(gs, new List<int>())
                .Select(p => gedMainSlot[p]).Where(x => x >= 0).ToHashSet();
            if (redSet.Except(gedSet).Any())
            {
                under++;   // RED lists a parent GED lacks -> vanishing risk
            }

            if (gedSet.Except(redSet).Any())
            {
                over++;
            }
        }

        return new Stats(redSubs.Count, gedSubs.Count, redEntries, gedEntries, matched, under, over, unmatched);
    }

    private static string Analyze(string file, Geometry red, Geometry ged, List<Brush> brushes, int[] witnessed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== detail-room parent-set parity — {file} ===");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        var redMain = MainRooms(red, out int[] redMainSlot);
        var gedMain = MainRooms(ged, out int[] gedMainSlot);
        int[] redToGed = GreedyMatch(redMain, gedMain);

        var redParents = ParentsOf(red);
        var gedParents = ParentsOf(ged);
        var redSubs = SubroomIdx(red);
        var gedSubs = SubroomIdx(ged);

        int redEntries = redParents.Values.Sum(v => v.Count);
        int gedEntries = gedParents.Values.Sum(v => v.Count);

        sb.AppendLine($"rooms: RED {red.Rooms.Count} ({redMain.Count} main / {redSubs.Count} sub) | GED {ged.Rooms.Count} ({gedMain.Count} main / {gedSubs.Count} sub)");
        sb.AppendLine($"subroom child-entries (sum over lists): RED {redEntries} | GED {gedEntries}");
        int redMulti = redParents.Count(kv => kv.Value.Count > 1);
        int gedMulti = gedParents.Count(kv => kv.Value.Count > 1);
        sb.AppendLine($"detail rooms with >1 parent: RED {redMulti} | GED {gedMulti}");
        sb.AppendLine();

        // ---- full enumeration of GED subrooms whose parent set differs from RED's matched subroom ----
        sb.AppendLine("== FULL parent-set MISMATCH enumeration (GED sub -> RED matched sub) ==");
        sb.AppendLine("  legend: UNDER = RED lists a parent GED omits (VANISHING RISK); OVER = GED lists extra parent");
        int under = 0, over = 0, unmatched = 0, matched = 0;
        var mismatchLines = new List<(double vol, string line)>();
        foreach (int gs in gedSubs)
        {
            Aabb ab = ged.Rooms[gs].Aabb;
            int rs = BestSubroomMatch(ab, red, redSubs);
            if (rs < 0)
            {
                unmatched++;
                continue;
            }

            matched++;
            var redSet = redParents.GetValueOrDefault(rs, new List<int>())
                .Select(p => redMainSlot[p]).Where(x => x >= 0).Select(x => redToGed[x]).Where(x => x >= 0).ToHashSet();
            var gedSet = gedParents.GetValueOrDefault(gs, new List<int>())
                .Select(p => gedMainSlot[p]).Where(x => x >= 0).ToHashSet();
            var missing = redSet.Except(gedSet).ToList();
            var extra = gedSet.Except(redSet).ToList();
            if (missing.Count == 0 && extra.Count == 0)
            {
                continue;
            }

            if (missing.Count > 0)
            {
                under++;
            }

            if (extra.Count > 0)
            {
                over++;
            }

            string tag = missing.Count > 0 ? "UNDER" : "over";
            string mParents = string.Join(",", missing.Select(m => $"gm#{m}{CenterShort(gedMain[m].Aabb)}"));
            string eParents = string.Join(",", extra.Select(m => $"gm#{m}{CenterShort(gedMain[m].Aabb)}"));
            mismatchLines.Add((Volume(ab), $"  [{tag}] gedSub#{gs} c={Center(ab)} sz={Size(ab)} redParents={redSet.Count} gedParents={gedSet.Count}" +
                (missing.Count > 0 ? $"  MISSING:[{mParents}]" : "") + (extra.Count > 0 ? $"  EXTRA:[{eParents}]" : "")));
        }

        foreach (var (_, line) in mismatchLines.OrderByDescending(x => x.vol))
        {
            sb.AppendLine(line);
        }

        sb.AppendLine();
        sb.AppendLine($"summary: matched {matched}/{gedSubs.Count} GED subrooms; UNDER-attached {under} (vanishing risk); OVER-attached {over}; unmatched {unmatched}");
        sb.AppendLine();

        // ---- per-UID diagnosis for the witnessed brushes ----
        sb.AppendLine("== PER-UID diagnosis (Goober's witnessed brushes) ==");
        foreach (int uid in witnessed)
        {
            Brush? b = brushes.FirstOrDefault(x => x.Uid == uid);
            if (b is null)
            {
                sb.AppendLine($"  uid {uid}: NOT FOUND in brushes section (may be a mover-owned brush excluded from the static fold)");
                continue;
            }

            bool isDetail = (b.Flags & (uint)BrushFlags.Detail) != 0;
            bool isAir = (b.Flags & (uint)BrushFlags.Air) != 0;
            bool isPortal = (b.Flags & (uint)BrushFlags.Portal) != 0;
            Aabb wb = WorldAabb(b);
            sb.AppendLine($"  uid {uid}: flags=0x{b.Flags:X} detail={isDetail} air={isAir} portal={isPortal} worldAABB c={Center(wb)} sz={Size(wb)}");
            if (!isDetail)
            {
                sb.AppendLine($"      -> NOT a detail brush: its faces are world CSG geometry, not a subroom. Different mechanism (not single-parent attach).");
            }

            int gs = BestSubroomMatchByAabb(wb, ged, gedSubs);
            int rs = BestSubroomMatchByAabb(wb, red, redSubs);
            if (gs >= 0)
            {
                var gedSet = gedParents.GetValueOrDefault(gs, new List<int>())
                    .Select(p => gedMainSlot[p]).Where(x => x >= 0).ToList();
                sb.AppendLine($"      GED subroom#{gs} c={Center(ged.Rooms[gs].Aabb)} sz={Size(ged.Rooms[gs].Aabb)} parents(gm#)=[{string.Join(",", gedSet.Select(x => $"{x}{CenterShort(gedMain[x].Aabb)}"))}]");
            }
            else
            {
                int host = SmallestContainingRoomAny(ged, CenterVec(wb));
                string hk = host < 0 ? "none" : (ged.Rooms[host].IsSubroom != 0 ? "SUBROOM" : "MAIN");
                sb.AppendLine($"      GED: no subroom matches this brush's world AABB (IoU too low) — subroom granularity gap (flagship-20 item 3b), NOT an attach failure.");
                sb.AppendLine($"      GED space at brush centre -> room#{host} ({hk}) c={(host < 0 ? "-" : Center(ged.Rooms[host].Aabb))} sz={(host < 0 ? "-" : Size(ged.Rooms[host].Aabb))}" +
                    (host >= 0 && ged.Rooms[host].IsSubroom != 0 ? $" parents(gm#)=[{string.Join(",", gedParents.GetValueOrDefault(host, new List<int>()).Select(p => gedMainSlot[p]).Where(x => x >= 0))}]" : ""));
            }

            if (rs >= 0)
            {
                var redSet = redParents.GetValueOrDefault(rs, new List<int>()).ToList();
                var redSetInGed = redSet.Select(p => redMainSlot[p]).Where(x => x >= 0).Select(x => redToGed[x]).Where(x => x >= 0).ToList();
                sb.AppendLine($"      RED subroom#{rs} c={Center(red.Rooms[rs].Aabb)} sz={Size(red.Rooms[rs].Aabb)} parents(redRoom)=[{string.Join(",", redSet)}] -> mapped gm#=[{string.Join(",", redSetInGed)}]");
            }
            else
            {
                sb.AppendLine($"      RED: no subroom matches this brush's world AABB.");
            }

            // verdict
            if (gs >= 0 && rs >= 0)
            {
                var redSet = redParents.GetValueOrDefault(rs, new List<int>())
                    .Select(p => redMainSlot[p]).Where(x => x >= 0).Select(x => redToGed[x]).Where(x => x >= 0).ToHashSet();
                var gedSet = gedParents.GetValueOrDefault(gs, new List<int>())
                    .Select(p => gedMainSlot[p]).Where(x => x >= 0).ToHashSet();
                var missing = redSet.Except(gedSet).ToList();
                sb.AppendLine(missing.Count > 0
                    ? $"      VERDICT: UNDER-ATTACHED — RED gives {redSet.Count} parents, GED gives {gedSet.Count}; MISSING {missing.Count} (single-vs-multi-parent CONFIRMED)."
                    : $"      VERDICT: parent sets AGREE ({redSet.Count} vs {gedSet.Count}) — vanishing here is NOT single-parent attach (investigate other mechanism).");
            }

            sb.AppendLine();
        }

        return sb.ToString();
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

    private static List<int> SubroomIdx(Geometry g)
    {
        var list = new List<int>();
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsSubroom != 0)
            {
                list.Add(i);
            }
        }

        return list;
    }

    /// <summary>Room index -> list of parent room indices (main rooms whose subroom list names it).</summary>
    private static Dictionary<int, List<int>> ParentsOf(Geometry g)
    {
        var d = new Dictionary<int, List<int>>();
        foreach (SubroomList sl in g.SubroomLists)
        {
            foreach (int child in sl.SubroomIndices)
            {
                if (!d.TryGetValue(child, out var l))
                {
                    l = new List<int>();
                    d[child] = l;
                }

                if (!l.Contains(sl.RoomIndex))
                {
                    l.Add(sl.RoomIndex);
                }
            }
        }

        return d;
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

    /// <summary>Best-IoU subroom in <paramref name="other"/> for a given AABB (>=0.05), else -1.</summary>
    private static int BestSubroomMatch(Aabb a, Geometry other, List<int> otherSubs)
    {
        int best = -1;
        double bestIou = 0.05;
        foreach (int s in otherSubs)
        {
            double iou = Iou(a, other.Rooms[s].Aabb);
            if (iou > bestIou)
            {
                bestIou = iou;
                best = s;
            }
        }

        return best;
    }

    private static int BestSubroomMatchByAabb(Aabb a, Geometry g, List<int> subs) => BestSubroomMatch(a, g, subs);

    private static int SmallestContainingRoomAny(Geometry g, Vec3 p)
    {
        int best = -1;
        double bestVol = double.MaxValue;
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            Aabb a = g.Rooms[i].Aabb;
            if (p.X < a.P1.X - 0.1f || p.X > a.P2.X + 0.1f || p.Y < a.P1.Y - 0.1f ||
                p.Y > a.P2.Y + 0.1f || p.Z < a.P1.Z - 0.1f || p.Z > a.P2.Z + 0.1f)
            {
                continue;
            }

            double vol = Volume(a);
            if (vol < bestVol)
            {
                bestVol = vol;
                best = i;
            }
        }

        return best;
    }

    private static Aabb WorldAabb(Brush b)
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

        return new Aabb(mn, mx);
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

    private static string CenterShort(Aabb a) =>
        $"({(a.P1.X + a.P2.X) * 0.5f:F0},{(a.P1.Y + a.P2.Y) * 0.5f:F0},{(a.P1.Z + a.P2.Z) * 0.5f:F0})";

    private static string Size(Aabb a) =>
        $"({a.P2.X - a.P1.X:F1}x{a.P2.Y - a.P1.Y:F1}x{a.P2.Z - a.P1.Z:F1})";

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

    private static void WriteArtifact(string name, string text)
    {
        string? envRoot = Environment.GetEnvironmentVariable("GED_REPO_ROOT");
        DirectoryInfo? dir =
            (envRoot is not null && Directory.Exists(envRoot) ? new DirectoryInfo(envRoot) : null)
            ?? FindRepoRoot(AppContext.BaseDirectory)
            ?? FindRepoRoot(Directory.GetCurrentDirectory());
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
