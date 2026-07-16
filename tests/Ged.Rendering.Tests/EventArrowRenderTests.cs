using System;
using System.IO;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Offscreen-render evidence for the directional-event facing arrow (Alpine
/// event.cpp:1249-1263): builds the scene end-to-end through <see cref="SceneBuilder"/>
/// from an RFL with an oriented Teleport event, renders a close-up, asserts
/// arrow-coloured pixels are actually rasterized, and writes a PNG artifact.
/// Skips gracefully when no GPU device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class EventArrowRenderTests
{
    [Fact]
    public void EventFacingArrow_RenderArtifact_With_ArrowColoredPixels()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // A Teleport event facing +X, over a reference floor block.
        var events = new EventsSection();
        events.Events.Add(new RflEvent
        {
            Uid = 10,
            ClassName = "Teleport",
            Position = new Vec3(0, 2.5f, 0),
            Rotation = new Mat3(new Vec3(1, 0, 0), new Vec3(0, 0, -1), new Vec3(0, 1, 0)),
        });

        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)SectionType.Events, Array.Empty<byte>()) { Content = events, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        RenderScene scene = SceneBuilder.Build(rfl, new SceneBuildOptions());

        Brush floor = BrushFactory.Create(new BrushCreateParams { Width = 10f, Height = 0.4f, Depth = 10f }, 1);
        floor.Position = new Vec3(0, -1, 0);
        BrushEmitter.Append(scene, new[] { floor }, BrushPickGranularity.Brush, solidFill: true);

        // Close-up: camera slightly above/behind, looking at the event; the arrow
        // sits against the dark backdrop so shaft + head read clearly.
        var camera = new Camera { Position = new Vector3(1f, 3f, -4f) };
        camera.LookAt(new Vector3(1f, 3f, -4f), new Vector3(1f, 2.4f, 0f));
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);

        Assert.Equal(640 * 480 * 4, pixels.Length);
        Assert.True(RenderTestSupport.IsNonTrivial(pixels, out int distinct), $"render was trivial ({distinct} colors).");

        // The arrow must actually rasterize: look for its colour (255,110,40) within tolerance.
        Assert.True(CountNear(pixels, 255, 110, 40, tol: 40) >= 4,
            "expected arrow-coloured pixels in the close-up render");

        File.WriteAllBytes(
            Path.Combine(RenderTestSupport.ArtifactsDir, "event_facing_arrow.png"),
            PngWriter.Encode(640, 480, pixels));
    }

    private static int CountNear(byte[] rgba, int r, int g, int b, int tol)
    {
        int count = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (Math.Abs(rgba[i] - r) <= tol && Math.Abs(rgba[i + 1] - g) <= tol && Math.Abs(rgba[i + 2] - b) <= tol)
            {
                count++;
            }
        }

        return count;
    }
}
