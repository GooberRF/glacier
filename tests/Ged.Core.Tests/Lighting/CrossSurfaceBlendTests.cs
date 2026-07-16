using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Doorway-seam fixture for the cross-surface lightmap seam blend (Alpine
/// <c>-smoothlights</c>, lightmap.cpp:81-110). Two coplanar floor fragments sharing a
/// world edge but split into different rooms get independently-lit lightmaps whose
/// abutting edge texels disagree — the visible seam. These tests prove the blend
/// removes that discontinuity, is correctly gated OFF for the RED-Classic parity path,
/// and never fires for non-adjacent / same-room / non-coplanar pairs.
/// </summary>
public sealed class CrossSurfaceBlendTests
{
    private const int Page = 16;

    // Two 4x4 coplanar floor fragments on the plane z=0 (normal +Z). A is world x in
    // [0,4], B in [4,8], sharing the edge at x=4 — the portal cut under a doorway. They
    // sit in different atlas columns (A at X=0, B at X=6) with a gutter between, so
    // border replication does not cross-contaminate the measured edges; UvAdd on B maps
    // its texels back to world x [4.5,7.5] so the two fragments still abut in world space.
    private static Surface FragA() => Frag(room: 0, atlasX: 0, uvAddU: 0f, worldX0: 0f);

    private static Surface FragB() => Frag(room: 1, atlasX: 6, uvAddU: 0.125f, worldX0: 4f);

    private static Surface Frag(int room, byte atlasX, float uvAddU, float worldX0)
    {
        return new Surface
        {
            LightmapIndex = 0,
            X = atlasX,
            Y = 0,
            W = 4,
            H = 4,
            XPixelsPerMeter = 1f,
            YPixelsPerMeter = 1f,
            BoundingBox = new Aabb(new Vec3(worldX0, 0f, 0f), new Vec3(worldX0 + 4f, 4f, 0f)),
            Plane = new RfPlane(new Vec3(0f, 0f, 1f), 0f),
            ShouldSmooth = 0,
            UCoefficient = 0, // x
            VCoefficient = 1, // y
            DroppedCoefficient = 2, // z
            UvAdd = new Uv(uvAddU, 0f),
            UvScale = new Uv(1f / Page, 1f / Page),
            RoomIndex = room,
        };
    }

    // Room 0 (bright) contains A's texels; room 1 (dark) contains B's. Non-overlapping,
    // so the per-texel ambient lookup gives each fragment its own room's ambient and the
    // two lightmaps disagree at the shared edge.
    private static AmbientField TwoRooms() => new(
        new Vec3(1f, 1f, 1f),
        new List<Room>
        {
            RoomWith(new Aabb(new Vec3(-1f, -1f, -1f), new Vec3(4f, 5f, 1f)), new RfColor(220, 220, 220, 255)),
            RoomWith(new Aabb(new Vec3(4f, -1f, -1f), new Vec3(9f, 5f, 1f)), new RfColor(40, 40, 40, 255)),
        });

    private static Room RoomWith(Aabb box, RfColor ambient) => new()
    {
        Aabb = box,
        HasAmbientLight = 1,
        AmbientColor = ambient,
    };

    private static LightingOptions AlpineOpts(bool crossRoomBlend) => new()
    {
        Quality = true,
        CrossRoomBlend = crossRoomBlend,
        CastShadows = false,
    };

    private static List<Lightmap> FreshPage() =>
        new() { new Lightmap { Width = Page, Height = Page, Pixels = new byte[Page * Page * 3] } };

    private static (List<Lightmap> Pages, BakeStats Stats) Bake(LightingOptions opts, params Surface[] surfaces)
    {
        var input = new List<SurfaceBake>();
        foreach (Surface s in surfaces)
        {
            input.Add(new SurfaceBake(s, fullBright: false));
        }

        List<Lightmap> pages = FreshPage();
        OccluderBvh occ = OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());
        BakeStats stats = Lightmapper.Bake(input, pages, new List<EngineLight>(), occ, TwoRooms(), opts);
        return (pages, stats);
    }

    // Sum |R| difference between A's right-edge atlas column and B's left-edge atlas column,
    // over the four shared rows — the cross-surface discontinuity at the doorway seam.
    private static int SeamDiscontinuity(Lightmap page, Surface a, Surface b)
    {
        int stride = page.Width * 3;
        int ax = a.X + a.W - 1; // A's rightmost texel column
        int bx = b.X;           // B's leftmost texel column
        int sum = 0;
        for (int row = 0; row < a.H; row++)
        {
            int oa = ((a.Y + row) * stride) + (ax * 3);
            int ob = ((b.Y + row) * stride) + (bx * 3);
            sum += Math.Abs(page.Pixels[oa] - page.Pixels[ob]);
        }

        return sum;
    }

    [Fact]
    public void Blend_On_Reduces_Doorway_Seam_Discontinuity()
    {
        Surface a = FragA(), b = FragB();
        (List<Lightmap> off, BakeStats offStats) = Bake(AlpineOpts(crossRoomBlend: false), a, b);
        (List<Lightmap> on, BakeStats onStats) = Bake(AlpineOpts(crossRoomBlend: true), a, b);

        int seamOff = SeamDiscontinuity(off[0], a, b);
        int seamOn = SeamDiscontinuity(on[0], a, b);

        Assert.True(seamOff > 0, "fixture must actually produce a seam with the blend off");
        Assert.Equal(0, offStats.SeamTexelsBlended);
        Assert.True(onStats.SeamTexelsBlended >= 4, $"expected >=4 edge texels blended, got {onStats.SeamTexelsBlended}");
        Assert.True(seamOn < seamOff, $"blend should reduce the seam: off={seamOff} on={seamOn}");
        // The averaged edge texels should be (near) continuous — well under a quarter of the raw seam.
        Assert.True(seamOn <= seamOff / 4, $"blend should nearly close the seam: off={seamOff} on={seamOn}");
    }

    [Fact]
    public void Default_Options_Do_Not_Blend()
    {
        Surface a = FragA(), b = FragB();

        // Default options = the RED-Classic parity path: CrossRoomBlend is off, so the seam is
        // preserved byte-for-byte (matching RED's own seam-carrying references). The blend only
        // fires when the author enables it (WithMethod sets CrossRoomBlend from LightingMethod.SeamBlend).
        var def = new LightingOptions { CastShadows = false };
        Assert.False(def.CrossRoomBlend);

        (List<Lightmap> defPages, BakeStats defStats) = Bake(def, a, b);
        (List<Lightmap> offPages, _) = Bake(AlpineOpts(crossRoomBlend: false), a, b);

        Assert.Equal(0, defStats.SeamTexelsBlended);
        Assert.Equal(offPages[0].Pixels, defPages[0].Pixels);
    }

    [Fact]
    public void WithMethod_SeamBlend_Enables_The_Blend()
    {
        var opts = new LightingOptions { CastShadows = false };
        opts.WithMethod(new LightingMethod { Base = LightingBase.RedClassic, SeamBlend = true });
        Assert.True(opts.CrossRoomBlend); // RED Classic + Seam Blend closes the seam (Alpine -smoothlights)

        Surface a = FragA(), b = FragB();
        (List<Lightmap> pages, BakeStats stats) = Bake(opts, a, b);
        Assert.True(stats.SeamTexelsBlended >= 4);
        (List<Lightmap> off, _) = Bake(AlpineOpts(crossRoomBlend: false), a, b);
        Assert.True(SeamDiscontinuity(pages[0], a, b) < SeamDiscontinuity(off[0], a, b));
    }

    [Fact]
    public void Apply_Makes_Shared_Edge_Texels_Equal()
    {
        Surface a = FragA(), b = FragB();
        var mappers = new[] { new SurfaceTexelMapper(a, Page, Page), new SurfaceTexelMapper(b, Page, Page) };
        var widths = new[] { (int)a.W, (int)b.W };
        var heights = new[] { (int)a.H, (int)b.H };

        // A uniformly bright (0.8), B uniformly dark (0.2).
        float[] ba = Fill(a.W * a.H, 0.8f);
        float[] bb = Fill(b.W * b.H, 0.2f);
        var buffers = new[] { ba, bb };
        var input = new List<SurfaceBake> { new(a, false), new(b, false) };

        int blended = CrossSurfaceBlend.Apply(input, buffers, mappers, widths, heights);

        Assert.Equal(4, blended); // one pair per shared row, matched once from each side
        for (int row = 0; row < a.H; row++)
        {
            int oa = ((row * a.W) + (a.W - 1)) * 3; // A right edge
            int ob = (row * b.W) * 3;               // B left edge
            Assert.Equal(0.5f, ba[oa], 3);
            Assert.Equal(0.5f, bb[ob], 3);
        }

        // Interior columns are untouched.
        Assert.Equal(0.8f, ba[0], 3);
        Assert.Equal(0.2f, bb[(b.W - 1) * 3], 3);
    }

    [Fact]
    public void Same_Room_Or_Non_Adjacent_Pairs_Are_Not_Blended()
    {
        // Same room: both room 0.
        Surface a = FragA();
        Surface bSameRoom = Frag(room: 0, atlasX: 6, uvAddU: 0.125f, worldX0: 4f);
        Assert.Equal(0, ApplyCount(a, bSameRoom));

        // Non-adjacent: B shifted far away in world space (no shared edge).
        Surface bFar = Frag(room: 1, atlasX: 6, uvAddU: -3.875f, worldX0: 68f); // world x [68,72]
        Assert.Equal(0, ApplyCount(FragA(), bFar));
    }

    private static int ApplyCount(Surface a, Surface b)
    {
        var mappers = new[] { new SurfaceTexelMapper(a, Page, Page), new SurfaceTexelMapper(b, Page, Page) };
        var widths = new[] { (int)a.W, (int)b.W };
        var heights = new[] { (int)a.H, (int)b.H };
        var buffers = new[] { Fill(a.W * a.H, 0.8f), Fill(b.W * b.H, 0.2f) };
        var input = new List<SurfaceBake> { new(a, false), new(b, false) };
        return CrossSurfaceBlend.Apply(input, buffers, mappers, widths, heights);
    }

    private static float[] Fill(int texels, float v)
    {
        var buf = new float[texels * 3];
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] = v;
        }

        return buf;
    }
}
