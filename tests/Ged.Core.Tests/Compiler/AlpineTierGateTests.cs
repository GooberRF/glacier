using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// ALPINE-TIER gates (ALPINE-COMPAT-SPEC §7.2 tier b, chunk C2): Glacier's default build (Shared BSP + Alpine build
/// repair, the <see cref="HoleParityGateTests"/> path) against Alpine RED's own compiled output of the same level —
/// the <c>-bake … -buildonly</c> references under <c>research/fixtures/alpine-ref/&lt;alpine id&gt;/build</c>
/// (RepairBuildHoles on). Skips when the references are absent (retail- and community-derived, gitignored).
/// <para>Per level: (1) non-detail open edges, measured exactly as <see cref="HoleParityGateTests"/> measures RED's
/// baseline, never above the recorded ceiling — Glacier's measured count + 2, the hole-parity convention; the target
/// is Glacier ≤ Alpine RED, and where Glacier already beats Alpine RED the ceiling keeps Glacier's number; (2) main
/// rooms and portals: the distance from Alpine RED's counts may not grow (Glacier's room/portal graph is not yet
/// RED-identical on every level — a pre-existing gap the repair does not cause); (3) faces: reported only; (4) the
/// repair counters: reported, with the sanity bound that the repair never leaves more holes by Alpine RED's own
/// per-room CheckForHoles rule than it found. The reference numbers are pinned for Alpine 8db7d90b (a regenerated
/// reference set from another Alpine build only reports).</para>
/// <para>The stock corpus's small levels run in the fast tier; the rest of the stock set and the community exemplars
/// are DeepGate. Measured tables: <c>tests/artifacts/alpine_tier_measure.txt</c>, compiler-parity-notes.md
/// ("Alpine build repair").</para>
/// </summary>
public sealed class AlpineTierGateTests
{
    private const string PinnedAlpineId = "8db7d90b";

    private readonly ITestOutputHelper _out;

    public AlpineTierGateTests(ITestOutputHelper output) => _out = output;

    /// <summary>A level's recorded numbers: Glacier hole ceiling, Alpine RED holes/main rooms/portals, and Glacier's
    /// room/portal counts when recorded (the distance ratchet).</summary>
    private sealed record Expect(int HoleCeiling, int AlpineHoles, int AlpineRooms, int AlpinePortals, int GlacierRooms, int GlacierPortals);

