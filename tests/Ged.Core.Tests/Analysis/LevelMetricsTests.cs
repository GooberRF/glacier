using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Analysis;
using Ged.Core.IO.Rfl;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Analysis.AnalysisFixtures;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The fingerprint extractors (CORPUS-NORMS-SPEC §4-1 synthetic truth, §8.2 goldens, determinism, malformed input,
/// timeout). Synthetic levels are kit-built and compiled SEALED in-test; each measured dimension must come back
/// within one lattice step (±0.5 m widths/heights, ±0.03 m riser, ±0.1 m tread).
/// </summary>
public sealed class LevelMetricsTests
{
    private readonly ITestOutputHelper _out;

    public LevelMetricsTests(ITestOutputHelper output) => _out = output;

    private static string TwoRooms(double roomH, string connect) =>
        $"local a = kit.room{{name = 'a', pos = {{0, 0, 0}}, size = {{12, {roomH}, 12}}}}\n" +
        $"local b = kit.room{{name = 'b', pos = {{13, 0, 0}}, size = {{12, {roomH}, 12}}}}\n" + connect;

    // ---- synthetic truth ------------------------------------------------------------------------

    [Theory]
    [InlineData(2.0, 2.5)]
    [InlineData(3.0, 3.0)]
    [InlineData(4.0, 4.0)]
    public void Doorway_Width_And_Height_Are_Recovered(double width, double height)
    {
        LevelFingerprint fp = Analyze(Build(TwoRooms(6, $"kit.doorway{{name = 'd', a = a, b = b, width = {width}, height = {height}}}")));
        _out.WriteLine(fp.Json["opening"]!.ToJsonString());
        Assert.Equal(1, (int)Value(fp, "opening.count")!);
        Assert.InRange(Value(fp, "opening.width")!.Value, width - 0.5, width + 0.5);
        Assert.InRange(Value(fp, "opening.height")!.Value, height - 0.5, height + 0.5);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    [InlineData(6.0)]
    public void Corridor_Width_Is_Recovered(double width)
    {
        string kit = "local a = kit.room{name = 'a', pos = {0, 0, 0}, size = {12, 6, 12}}\n" +
                     "local b = kit.room{name = 'b', pos = {38, 0, 0}, size = {12, 6, 12}}\n" + // 26 m gap
                     $"kit.corridor{{name = 'c', a = a, b = b, width = {width}, height = 3}}";
        LevelFingerprint fp = Analyze(Build(kit));
        _out.WriteLine(fp.Json["corridor"]!.ToJsonString());
        Assert.InRange(Value(fp, "corridor.width")!.Value, width - 0.5, width + 0.5);
        Assert.InRange(Value(fp, "corridor.height")!.Value, 2.5, 3.5);
        Assert.True(Value(fp, "corridor.fraction") > 0);
    }

    [Theory]
    [InlineData(0.25, 0.5)]
    [InlineData(0.5, 0.5)]
    public void Stair_Riser_And_Tread_Are_Recovered(double riser, double tread)
    {
        string kit = "kit.room{name = 'r', pos = {0, 0, 0}, size = {24, 8, 12}}\n" +
                     $"kit.stairs{{name = 's', pos = {{-6, 0, 0}}, dir = '+x', rise = 2, riser = {riser}, tread = {tread}, width = 3}}";
        LevelFingerprint fp = Analyze(Build(kit));
        _out.WriteLine(fp.Json["stair"]!.ToJsonString());
        Assert.Equal(1, (int)Value(fp, "stair.count")!);
        Assert.InRange(Value(fp, "stair.riser")!.Value, riser - 0.03, riser + 0.03);
        Assert.InRange(Value(fp, "stair.tread")!.Value, tread - 0.1, tread + 0.1);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(8.0)]
    public void Room_Height_Is_Recovered(double height)
    {
        LevelFingerprint fp = Analyze(Build($"kit.room{{name = 'r', pos = {{0, 0, 0}}, size = {{12, {height}, 12}}}}"));
        _out.WriteLine(fp.Json["space"]!.ToJsonString());
        Assert.InRange(Value(fp, "space.headroom")!.Value, height - 0.5, height + 0.5);
        Assert.InRange(Value(fp, "space.floor_area")!.Value, 144 - 12, 144 + 1); // 12 x 12 floor, within a lattice row
        Assert.InRange(Value(fp, "space.clear_width")!.Value, 11.5, 12.5); // facing-surface pairs: no corner slivers
        Assert.Equal(0, Value(fp, "space.open_sky_fraction"));
        Assert.Equal(0, Value(fp, "opening.count"));
        Assert.Equal(0, Value(fp, "corridor.fraction"));
    }

    [Fact]
    public void Portalised_Loop_Topology_And_Portal_Size()
    {
        LevelFingerprint fp = Analyze(Build(Goldens.Single(g => g.Name == "portal-loop").Kit));
        _out.WriteLine(fp.Json["topology"]!.ToJsonString());
        _out.WriteLine(fp.Json["portal"]!.ToJsonString());
        // Each kit portal doorway is an air portal brush that cuts the wall (RED's construct): a room of its own with a
        // portal at each end, so the ring is 4 rooms + 4 doorway rooms joined by 8 portals.
        Assert.Equal(8, (int)Value(fp, "topology.rooms")!);
        Assert.Equal(8, (int)Value(fp, "topology.portals")!);
        Assert.Equal(1, (int)Value(fp, "topology.loops")!);
        Assert.Equal(1, (int)Value(fp, "topology.components")!);
        Assert.Equal(0, (int)Value(fp, "topology.dead_ends")!);
        Assert.Equal(4, (int)Value(fp, "topology.diameter")!);
        Assert.InRange(Value(fp, "portal.width")!.Value, 2.5, 3.5);   // kit door default 3 x 3
        Assert.InRange(Value(fp, "portal.height")!.Value, 2.5, 3.5);
        Assert.NotNull(fp.Json["room"]); // 4 main rooms: the AABB group is emitted
        JsonArray motifs = fp.Json["events"]!["motifs"]!.AsArray();
        Assert.Contains(motifs, m => m!["motif"]!.GetValue<string>() == "Trigger>Event:Delay");
    }

    /// <summary>
    /// Extractor 2 (DESIGN-QUALITY-SPEC E12): identity.playerClass from the spawn count, and identity.class from the
    /// design grader's class detector when asked for (the corpus sweep asks) — or from the gametype for payload.
    /// </summary>
    [Fact]
    public void Identity_Carries_The_Player_Class_And_The_Level_Class()
    {
        byte[] bytes = Build("level.props:set('multiplayer', true) local r = kit.room{name = 'r', pos = {0, 0, 0}, size = {16, 5, 16}}\n" +
                             "for i = 1, 4 do level.place{kind = 'respawn', pos = {-6 + 3 * i, 1, 0}} end");
        JsonNode id = Analyze(bytes, "dmbox.rfl").Json["identity"]!;
        Assert.Equal("S", id["playerClass"]!.GetValue<string>());
        Assert.Null(id["class"]); // the spatial detector runs only when asked for

        JsonNode measured = Analyze(bytes, "dmbox.rfl", new MetricOptions { FileName = "dmbox.rfl", LevelClass = true }).Json["identity"]!["class"]!;
        Assert.Equal("standard", measured["value"]!.GetValue<string>());
        Assert.Equal("detector", measured["source"]!.GetValue<string>());
        Assert.Equal(0, measured["falloff_edge_fraction"]!.GetValue<double>());

        JsonNode payload = Analyze(bytes, "pay_box.rfl").Json["identity"]!["class"]!;
        Assert.Equal("payload", payload["value"]!.GetValue<string>());
        Assert.Equal("gametype", payload["source"]!.GetValue<string>());
        Assert.Equal(2, FingerprintSchema.ExtractorVersion);
    }

    [Fact]
    public void Level_Without_Geometry_Is_Object_Only()
    {
        byte[] bytes = Ged.Core.Editor.LevelTemplates.CreateNew(new DateTime(2026, 1, 1)).SaveToBytes(updateTimestamp: false);
        LevelFingerprint fp = Analyze(bytes);
        Assert.Equal(FingerprintSchema.Status.OkPartial, fp.Status);
        Assert.Null(fp.Json["space"]);
        Assert.Equal("no_geometry", fp.Json["skipped"]!["space"]!.GetValue<string>());
        Assert.NotNull(fp.Json["counts"]);
        Assert.NotNull(fp.Json["lights"]);
        Assert.Equal("sp", fp.Json["identity"]!["gametype"]!["value"]!.GetValue<string>());
    }

    // ---- goldens (§8.2) -------------------------------------------------------------------------

    public static IEnumerable<object[]> GoldenNames() => Goldens.Select(g => new object[] { g.Name });

    /// <summary>
    /// The 3 golden fingerprints. Regenerate with GED_REGEN_FINGERPRINT_GOLDENS=1 after an intended change to a
    /// metric (and bump <see cref="FingerprintSchema.ExtractorVersion"/> when a definition changed).
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void Golden_Fingerprint(string name)
    {
        string kit = Goldens.Single(g => g.Name == name).Kit;
        string text = Analyze(Build(kit), name + ".rfl").ToJsonText();
        string path = Path.Combine(GoldenDir, name + ".json");
        if (Environment.GetEnvironmentVariable("GED_REGEN_FINGERPRINT_GOLDENS") == "1")
        {
            Directory.CreateDirectory(GoldenDir);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(path), $"golden missing: {path} (run with GED_REGEN_FINGERPRINT_GOLDENS=1)");
        string expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, text);
    }

    // ---- determinism ----------------------------------------------------------------------------

    [Fact]
    public void Fingerprinting_Twice_Is_Byte_Identical()
    {
        byte[] bytes = Build(Goldens[1].Kit);
        string a = Analyze(bytes, "x.rfl").ToJsonText();
        string b = Analyze((byte[])bytes.Clone(), "x.rfl").ToJsonText();
        Assert.Equal(a, b);
        Assert.DoesNotContain("\r", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_Does_Not_Modify_The_Level()
    {
        byte[] bytes = Build(Goldens[1].Kit);
        byte[] copy = (byte[])bytes.Clone();
        RflFile rfl = RflFile.Load(bytes);
        LevelMetrics.Compute(rfl, new MetricOptions { FileName = "x.rfl" }, CancellationToken.None);
        Assert.Equal(copy, bytes);
        Assert.Equal(copy, rfl.Save(updateTimestamp: false)); // the loaded file still serializes byte-identically
    }

    // ---- malformed input ------------------------------------------------------------------------

    [Fact]
    public void Empty_And_Bad_Magic_Throw_Format_Errors()
    {
        Assert.Throws<RflFormatException>(() => LevelMetrics.Analyze(Array.Empty<byte>(), null, CancellationToken.None));
        Assert.Throws<RflFormatException>(() => LevelMetrics.Analyze(Encoding.ASCII.GetBytes("this is not a level file at all......"), null, CancellationToken.None));
    }

    [Fact]
    public void Unsupported_Version_Is_Header_Only()
    {
        byte[] bytes = Build(Goldens[0].Kit);
        BitConverter.GetBytes(307).CopyTo(bytes, 4); // 306 is supported since Alpine 1.5; 307 is not
        LevelFingerprint fp = Analyze(bytes);
        Assert.Equal(FingerprintSchema.Status.UnsupportedVersion, fp.Status);
        Assert.Equal(307, fp.Json["identity"]!["rflVersion"]!.GetValue<int>());
        Assert.Null(fp.Json["space"]);
    }

    /// <summary>
    /// Truncation at every section boundary (and inside sections) and a section length past EOF never let a
    /// non-format exception escape: each cut either parses (fewer sections) or throws a recognisable error the
    /// sweep records as <c>parse_error</c>.
    /// </summary>
    [Fact]
    public void Truncated_And_Overlong_Sections_Fail_Cleanly()
    {
        byte[] bytes = Build(Goldens[1].Kit);
        RflFile rfl = RflFile.Load(bytes);
        var cuts = new List<int>();
        int cursor = bytes.Length - rfl.Sections.Sum(s => 8 + s.RawBytes.Length) - rfl.TrailingBytes.Length;
        foreach (RflSection s in rfl.Sections)
        {
            cuts.Add(cursor);
            cuts.Add(cursor + 8 + (s.RawBytes.Length / 2));
            cursor += 8 + s.RawBytes.Length;
        }

        cuts.Add(LevelMetrics.MinHeaderBytes);
        cuts.Add(LevelMetrics.MinHeaderBytes - 1);
        foreach (int cut in cuts.Where(c => c > 0 && c < bytes.Length).Distinct())
        {
            byte[] t = bytes[..cut];
            AssertCleanOutcome(t);
        }

        // A section length running past EOF.
        byte[] overlong = (byte[])bytes.Clone();
        int first = bytes.Length - rfl.Sections.Sum(s => 8 + s.RawBytes.Length) - rfl.TrailingBytes.Length;
        BitConverter.GetBytes(int.MaxValue / 2).CopyTo(overlong, first + 4);
        AssertCleanOutcome(overlong);

        // Garbage inside the geometry section body.
        byte[] garbled = (byte[])bytes.Clone();
        int pos = first;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.StaticGeometry)
            {
                for (int i = 0; i < Math.Min(64, s.RawBytes.Length); i++)
                {
                    garbled[pos + 8 + i] = 0xFF;
                }
            }

            pos += 8 + s.RawBytes.Length;
        }

        AssertCleanOutcome(garbled);
    }

    private static void AssertCleanOutcome(byte[] bytes)
    {
        try
        {
            LevelFingerprint fp = LevelMetrics.Analyze(bytes, new MetricOptions { FileName = "t.rfl" }, CancellationToken.None);
            Assert.NotNull(fp.Json["identity"]);
        }
        catch (Exception ex) when (ex is RflFormatException or EndOfStreamException or InvalidDataException or ArgumentException
            or IndexOutOfRangeException or InvalidOperationException or OverflowException or FormatException or NotSupportedException)
        {
            // Recorded by the sweep as parse_error.
        }
    }

    // ---- timeout --------------------------------------------------------------------------------

    [Fact]
    public void Slow_Sampler_Honours_The_Cancellation_Token()
    {
        // The token is cancelled from inside the sampler at a fixed floor sample (not by a timer, which a loaded thread
        // pool can fire after the run is over), and the run must stop within one of the sampler's cancellation checks
        // (every 1,024 rays; a sample casts at least one), well short of what an uncancelled run measures.
        byte[] bytes = Build(Goldens[0].Kit);
        int total = 0;
        LevelMetrics.Analyze(bytes, new MetricOptions { FileName = "slow.rfl", SamplerProbe = () => total++ }, CancellationToken.None);

        const int CancelAt = 20;
        int calls = 0;
        using var cts = new CancellationTokenSource();
        var options = new MetricOptions
        {
            FileName = "slow.rfl",
            SamplerProbe = () =>
            {
                if (++calls == CancelAt)
                {
                    cts.Cancel();
                }
            },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => LevelMetrics.Analyze(bytes, options, cts.Token));
        _out.WriteLine($"uncancelled run: {total} samples; cancelled at {CancelAt}, stopped after {calls}");
        Assert.InRange(calls, CancelAt, CancelAt + 1024);
        Assert.True(total > 2 * calls, $"the uncancelled run measures {total} samples, the cancelled one stopped after {calls}");
    }

    // ---- identity helpers ---------------------------------------------------------------------------

    [Theory]
    [InlineData("  [ABC] Some  Name!! ", "some name")]
    [InlineData("{clan}Builder", "builder")]
    [InlineData("\u0093Quoted\u0094", "quoted")]
    [InlineData("", "")]
    public void Author_Normalisation(string raw, string expected) => Assert.Equal(expected, LevelIdentity.NormalizeAuthor(raw));

    [Theory]
    [InlineData(false, "dm01.rfl", false, "sp", "levelinfo", false)]
    [InlineData(true, "dm01.rfl", false, "dm", "prefix", false)]
    [InlineData(true, "ctf_base.rfl", true, "ctf", "prefix", false)]
    [InlineData(true, "ctf_base.rfl", false, "ctf", "prefix", true)]
    [InlineData(true, "dm-arena.rfl", true, "dm", "prefix", true)]
    [InlineData(true, "koth_hill.rfl", false, "koth", "prefix", false)]
    [InlineData(true, "mymap.rfl", true, "ctf", "objects", false)]
    [InlineData(true, "mymap.rfl", false, "mp_other", "prefix", false)]
    public void Gametype_Classification(bool mp, string file, bool flags, string value, string source, bool conflict)
    {
        GametypeVerdict v = LevelIdentity.ClassifyGametype(mp, file, flags, hasTeamSpawns: false);
        Assert.Equal((value, source, conflict), (v.Value, v.Source, v.Conflict));
    }

    [Theory]
    [InlineData("mymap_v2.rfl", "mymap")]
    [InlineData("mymap-b3.rfl", "mymap")]
    [InlineData("mymap_final.rfl", "mymap")]
    [InlineData("mymap beta.rfl", "mymap")]
    [InlineData("mymap_2.rfl", "mymap")]
    [InlineData("dm01.rfl", "dm01")]
    [InlineData("ctf_base_v2_final.rfl", "ctf_base")]
    public void Version_Series_Base_Name(string file, string expected) => Assert.Equal(expected, LevelIdentity.SeriesBaseName(file));

    [Theory]
    [InlineData("level_autosave.rfl", true)]
    [InlineData("level.bak.rfl", true)]
    [InlineData("~level.rfl", true)]
    [InlineData("level~.rfl", true)]
    [InlineData("level - Copy.rfl", true)]
    [InlineData("level_backup2.rfl", true)]
    [InlineData("level.rfl", false)]
    public void Autosave_Names(string name, bool excluded) => Assert.Equal(excluded, LevelIdentity.IsAutosaveName(name));

    [Fact]
    public void Percentile_Type7_On_A_Hand_Computed_Set()
    {
        double[] s = { 1, 2, 3, 4, 10 };
        Assert.Equal(1.2, Percentiles.Type7(s, 0.05), 9);   // h = 0.2
        Assert.Equal(2.0, Percentiles.Type7(s, 0.25), 9);   // h = 1
        Assert.Equal(3.0, Percentiles.Type7(s, 0.50), 9);
        Assert.Equal(4.0, Percentiles.Type7(s, 0.75), 9);
        Assert.Equal(8.8, Percentiles.Type7(s, 0.95), 9);   // h = 3.8 → 4 + 0.8 × 6
    }
}
