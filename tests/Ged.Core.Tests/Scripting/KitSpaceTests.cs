using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// The kit.space archetype library (DESIGN-QUALITY-SPEC §c.1-3, Q3 gate): every archetype builds sealed with zero holes
/// under BOTH build methods, lint-clean, as one exact undo step, and meets its proportion targets measured on the compiled
/// geometry with <see cref="SpaceMetricsProbe"/>: headroom p50 in its band, ceiling variety (headroom IQR/median at least
/// 0.5 or at least 25% of neighbouring 8 m cells stepping 1.5 m), off-axis wall share, and the sky share of open-air and
/// payload pieces. Measured values are written to tests/artifacts/kit_space/metrics_&lt;archetype&gt;.txt.
/// </summary>
public sealed class KitSpaceTests
{
    private readonly ITestOutputHelper _out;

    public KitSpaceTests(ITestOutputHelper output) => _out = output;

    /// <summary>One archetype's gate: floor band to measure, headroom p50 band of covered floor, ceiling variety, off-axis floor, sky floor.</summary>
    public sealed record Gate(string Archetype, double MinY, double MaxY, double HeadLo, double HeadHi, bool Varied, double OffAxisMin, double SkyMin, double HubRatio = 0);

    public static readonly Gate[] Gates =
    {
        // indoor: headroom bands from the spec's proportion rules (§c.1-2) for the space's footprint class.
        new("hall", -1, 2, 5.5, 10.5, true, 0.08, 0),
        new("atrium", -1, 7, 5, 14.5, true, 0.15, 0),
        new("arena", -1, 4, 7, 12.5, true, 0.05, 0),
        // ring: corridor proportions (4-5 m) whose variety is the hubs: bays at least 2x the corridor height (§c.1-2).
        new("ring", -1, 7, 4, 5, false, 0.02, 0, HubRatio: 2),
        new("sunken_court", -6, 2, 7, 17.5, true, 0.15, 0),
        new("stair_hall", -1, 11, 4, 16.5, true, 0.08, 0),
        new("courtyard", -1, 2, 4, 5, false, 0.05, 0.4),
        new("canyon_bridge", -1, 13, 0, 1e9, false, 0.3, 0.6),
        new("cave_chamber", -1, 3, 4.5, 12, true, 0.15, 0),
        new("pit_room", -11, 5, 8, 20, true, 0.1, 0),
        new("control_room", -1, 6, 4, 12.5, true, 0.05, 0),
        new("storage", -1, 2, 4, 9, true, 0.03, 0),
        // open air: decks and bridges under the sky (the void floor far below is left out).
        new("platform", -7, 6, 0, 1e9, false, 0.1, 0.8),
        new("sky_bridge", -11, 4, 0, 1e9, false, 0.1, 0.8),
        new("open_deck", -1, 2, 0, 1e9, false, 0.1, 0.8),
        // payload pieces: outdoor fight spaces.
        new("cart_road", -1, 4, 0, 1e9, false, 0.05, 0.6),
        new("overlook", -1, 5, 0, 1e9, false, 0.05, 0.7),
        new("gate", -1, 11, 0, 1e9, false, 0.05, 0.6),
        new("finale", -3, 5, 0, 1e9, false, 0.15, 0.6),
    };

    public static IEnumerable<object[]> Archetypes() => Gates.Select(g => new object[] { g.Archetype });

    private static Gate GateOf(string archetype) => Gates.Single(g => g.Archetype == archetype);

