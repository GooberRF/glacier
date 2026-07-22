using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
/// An authored AIR + Portal brush (ctf06 UID 414, flags Portal|Air) whose 6 faces carry REAL textures
/// must render those textures in the Brush-mode overlay under EVERY View ▸ Portal Faces setting — the
/// air-carve clone survives the CSG solve as real cavity-wall faces, so Object mode shows them under all
/// three settings and the authored overlay (which draws when the compiled world is suppressed in Brush
/// mode) must match, or the faces "vanish" in Brush mode (the reported asymmetry). So the fill is
/// SUBSTANTIAL and essentially mode-INDEPENDENT (View ▸ Portal Faces does not delete or tint these real
/// faces — only genuine texture-less portal membranes obey it). The brush also stays SELECTABLE in every
/// mode: the GPU pick at the brush centre resolves to the brush under None / See-Through / Opaque.
/// (A SOLID portal brush — no Air — is a boolean no-op yielding only a membrane, and STILL obeys View ▸
/// Portal Faces; that direction is guarded at the emission layer in Ged.App.Tests with the real session.)
/// </summary>
[Collection(GpuTestCollection.Name)]
public sealed class AirPortalBrushRenderTests
{
    private const string LevelName = "ctf06.rfl";
    private const int Uid414 = 414;
    private const int W = 480, H = 360;

    private static IReadOnlyList<Brush>? LoadBrushes()
    {
        string? path = RenderTestSupport.CorpusFile(LevelName);
        if (path is null)
        {
            return null;
        }

        RflFile file = RflFile.Load(path);
        file.ParseAllKnownSections();
        return file.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault()?.Brushes;
    }

    private static RenderScene BuildOverlay(Brush b, PortalFaceDrawMode mode)
    {
        // Merged overlay (survival map with no entry for this portal brush, as in a real build).
        var scene = new RenderScene();
        BrushEmitter.Append(scene, new[] { b }, BrushPickGranularity.Brush, solidFill: true,
            survivingFaces: new Dictionary<int, bool[]>(), portalFaces: mode);
        return scene;
    }

    private static int ChangedPixels(byte[] a, byte[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                changed++;
            }
        }

