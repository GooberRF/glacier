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
/// Pass 23 — PER-ROOM FLAG PARITY gate (Defect 2, glass/alpha). Every prior room audit checked field
/// VALIDITY (in range, self-consistent) but never EQUALITY of the room flag bytes against RED's original.
/// Goober's report: rooms that are an encased window do not show the detail glass. RF reads room.has_alpha
/// to schedule the alpha render pass; GED never set it, so those subrooms were scheduled opaque.
/// <para>
/// The face-level alpha bit is derived from texture content (RED scans the TGA/VBM alpha channel; GED does
/// the same via a VFS <c>TextureTraitsCache</c>). To test the room ROLLUP in isolation from texture I/O —
/// which is separately verified and needs a mounted install — this gate seeds GED's texture traits from
/// RED's OWN compiled per-texture flags, then asserts GED reproduces RED's per-room has_alpha (and the
/// other room flags) on spatially-matched rooms.
/// </para>
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class RoomFlagParityTests
{
    private readonly ITestOutputHelper _out;

    public RoomFlagParityTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dmabruptdecayrc2a27.rfl")]
    [InlineData("dm04.rfl")]
    public void Per_Room_Flags_Match_Red(string file)
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

        // Per-texture-name trait map from RED's compiled faces (invisible 0x2000 / alpha 0x40 / holes 0x80).
        var traitByName = BuildTraitMap(red);
        var options = new CompileOptions
        {
            Alpine = rfl.Context.IsAlpine,
            BuildSurfaces = false,
            TextureTraits = name => traitByName.TryGetValue(name, out TextureTraits t) ? t : TextureTraits.None,
        };

        Geometry ged = GeometryCompiler.Compile(brushes, effects, options).Geometry;

        // Mirror the shipping pipeline (GeometryBuildService.Build): authored is_airlock is preserved from
        // the source room table onto the spatially-matching rebuilt rooms — RED never recomputes it either
        // (flagship 29). The parity below therefore checks what GED actually writes on a rebuild.
        RoomFlagPreservation.PreserveAirlock(red, ged);

        // Sanity: GED must now produce alpha rooms (the rollup fired).
        int redAlpha = red.Rooms.Count(r => r.HasAlpha != 0);
        int gedAlpha = ged.Rooms.Count(r => r.HasAlpha != 0);

        // Spatial match (greedy best-IoU, all rooms — alpha rooms are subrooms). A tight threshold keeps
        // the comparison to confidently-corresponding rooms.
        const double IouThreshold = 0.30;
        int[] redToGed = GreedyMatch(red.Rooms, ged.Rooms, IouThreshold);

        int matched = 0, alphaDiff = 0, skyDiff = 0, coldDiff = 0, outDiff = 0, airDiff = 0, ambDiff = 0, liqDiff = 0;
        var sb = new StringBuilder();
        sb.AppendLine($"ROOM FLAG PARITY — {file}");
        sb.AppendLine($"RED alpha rooms={redAlpha}  GED alpha rooms={gedAlpha}");
        sb.AppendLine($"matched (IoU>={IouThreshold}): (below)");
        for (int i = 0; i < red.Rooms.Count; i++)
        {
            if (redToGed[i] < 0)
            {
                continue;
            }

            matched++;
            Room rr = red.Rooms[i];
            Room gr = ged.Rooms[redToGed[i]];
            if (rr.HasAlpha != gr.HasAlpha)
            {
                alphaDiff++;
                sb.AppendLine($"  ALPHA red=0x{rr.Id:X}({rr.HasAlpha}) ged=0x{gr.Id:X}({gr.HasAlpha}) center={Center(rr.Aabb)} size={Size(rr.Aabb)}");
            }

            if (rr.IsSkyroom != gr.IsSkyroom) skyDiff++;
            if (rr.IsCold != gr.IsCold) coldDiff++;
            if (rr.IsOutside != gr.IsOutside) outDiff++;
            if (rr.IsAirlock != gr.IsAirlock)
            {
                airDiff++;
                sb.AppendLine($"  AIRLOCK red=0x{rr.Id:X}({rr.IsAirlock}) ged=0x{gr.Id:X}({gr.IsAirlock}) iou={Iou(rr.Aabb, gr.Aabb):F2} sub(r{rr.IsSubroom}/g{gr.IsSubroom}) center={Center(rr.Aabb)} size={Size(rr.Aabb)}");
            }

            if (rr.HasAmbientLight != gr.HasAmbientLight) ambDiff++;
            if (rr.IsLiquidRoom != gr.IsLiquidRoom) liqDiff++;
        }

        sb.AppendLine($"RED totals: cold={red.Rooms.Count(r => r.IsCold != 0)} outside={red.Rooms.Count(r => r.IsOutside != 0)} airlock={red.Rooms.Count(r => r.IsAirlock != 0)}");
        sb.AppendLine($"GED totals: cold={ged.Rooms.Count(r => r.IsCold != 0)} outside={ged.Rooms.Count(r => r.IsOutside != 0)} airlock={ged.Rooms.Count(r => r.IsAirlock != 0)}");
        sb.AppendLine($"effects: total={effects.Count} cold={effects.Count(e => e.RoomIsCold != 0)} outside={effects.Count(e => e.RoomIsOutside != 0)} airlock={effects.Count(e => e.RoomIsAirLock != 0)}");
        int alRoomsSub = red.Rooms.Count(r => r.IsAirlock != 0 && r.IsSubroom != 0);
        int alRoomsAlpha = red.Rooms.Count(r => r.IsAirlock != 0 && r.HasAlpha != 0);
        int alRoomsLiq = red.Rooms.Count(r => r.IsAirlock != 0 && r.IsLiquidRoom != 0);
        sb.AppendLine($"RED airlock rooms: total={red.Rooms.Count(r => r.IsAirlock != 0)} ofWhich subroom={alRoomsSub} alpha={alRoomsAlpha} liquid={alRoomsLiq}");
        sb.AppendLine($"matched rooms={matched}");
        sb.AppendLine($"flag diffs: alpha={alphaDiff} sky={skyDiff} cold={coldDiff} outside={outDiff} airlock={airDiff} ambient={ambDiff} liquid={liqDiff}");
        _out.WriteLine(sb.ToString());
        WriteArtifact($"pass23_roomflagparity_{Path.GetFileNameWithoutExtension(file)}.txt", sb.ToString());

        // The rollup fired: GED now sets has_alpha (was 0 before this pass).
        Assert.True(gedAlpha > 0, $"{file}: GED produced no alpha rooms — the has_alpha rollup did not fire");

        // Per-room has_alpha equality on matched rooms — the reported glass defect. Zero diffs.
        Assert.True(alphaDiff == 0,
            $"{file}: {alphaDiff} matched rooms disagree on has_alpha vs RED");

        // Sky / cold / outside / ambient / liquid come from room-effect assignment (already at parity);
        // pinned at 0 so a regression is caught.
        Assert.True(skyDiff == 0 && coldDiff == 0 && outDiff == 0 && ambDiff == 0 && liqDiff == 0,
            $"{file}: room-flag diffs sky={skyDiff} cold={coldDiff} outside={outDiff} ambient={ambDiff} liquid={liqDiff}");

        // is_airlock: flagship 29 (AirlockRuleDiag) PINNED the mechanism as AUTHORED/PRESERVED room state,
        // not a compile rule — dmabrupt ships 17 airlock rooms with ZERO airlock room effects, so RED's own
        // build path (GeoBuild_Driver effect->room copy, which GED reproduces) would also emit 0; the flags
        // survive only because RED preserves serialized room state. GED now preserves it the same way
        // (RoomFlagPreservation, mirrored above). Measured diff 0: GED writes 14 of RED's 17 airlock rooms
        // (the other 3 subrooms have no surviving IoU>=0.30 room, so they are unmatched and cannot count
        // here). Pinned at 0 — a preservation regression or structure collapse trips immediately.
        int airlockFloor = 0;
        Assert.True(airDiff <= airlockFloor,
            $"{file}: airlock diff {airDiff} exceeds {airlockFloor} — the authored-airlock preservation (RoomFlagPreservation) regressed");
    }

    private static Dictionary<string, TextureTraits> BuildTraitMap(Geometry red)
    {
        var invisible = new HashSet<string>();
        var alpha = new HashSet<string>();
        var holes = new HashSet<string>();
        foreach (Face f in red.Faces)
        {
            if (f.Texture < 0 || f.Texture >= red.Textures.Count)
            {
                continue;
            }

            string name = red.Textures[f.Texture];
            var flags = (FaceFlags)f.Flags;
            if ((flags & FaceFlags.IsInvisible) != 0) invisible.Add(name);
            if ((flags & FaceFlags.HasAlpha) != 0) alpha.Add(name);
            if ((flags & FaceFlags.HasHoles) != 0) holes.Add(name);
        }

        var map = new Dictionary<string, TextureTraits>();
        foreach (string name in red.Textures)
        {
            map[name] = new TextureTraits(invisible.Contains(name), alpha.Contains(name), holes.Contains(name));
        }

        return map;
    }

    private static int[] GreedyMatch(List<Room> red, List<Room> ged, double threshold)
    {
        int nr = red.Count, ng = ged.Count;
        var redToGed = new int[nr];
        var gedTaken = new bool[ng];
        Array.Fill(redToGed, -1);

        var cands = new List<(double Iou, int R, int G)>();
        for (int r = 0; r < nr; r++)
        {
            for (int g = 0; g < ng; g++)
            {
                double iou = Iou(red[r].Aabb, ged[g].Aabb);
                if (iou >= threshold)
                {
                    cands.Add((iou, r, g));
                }
            }
        }

        cands.Sort((a, b) => b.Iou.CompareTo(a.Iou));
        foreach ((double _, int r, int g) in cands)
        {
            if (redToGed[r] < 0 && !gedTaken[g])
            {
                redToGed[r] = g;
                gedTaken[g] = true;
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