    private static string ArtifactsDir()
    {
        string root = ScriptTestFixture.RepoRoot() ?? Path.GetTempPath();
        string dir = Path.Combine(root, "tests", "artifacts", "kit_space");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(Archetypes))]
    public void Archetype_Builds_Sealed_And_Meets_Its_Proportions(string archetype)
    {
        Gate g = GateOf(archetype);
        KitRun run = Run($"local s = kit.space{{ archetype = '{archetype}', pos = {{0, 0, 0}} }}\n" +
                         "return s.name .. ':' .. s.class .. ':' .. #s.brushes .. ':' .. #s.anchors .. ':' .. #s.warnings");
        string[] r = run.Result.ReturnValue!.Split(':');
        Assert.Equal(archetype + "1", r[0]);
        Assert.Equal("0", r[4]); // an archetype at its own size raises no proportion or scale warnings

        CompiledLevel shared = AssertWatertight(run.Doc, sharedBsp: true);
        CompiledLevel incremental = AssertWatertight(run.Doc, sharedBsp: false);
        Assert.NotEmpty(incremental.Geometry.Faces);
        SpaceMetrics m = SpaceMetricsProbe.Measure(shared.Geometry, g.MinY, g.MaxY);
        string line = $"{archetype} ({r[1]}): {r[2]} brushes, holes 0/0 (Shared BSP/Incremental); {m}";
        _out.WriteLine(line);
        File.WriteAllText(Path.Combine(ArtifactsDir(), $"metrics_{archetype}.txt"), line + Environment.NewLine);

        Assert.True(m.Samples > 50, line);
        if (g.HeadHi < 1e8)
        {
            Assert.True(m.HeadroomP50 >= g.HeadLo && m.HeadroomP50 <= g.HeadHi, $"headroom p50 outside {g.HeadLo}-{g.HeadHi} m: {line}");
        }

        if (g.HubRatio > 0)
        {
            Assert.True(m.HeadroomP90 >= g.HubRatio * m.HeadroomP50, $"no hub at least {g.HubRatio}x the corridor height: {line}");
        }

        if (g.Varied)
        {
            Assert.True(m.Varied(), $"ceilings do not vary enough (IQR/median >= 0.5 or cell steps >= 0.25): {line}");
        }

        Assert.True(m.OffAxisShare >= g.OffAxisMin, $"off-axis wall share below {g.OffAxisMin}: {line}");
        Assert.True(m.SkyShare >= g.SkyMin, $"sky share below {g.SkyMin}: {line}");
        if (r[1] == "indoor" && g.SkyMin == 0)
        {
            Assert.True(m.OffAxisShare <= 0.6, $"off-axis share above 0.6 reads as organic noise: {line}");
        }

        AssertLintClean(run);
        AssertUndoRestores(run);
    }

    [Fact]
    public void Archetypes_List_Matches_The_Gates()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(LevelTemplates.CreateNew(FixedDate),
            "local kit = ged.import('kit') local out = {} for _, a in ipairs(kit.archetypes{}) do out[#out + 1] = a.name .. '/' .. a.class end return table.concat(out, ',')");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        string[] listed = r.ReturnValue!.Split(',');
        Assert.Equal(Gates.Select(g => g.Archetype), listed.Select(l => l.Split('/')[0]));
        Assert.Equal(12, listed.Count(l => l.EndsWith("/indoor", StringComparison.Ordinal)));
        Assert.Equal(3, listed.Count(l => l.EndsWith("/open_air", StringComparison.Ordinal)));
        Assert.Equal(4, listed.Count(l => l.EndsWith("/payload", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("hall", "+x")]
    [InlineData("control_room", "-x")]
    [InlineData("storage", "-z")]
    [InlineData("cart_road", "+x")]
    public void Archetype_Facing_Rotates_And_Stays_Sealed(string archetype, string facing)
    {
        KitRun run = Run($"local s = kit.space{{ archetype = '{archetype}', pos = {{0, 0, 0}}, facing = '{facing}' }}\n" +
                         "return #s.warnings .. ':' .. s.facing");
        Assert.Equal("0:" + facing, run.Result.ReturnValue);
        AssertAccepted(run);
    }

    [Fact]
    public void Space_Rejects_Unknown_Archetypes_And_Small_Plans()
    {
        ScriptDiagnostic unknown = Fail("kit.space{ archetype = 'atirum', pos = {0, 0, 0} }");
        Assert.Contains("unknown archetype 'atirum'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("atrium", unknown.Hint, StringComparison.Ordinal);
        ScriptDiagnostic small = Fail("kit.space{ archetype = 'hall', pos = {0, 0, 0}, size = {10, 8} }");
        Assert.Contains("at least 16 x 12 m", small.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Space_Tags_Every_Brush_And_Groups_Them()
    {
        KitRun run = Run("local s = kit.space{ archetype = 'storage', name = 'store', pos = {0, 0, 0} } return #s.brushes");
        int count = int.Parse(run.Result.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(run.Doc);
        Assert.Equal(count, tags.Count(t => t.Value.TryGetValue("kit.space", out string? v) && v == "store"));
        Assert.Contains(new GroupService(run.Doc).Groups, gr => gr.Name == "store");
    }

    [Fact]
    public void Organic_Archetypes_Are_Moderated()
    {
        // Organic moderation (spec c.1-8): >= 30% flat floor, >= 1 built element per 100 m², and the compiled
        // geometry keeps sloped area <= 0.30 or wall planes <= 45 per 100 m².
        foreach ((string archetype, double minY, double maxY) in new[] { ("cave_chamber", -1.0, 3.0), ("canyon_bridge", -1.0, 13.0) })
        {
            KitRun run = Run($"local s = kit.space{{ archetype = '{archetype}', pos = {{0, 0, 0}} }}\n" +
                             "local o = s.organic return string.format('%.3f:%.3f:%s', o.flat_share, o.built_per_100m2, tostring(o.ok))");
            string[] o = run.Result.ReturnValue!.Split(':');
            double flat = double.Parse(o[0], System.Globalization.CultureInfo.InvariantCulture);
            double built = double.Parse(o[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(flat >= 0.3, $"{archetype} flat share {flat}");
            Assert.True(built >= 1, $"{archetype} built elements per 100 m² {built}");
            Assert.Equal("true", o[2]);
            SpaceMetrics m = SpaceMetricsProbe.Measure(Sealed(run.Doc, sharedBsp: true).Geometry, minY, maxY);
            _out.WriteLine($"{archetype}: {m}");
            Assert.True(m.SlopedShare <= 0.30 || m.WallPlanesPer100 <= 45, $"{archetype} is organic noise: {m}");
        }
    }
}
