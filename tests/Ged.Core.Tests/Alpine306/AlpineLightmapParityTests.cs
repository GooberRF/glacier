using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// Glacier's Alpine lightmaps against Alpine RED's own (C6 verification, ALPINE-COMPAT-SPEC §7.2 rule 4). The
/// references are Alpine RED <c>-bake -rawlightmaps</c> outputs of stock levels
/// (<c>research/fixtures/alpine-ref/&lt;id&gt;/bake-raw</c>, gitignored — the tests skip when absent). Glacier relights
/// each reference's OWN compiled geometry and stock pages with the Alpine pipeline, so the surfaces are identical and
/// the derived atlas is comparable chart for chart:
/// <list type="bullet">
/// <item>the reader-parity validator accepts Alpine RED's sections (XXH32 fingerprint and record layout included);</item>
/// <item>Glacier derives exactly Alpine RED's chart table and packs exactly its tile table and page count;</item>
/// <item>chart texels and the stock fragments derived from them are measured against Alpine RED's and held to
/// ratchets (mean / 95th percentile absolute difference per channel), over every chart texel and over the texels
/// whose centre lies on one of the surface's faces (the only ones ever drawn: Alpine fills the rest of a chart's
/// rectangle from the nearest in-face texel, Glacier lights it as a plane). Glacier lights the charts with its own
/// stock-parity kernel rather than Alpine's fixed pipeline, so the texels are close, not equal (FEATURES).</item>
/// <item>with an Alpine sun added to the level, each bake's sun contribution is reported (bake minus a bake without
/// it), and the on-face ratchets hold with the sun as without it.</item>
/// </list>
/// </summary>
public sealed class AlpineLightmapParityTests
{
    // Ratchets (texel units, 0..255): measured 2026-11-15 and recorded with a small margin; tighten, never loosen.
    // C7 bakes with Alpine's fixed-pipeline occluder rules (AlpineOccluders), which tightened dm02 and dm12.
    private static readonly Dictionary<string, (double MeanChart, double P95Chart, double MeanStock, double MeanFace, double P95Face)> Ratchets = new(StringComparer.OrdinalIgnoreCase)
    {
        // measured: all chart texels mean / p95, stock-fragment mean, on-face chart texels mean / p95
        ["glass_house.rfl"] = (2.6, 13, 1.7, 0.9, 2), // 2.35 / 11, 1.48, 0.75 / 1
        ["dm01.rfl"] = (12.0, 75, 9.0, 4.3, 28), // 11.00 / 69, 8.10, 3.89 / 25
        ["dm02.rfl"] = (17.0, 86, 24.5, 14.0, 73), // C7 (Alpine occluder rules): 15.94 / 81, 22.90, 13.03 / 68 (C6: 16.40 / 83, 23.58, 13.69 / 70)
        ["dm10.rfl"] = (7.6, 48, 6.2, 0.8, 2), // 6.87 / 43, 5.56, 0.63 / 1
        ["dm12.rfl"] = (15.0, 92, 19.8, 10.3, 81), // C7: 13.69 / 87, 17.96, 9.36 / 73 (C6: 14.80 / 89, 20.95, 10.60 / 81)
        ["dm13.rfl"] = (5.4, 40, 3.8, 0.6, 2), // 4.82 / 36, 3.44, 0.50 / 1
        ["dm14.rfl"] = (18.4, 100, 14.1, 0.7, 2), // 16.73 / 93, 12.80, 0.58 / 1
        ["dm18.rfl"] = (6.6, 39, 4.4, 0.6, 2), // 6.00 / 35, 4.00, 0.51 / 1
        // the same levels with an Alpine sun added (yaw 35, pitch 50, intensity 1.2, spread 3; Glacier-written input)
        ["glass_house_sun.rfl"] = (2.6, 13, 1.7, 0.9, 2), // as glass_house: no sky face lets the sun in
        ["dm01_sun.rfl"] = (12.8, 80, 9.4, 4.3, 28), // 11.64 / 72, 8.46, 3.82 / 25
        ["dm02_sun.rfl"] = (20.5, 112, 26.8, 14.0, 73), // C7: 18.88 / 104, 24.81, 13.03 / 68 (C6: 19.35 / 105, 25.49, 13.69 / 69)
    };