        return changed;
    }

    [Fact]
    public void Ctf06_Uid414_Air_Portal_Real_Faces_Render_In_Every_Mode_And_Stay_Pickable()
    {
        IReadOnlyList<Brush>? brushes = LoadBrushes();
        if (brushes is null)
        {
            return; // corpus unavailable
        }

        Brush? b = brushes.FirstOrDefault(x => x.Uid == Uid414);
        Assert.NotNull(b);
        Assert.NotEqual(0u, (uint)(BrushFlags.Portal & (BrushFlags)b!.Flags)); // it really is a portal brush
        Assert.NotEqual(0u, (uint)(BrushFlags.Air & (BrushFlags)b!.Flags));    // ... and an AIR brush (air-carve)

        using GraphicsDevice? gd = RenderTestSupport.TryCreateDevice(out _);
        if (gd is null)
        {
            return; // no GPU device in this environment
        }

        (Vector3 eye, Vector3 target) = FrameBrush(b);
        var cam = new Camera { AspectRatio = (float)W / H };
        cam.LookAt(eye, target);

        byte[] empty = OffscreenRenderer.Render(gd, new RenderScene(), null, cam, RenderMode.JustTextures, W, H);

        using var renderer = new SceneRenderer(gd);
        using var pickTarget = gd.CreatePickTarget(W, H);

        var fill = new Dictionary<PortalFaceDrawMode, int>();
        foreach (PortalFaceDrawMode mode in new[] { PortalFaceDrawMode.None, PortalFaceDrawMode.SeeThru, PortalFaceDrawMode.Opaque })
        {
            RenderScene scene = BuildOverlay(b, mode);

            // COLOUR: fill grows with the mode; None draws no fill (only the wireframe).
            byte[] img = OffscreenRenderer.Render(gd, scene, null, cam, RenderMode.JustTextures, W, H);
            fill[mode] = ChangedPixels(img, empty);
            File.WriteAllBytes(Path.Combine(RenderTestSupport.ArtifactsDir, $"ctf06_uid414_portal_{mode}.png"),
                PngWriter.Encode(W, H, img));

            // PICK: the brush centre resolves to the brush in EVERY mode — including None.
            using var gpu = new GpuScene(gd, scene, null);
            PickId hit = renderer.RenderPick(cam, gpu, pickTarget, W / 2, H / 2);
            Assert.Equal(PickKind.Brush, hit.Kind);
            Assert.Equal(Uid414, hit.Index);
        }

        // The air+portal brush's REAL textures render under every setting (a substantial fill in each),
        // and the fill is essentially mode-INDEPENDENT — View ▸ Portal Faces does not delete (None) or
        // tint (See-Through/Opaque) these real-textured faces. Pre-fix, None painted almost nothing (the
        // faces were pick-only) while See-Through/Opaque forced a flat teal quad; now all three match the
        // Object-mode compiled cavity walls.
        int none = fill[PortalFaceDrawMode.None];
        int seeThru = fill[PortalFaceDrawMode.SeeThru];
        int opaque = fill[PortalFaceDrawMode.Opaque];
        Assert.True(none > 1000, $"None must draw the real textures (got {none}).");
        Assert.True(seeThru > 1000, $"See-Through must draw the real textures (got {seeThru}).");
        Assert.True(opaque > 1000, $"Opaque must draw the real textures (got {opaque}).");

        // Mode-independent: the largest and smallest painted areas differ only marginally (identical scene
        // for real-textured faces regardless of the portal-faces setting).
        int max = System.Math.Max(none, System.Math.Max(seeThru, opaque));
        int min = System.Math.Min(none, System.Math.Min(seeThru, opaque));
        Assert.True(max - min <= max / 20,
            $"Air+portal real-face fill must be mode-independent (None={none}, SeeThru={seeThru}, Opaque={opaque}).");
    }

    /// <summary>True when any triangle of <paramref name="batch"/> carries the whole-brush pick id of <paramref name="uid"/>.</summary>
    private static bool Covers(GeometryBatch batch, int uid) =>
        batch.Vertices.Any(v => PickId.Decode(v.PickId) is { Kind: PickKind.Brush } p && p.Index == uid);

    private static bool HasRealTexturedFaces(RenderScene scene, int uid) =>
        scene.Batches.Any(b => !b.IsPortal && !b.PickOnly && b.TextureName.Length > 0 && Covers(b, uid));

    [Fact]
    public void Air_Portal_Real_Faces_Emit_As_Real_Textures_In_Every_Mode_Headless()
    {
        // Headless emission-layer proof (no GPU): UID 414's faces emit as REAL-textured batches (not
        // portal, not pick-only) under all three View ▸ Portal Faces settings — and the emitted geometry
        // is mode-INDEPENDENT (the setting no longer touches these real faces).
        IReadOnlyList<Brush>? brushes = LoadBrushes();
        if (brushes is null)
        {
            return;
        }

        Brush? b = brushes.FirstOrDefault(x => x.Uid == Uid414);
        Assert.NotNull(b);
        Assert.NotEqual(0u, (uint)(BrushFlags.Air & (BrushFlags)b!.Flags));

        int? tris = null;
        foreach (PortalFaceDrawMode mode in new[] { PortalFaceDrawMode.None, PortalFaceDrawMode.SeeThru, PortalFaceDrawMode.Opaque })
        {
            RenderScene scene = BuildOverlay(b, mode);
            Assert.True(HasRealTexturedFaces(scene, Uid414), $"UID 414 must emit real-textured faces under {mode}.");

            int realTris = scene.Batches
                .Where(x => !x.IsPortal && !x.PickOnly && x.TextureName.Length > 0 && Covers(x, Uid414))
                .Sum(x => x.Indices.Count / 3);
            tris ??= realTris;
            Assert.Equal(tris.Value, realTris); // mode-independent real geometry
        }

        Assert.True(tris is > 0);
    }

    [Fact]
    public void Solid_Portal_Brush_Still_Obeys_Portal_Faces_Headless()
    {
        // Guard the discriminator (don't over-correct): a SOLID portal brush (Portal, NO Air) is a boolean
        // no-op that yields only a membrane — Object mode shows nothing solid — so its authored faces must
        // STILL obey View ▸ Portal Faces (None → no real fill, pick-only; See-Through/Opaque → portal tint,
        // never a real texture). Built by clearing UID 414's Air flag.
        IReadOnlyList<Brush>? brushes = LoadBrushes();
        if (brushes is null)
        {
            return;
        }

        Brush? b = brushes.FirstOrDefault(x => x.Uid == Uid414);
        Assert.NotNull(b);
        b!.Flags = (uint)((BrushFlags)b.Flags & ~BrushFlags.Air); // now a SOLID portal brush
        Assert.Equal(0u, (uint)(BrushFlags.Air & (BrushFlags)b.Flags));

        // None: no real-textured fill; the brush stays selectable via a pick-only batch.
        RenderScene none = BuildOverlay(b, PortalFaceDrawMode.None);
        Assert.False(HasRealTexturedFaces(none, Uid414));
        Assert.Contains(none.Batches, x => x.PickOnly && Covers(x, Uid414));

        // See-Through / Opaque: a portal-tinted fill, never a real texture.
        foreach (PortalFaceDrawMode mode in new[] { PortalFaceDrawMode.SeeThru, PortalFaceDrawMode.Opaque })
        {
            RenderScene s = BuildOverlay(b, mode);
            Assert.Contains(s.Batches, x => x.IsPortal && Covers(x, Uid414));
            Assert.False(HasRealTexturedFaces(s, Uid414));
        }
    }

    /// <summary>An eye/target framing the brush head-on from a corner of its world AABB.</summary>
    private static (Vector3 Eye, Vector3 Target) FrameBrush(Brush b)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vec3 lv in b.Geometry.Vertices)
        {
            Vector3 w = ToWorld(b, lv);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }

        Vector3 center = (min + max) * 0.5f;
        float extent = MathF.Max((max - min).Length(), 2f);
        Vector3 dir = Vector3.Normalize(new Vector3(0.6f, 0.5f, -0.6f));
        return (center + (dir * extent * 1.6f), center);
    }

    // world = pos + x·Right + y·Up + z·Forward (the BrushEmitter/RF convention).
    private static Vector3 ToWorld(Brush b, Vec3 v)
    {
        Mat3 r = b.Rotation;
        return new Vector3(b.Position.X, b.Position.Y, b.Position.Z)
            + (new Vector3(r.Right.X, r.Right.Y, r.Right.Z) * v.X)
            + (new Vector3(r.Up.X, r.Up.Y, r.Up.Z) * v.Y)
            + (new Vector3(r.Forward.X, r.Forward.Y, r.Forward.Z) * v.Z);
    }
}
