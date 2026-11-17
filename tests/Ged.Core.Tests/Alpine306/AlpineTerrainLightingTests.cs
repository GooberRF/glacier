using System.IO;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// Terrain lighting charts (ALPINE-COMPAT-SPEC C9): Alpine's pinned chart fingerprint vectors, the fingerprints Alpine RED
/// stored in every corpus terrain chart, the terrain table Glacier writes (validator parity), and Glacier's terrain
/// chart texels against Alpine RED's own <c>-bake -rawlightmaps</c> bakes of the terrain reference levels
/// (<c>research/fixtures/alpine-ref/&lt;id&gt;/terrain-bake-raw</c>, gitignored; the comparison skips without them).
/// </summary>
public sealed class AlpineTerrainLightingTests
{
    private readonly ITestOutputHelper _out;

    public AlpineTerrainLightingTests(ITestOutputHelper output) => _out = output;

    /// <summary>The 3 x 3 grid of Alpine's pinned fingerprint vectors (alpine_terrain.h:1883-1889, 1955-1968).</summary>
    private static AlpineTerrain PinnedGrid(byte decorationFlags)
    {
        var t = new AlpineTerrain
        {
            Uid = -7,
            Origin = new Vec3(-12.5f, -0.0f, 1024f),
            CellSize = 2f,
            HeightMin = -3f,
            HeightRange = 64f,
            Flags = AlpineTerrainRules.FlagGeoable,
            Thickness = 16f,
            SkirtDepth = 8f,
            Grid = new AlpineTerrainGrid
            {
                Nx = 3,
                Nz = 3,
                WeightResolution = 1,
                Heights = new ushort[] { 0, 1, 0xFFFF, 0x1234, 0x8000, 0xABCD, 7, 0x0100, 0x7FFF },
                Weights = new byte[32],
                Holes = new byte[] { 0x04 },
                Diagonals = new byte[] { 0x09 },
                DecorationCoverage = new byte[] { 255, 0, 17, 128 },
            },
        };
        t.Layers.Add(new AlpineTerrainLayer());
        t.Decorations.Add(new AlpineTerrainDecoration("rock.v3m", 0.5f, 0.75f, 1.5f, 35f, 80f, 0.5f, AlpineTerrainRules.DecorationLinkNone, decorationFlags));
        return t;
    }

    [Fact]
    public void Chart_Fingerprint_Matches_Alpines_Pinned_Vectors()
    {
        const byte flags = AlpineTerrainSurface.DecorationFlagRandomYaw | AlpineTerrainSurface.DecorationFlagCastsShadows;

        // alpine_terrain.h:1955-1968: drawing-only flags do not move it
        Assert.Equal(0x2BD621293A0026F6UL, AlpineTerrainSurface.ChartFingerprint(PinnedGrid(flags)));
        Assert.Equal(0x2BD621293A0026F6UL, AlpineTerrainSurface.ChartFingerprint(PinnedGrid(flags | AlpineTerrainSurface.DecorationFlagDitherFade)));
        Assert.Equal(0x2BD621293A0026F6UL, AlpineTerrainSurface.ChartFingerprint(PinnedGrid(flags | AlpineTerrainSurface.DecorationFlagEdges)));

        // without a shadow-casting decoration the chart fingerprint is the lighting fingerprint (:1883-1889, 1948-1951)
        Assert.Equal(0xEE420A033E6AF829UL, AlpineTerrainSurface.ChartFingerprint(PinnedGrid(AlpineTerrainSurface.DecorationFlagRandomYaw)));
    }

    [Fact]
    public void Decoration_Texel_Key_Matches_Alpines_Pinned_Vector()
    {
        // alpine_terrain.h:1703
        Assert.Equal(0xE541EBED09662C5FUL, AlpineTerrainSurface.DecorationTexelKey(AlpineTerrainSurface.DecorationSeed(-7, 3), 12, 34));
    }