    private readonly ITestOutputHelper _out;

    public AlpineLightmapParityTests(ITestOutputHelper output) => _out = output;

    /// <summary>The level cheap enough for the fast tier; the rest run in the deep tier.</summary>
    private const string FastLevel = "glass_house.rfl";

    public static IEnumerable<object[]> DeepLevels() => Ratchets.Keys.Where(k => k != FastLevel).Select(k => new object[] { k });

    [Fact]
    public void Fast_Tier_Level_Matches_Alpine_REDs_Atlas() => Check(FastLevel);

    [Theory]
    [Trait("Category", "DeepGate")] // a full chart bake per level; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
    [MemberData(nameof(DeepLevels))]
    public void Glacier_Matches_Alpine_REDs_Atlas_And_Stays_Within_The_Texel_Ratchets(string level) => Check(level);

    /// <summary>
    /// Alpine RED's shipping (BC7) bakes pass the validator too, with the BC7 layer picked, and carry the same chart and
    /// tile tables as its raw bakes of the same level: the codec changes only the payload.
    /// </summary>
    [Theory]
    [InlineData("glass_house.rfl")]
    [InlineData("dm14.rfl")]
    public void The_Validator_Accepts_Alpine_REDs_BC7_Sections(string level)
    {
        if (Reference(level, "bake") is not { } bc7Path || Reference(level) is not { } rawPath)
        {
            return;
        }

        (AlpineLightmapReadResult bc7, _) = ReadReference(bc7Path);
        (AlpineLightmapReadResult raw, _) = ReadReference(rawPath);
        Assert.True(bc7.Ok && bc7.SurfacesOk, bc7.Reason + bc7.SurfaceReason);
        Assert.Equal(1, bc7.LayerCodec);
        Assert.Equal((uint)(bc7.PageCount * AlpineLightmapLayout.Bc7PageBytes), bc7.LayerUncompressedSize);
        Assert.Equal(raw.Charts, bc7.Charts);
        Assert.Equal((uint)(raw.PageCount * AlpineLightmapLayout.RawPageBytes), raw.LayerUncompressedSize);
    }

    private static (AlpineLightmapReadResult Read, Geometry Geometry) ReadReference(string path)
    {
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry g = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        byte[] body = rfl.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(rfl.Context);
        return (AlpineLightmapValidator.Read(body, g), g);
    }

