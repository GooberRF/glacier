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
/// Flagship 17 — RED GROUND-TRUTH STRUCTURAL DIFF for rooms / portals / liquid.
/// <para>
/// The proxy metric so far (hole counts) and the count-only sweep (QaParitySweepTests)
/// never compared the room/portal STRUCTURE of a community level against RED's ORIGINAL
/// compiled geometry — only GED-path-vs-GED-path. Goober's in-game evidence on
/// dmabruptdecayrc2a27 (invisible faces = wrong room membership / PVS culling; broken
/// movers = wrong room links; derendering = wrong portal linkage) is a STRUCTURE defect,
/// not a count defect (main-room / portal COUNTS are close: RED 28/46 vs GED 30/39).
/// </para>
/// <para>
/// This fixture dumps, for a level, RED's original vs GED's recompile:
///   (1) main-room correspondence by AABB spatial identity (greedy IoU match) — over/under
///       segmentation, unmatched rooms both directions;
///   (2) the portal LINKAGE GRAPH mapped through that correspondence — RED room-pair links
///       reproduced / missing / extra in GED (the PVS culling + mover-link surface);
///   (3) LIQUID room property parity (depth / colour / surface texture / visibility / type);
///   (4) face→room MEMBERSHIP divergence — RED faces whose spatially-corresponding GED room
///       differs from RED's assignment.
/// Written to tests/artifacts/red_room_structure_&lt;level&gt;.txt so a regression is caught,
/// like the Dm04 record dump. Assertions are report-first (pinned to measured floors).
/// </para>
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class RedRoomStructureDiffTests
{
    private readonly ITestOutputHelper _out;

    public RedRoomStructureDiffTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Structural_Diff_Against_Red_Original(string file)
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

        if (!Load(path, out Geometry red, out List<Brush> brushes, out List<RoomEffect> effects))
        {
            return;
        }

        CompiledLevel compiled = GeometryCompiler.Compile(
            brushes, effects, new CompileOptions { BuildSurfaces = false });
        Geometry ged = compiled.Geometry;

        var report = Diff(file, red, ged);
        _out.WriteLine(report.Text);
        WriteArtifact($"red_room_structure_{Path.GetFileNameWithoutExtension(file)}.txt", report.Text);

        // Report-first: every RED main room must correspond to SOME GED room (no lost space),
        // and the level must not explode into far more main rooms than RED (over-segmentation
        // is the in-game "portal-less junk room blanks the world" defect).
        Assert.True(report.RedMainMatched >= (int)(report.RedMain * 0.75),
            $"{file}: only {report.RedMainMatched}/{report.RedMain} RED main rooms matched a GED room");

        // Flagship 20 floors (pinned to the measured improvements — a regression trips these):
        // (1) GED must reproduce every RED portal room-pair link (PVS / mover connectivity). dmabrupt
        //     was 40/46 (6 missing hub-room links); the RF-consistent portal-side resolution hit 46/46.
        Assert.True(report.PortalLinksMissing == 0,
            $"{file}: {report.PortalLinksMissing} RED portal links absent in GED (reproduced {report.PortalLinksReproduced}/{report.PortalLinksRed})");

        // (2) GED must not over-segment past RED's main-room count (the spurious invisible-box rooms
        //     were killed; dmabrupt 30 -> 28 == RED 28). Allow no more than RED + 1.
        Assert.True(report.GedMain <= report.RedMain + 1,
            $"{file}: GED {report.GedMain} main rooms vs RED {report.RedMain} — over-segmentation");

        // (3) Face->room membership on the UNAMBIGUOUS (non-nested) faces must stay high — this is the
        //     honest signal (the naive % is nesting-saturated; GED == RED-self ~77% on dmabrupt). Measured
        //     dmabrupt 100% (== ceiling), dm04 94% (ceiling 95% — a real pre-existing red#6/ged#1 split).
        //     The floor catches a membership collapse without being brittle to dm04's known segmentation.
        double honest = report.HonestProbed == 0 ? 1.0 : report.HonestAgree / (double)report.HonestProbed;
        Assert.True(honest >= 0.90,
            $"{file}: honest membership {report.HonestAgree}/{report.HonestProbed} ({honest:P1}) below 90%");

        // Flagship 26 floors — detail-room (subroom) multi-parent attach parity. RF renders a detail
        // room's faces when ANY parent room is visible; RED lists a detail under every main room its
        // faces rest against. The single-parent attach GED used to run left details attached to one
        // (often wrong) parent — the angle-dependent vanishing Goober hit. Pin the parent-attach volume
        // to RED's: total subroom child-entries (sum over all lists) must approach RED's from within a
        // band (no massive under-attach = vanishing, no wild overshoot = flagship-20's over-attach fear),
        // and the multi-parent attach must actually be producing >1-parent details where RED has them.
        int redEntries = red.SubroomLists.Sum(s => s.SubroomIndices.Count);
        int gedEntries = ged.SubroomLists.Sum(s => s.SubroomIndices.Count);
        if (redEntries > 0)
        {
            Assert.True(gedEntries >= (int)(redEntries * 0.80),
                $"{file}: subroom child-entries {gedEntries} < 80% of RED {redEntries} — details under-attached (vanishing risk)");
            Assert.True(gedEntries <= (int)(redEntries * 1.25) + 5,
                $"{file}: subroom child-entries {gedEntries} > 125% of RED {redEntries} — details over-attached");
        }

        int redMulti = MultiParent(red);
        int gedMulti = MultiParent(ged);
        if (redMulti >= 5)
        {
            Assert.True(gedMulti > 0,
                $"{file}: RED has {redMulti} multi-parent detail rooms but GED has {gedMulti} — multi-parent attach not firing");
        }
    }

    /// <summary>Count of detail rooms listed under more than one parent (multi-parent attach).</summary>
    private static int MultiParent(Geometry g)
    {
        var count = new Dictionary<int, int>();
        foreach (SubroomList sl in g.SubroomLists)
        {
            foreach (int child in sl.SubroomIndices)
            {
                count[child] = count.GetValueOrDefault(child, 0) + 1;
            }
        }

        return count.Values.Count(v => v > 1);
    }

    /// <summary>
    /// Flagship 24 GATE — the water-room visibility fix. RED links the liquid room to the big air room
    /// through ONE portal covering the whole water surface (28.82 × 11 m ≈ 317 m²); GED's old geometric
    /// portal-side probe resolved the liquid room on BOTH sides of the near-horizontal water membrane and
    /// starved that link to ~5 m² of edge slivers (both-ways PVS collapse — the amp room vanishing). RED's
    /// face-vote room classification (FUN_004861d0) restores it. Gate: the liquid room's largest portal
    /// window is ≥ 300 m² AND GED emits no MORE portal records than RED (the spurious room1 water sliver
    /// that made GED's 47th record is gone).
    /// </summary>
    [Fact]
    public void Dmabrupt_Water_Room_Portal_Gate()
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

        int liq = -1;
        for (int i = 0; i < ged.Rooms.Count; i++)
        {
            if (ged.Rooms[i].IsLiquidRoom != 0)
            {
                liq = i;
                break;
            }
        }

        Assert.True(liq >= 0, "dmabrupt: no GED liquid room");

        float bestWindow = 0;
        foreach (Portal p in ged.Portals)
        {
            if (p.RoomIndex1 != liq && p.RoomIndex2 != liq)
            {
                continue;
            }

            float dx = Math.Abs(p.Point2.X - p.Point1.X);
            float dy = Math.Abs(p.Point2.Y - p.Point1.Y);
            float dz = Math.Abs(p.Point2.Z - p.Point1.Z);
            float min = Math.Min(dx, Math.Min(dy, dz));
            float window = min > 1e-4f ? (dx * dy * dz) / min : Math.Max(dx * dy, Math.Max(dy * dz, dx * dz));
            bestWindow = Math.Max(bestWindow, window);
        }

        Assert.True(bestWindow >= 300f,
            $"dmabrupt: liquid-room portal window {bestWindow:F1} m² < 300 (RED ≈317) — the water room is PVS-starved");

        // No extra portal records: GED must not exceed RED's count (the spurious room1 water sliver is gone).
        Assert.True(ged.Portals.Count <= red.Portals.Count,
            $"dmabrupt: GED {ged.Portals.Count} portal records vs RED {red.Portals.Count} — spurious extra record(s)");
    }

    /// <summary>
    /// DIAGNOSTIC: capture GED's per-membrane room votes on dmabrupt and dump the membranes that
    /// FAILED to divide (front room == back room, or a side is -1 / a subroom) — those are the
    /// portal records RED emits but GED does not (the missing PVS/mover links).
    /// </summary>
    [Fact]
    public void Dump_Membrane_Votes_Dmabrupt()
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

        var sb = new StringBuilder();
        RoomBuilder.CaptureJoins = true;
        try
        {
            CompiledLevel c = GeometryCompiler.Compile(brushes, effects, new CompileOptions { BuildSurfaces = false });
            var membranes = RoomBuilder.CapturedMembranes;
            sb.AppendLine($"dmabrupt membrane votes — {membranes?.Count ?? 0} membranes; RED portals {red.Portals.Count}, GED portals {c.Geometry.Portals.Count}");
            sb.AppendLine();
            int failed = 0;
            if (membranes is not null)
            {
                foreach ((int uid, Vec3 probe, int front, int back, List<Vec3>? opening) in membranes)
                {
                    bool divides = front >= 0 && back >= 0 && front != back;
                    if (!divides)
                    {
                        failed++;
                        string reason = front == back ? "SAME ROOM (no division)"
                            : front < 0 ? "front=-1 (unlocated)"
                            : back < 0 ? "back=-1 (unlocated)" : "?";
                        sb.AppendLine($"  brush uid={uid} probe=({probe.X:F1},{probe.Y:F1},{probe.Z:F1}) front={front} back={back}  {reason}");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine($"failed divisions: {failed} / {membranes?.Count ?? 0}");
        }
        finally
        {
            RoomBuilder.CaptureJoins = false;
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact("membrane_votes_dmabrupt.txt", sb.ToString());
    }

    /// <summary>
    /// DIAGNOSTIC: corpus-wide liquid-surface area, RED original vs GED recompile, for every level
    /// with a liquid room. RED's surface is double-sided, so the GED-vs-RED ratio reported is
    /// GED / (RED / 2): 200% means GED (also double-sided) reproduces RED's surface exactly.
    /// </summary>
    [Fact]
    public void Sweep_Liquid_Surface_Corpus()
    {
        if (!Corpus.Available)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("liquid-surface corpus sweep (RED double-sided area vs GED; ratio = GED/(RED/2), 200% == exact)");
        sb.AppendLine($"{"level",-30} {"redLiqRooms",11} {"redArea",9} {"gedArea",9} {"ged/(red/2)",12}");
        foreach (string path in Corpus.RflFiles)
        {
            string name = Path.GetFileName(path);
            if (name.Contains(".autosave", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Load(path, out Geometry red, out List<Brush> brushes, out List<RoomEffect> effects))
            {
                continue;
            }

            int redLiq = red.Rooms.Count(r => r.IsLiquidRoom != 0);
            if (redLiq == 0)
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
                sb.AppendLine($"{name,-30} EXCEPTION {ex.GetType().Name}");
                continue;
            }

            float redArea = LiquidArea(red);
            float gedArea = LiquidArea(ged);
            double ratio = redArea <= 0 ? 0 : gedArea / (redArea / 2.0);
            sb.AppendLine($"{name,-30} {redLiq,11} {redArea,9:F1} {gedArea,9:F1} {ratio,12:P0}");
        }

        _out.WriteLine(sb.ToString());
        WriteArtifact("liquid_surface_sweep.txt", sb.ToString());
    }

    private static float LiquidArea(Geometry g)
    {
        float area = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            bool flagged = ((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0;
            bool wtrTex = f.Texture >= 0 && f.Texture < g.Textures.Count &&
                          g.Textures[f.Texture].StartsWith("wtr_", StringComparison.OrdinalIgnoreCase);
            if (flagged || wtrTex)
            {
                area += FaceArea(g, f);
            }
        }

        return area;
    }

    // ---- structural diff -----------------------------------------------------------------

    private sealed record DiffResult(
        string Text, int RedMain, int GedMain, int RedMainMatched,
        int PortalLinksRed, int PortalLinksReproduced, int PortalLinksMissing, int PortalLinksExtra,
        int HonestProbed, int HonestAgree);

    private static DiffResult Diff(string file, Geometry red, Geometry ged)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RED ground-truth room/portal/liquid structural diff — {file}");
        sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        var redMain = MainRooms(red);
        var gedMain = MainRooms(ged);
        sb.AppendLine($"rooms: RED {red.Rooms.Count} ({redMain.Count} main) | GED {ged.Rooms.Count} ({gedMain.Count} main)");
        sb.AppendLine($"portals: RED {red.Portals.Count} | GED {ged.Portals.Count}");
        sb.AppendLine($"subroom-lists: RED {red.SubroomLists.Count} | GED {ged.SubroomLists.Count}");
        sb.AppendLine();

        // ---- (1) main-room correspondence (greedy best-IoU match) ------------------------
        int[] redToGed = GreedyMatch(redMain, gedMain, out int[] gedToRed);
        int matched = redToGed.Count(x => x >= 0);
        sb.AppendLine("== main-room correspondence (AABB IoU >= 0.10 greedy) ==");
        sb.AppendLine($"matched {matched}/{redMain.Count} RED main rooms; {gedMain.Count - gedToRed.Count(x => x >= 0)} GED main rooms unmatched (over-segmentation)");
        sb.AppendLine();

        sb.AppendLine("RED main rooms with NO GED counterpart (lost / merged-away space):");
        int lost = 0;
        for (int i = 0; i < redMain.Count; i++)
        {
            if (redToGed[i] < 0)
            {
                lost++;
                Room r = redMain[i];
                sb.AppendLine($"  red#{i} id=0x{r.Id:X} vol={Volume(r.Aabb):F1} center={Center(r.Aabb)} size={Size(r.Aabb)}");
            }
        }

        if (lost == 0)
        {
            sb.AppendLine("  (none)");
        }

        sb.AppendLine();
        sb.AppendLine("GED main rooms with NO RED counterpart (spurious / split):");
        int spurious = 0;
        for (int j = 0; j < gedMain.Count; j++)
        {
            if (gedToRed[j] < 0)
            {
                spurious++;
                Room r = gedMain[j];
                sb.AppendLine($"  ged#{j} id=0x{r.Id:X} vol={Volume(r.Aabb):F1} center={Center(r.Aabb)} size={Size(r.Aabb)}");
            }
        }

        if (spurious == 0)
        {
            sb.AppendLine("  (none)");
        }

        sb.AppendLine();

        // ---- (2) portal linkage graph mapped through the correspondence ------------------
        // Map RED room index -> RED main slot -> GED main slot; build the linked room-pair set
        // for RED (only where both endpoints matched) and for GED, then diff.
        var redMainSlotOfRoom = SlotOfRoom(red);
        var gedMainSlotOfRoom = SlotOfRoom(ged);

        var redLinks = LinkSet(red, redMainSlotOfRoom);
        var gedLinks = LinkSet(ged, gedMainSlotOfRoom);

        // Translate RED links into GED main-slot space through the match.
        var redLinksInGed = new HashSet<(int, int)>();
        int redLinksMappable = 0;
        foreach ((int a, int b) in redLinks)
        {
            int ga = redToGed[a], gb = redToGed[b];
            if (ga < 0 || gb < 0)
            {
                continue; // an endpoint has no GED counterpart — counted separately
            }

            redLinksMappable++;
            redLinksInGed.Add(ga < gb ? (ga, gb) : (gb, ga));
        }

        int reproduced = redLinksInGed.Count(l => gedLinks.Contains(l));
        int missing = redLinksInGed.Count - reproduced;
        int extra = gedLinks.Count - reproduced;

        sb.AppendLine("== portal linkage graph (main-room adjacency through the correspondence) ==");
        sb.AppendLine($"RED main-room links: {redLinks.Count} ({redLinksMappable} both-endpoints-matched)");
        sb.AppendLine($"GED main-room links: {gedLinks.Count}");
        sb.AppendLine($"reproduced: {reproduced} | missing (RED link absent in GED): {missing} | extra (GED-only link): {extra}");
        sb.AppendLine();

        sb.AppendLine("MISSING RED links (mapped GED main slots) — broken PVS/mover connectivity:");
        int shown = 0;
        foreach ((int a, int b) in redLinksInGed)
        {
            if (!gedLinks.Contains((a, b)) && shown++ < 40)
            {
                sb.AppendLine($"  gedMain#{a} <-> gedMain#{b}   (center {Center(gedMain[a].Aabb)} / {Center(gedMain[b].Aabb)})");
            }
        }

        if (missing == 0)
        {
            sb.AppendLine("  (none)");
        }

        sb.AppendLine();

        // ---- (3) liquid rooms ------------------------------------------------------------
        sb.AppendLine("== liquid rooms ==");
        var redLiquid = red.Rooms.Where(r => r.IsLiquidRoom != 0).ToList();
        var gedLiquid = ged.Rooms.Where(r => r.IsLiquidRoom != 0).ToList();
        sb.AppendLine($"RED liquid rooms: {redLiquid.Count} | GED liquid rooms: {gedLiquid.Count}");
        foreach (Room lr in redLiquid)
        {
            RoomLiquidProperties lp = lr.LiquidProperties!;
            Room? match = gedLiquid
                .OrderByDescending(g => Iou(lr.Aabb, g.Aabb))
                .FirstOrDefault();
            double iou = match is null ? 0 : Iou(lr.Aabb, match.Aabb);
            sb.AppendLine($"  RED liquid id=0x{lr.Id:X} center={Center(lr.Aabb)} size={Size(lr.Aabb)}");
            sb.AppendLine($"    depth={lp.Depth:F3} type={lp.LiquidType} tex={lp.SurfaceTexture} vis={lp.Visibility:F1} color={lp.Color.R},{lp.Color.G},{lp.Color.B}");
            if (match is null || iou < 0.05)
            {
                sb.AppendLine($"    -> NO GED liquid room matches (best IoU {iou:F2})");
            }
            else
            {
                RoomLiquidProperties gp = match.LiquidProperties!;
                sb.AppendLine($"    -> GED liquid center={Center(match.Aabb)} size={Size(match.Aabb)} IoU={iou:F2}");
                sb.AppendLine($"       depth={gp.Depth:F3} type={gp.LiquidType} tex={gp.SurfaceTexture} vis={gp.Visibility:F1} color={gp.Color.R},{gp.Color.G},{gp.Color.B}");
            }
        }

        sb.AppendLine();

        // ---- (3a2) liquid ROOM extent: room AABB vs member-face AABB vs portal growth ----
        sb.AppendLine("== liquid room extent (AABB source) ==");
        DumpLiquidRoomExtent(sb, "RED", red);
        DumpLiquidRoomExtent(sb, "GED", ged);
        sb.AppendLine();

        // ---- (3b) liquid SURFACE geometry (the wtr_*.vbm faces) --------------------------
        sb.AppendLine("== liquid surface geometry ==");
        DumpLiquidFaces(sb, "RED", red);
        DumpLiquidFaces(sb, "GED", ged);
        sb.AppendLine();

        // ---- (4) face->room membership divergence ----------------------------------------
        // For each RED world face (real texture, main room), the room RED assigned maps to a GED
        // main slot; independently locate the GED main room whose AABB smallest-contains the face
        // centroid. A mismatch means the wall renders/culls under a different room than RED.
        // The naive proxy (RED assignment vs GED SmallestContaining) is SATURATED by AABB nesting:
        // a face RED assigned to a big room but sitting near a nested smaller room scores as a mismatch
        // even against RED's OWN data. So report three numbers: the naive %, the RED-vs-RED self ceiling
        // (RED assignment vs RED SmallestContaining — the metric's floor under RED's own nesting), and
        // the HONEST % over UNAMBIGUOUS faces (those where RED's own SmallestContaining agrees with RED's
        // assignment — the subset where the spatial proxy is valid and GED should match RED exactly).
        int probed = 0, agree = 0;             // naive: GED vs RED assignment
        int selfProbed = 0, selfAgree = 0;     // control: RED vs RED (the ceiling)
        int uProbed = 0, uAgree = 0;           // honest: GED vs RED on unambiguous faces
        foreach (Face f in red.Faces)
        {
            if (f.Texture < 0 || f.Vertices.Count < 3 || f.RoomIndex < 0 || f.RoomIndex >= red.Rooms.Count)
            {
                continue;
            }

            if (red.Rooms[f.RoomIndex].IsSubroom != 0)
            {
                continue;
            }

            int redSlot = redMainSlotOfRoom.GetValueOrDefault(f.RoomIndex, -1);
            if (redSlot < 0)
            {
                continue;
            }

            Vec3 c = Centroid(red, f);
            int redSelf = SmallestContaining(redMain, c);
            if (redSelf >= 0)
            {
                selfProbed++;
                if (redSelf == redSlot)
                {
                    selfAgree++;
                }
            }

            if (redToGed[redSlot] < 0)
            {
                continue;
            }

            int gedSlot = SmallestContaining(gedMain, c);
            if (gedSlot < 0)
            {
                continue;
            }

            probed++;
            if (gedSlot == redToGed[redSlot])
            {
                agree++;
            }

            // Unambiguous: RED's own spatial proxy already agrees with RED's assignment for this face.
            if (redSelf == redSlot)
            {
                uProbed++;
                if (gedSlot == redToGed[redSlot])
                {
                    uAgree++;
                }
            }
        }

        double agreePct = probed == 0 ? 0 : agree / (double)probed;
        double selfPct = selfProbed == 0 ? 0 : selfAgree / (double)selfProbed;
        double honestPct = uProbed == 0 ? 0 : uAgree / (double)uProbed;
        sb.AppendLine("== face->room membership ==");
        sb.AppendLine($"naive proxy: probed {probed}, GED matches RED-assignment for {agree} ({agreePct:P0})");
        sb.AppendLine($"CEILING (RED-vs-RED self, AABB-nesting bound): {selfAgree}/{selfProbed} ({selfPct:P0})");
        sb.AppendLine($"HONEST (unambiguous non-nested faces): {uAgree}/{uProbed} ({honestPct:P0}) — GED reproduces RED room membership");
        sb.AppendLine();

        return new DiffResult(
            sb.ToString(), redMain.Count, gedMain.Count, matched,
            redLinksInGed.Count, reproduced, missing, extra, uProbed, uAgree);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static List<Room> MainRooms(Geometry g) => g.Rooms.Where(r => r.IsSubroom == 0).ToList();

    private static Dictionary<int, int> SlotOfRoom(Geometry g)
    {
        var d = new Dictionary<int, int>();
        int slot = 0;
        for (int i = 0; i < g.Rooms.Count; i++)
        {
            if (g.Rooms[i].IsSubroom == 0)
            {
                d[i] = slot++;
            }
        }

        return d;
    }

    private static HashSet<(int, int)> LinkSet(Geometry g, Dictionary<int, int> mainSlot)
    {
        var set = new HashSet<(int, int)>();
        foreach (Portal p in g.Portals)
        {
            if (!mainSlot.TryGetValue(p.RoomIndex1, out int a) || !mainSlot.TryGetValue(p.RoomIndex2, out int b) || a == b)
            {
                continue;
            }

            set.Add(a < b ? (a, b) : (b, a));
        }

        return set;
    }

    /// <summary>Greedy best-IoU matching of RED main rooms to GED main rooms (1:1, IoU >= 0.10).</summary>
    private static int[] GreedyMatch(List<Room> redMain, List<Room> gedMain, out int[] gedToRed)
    {
        int nr = redMain.Count, ng = gedMain.Count;
        var redToGed = new int[nr];
        gedToRed = new int[ng];
        Array.Fill(redToGed, -1);
        Array.Fill(gedToRed, -1);

        var candidates = new List<(double Iou, int R, int G)>();
        for (int r = 0; r < nr; r++)
        {
            for (int gi = 0; gi < ng; gi++)
            {
                double iou = Iou(redMain[r].Aabb, gedMain[gi].Aabb);
                if (iou >= 0.10)
                {
                    candidates.Add((iou, r, gi));
                }
            }
        }

        candidates.Sort((a, b) => b.Iou.CompareTo(a.Iou));
        foreach ((double _, int r, int gi) in candidates)
        {
            if (redToGed[r] < 0 && gedToRed[gi] < 0)
            {
                redToGed[r] = gi;
                gedToRed[gi] = r;
            }
        }

        return redToGed;
    }

    private static int SmallestContaining(List<Room> rooms, Vec3 p)
    {
        int best = -1;
        double bestVol = double.MaxValue;
        for (int i = 0; i < rooms.Count; i++)
        {
            Aabb a = rooms[i].Aabb;
            if (p.X < a.P1.X - 0.1f || p.X > a.P2.X + 0.1f ||
                p.Y < a.P1.Y - 0.1f || p.Y > a.P2.Y + 0.1f ||
                p.Z < a.P1.Z - 0.1f || p.Z > a.P2.Z + 0.1f)
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

    /// <summary>For each liquid room: its stored AABB vs the AABB of its own member (non-portal) faces vs
    /// its portal-face AABB — so a room grown taller than its geometry (by portals) is visible.</summary>
    private static void DumpLiquidRoomExtent(StringBuilder sb, string tag, Geometry g)
    {
        for (int ri = 0; ri < g.Rooms.Count; ri++)
        {
            if (g.Rooms[ri].IsLiquidRoom == 0)
            {
                continue;
            }

            var memMn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var memMx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            var porMn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var porMx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            int memFaces = 0, porFaces = 0;
            foreach (Face f in g.Faces)
            {
                if (f.RoomIndex != ri || f.Vertices.Count < 3)
                {
                    continue;
                }

                bool portal = f.IsPortalFace;
                foreach (FaceVertex v in f.Vertices)
                {
                    if (v.Index < 0 || v.Index >= g.Vertices.Count)
                    {
                        continue;
                    }

                    Vec3 p = g.Vertices[v.Index];
                    if (portal)
                    {
                        porMn = Vec3Math.Min(porMn, p);
                        porMx = Vec3Math.Max(porMx, p);
                    }
                    else
                    {
                        memMn = Vec3Math.Min(memMn, p);
                        memMx = Vec3Math.Max(memMx, p);
                    }
                }

                if (portal)
                {
                    porFaces++;
                }
                else
                {
                    memFaces++;
                }
            }

            Aabb bb = g.Rooms[ri].Aabb;
            sb.AppendLine($"  {tag} liquid room#{ri}: storedAABB y[{bb.P1.Y:F1}..{bb.P2.Y:F1}] center={Center(bb)}");
            sb.AppendLine($"    member faces ({memFaces}): y[{(memFaces == 0 ? 0 : memMn.Y):F1}..{(memFaces == 0 ? 0 : memMx.Y):F1}]");
            sb.AppendLine($"    portal faces ({porFaces}): y[{(porFaces == 0 ? 0 : porMn.Y):F1}..{(porFaces == 0 ? 0 : porMx.Y):F1}]");
        }
    }

    /// <summary>Reports LiquidSurface-flagged faces and wtr_-textured faces separately, with Y range.</summary>
    private static void DumpLiquidFaces(StringBuilder sb, string tag, Geometry g)
    {
        float flagArea = 0, texArea = 0;
        int flagFaces = 0, texFaces = 0;
        float flagMinY = float.MaxValue, flagMaxY = float.MinValue;
        float texMinY = float.MaxValue, texMaxY = float.MinValue;
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            bool flagged = ((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0;
            bool wtrTex = f.Texture >= 0 && f.Texture < g.Textures.Count &&
                          g.Textures[f.Texture].StartsWith("wtr_", StringComparison.OrdinalIgnoreCase);
            if (!flagged && !wtrTex)
            {
                continue;
            }

            float a = FaceArea(g, f);
            float cy = Centroid(g, f).Y;
            if (flagged)
            {
                flagFaces++;
                flagArea += a;
                flagMinY = Math.Min(flagMinY, cy);
                flagMaxY = Math.Max(flagMaxY, cy);
            }

            if (wtrTex)
            {
                texFaces++;
                texArea += a;
                texMinY = Math.Min(texMinY, cy);
                texMaxY = Math.Max(texMaxY, cy);
            }
        }

        sb.AppendLine($"  {tag}: LiquidSurface-flagged {flagFaces} faces area {flagArea:F1} m² y[{(flagFaces == 0 ? 0 : flagMinY):F2}..{(flagFaces == 0 ? 0 : flagMaxY):F2}]");
        sb.AppendLine($"  {tag}: wtr_-textured     {texFaces} faces area {texArea:F1} m² y[{(texFaces == 0 ? 0 : texMinY):F2}..{(texFaces == 0 ? 0 : texMaxY):F2}]");
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

    private static Vec3 Centroid(Geometry g, Face f)
    {
        var c = new Vec3(0, 0, 0);
        int n = 0;
        foreach (FaceVertex v in f.Vertices)
        {
            if (v.Index >= 0 && v.Index < g.Vertices.Count)
            {
                c = c.Add(g.Vertices[v.Index]);
                n++;
            }
        }

        return n == 0 ? c : c.Scale(1f / n);
    }

    private static float FaceArea(Geometry g, Face f)
    {
        Vec3 c = Centroid(g, f);
        float area = 0;
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            int ia = f.Vertices[i].Index, ib = f.Vertices[(i + 1) % f.Vertices.Count].Index;
            if (ia < 0 || ia >= g.Vertices.Count || ib < 0 || ib >= g.Vertices.Count)
            {
                return area;
            }

            Vec3 a = g.Vertices[ia].Sub(c);
            Vec3 b = g.Vertices[ib].Sub(c);
            area += a.Cross(b).Length() * 0.5f;
        }

        return area;
    }

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
        // Match RED's static fold: exclude mover-owned brushes (they animate from the movers section).
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