    /// <summary>
    /// Every usable terrain chart Alpine RED stored in a corpus level carries the chart fingerprint Glacier computes for
    /// that level's terrain (when the terrain is unchanged since the bake, which a level fresh from Alpine RED is).
    /// </summary>
    [Fact]
    public void Reference_Terrain_Charts_Carry_Glaciers_Chart_Fingerprint() => CheckFingerprints(includeCorpus: false);

    [Fact]
    [Trait("Category", "DeepGate")] // loads every v306 corpus level (~70 s)
    public void Corpus_Terrain_Charts_Carry_Glaciers_Chart_Fingerprint() => CheckFingerprints(includeCorpus: true);

    private void CheckFingerprints(bool includeCorpus)
    {
        int matched = 0, records = 0, stale = 0;
        foreach (string path in TerrainLightingLevels(includeCorpus))
        {
            RflFile rfl = RflFile.Load(path);
            rfl.ParseAllKnownSections();
            if (rfl.Sections.Select(s => s.Content).OfType<AlpineTerrainSection>().FirstOrDefault() is not { IsReadOnly: false } terrains)
            {
                continue;
            }

            byte[] body = rfl.Sections.First(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(rfl.Context);
            AlpineLightmapReadResult read = AlpineLightmapValidator.Read(body, null);
            for (int i = 0; i < read.Terrains.Count; i++)
            {
                AlpineTerrainChartRecord c = read.Terrains[i];
                if (terrains.Terrains.FirstOrDefault(t => t.Uid == c.TerrainUid) is not { } t)
                {
                    continue;
                }

                records++;
                if (AlpineLightmapValidator.TerrainChartMatches(c, t))
                {
                    matched++;
                }
                else
                {
                    stale++;
                    _out.WriteLine($"{Path.GetFileName(path)} terrain {t.Uid}: stored {c.Fingerprint:X16} Glacier {AlpineTerrainSurface.ChartFingerprint(t):X16} ({c.W}x{c.H})");
                }
            }
        }

        _out.WriteLine($"{matched} of {records} terrain chart records match ({stale} stale)");

        // Measured: the 5 reference bakes 6 / 6; the corpus 36 of 37 (tera12~.rfl, an autosave whose terrain was edited
        // after its bake, is the stale one).
        Assert.True(stale <= (includeCorpus ? 1 : 0), $"{stale} stale terrain chart records");
    }

    private static IEnumerable<string> TerrainLightingLevels(bool includeCorpus)
    {
        foreach (string level in RefLevels)
        {
            if (AlpineLightmapParityTests.Reference(level, "terrain-bake-raw") is { } p)
            {
                yield return p;
            }
        }

        foreach (string f in includeCorpus ? Alpine306Corpus.CorpusFiles : Array.Empty<string>())
        {
            RflFile rfl = RflFile.Load(f);
            if (rfl.Sections.Any(s => s.TypeId == (uint)SectionType.AlpineTerrain) && rfl.Sections.Any(s => s.TypeId == (uint)SectionType.AlpineLightmaps))
            {
                yield return f;
            }
        }
    }

    private static readonly string[] RefLevels = { "tersunmesh1.rfl", "c8_terrain_check.rfl", "kk1.rfl", "tertest5.rfl", "teraa.rfl" };

    /// <summary>
    /// Glacier's sealed lit build of each terrain reference level writes a terrain table the validator accepts, with
    /// Alpine RED's chart sizes, origins, texel sizes and fingerprints, and terrain texels close to Alpine RED's.
    /// </summary>
    // Ratchets (mean |Δ| per channel, 0..255), measured 2026-11-15 and recorded with a small margin; tighten, never loosen.
    // tersunmesh1: 0.038 (max 84 on a few dozen texels where Alpine RED's decorations cast shadows; Glacier's do not).
    private static readonly Dictionary<string, double> Ratchets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tertest5.rfl"] = 0.005, // 0.000
        ["kk1.rfl"] = 0.005, // 0.000
        ["teraa.rfl"] = 0.005, // 0.000 (max 1)
        ["c8_terrain_check.rfl"] = 0.005, // 0.000 (max 1)
        ["tersunmesh1.rfl"] = 0.05, // 0.038
    };

    [Fact]
    public void Fast_Tier_Terrain_Charts_Match_Alpine_REDs() => Check("tertest5.rfl");

