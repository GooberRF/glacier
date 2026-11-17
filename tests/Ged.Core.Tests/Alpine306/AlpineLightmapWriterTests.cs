using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Lighting.Alpine;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// Glacier writing Alpine Faction's lightmap chunk (ALPINE-COMPAT-SPEC C6): the shared derivation, the packer, the
/// section writer and fingerprints, the reader-parity validator, the pipeline switch, and the save rule.
/// </summary>
public sealed class AlpineLightmapWriterTests
{
    [Theory]
    [InlineData("", 0x02CC5D05u)]
    [InlineData("a", 0x550D7456u)]
    [InlineData("abc", 0x32D153FFu)]
    [InlineData("Nobody inspects the spammish repetition", 0xE2293B2Fu)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0xE85EA4DEu)]
    public void Xxh32_Matches_The_Published_Test_Vectors(string text, uint expected) =>
        Assert.Equal(expected, Xxh32.Hash(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Chart_Geometry_Follows_The_Shared_Derivation()
    {
        // alpine_lightmap.h:342-373: one tile keeps a one-texel ring, a tiled axis the 4-texel gutter.
        AlpineChartGeometry small = AlpineLightmapLayout.SurfaceChart(10, 6, 4, 3);
        Assert.Equal(new AlpineChartGeometry(32, 12, 1, 1, 1, 1), small);
        Assert.Equal(new AlpineTileDims(32, 12, 36, 16), AlpineLightmapLayout.TileDims(small, 0, 0));

        AlpineChartGeometry big = AlpineLightmapLayout.SurfaceChart(64, 10, 8, 1);
        Assert.Equal(new AlpineChartGeometry(496, 8, 3, 1, 4, 1), big);
        Assert.Equal(new AlpineTileDims(244, 8, 252, 12), AlpineLightmapLayout.TileDims(big, 0, 0));
        Assert.Equal(new AlpineTileDims(8, 8, 16, 12), AlpineLightmapLayout.TileDims(big, 2, 0));
        Assert.Equal((240, -1), AlpineLightmapLayout.TileOrigin(big, 1, 0));

        Assert.True(AlpineLightmapLayout.SurfaceChart(2, 10, 4, 4).IsEmpty);
        Assert.True(AlpineLightmapLayout.SurfaceChart(10, 10, 0, 4).IsEmpty);

        // k = lround(density / ((dim - 2) / extent)), clamped to 1..65535 (alpine_lightmap.h:734-745).
        Assert.Equal(8, AlpineLightmapLayout.KFromDensity(10, 8f, 8f));
        Assert.Equal(3, AlpineLightmapLayout.KFromDensity(10, 2.5f, 8f)); // 2.5 rounds away from zero
        Assert.Equal(1, AlpineLightmapLayout.KFromDensity(10, 0.01f, 8f));
        Assert.Equal(1, AlpineLightmapLayout.KFromDensity(2, 8f, 8f));
    }

    [Fact]
    public void The_Packer_Is_Deterministic_And_Keeps_Tiles_On_Their_Pages()
    {
        var charts = new List<AlpineChartGeometry>();
        var rng = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            charts.Add(AlpineLightmapLayout.SurfaceChart(rng.Next(3, 60), rng.Next(3, 60), rng.Next(1, 9), rng.Next(1, 9)));
        }

        Assert.Equal(AlpinePackResult.Ok, AlpineAtlasPacker.Pack(charts, 1024, out int[] bases, out AlpineTile[] tiles, out int pages));
        Assert.Equal(AlpinePackResult.Ok, AlpineAtlasPacker.Pack(charts, 1024, out _, out AlpineTile[] again, out int pages2));
        Assert.Equal(tiles, again);
        Assert.Equal(pages, pages2);

        // Every tile on a page, and no two stored rects overlap.
        var used = new HashSet<(int, int, int)>();
        for (int c = 0; c < charts.Count; c++)
        {
            AlpineChartGeometry g = charts[c];
            for (int ty = 0; ty < g.Ny; ty++)
            {
                for (int tx = 0; tx < g.Nx; tx++)
                {
                    AlpineTile t = tiles[AlpineLightmapLayout.TileIndex(bases[c], g, tx, ty)];
                    AlpineTileDims td = AlpineLightmapLayout.TileDims(g, tx, ty);
                    Assert.InRange(t.Page, 0, pages - 1);
                    Assert.True(t.X + td.StoredW <= 256 && t.Y + td.StoredH <= 256);
                    for (int y = 0; y < td.StoredH; y += 4)
                    {
                        for (int x = 0; x < td.StoredW; x += 4)
                        {
                            Assert.True(used.Add((t.Page, t.X + x, t.Y + y)), "overlapping tiles");
                        }
                    }
                }
            }
        }

        Assert.Equal(AlpinePackResult.OverBudget, AlpineAtlasPacker.Pack(charts, 1, out _, out _, out _));
        Assert.Equal(AlpinePackResult.NoCharts, AlpineAtlasPacker.Pack(new AlpineChartGeometry[3], 1024, out _, out _, out _));
    }

    [Fact]
    public void An_Alpine_Bake_Writes_A_Section_The_Reader_Accepts_And_Stock_Fragments_Are_Its_Box_Means()
    {
        EditorDocument doc = LitRoom();
        CompiledLevel built = GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc));
        AlpineLightmapBake bake = Assert.IsType<AlpineLightmapBake>(built.BakeStats!.AlpineLightmaps);
        Assert.Same(built.Geometry, bake.Geometry);
        Assert.Equal(LightmapPipeline.Alpine, built.BakeStats.Pipeline);
        Assert.True(bake.ChartedSurfaces > 0);
        Assert.Equal(8, bake.Density);

        // The stock interior is the rounded box mean of the chart texels over it (alpine_lightmaps.cpp:797-835).
        int checkedSurfaces = 0;
        for (int i = 0; i < built.Geometry.Surfaces.Count; i++)
        {
            AlpineSurfaceChart c = bake.Charts[i];
            if (c.Geometry.IsEmpty)
            {
                continue;
            }

            Surface s = built.Geometry.Surfaces[i];
            Lightmap page = built.Lightmaps[s.LightmapIndex];
            byte[] chart = Decode(bake, c);
            for (int j = 0; j + 2 < s.H; j++)
            {
                for (int k = 0; k + 2 < s.W; k++)
                {
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int sum = 0;
                        for (int dv = 0; dv < c.KV; dv++)
                        {
                            for (int du = 0; du < c.KU; du++)
                            {
                                sum += chart[((((j * c.KV) + dv) * c.Geometry.Cw) + (k * c.KU) + du) * 3 + ch];
                            }
                        }

                        int n = c.KU * c.KV;
                        Assert.Equal((sum + (n / 2)) / n, page.Pixels[((((s.Y + 1 + j) * page.Width) + s.X + 1 + k) * 3) + ch]);
                    }
                }
            }

            checkedSurfaces++;
        }

        Assert.True(checkedSurfaces > 0);

        // Saved: one chunk at the "after movers" slot, fingerprinted for the surfaces written, accepted by the reader.
        RflFile saved = RflFile.Load(SaveAndRead(doc, out IReadOnlyList<string> notes));
        saved.ParseAllKnownSections();
        Assert.Contains(notes, n => n.StartsWith("Wrote the level's Alpine lightmaps", StringComparison.Ordinal));
        RflSection chunk = Assert.Single(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        Geometry geometry = saved.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        byte[] body = chunk.GetBodyBytes(saved.Context);
        AlpineLightmapReadResult read = AlpineLightmapValidator.Read(body, geometry);
        Assert.True(read.Ok, read.Reason);
        Assert.True(read.SurfacesOk, read.SurfaceReason);
        Assert.Equal(1, read.LayerCodec); // BC7, Alpine RED's shipping codec
        Assert.Equal(1, read.LayerCompression); // zlib
        Assert.Equal((uint)geometry.Faces.Count, read.SourceFaces);
        Assert.Equal(AlpineLightmapWriter.SurfaceHash(geometry), read.SourceHash);

        // The fingerprint covers exactly the surface records the geometry section ends with.
        byte[] geo = saved.Sections.Single(s => s.TypeId == (uint)SectionType.StaticGeometry).GetBodyBytes(saved.Context);
        byte[] records = AlpineLightmapWriter.SurfaceRecords(geometry);
        Assert.Equal(records, geo[^records.Length..]);

        // The decoded layer is the bake's pages, BC7-encoded: exactly the BC7 size, and within a few levels of every texel.
        byte[] layer = AlpineLightmapValidator.DecodeLayer(body, read)!;
        Assert.Equal(bake.Pages.Length * AlpineLightmapLayout.Bc7PageBytes, layer.Length);
        long diff = 0;
        int worst = 0;
        for (int p = 0; p < bake.Pages.Length; p++)
        {
            byte[] decoded = Bc7Decoder.DecodePageRgb(layer.AsSpan(p * AlpineLightmapLayout.Bc7PageBytes, AlpineLightmapLayout.Bc7PageBytes), 256);
            for (int k = 0; k < decoded.Length; k++)
            {
                int d = Math.Abs(decoded[k] - bake.Pages[p][k]);
                diff += d;
                worst = Math.Max(worst, d);
            }
        }

        Assert.True(diff / (double)(bake.Pages.Length * AlpineLightmapLayout.RawPageBytes) < 0.5, $"mean BC7 error {diff / (double)(bake.Pages.Length * AlpineLightmapLayout.RawPageBytes):0.000}");
        Assert.True(worst <= 24, $"max BC7 error {worst}");
        Assert.True(new AlpineLightmapsSection().Type == SectionType.AlpineLightmaps);
        Assert.True(((AlpineLightmapsSection)chunk.Content!).IsUsable);

        // Order: after 0x2000's slot, before 0x3000 (RflSectionOrder); the second save is byte-stable.
        List<uint> ids = saved.Sections.Select(s => s.TypeId).ToList();
        Assert.True(ids.IndexOf((uint)SectionType.AlpineLightmaps) > ids.IndexOf((uint)SectionType.StaticGeometry));
        byte[] once = saved.Save();
        Assert.Empty(saved.UpgradeToAlpine());
        Assert.Equal(once, saved.Save());
    }

    [Fact]
    public void Legacy_Lighting_Bakes_Exactly_The_Stock_Pages_And_Writes_No_Chunk()
    {
        EditorDocument doc = LitRoom();
        Assert.True(AlpineLightmapSettings.Apply(doc.Rfl, LightmapPipeline.Legacy, null));
        CompiledLevel legacy = GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc));
        Assert.Null(legacy.BakeStats!.AlpineLightmaps);
        Assert.Equal(LightmapPipeline.Legacy, legacy.BakeStats.Pipeline);

        // The same compile with no Alpine request at all (every parity gate's path): byte-identical pages.
        CompileOptions raw = Options(doc);
        raw.Lighting.AlpineCharts = null;
        CompiledLevel stock = GeometryBuildService.Build(doc.Rfl, raw);
        Assert.Equal(stock.Lightmaps.Count, legacy.Lightmaps.Count);
        for (int i = 0; i < stock.Lightmaps.Count; i++)
        {
            Assert.Equal(stock.Lightmaps[i].Pixels, legacy.Lightmaps[i].Pixels);
        }

        RflFile saved = RflFile.Load(SaveAndRead(doc, out _));
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
    }

    [Fact]
    public void At_k_1_The_Charts_Are_The_Stock_Fragments_And_The_Stock_Pages_Do_Not_Change()
    {
        EditorDocument doc = LitRoom();
        Assert.True(AlpineLightmapSettings.Apply(doc.Rfl, LightmapPipeline.Alpine, 1));
        CompiledLevel alpine = GeometryBuildService.Build(doc.Rfl, Options(doc));
        CompileOptions raw = Options(doc);
        raw.Lighting.AlpineCharts = null;
        CompiledLevel stock = GeometryBuildService.Build(doc.Rfl, raw);
        AlpineLightmapBake bake = alpine.BakeStats!.AlpineLightmaps!;

        int single = 0;
        for (int i = 0; i < alpine.Geometry.Surfaces.Count; i++)
        {
            AlpineSurfaceChart c = bake.Charts[i];
            if (c.Geometry.IsEmpty || c.KU != 1 || c.KV != 1 || c.Geometry.TileCount != 1)
            {
                continue;
            }

            single++;
            Surface s = alpine.Geometry.Surfaces[i];
            Lightmap page = stock.Lightmaps[s.LightmapIndex];
            byte[] chart = Decode(bake, c);
            for (int y = 0; y < c.Geometry.Ch; y++)
            {
                for (int x = 0; x < c.Geometry.Cw; x++)
                {
                    int src = ((((s.Y + 1 + y) * page.Width) + s.X + 1 + x) * 3);
                    int dst = ((y * c.Geometry.Cw) + x) * 3;
                    Assert.Equal(page.Pixels.AsSpan(src, 3).ToArray(), chart.AsSpan(dst, 3).ToArray());
                }
            }
        }

        Assert.True(single > 0);
        for (int i = 0; i < stock.Lightmaps.Count; i++)
        {
            Assert.Equal(stock.Lightmaps[i].Pixels, alpine.Lightmaps[i].Pixels);
        }
    }

    [Fact]
    public void Density_Off_Bakes_No_Charts_And_A_Later_Build_Without_Lighting_Drops_Them()
    {
        EditorDocument doc = LitRoom();
        AlpineLightmapSettings.Apply(doc.Rfl, null, 255);
        CompiledLevel off = GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc));
        Assert.Null(off.BakeStats!.AlpineLightmaps);
        Assert.Contains("off", off.BakeStats.AlpineSkipped, StringComparison.Ordinal);

        AlpineLightmapSettings.Apply(doc.Rfl, null, 0);
        GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc));
        Assert.NotNull(doc.Rfl.AlpineLightmaps);

        // A geometry-only build discards them: the next save writes none.
        CompileOptions geometryOnly = Options(doc);
        geometryOnly.BakeLighting = false;
        GeometryBuildService.BuildAndApply(doc.Rfl, geometryOnly);
        Assert.Null(doc.Rfl.AlpineLightmaps);
        RflFile saved = RflFile.Load(SaveAndRead(doc, out _));
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
    }

    [Fact]
    public void Charts_Not_Allowed_Drops_Them_On_Save()
    {
        // Baked with the Alpine pipeline, then the level switched to Legacy lighting before the save.
        EditorDocument doc = LitRoom();
        GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc));
        AlpineLightmapSettings.Apply(doc.Rfl, LightmapPipeline.Legacy, null);
        RflFile saved = RflFile.Load(SaveAndRead(doc, out IReadOnlyList<string> notes));
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        Assert.Contains(notes, n => n.StartsWith("Legacy lighting is set", StringComparison.Ordinal));

        // An unrebuilt level carrying surface charts with Legacy lighting set drops them as well (Alpine RED's
        // retained_brush && !charts_allowed, alpine_lightmaps.cpp:1824-1839).
        EditorDocument lit = LitRoom();
        GeometryBuildService.BuildAndApply(lit.Rfl, Options(lit));
        byte[] withCharts = SaveAndRead(lit, out _);
        EditorDocument reopened = new(RflFile.Load(withCharts), "reopened.rfl");
        AlpineLightmapSettings.Apply(reopened.Rfl, LightmapPipeline.Legacy, null);
        RflFile resaved = RflFile.Load(SaveAndRead(reopened, out IReadOnlyList<string> notes2));
        Assert.DoesNotContain(resaved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        Assert.Contains(notes2, n => n.Contains("Legacy lighting is set", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Sun_Lights_A_Level_With_Either_Pipeline()
    {
        EditorDocument doc = LitRoom(withLight: false, openTop: true);
        AlpineLevelPropertiesSection props = AlpineLightmapSettings_Props(doc.Rfl);
        CompiledLevel dark = GeometryBuildService.Build(doc.Rfl, Options(doc));
        props.EnableSun = 1;
        props.SunPitch = 90f;
        props.SunIntensity = 1f;
        CompiledLevel sunny = GeometryBuildService.Build(doc.Rfl, Options(doc));
        Assert.Equal(1, sunny.BakeStats!.DirectionalLights);
        Assert.True(Brightness(sunny) > Brightness(dark) + 1.0, $"sun {Brightness(sunny):0.0} vs none {Brightness(dark):0.0}");

        AlpineLightmapSettings.Apply(doc.Rfl, LightmapPipeline.Legacy, null);
        CompiledLevel legacySun = GeometryBuildService.Build(doc.Rfl, Options(doc));
        Assert.Equal(1, legacySun.BakeStats!.DirectionalLights);
        Assert.True(Brightness(legacySun) > Brightness(dark) + 1.0);
    }

    [Fact]
    public void Directional_Light_Volume_And_Cone_Follow_Alpine()
    {
        var d = new AlpineDirectionalLight
        {
            Uid = 5, Position = new Vec3(0, 10, 0), Orientation = new Mat3(new Vec3(0, -1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
            Color = new RfColor(255, 128, 0, 255), Intensity = 2f, Shape = 2, ExtentX = 5f, Feather = 1f, Spread = 10f,
        };
        EngineLight l = AlpineDirectionalLights.Object(d)!.Value;
        Assert.Equal(EngineLightType.Directional, l.Type);
        Assert.Equal(new Vec3(0, 1, 0), l.Position); // toward the light: minus the travel direction
        Assert.Equal(9, l.ShadowDirections!.Length);
        Assert.Equal(8f, l.Color.X, 3); // 255/255 × intensity 2 × 4
        Assert.Equal(128f * 8f / 255f, l.Color.Y, 3);
        Assert.Equal(1f, l.Volume!.Weight(new Vec3(0, 10, 0)));
        Assert.Equal(0.5f, l.Volume.Weight(new Vec3(4.5f, 10, 0)), 4);
        Assert.Equal(0f, l.Volume.Weight(new Vec3(6f, 10, 0)));
        Assert.Null(AlpineDirectionalLights.Object(new AlpineDirectionalLight { InitiallyOn = 0 }));

        Vec3 sun = AlpineDirectionalLights.SunToLight(90f, 0f); // yaw 90: toward +X on the horizon
        Assert.Equal(1f, sun.X, 5);
        Assert.Equal(0f, sun.Y, 5);
    }

    [Fact]
    public void Lua_Sets_The_Pipeline_And_Reads_What_The_Bake_Produced()
    {
        EditorDocument doc = LitRoom();
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc, """
            local s = ops.lightmaps("legacy", 16)
            assert(s.pipeline == "legacy" and s.density == 16 and not s.charts_enabled, tostring(s))
            local lit = ops.light()
            assert(lit.lightmaps.pipeline == "legacy" and lit.lightmaps.charts == 0, tostring(lit.lightmaps))
            s = ops.lightmaps("alpine", 0)
            assert(s.charts_enabled and s.effective_density == 8)
            lit = ops.light()
            assert(lit.lightmaps.pipeline == "alpine" and lit.lightmaps.charts > 0 and lit.lightmaps.density == 8 and lit.lightmaps.pages > 0)
            return lit.lightmaps.charts
            """);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.NotNull(doc.Rfl.AlpineLightmaps);
        Assert.Equal(LightmapPipeline.Alpine, AlpineLightmapSettings.FromLevel(doc.Rfl).Pipeline);

        (ScriptRunResult bad, _) = ScriptTestFixture.Run(doc, "ops.lightmaps(\"fancy\")");
        Assert.False(bad.Success);
        Assert.Contains("Unknown lightmap pipeline", bad.Error!.ToDisplayString(), StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A sealed, lit build's options through the shared factory (the GUI / automation path).</summary>
    internal static CompileOptions Options(EditorDocument doc) =>
        CompileOptionsFactory.CreateForDocument(doc, vfs: null, BuildSettings.EditorDefaults,
            new CompileRequest { Sealed = true, BakeLighting = true, CastShadows = true });

    /// <summary>A new level with one lit room (and optionally an opening in its ceiling for the sun).</summary>
    internal static EditorDocument LitRoom(bool withLight = true, bool openTop = false)
    {
        string script = openTop
            ? "local yard = kit.room{ name = 'yard', pos = {0, 0, 0}, size = {12, 6, 12} }\nkit.sky_room{ open = { yard } }"
            : "kit.room{ name = 'hall', pos = {0, 0, 0}, size = {12, 6, 12} }";
        if (withLight)
        {
            script += "\nlevel.place('light', 2, 4, 2)";
        }

        return KitTestHarness.Run(script).Doc;
    }

    private static AlpineLevelPropertiesSection AlpineLightmapSettings_Props(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
    }

    private static double Brightness(CompiledLevel c) => c.Lightmaps.SelectMany(p => p.Pixels).Average(b => (double)b);

    /// <summary>A chart's interior texels decoded from the bake's tiles (owner tile per texel).</summary>
    internal static byte[] Decode(AlpineLightmapBake bake, AlpineSurfaceChart c)
    {
        AlpineChartGeometry g = c.Geometry;
        var rgb = new byte[g.Cw * g.Ch * 3];
        for (int v = 0; v < g.Ch; v++)
        {
            for (int u = 0; u < g.Cw; u++)
            {
                (int index, int sx, int sy, bool valid) = AlpineLightmapLayout.TileSlot(g, c.TileBase, u, v);
                Assert.True(valid);
                AlpineTile t = bake.Tiles[index];
                int src = ((((t.Y + sy) * 256) + t.X + sx) * 3);
                Buffer.BlockCopy(bake.Pages[t.Page], src, rgb, ((v * g.Cw) + u) * 3, 3);
            }
        }

        return rgb;
    }

    internal static byte[] SaveAndRead(EditorDocument doc, out IReadOnlyList<string> notes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ged-alpine-lm-{Guid.NewGuid():N}.rfl");
        try
        {
            doc.Save(path, updateTimestamp: false);
            notes = doc.LastSaveNotes;
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
