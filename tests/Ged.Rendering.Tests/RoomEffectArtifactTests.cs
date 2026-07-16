using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Visual evidence for room-effect object-citizenship: renders a real level containing room
/// effects (dmabrupt: a sky room + a liquid room) at an overview and a close-up framed on the
/// room-effect markers, plus a room-effects-only pass so the RoomFX "waves" glyphs are
/// unmistakable. Writes PNGs + a counts summary to tests/artifacts. Skips when the corpus /
/// D3D is absent.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class RoomEffectArtifactTests
{
    private const int W = 1100;
    private const int H = 780;

    [Fact]
    public void Dmabrupt_Room_Effect_Billboards()
    {
        string? path = RenderTestSupport.CorpusFile("dmabruptdecayrc2a27.rfl");
        if (path is null)
        {
            return;
        }

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        gd.SetIconAtlas(IconAtlas.Build());
        AssetVfs? vfs = RenderTestSupport.RfInstall is null ? null : GameMount.Mount(RenderTestSupport.RfInstall);
        try
        {
            RflFile file = RflFile.Load(path);

            RenderScene overview = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 0.7f });
            int roomFxCount = overview.Billboards.Count(b => b.Kind == BillboardKind.RoomEffect);

            // A room-effects-only pass: strip every other billboard so the RoomFX glyphs are unobstructed.
            RenderScene only = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 1.6f });
            only.Billboards.RemoveAll(b => b.Kind != BillboardKind.RoomEffect);
            only.Lines.Clear();

            RenderScene closeUp = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 1.6f });
            Aabb fxBounds = RoomEffectBounds(file);

            RenderView(gd, overview, vfs, overview.Bounds, "roomfx_overview");
            RenderView(gd, closeUp, vfs, fxBounds, "roomfx_closeup");
            RenderView(gd, only, vfs, fxBounds, "roomfx_only_closeup");
            RenderOrthoTop(gd, only, vfs, fxBounds, "roomfx_only_topdown");

            // A tight per-effect shot: a big billboard framed on a single marker so the RoomFX
            // "waves" glyph is unmistakable (the two markers here are ~240 units apart).
            List<Vector3> positions = RoomEffectPositions(file);
            for (int i = 0; i < positions.Count; i++)
            {
                RenderScene single = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 2.6f });
                single.Billboards.RemoveAll(b => b.Kind != BillboardKind.RoomEffect);
                single.Lines.Clear();
                Vector3 p = positions[i];
                var box = new Aabb(new Vec3(p.X - 7, p.Y - 7, p.Z - 7), new Vec3(p.X + 7, p.Y + 7, p.Z + 7));
                RenderView(gd, single, vfs, box, $"roomfx_marker{i}");
            }

            File.WriteAllText(
                Path.Combine(RenderTestSupport.ArtifactsDir, "roomfx_summary.txt"),
                $"room-effect billboards: {roomFxCount}\n" +
                $"closeup bounds: {fxBounds.P1} .. {fxBounds.P2}\n");

            Assert.True(roomFxCount > 0, "Expected room effects to emit billboards.");
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    private static void RenderView(GraphicsDevice gd, RenderScene scene, AssetVfs? vfs, Aabb bounds, string name)
    {
        var camera = new Camera { Projection = CameraProjection.Perspective };
        camera.Frame(bounds);
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.TexturesAndLightmaps, W, H);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, name + ".png"), PngWriter.Encode(W, H, pixels));
    }

    private static void RenderOrthoTop(GraphicsDevice gd, RenderScene scene, AssetVfs? vfs, Aabb bounds, string name)
    {
        Vector3 min = new(bounds.P1.X, bounds.P1.Y, bounds.P1.Z);
        Vector3 max = new(bounds.P2.X, bounds.P2.Y, bounds.P2.Z);
        Vector3 center = (min + max) * 0.5f;
        float aspect = (float)W / H;
        float halfZ = MathF.Max((max.Z - min.Z) * 0.5f, (max.X - min.X) * 0.5f / aspect) * 1.15f;

        var camera = new Camera
        {
            Projection = CameraProjection.Orthographic,
            Ortho = OrthoView.Top,
            Position = center,
            OrthoZoom = MathF.Max(halfZ, 3f),
            AspectRatio = aspect,
        };
        byte[] pixels = OffscreenRenderer.Render(gd, scene, vfs, camera, RenderMode.Wireframe, W, H);
        File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, name + ".png"), PngWriter.Encode(W, H, pixels));
    }

    private static List<Vector3> RoomEffectPositions(RflFile file)
    {
        file.ParseAllKnownSections();
        var pts = new List<Vector3>();
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is RoomEffectsSection re)
            {
                pts.AddRange(re.Effects.Select(e => new Vector3(e.Header.Position.X, e.Header.Position.Y, e.Header.Position.Z)));
            }
        }

        return pts;
    }

    /// <summary>A padded AABB around every room-effect marker position.</summary>
    private static Aabb RoomEffectBounds(RflFile file)
    {
        List<Vector3> pts = RoomEffectPositions(file);

        if (pts.Count == 0)
        {
            return SceneBuilder.Build(file, new SceneBuildOptions()).Bounds;
        }

        Vector3 min = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        Vector3 max = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Vector3 pad = Vector3.Max((max - min) * 0.5f, new Vector3(6f));
        min -= pad;
        max += pad;
        return new Aabb(new Vec3(min.X, min.Y, min.Z), new Vec3(max.X, max.Y, max.Z));
    }
}
