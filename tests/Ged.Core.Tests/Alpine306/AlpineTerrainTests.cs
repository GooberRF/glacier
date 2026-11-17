using System.IO;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The Alpine terrain data model (chunk 0x0AFBAE0B, ALPINE-COMPAT-SPEC §1.6, §4): Alpine's pinned hash vectors, the typed
/// reader / writer (blob included) over synthetic records and every corpus terrain level, and the save rules.
/// </summary>
public sealed class AlpineTerrainTests
{
    private static readonly RflContext V306 = new(RflFile.AlpineSaveVersion);
    private readonly ITestOutputHelper _output;

    public AlpineTerrainTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Every v306 corpus level holding a terrain chunk (full paths); empty without a corpus root.</summary>
    public static IReadOnlyList<string> CorpusTerrainFiles() => TerrainFiles.Value;

    private static readonly Lazy<IReadOnlyList<string>> TerrainFiles =
        new(() => Alpine306Corpus.CorpusFiles.Where(f => HasTerrainChunk(File.ReadAllBytes(f))).ToList());

    /// <summary>The copied fixture levels holding a terrain chunk.</summary>
    public static IReadOnlyList<string> FixtureTerrainFiles() =>
        Alpine306Corpus.Fixtures.Where(f => HasTerrainChunk(File.ReadAllBytes(f))).ToList();

    private static bool HasTerrainChunk(byte[] bytes)
    {
        RflFile rfl = RflFile.Load(bytes);
        return rfl.Sections.Any(s => s.TypeId == (uint)SectionType.AlpineTerrain);
    }

    [Fact]
    public void Position_Hash_Term_Matches_Alpines_Pinned_Vector()
    {
        // alpine_terrain.h:1608
        Assert.Equal(0x3E086D6D9F723AD7UL, AlpineTerrainRules.PositionHashTerm(0x3F800000u, 0xC2280000u, 0u));
        Assert.Equal(0u, AlpineTerrainRules.PositionBits(-0f));
    }

    [Fact]
    public void Lighting_Fingerprint_Matches_Alpines_Pinned_Vector()
    {
        // alpine_terrain.h:1883-1889
        ushort[] heights = { 0, 1, 0xFFFF, 0x1234, 0x8000, 0xABCD, 7, 0x0100, 0x7FFF };
        ulong fp = AlpineTerrainRules.LightingFingerprint(new Vec3(-12.5f, -0.0f, 1024f), 2f, -3f, 64f, 3, 3,
            AlpineTerrainRules.FlagGeoable, 16f, 8f, heights, new byte[] { 0x04 }, new byte[] { 0x09 });
        Assert.Equal(0xEE420A033E6AF829UL, fp);
    }

    [Fact]
    public void Effective_Chunk_Cells_Follow_The_Caps()
    {
        Assert.Equal(64, AlpineTerrainRules.MaxChunkCells(0));
        Assert.Equal(32, AlpineTerrainRules.MaxChunkCells(AlpineTerrainRules.FlagSkirts));
        Assert.Equal(32, AlpineTerrainRules.MaxChunkCells(AlpineTerrainRules.FlagGeoable));
        Assert.Equal(64, AlpineTerrainRules.EffectiveChunkCells(256, 256, 128, 0));
        Assert.Equal(16, AlpineTerrainRules.EffectiveChunkCells(256, 256, 16, 0));
        Assert.Equal(32, AlpineTerrainRules.EffectiveChunkCells(128, 128, 32, AlpineTerrainRules.FlagGeoable));
    }

    [Fact]
    public void A_Created_Terrain_Round_Trips_Through_The_Writer()
    {
        AlpineTerrain t = AlpineTerrainEdit.Create(77, new Vec3(-10, 2, -10), 33, 17, 1.5f);
        t.Grid.Heights[5] = 40000;
        AlpineTerrainRules.SetBit(t.Grid.Holes, 3, true);
        var section = new AlpineTerrainSection();
        section.Terrains.Add(t);
        byte[] bytes = Bytes(section);

        var parsed = (AlpineTerrainSection)Parse(bytes);
        Assert.Null(parsed.StructureProblem);
        AlpineTerrain back = Assert.Single(parsed.Terrains);
        Assert.Equal(33, back.Nx);
        Assert.Equal(17, back.Nz);
        Assert.Equal(40000, back.Grid.Heights[5]);
        Assert.True(AlpineTerrainRules.GetBit(back.Grid.Holes, 3));
        Assert.Equal("Terrain", back.ScriptName);
        Assert.Equal(bytes, Bytes(parsed));
    }

