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
/// Offscreen evidence for the Item 8 draw-brush tool: a <see cref="DrawBrushTool"/>
/// is driven with known rays to stage 2 (the rubber-band base rectangle, drawn as a
/// thin ghost slab) and stage 3 (the extruded ghost box), each rendered over a
/// reference grid to tests/artifacts/drawbrush. Skips gracefully without a D3D11
/// device. The ghost edges reproduce the App's cutter-ghost path: a BrushFactory box
/// at the tool's center/dims with deduped edge lines in the ghost color.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class DrawBrushArtifactTests
{
    private static readonly uint GhostColor = Palette.Rgba(200, 200, 90, 200);

    [Fact]
    public void DrawBrush_Stage2_Rect_And_Stage3_Box_RenderArtifacts()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "drawbrush");
        Directory.CreateDirectory(dir);

        var tool = new DrawBrushTool { GridSize = 1f, SnapEnabled = true };
        tool.Begin();

        // Stage 1 → 2: a vertical ray anchors corner A at the world origin.
        tool.Click(new CoreVec3(0.2f, 10f, 0.1f), new CoreVec3(0f, -1f, 0f));

        // Stage 2: rubber-band the base to (4, 0, 3) — a 4×3 rectangle (thin ghost slab).
        tool.Hover(new CoreVec3(4.2f, 10f, 2.8f), new CoreVec3(0f, -1f, 0f));
        Save(gd, dir, tool, "drawbrush_stage2_rect.png");

        // Fix the base, then extrude: a horizontal ray at y=2 maps to height 2.
        tool.Click(new CoreVec3(4.2f, 10f, 2.8f), new CoreVec3(0f, -1f, 0f));
        tool.Hover(new CoreVec3(2f, 2f, -10f), new CoreVec3(0f, 0f, 1f));
        Assert.Equal(DrawBrushStage.Height, tool.Stage);
        Save(gd, dir, tool, "drawbrush_stage3_box.png");
    }

    private static void Save(GraphicsDevice gd, string dir, DrawBrushTool tool, string file)
    {
        Assert.True(tool.GhostBox is not null, $"{file}: the tool has no ghost box to render.");
        (CoreVec3 center, float w, float h, float d) = tool.GhostBox!.Value;

        var camera = new Camera { Position = new Vector3(9f, 7f, -9f), AspectRatio = 640f / 480f };
        camera.LookAt(camera.Position, new Vector3(2f, 0.5f, 1.5f));

        var scene = new RenderScene();
        GridBuilder.Append(scene, Vector3.Zero, 12f, 1f);
        scene.Lines.AddRange(GhostEdges(center, w, h, d));

        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);
        Assert.Equal(640 * 480 * 4, pixels.Length);
        Assert.True(RenderTestSupport.IsNonTrivial(pixels, out int distinct), $"{file} was trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(dir, file), PngWriter.Encode(640, 480, pixels));
    }

    /// <summary>Deduped ghost-box edge lines — the same approach as the App's cutter ghost.</summary>
    private static IEnumerable<LineSegment> GhostEdges(CoreVec3 center, float w, float h, float d)
    {
        Brush ghost = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = w, Height = h, Depth = d }, 0);
        ghost.Position = center;

        var seen = new HashSet<(int, int)>();
        foreach (Face f in ghost.Geometry.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int a = f.Vertices[i].Index;
                int b = f.Vertices[(i + 1) % n].Index;
                var key = a < b ? (a, b) : (b, a);
                if (!seen.Add(key))
                {
                    continue;
                }

                CoreVec3 pa = ghost.Position.Add(ghost.Geometry.Vertices[a]);
                CoreVec3 pb = ghost.Position.Add(ghost.Geometry.Vertices[b]);
                yield return new LineSegment(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), GhostColor);
            }
        }
    }
}
