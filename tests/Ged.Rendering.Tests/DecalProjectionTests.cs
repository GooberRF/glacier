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
/// The "Draw Decals" viewport preview: with the toggle on, each decal's texture is projected onto
/// the static geometry it faces (world faces clipped to the decal box, UVs along the forward axis,
/// an alpha-blended depth-biased overlay batch). Default-off, so nothing is emitted otherwise.
/// </summary>
public sealed class DecalProjectionTests
{
    // A decal 2 m above a floor, forward pointing straight down (−Y) so it projects onto the floor,
    // with a footprint (right = X, up = Z; extents 4×4) that covers the whole quad.
    private static readonly Mat3 ForwardDown = new(
        new Vec3(0, -1, 0),  // forward = −Y (aim down at the floor)
        new Vec3(1, 0, 0),   // right   = +X
        new Vec3(0, 0, 1));  // up      = +Z

    private static RflFile FloorWithDecal(bool withGeometry = true)
    {
        var rfl = new RflFile();

        if (withGeometry)
        {
            var geo = new Geometry();
            geo.Textures.Add("floor.tga");
            geo.Vertices.Add(new Vec3(-2, 0, -2));
            geo.Vertices.Add(new Vec3(2, 0, -2));
            geo.Vertices.Add(new Vec3(2, 0, 2));
            geo.Vertices.Add(new Vec3(-2, 0, 2));
            geo.Faces.Add(new Face
            {
                Texture = 0, SurfaceIndex = -1, RoomIndex = 0, Plane = new RfPlane(new Vec3(0, 1, 0), 0f),
                Vertices = new List<FaceVertex>
                {
                    new() { Index = 0, TextureCoords = new Uv(0, 1) },
                    new() { Index = 1, TextureCoords = new Uv(1, 1) },
                    new() { Index = 2, TextureCoords = new Uv(1, 0) },
                    new() { Index = 3, TextureCoords = new Uv(0, 0) },
                },
            });
            rfl.Sections.Add(new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>())
            {
                Content = new GeometrySection { Geometry = geo },
                Dirty = true,
            });
        }

        var decals = new DecalsSection();
        decals.Decals.Add(new Decal
        {
            Header = new ObjectHeader { Uid = 3, Position = new Vec3(0, 2, 0), Rotation = ForwardDown },
            Extents = new Vec3(4, 4, 6),
            Texture = "bullethole.tga",
            Alpha = 255,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.Decals, Array.Empty<byte>()) { Content = decals, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static GeometryBatch? ProjectionBatch(RenderScene scene) =>
        scene.Batches.FirstOrDefault(b => b.Pass == RenderPass.Alpha && b.TextureName == "bullethole.tga" && b.Indices.Count > 0);

    [Fact]
    public void DrawDecals_On_Projects_The_Texture_Onto_The_Faced_Geometry()
    {
        RenderScene scene = SceneBuilder.Build(FloorWithDecal(), new SceneBuildOptions { DrawDecals = true });

        GeometryBatch? proj = ProjectionBatch(scene);
        Assert.NotNull(proj);

        // The projected quad sits on the floor (y ≈ 0, lifted a hair by the depth bias) with UVs in [0,1].
        Assert.All(proj!.Vertices, v => Assert.True(Math.Abs(v.Position.Y) < 0.1f, $"off the floor: {v.Position}"));
        Assert.All(proj.Vertices, v =>
        {
            Assert.InRange(v.TexCoord.X, -0.01f, 1.01f);
            Assert.InRange(v.TexCoord.Y, -0.01f, 1.01f);
        });
    }

    [Fact]
    public void DrawDecals_Off_Emits_No_Projection()
    {
        RenderScene scene = SceneBuilder.Build(FloorWithDecal(), new SceneBuildOptions());
        Assert.Null(ProjectionBatch(scene));
    }

    [Fact]
    public void DrawDecals_On_Without_Static_Geometry_Emits_No_Projection()
    {
        // No compiled geometry to receive the projection → nothing emitted (no floating quads).
        RenderScene scene = SceneBuilder.Build(FloorWithDecal(withGeometry: false), new SceneBuildOptions { DrawDecals = true });
        Assert.Null(ProjectionBatch(scene));
    }

    [Fact]
    public void Projection_Direct_Builder_Clips_To_The_Decal_Footprint()
    {
        // A floor larger than the decal footprint: the projected polygon is clipped to the box
        // footprint (|x| ≤ 2, |z| ≤ 2), not the full floor.
        var geo = new Geometry();
        geo.Vertices.Add(new Vec3(-10, 0, -10));
        geo.Vertices.Add(new Vec3(10, 0, -10));
        geo.Vertices.Add(new Vec3(10, 0, 10));
        geo.Vertices.Add(new Vec3(-10, 0, 10));
        geo.Faces.Add(new Face
        {
            Texture = 0, SurfaceIndex = -1, RoomIndex = 0, Plane = new RfPlane(new Vec3(0, 1, 0), 0f),
            Vertices = new List<FaceVertex>
            {
                new() { Index = 0 }, new() { Index = 1 }, new() { Index = 2 }, new() { Index = 3 },
            },
        });

        var decal = new Decal
        {
            Header = new ObjectHeader { Uid = 3, Position = new Vec3(0, 2, 0), Rotation = ForwardDown },
            Extents = new Vec3(4, 4, 6),
            Texture = "d.tga",
            Alpha = 200,
        };

        var scene = new RenderScene();
        DecalProjectionBuilder.Append(scene, geo, new[] { decal });

        GeometryBatch batch = Assert.Single(scene.Batches);
        Assert.All(batch.Vertices, v =>
        {
            Assert.True(Math.Abs(v.Position.X) <= 2.001f, $"x out of footprint: {v.Position}");
            Assert.True(Math.Abs(v.Position.Z) <= 2.001f, $"z out of footprint: {v.Position}");
        });
        Assert.Equal(200f / 255f, batch.Tint.W, 3);
    }
}
