using System;
using System.Collections.Generic;
using System.IO;
using Ged.Core.Compiler;
using Ged.Core.IO.Tex;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Tests.Compiler;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Item 4 — light cookies (greyscale projection gobos): Rec.601 luminance conversion, bilinear
/// clamp sampling, the SPOT gobo projection math (hand-computed: a half-black cookie darkens half
/// the cone), and end-to-end baker integration (a spot through a half-black cookie darkens half a
/// lit wall; a white cookie is a no-op) with a rendered lightmap-page artifact.
/// </summary>
public sealed class LightCookieTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    // ---- Luminance conversion (Rec.601) --------------------------------------

    [Fact]
    public void FromRgba_Uses_Rec601_Luminance()
    {
        Assert.Equal(0.299f, LightCookie.FromRgba(1, 1, new byte[] { 255, 0, 0, 255 }).SampleBilinearClamp(0, 0), 3);
        Assert.Equal(0.587f, LightCookie.FromRgba(1, 1, new byte[] { 0, 255, 0, 255 }).SampleBilinearClamp(0, 0), 3);
        Assert.Equal(0.114f, LightCookie.FromRgba(1, 1, new byte[] { 0, 0, 255, 255 }).SampleBilinearClamp(0, 0), 3);
        Assert.Equal(1.000f, LightCookie.FromRgba(1, 1, new byte[] { 255, 255, 255, 255 }).SampleBilinearClamp(0, 0), 3);
        Assert.Equal(0.000f, LightCookie.FromRgba(1, 1, new byte[] { 0, 0, 0, 255 }).SampleBilinearClamp(0, 0), 3);
    }

    [Fact]
    public void SampleBilinearClamp_Interpolates_And_Clamps()
    {
        var c = new LightCookie(2, 1, new float[] { 0f, 1f });
        Assert.Equal(0.0f, c.SampleBilinearClamp(0f, 0f), 3);
        Assert.Equal(1.0f, c.SampleBilinearClamp(1f, 0f), 3);
        Assert.Equal(0.5f, c.SampleBilinearClamp(0.5f, 0f), 3);
        Assert.Equal(1.0f, c.SampleBilinearClamp(5f, 0f), 3);   // clamp past the right edge
        Assert.Equal(0.0f, c.SampleBilinearClamp(-5f, 0f), 3);  // clamp past the left edge
    }

    // ---- SPOT gobo projection math (hand-computed) ---------------------------

    [Fact]
    public void Spot_Gobo_Projects_Half_Black_Cookie_To_Half_Dark_Cone()
    {
        // A +Z spot with a 45° outer cone (tan = 1), cookie U/V = world +X/+Y. Cookie is black on
        // the LEFT half (U<0.5) and white on the right — so it darkens the −X half of the cone.
        var cookie = new LightCookie(4, 1, new float[] { 0f, 0f, 1f, 1f });
        var light = new EngineLight
        {
            Type = EngineLightType.Spot,
            Position = V(0, 0, 0),
            SpotAxis = V(0, 0, 1),
            CookieRight = V(1, 0, 0),
            CookieUp = V(0, 1, 0),
            CookieConeTan = 1f,
            Cookie = cookie,
            CookieSharpness = LightCookie.BilinearSharpness, // 0.75: the raw-bilinear look (seam interpolates)
        };

        // +X side of the cone (u = +0.7) → white → fully lit.
        Assert.Equal(1f, CookieProjection.Mask(light, V(0.7f, 0, 1)), 3);
        // −X side (u = −0.7) → black → fully dark.
        Assert.Equal(0f, CookieProjection.Mask(light, V(-0.7f, 0, 1)), 3);
        // Cone centre → the black/white seam → half (bilinear).
        Assert.Equal(0.5f, CookieProjection.Mask(light, V(0, 0, 1)), 3);
    }

    [Fact]
    public void No_Cookie_And_Tube_Cookie_Are_A_No_Op()
    {
        var noCookie = new EngineLight { Type = EngineLightType.Spot, Position = V(0, 0, 0), SpotAxis = V(0, 0, 1) };
        Assert.Equal(1f, CookieProjection.Mask(noCookie, V(1, 0, 1)), 3);

        var tube = new EngineLight
        {
            Type = EngineLightType.Tube,
            Position = V(0, 0, 0),
            Cookie = new LightCookie(2, 1, new float[] { 0f, 0f }), // all black, but tube is skipped
        };
        Assert.Equal(1f, CookieProjection.Mask(tube, V(1, 0, 1)), 3);
    }

    // ---- End-to-end baker integration ----------------------------------------

    private static Light Spot(int uid, Vec3 pos, Mat3 rot, float fov, float range) => new()
    {
        Uid = uid,
        Position = pos,
        Rotation = rot,
        Flags = 0x8u | (2u << 4) | (2u << 8), // enabled, SPOT, state on
        Color = new RfColor(255, 255, 255, 255),
        Range = range,
        Fov = fov,
        FovDropoff = 0f,
        OnIntensity = 1f,
        DropoffType = 0,
    };

    private static long Sum(IReadOnlyList<Lightmap> pages)
    {
        long s = 0;
        foreach (Lightmap p in pages)
        {
            foreach (byte b in p.Pixels)
            {
                s += b;
            }
        }

        return s;
    }

    [Fact]
    public void Baker_Applies_The_Cookie_Half_Darkening_A_Lit_Wall()
    {
        // Room + a +Z spot aimed at the +Z wall. Rotation rows are forward, right, up.
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 12, 8, 12) };
        CompiledLevel c = GeometryCompiler.Compile(brushes);
        var rot = new Mat3(V(0, 0, 1), V(1, 0, 0), V(0, 1, 0));
        var spot = new List<Light> { Spot(1, V(0, 0, -5), rot, fov: 90f, range: 25f) };
        var ambient = new RfColor(0, 0, 0, 255);

        // Baseline (no cookie).
        List<Lightmap> plain = LevelLighting.FreshPages(c.Lightmaps);
        LevelLighting.BakeInto(c.Geometry, plain, spot, ambient, new LightingOptions());
        long plainSum = Sum(plain);
        Assert.True(plainSum > 0, "the spot must light the room");

        // A pure-white cookie is a lighting no-op (mask == 1 everywhere). Sharpness is held below
        // the smoothing-exclusion threshold so the ONLY variable is the (identity) cookie mask —
        // a uniform cookie blurs/samples to 1.0 at any sharpness, so the bake stays byte-identical.
        var white = new LightCookie(4, 4, Fill(16, 1f));
        List<Lightmap> whitePages = LevelLighting.FreshPages(c.Lightmaps);
        LevelLighting.BakeInto(c.Geometry, whitePages, spot, ambient,
            new LightingOptions { CookieResolver = uid => uid == 1 ? white : null, CookieSharpnessResolver = _ => 0.5f });
        Assert.Equal(plainSum, Sum(whitePages));

        // A half-black cookie (left half black) darkens roughly the −X half of the lit wall.
        var halfBlack = new LightCookie(4, 1, new float[] { 0f, 0f, 1f, 1f });
        List<Lightmap> cookiePages = LevelLighting.FreshPages(c.Lightmaps);
        LevelLighting.BakeInto(c.Geometry, cookiePages, spot, ambient,
            new LightingOptions { CookieResolver = uid => uid == 1 ? halfBlack : null });
        long cookieSum = Sum(cookiePages);

        Assert.True(cookieSum < plainSum * 0.75, $"the cookie must darken part of the bake ({cookieSum} vs {plainSum})");
        Assert.True(cookieSum > plainSum * 0.15, $"the lit half must survive ({cookieSum} vs {plainSum})");

        WriteAtlasArtifact("cookie_half_dark_wall.png", cookiePages);
    }

    private static float[] Fill(int n, float value)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = value;
        }

        return a;
    }

    private static void WriteAtlasArtifact(string file, IReadOnlyList<Lightmap> pages)
    {
        if (pages.Count == 0 || pages[0].Width <= 0)
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

        Lightmap page = pages[0];
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
        File.WriteAllBytes(Path.Combine(outDir, file), PngWriter.Encode(page.Width, page.Height, rgba));
    }
}
