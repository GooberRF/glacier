using System;
using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Backface culling (RED parity). The solid world pass culls back faces by default so a
/// wall seen from behind vanishes, while front faces survive; the "Disable backface
/// culling" option renders both faces. Also verifies, on a real corpus level, that
/// front faces survive from an interior camera (guards the left-handed cull winding).
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class BackfaceCullingRenderTests
{
    private const int Size = 128;

    // The dark editor clear colour (SceneRenderer.ClearColor) as bytes.
    private static readonly (int R, int G, int B) Background = (26, 28, 33);

    private static (int R, int G, int B) CenterPixel(byte[] px)
    {
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        return (px[i], px[i + 1], px[i + 2]);
    }

    private static bool IsBackground((int R, int G, int B) c) =>
        Math.Abs(c.R - Background.R) + Math.Abs(c.G - Background.G) + Math.Abs(c.B - Background.B) < 30;

    private static int NonBackgroundCount(byte[] px)
    {
        int n = 0;
        for (int p = 0; p < px.Length; p += 4)
        {
            var c = (px[p], px[p + 1], px[p + 2]);
            if (!IsBackground(c))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// A single opaque quad at z=5 whose winding front faces -Z (toward a camera at the
    /// origin): the triangle winding order is chosen so the winding-normal points at the
    /// origin camera, matching how a real world wall is authored. A camera in front sees
    /// the front face (survives culling); from behind it sees the back face (culled).
    /// </summary>
    private static RenderScene WallScene()
    {
        var scene = new RenderScene();
        var batch = new GeometryBatch(string.Empty, -1, RenderPass.Opaque);
        Vector3 n = new(0f, 0f, -1f);
        uint col = Palette.Rgba(200, 120, 80);
        uint pick = new PickId(PickKind.Face, 0).Encode();

        void V(float x, float y) => batch.Vertices.Add(new WorldVertex
        {
            Position = new Vector3(x, y, 5f),
            Normal = n,
            TexCoord = Vector2.Zero,
            LightmapCoord = Vector2.Zero,
            Color = col,
            PickId = pick,
        });

        V(-3f, -3f);
        V(3f, -3f);
        V(3f, 3f);
        V(-3f, 3f);
        // Winding-normal -Z (front toward the origin camera): reverse of the naive order.
        batch.Indices.AddRange(new uint[] { 0, 2, 1, 0, 3, 2 });
        scene.Batches.Add(batch);
        return scene;
    }

    [Fact]
    public void Front_Facing_Wall_Survives_Culling()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // Camera at the origin looking +Z at the wall's front face.
        var cam = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        byte[] px = OffscreenRenderer.Render(
            gd, WallScene(), null, cam, RenderMode.JustTextures, Size, Size);

        // Culling is ON by default; the front face must still render (this also pins the
        // left-handed cull winding — if it were inverted, the front face would vanish).
        Assert.False(IsBackground(CenterPixel(px)), "Front-facing wall was wrongly culled.");
    }

    [Fact]
    public void Wall_Seen_From_Behind_Is_Culled()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // Behind the wall (z = 10) looking back toward it (-Z): the camera sees its back.
        var cam = new Camera();
        cam.LookAt(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 5f));
        byte[] px = OffscreenRenderer.Render(
            gd, WallScene(), null, cam, RenderMode.JustTextures, Size, Size);

        Assert.True(IsBackground(CenterPixel(px)), "Back face should be culled (background expected).");
    }

    [Fact]
    public void Wall_From_Behind_Is_Visible_When_Culling_Disabled()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var cam = new Camera();
        cam.LookAt(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 5f));
        byte[] px = OffscreenRenderer.Render(
            gd, WallScene(), null, cam, RenderMode.JustTextures, Size, Size,
            disableBackfaceCulling: true);

        Assert.False(IsBackground(CenterPixel(px)), "Disable-culling should show the back face.");
    }

    [Fact]
    public void Corpus_Interior_Front_Faces_Survive_Culling()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        string? file = RenderTestSupport.CorpusFile("dm01.rfl");
        if (gd is null || file is null)
        {
            return; // needs a device + the example-level corpus
        }

        RflFile level = RflFile.Load(file);
        RenderScene scene = SceneBuilder.Build(level, new SceneBuildOptions { IncludeObjects = false });

        var cam = new Camera();
        cam.LookAt(scene.SuggestedCameraPosition, scene.SuggestedCameraTarget);

        // Room colours so untextured corpus geometry is visible without a VFS.
        byte[] off = OffscreenRenderer.Render(gd, scene, null, cam, RenderMode.RoomColors, Size, Size,
            disableBackfaceCulling: true);
        byte[] on = OffscreenRenderer.Render(gd, scene, null, cam, RenderMode.RoomColors, Size, Size,
            disableBackfaceCulling: false);

        int offCount = NonBackgroundCount(off);
        int onCount = NonBackgroundCount(on);

        // From inside the level the view is dominated by front-facing walls, so culling
        // must NOT gut the image: an inverted winding would drop this to near zero.
        Assert.True(offCount > Size * Size / 5, $"interior view unexpectedly empty ({offCount}px).");
        Assert.True(onCount > offCount / 2, $"culling removed too much ({onCount} vs {offCount}); winding may be inverted.");
    }

    /// <summary>
    /// The wall (face id 0) at z=5 front-faces -Z (toward the origin), so it is BACK-facing
    /// to a camera behind it at z=10; a second surface (face id 1) at z=2 front-faces +Z,
    /// so it faces that camera and sits beyond the wall along the view ray.
    /// </summary>
    private static RenderScene WallWithSurfaceBehind()
    {
        var scene = new RenderScene();

        static void Quad(RenderScene s, float z, Vector3 normal, uint[] indices, int faceId, byte r, byte g, byte b)
        {
            var batch = new GeometryBatch(string.Empty, -1, RenderPass.Opaque);
            uint col = Palette.Rgba(r, g, b);
            uint pick = new PickId(PickKind.Face, faceId).Encode();
            void V(float x, float y) => batch.Vertices.Add(new WorldVertex
            {
                Position = new Vector3(x, y, z),
                Normal = normal,
                TexCoord = Vector2.Zero,
                LightmapCoord = Vector2.Zero,
                Color = col,
                PickId = pick,
            });

            V(-3f, -3f);
            V(3f, -3f);
            V(3f, 3f);
            V(-3f, 3f);
            batch.Indices.AddRange(indices);
            s.Batches.Add(batch);
        }

        // Wall (id 0), front toward -Z (WallScene winding).
        Quad(scene, 5f, new Vector3(0f, 0f, -1f), new uint[] { 0, 2, 1, 0, 3, 2 }, 0, 200, 120, 80);
        // Surface beyond (id 1), front toward +Z (reversed winding) so it faces the z=10 camera.
        Quad(scene, 2f, new Vector3(0f, 0f, 1f), new uint[] { 0, 1, 2, 0, 2, 3 }, 1, 80, 160, 200);
        return scene;
    }

    [Fact]
    public void Pick_Falls_Through_A_Back_Facing_Wall_When_Culling_Is_On()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        const int size = 64;
        using var renderer = new SceneRenderer(gd);
        using var gpu = new GpuScene(gd, WallWithSurfaceBehind(), null);
        using var pick = gd.CreatePickTarget(size, size);

        // Behind the wall (z=10) looking back toward it (-Z): the wall (z=5) is back-facing,
        // and the surface at z=2 lies beyond it along the ray.
        var cam = new Camera();
        cam.LookAt(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 5f));

        // Culling ON (default): the back-facing wall is skipped, so the click picks THROUGH
        // it to the surface beyond (face id 1).
        renderer.DisableBackfaceCulling = false;
        PickId through = renderer.RenderPick(cam, gpu, pick, size / 2, size / 2);
        Assert.Equal(PickKind.Face, through.Kind);
        Assert.Equal(1, through.Index);

        // Culling OFF: the wall's back face is pickable again and, being nearest, wins.
        renderer.DisableBackfaceCulling = true;
        PickId wall = renderer.RenderPick(cam, gpu, pick, size / 2, size / 2);
        Assert.Equal(PickKind.Face, wall.Kind);
        Assert.Equal(0, wall.Index);
    }

    [Fact]
    public void Double_Sided_Mesh_Triangle_Renders_From_Both_Sides()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        // A quad mesh placed at z=5, viewed from the front (origin, +Z) and back (z=10, -Z).
        var front = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        var back = new Camera();
        back.LookAt(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 5f));

        bool SingleVisible(Camera cam) => Visible(RenderMesh(gd, MeshBytes(doubleSided: false), cam));
        bool DoubleVisible(Camera cam) => Visible(RenderMesh(gd, MeshBytes(doubleSided: true), cam));

        bool singleFront = SingleVisible(front);
        bool singleBack = SingleVisible(back);
        bool doubleFront = DoubleVisible(front);
        bool doubleBack = DoubleVisible(back);

        // A single-sided mesh is culled from exactly one side (back-face). The 0x20
        // double-sided triangle renders from BOTH sides regardless of the global cull.
        Assert.True(singleFront ^ singleBack, $"single-sided should show from one side only (front={singleFront}, back={singleBack}).");
        Assert.True(doubleFront && doubleBack, $"double-sided must show from both sides (front={doubleFront}, back={doubleBack}).");
    }

    [Fact]
    public void Pick_Honors_The_Double_Sided_Mesh_Exception_When_Culling()
    {
        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return;
        }

        var front = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        var back = new Camera();
        back.LookAt(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 5f));

        bool SinglePicked(Camera cam) => MeshPicked(gd, MeshBytes(doubleSided: false), cam);
        bool DoublePicked(Camera cam) => MeshPicked(gd, MeshBytes(doubleSided: true), cam);

        // Culling ON (default): a single-sided mesh is pickable from exactly one side
        // (back-face culled from the other); the 0x20 double-sided mesh is pickable from
        // BOTH — the pick pass honors the per-mesh double-sided exception via DrawMeshes.
        Assert.True(SinglePicked(front) ^ SinglePicked(back), "single-sided mesh should pick from one side only");
        Assert.True(DoublePicked(front) && DoublePicked(back), "double-sided mesh must pick from both sides");
    }

    private static bool MeshPicked(GraphicsDevice gd, byte[] meshV3m, Camera cam)
    {
        using var vfs = new Ged.Core.Assets.AssetVfs(new[] { new MeshSource("dstest.v3m", meshV3m) });
        var scene = new RenderScene();
        scene.Meshes.Add(new MeshInstance
        {
            MeshFilename = "dstest.v3m",
            World = Matrix4x4.CreateTranslation(0f, 0f, 5f),
            PickId = new PickId(PickKind.Mesh, 0),
        });

        const int size = 64;
        using var renderer = new SceneRenderer(gd);
        using var gpu = new GpuScene(gd, scene, vfs);
        using var pick = gd.CreatePickTarget(size, size);
        renderer.DisableBackfaceCulling = false; // culling ON
        PickId id = renderer.RenderPick(cam, gpu, pick, size / 2, size / 2);
        return id.Kind == PickKind.Mesh && id.Index == 0;
    }

    private static bool Visible(byte[] px) => NonBackgroundCount(px) > 100;

    private static byte[] RenderMesh(GraphicsDevice gd, byte[] meshV3m, Camera cam)
    {
        using var vfs = new Ged.Core.Assets.AssetVfs(new[] { new MeshSource("dstest.v3m", meshV3m) });
        var scene = new RenderScene();
        scene.Meshes.Add(new MeshInstance
        {
            MeshFilename = "dstest.v3m",
            World = Matrix4x4.CreateTranslation(0f, 0f, 5f),
            PickId = new PickId(PickKind.Mesh, 0),
        });
        return OffscreenRenderer.Render(gd, scene, vfs, cam, RenderMode.JustTextures, Size, Size);
    }

    /// <summary>A ±3 quad in the local XY plane (two triangles), optionally double-sided (flag 0x20).</summary>
    private static byte[] MeshBytes(bool doubleSided)
    {
        var group = new Ged.Core.IO.Mesh.Import.ImportedGroup { Texture = "white.tga" };
        void P(float x, float y)
        {
            group.Positions.Add(new Ged.Core.Model.Vec3(x, y, 0f));
            group.Normals.Add(new Ged.Core.Model.Vec3(0f, 0f, -1f));
            group.TexCoords.Add(new Ged.Core.Model.Uv(0f, 0f));
        }

        P(-3f, -3f);
        P(3f, -3f);
        P(3f, 3f);
        P(-3f, 3f);
        group.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });

        Ged.Core.IO.Mesh.V3dFile file = Ged.Core.IO.Mesh.V3dMeshBuilder.Build("dstest", new[] { group });
        if (doubleSided)
        {
            foreach (Ged.Core.IO.Mesh.V3dSubmesh sm in file.Submeshes)
            {
                foreach (Ged.Core.IO.Mesh.V3dLod lod in sm.Lods)
                {
                    foreach (Ged.Core.IO.Mesh.V3dBatch batch in lod.Batches)
                    {
                        for (int t = 0; t < batch.Triangles.Length; t++)
                        {
                            batch.Triangles[t] = batch.Triangles[t] with { Flags = Ged.Core.IO.Mesh.V3dTriangle.DoubleSided };
                        }
                    }
                }
            }
        }

        return Ged.Core.IO.Mesh.V3dWriter.Write(file);
    }

    /// <summary>A one-file in-memory VFS mount serving a synthetic mesh by name.</summary>
    private sealed class MeshSource : Ged.Core.Assets.IAssetSource
    {
        private readonly string _name;
        private readonly byte[] _data;

        public MeshSource(string name, byte[] data)
        {
            _name = name;
            _data = data;
        }

        public string Description => "in-memory";

        public Ged.Core.Assets.AssetSourceKind Kind => Ged.Core.Assets.AssetSourceKind.LooseDirectory;

        public string? Category => null;

        public bool Contains(string name) => string.Equals(name, _name, StringComparison.OrdinalIgnoreCase);

        public byte[]? Read(string name) => Contains(name) ? _data : null;

        public long? GetSize(string name) => Contains(name) ? _data.Length : null;

        public System.Collections.Generic.IEnumerable<string> EnumerateFiles()
        {
            yield return _name;
        }

        public void Rescan()
        {
        }
    }
}