    [Theory]
    [MemberData(nameof(Alpine306Corpus.FixtureNames), MemberType = typeof(Alpine306Corpus))]
    public void Fixture_Terrain_Chunks_Parse_Typed_And_Rewrite_Byte_Identically(string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        List<string> failures = CheckRoundTrip(Path.Combine(Alpine306Corpus.FixtureDirectory!, fileName), out _);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The corpus levels whose terrain chunk is a pre-release layout the current Alpine reader rejects too, so the game
    /// ignores the chunk: ter_deco / ter_deco~ (an older decoration record, read as an out-of-range link layer) and
    /// tertest4a / tertest4a~ (a blob 24 bytes longer than the header implies). Kept raw and read-only.
    /// </summary>
    private const int MaxPreReleaseTerrainChunks = 4;

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Every_Corpus_Terrain_Chunk_Parses_Typed_And_Rewrites_Byte_Identically()
    {
        IReadOnlyList<string> files = CorpusTerrainFiles();
        if (files.Count == 0)
        {
            return;
        }

        var failures = new List<string>();
        int preRelease = 0, terrains = 0;
        foreach (string f in files)
        {
            failures.AddRange(CheckRoundTrip(f, out AlpineTerrainSection? s));
            if (s?.StructureProblem is { } problem)
            {
                preRelease++;
                _output.WriteLine($"{Path.GetFileName(f)}: kept raw ({problem})");
            }

            terrains += s?.Terrains.Count ?? 0;
        }

        _output.WriteLine($"{files.Count} terrain levels, {terrains} terrains, {preRelease} raw chunks, {failures.Count} failures.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(preRelease <= MaxPreReleaseTerrainChunks, $"{preRelease} raw terrain chunks (ratchet {MaxPreReleaseTerrainChunks})");
    }

    // ---- emitter snapshots against Alpine RED's own compiled chunks ---------------------------------------------

    /// <summary>
    /// Alpine RED compiles each chunk brush with its emitted positions bit for bit (terrain_build.cpp:255-264) and stores
    /// the room's distinct vertex count and position hash. So for every built corpus terrain, Glacier's emitter must
    /// reproduce each chunk's vertex set exactly: the emitter snapshot test against RED's output bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Alpine306Corpus.FixtureNames), MemberType = typeof(Alpine306Corpus))]
    public void Emitter_Reproduces_Alpine_REDs_Compiled_Chunks_On_Fixtures(string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        (int matched, int total, List<string> misses) = EmitterAgreement(Path.Combine(Alpine306Corpus.FixtureDirectory!, fileName));
        _output.WriteLine($"{fileName}: {matched}/{total} chunks");
        Assert.True(matched == total, string.Join(Environment.NewLine, misses));
    }

    /// <summary>Corpus-wide emitter agreement (ratchet: every chunk of every built terrain).</summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void Emitter_Reproduces_Alpine_REDs_Compiled_Chunks_Across_The_Corpus()
    {
        IReadOnlyList<string> files = CorpusTerrainFiles();
        if (files.Count == 0)
        {
            return;
        }

        int matched = 0, total = 0;
        var misses = new List<string>();
        foreach (string f in files)
        {
            (int m, int t, List<string> miss) = EmitterAgreement(f);
            matched += m;
            total += t;
            misses.AddRange(miss);
            _output.WriteLine($"{Path.GetFileName(f)}: {m}/{t}");
        }

        _output.WriteLine($"{matched}/{total} corpus chunks reproduced bit for bit.");
        Assert.True(matched == total, string.Join(Environment.NewLine, misses.Take(40)));
    }

    /// <summary>The ported game resolver accepts every built corpus terrain against the level's own compiled geometry.</summary>
    [Theory]
    [MemberData(nameof(Alpine306Corpus.FixtureNames), MemberType = typeof(Alpine306Corpus))]
    public void Resolver_Accepts_Alpine_Built_Terrains_On_Fixtures(string? fileName)
    {
        if (fileName is null)
        {
            return;
        }

        List<string> failures = ResolveLoaded(Path.Combine(Alpine306Corpus.FixtureDirectory!, fileName), out _);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Resolver_Accepts_Alpine_Built_Terrains_Across_The_Corpus()
    {
        IReadOnlyList<string> files = CorpusTerrainFiles();
        var failures = new List<string>();
        int resolved = 0;
        foreach (string f in files)
        {
            failures.AddRange(ResolveLoaded(f, out int n));
            resolved += n;
        }

        _output.WriteLine($"{resolved} built terrains resolve against their levels' compiled geometry; {failures.Count} do not.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---- Glacier builds against Alpine RED's -bake -buildonly output ------------------------------------------

    /// <summary>The Alpine RED -bake -buildonly references of the corpus terrain levels (research/fixtures, gitignored).</summary>
    public static IReadOnlyList<string> AlpineTerrainReferences() =>
        TestPaths.ResearchFixtures is { } root && Directory.Exists(Path.Combine(root, "alpine-ref", "8db7d90b", "terrain-build"))
            ? Directory.EnumerateFiles(Path.Combine(root, "alpine-ref", "8db7d90b", "terrain-build"), "*.rfl").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Chunks whose Glacier-compiled vertex set equals Alpine RED's own build of the same level, over the reference set
    /// (12 corpus levels and the Glacier-made c8_terrain_check, 592 chunks: all match; ratchet, measured 2026-11-15). Glacier's welder may merge a terrain vertex into a world vertex within
    /// 1e-4, the only way the two can differ; the mapping stays self-consistent either way.
    /// </summary>
    private const int MinChunksMatchingAlpineBuild = 592;

    [Fact]
    [Trait("Category", "DeepGate")]
    public void Glacier_Builds_Of_The_Alpine_Reference_Terrain_Levels_Resolve_And_Match_Alpine_RED()
    {
        IReadOnlyList<string> refs = AlpineTerrainReferences();
        if (refs.Count == 0)
        {
            return;
        }

        int matched = 0, total = 0;
        var failures = new List<string>();
        foreach (string path in refs)
        {
            byte[] bytes = File.ReadAllBytes(path);
            RflFile alpine = RflFile.Load(bytes);
            alpine.ParseAllKnownSections();
            var alpineTerrains = alpine.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().Single().Terrains;

            RflFile glacier = RflFile.Load(bytes);
            var options = Ged.Core.Compiler.CompileOptionsFactory.Create(glacier, Ged.Core.Compiler.BuildSettings.EditorDefaults, new Ged.Core.Compiler.CompileRequest());
            Ged.Core.Compiler.CompiledLevel result = Ged.Core.Compiler.GeometryBuildService.BuildAndApply(glacier, options);
            var glacierTerrains = glacier.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().Single().Terrains;
            foreach ((AlpineTerrain t, string? problem) in Ged.Core.Compiler.TerrainBuildMapping.Resolve(glacierTerrains, result.Geometry))
            {
                if (problem is not null)
                {
                    failures.Add($"{Path.GetFileName(path)}: Glacier-built terrain {t.Uid} does not resolve: {problem}");
                }
            }

            int fileMatched = 0, fileTotal = 0;
            foreach (AlpineTerrain a in alpineTerrains)
            {
                AlpineTerrain? g = glacierTerrains.FirstOrDefault(x => x.Uid == a.Uid);
                for (int k = 0; k < a.BuildMapping.Count; k++)
                {
                    fileTotal++;
                    if (g is not null && k < g.BuildMapping.Count && g.BuildMapping[k].VertexCount == a.BuildMapping[k].VertexCount
                        && g.BuildMapping[k].PositionHash == a.BuildMapping[k].PositionHash)
                    {
                        fileMatched++;
                    }
                }
            }

            // Alpine RED's own chunks are the emitted ones bit for bit: nothing in its build cuts or moves terrain
            // (c8_terrain_check.rfl carries an air brush through its hills — owner answer Q10).
            (int emitted, int emittedTotal, List<string> emitterMisses) = EmitterAgreement(path);
            if (emitted != emittedTotal)
            {
                failures.AddRange(emitterMisses);
            }

            matched += fileMatched;
            total += fileTotal;
            _output.WriteLine($"{Path.GetFileName(path)}: {fileMatched}/{fileTotal} chunks match Alpine RED; {result.Report.Rooms} rooms, {result.Report.Faces} faces");
        }

        _output.WriteLine($"{matched}/{total} chunks match Alpine RED's build.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(matched >= MinChunksMatchingAlpineBuild, $"{matched} chunks match (ratchet {MinChunksMatchingAlpineBuild})");
    }

    internal static List<string> ResolveLoaded(string path, out int resolved)
    {
        var failures = new List<string>();
        resolved = 0;
        RflFile rfl = RflFile.Load(File.ReadAllBytes(path));
        rfl.ParseAllKnownSections();
        if (rfl.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().FirstOrDefault() is not { IsReadOnly: false } section)
        {
            return failures;
        }

        Geometry? g = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        var built = section.Terrains.Where(t => t.BuildMapping.Count > 0).ToList();
        foreach ((AlpineTerrain t, string? problem) in Ged.Core.Compiler.TerrainBuildMapping.Resolve(built, g))
        {
            if (problem is null)
            {
                resolved++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(path)}: terrain {t.Uid}: {problem}");
            }
        }

        return failures;
    }

    internal static (int Matched, int Total, List<string> Misses) EmitterAgreement(string path)
    {
        var misses = new List<string>();
        int matched = 0, total = 0;
        RflFile rfl = RflFile.Load(File.ReadAllBytes(path));
        rfl.ParseAllKnownSections();
        if (rfl.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().FirstOrDefault() is not { IsReadOnly: false } section)
        {
            return (0, 0, misses);
        }

        foreach (AlpineTerrain t in section.Terrains)
        {
            if (t.BuildMapping.Count == 0 || t.BuildMapping.Count != Ged.Core.Compiler.TerrainChunkEmitter.ChunkCount(t))
            {
                continue;
            }

            var solids = Ged.Core.Compiler.TerrainChunkEmitter.BuildChunkSolids(t.DeepCopy()).ToDictionary(s => s.Chunk);
            for (int k = 0; k < t.BuildMapping.Count; k++)
            {
                AlpineTerrainChunkMapping m = t.BuildMapping[k];
                total++;
                (uint count, ulong hash) = solids.TryGetValue(k, out var solid)
                    ? AlpineTerrainRules.PositionSetHash(solid.Positions)
                    : (0u, 0UL);
                if (count == m.VertexCount && hash == m.PositionHash)
                {
                    matched++;
                }
                else
                {
                    misses.Add($"{Path.GetFileName(path)}: terrain {t.Uid} chunk {k}: emitted {count} vertices / {hash:X16}, RED stored {m.VertexCount} / {m.PositionHash:X16}");
                }
            }
        }

        return (matched, total, misses);
    }

    private static List<string> CheckRoundTrip(string path, out AlpineTerrainSection? terrain)
    {
        var failures = new List<string>();
        RflFile rfl = RflFile.Load(File.ReadAllBytes(path));
        rfl.ParseAllKnownSections();
        terrain = null;
        foreach (RflSection s in rfl.Sections.Where(s => s.TypeId == (uint)SectionType.AlpineTerrain))
        {
            terrain = (AlpineTerrainSection)s.Content!;
            byte[] written = Bytes(terrain);
            if (!written.AsSpan().SequenceEqual(s.RawBytes))
            {
                failures.Add($"{path}: terrain chunk rewrites differently ({written.Length} vs {s.RawBytes.Length} bytes, first difference at {FirstDifference(written, s.RawBytes)})");
            }

            foreach (AlpineTerrain t in terrain.IsReadOnly ? Enumerable.Empty<AlpineTerrain>() : terrain.Terrains)
            {
                if (AlpineTerrainRules.Validate(t, AlpineTerrainSection.WireFlags(t)) is { } err)
                {
                    failures.Add($"{path}: terrain {t.Uid} fails validation after load: {err}");
                }
            }
        }

        return failures;
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }

    internal static IRflSectionContent Parse(byte[] body)
    {
        var section = new RflSection((uint)SectionType.AlpineTerrain, body);
        Assert.True(RflSectionRegistry.TryParse(section, V306, out IRflSectionContent? content));
        return content!;
    }

    internal static byte[] Bytes(IRflSectionContent content)
    {
        var w = new RfWriter();
        content.Write(w, V306);
        return w.ToArray();
    }
}
