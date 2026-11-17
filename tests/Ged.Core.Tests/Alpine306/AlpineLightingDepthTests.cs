using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// Alpine lighting parity depth (ALPINE-COMPAT-SPEC C7): the BC7 codec and its RDO levels, D3D11-only saves, the
/// mover table, high-resolution stock pages from the level property, the fixed pipeline's occluder rules ("No shadow
/// cast", alpha-tested occlusion, invisible / alpha faces) and liquid emission.
/// </summary>
public sealed class AlpineLightingDepthTests
{
    private readonly ITestOutputHelper _out;

    public AlpineLightingDepthTests(ITestOutputHelper output) => _out = output;

    // ── BC7 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Bc7_Round_Trips_Smooth_And_Flat_Blocks_Closely()
    {
        var rng = new Random(3);
        Span<byte> block = stackalloc byte[16];
        Span<byte> rgba = stackalloc byte[64];
        var px = new byte[48];
        long total = 0;
        int worst = 0;
        for (int n = 0; n < 2000; n++)
        {
            // a lightmap-like block: a gradient plus a little noise, sometimes a hard edge
            int r0 = rng.Next(256), g0 = rng.Next(256), b0 = rng.Next(256);
            int dx = rng.Next(-12, 13), dy = rng.Next(-12, 13);
            bool edge = rng.Next(4) == 0;
            for (int i = 0; i < 16; i++)
            {
                int x = i % 4, y = i / 4;
                int shift = edge && x >= 2 ? 60 : 0;
                px[i * 3] = (byte)Math.Clamp(r0 + (dx * x) + (dy * y) - shift + rng.Next(-2, 3), 0, 255);
                px[(i * 3) + 1] = (byte)Math.Clamp(g0 + (dx * x) + (dy * y) - shift + rng.Next(-2, 3), 0, 255);
                px[(i * 3) + 2] = (byte)Math.Clamp(b0 + (dx * x) + (dy * y) - shift + rng.Next(-2, 3), 0, 255);
            }

            int err = Bc7Encoder.EncodeBlock(px, block);
            Assert.Equal(err, Bc7Encoder.BlockError(block, px));
            int mode = Bc7Decoder.ModeOf(block);
            Assert.True(mode is 1 or 6, $"mode {mode}");
            Bc7Decoder.DecodeBlock(block, rgba);
            for (int i = 0; i < 16; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(rgba[(i * 4) + c] - px[(i * 3) + c]);
                    total += d;
                    worst = Math.Max(worst, d);
                }
            }
        }

        double mean = total / (2000.0 * 48);
        _out.WriteLine($"BC7 round trip: mean |d| {mean:0.000}, max {worst}");
        Assert.True(mean < 1.5, $"mean {mean}");
        Assert.True(worst <= 24, $"max {worst}");

