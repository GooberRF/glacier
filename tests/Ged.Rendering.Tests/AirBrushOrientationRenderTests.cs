using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Render evidence for storing air brushes inward, as RED does (<see cref="BrushOrientation"/>): a level whose
/// air room is stored inward builds and renders PIXEL-IDENTICALLY to the same level with the room stored outward
/// (the compiler reads every air brush inward), seen from inside the room. The authored brush fill
/// (unbuilt / mid-edit brush view, back-face culled) draws an inward air brush the way RED-made air brushes always
/// drew — its walls visible from inside — and the artifacts record both looks for eyeballing.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class AirBrushOrientationRenderTests
{
    private const int W = 480, H = 360;

    private static Brush Box(int uid, Vec3 c, float w, float h, float d, BrushFlags flags, bool inward)
    {
        var b = new Brush
        {
            Uid = uid,
            Position = c,
            Rotation = Mat3.Identity,
            Geometry = BrushFactory.Box(w, h, d, 1, 1, 1, "wall"),
            Flags = (uint)flags,
            Life = -1,
        };
        if (inward)
        {
            BrushOrientation.Reverse(b.Geometry);
        }

        return b;
    }

    private static List<Brush> Level(bool airInward) => new()
    {
        Box(1, new Vec3(0, 4, 0), 16, 8, 16, BrushFlags.Air, airInward),
        Box(2, new Vec3(3, 4, 2), 2, 8, 2, BrushFlags.None, inward: false), // a pillar
        Box(3, new Vec3(-4, 1, -3), 3, 2, 3, BrushFlags.None, inward: false), // a crate
    };

    private static RflFile Compiled(bool airInward)
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.Brushes, System.Array.Empty<byte>())
        {
            Content = new BrushesSection { Brushes = Level(airInward) },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        GeometryBuildService.BuildAndApply(rfl);
        return rfl;
    }

    private static int ChangedPixels(byte[] a, byte[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])
            {
                changed++;
            }
        }

        return changed;
    }

    private static Camera Cam(Vector3 eye, Vector3 target)
    {
        var cam = new Camera { AspectRatio = (float)W / H };
        cam.LookAt(eye, target);
        return cam;
    }

    [Fact]
    public void Inward_Air_Room_Builds_And_Renders_Exactly_Like_The_Outward_One()
    {
        RflFile outward = Compiled(airInward: false);
        RflFile inward = Compiled(airInward: true);
        Assert.Equal(BrushWinding.Inward, BrushOrientation.MeasureLocal(Level(true)[0].Geometry));

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return; // no GPU device in this environment
        }

        var views = new (string Name, Camera Cam)[]
        {
            ("inside", Cam(new Vector3(-6, 5, -6), new Vector3(4, 2, 5))),
            ("inside_up", Cam(new Vector3(5, 1.5f, 5), new Vector3(-3, 7, -4))),
        };
        foreach ((string name, Camera cam) in views)
        {
            // Wireframe: every compiled edge is drawn, so any difference in how the faces were split shows.
            byte[] a = OffscreenRenderer.Render(gd, SceneBuilder.Build(outward, new SceneBuildOptions()), null, cam, RenderMode.Wireframe, W, H);
            byte[] b = OffscreenRenderer.Render(gd, SceneBuilder.Build(inward, new SceneBuildOptions()), null, cam, RenderMode.Wireframe, W, H);
            File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, $"air_orientation_compiled_{name}_outward.png"), PngWriter.Encode(W, H, a));
            File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, $"air_orientation_compiled_{name}_inward.png"), PngWriter.Encode(W, H, b));
            Assert.True(RenderTestSupport.IsNonTrivial(a, out _), $"{name}: the compiled room must render.");
            Assert.Equal(0, ChangedPixels(a, b));
        }
    }

    [Fact]
    public void Authored_Air_Brush_Fill_Records_Both_Orientations_For_Eyeballing()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        Camera inside = Cam(new Vector3(-6, 5, -6), new Vector3(4, 2, 5));
        Camera outside = Cam(new Vector3(-20, 16, -22), new Vector3(0, 3, 0));
        byte[] empty = OffscreenRenderer.Render(gd, new RenderScene(), null, inside, RenderMode.JustTextures, W, H);
        var fill = new Dictionary<string, int>();
        foreach (bool airInward in new[] { false, true })
        {
            var scene = new RenderScene();
            BrushEmitter.Append(scene, Level(airInward), BrushPickGranularity.Brush, solidFill: true);
            foreach ((string view, Camera cam) in new[] { ("inside", inside), ("outside", outside) })
            {
                byte[] img = OffscreenRenderer.Render(gd, scene, null, cam, RenderMode.JustTextures, W, H);
                string key = $"{view}_{(airInward ? "inward" : "outward")}";
                File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, $"air_orientation_authored_{key}.png"), PngWriter.Encode(W, H, img));
                fill[key] = ChangedPixels(img, empty);
            }
        }

        // Stored inward (RED's form), the air room's walls fill the view from inside, as compiled walls do.
        Assert.True(fill["inside_inward"] > W * H / 2, $"inward air brush seen from inside fills {fill["inside_inward"]} px");
        Assert.True(fill["inside_inward"] > fill["inside_outward"], "the inward air brush shows its walls from inside");
    }
}