    private void Check(string level)
    {
        if (Reference(level) is not { } path)
        {
            return; // Alpine RED raw-lightmap references are absent (research/fixtures/alpine-ref/<id>/bake-raw)
        }

        RflFile reference = RflFile.Load(path);
        reference.ParseAllKnownSections();
        Geometry refGeo = reference.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        List<Lightmap> refPages = reference.Sections.Select(s => s.Content).OfType<LightmapsSection>().Single().Lightmaps;
        byte[] refBody = reference.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(reference.Context);

        // 1. The validator accepts Alpine RED's section against its own geometry: the fingerprint is XXH32 of the
        //    surface records as written.
        AlpineLightmapReadResult refRead = AlpineLightmapValidator.Read(refBody, refGeo);
        Assert.True(refRead.Ok, refRead.Reason);
        Assert.True(refRead.SurfacesOk, refRead.SurfaceReason);
        byte[] refLayer = AlpineLightmapValidator.DecodeLayer(refBody, refRead)!;
        Assert.NotNull(refLayer);

        // 2. The same chart table and tile table from Glacier's derivation and packer.
        int stockPage = AlpineChartBaker.FirstStockPage(refGeo.Surfaces, refPages);
        AlpineChartGeometry[] charts = AlpineChartBaker.DeriveCharts(refGeo.Surfaces, refPages, stockPage, refRead.BaseDensity, out ushort[] kU, out ushort[] kV);
        for (int i = 0; i < charts.Length; i++)
        {
            Assert.True(
                (charts[i].IsEmpty ? (ushort)0 : kU[i]) == refRead.Charts[i].KU && (charts[i].IsEmpty ? (ushort)0 : kV[i]) == refRead.Charts[i].KV,
                $"surface {i}: Glacier k {kU[i]}x{kV[i]} vs Alpine RED {refRead.Charts[i].KU}x{refRead.Charts[i].KV}");
        }

        Assert.Equal(AlpinePackResult.Ok, AlpineAtlasPacker.Pack(charts, AlpineLightmapLayout.StageRawPages, out _, out AlpineTile[] tiles, out int pages));
        Assert.Equal(refRead.PageCount, pages);
        Assert.Equal(refRead.Tiles, tiles);

        // 3. Glacier's Alpine bake of the same geometry, compared texel for texel.
        var stockBefore = refPages.Select(p => (byte[])p.Pixels.Clone()).ToList();
        List<Light> lights = reference.Sections.Where(s => s.TypeId == (uint)SectionType.Lights).Select(s => s.Content)
            .OfType<LightsSection>().SelectMany(l => l.Lights).ToList();
        RfColor? ambient = reference.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().FirstOrDefault()?.AmbientColor;
        var options = new LightingOptions { AlpineCharts = AlpineLightmapSettings.FromLevel(reference) with { Codec = AlpineLightmapCodec.RawRgb8 } };
        BakeStats stats = GeometryBuildService.Relight(reference, lights, ambient, options)!;
        AlpineLightmapBake bake = Assert.IsType<AlpineLightmapBake>(stats.AlpineLightmaps);
        Assert.Equal(refRead.BaseDensity, bake.Density);
        Assert.Equal(tiles, bake.Tiles);

        // A level with an Alpine sun: the sun's own contribution, Glacier's against Alpine RED's. Glacier's is this bake
        // minus a bake with the sun off; Alpine RED's is its bake of this level minus its bake of the level without the sun.
        string? sunNote = null;
        if (level.EndsWith("_sun.rfl", StringComparison.OrdinalIgnoreCase)
            && Reference(level.Replace("_sun.rfl", ".rfl", StringComparison.OrdinalIgnoreCase)) is { } basePath
            && AlpineLightmapSettings.FindProperties(reference) is { } sunProps)
        {
            (AlpineLightmapReadResult baseRead, _) = ReadReference(basePath);
            RflFile baseFile = RflFile.Load(basePath);
            baseFile.ParseAllKnownSections();
            byte[] baseLayer = AlpineLightmapValidator.DecodeLayer(
                baseFile.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(baseFile.Context), baseRead)!;
            byte[][] sunPages = bake.Pages.Select(p => (byte[])p.Clone()).ToArray();
            sunProps.EnableSun = 0;
            BakeStats noSun = GeometryBuildService.Relight(reference, lights, ambient, new LightingOptions { AlpineCharts = AlpineLightmapSettings.FromLevel(reference) with { Codec = AlpineLightmapCodec.RawRgb8 } })!;
            Assert.Equal(0, noSun.DirectionalLights);
            Assert.Equal(1, stats.DirectionalLights);
            long glacierSun = 0, alpineSun = 0, both = 0, n = 0;
            for (int p = 0; p < pages && baseLayer.Length == refLayer.Length; p++)
            {
                for (int k = 0; k < AlpineLightmapLayout.RawPageBytes; k++)
                {
                    int g = sunPages[p][k] - noSun.AlpineLightmaps!.Pages[p][k];
                    int a = refLayer[(p * AlpineLightmapLayout.RawPageBytes) + k] - baseLayer[(p * AlpineLightmapLayout.RawPageBytes) + k];
                    glacierSun += Math.Abs(g);
                    alpineSun += Math.Abs(a);
                    both += (g > 4 && a > 4) ? 1 : 0;
                    n += (g > 4 || a > 4) ? 1 : 0;
                }
            }

            sunNote = $"sun contribution: Glacier mean {glacierSun / (double)(pages * AlpineLightmapLayout.RawPageBytes):0.000}, " +
                      $"Alpine RED mean {alpineSun / (double)(pages * AlpineLightmapLayout.RawPageBytes):0.000}, " +
                      $"texel channels lit by the sun in both / in either {both}/{n}";
            sunProps.EnableSun = 1;
            bake = GeometryBuildService.Relight(reference, lights, ambient, new LightingOptions { AlpineCharts = AlpineLightmapSettings.FromLevel(reference) with { Codec = AlpineLightmapCodec.RawRgb8 } })!.AlpineLightmaps!;
        }

        // Every chart texel, and separately the texels whose centre lies on one of the surface's faces: the rest of a
        // chart's rectangle is never drawn (Alpine fills it from the nearest in-face texel, Glacier lights it as a plane).
        var chartDiffs = new List<int>();
        var faceDiffs = new List<int>();
        var facesOf = new Dictionary<int, List<Face>>();
        foreach (Face f in refGeo.Faces)
        {
            if (f.SurfaceIndex >= 0 && f.SurfaceIndex < charts.Length)
            {
                (facesOf.TryGetValue(f.SurfaceIndex, out List<Face>? list) ? list : facesOf[f.SurfaceIndex] = new List<Face>()).Add(f);
            }
        }

        for (int i = 0; i < charts.Length; i++)
        {
            if (charts[i].IsEmpty)
            {
                continue;
            }

            Surface surf = refGeo.Surfaces[i];
            List<(float U, float V)[]> polys = (facesOf.GetValueOrDefault(i) ?? new List<Face>())
                .Select(f => f.Vertices.Select(fv => (refGeo.Vertices[fv.Index].Component(surf.UCoefficient), refGeo.Vertices[fv.Index].Component(surf.VCoefficient))).ToArray())
                .ToList();
            for (int v = 0; v < charts[i].Ch; v++)
            {
                for (int u = 0; u < charts[i].Cw; u++)
                {
                    (int index, int sx, int sy, _) = AlpineLightmapLayout.TileSlot(charts[i], bake.Charts[i].TileBase, u, v);
                    AlpineTile t = tiles[index];
                    int o = ((((t.Y + sy) * 256) + t.X + sx) * 3);
                    float pu = (surf.X + 1 + ((u + 0.5f) / kU[i])) / stockPage, pv = (surf.Y + 1 + ((v + 0.5f) / kV[i])) / stockPage;
                    float wu = (pu - surf.UvAdd.U) / surf.UvScale.U, wv = (pv - surf.UvAdd.V) / surf.UvScale.V;
                    bool onFace = polys.Any(p => Inside(p, wu, wv));
                    for (int c = 0; c < 3; c++)
                    {
                        int d = Math.Abs(bake.Pages[t.Page][o + c] - refLayer[(t.Page * AlpineLightmapLayout.RawPageBytes) + o + c]);
                        chartDiffs.Add(d);
                        if (onFace)
                        {
                            faceDiffs.Add(d);
                        }
                    }
                }
            }
        }

        var stockDiffs = new List<int>();
        foreach (Surface s in refGeo.Surfaces)
        {
            if ((uint)s.LightmapIndex >= (uint)refPages.Count)
            {
                continue;
            }

            Lightmap page = refPages[s.LightmapIndex];
            for (int y = 1; y + 1 < s.H; y++)
            {
                for (int x = 1; x + 1 < s.W; x++)
                {
                    int o = ((((s.Y + y) * page.Width) + s.X + x) * 3);
                    for (int c = 0; c < 3; c++)
                    {
                        stockDiffs.Add(Math.Abs(page.Pixels[o + c] - stockBefore[s.LightmapIndex][o + c]));
                    }
                }
            }
        }

        // Opt-in evidence: GED_ALPINE_LM_DUMP=<dir> writes each atlas page of both bakes side by side (Glacier left).
        if (Environment.GetEnvironmentVariable("GED_ALPINE_LM_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            for (int p = 0; p < Math.Min(pages, 8); p++)
            {
                var rgba = new byte[512 * 256 * 4];
                byte[] refPage = refLayer.AsSpan(p * AlpineLightmapLayout.RawPageBytes, AlpineLightmapLayout.RawPageBytes).ToArray();
                for (int y = 0; y < 256; y++)
                {
                    for (int x = 0; x < 512; x++)
                    {
                        int src = (((y * 256) + (x % 256)) * 3);
                        byte[] from = x < 256 ? bake.Pages[p] : refPage;
                        int dst = ((y * 512) + x) * 4;
                        rgba[dst] = from[src];
                        rgba[dst + 1] = from[src + 1];
                        rgba[dst + 2] = from[src + 2];
                        rgba[dst + 3] = 255;
                    }
                }

                File.WriteAllBytes(Path.Combine(dump, $"{Path.GetFileNameWithoutExtension(level)}-page{p}.png"),
                    Ged.Core.IO.Tex.PngWriter.Encode(512, 256, rgba));
            }
        }

        double meanChart = chartDiffs.Average();
        double p95Chart = Percentile(chartDiffs, 0.95);
        double meanStock = stockDiffs.Count > 0 ? stockDiffs.Average() : 0;
        _out.WriteLine(
            $"{level}: {bake.ChartedSurfaces} charts, {tiles.Length} tiles, {pages} pages at density {bake.Density} (identical to Alpine RED); " +
            $"chart texels {chartDiffs.Count / 3}: mean |d| {meanChart:0.00}, p95 {p95Chart}, max {chartDiffs.Max()}; " +
            $"on-face chart texels {faceDiffs.Count / 3}: mean |d| {(faceDiffs.Count > 0 ? faceDiffs.Average() : 0):0.00}, p95 {(faceDiffs.Count > 0 ? Percentile(faceDiffs, 0.95) : 0)}; " +
            $"stock fragments: mean |d| {meanStock:0.00}" + (sunNote is null ? string.Empty : "; " + sunNote));

        (double maxMean, double maxP95, double maxStock, double maxFace, double maxFaceP95) = Ratchets[level];
        double meanFace = faceDiffs.Count > 0 ? faceDiffs.Average() : 0;
        double p95Face = faceDiffs.Count > 0 ? Percentile(faceDiffs, 0.95) : 0;
        Assert.True(meanFace <= maxFace, $"on-face mean |d| {meanFace:0.00} > ratchet {maxFace}");
        Assert.True(p95Face <= maxFaceP95, $"on-face p95 |d| {p95Face} > ratchet {maxFaceP95}");
        Assert.True(meanChart <= maxMean, $"chart mean |d| {meanChart:0.00} > ratchet {maxMean}");
        Assert.True(p95Chart <= maxP95, $"chart p95 |d| {p95Chart} > ratchet {maxP95}");
        Assert.True(meanStock <= maxStock, $"stock mean |d| {meanStock:0.00} > ratchet {maxStock}");
    }

    // Even-odd point in polygon.
    private static bool Inside((float U, float V)[] poly, float x, float y)
    {
        bool inside = false;
        for (int a = 0, b = poly.Length - 1; a < poly.Length; b = a++)
        {
            if ((poly[a].V > y) != (poly[b].V > y)
                && x < ((poly[b].U - poly[a].U) * (y - poly[a].V) / (poly[b].V - poly[a].V)) + poly[a].U)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double Percentile(List<int> values, double q)
    {
        var counts = new int[256];
        foreach (int v in values)
        {
            counts[Math.Clamp(v, 0, 255)]++;
        }

        long target = (long)Math.Ceiling(q * values.Count);
        long run = 0;
        for (int i = 0; i < 256; i++)
        {
            run += counts[i];
            if (run >= target)
            {
                return i;
            }
        }

        return 255;
    }

    /// <summary>The newest raw-lightmap reference of <paramref name="level"/>, or null.</summary>
    internal static string? Reference(string level, string kind = "bake-raw")
    {
        if (TestPaths.ResearchFixtures is not { } fixtures)
        {
            return null;
        }

        string root = Path.Combine(fixtures, "alpine-ref");
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (string dir in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            string file = Path.Combine(dir, kind, level);
            if (File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }
}