    // GENERATED-TABLE-START
    // Measured 2026-11-12 (Glacier default build = Shared BSP + repair; alpine-ref 8db7d90b). Columns: Glacier hole
    // ceiling (measured + 2), Alpine RED holes / main rooms / portals, Glacier main rooms / portals (distance ratchet).
    // Room/portal columns re-measured 2026-11-13 with the air-portal chop and inside-out rooms (compiler-parity-notes.md
    // "Air portal brushes and inside-out rooms"): 33 of the 59 levels now equal Alpine RED on both counts (15 before).
    private static readonly Dictionary<string, Expect> Table = new()
    {
        ["ctf01.rfl"] = new(7, 6, 53, 54, 53, 54), // Glacier 5
        ["ctf02.rfl"] = new(2, 73, 15, 19, 13, 16), // Glacier 0
        ["ctf03.rfl"] = new(2, 0, 82, 104, 84, 106), // Glacier 0
        ["ctf04.rfl"] = new(2, 0, 25, 36, 25, 36), // Glacier 0
        ["ctf05.rfl"] = new(2, 0, 16, 17, 16, 17), // Glacier 0
        ["ctf06.rfl"] = new(2, 0, 86, 124, 86, 124), // Glacier 0
        ["ctf07.rfl"] = new(44, 0, 77, 97, 77, 97), // Glacier 42 (Alpine RED lower)
        ["ctfstockintradeb1.rfl"] = new(5, 0, 41, 49, 43, 51), // Glacier 3 (Alpine RED lower)
        ["ctfwlpro.rfl"] = new(2, 0, 18, 22, 6, 7), // Glacier 0
        ["dm01.rfl"] = new(2, 0, 37, 21, 37, 21), // Glacier 0
        ["dm02.rfl"] = new(2, 0, 10, 8, 10, 8), // Glacier 0
        ["dm03.rfl"] = new(2, 0, 27, 28, 27, 28), // Glacier 0
        ["dm04.rfl"] = new(2, 0, 9, 9, 9, 10), // Glacier 0
        ["dm05.rfl"] = new(2, 0, 13, 16, 13, 16), // Glacier 0
        ["dm06.rfl"] = new(2, 0, 41, 46, 41, 46), // Glacier 0
        ["dm07.rfl"] = new(4, 10, 15, 17, 10, 10), // Glacier 2
        ["dm08.rfl"] = new(4, 2, 8, 7, 8, 7), // Glacier 2
        ["dm09.rfl"] = new(2, 2, 111, 116, 116, 121), // Glacier 0
        ["dm10.rfl"] = new(2, 0, 28, 32, 29, 32), // Glacier 0
        ["dm12.rfl"] = new(2, 0, 44, 58, 44, 58), // Glacier 0
        ["dm13.rfl"] = new(2, 0, 15, 21, 13, 19), // Glacier 0
        ["dm14.rfl"] = new(2, 0, 6, 4, 4, 4), // Glacier 0
        ["dm15.rfl"] = new(18, 0, 35, 38, 35, 38), // Glacier 16 (Alpine RED lower)
        ["dm16.rfl"] = new(2, 0, 23, 27, 23, 27), // Glacier 0
        ["dm17.rfl"] = new(2, 0, 50, 61, 52, 65), // Glacier 0
        ["dm19.rfl"] = new(2, 0, 21, 17, 21, 17), // Glacier 0
        ["dm20.rfl"] = new(2, 0, 32, 48, 32, 48), // Glacier 0
        ["dmabruptdecayrc2a27.rfl"] = new(8, 0, 28, 46, 28, 46), // Glacier 6 (Alpine RED lower)
        ["dmabruptdecayrc2a27~.rfl"] = new(8, 0, 28, 46, 28, 46), // Glacier 6 (Alpine RED lower)
        ["dmedgeofdespairb1a1.rfl"] = new(2, 22, 2, 0, 2, 0), // Glacier 0
        ["dmedificationduelb2.rfl"] = new(2, 0, 31, 36, 31, 36), // Glacier 0
        ["dmfoundationclassicb1.rfl"] = new(2, 0, 32, 39, 32, 39), // Glacier 0
        ["dmwarzoneclassicb1.rfl"] = new(19, 0, 16, 18, 8, 8), // Glacier 17 (Alpine RED lower)
        ["geddmabruptdecayrc2a27.rfl"] = new(8, 0, 28, 46, 28, 46), // Glacier 6 (Alpine RED lower)
        ["glass_house.rfl"] = new(2, 0, 1, 0, 1, 0), // Glacier 0
        ["kothcowb1~.rfl"] = new(2, 2, 61, 81, 64, 83), // Glacier 0
        ["CTF-Cosmonaut1.1.rfl"] = new(2, 0, 16, 21, 18, 24), // Glacier 0
        ["ctf-dualityalm.rfl"] = new(72, 9, 26, 36, 24, 34), // Glacier 70 (Alpine RED lower)
        ["ctf-hunab8.rfl"] = new(14, 13, 4, 0, 4, 0), // Glacier 12
        ["ctfscav.rfl"] = new(6, 0, 1, 0, 1, 0), // Glacier 4 (Alpine RED lower)
        ["ctf_desertpro_b1.rfl"] = new(14, 0, 48, 69, 38, 56), // Glacier 12 (Alpine RED lower); before repair 21
        ["DM-HeavenIslands.rfl"] = new(23, 14, 2, 0, 2, 0), // Glacier 21 (Alpine RED lower)
        ["dm-rfu4-riverrequiem.rfl"] = new(2, 0, 12, 12, 12, 12), // Glacier 0
        ["dm-rfu5-thedarkzone.rfl"] = new(4, 4, 39, 48, 32, 41), // Glacier 2; before repair 0; main rooms were 33: the one room that went is a free-standing shell Alpine RED also builds as a detail room (its room 14)
        ["DM-TempleOfWar.rfl"] = new(2, 0, 44, 63, 43, 57), // Glacier 0
        ["DM-Turrican_PowerPlant_.rfl"] = new(2, 0, 61, 64, 51, 40), // Glacier 0
        ["dm11.rfl"] = new(2, 14, 2, 0, 2, 0), // Glacier 0
        ["dmabandonedb1.rfl"] = new(2, 0, 8, 9, 8, 9), // Glacier 0
        ["dmabruptdecayrc2.rfl"] = new(8, 0, 28, 46, 28, 46), // Glacier 6 (Alpine RED lower)
        ["dmchastitybeltb22.rfl"] = new(2, 5, 38, 44, 34, 42), // Glacier 0
        ["dmhualpar.rfl"] = new(19, 16, 1, 0, 1, 0), // Glacier 17 (Alpine RED lower)
        ["dmskooldaze.rfl"] = new(66, 11, 14, 12, 14, 12), // Glacier 64 (Alpine RED lower)
        ["dmsteamventsrc2.rfl"] = new(2, 4, 42, 57, 44, 60), // Glacier 0
        ["dmsteelshaperb1.rfl"] = new(2, 2, 53, 70, 52, 69), // Glacier 0; main rooms were 53: the one room that went is a free-standing shell Alpine RED also builds as a detail room (its room 138)
        ["DM_Airship_1.2_.rfl"] = new(14, 16, 9, 9, 8, 8), // Glacier 12
        ["dm_deck16fixed.rfl"] = new(14, 0, 16, 22, 16, 22), // Glacier 12 (Alpine RED lower)
        ["escinmassb1.rfl"] = new(70, 0, 80, 104, 79, 103), // Glacier 68 (Alpine RED lower)
        ["kothexileb1.rfl"] = new(10, 18, 162, 205, 163, 225), // Glacier 8
        ["kothsecretsalvageb1.rfl"] = new(2, 0, 88, 111, 51, 67), // Glacier 0
    };

