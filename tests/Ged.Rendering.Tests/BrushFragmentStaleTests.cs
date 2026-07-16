using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Item 5b: a brush edited since the fragment stash was built (marked stale) must draw
/// its full authored polygons, ignoring the stale survival map / fragment index, while
/// untouched brushes keep their fragment overlay. Proves the per-brush staleness path
/// in <see cref="BrushEmitter"/> so a move of one brush never reverts the others.
/// </summary>
public sealed class BrushFragmentStaleTests
{
    private static Brush Box(int uid, Vec3 pos)
    {
        Brush b = BrushFactory.Create(
            new BrushCreateParams { Shape = BrushShape.Box, Width = 2f, Height = 2f, Depth = 2f }, uid);
        b.Position = pos;
        return b;
    }

    /// <summary>A fragment index that covers <paramref name="uid"/> but reports every face
    /// fully clipped (empty fragment lists) — so normally the brush draws nothing.</summary>
    private static BrushFragmentIndex FullyClipped(int uid, int faceCount) =>
        BrushFragmentIndex.Build(
            new Geometry(),
            new Dictionary<int, int> { [uid] = 0 },
            new Dictionary<int, bool[]> { [uid] = new bool[faceCount] });

    [Fact]
    public void A_Covered_Brush_With_All_Faces_Clipped_Draws_Nothing()
    {
        Brush a = Box(1, new Vec3(0, 0, 0));
        BrushFragmentIndex idx = FullyClipped(1, a.Geometry.Faces.Count);

        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { a }, BrushPickGranularity.Brush, survivingFragments: idx);

        Assert.Empty(scene.Lines); // fully clipped by the (fresh) stash
    }

    [Fact]
    public void A_Stale_Brush_Ignores_The_Fragment_Index_And_Draws_Authored()
    {
        Brush a = Box(1, new Vec3(0, 0, 0));
        BrushFragmentIndex idx = FullyClipped(1, a.Geometry.Faces.Count);

        var scene = new RenderScene();
        BrushEmitter.Append(
            scene, new[] { a }, BrushPickGranularity.Brush,
            survivingFragments: idx, staleFragmentBrushes: new HashSet<int> { 1 });

        Assert.NotEmpty(scene.Lines); // stale → authored box edges drawn
    }

    [Fact]
    public void Marking_One_Brush_Stale_Leaves_The_Other_Brushs_Fragments_Intact()
    {
        Brush a = Box(1, new Vec3(0, 0, 0));
        Brush b = Box(2, new Vec3(20, 0, 0));

        // Both brushes are covered by the stash; both are fully clipped in it.
        BrushFragmentIndex idx = BrushFragmentIndex.Build(
            new Geometry(),
            new Dictionary<int, int> { [1] = 0, [2] = 100 },
            new Dictionary<int, bool[]>
            {
                [1] = new bool[a.Geometry.Faces.Count],
                [2] = new bool[b.Geometry.Faces.Count],
            });

        // Only brush A is stale (just moved). A draws authored; B stays fully clipped.
        var scene = new RenderScene();
        BrushEmitter.Append(
            scene, new[] { a, b }, BrushPickGranularity.Brush,
            survivingFragments: idx, staleFragmentBrushes: new HashSet<int> { 1 });

        // Every drawn line belongs to A (near origin); none near B at x≈20 (B kept its
        // fragment overlay, which is fully clipped → nothing).
        Assert.NotEmpty(scene.Lines);
        foreach (LineSegment seg in scene.Lines)
        {
            Assert.True(seg.A.X < 10f && seg.B.X < 10f, "only the stale brush A should render authored edges");
        }
    }
}
