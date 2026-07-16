using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Tests.Compiler;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Item 6 (+ amendments) — cookie SHARPNESS: the payload extension (vstring filename + f32,
/// tolerant of the old filename-only form), the pre-blurred sampling chain and its re-tuned
/// mapping (1.0 = nearest/crispest, ~0.75 = old raw bilinear, below = progressive blur), and the
/// format-safe High-Resolution Lightmaps modifier (256 pages / 255 fragments / higher ppm).
/// </summary>
public sealed class LightCookieSharpnessTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    // ---- Payload round-trip (old filename-only + new filename+sharpness) ------

    [Fact]
    public void Old_FilenameOnly_Payload_Reads_As_Default_Sharpness()
    {
        // Simulate a pre-item-6 cookie block: a bare vstring, no trailing f32.
        var w = new RfWriter(16);
        w.WriteVString("cookies/grid.tga");
        byte[] oldPayload = w.ToArray();

        var doc = EmptyDoc();
        var meta = new GedObjectMetadataService(doc);
        meta.SetBlock(9, GedMetadataType.LightCookie, oldPayload);

        Assert.Equal("cookies/grid.tga", meta.Cookie(9));
        Assert.Equal(1f, meta.CookieSharpness(9), 4); // tolerant reader → default 1.0
    }

    [Fact]
    public void New_Payload_Round_Trips_Filename_And_Sharpness()
    {
        var doc = EmptyDoc();
        var meta = new GedObjectMetadataService(doc);
        meta.SetCookie(3, "beam.tga", 0.4f);

        Assert.Equal("beam.tga", meta.Cookie(3));
        Assert.Equal(0.4f, meta.CookieSharpness(3), 4);

        // Persist + reload the whole document: the sharpness survives the section round-trip.
        byte[] bytes = doc.SaveToBytes();
        var reloaded = new EditorDocument(RflFile.Load(bytes));
        var meta2 = new GedObjectMetadataService(reloaded);
        Assert.Equal("beam.tga", meta2.Cookie(3));
        Assert.Equal(0.4f, meta2.CookieSharpness(3), 4);
    }

    [Fact]
    public void SetCookie_Preserves_Sharpness_And_SetCookieSharpness_Preserves_Filename()
    {
        var doc = EmptyDoc();
        var meta = new GedObjectMetadataService(doc);
        meta.SetCookie(1, "a.tga", 0.6f);

        meta.SetCookieSharpness(1, 0.2f);              // change only sharpness
        Assert.Equal("a.tga", meta.Cookie(1));
        Assert.Equal(0.2f, meta.CookieSharpness(1), 4);

        // SetCookieSharpness on a light with no cookie is a no-op.
        meta.SetCookieSharpness(2, 0.5f);
        Assert.Null(meta.Cookie(2));
    }

    // ---- Blur-chain determinism ----------------------------------------------

    [Fact]
    public void Blur_Chain_Is_Deterministic_And_Actually_Blurs()
    {
        float[] data = Checker(16, 16);
        var a = new LightCookie(16, 16, (float[])data.Clone());
        var b = new LightCookie(16, 16, (float[])data.Clone());

        // Same construction + same query → identical result (fixed kernel, pure float ops).
        for (float s = 0f; s <= 1f; s += 0.1f)
        {
            Assert.Equal(a.Sample(0.31f, 0.62f, s), b.Sample(0.31f, 0.62f, s), 6);
        }

        // The blurriest level genuinely differs from the crisp sample on a high-contrast image.
        float crisp = a.Sample(0.5f, 0.5f, 1.0f);
        float blurry = a.Sample(0.5f, 0.5f, 0.0f);
        Assert.True(Math.Abs(crisp - blurry) > 0.1f, $"blur must change a checker ({crisp} vs {blurry})");
    }

    // ---- Sampling at the sharpness extremes (hard-edge fixture) ---------------

    [Fact]
    public void Hard_Edge_Cookie_Is_Crisp_At_Top_Bilinear_At_075_And_Smeared_Below()
    {
        // 8×1 hard edge: black|white at the middle (index 3→4 boundary).
        var c = new LightCookie(8, 1, new float[] { 0, 0, 0, 0, 1, 1, 1, 1 });
        const float uBlackEdge = 3f / 7f; // exactly the last black texel

        // 1.0 = NEAREST: a black texel stays hard black (crisper than bilinear).
        Assert.Equal(0f, c.Sample(uBlackEdge, 0f, 1.0f), 3);
        // 0.75 = raw bilinear: at an exact texel centre it is still that texel's value.
        Assert.Equal(0f, c.Sample(uBlackEdge, 0f, 0.75f), 3);
        // 0.0 = blurriest: white bleeds into the black edge texel (smeared, no longer 0).
        Assert.True(c.Sample(uBlackEdge, 0f, 0.0f) > 0.05f,
            $"blurriest edge should smear, got {c.Sample(uBlackEdge, 0f, 0.0f)}");

        // At the seam (u=0.5) bilinear interpolates to a mid value; nearest snaps to a hard step.
        Assert.Equal(0.5f, c.Sample(0.5f, 0f, 0.75f), 2);
        float atOne = c.Sample(0.5f, 0f, 1.0f);
        Assert.True(atOne < 0.02f || atOne > 0.98f, $"nearest seam must be a hard step, got {atOne}");
    }

    // ---- High-Resolution Lightmaps: format-safe higher texel density ---------

    [Fact]
    public void HighRes_Uses_256_Pages_255_Fragments_And_Higher_Ppm_Vs_Stock()
    {
        // A big room so its floor fragment is large enough to exercise the widened cap/page.
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, new Vec3(0, 0, 0), 60, 8, 60) };

        CompiledLevel stock = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = true, HighResLightmaps = false });
        CompiledLevel hi = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = true, HighResLightmaps = true });

        Assert.All(stock.Lightmaps, p => { Assert.Equal(128, p.Width); Assert.Equal(128, p.Height); });
        Assert.All(hi.Lightmaps, p => { Assert.Equal(256, p.Width); Assert.Equal(256, p.Height); });

        // Every fragment coord/size stays within the u8 file field (≤255) and inside its page — format safety.
        foreach (Surface s in hi.Geometry.Surfaces)
        {
            Assert.True(s.X <= 255 && s.Y <= 255 && s.W <= 255 && s.H <= 255);
            Assert.True(s.X + s.W <= 256 && s.Y + s.H <= 256, "fragment must fit inside its 256 page");
        }

        // High-res is a ×4 texel-density multiplier over stock (2.0 base × res mult × 4).
        float stockMaxPpm = stock.Geometry.Surfaces.Max(s => s.XPixelsPerMeter);
        float hiMaxPpm = hi.Geometry.Surfaces.Max(s => s.XPixelsPerMeter);
        Assert.True(hiMaxPpm > stockMaxPpm * 3.5f, $"high-res ppm ({hiMaxPpm}) should be ~4× stock ({stockMaxPpm})");

        // A high-res fragment exceeds the stock 64-texel cap AND the stock 128 page — only the
        // widened 255 fragment / 256 page can hold it (the format extension actually exercised).
        Assert.Contains(hi.Geometry.Surfaces, s => s.W > 128 || s.H > 128);
        Assert.All(stock.Geometry.Surfaces, s => Assert.True(s.W <= 64 && s.H <= 64)); // stock stays ≤64
    }

    // ---- Rendered evidence: stock (smeared) vs high-res + 100% sharpness (crisp) --------------

    [Fact]
    public void HighRes_Plus_Full_Sharpness_Crisps_The_Cookie_Boundary_Vs_Stock()
    {
        // A room + a +Z spot aimed at the +Z wall, projecting a hard-edged (left-black) cookie so
        // the wall gets a vertical dark|lit boundary at x≈0.
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, new Vec3(0, 0, 0), 12, 8, 12) };
        var rot = new Mat3(new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(0, 1, 0)); // forward,right,up
        var spot = new List<Light>
        {
            new()
            {
                Uid = 1, Position = new Vec3(0, 0, -5), Rotation = rot,
                Flags = 0x8u | (2u << 4) | (2u << 8), Color = new RfColor(255, 255, 255, 255),
                Range = 25f, Fov = 90f, FovDropoff = 0f, OnIntensity = 1f, DropoffType = 0,
            },
        };
        var ambient = new RfColor(0, 0, 0, 255);
        var cookie = new LightCookie(32, 32, HalfBlack(32, 32));

        // STOCK: 128 pages, and the old raw-bilinear look (0.75 sharpness → not smoothing-excluded).
        CompiledLevel stock = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = true, HighResLightmaps = false });
        List<Lightmap> stockPages = LevelLighting.FreshPages(stock.Lightmaps);
        LevelLighting.BakeInto(stock.Geometry, stockPages, spot, ambient,
            new LightingOptions { CookieResolver = _ => cookie, CookieSharpnessResolver = _ => LightCookie.BilinearSharpness });

        // HIGH-RES + 100% sharpness: 256 pages, nearest sampling, smoothing excluded on the gobo wall.
        CompiledLevel hi = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = true, HighResLightmaps = true });
        List<Lightmap> hiPages = LevelLighting.FreshPages(hi.Lightmaps);
        LevelLighting.BakeInto(hi.Geometry, hiPages, spot, ambient,
            new LightingOptions { CookieResolver = _ => cookie, CookieSharpnessResolver = _ => 1.0f });

        (double stockCrisp, int stockSurf) = BoundarySteepnessPerMeter(stock.Geometry, stockPages);
        (double hiCrisp, int hiSurf) = BoundarySteepnessPerMeter(hi.Geometry, hiPages);

        WritePage("cookie_boundary_stock.png", stockPages[stock.Geometry.Surfaces[stockSurf].LightmapIndex]);
        WritePage("cookie_boundary_highres_sharp.png", hiPages[hi.Geometry.Surfaces[hiSurf].LightmapIndex]);

        // Crispness = how steep the gobo boundary is in WORLD space (normalized-luminance change per
        // meter): the sharpest single-texel step × the surface's texel density. Isolates the cookie
        // edge from the spot's smooth falloff (whose per-texel gradient is small). High-res + nearest
        // + smoothing-excluded makes that edge far steeper per meter than the stock smeared bilinear.
        Assert.True(hiCrisp > stockCrisp * 2.0,
            $"high-res+sharp boundary should be much steeper per meter: {hiCrisp:F2}/m vs stock {stockCrisp:F2}/m");
    }

    /// <summary>Steepest normalized-luminance gradient per meter on the highest-contrast lit surface.</summary>
    private static (double SteepnessPerMeter, int SurfaceIndex) BoundarySteepnessPerMeter(Geometry g, IReadOnlyList<Lightmap> pages)
    {
        int best = 0;
        double bestRange = -1;
        for (int i = 0; i < g.Surfaces.Count; i++)
        {
            (float mn, float mx, _) = FragmentStats(g.Surfaces[i], pages);
            if (mx - mn > bestRange)
            {
                bestRange = mx - mn;
                best = i;
            }
        }

        Surface s = g.Surfaces[best];
        (float min, float max, float[] lum) = FragmentStats(s, pages);
        float range = max - min;
        if (range < 1e-3f)
        {
            return (0, best);
        }

        float maxStep = 0f;
        for (int row = 0; row < s.H; row++)
        {
            for (int col = 0; col < s.W; col++)
            {
                float here = lum[(row * s.W) + col];
                if (col + 1 < s.W)
                {
                    maxStep = MathF.Max(maxStep, MathF.Abs(lum[(row * s.W) + col + 1] - here));
                }

                if (row + 1 < s.H)
                {
                    maxStep = MathF.Max(maxStep, MathF.Abs(lum[((row + 1) * s.W) + col] - here));
                }
            }
        }

        float ppm = MathF.Max(s.XPixelsPerMeter, s.YPixelsPerMeter);
        return ((maxStep / range) * ppm, best);
    }

    private static (float Min, float Max, float[] Lum) FragmentStats(Surface s, IReadOnlyList<Lightmap> pages)
    {
        Lightmap page = pages[s.LightmapIndex];
        var lum = new float[s.W * s.H];
        float min = float.MaxValue, max = float.MinValue;
        for (int row = 0; row < s.H; row++)
        {
            for (int col = 0; col < s.W; col++)
            {
                int o = (((s.Y + row) * page.Width) + (s.X + col)) * 3;
                float l = (page.Pixels[o] + page.Pixels[o + 1] + page.Pixels[o + 2]) / 3f;
                lum[(row * s.W) + col] = l;
                min = MathF.Min(min, l);
                max = MathF.Max(max, l);
            }
        }

        return (min, max, lum);
    }

    private static float[] HalfBlack(int w, int h)
    {
        var a = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                a[(y * w) + x] = x < w / 2 ? 0f : 1f;
            }
        }

        return a;
    }

    private static void WritePage(string file, Lightmap page)
    {
        if (page.Width <= 0)
        {
            return;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return;
        }

        var rgba = new byte[page.Width * page.Height * 4];
        int px = Math.Min(page.Width * page.Height, page.Pixels.Length / 3);
        for (int i = 0; i < px; i++)
        {
            rgba[(i * 4) + 0] = page.Pixels[(i * 3) + 0];
            rgba[(i * 4) + 1] = page.Pixels[(i * 3) + 1];
            rgba[(i * 4) + 2] = page.Pixels[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        string outDir = Path.Combine(dir.FullName, "tests", "artifacts", "cookies");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, file), Ged.Core.IO.Tex.PngWriter.Encode(page.Width, page.Height, rgba));
    }

    private static float[] Checker(int w, int h)
    {
        var a = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                a[(y * w) + x] = ((x + y) & 1) == 0 ? 1f : 0f;
            }
        }

        return a;
    }
}
