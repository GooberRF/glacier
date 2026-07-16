using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Rendering.Tests;

/// <summary>
/// Visual + numeric proof for the Corner Leak Fix and Smooth Gutter Normals options. Bakes the two
/// synthetic defect fixtures (an ambient corner leak; a smoothed surface's gutter rim) off vs on and
/// writes upscaled lightmap-atlas PNGs to tests/artifacts/lighting_leakfix/, then (if the corpus +
/// RF install are present) compiles a real level with each option off vs on and reports how many
/// atlas texels change and in which direction. The GPU close-up render is best-effort (skips without
/// a device). The synthetic-fixture atlas dumps always run.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class LightingLeakFixArtifactTests
{
    private readonly ITestOutputHelper _out;
    public LightingLeakFixArtifactTests(ITestOutputHelper output) => _out = output;

    private static OccluderBvh NoOcc() => OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());
    private static Room RoomWith(Aabb box, RfColor amb) => new() { Aabb = box, HasAmbientLight = 1, AmbientColor = amb };

    // ---- Synthetic fixture atlas dumps (CPU, always run) ----------------------

    [Fact]
    public void Fixture_Atlases_Off_Vs_On_Are_Written()
    {
        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "lighting_leakfix");
        Directory.CreateDirectory(dir);

        // Ambient corner leak: dark room floor with a smaller bright room's bbox overlapping a corner.
        AmbientField amb = new(
            new Vec3(1, 1, 1),
            new List<Room>
            {
                RoomWith(new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 4)), new RfColor(40, 40, 40, 255)),
                RoomWith(new Aabb(new Vec3(8, 8, 0), new Vec3(14, 14, 4)), new RfColor(230, 230, 230, 255)),
            });
        Surface floor = new()
        {
            LightmapIndex = 0, X = 0, Y = 0, W = 24, H = 24,
            BoundingBox = new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 0)),
            Plane = new RfPlane(new Vec3(0, 0, 1), 0f),
            ShouldSmooth = 0, UCoefficient = 0, VCoefficient = 1, DroppedCoefficient = 2,
            UvAdd = new Uv(0.5f / 32, 0.5f / 32), UvScale = new Uv(1f / 32, 1f / 32), RoomIndex = 0,
        };
        WriteAtlas(dir, "ambient_leak_off", BakeOne(floor, null, amb, o => o.CornerLeakFix = false, quality: true, shadows: false), 32, floor);
        WriteAtlas(dir, "ambient_leak_on", BakeOne(floor, null, amb, o => o.CornerLeakFix = true, quality: true, shadows: false), 32, floor);

        // Smooth gutter rim: one smoothed triangle whose gutter texels fall back to the flat normal.
        var sf = new List<SmoothFace>
        {
            new(
                new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 10, 0) },
                new[]
                {
                    new Vec3(-0.6f, -0.6f, 1).Normalized(),
                    new Vec3(0.85f, -0.2f, 1).Normalized(),
                    new Vec3(-0.2f, 0.85f, 1).Normalized(),
                }),
        };
        Surface tri = new()
        {
            LightmapIndex = 0, X = 0, Y = 0, W = 24, H = 24,
            BoundingBox = new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 0)),
            Plane = new RfPlane(new Vec3(0, 0, 1), 0f),
            ShouldSmooth = 1, UCoefficient = 0, VCoefficient = 1, DroppedCoefficient = 2,
            UvAdd = new Uv(0.5f / 32, 0.5f / 32), UvScale = new Uv(1f / 32, 1f / 32), RoomIndex = 0,
        };
        var light = new EngineLight
        {
            Type = EngineLightType.Point, Position = new Vec3(50, -25, 22), Position2 = new Vec3(50, -25, 22),
            Color = new Vec3(2f, 2f, 2f), Range = 250f, RangeSq = 62500f, AttenAlgo = 0, Enabled = true, CastsShadows = false,
        };
        AmbientField famb = new(new Vec3(0.15f, 0.15f, 0.15f), Array.Empty<Room>());
        WriteAtlas(dir, "smooth_gutter_off", BakeOne(tri, sf, famb, o => o.SmoothGutterNormals = false, quality: false, shadows: false, light), 32, tri);
        WriteAtlas(dir, "smooth_gutter_on", BakeOne(tri, sf, famb, o => o.SmoothGutterNormals = true, quality: false, shadows: false, light), 32, tri);

        _out.WriteLine($"wrote fixture atlases to {dir}");
        Assert.True(File.Exists(Path.Combine(dir, "ambient_leak_on.png")));
        Assert.True(File.Exists(Path.Combine(dir, "smooth_gutter_on.png")));
    }

    private static Lightmap BakeOne(Surface s, List<SmoothFace>? sf, AmbientField amb, Action<LightingOptions> cfg, bool quality, bool shadows, EngineLight? light = null)
    {
        var page = new Lightmap { Width = 32, Height = 32, Pixels = new byte[32 * 32 * 3] };
        var opts = new LightingOptions { CastShadows = shadows, Quality = quality, SmoothIterations = quality ? 1 : 0 };
        cfg(opts);
        var lights = light is EngineLight l ? new List<EngineLight> { l } : new List<EngineLight>();
        Lightmapper.Bake(new List<SurfaceBake> { new(s, false, sf) }, new[] { page }, lights, NoOcc(), amb, opts);
        return page;
    }

    // Upscale the fragment region of the atlas (nearest-neighbour ×N) and write RGBA PNG.
    private static void WriteAtlas(string dir, string name, Lightmap page, int pageDim, Surface s)
    {
        const int scale = 14;
        int w = s.W, h = s.H;
        int ow = w * scale, oh = h * scale;
        var rgba = new byte[ow * oh * 4];
        int stride = page.Width * 3;
        for (int oy = 0; oy < oh; oy++)
        {
            int row = s.Y + (oy / scale);
            for (int ox = 0; ox < ow; ox++)
            {
                int col = s.X + (ox / scale);
                int si = (row * stride) + (col * 3);
                int di = ((oy * ow) + ox) * 4;
                rgba[di] = page.Pixels[si];
                rgba[di + 1] = page.Pixels[si + 1];
                rgba[di + 2] = page.Pixels[si + 2];
                rgba[di + 3] = 255;
            }
        }

        File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(ow, oh, rgba));
    }

    // ---- Corpus numeric (compile a real level off vs on) ----------------------

    [Fact]
    public void Corpus_Options_Change_The_Real_Atlas_And_Default_Is_Unchanged()
    {
        string? orig = RenderTestSupport.CorpusFile("dm04.rfl");
        if (orig is null || RenderTestSupport.RfInstall is null)
        {
            _out.WriteLine("skipped: no corpus / RF install");
            return;
        }

        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            var traits = new TextureTraitsCache(vfs);

            byte[] Bake(Action<LightingOptions> cfg)
            {
                RflFile rfl = RflFile.Load(orig);
                var opts = new CompileOptions { TextureTraits = traits.Get, BakeLighting = true };
                cfg(opts.Lighting);
                GeometryBuildService.BuildAndApply(rfl, opts);
                return AtlasBytes(rfl);
            }

            byte[] baseline = Bake(_ => { });
            byte[] baselineAgain = Bake(_ => { });
            byte[] leak = Bake(o => o.CornerLeakFix = true);
            byte[] gutters = Bake(o => { o.SmoothGutterNormals = true; o.AngleWeightedNormals = true; });

            Assert.Equal(baseline, baselineAgain); // default bake is deterministic + unchanged

            Report("CornerLeakFix", baseline, leak);
            Report("SmoothGutters", baseline, gutters);

            // Each option must engage on the real atlas (touch at least some texels).
            Assert.True(Changed(baseline, leak) + Changed(baseline, gutters) > 0,
                "at least one option must change the real dm04 atlas");
        }
        finally
        {
            vfs.Dispose();
        }
    }

    private void Report(string tag, byte[] a, byte[] b)
    {
        int changed = 0; long darker = 0, brighter = 0; int maxDelta = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = b[i] - a[i];
            if (d != 0)
            {
                changed++;
                if (d < 0) { darker += -d; } else { brighter += d; }
                maxDelta = Math.Max(maxDelta, Math.Abs(d));
            }
        }

        _out.WriteLine($"dm04 {tag}: {changed} channel-values changed ({changed * 100.0 / Math.Max(1, n):F3}%), " +
            $"darkened-sum {darker}, brightened-sum {brighter}, max |Δ| {maxDelta}");
    }

    private static int Changed(byte[] a, byte[] b)
    {
        int c = 0, n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i]) { c++; }
        }

        return c;
    }

    // ---- Corpus close-up visual (GPU; best-effort) ----------------------------

    [Fact]
    public void Corpus_CloseUp_Off_Vs_On_Renders()
    {
        string? orig = RenderTestSupport.CorpusFile("dm04.rfl");
        if (orig is null || RenderTestSupport.RfInstall is null)
        {
            _out.WriteLine("skipped: no corpus / RF install");
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out string why);
        if (gd is null)
        {
            _out.WriteLine($"skipped: no GPU ({why})");
            return;
        }

        const int size = 720;
        AssetVfs vfs = GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            var traits = new TextureTraitsCache(vfs);
            string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "lighting_leakfix");
            Directory.CreateDirectory(dir);

            byte[]? offPx = null;
            foreach ((string tag, Action<LightingOptions> cfg) in new (string, Action<LightingOptions>)[]
            {
                ("off", _ => { }),
                ("on", o => { o.CornerLeakFix = true; o.SmoothGutterNormals = true; o.AngleWeightedNormals = true; }),
            })
            {
                RflFile rfl = RflFile.Load(orig);
                var opts = new CompileOptions { TextureTraits = traits.Get, BakeLighting = true };
                cfg(opts.Lighting);
                GeometryBuildService.BuildAndApply(rfl, opts);

                RenderScene scene = SceneBuilder.Build(rfl, new SceneBuildOptions
                {
                    IncludeObjects = false, IncludeLinks = false, IncludeLightRanges = false, IncludeRegionOutlines = false,
                });
                Vector3 c = (ToVec(scene.Bounds.P1) + ToVec(scene.Bounds.P2)) * 0.5f;
                var cam = new Camera { Projection = CameraProjection.Perspective, AspectRatio = 1f };
                cam.LookAt(c + new Vector3(3f, 1.5f, 0f), c + new Vector3(0f, 1f, 4f));

                byte[] px = OffscreenRenderer.Render(gd, scene, vfs, cam, RenderMode.TexturesAndLightmaps, size, size);
                File.WriteAllBytes(Path.Combine(dir, $"dm04_leakfix_{tag}.png"), PngWriter.Encode(size, size, px));
                if (tag == "off") { offPx = px; } else { _out.WriteLine($"dm04 close-up on-vs-off diff {DiffPct(offPx!, px):F2}%"); }
            }

            Assert.True(File.Exists(Path.Combine(dir, "dm04_leakfix_on.png")));
        }
        finally
        {
            vfs.Dispose();
        }
    }

    private static Vector3 ToVec(Ged.Core.Model.Vec3 v) => new(v.X, v.Y, v.Z);

    private static double DiffPct(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length) / 4, diff = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            if (Math.Abs(a[o] - b[o]) > 6 || Math.Abs(a[o + 1] - b[o + 1]) > 6 || Math.Abs(a[o + 2] - b[o + 2]) > 6)
            {
                diff++;
            }
        }

        return n == 0 ? 0 : diff * 100.0 / n;
    }

    private static byte[] AtlasBytes(RflFile rfl)
    {
        var ms = new MemoryStream();
        foreach (RflSection sec in rfl.Sections)
        {
            if (sec.Content is LightmapsSection lm)
            {
                foreach (Lightmap page in lm.Lightmaps)
                {
                    ms.Write(page.Pixels, 0, page.Pixels.Length);
                }
            }
        }

        return ms.ToArray();
    }
}
