using System.IO;
using System.Numerics;
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
/// Gates for the stock three-way portal-face draw mode (View menu): the scene
/// builder emits portal faces per mode into the right pass with the portal tint —
/// None (hidden), See-thru (alpha pass, ~35% alpha) and Non-see-thru (opaque pass,
/// full alpha) — and a three-PNG artifact renders the same scene in all three modes.
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class PortalFaceRenderTests
{
    private static readonly uint PortalColor = Palette.Rgba(0x40, 0xE0, 0xD0, 255);

    [Fact]
    public void None_Mode_Emits_No_Portal_Batch()
    {
        RenderScene scene = Build(PortalFaceDrawMode.None);
        Assert.DoesNotContain(scene.Batches, b => b.IsPortal);
        // The opaque wall behind the portal is still emitted.
        Assert.Contains(scene.Batches, b => !b.IsPortal && b.Pass == RenderPass.Opaque);
    }

    [Fact]
    public void SeeThru_Mode_Emits_Translucent_Portal_In_Alpha_Pass()
    {
        RenderScene scene = Build(PortalFaceDrawMode.SeeThru);
        GeometryBatch portal = Assert.Single(scene.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Alpha, portal.Pass);
        Assert.Equal(0.35f, portal.Tint.W, 3);          // ~35% alpha
        Assert.Equal(0x40 / 255f, portal.Tint.X, 3);    // portal-brush tint (R)
        Assert.Equal(0xE0 / 255f, portal.Tint.Y, 3);    // (G)
        Assert.Equal(0xD0 / 255f, portal.Tint.Z, 3);    // (B)
        Assert.Equal(8, portal.Vertices.Count);         // BOTH portal quads (texture −1 + pip2 form)
        Assert.Equal(string.Empty, portal.TextureName); // textured portal's texture is dropped (flat tint)
    }

    [Fact]
    public void Opaque_Mode_Emits_Solid_Portal_In_Opaque_Pass()
    {
        RenderScene scene = Build(PortalFaceDrawMode.Opaque);
        GeometryBatch portal = Assert.Single(scene.Batches, b => b.IsPortal);
        Assert.Equal(RenderPass.Opaque, portal.Pass);
        Assert.Equal(1.0f, portal.Tint.W, 3);           // full alpha
        Assert.Equal(0x40 / 255f, portal.Tint.X, 3);
        Assert.Equal(8, portal.Vertices.Count);         // both marker forms drawn
    }

    [Fact]
    public void Boolean_Shim_Maps_To_SeeThru()
    {
        var options = new SceneBuildOptions { IncludePortalFaces = true };
        Assert.Equal(PortalFaceDrawMode.SeeThru, options.PortalFaces);
        Assert.True(options.IncludePortalFaces);

        options.PortalFaces = PortalFaceDrawMode.None;
        Assert.False(options.IncludePortalFaces);
    }

    [Fact]
    public void Renders_All_Three_Modes_To_Artifacts()
    {
        // Render the community level that exposed the bug (portal faces marked only by
        // portal_index_plus_2, not texture −1) so the artifacts show the fix: None hides
        // them, See-thru tints them translucent, Opaque tints them solid. Falls back to
        // the synthetic level when the corpus is unavailable.
        string? corpus = RenderTestSupport.CorpusFile("ctfstockintradeb1.rfl");

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        RflFile CorpusLevel() => RflFile.Load(corpus!);

        foreach ((PortalFaceDrawMode mode, string name) in new[]
        {
            (PortalFaceDrawMode.None, "portal_faces_none"),
            (PortalFaceDrawMode.SeeThru, "portal_faces_seethru"),
            (PortalFaceDrawMode.Opaque, "portal_faces_opaque"),
        })
        {
            Camera camera;
            RenderScene scene;
            if (corpus is not null)
            {
                RflFile level = CorpusLevel();
                scene = SceneBuilder.Build(level, new SceneBuildOptions
                {
                    IncludeObjects = false,
                    IncludeMovers = false,
                    PortalFaces = mode,
                    PortalFaceColor = PortalColor,
                });

                // Frame the largest portal face head-on so the mode difference is
                // visible in the artifact (the player-start view may face no portal).
                camera = new Camera();
                if (FramePortalFace(level) is (Vector3 eye, Vector3 target))
                {
                    camera.LookAt(eye, target);
                }
                else
                {
                    camera.LookAt(scene.SuggestedCameraPosition, scene.SuggestedCameraTarget);
                }
            }
            else
            {
                scene = Build(mode);
                camera = new Camera { Position = new Vector3(0f, 0f, 0f), Yaw = 0f, Pitch = 0f };
            }

            // Room-colour shading so the untextured world geometry is visible for context
            // (the corpus level renders with no VFS); the portal tint draws on top.
            RenderMode renderMode = corpus is not null ? RenderMode.RoomColors : RenderMode.JustTextures;
            byte[] px = OffscreenRenderer.Render(gd, scene, null, camera, renderMode, 480, 360);
            File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, name + ".png"),
                PngWriter.Encode(480, 360, px));
        }
    }

    private static RenderScene Build(PortalFaceDrawMode mode) => SceneBuilder.Build(PortalLevel(),
        new SceneBuildOptions
        {
            IncludeObjects = false,
            IncludeMovers = false,
            PortalFaces = mode,
            PortalFaceColor = PortalColor,
        });

    /// <summary>
    /// Picks the largest portal face (by vertex count then AABB extent) and returns a
    /// camera (eye, target) framing it head-on along its plane normal, or null when
    /// the level carries no portal face.
    /// </summary>
    private static (Vector3 Eye, Vector3 Target)? FramePortalFace(RflFile file)
    {
        file.ParseAllKnownSections();
        Geometry? geo = null;
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                geo = g.Geometry;
                break;
            }
        }

        if (geo is null)
        {
            return null;
        }

        Face? best = null;
        float bestExtent = -1f;
        foreach (Face f in geo.Faces)
        {
            if (!f.IsPortalFace || f.Vertices.Count < 3)
            {
                continue;
            }

            (Vector3 min, Vector3 max) = FaceBounds(geo, f);
            float extent = (max - min).Length();
            if (extent > bestExtent)
            {
                bestExtent = extent;
                best = f;
            }
        }

        if (best is null)
        {
            return null;
        }

        (Vector3 bmin, Vector3 bmax) = FaceBounds(geo, best);
        Vector3 centroid = (bmin + bmax) * 0.5f;
        var n = Vector3.Normalize(new Vector3(best.Plane.Normal.X, best.Plane.Normal.Y, best.Plane.Normal.Z));
        float dist = MathF.Max(4f, bestExtent * 1.2f);
        return (centroid + (n * dist), centroid);
    }

    private static (Vector3 Min, Vector3 Max) FaceBounds(Geometry geo, Face f)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (FaceVertex fv in f.Vertices)
        {
            if (fv.Index >= 0 && fv.Index < geo.Vertices.Count)
            {
                Vec3 v = geo.Vertices[fv.Index];
                var p = new Vector3(v.X, v.Y, v.Z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        return (min, max);
    }

    /// <summary>
    /// A minimal level: a textured wall quad at z=7 with, in front of it, two portal
    /// faces — one marked the classic way (texture −1) at z=5 and one the
    /// community-map way (REAL texture index + portal_index_plus_2 ≥ 2) at z=6 —
    /// all facing the camera at the origin. Covers both halves of the RED portal
    /// predicate without needing the corpus.
    /// </summary>
    private static RflFile PortalLevel()
    {
        var geo = new Geometry();
        geo.Textures.Add("wall.tga");

        // Portal quad (texture −1, z=5) verts 0..3; textured portal quad
        // (portal_index_plus_2, z=6) verts 4..7; wall quad (z=7) verts 8..11.
        AddQuad(geo, 5f);
        AddQuad(geo, 6f);
        AddQuad(geo, 7f);

        var normal = new Vec3(0f, 0f, -1f);
        // Opaque wall face (textured).
        geo.Faces.Add(new Face
        {
            Texture = 0,
            SurfaceIndex = -1,
            RoomIndex = 0,
            Plane = new RfPlane(normal, 0f),
            Vertices = Quad(8),
        });
        // Portal face marker 1: texture −1.
        geo.Faces.Add(new Face
        {
            Texture = -1,
            SurfaceIndex = -1,
            RoomIndex = 0,
            Plane = new RfPlane(normal, 0f),
            Vertices = Quad(0),
        });
        // Portal face marker 2: REAL texture index + portal_index_plus_2 (the
        // community-map form that the old Texture<0-only test missed).
        geo.Faces.Add(new Face
        {
            Texture = 0,
            PortalIndexPlus2 = 2,
            SurfaceIndex = -1,
            RoomIndex = 0,
            Plane = new RfPlane(normal, 0f),
            Vertices = Quad(4),
        });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "portaltest";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        rfl.Sections.Insert(0, new RflSection((uint)SectionType.StaticGeometry, System.Array.Empty<byte>())
        {
            Content = new GeometrySection { Geometry = geo },
            Dirty = true,
        });
        return rfl;
    }

    private static void AddQuad(Geometry geo, float z)
    {
        geo.Vertices.Add(new Vec3(-3f, -3f, z));
        geo.Vertices.Add(new Vec3(3f, -3f, z));
        geo.Vertices.Add(new Vec3(3f, 3f, z));
        geo.Vertices.Add(new Vec3(-3f, 3f, z));
    }

    private static System.Collections.Generic.List<FaceVertex> Quad(int baseIndex) => new()
    {
        new FaceVertex { Index = baseIndex, TextureCoords = new Uv(0f, 1f) },
        new FaceVertex { Index = baseIndex + 1, TextureCoords = new Uv(1f, 1f) },
        new FaceVertex { Index = baseIndex + 2, TextureCoords = new Uv(1f, 0f) },
        new FaceVertex { Index = baseIndex + 3, TextureCoords = new Uv(0f, 0f) },
    };
}
