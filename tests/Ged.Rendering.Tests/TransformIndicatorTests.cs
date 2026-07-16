using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// The in-viewport transform progress indicators (item: transform indicators): the MOVE
/// dimension line, the ROTATE angle arc, the label formatting (including the new '%'
/// glyph), plus offscreen PNG artifacts of a mid-drag frame for each tool, mirroring
/// <see cref="GizmoArtifactTests"/>.
/// </summary>
public sealed class TransformIndicatorTests
{
    private const uint Color = 0xFFFFFFFF;

    // ---- Move dimension line ---------------------------------------------------

    [Fact]
    public void MoveLine_Spans_Start_To_End_With_End_Ticks()
    {
        var start = new Vector3(1, 2, 3);
        var end = new Vector3(5, 2, 3);
        IReadOnlyList<LineSegment> lines = TransformIndicatorBuilder.MoveLine(start, end, Color);

        // 1 main line + 2 tick strokes per end.
        Assert.Equal(5, lines.Count);
        Assert.Equal(start, lines[0].A);
        Assert.Equal(end, lines[0].B);

        // Ticks are perpendicular to the line direction and centred on the endpoints.
        Vector3 dir = Vector3.Normalize(end - start);
        foreach (LineSegment tick in lines.Skip(1))
        {
            Vector3 mid = (tick.A + tick.B) * 0.5f;
            Assert.True(Vector3.Distance(mid, start) < 1e-4f || Vector3.Distance(mid, end) < 1e-4f);
            Vector3 tickDir = Vector3.Normalize(tick.B - tick.A);
            Assert.True(MathF.Abs(Vector3.Dot(tickDir, dir)) < 1e-4f, "tick must be ⊥ the dimension line");
        }
    }

    [Fact]
    public void MoveLine_Is_Empty_For_A_Zero_Delta()
    {
        Assert.Empty(TransformIndicatorBuilder.MoveLine(new Vector3(1, 1, 1), new Vector3(1, 1, 1), Color));
    }

    // ---- Rotate arc --------------------------------------------------------------

    [Fact]
    public void RotationArc_Sweeps_The_Requested_Angle_At_The_Requested_Radius()
    {
        var pivot = new Vector3(2, 0, 0);
        Vector3 axis = Vector3.UnitY;
        Vector3 startDir = Vector3.UnitX;
        const float sweep = 90f;
        const float radius = 3f;

        IReadOnlyList<LineSegment> lines = TransformIndicatorBuilder.RotationArc(pivot, axis, startDir, sweep, radius, Color);
        Assert.NotEmpty(lines);

        // The last two segments are the spokes: pivot → arc start, pivot → arc end.
        LineSegment startSpoke = lines[^2];
        LineSegment endSpoke = lines[^1];
        Assert.Equal(pivot, startSpoke.A);
        Assert.Equal(pivot, endSpoke.A);
        Assert.True(Vector3.Distance(startSpoke.B, pivot + (startDir * radius)) < 1e-3f);

        // End spoke = startDir rotated +90° around +Y (right-handed): X → -Z.
        var expectedEnd = pivot + (new Vector3(0, 0, -1) * radius);
        Assert.True(Vector3.Distance(endSpoke.B, expectedEnd) < 1e-3f,
            $"arc end {endSpoke.B} should be {expectedEnd}");

        // Every arc vertex sits on the circle (radius from pivot) in the plane ⊥ axis.
        foreach (LineSegment seg in lines.Take(lines.Count - 2))
        {
            Assert.True(MathF.Abs(Vector3.Distance(seg.A, pivot) - radius) < 1e-3f);
            Assert.True(MathF.Abs(Vector3.Dot(seg.A - pivot, axis)) < 1e-3f);
        }
    }

    [Fact]
    public void RotationArc_Is_Empty_For_A_Zero_Sweep()
    {
        Assert.Empty(TransformIndicatorBuilder.RotationArc(
            Vector3.Zero, Vector3.UnitY, Vector3.UnitX, 0f, 2f, Color));
    }

    [Fact]
    public void RotateAround_Matches_The_Gizmos_SignedAngle_Convention()
    {
        // GizmoMath.SignedAngle(prev, curr, axis) = atan2((prev×curr)·axis, prev·curr):
        // rotating by +θ via RotateAround must measure back as +θ.
        Vector3 v = Vector3.UnitX;
        Vector3 axis = Vector3.UnitY;
        Vector3 rotated = TransformIndicatorBuilder.RotateAround(v, axis, 30f);

        var prev = new CoreVec3(v.X, v.Y, v.Z);
        var curr = new CoreVec3(rotated.X, rotated.Y, rotated.Z);
        float measured = GizmoMath.SignedAngle(prev, curr, new CoreVec3(0, 1, 0)) * 180f / MathF.PI;
        Assert.True(MathF.Abs(measured - 30f) < 1e-2f, $"expected +30°, measured {measured}°");
    }

