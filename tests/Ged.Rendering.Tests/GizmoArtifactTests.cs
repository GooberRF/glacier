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
/// Offscreen evidence for the transform manipulator: idle / hover / drag states for
/// the Move, Rotate and Scale tools, each drawn over a reference brush and saved to
/// tests/artifacts/gizmo. Skips gracefully when no D3D11 device is available.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class GizmoArtifactTests
{
    // Okabe–Ito axis triad (matches the AppSettings defaults).
    private static readonly uint ColX = Palette.Rgba(0xD5, 0x5E, 0x00);
    private static readonly uint ColY = Palette.Rgba(0x00, 0x9E, 0x73);
    private static readonly uint ColZ = Palette.Rgba(0x56, 0xB4, 0xE9);

    [Fact]
    public void Gizmo_Idle_Hover_Drag_RenderArtifacts()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "gizmo");
        Directory.CreateDirectory(dir);

        var pose = new GizmoPose(CoreVec3.Zero, new CoreVec3(1, 0, 0), new CoreVec3(0, 1, 0), new CoreVec3(0, 0, 1), 4f);

        (GizmoTool tool, GizmoHandle hot)[] tools =
        {
            (GizmoTool.Move, GizmoHandle.MoveX),
            (GizmoTool.Rotate, GizmoHandle.RotateZ),
            (GizmoTool.Scale, GizmoHandle.ScaleX),
        };

        foreach ((GizmoTool tool, GizmoHandle hot) in tools)
        {
            SaveState(gd, dir, pose, tool, GizmoHandle.None, GizmoHandle.None, dragging: false, $"gizmo_{tool}_idle.png".ToLowerInvariant());
            SaveState(gd, dir, pose, tool, hot, GizmoHandle.None, dragging: false, $"gizmo_{tool}_hover.png".ToLowerInvariant());
            SaveState(gd, dir, pose, tool, GizmoHandle.None, hot, dragging: true, $"gizmo_{tool}_drag.png".ToLowerInvariant());
        }
    }

    private static void SaveState(GraphicsDevice gd, string dir, GizmoPose pose, GizmoTool tool, GizmoHandle hover, GizmoHandle drag, bool dragging, string file)
    {
        var camera = new Camera { Position = new Vector3(6, 6, -8), AspectRatio = 640f / 480f };
        camera.LookAt(camera.Position, Vector3.Zero);

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { Box(1, new CoreVec3(0, 0, 0), 3f) }, BrushPickGranularity.Brush, solidFill: true);
        scene.Lines.AddRange(GizmoGeometry.Build(pose, tool, hover, drag, dragging, ColX, ColY, ColZ, camera.Right, camera.Up));

        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);
        Assert.Equal(640 * 480 * 4, pixels.Length);
        Assert.True(RenderTestSupport.IsNonTrivial(pixels, out int distinct), $"{file} was trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(dir, file), PngWriter.Encode(640, 480, pixels));
    }

    private static Brush Box(int uid, CoreVec3 pos, float size)
    {
        Brush b = BrushFactory.Create(new BrushCreateParams { Width = size, Height = size, Depth = size }, uid);
        b.Position = pos;
        return b;
    }
}
