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
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Visual evidence for the keyframe-billboard / keyframe-link / arrowhead work: renders a
/// real mover level (dmabrupt) before and after, at an overview and a close-up framed on
/// the keyframes, writing PNGs + a counts summary to tests/artifacts for human inspection.
/// "Before" is the faithful prior output (no keyframe billboards; links drawn as plain
/// lines with no arrowheads and no keyframe links). Skips when the corpus / D3D is absent.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class KeyframeArtifactTests
{
    private const int W = 1100;
    private const int H = 780;

    [Fact]
    public void Dmabrupt_Keyframe_Billboards_And_Arrowed_Links()
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

            // Overview scenes (small billboards so the level reads).
            RenderScene afterWide = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 0.6f });
            int keyframeCount = afterWide.Billboards.Count(b => b.Kind == BillboardKind.Keyframe);
            int lineCount = afterWide.Lines.Count;

            RenderScene beforeWide = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 0.6f });
            beforeWide.Billboards.RemoveAll(b => b.Kind == BillboardKind.Keyframe);
            beforeWide.Lines.Clear();
            AddOldStyleLinks(file, beforeWide);

            // Close-up scenes (larger billboards) framed tightly on one moving group's keyframes,
            // so the gold diamonds and the arrowheads on the link lines are unmistakable.
            RenderScene afterClose = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 1.4f });
            RenderScene beforeClose = SceneBuilder.Build(file, new SceneBuildOptions { BillboardSize = 1.4f });
            beforeClose.Billboards.RemoveAll(b => b.Kind == BillboardKind.Keyframe);
            beforeClose.Lines.Clear();
            AddOldStyleLinks(file, beforeClose);

            Aabb kfBounds = TightestMoverKeyframeBounds(file, afterClose);

            RenderView(gd, afterWide, vfs, afterWide.Bounds, "dmabrupt_after_overview");
            RenderView(gd, beforeWide, vfs, beforeWide.Bounds, "dmabrupt_before_overview");
            RenderView(gd, afterClose, vfs, kfBounds, "dmabrupt_after_closeup");
            RenderView(gd, beforeClose, vfs, kfBounds, "dmabrupt_before_closeup");

            // Top-down wireframe "link map" of the mover region: walls drawn as wireframe so
            // the gold keyframe diamonds and the arrowheads on the link lines are unobstructed.
            RenderOrthoTop(gd, afterClose, vfs, kfBounds, "dmabrupt_after_linkmap");
            RenderOrthoTop(gd, beforeClose, vfs, kfBounds, "dmabrupt_before_linkmap");

            File.WriteAllText(
                Path.Combine(RenderTestSupport.ArtifactsDir, "dmabrupt_keyframe_summary.txt"),
                $"keyframe billboards (after): {keyframeCount}\n" +
                $"link+arrowhead segments (after): {lineCount}\n" +
                $"link segments (before, plain): {beforeWide.Lines.Count}\n" +
                $"closeup bounds: {kfBounds.P1} .. {kfBounds.P2}\n");

            Assert.True(keyframeCount > 0, "Expected mover keyframes to emit billboards.");
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
        File.WriteAllBytes(
            Path.Combine(RenderTestSupport.ArtifactsDir, name + ".png"),
            PngWriter.Encode(W, H, pixels));
    }

    /// <summary>A top-down orthographic wireframe render (an unobstructed link/keyframe map).</summary>
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
        File.WriteAllBytes(
            Path.Combine(RenderTestSupport.ArtifactsDir, name + ".png"),
            PngWriter.Encode(W, H, pixels));
    }

    /// <summary>
    /// A tight, padded AABB around the keyframes of the single moving group with the most
    /// keyframes (a real cluster with a chain link + member-&gt;start links to show), so the
    /// close-up frames one mover rather than the whole spread of movers.
    /// </summary>
    private static Aabb TightestMoverKeyframeBounds(RflFile file, RenderScene scene)
    {
        file.ParseAllKnownSections();
        List<Vector3>? best = null;
        float bestDiag = -1f;
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is not GroupsSection gs)
            {
                continue;
            }

            foreach (Group g in gs.Groups)
            {
                if (g.IsMoving == 0 || g.MovingData is not { } data || data.Keyframes.Count < 2)
                {
                    continue; // need >= 2 keyframes to show a chain link + arrowhead
                }

                var pts = data.Keyframes.Select(k => new Vector3(k.Position.X, k.Position.Y, k.Position.Z)).ToList();
                Vector3 lo = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                Vector3 hi = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                float diag = (hi - lo).Length();

                // Prefer the most separated keyframes that still frame nicely (<= 30 units).
                if (diag > bestDiag && diag <= 30f)
                {
                    bestDiag = diag;
                    best = pts;
                }
            }
        }

        if (best is null || best.Count == 0)
        {
            return scene.Bounds;
        }

        Vector3 min = best.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        Vector3 max = best.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Vector3 pad = Vector3.Max((max - min) * 0.5f, new Vector3(3f));
        min -= pad;
        max += pad;
        return new Aabb(new Vec3(min.X, min.Y, min.Z), new Vec3(max.X, max.Y, max.Z));
    }

    /// <summary>The pre-change link emission: originator .Links as plain lines (no arrowheads, no keyframe links).</summary>
    private static void AddOldStyleLinks(RflFile file, RenderScene scene)
    {
        file.ParseAllKnownSections();
        var pos = scene.Billboards
            .Where(b => b.PickId.Kind == PickKind.Object)
            .GroupBy(b => b.PickId.Index)
            .ToDictionary(g => g.Key, g => g.First().Position);

        void Link(int from, IReadOnlyList<int> links)
        {
            if (!pos.TryGetValue(from, out Vector3 a))
            {
                return;
            }

            foreach (int to in links)
            {
                if (pos.TryGetValue(to, out Vector3 b))
                {
                    scene.Lines.Add(new LineSegment(a, b, Palette.Rgba(255, 220, 80, 200)));
                }
            }
        }

        foreach (RflSection section in file.Sections)
        {
            switch (section.Content)
            {
                case EventsSection s:
                    foreach (RflEvent e in s.Events) { Link(e.Uid, e.Links); }
                    break;
                case TriggersSection s:
                    foreach (Trigger t in s.Triggers) { Link(t.Uid, t.Links); }
                    break;
                case CluttersSection s:
                    foreach (Clutter c in s.Clutters) { Link(c.Header.Uid, c.Links); }
                    break;
                case NavPointsSection s:
                    foreach (NavPoint n in s.NavPoints) { Link(n.Uid, n.Links); }
                    break;
            }
        }
    }
}
