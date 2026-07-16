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
/// ITEM 1 GATE — the in-game material-debris shatter (Alpine <c>do_material_debris_shatter</c>,
/// reimplemented in <see cref="CarveSimulation"/>) must produce NO MORE ear-clip cap stalls on GED's
/// compiled geometry than on RED's own. Goober saw "[CapFace] Ear clip stuck: remaining=5 of 7"
/// digging a GED-built breakable brush; the stall is a boundary loop assembled ACROSS faces after
/// bisection, which per-compiled-face probing (<see cref="EarClipCapGateTests"/>) cannot see.
/// <para>
/// The shatter's bisection planes come only from each chunk's bounding box, so the harness is fully
/// DETERMINISTIC — every reachable room is shattered exactly once; there is nothing to seed. RED is NOT
/// zero here: its own compiled dmabrupt geometry stalls 15 cap loops (an inherent property of Alpine's
/// boundary-chaining on non-manifold bisection fragments), so the invariant is "no worse than RED",
/// pinned with the measured numbers below. GED's own T-joint stations on detail brushes previously
/// added 8 more (23); <see cref="TJointFixer"/> now leaves detail faces pristine like RED.
/// </para>
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class CarveSimGateTests
{
    private readonly ITestOutputHelper _out;

    public CarveSimGateTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dmabrupt_Material_Shatter_Caps_No_Worse_Than_Red()
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
        Geometry red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        AlpineLevelPropertiesSection alp =
            rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().First();

        CompiledLevel result = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = false, FixTJoints = true });
        Geometry ged = result.Geometry;

        (int redStuck, int redLoops) = Shatter(red, CarveSimDiag.ReachableBreakableRooms(red, alp, null));
        (int gedStuck, int gedLoops) = Shatter(ged, CarveSimDiag.ReachableBreakableRooms(ged, alp, result.BrushRoomUid));

        _out.WriteLine($"reachable breakable: RED loops={redLoops} stuck={redStuck}; GED loops={gedLoops} stuck={gedStuck}");

        // Superset (all geoable ∪ breakable rooms) — a broader honesty check.
        (int redAllStuck, _) = Shatter(red, CarveSimDiag.AllGeoableBreakableRooms(red, alp, null));
        (int gedAllStuck, _) = Shatter(ged, CarveSimDiag.AllGeoableBreakableRooms(ged, alp, result.BrushRoomUid));
        _out.WriteLine($"all geoable∪breakable: RED stuck={redAllStuck}; GED stuck={gedAllStuck}");

        Assert.True(gedStuck <= redStuck,
            $"GED material-shatter cap stalls {gedStuck} exceed RED's {redStuck} (reachable breakable rooms) — " +
            "geomod/breakable destruction would leave GED-built brushes uncapped");
        Assert.True(gedAllStuck <= redAllStuck,
            $"GED material-shatter cap stalls {gedAllStuck} exceed RED's {redAllStuck} (all geoable∪breakable rooms)");
    }

    private static (int Stuck, int Loops) Shatter(Geometry g, HashSet<int> rooms)
    {
        var res = new CarveSimulation.Result();
        foreach (int r in rooms.OrderBy(x => x))
        {
            CarveSimulation.ShatterRoom(g, r, res);
        }

        return (res.Stuck, res.Loops);
    }
}