    /// <summary>
    /// A level saved as loaded keeps Alpine RED's terrain lighting silently and byte for byte; once a terrain is sculpted
    /// the save log says its lighting is out of date (Alpine RED's <c>warn_terrain_lighting</c>), and the chunk is kept.
    /// </summary>
    [Fact]
    public void Sculpting_A_Baked_Terrain_Notes_Its_Lighting_Is_Out_Of_Date()
    {
        if (AlpineLightmapParityTests.Reference("tertest5.rfl", "terrain-bake-raw") is not { } path)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        Ged.Core.Editor.EditorDocument doc = Ged.Core.Editor.EditorDocument.OpenBytes(original);
        Assert.DoesNotContain(doc.Rfl.UpgradeToAlpine(), n => n.Contains("lighting was baked", StringComparison.Ordinal));
        Assert.Equal(original, doc.Rfl.Save(updateTimestamp: false));

        Ged.Core.Editor.LevelObject obj = doc.Objects.First(o => o.Model is AlpineTerrain);
        var t = (AlpineTerrain)obj.Model;
        var stroke = new Ged.Core.Editing.TerrainStroke(doc, obj, new Ged.Core.Editing.TerrainBrushSettings { Tool = Ged.Core.Editing.TerrainTool.Raise, Radius = 6f, Strength = 1f });
        stroke.MoveTo(t.Origin.X + (AlpineTerrainRules.Extent(t.Nx, t.CellSize) * 0.5f), t.Origin.Z + (AlpineTerrainRules.Extent(t.Nz, t.CellSize) * 0.5f));
        Assert.True(stroke.Finish());
        IReadOnlyList<string> notes = doc.Rfl.UpgradeToAlpine();
        Assert.Contains(notes, n => n.Contains("changed since its lighting was baked", StringComparison.Ordinal));
        Assert.Contains(doc.Rfl.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
    }

    [Theory]
    [Trait("Category", "DeepGate")] // a sealed lit build per level (c8_terrain_check ~80 s)
    [InlineData("tersunmesh1.rfl")]
    [InlineData("c8_terrain_check.rfl")]
    [InlineData("kk1.rfl")]
    [InlineData("teraa.rfl")]
    public void Glacier_Terrain_Charts_Match_Alpine_REDs(string level) => Check(level);

    private void Check(string level)
    {
        if (AlpineLightmapParityTests.Reference(level, "terrain-bake-raw") is not { } path)
        {
            return;
        }

        RflFile reference = RflFile.Load(path);
        reference.ParseAllKnownSections();
        byte[] refBody = reference.Sections.First(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(reference.Context);
        AlpineLightmapReadResult refRead = AlpineLightmapValidator.Read(refBody, null);
        byte[] refLayer = AlpineLightmapValidator.DecodeLayer(refBody, refRead)!;
        Assert.NotNull(refLayer);
        var refPages = Enumerable.Range(0, refRead.PageCount).Select(p => refLayer.AsSpan(p * AlpineLightmapLayout.RawPageBytes, AlpineLightmapLayout.RawPageBytes).ToArray()).ToList();

        RflFile glacier = RflFile.Load(path);
        glacier.ParseAllKnownSections();
        CompileOptions options = CompileOptionsFactory.Create(glacier, BuildSettings.EditorDefaults, new CompileRequest { Sealed = true, BakeLighting = true, CastShadows = true });
        options.Lighting.AlpineCharts = options.Lighting.AlpineCharts! with { Codec = AlpineLightmapCodec.RawRgb8 };
        CompiledLevel result = GeometryBuildService.BuildAndApply(glacier, options);
        AlpineLightmapBake bake = Assert.IsType<AlpineLightmapBake>(result.BakeStats?.AlpineLightmaps);
        foreach (string line in bake.Log)
        {
            _out.WriteLine(line);
        }

        // Validator parity: Glacier's own section reads back with its terrain table usable.
        AlpineLightmapReadResult mine = AlpineLightmapValidator.Read(AlpineLightmapWriter.Stamp(bake.Body, result.Geometry), result.Geometry);
        Assert.True(mine.Ok, mine.Reason);
        Assert.Equal(string.Empty, mine.TerrainReason);
        Assert.All(mine.TerrainOk, Assert.True);

        Assert.Equal(refRead.Terrains.Count, bake.Terrains.Count);

        // The atlas Alpine RED wrote for the same level: page and tile counts equal (c8_terrain_check: 237 pages, 259
        // tiles — its six sky-box surfaces at 8 texels per unit fill 236 pages in Alpine RED's own bake too).
        Assert.Equal(refRead.PageCount, bake.Pages.Length);
        Assert.Equal(refRead.Tiles.Count, bake.Tiles.Length);

        // The terrain charts are tiled exactly as Alpine RED tiles them. (Surface tiles are not compared slot by slot:
        // the surfaces come from Glacier's own rebuild of the level, whose surface extents can differ slightly from the
        // ones RED compiled, which moves where the packer puts them; tertest5's and teraa's atlases are identical.)
        var diffs = new List<int>();
        foreach ((AlpineTerrainChartRecord c, int tileBase) in bake.TerrainTiles())
        {
            int k = refRead.Terrains.FindIndex(r => r.TerrainUid == c.TerrainUid);
            Assert.True(k >= 0, $"terrain {c.TerrainUid} has no chart in Alpine RED's bake");
            AlpineTerrainChartRecord r = refRead.Terrains[k];
            Assert.Equal((r.W, r.H, r.OriginX, r.OriginZ, r.TexelSize, r.Fingerprint), (c.W, c.H, c.OriginX, c.OriginZ, c.TexelSize, c.Fingerprint));
            int n = c.Geometry.TileCount;
            Assert.Equal(
                refRead.Tiles.Skip(refRead.TerrainBases[k]).Take(n).Select(t => (t.X, t.Y)),
                bake.Tiles.Skip(tileBase).Take(n).Select(t => (t.X, t.Y)));
            byte[] g = AlpineTerrainCharts.Gather(c, tileBase, bake.Tiles, bake.Pages)!;
            byte[] a = AlpineTerrainCharts.Gather(r, refRead.TerrainBases[k], refRead.Tiles, refPages)!;
            for (int i = 0; i < g.Length; i++)
            {
                diffs.Add(Math.Abs(g[i] - a[i]));
            }

            if (Environment.GetEnvironmentVariable("GED_TERRAIN_LM_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                var side = new byte[c.W * 2 * c.H * 4];
                for (int y = 0; y < c.H; y++)
                {
                    for (int x = 0; x < c.W; x++)
                    {
                        for (int ch = 0; ch < 3; ch++)
                        {
                            side[((((c.H - 1 - y) * c.W * 2) + x) * 4) + ch] = g[(((y * c.W) + x) * 3) + ch];
                            side[((((c.H - 1 - y) * c.W * 2) + c.W + x) * 4) + ch] = a[(((y * c.W) + x) * 3) + ch];
                        }

                        side[((((c.H - 1 - y) * c.W * 2) + x) * 4) + 3] = 255;
                        side[((((c.H - 1 - y) * c.W * 2) + c.W + x) * 4) + 3] = 255;
                    }
                }

                File.WriteAllBytes(Path.Combine(dump, $"{Path.GetFileNameWithoutExtension(level)}_terrain{c.TerrainUid}.png"),
                    Ged.Core.IO.Tex.PngWriter.Encode(c.W * 2, c.H, side));
            }
        }

        diffs.Sort();
        double mean = diffs.Count == 0 ? 0 : diffs.Average();
        int p95 = diffs.Count == 0 ? 0 : diffs[(int)(diffs.Count * 0.95)];
        _out.WriteLine($"{level}: terrain texels mean |d| {mean:0.000}, p95 {p95}, max {(diffs.Count == 0 ? 0 : diffs[^1])} over {diffs.Count / 3} texels");
        Assert.True(diffs.Count > 0);
        Assert.True(mean <= Ratchets[level], $"{level}: terrain texel mean |d| {mean:0.000} exceeds the ratchet {Ratchets[level]}");
    }
}