        // a flat block is exact
        Array.Fill(px, (byte)77);
        Assert.Equal(0, Bc7Encoder.EncodeBlock(px, block));
    }

    [Fact]
    public void Rdo_Levels_Shrink_The_Layer_And_Keep_Shared_Blocks()
    {
        // A smooth synthetic page: what the rate-distortion pass trades error for size on.
        var page = new byte[AlpineLightmapLayout.RawPageBytes];
        var rng = new Random(11);
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int o = ((y * 256) + x) * 3;
                page[o] = (byte)(60 + (x / 4) + rng.Next(0, 3));
                page[o + 1] = (byte)(50 + (y / 5) + rng.Next(0, 3));
                page[o + 2] = (byte)(40 + ((x + y) / 8));
            }
        }

        var shared = new bool[4096];
        shared[100] = true;
        (byte[][] quality, int q0) = AlpineLayerEncoder.Encode(new[] { page }, AlpineLightmapCodec.Bc7, AlpineLightmapCompression.Quality, shared);
        (byte[][] balanced, int q1) = AlpineLayerEncoder.Encode(new[] { page }, AlpineLightmapCodec.Bc7, AlpineLightmapCompression.Balanced, shared);
        (byte[][] compact, int q2) = AlpineLayerEncoder.Encode(new[] { page }, AlpineLightmapCodec.Bc7, AlpineLightmapCompression.Compact, shared);
        long s0 = Zlib(quality[0]), s1 = Zlib(balanced[0]), s2 = Zlib(compact[0]);
        double e0 = MeanError(quality[0], page), e1 = MeanError(balanced[0], page), e2 = MeanError(compact[0], page);
        _out.WriteLine($"quality {s0} B / {e0:0.000}; balanced {s1} B ({q1} blocks) / {e1:0.000}; compact {s2} B ({q2} blocks) / {e2:0.000}");
        Assert.Equal(0, q0);
        Assert.True(s1 < s0 && s2 <= s1, $"sizes {s0} {s1} {s2}");
        Assert.True(e1 < e0 + 1.0 && e2 < e0 + 2.0, $"errors {e0} {e1} {e2}");
        Assert.Equal(quality[0].AsSpan(100 * 16, 16).ToArray(), compact[0].AsSpan(100 * 16, 16).ToArray());

        // raw RGB8 stays available and is the page itself
        (byte[][] raw, _) = AlpineLayerEncoder.Encode(new[] { page }, AlpineLightmapCodec.RawRgb8, AlpineLightmapCompression.Compact, null);
        Assert.Same(page, raw[0]);
    }

    /// <summary>
    /// Alpine RED's own BC7 bakes decode to within a fraction of a level of its raw bakes of the same level (the decoder
    /// reads its partitions and modes right), and Glacier's BC7 of the same raw pages is within 25% of Alpine RED's
    /// layer size (C7 "byte size sanity").
    /// </summary>
    [Theory]
    [InlineData("glass_house.rfl")]
    [InlineData("dm14.rfl")]
    public void Bc7_Layers_Match_Alpine_REDs_In_Decode_And_Size(string level)
    {
        if (AlpineLightmapParityTests.Reference(level, "bake") is not { } bc7Path || AlpineLightmapParityTests.Reference(level) is not { } rawPath)
        {
            return;
        }

        (byte[] bcLayer, AlpineLightmapReadResult br) = Layer(bc7Path);
        (byte[] rawLayer, AlpineLightmapReadResult rr) = Layer(rawPath);
        Assert.Equal(br.PageCount, rr.PageCount);
        long alpineDiff = 0, glacierBytes = 0;
        var glacierPages = new List<byte[]>();
        for (int p = 0; p < br.PageCount; p++)
        {
            byte[] rawPage = rawLayer.AsSpan(p * AlpineLightmapLayout.RawPageBytes, AlpineLightmapLayout.RawPageBytes).ToArray();
            byte[] decoded = Bc7Decoder.DecodePageRgb(bcLayer.AsSpan(p * AlpineLightmapLayout.Bc7PageBytes, AlpineLightmapLayout.Bc7PageBytes), 256);
            for (int k = 0; k < rawPage.Length; k++)
            {
                alpineDiff += Math.Abs(decoded[k] - rawPage[k]);
            }

            glacierPages.Add(rawPage);
        }

        (byte[][] mine, _) = AlpineLayerEncoder.Encode(glacierPages, AlpineLightmapCodec.Bc7, AlpineLightmapCompression.Quality, null);
        glacierBytes = Zlib(mine.SelectMany(b => b).ToArray());
        double alpineMean = alpineDiff / (double)rawLayer.Length;
        double ratio = glacierBytes / (double)br.LayerStoredSize;
        _out.WriteLine($"{level}: Alpine RED BC7 vs raw mean |d| {alpineMean:0.000}; Glacier BC7 {glacierBytes} B vs Alpine RED {br.LayerStoredSize} B (×{ratio:0.000})");
        Assert.True(alpineMean < 0.25, $"decoded Alpine RED BC7 differs from its raw bake by {alpineMean}");
        Assert.InRange(ratio, 0.75, 1.25);
    }

    /// <summary>
    /// Glacier relighting the geometry of an Alpine RED bake gets Alpine RED's atlas and texels to within a ratchet:
    /// <c>c5_objects</c> is the owner's single-room check level (one 60×20×60 room, a range-10 light under the ceiling, a
    /// boxed Directional Light the ceiling blocks) as Alpine RED baked it to BC7 — walls, floor and ceiling at the ambient
    /// floor, the ceiling's lit spot close to Alpine RED's; <c>c7_liquid</c> is a lava pool whose Liquid Room effect emits
    /// light (Up And Down), Alpine RED's raw bake — the liquid emission's glow measured against Alpine's.
    /// </summary>
    [Theory]
    [InlineData("c5_objects.rfl", "bake", 0.05, 2)] // measured 2026-11-15: mean 0.011 / p99 1 (max 2) against the decoded BC7
    [InlineData("c7_liquid.rfl", "bake-raw", 0.6, 2)] // measured 2026-11-15: mean 0.504 / p99 1 (max 20)
    public void Relighting_An_Alpine_RED_Bake_Matches_Its_Texels(string level, string kind, double maxMean, int maxP99)
    {
        if (AlpineLightmapParityTests.Reference(level, kind) is not { } path)
        {
            return;
        }

        RflFile reference = RflFile.Load(path);
        reference.ParseAllKnownSections();
        AlpineLightmapAtlas refAtlas = Assert.IsType<AlpineLightmapAtlas>(AlpineLightmapAtlas.FromLevel(reference));
        List<Light> lights = reference.Sections.Where(s => s.TypeId == (uint)SectionType.Lights).Select(s => s.Content)
            .OfType<LightsSection>().SelectMany(l => l.Lights).ToList();
        RfColor? ambient = reference.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().FirstOrDefault()?.AmbientColor;
        var options = new LightingOptions { AlpineCharts = AlpineLightmapSettings.FromLevel(reference) with { Codec = AlpineLightmapCodec.RawRgb8 } };
        CompileOptionsFactory.ResolveAlpineLighting(options, reference, vfs: null, warnings: null);
        BakeStats stats = GeometryBuildService.Relight(reference, lights, ambient, options)!;
        AlpineLightmapBake bake = stats.AlpineLightmaps!;
        Assert.Equal(refAtlas.Tiles, bake.Tiles);
        Assert.Equal(refAtlas.Pages.Length, bake.Pages.Length);

        var diffs = new List<int>();
        Geometry g = reference.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        for (int i = 0; i < g.Surfaces.Count; i++)
        {
            byte[]? mine = AlpineLightmapWriterTests.Decode(bake, bake.Charts[i]) is { Length: > 0 } m && !bake.Charts[i].Geometry.IsEmpty ? m : null;
            byte[]? theirs = refAtlas.ChartImage(i);
            if (mine is null || theirs is null)
            {
                continue;
            }

            for (int k = 0; k < mine.Length; k++)
            {
                diffs.Add(Math.Abs(mine[k] - theirs[k]));
            }
        }

        double mean = diffs.Average();
        int p99 = diffs.OrderBy(d => d).ElementAt((int)(diffs.Count * 0.99));
        _out.WriteLine($"{level}: {diffs.Count / 3} chart texels, mean |d| {mean:0.000}, p99 {p99}, max {diffs.Max()}; liquid emitters {stats.LiquidEmitters}");
        Assert.True(mean <= maxMean, $"mean {mean}");
        Assert.True(p99 <= maxP99, $"p99 {p99}");
    }
    // ── D3D11-only, high resolution, movers ──────────────────────────────────

    [Fact]
    public void D3D11_Only_Omits_The_Stock_Section_And_Writes_Index_Minus_One()
    {
        EditorDocument doc = AlpineLightmapWriterTests.LitRoom();
        Props(doc.Rfl).D3D11OnlyLightmaps = AlpineLevelPropertiesSection.D3D11OnlySetting;
        GeometryBuildService.BuildAndApply(doc.Rfl, AlpineLightmapWriterTests.Options(doc));
        byte[] bytes = AlpineLightmapWriterTests.SaveAndRead(doc, out IReadOnlyList<string> notes);
        Assert.Contains(notes, n => n.Contains("D3D11-only lightmaps: the stock lightmaps are not saved", StringComparison.Ordinal));

        RflFile saved = RflFile.Load(bytes);
        saved.ParseAllKnownSections();
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.Lightmaps);
        Geometry g = saved.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        Assert.NotEmpty(g.Surfaces);
        Assert.All(g.Surfaces, s => Assert.Equal(-1, s.LightmapIndex));
        AlpineLevelPropertiesSection props = Props(saved);
        Assert.Equal(AlpineLevelPropertiesSection.D3D11OnlySetting | AlpineLevelPropertiesSection.D3D11OnlyStockOmitted, (int)props.D3D11OnlyLightmaps);
        Assert.NotEqual(0, props.RequireD3D11 & AlpineLevelPropertiesSection.RequireD3D11Needed);

        // The game's reader accepts the section against the geometry as written (index −1 in the fingerprint)…
        byte[] body = saved.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(saved.Context);
        AlpineLightmapReadResult read = AlpineLightmapValidator.Read(body, g);
        Assert.True(read.Ok && read.SurfacesOk, read.Reason + read.SurfaceReason);

        // …the document keeps its stock lightmaps, the viewport draws the charts of the reloaded level, and a re-save of
        // the reloaded level is byte-stable.
        Assert.Contains(doc.Rfl.Sections, s => s.TypeId == (uint)SectionType.Lightmaps);
        Assert.NotNull(AlpineLightmapAtlas.FromLevel(saved));
        byte[] once = saved.Save();
        saved.UpgradeToAlpine();
        Assert.Equal(once, saved.Save());
    }

    [Fact]
    public void Without_Charts_D3D11_Only_Keeps_The_Stock_Section()
    {
        EditorDocument doc = AlpineLightmapWriterTests.LitRoom();
        Props(doc.Rfl).D3D11OnlyLightmaps = AlpineLevelPropertiesSection.D3D11OnlySetting;
        AlpineLightmapSettings.Apply(doc.Rfl, LightmapPipeline.Legacy, null);
        GeometryBuildService.BuildAndApply(doc.Rfl, AlpineLightmapWriterTests.Options(doc));
        RflFile saved = RflFile.Load(AlpineLightmapWriterTests.SaveAndRead(doc, out _));
        Assert.Contains(saved.Sections, s => s.TypeId == (uint)SectionType.Lightmaps);
        Assert.Equal(AlpineLevelPropertiesSection.D3D11OnlySetting, (int)Props(saved).D3D11OnlyLightmaps);
    }

    [Fact]
    public void HighRes_Property_Bakes_256_Pages_With_254_Texel_Fragments()
    {
        EditorDocument doc = AlpineLightmapWriterTests.LitRoom();
        Props(doc.Rfl).HighresLightmaps = 1;
        CompiledLevel built = GeometryBuildService.Build(doc.Rfl, AlpineLightmapWriterTests.Options(doc));
        Assert.All(built.Lightmaps, p => Assert.Equal(256, p.Width));
        Assert.All(built.Geometry.Surfaces, s => Assert.True(s.W <= 254 && s.H <= 254));
        AlpineLightmapBake bake = built.BakeStats!.AlpineLightmaps!;
        Assert.Equal(256, bake.StockPage);
    }

    [Fact]
    public void A_Mover_Gets_A_Mover_Table_Record_The_Reader_Accepts()
    {
        EditorDocument doc = Scripting.KitTestHarness.Run(
            "kit.room{ name = 'hall', pos = {0, 0, 0}, size = {16, 8, 16} }\n" +
            "level.place('light', 2, 5, 2)\n" +
            "local door = level.brush{ pos = {0, 1.5, 4}, size = {3, 3, 0.5}, detail = true }\n" +
            "level.create_mover{ name = 'door', brushes = { door } }").Doc;
        CompiledLevel built = GeometryBuildService.BuildAndApply(doc.Rfl, AlpineLightmapWriterTests.Options(doc));
        AlpineLightmapBake bake = built.BakeStats!.AlpineLightmaps!;
        AlpineMoverCharts mover = Assert.Single(bake.Movers);
        Assert.True(mover.ChartCount > 0);
        Assert.True(bake.MoverDensity > 0);

        RflFile saved = RflFile.Load(AlpineLightmapWriterTests.SaveAndRead(doc, out IReadOnlyList<string> notes));
        saved.ParseAllKnownSections();
        Assert.Contains(notes, n => n.Contains("charts on 1 movers", StringComparison.Ordinal));
        Geometry g = saved.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        byte[] body = saved.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(saved.Context);
        AlpineLightmapReadResult read = AlpineLightmapValidator.Read(body, g);
        Assert.True(read.Ok && read.SurfacesOk, read.Reason + read.SurfaceReason);
        Assert.Equal(new[] { AlpineLightmapWriter.MoverTableTag }, read.TableTags.ToArray());

        // The record's fingerprint is the XXH32 of the mover's surface records as the movers section wrote them.
        Brush savedMover = saved.Sections.Select(s => s.Content).OfType<MoversSection>().Single().Movers.Single(m => m.Uid == mover.Uid);
        int tableBody = AlpineLightmapWriter.HeaderSize + (g.Surfaces.Count * 4) + 4 + 16;
        Assert.Equal(mover.Uid, BitConverter.ToInt32(body, tableBody + 4));
        Assert.Equal((uint)savedMover.Geometry.Surfaces.Count, BitConverter.ToUInt32(body, tableBody + 8));
        Assert.Equal(AlpineLightmapWriter.SurfaceHash(savedMover.Geometry), BitConverter.ToUInt32(body, tableBody + 12));
    }

    /// <summary>
    /// A stock level with RED-authored movers (dmabrupt: Alpine RED's bake of it charts 6 movers): Glacier's lit build
    /// writes a mover table for them that the reader accepts, each record fingerprinted with the movers section as saved.
    /// </summary>
    [Fact]
    [Trait("Category", "DeepGate")]
    public void Stock_Movers_Get_A_Mover_Table()
    {
        if (TestPaths.Research is not { } research || Path.Combine(research, "example_rfls", "dmabruptdecayrc2a27.rfl") is not { } file
            || !File.Exists(file))
        {
            return;
        }

        var doc = new EditorDocument(RflFile.Load(file), "dmabrupt.rfl");
        CompiledLevel built = GeometryBuildService.BuildAndApply(doc.Rfl, AlpineLightmapWriterTests.Options(doc));
        AlpineLightmapBake bake = built.BakeStats!.AlpineLightmaps!;
        _out.WriteLine($"dmabrupt: {bake.Movers.Count} movers, {bake.Movers.Sum(m => m.Charts.Length)} mover surfaces, {bake.Movers.Sum(m => m.ChartCount)} charted, at density {bake.MoverDensity}; {bake.Pages.Length} pages");
        Assert.Equal(6, bake.Movers.Count); // as Alpine RED's own bake of the level
        RflFile saved = RflFile.Load(AlpineLightmapWriterTests.SaveAndRead(doc, out _));
        saved.ParseAllKnownSections();
        Geometry g = saved.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        byte[] body = saved.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(saved.Context);
        AlpineLightmapReadResult read = AlpineLightmapValidator.Read(body, g);
        Assert.True(read.Ok && read.SurfacesOk, read.Reason + read.SurfaceReason);
        List<Brush> movers = saved.Sections.Select(s => s.Content).OfType<MoversSection>().Single().Movers;
        foreach (AlpineMoverCharts m in bake.Movers)
        {
            Brush mover = movers.Single(b => b.Uid == m.Uid);
            Assert.Equal(AlpineLightmapWriter.SurfaceHash(mover.Geometry), BitConverter.ToUInt32(body, m.HashOffset));
        }
    }

    // ── Occluder rules and liquid emission ───────────────────────────────────

    [Fact]
    public void No_Shadow_Cast_Brushes_Leave_The_Occluder_Set()
    {
        const string script =
            "kit.room{ name = 'hall', pos = {0, 0, 0}, size = {16, 8, 16} }\n" +
            "level.place('light', 0, 6, 0)\n" +
            "local slab = level.brush{ pos = {0, 3, 0}, size = {6, 0.5, 6}, detail = true }\n" +
            "return slab.uid";
        Scripting.KitTestHarness.KitRun run = Scripting.KitTestHarness.Run(script);
        int slab = int.Parse(run.Result.ReturnValue!, System.Globalization.CultureInfo.InvariantCulture);
        double shadowed = FloorBrightness(GeometryBuildService.Build(run.Doc.Rfl, AlpineLightmapWriterTests.Options(run.Doc)));
        Props(run.Doc.Rfl).NoShadowCastBrushUids.Add(slab);
        CompiledLevel open = GeometryBuildService.Build(run.Doc.Rfl, AlpineLightmapWriterTests.Options(run.Doc));
        double lit = FloorBrightness(open);
        _out.WriteLine($"floor under the slab: {shadowed:0.0} shadowed, {lit:0.0} with No shadow cast");
        Assert.True(lit > shadowed + 5, $"{lit} vs {shadowed}");
        Assert.Contains(open.Report.Messages, m => m.Text.Contains("\"No shadow cast\" brushes resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Liquid_Emission_Lights_What_Is_Above_The_Surface()
    {
        EditorDocument doc = AlpineLightmapWriterTests.LitRoom(withLight: false);
        RflSection host = doc.Rfl.GetOrCreateSection(SectionType.RoomEffects, () => new RoomEffectsSection());
        var effects = (RoomEffectsSection)host.Content!;
        effects.Effects.Add(new RoomEffect
        {
            EffectType = RoomEffectsSection.EffectLiquidRoom,
            LiquidProperties = new RoomEffectLiquidProperties { Waveform = 0, Depth = 2f, SurfaceTexture = "water.tga", LiquidType = 1, Visibility = 8f },
            Header = new ObjectHeader { Uid = 7001, ClassName = "Room Effect", ScriptName = "Room Effect", Position = new Vec3(0, 1, 0) },
        });
        host.Dirty = true;
        double dark = Brightness(GeometryBuildService.Build(doc.Rfl, AlpineLightmapWriterTests.Options(doc)));

        RflSection le = doc.Rfl.GetOrCreateSection(SectionType.AlpineLiquidEmission, () => new AlpineLiquidEmissionSection());
        ((AlpineLiquidEmissionSection)le.Content!).Records.Add(new AlpineLiquidEmission { RoomEffectUid = 7001, Mode = 1, Intensity = 2f, Distance = 10f });
        le.Dirty = true;
        CompiledLevel glow = GeometryBuildService.Build(doc.Rfl, AlpineLightmapWriterTests.Options(doc));
        double lit = Brightness(glow);
        _out.WriteLine($"mean texel {dark:0.00} without emission, {lit:0.00} with");
        Assert.Equal(1, glow.BakeStats!.LiquidEmitters);
        Assert.True(lit > dark + 1.0, $"{lit} vs {dark}");
    }

    [Fact]
    public void Liquid_Surface_Form_Factor_And_Falloff_Follow_Alpine()
    {
        var quad = new Vec3[] { new(-50, 0, -50), new(-50, 0, 50), new(50, 0, 50), new(50, 0, -50) };
        AlpineLiquidSurface s = AlpineLiquidSurface.Build(new[] { ((IReadOnlyList<Vec3>)quad, new Vec3(0, 1, 0)) })!;
        Assert.Equal(0f, s.Height);
        Assert.True(s.Contains(0, 0) && !s.Contains(60, 0));
        Assert.InRange(s.FormFactor(new Vec3(0, 1, 0), new Vec3(0, -1, 0)), 0.98f, 1f); // facing a near-endless surface
        Assert.Equal(0f, s.FormFactor(new Vec3(0, 1, 0), new Vec3(0, 1, 0))); // facing away
        var e = new AlpineLiquidEmitter { Surface = s, Color = new Vec3(1, 0.5f, 0.25f), Reach = 8f, Below = false };
        Assert.Equal(0.75f, e.Falloff(new Vec3(0, 2, 0)), 4);
        Assert.Equal(0f, e.Falloff(new Vec3(0, -2, 0))); // "up" lights nothing below
        Assert.Equal(255, e.Visibility(null, new Vec3(0, 2, 0), new Vec3(0, -1, 0), -1, 0.1f, 0, 0));
    }

    [Fact]
    public void Occluder_Rules_Follow_Each_Lights_Flags()
    {
        // One wall between a receiver at z = 0 and a light at z = 10, of each face class.
        static AlpineOccluders Wall(ushort flags, Vec3 normal) => AlpineOccluders.Build(
            new[] { new AlpineOccluderFace(new Vec3[] { new(-5, -5, 5), new(5, -5, 5), new(5, 5, 5), new(-5, 5, 5) }, null, flags, "wall.tga", 3, normal, false) },
            null, alphaTested: false, textures: null);
        var light = new EngineLight { Type = EngineLightType.Point, Position = new Vec3(0, 0, 10), Position2 = new Vec3(0, 0, 10), Range = 20, Enabled = true };
        var origin = new Vec3(0, 0, 0.02f);
        var ns = new Vec3(0, 0, 1);
        var stock = new AlpineLightmapSettings();
        (uint skip, uint one) = AlpineOccluders.RayFlags(light, stock);

        Assert.Equal(0, Wall(0, ns).PointMask(light, origin, ns, -1, 0.1f, skip, one));
        Assert.Equal(255, Wall(0, ns).PointMask(light, origin, ns, 3, 0.1f, skip, one)); // the receiver's own surface
        Assert.Equal(255, Wall((ushort)FaceFlags.LiquidSurface, ns).PointMask(light, origin, ns, -1, 0.1f, skip, one));
        Assert.Equal(255, Wall((ushort)FaceFlags.IsInvisible, ns).PointMask(light, origin, ns, -1, 0.1f, skip, one));
        Assert.Equal(0, Wall((ushort)FaceFlags.ShowSky, ns).PointMask(light, origin, ns, -1, 0.1f, skip, one)); // sky blocks point lights

        // Invisible faces occlude one-sided: light arriving on the front (the side the light is on) is stopped.
        var inv = stock with { InvisibleFacesOcclude = true };
        (uint s2, uint o2) = AlpineOccluders.RayFlags(light, inv);
        Assert.Equal(0, Wall((ushort)FaceFlags.IsInvisible, new Vec3(0, 0, 1)).PointMask(light, origin, ns, -1, 0.1f, s2, o2));
        Assert.Equal(255, Wall((ushort)FaceFlags.IsInvisible, new Vec3(0, 0, -1)).PointMask(light, origin, ns, -1, 0.1f, s2, o2));

        // Alpha-tested: a half-transparent see-through face with holes passes about half the light.
        var mask = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            mask[(i * 4) + 3] = (byte)((i % 2 == 0) ? 255 : 0);
        }

        AlpineAlphaMask am = AlpineAlphaMask.FromRgba(8, 8, mask)!;
        AlpineOccluders grate = AlpineOccluders.Build(
            new[]
            {
                new AlpineOccluderFace(new Vec3[] { new(-5, -5, 5), new(5, -5, 5), new(5, 5, 5), new(-5, 5, 5) },
                    new Uv[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) }, (ushort)(FaceFlags.HasAlpha | FaceFlags.HasHoles), "grate.tga", -1, ns, false),
            },
            null, alphaTested: true, textures: new FixedAlpha(am));
        Assert.Equal(2, grate.AlphaTestedTriangles); // the quad's two triangles
        byte half = grate.PointMask(light, origin, ns, -1, 2f, skip, one);
        Assert.InRange(half, 100, 155);
        Assert.Equal(0, AlpineOccluders.Build(
            new[] { new AlpineOccluderFace(new Vec3[] { new(-5, -5, 5), new(5, -5, 5), new(5, 5, 5) }, null, (ushort)(FaceFlags.HasAlpha | FaceFlags.HasHoles), "grate.tga", -1, ns, false) },
            null, alphaTested: false, textures: null).TriangleCount); // without alpha-tested occlusion a see-through face never occludes
    }

    private sealed class FixedAlpha : IAlpineTextureAlpha
    {
        private readonly AlpineAlphaMask _mask;

        public FixedAlpha(AlpineAlphaMask mask) => _mask = mask;

        public bool HasAlpha(string texture) => true;

        public AlpineAlphaMask? Mask(string texture) => _mask;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AlpineLevelPropertiesSection Props(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
    }

    private static double Brightness(CompiledLevel c) => c.Lightmaps.SelectMany(p => p.Pixels).Average(b => (double)b);

    /// <summary>The mean stock texel of the upward-facing surfaces at floor height under the room centre.</summary>
    private static double FloorBrightness(CompiledLevel c)
    {
        long sum = 0, n = 0;
        foreach (Surface s in c.Geometry.Surfaces)
        {
            if (s.Plane.Normal.Y < 0.9f || s.BoundingBox.P2.Y > 0.5f || (uint)s.LightmapIndex >= (uint)c.Lightmaps.Count)
            {
                continue;
            }

            Lightmap page = c.Lightmaps[s.LightmapIndex];
            var mapper = new SurfaceTexelMapper(s, page.Width, page.Height);
            for (int y = 1; y + 1 < s.H; y++)
            {
                for (int x = 1; x + 1 < s.W; x++)
                {
                    Vec3 w = mapper.World(x, y);
                    if (MathF.Abs(w.X) > 2f || MathF.Abs(w.Z) > 2f)
                    {
                        continue;
                    }

                    int o = ((((s.Y + y) * page.Width) + s.X + x) * 3);
                    sum += page.Pixels[o] + page.Pixels[o + 1] + page.Pixels[o + 2];
                    n += 3;
                }
            }
        }

        return n == 0 ? 0 : sum / (double)n;
    }

    private static (byte[], AlpineLightmapReadResult) Layer(string path)
    {
        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry g = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        byte[] body = rfl.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLightmaps).GetBodyBytes(rfl.Context);
        AlpineLightmapReadResult r = AlpineLightmapValidator.Read(body, g);
        return (AlpineLightmapValidator.DecodeLayer(body, r)!, r);
    }

    private static long Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.Length;
    }

    private static double MeanError(byte[] blocks, byte[] page)
    {
        byte[] decoded = Bc7Decoder.DecodePageRgb(blocks, 256);
        long d = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            d += Math.Abs(decoded[i] - page[i]);
        }

        return d / (double)decoded.Length;
    }
}