    // ---- Labels ------------------------------------------------------------------

    [Fact]
    public void Label_Formats_Match_The_Spec()
    {
        Assert.Equal("5.0 M", TransformIndicatorBuilder.FormatDistance(5f));
        Assert.Equal("12.25 M", TransformIndicatorBuilder.FormatDistance(12.25f));
        Assert.Equal("45°", TransformIndicatorBuilder.FormatAngle(45f));
        Assert.Equal("-22.5°", TransformIndicatorBuilder.FormatAngle(-22.5f));
        Assert.Equal("150%", TransformIndicatorBuilder.FormatScale(1.5f));
        Assert.Equal("50%", TransformIndicatorBuilder.FormatScale(0.5f));
    }

    [Fact]
    public void Percent_Glyph_Renders_Non_Blank()
    {
        // '%' was added to the label font for the scale indicator — it must rasterize to
        // actual white glyph pixels, not the blank cell unknown characters get.
        (int w, int h, byte[] rgba) = LabelBitmap.Render("%", scale: 1, pad: 0);
        Assert.True(w >= LabelBitmap.GlyphWidth && h >= LabelBitmap.GlyphHeight);
        bool anyGlyphPixel = false;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 255 && rgba[i + 3] == 255)
            {
                anyGlyphPixel = true;
                break;
            }
        }

        Assert.True(anyGlyphPixel, "the % glyph rendered blank");
    }

    // ---- Offscreen artifacts (mid-drag frame per tool) ----------------------------

    [Fact]
    public void Indicator_Mid_Drag_RenderArtifacts()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        string dir = Path.Combine(RenderTestSupport.ArtifactsDir, "transform_indicators");
        Directory.CreateDirectory(dir);

        var camera = new Camera { Position = new Vector3(6, 6, -8), AspectRatio = 640f / 480f };
        camera.LookAt(camera.Position, Vector3.Zero);
        uint indicator = Palette.Rgba(255, 220, 80, 255);

        // MOVE: brush + dimension line + distance label.
        {
            RenderScene scene = SceneWithBox();
            scene.Lines.AddRange(TransformIndicatorBuilder.MoveLine(Vector3.Zero, new Vector3(4, 0, 0), indicator));
            AddLabel(scene, TransformIndicatorBuilder.FormatDistance(4f), new Vector3(2, 0.5f, 0));
            Save(gd, scene, camera, dir, "indicator_move.png");
        }

        // ROTATE: brush + 45° arc + angle label.
        {
            RenderScene scene = SceneWithBox();
            scene.Lines.AddRange(TransformIndicatorBuilder.RotationArc(
                Vector3.Zero, Vector3.UnitY, Vector3.UnitX, 45f, 4f, indicator));
            AddLabel(scene, TransformIndicatorBuilder.FormatAngle(45f), new Vector3(0, 4.4f, 0));
            Save(gd, scene, camera, dir, "indicator_rotate.png");
        }

        // SCALE: brush + original-bounds ghost + percentage label.
        {
            RenderScene scene = SceneWithBox();
            scene.Lines.AddRange(OverlayBuilder.Box(
                CoreVec3.Zero, Mat3.Identity, new CoreVec3(1.5f, 1.5f, 1.5f), Palette.Rgba(160, 160, 170, 150)));
            AddLabel(scene, TransformIndicatorBuilder.FormatScale(1.5f), new Vector3(0, 2.8f, 0));
            Save(gd, scene, camera, dir, "indicator_scale.png");
        }
    }

    private static RenderScene SceneWithBox()
    {
        var scene = new RenderScene();
        Brush b = BrushFactory.Create(new BrushCreateParams { Width = 3f, Height = 3f, Depth = 3f }, 1);
        BrushEmitter.Append(scene, new[] { b }, BrushPickGranularity.Brush, solidFill: true);
        return scene;
    }

    private static void AddLabel(RenderScene scene, string text, Vector3 pos)
    {
        (int w, int h, byte[] rgba) = LabelBitmap.Render(text, scale: 2, pad: 2);
        string key = "$test:" + text;
        scene.InlineTextures[key] = new InlineTexture(w, h, rgba);
        scene.Billboards.Add(new Billboard(
            BillboardKind.Vertex, pos, 0.5f, Palette.Rgba(255, 255, 255), default,
            TextureName: key, Aspect: h > 0 ? w / (float)h : 1f));
    }

    private static void Save(GraphicsDevice gd, RenderScene scene, Camera camera, string dir, string file)
    {
        byte[] px = OffscreenRenderer.Render(gd, scene, vfs: null, camera, RenderMode.JustTextures, 640, 480);
        Assert.True(RenderTestSupport.IsNonTrivial(px, out int distinct), $"{file} was trivial ({distinct} colors).");
        File.WriteAllBytes(Path.Combine(dir, file), PngWriter.Encode(640, 480, px));
    }
}