    /// <summary>Small stock levels cheap enough for the fast tier.</summary>
    private static readonly string[] Fast = { "dm01.rfl", "dm03.rfl", "dm13.rfl", "dm14.rfl", "glass_house.rfl" };
    // GENERATED-TABLE-END

    public static IEnumerable<object[]> FastLevels() => Fast.Select(n => new object[] { n });

    public static IEnumerable<object[]> DeepLevels() => Table.Keys.Where(n => !Fast.Contains(n)).OrderBy(n => n).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(FastLevels))]
    public void Fast_Tier_Level_Holds_Its_Alpine_Tier_Ratchets(string level) => Check(level);

    [Theory]
    [Trait("Category", "DeepGate")] // heavy corpus compile; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
    [MemberData(nameof(DeepLevels))]
    public void Level_Holds_Its_Alpine_Tier_Ratchets(string level) => Check(level);

    private void Check(string name)
    {
        if (AlpineReference.Find() is not { } refs || refs.Get(name) is not { } level
            || !AlpineReference.TryLoad(level, out AlpineReference.Loaded? loaded) || loaded is null)
        {
            return;
        }

        Expect e = Table[name];
        CompiledLevel built = GeometryCompiler.Compile(loaded.Brushes, loaded.Effects, new CompileOptions { BuildSurfaces = false });
        Geometry mine = built.Geometry;
        Geometry alpine = loaded.Reference;
        BuildRepairStats r = built.Report.Repair;

        int gedHoles = HoleDetector.Detect(mine).Count;
        int alpineHoles = HoleDetector.Detect(alpine).Count;
        int gedRooms = AlpineReference.MainRooms(mine);
        int alpineRooms = AlpineReference.MainRooms(alpine);
        int gedRuleHoles = AlpineReference.AlpineRuleHoles(mine);
        _out.WriteLine($"{name}: holes glacier {gedHoles} (ceiling {e.HoleCeiling}) vs alpine {alpineHoles}; " +
                       $"main rooms {gedRooms} vs {alpineRooms}; portals {mine.Portals.Count} vs {alpine.Portals.Count}; " +
                       $"faces {mine.Faces.Count} vs {alpine.Faces.Count}; alpine-rule holes glacier {gedRuleHoles} vs alpine {AlpineReference.AlpineRuleHoles(alpine)}");
        _out.WriteLine("  glacier repair: " + string.Join(", ", r.Counters().Select(c => $"{c.Name} {c.Value}")));
        _out.WriteLine("  alpine repair:  " + string.Join(", ", AlpineReference.CounterNames.Select((c, i) => $"{c} {level.Counters[i]}")));

        if (refs.Id == PinnedAlpineId)
        {
            Assert.Equal(e.AlpineHoles, alpineHoles);
            Assert.Equal(e.AlpineRooms, alpineRooms);
            Assert.Equal(e.AlpinePortals, alpine.Portals.Count);
        }

        Assert.True(gedHoles <= e.HoleCeiling,
            $"{name}: {gedHoles} non-detail open edges exceeds the Alpine-tier ceiling {e.HoleCeiling} (Alpine RED {alpineHoles}) — a hole regression");
        Assert.True(System.Math.Abs(gedRooms - alpineRooms) <= System.Math.Abs(e.GlacierRooms - e.AlpineRooms),
            $"{name}: main rooms {gedRooms} moved further from Alpine RED's {alpineRooms} (recorded {e.GlacierRooms})");
        Assert.True(System.Math.Abs(mine.Portals.Count - alpine.Portals.Count) <= System.Math.Abs(e.GlacierPortals - e.AlpinePortals),
            $"{name}: portals {mine.Portals.Count} moved further from Alpine RED's {alpine.Portals.Count} (recorded {e.GlacierPortals})");

        Assert.True(r.Ran);
        Assert.All(r.Counters(), c => Assert.True(c.Value >= 0, $"{c.Name} is negative"));
        Assert.True(gedRuleHoles <= r.HolesAfterRooms,
            $"{name}: the repair left {gedRuleHoles} holes by Alpine RED's own rule, more than the {r.HolesAfterRooms} it found");
    }
}
