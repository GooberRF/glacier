using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.Rendering.Tests;

/// <summary>
/// Edge-mode artifact: renders a box brush in Edge mode with one edge SELECTED (orange) and
/// an adjacent edge HOVERED (cyan) — the same overlay lines the App emits — to
/// <c>tests/artifacts/edge_mode_selection.png</c>.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class EdgeModeArtifactTests
{
    [Fact]
    public void Edge_Mode_Selection_And_Hover_RenderArtifact()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var box = new Brush { Uid = 1, Rotation = Mat3.Identity, Geometry = BrushFactory.Box(2, 2, 2, 0, 0, 0, "t") };

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { box }, BrushPickGranularity.Brush, solidFill: true);

        // Pick two distinct real edges: one "selected" (orange), one "hovered" (cyan).
        IReadOnlyList<BrushEdge> edges = EdgeTopology.Edges(box.Geometry);
        AddEdge(scene, box, edges[0], Palette.Rgba(255, 130, 40, 255));
        AddEdge(scene, box, edges[1], Palette.Rgba(120, 220, 255, 255));

        var camera = new Camera { Position = new Vector3(4.5f, 3.5f, -5f), AspectRatio = 640f / 480f };
        camera.LookAt(camera.Position, Vector3.Zero);

        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);
        Assert.True(RenderTestSupport.IsNonTrivial(pixels, out _));

        Directory.CreateDirectory(RenderTestSupport.ArtifactsDir);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, "edge_mode_selection.png"),
            PngWriter.Encode(640, 480, pixels));
    }

    private static void AddEdge(RenderScene scene, Brush b, BrushEdge e, uint color)
    {
        CoreVec3 a = World(b, e.V0);
        CoreVec3 c = World(b, e.V1);
        scene.Lines.Add(new LineSegment(new Vector3(a.X, a.Y, a.Z), new Vector3(c.X, c.Y, c.Z), color));
    }

    private static CoreVec3 World(Brush b, int index) =>
        b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[index]));
}
