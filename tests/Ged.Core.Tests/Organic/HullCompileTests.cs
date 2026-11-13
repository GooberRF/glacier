using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;
using Ged.Core.Tests.Scripting;
using Xunit;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// Hull brushes through the compiler: SEALED builds under both build methods (RED-authentic Shared BSP
/// and Incremental) must be watertight, lint clean, one undo node with an exact undo.
/// </summary>
public sealed class HullCompileTests
{
    /// <summary>An irregular 12-point cave chamber about (cx, 0, cz): flat floor at y = 0, faceted walls and roof.</summary>
    private static string Chamber(double cx, double cz, double r) =>
        "{" + string.Join(", ", new[]
        {
            (cx - r, 0.0, cz - r), (cx + r, 0.0, cz - r * 0.8), (cx + r * 0.9, 0.0, cz + r), (cx - r * 1.1, 0.0, cz + r * 0.9),
            (cx - r * 1.2, 2.0, cz), (cx + r * 1.15, 2.2, cz + 0.3), (cx + 0.2, 2.1, cz - r * 1.2), (cx - 0.4, 1.9, cz + r * 1.25),
            (cx - r * 0.6, 4.1, cz - r * 0.5), (cx + r * 0.7, 4.3, cz - r * 0.4), (cx + r * 0.5, 4.0, cz + r * 0.6), (cx - r * 0.5, 4.4, cz + r * 0.55),
        }.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{{p.Item1}, {p.Item2}, {p.Item3}}}"))) + "}";

    private static void AssertHullsValid(KitRun run)
    {
        foreach (Brush b in new BrushEditor(run.Doc).Brushes)
        {
            GeometryReport r = GeometryCheck.Check(b);
            Assert.True(r.Closed && r.Outward && r.Convex && r.Planar, string.Join(" ", r.Issues));
        }
    }

    [Fact]
    public void One_Air_Hull_In_The_Solid_World()
    {
        KitRun run = Run("level.brush_from_points{points = " + Chamber(0, 0, 4) + ", air = true}");
        AssertHullsValid(run);
        AssertAccepted(run);
    }

    [Fact]
    public void Two_Overlapping_Air_Hulls_With_A_Shared_Exact_Floor()
    {
        KitRun run = Run("level.brushes_from_points{hulls = {" + Chamber(0, 0, 4) + ", " + Chamber(6, 1, 4) + "}, air = true}");
        AssertHullsValid(run);
        (CompiledLevel shared, CompiledLevel incremental) = AssertAccepted(run);

        // The shared floor plane y = 0 is exact on both hulls, so the compiled floor is one coplanar layer.
        foreach (CompiledLevel c in new[] { shared, incremental })
        {
            Assert.All(c.Geometry.Faces.Where(f => f.Plane.Normal.Y > 0.999f),
                f => Assert.All(f.Vertices, v => Assert.True(c.Geometry.Vertices[v.Index].Y == 0 || c.Geometry.Vertices[v.Index].Y > 0.01f)));
        }
    }

    [Fact]
    public void A_Room_With_A_Detail_Hull_And_A_Structural_Solid_Hull_On_Its_Floor()
    {
        KitRun run = Run(
            "level.brush{shape = 'box', pos = {0, 3, 0}, size = {20, 6, 20}, air = true}\n" +
            "level.brush_from_points{points = {{-6,0,-6},{-3,0,-6.5},{-2.5,0,-3},{-6.5,0,-2.5},{-5,2,-5},{-3.5,1.8,-4},{-4.2,2.3,-3.4}}, detail = true}\n" +
            "level.brush_from_points{points = {{2,0,2},{6,0,2.5},{5.5,0,6},{2.5,0,5.5},{3,3,3},{5,3.2,3.4},{4.4,2.9,5}}}");
        AssertHullsValid(run);
        AssertAccepted(run);
    }

    /// <summary>
    /// One thin, slanted air hull about 117 m from the origin whose long facet the single-precision plane fit
    /// misses by more than the on-plane band: a sealed build must finish (the BSP must consume the facet that
    /// donated a node plane rather than re-select that plane below it) and be watertight, under both build methods.
    /// </summary>
    [Fact]
    public async Task A_Thin_Far_Facet_Builds_Sealed_And_Watertight()
    {
        string path = Path.Combine(ScriptTestFixture.RepoRoot()!, "tests", "fixtures", "organic", "findings", "solver-hang-thin-facet.lua");
        KitRun run = Run(File.ReadAllText(path));
        foreach (bool shared in new[] { true, false })
        {
            Task<int> build = Task.Run(() => HoleDetector.Detect(Sealed(run.Doc, shared).Geometry).Count);
            Task first = await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(ReferenceEquals(first, build), $"the sealed {(shared ? "Shared BSP" : "Incremental")} build did not finish");
            Assert.Equal(0, await build);
        }
    }
}
