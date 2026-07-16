using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// The selected-decal facing face: ONE filled, semi-transparent quad on the side the decal's
/// projection aims at (the +forward face of the extents box), rendered like a flat portal-face
/// quad. Selection-gated; absent when the decal is not selected.
/// </summary>
public sealed class DecalFacingFaceTests
{
    // A decal at the origin whose forward axis is +X, extents (2,2,1): the facing face lies at
    // x = +0.5 (half the forward/Z extent), spanning the right (+Z) and up (+Y) axes.
    private static readonly Mat3 ForwardX = new(
        new Vec3(1, 0, 0),   // forward = +X
        new Vec3(0, 0, 1),   // right   = +Z
        new Vec3(0, 1, 0));  // up      = +Y

    private static RflFile DecalLevel(int uid = 7)
    {
        var decals = new DecalsSection();
        decals.Decals.Add(new Decal
        {
            Header = new ObjectHeader { Uid = uid, Position = new Vec3(0, 0, 0), Rotation = ForwardX },
            Extents = new Vec3(2, 2, 1),
            Texture = "decal1.tga",
        });
        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)SectionType.Decals, Array.Empty<byte>()) { Content = decals, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static GeometryBatch? FacingFaceBatch(RenderScene scene) =>
        scene.Batches.FirstOrDefault(b => b.Pass == RenderPass.Alpha && b.TextureName.Length == 0 && b.Vertices.Count == 4);

    [Fact]
    public void Selected_Decal_Emits_One_Face_On_The_Forward_Projection_Side()
    {
        RflFile file = DecalLevel(uid: 7);
        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions { SelectedDecalUids = new HashSet<int> { 7 } });

        GeometryBatch? face = FacingFaceBatch(scene);
        Assert.NotNull(face);

        // All four corners sit on the +forward side (x = +0.5), NOT on the −forward side.
        Assert.All(face!.Vertices, v => Assert.True(v.Position.X > 0.4f, $"vertex on wrong side: {v.Position}"));
        Assert.Equal(6, face.Indices.Count); // two triangles (a quad)

        // Semi-transparent (a highlight, not an opaque occluder).
        Assert.True(face.Tint.W is > 0f and < 1f);
    }

    [Fact]
    public void Unselected_Decal_Has_No_Facing_Face()
    {
        RflFile file = DecalLevel(uid: 7);

        // Nothing selected.
        Assert.Null(FacingFaceBatch(SceneBuilder.Build(file, new SceneBuildOptions())));

        // A different uid selected.
        Assert.Null(FacingFaceBatch(SceneBuilder.Build(file, new SceneBuildOptions { SelectedDecalUids = new HashSet<int> { 999 } })));
    }
}
