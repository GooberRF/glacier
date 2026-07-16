using System;
using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Corner light-leak fixtures for the Corner Leak Fix option. Two measured leak classes RED
/// leaves at corners: (1) a smaller bright room's bbox ambient bleeding onto a darker room's
/// corner floor (the per-texel smallest-bbox ambient lookup ignores the authoritative surface
/// room), and (2) a fragment-overhang texel clamped onto its surface's bbox edge starting its
/// shadow ray exactly on a coincident room-boundary wall, so a neighbouring room's light leaks
/// through it. These tests reproduce each leak numerically, prove the option closes it, and
/// prove the default (OFF) path stays byte-identical to the stock RED-Classic bake.
/// </summary>
public sealed class CornerLeakTests
{
    private static Room RoomWith(Aabb box, RfColor amb) => new() { Aabb = box, HasAmbientLight = 1, AmbientColor = amb };
    private static OccluderBvh NoOcc() => OccluderBvh.Build(Array.Empty<(Vec3, Vec3, Vec3)>());

    // ---- Ambient bbox leak ----------------------------------------------------

    // Dark room A (bbox x[0,10] y[0,10] z[0,4], vol 400) and a smaller BRIGHT alcove B
    // (x[8,14] y[8,14] z[0,4], vol 144). Their bboxes overlap in x[8,10] y[8,10]; because B is
    // smaller it wins the smallest-bbox ambient lookup there, so A's own corner floor reads bright.
    private static AmbientField DarkRoomWithBrightOverlap() => new(
        new Vec3(1, 1, 1),
        new List<Room>
        {
            RoomWith(new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 4)), new RfColor(40, 40, 40, 255)),
            RoomWith(new Aabb(new Vec3(8, 8, 0), new Vec3(14, 14, 4)), new RfColor(220, 220, 220, 255)),
        });

    private const int Page = 32;

    // A floor fragment on z=0 filling room A's footprint, assigned to room A (index 0).
    private static Surface FloorInRoomA() => new()
    {
        LightmapIndex = 0, X = 0, Y = 0, W = 16, H = 16,
        BoundingBox = new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 0)),
        Plane = new RfPlane(new Vec3(0, 0, 1), 0f),
        ShouldSmooth = 0, UCoefficient = 0, VCoefficient = 1, DroppedCoefficient = 2,
        UvAdd = new Uv(0.5f / Page, 0.5f / Page), UvScale = new Uv(1f / Page, 1f / Page), RoomIndex = 0,
    };

    private static Lightmap BakeAmbient(bool cornerLeakFix)
    {
        Surface s = FloorInRoomA();
        var page = new Lightmap { Width = Page, Height = Page, Pixels = new byte[Page * Page * 3] };
        var opts = new LightingOptions { CastShadows = false, Quality = true, CornerLeakFix = cornerLeakFix };
        Lightmapper.Bake(new List<SurfaceBake> { new(s, false) }, new[] { page },
            new List<EngineLight>(), NoOcc(), DarkRoomWithBrightOverlap(), opts);
        return page;
    }

    [Fact]
    public void Ambient_Leak_Reproduces_Then_Closes_With_The_Fix()
    {
        Lightmap off = BakeAmbient(cornerLeakFix: false);
        Lightmap on = BakeAmbient(cornerLeakFix: true);
        int stride = Page * 3;
        int R(Lightmap p, int cx, int cy) => p.Pixels[(cy * stride) + (cx * 3)];

        // A corner texel in the overlap (col/row 14 ~ world 9,9) vs an interior texel (col/row 3 ~ world 2,2).
        int cornerOff = R(off, 14, 14), centerOff = R(off, 3, 3);
        int cornerOn = R(on, 14, 14), centerOn = R(on, 3, 3);

        // OFF: the corner reads much brighter than the interior — the leak.
        Assert.True(cornerOff > centerOff + 40, $"fixture must leak: corner {cornerOff} vs center {centerOff}");
        // ON: the corner matches the interior (its own dark room's ambient) — leak closed.
        Assert.Equal(centerOn, cornerOn);
        Assert.True(cornerOn < cornerOff, $"fix must darken the leaked corner: off {cornerOff} on {cornerOn}");
    }

    [Fact]
    public void AmbientField_At_Prefers_Own_Room_Only_When_Asked()
    {
        AmbientField f = DarkRoomWithBrightOverlap();
        var corner = new Vec3(9, 9, 0); // inside both A and B's bbox; B is smaller

        // Stock: smallest-bbox room (B, bright) wins.
        Vec3 stock = f.At(corner, surfaceRoom: 0);
        Assert.True(stock.X > 0.5f);
        // Fix: the surface's own room (A, dark) wins because A's bbox contains the texel.
        Vec3 fix = f.At(corner, surfaceRoom: 0, preferOwnRoom: true);
        Assert.True(fix.X < 0.5f);

        // A texel OUTSIDE the surface's own room still uses the bbox lookup even with the fix
        // (a grouped surface extending past its room keeps the per-texel behaviour).
        var inBOnly = new Vec3(12, 12, 0); // only inside B
        Assert.True(f.At(inBOnly, surfaceRoom: 0, preferOwnRoom: true).X > 0.5f);
    }

    [Fact]
    public void Ambient_Default_Bake_Is_Byte_Identical_To_Fix_Off()
    {
        // Default options: CornerLeakFix defaults OFF — the stock RED-Classic ambient path.
        var def = new LightingOptions { CastShadows = false, Quality = true };
        Assert.False(def.CornerLeakFix);

        Surface s = FloorInRoomA();
        var defPage = new Lightmap { Width = Page, Height = Page, Pixels = new byte[Page * Page * 3] };
        Lightmapper.Bake(new List<SurfaceBake> { new(s, false) }, new[] { defPage },
            new List<EngineLight>(), NoOcc(), DarkRoomWithBrightOverlap(), def);

        Lightmap off = BakeAmbient(cornerLeakFix: false);
        Assert.Equal(off.Pixels, defPage.Pixels);
    }

    // ---- Shadow corner leak ---------------------------------------------------

    private const int SPage = 64;

    // A wall quad at x=5 (plane x=5, z[0,4]) between two rooms; the light is on the far side.
    private static OccluderBvh WallAtX5() => OccluderBvh.Build(new List<(Vec3, Vec3, Vec3)>
    {
        (new Vec3(5, 0, 0), new Vec3(5, 10, 0), new Vec3(5, 10, 4)),
        (new Vec3(5, 0, 0), new Vec3(5, 10, 4), new Vec3(5, 0, 4)),
    });

    // Floor surface in room B on z=0 whose low-x fragment column overhangs the polygon and CLAMPS
    // onto the bbox edge x=5 — the wall plane. UvAdd places col0's planar x at 4.80 (< bbMin 5.0).
    private static Surface FloorClampedToWall() => new()
    {
        LightmapIndex = 0, X = 0, Y = 0, W = 40, H = 40,
        BoundingBox = new Aabb(new Vec3(5, 0, 0), new Vec3(15, 10, 0)),
        Plane = new RfPlane(new Vec3(0, 0, 1), 0f),
        ShouldSmooth = 0, UCoefficient = 0, VCoefficient = 1, DroppedCoefficient = 2,
        UvScale = new Uv(1f / SPage, 1f / SPage), RoomIndex = 1,
        UvAdd = new Uv((0.5f - 4.80f) / SPage, 0.5f / SPage),
    };

    private static EngineLight LightBeyondWall() => new()
    {
        Type = EngineLightType.Point, Position = new Vec3(2, 5, 3), Position2 = new Vec3(2, 5, 3),
        Color = new Vec3(3, 3, 3), Range = 30f, RangeSq = 900f, AttenAlgo = 0, Enabled = true, CastsShadows = true,
    };

    private static Lightmap BakeShadow(bool cornerLeakFix)
    {
        Surface s = FloorClampedToWall();
        var page = new Lightmap { Width = SPage, Height = SPage, Pixels = new byte[SPage * SPage * 3] };
        var field = new AmbientField(new Vec3(0.1f, 0.1f, 0.1f), Array.Empty<Room>());
        var opts = new LightingOptions { CastShadows = true, Quality = false, SmoothIterations = 0, CornerLeakFix = cornerLeakFix };
        Lightmapper.Bake(new List<SurfaceBake> { new(s, false) }, new[] { page },
            new List<EngineLight> { LightBeyondWall() }, WallAtX5(), field, opts);
        return page;
    }

    private static int Col0Max(Lightmap p)
    {
        int stride = SPage * 3, max = 0;
        for (int row = 0; row < 40; row++)
        {
            max = Math.Max(max, p.Pixels[(row * stride) + 0]);
        }

        return max;
    }

    [Fact]
    public void Shadow_Leak_Reproduces_Then_Closes_With_The_Fix()
    {
        int off = Col0Max(BakeShadow(cornerLeakFix: false));
        int on = Col0Max(BakeShadow(cornerLeakFix: true));

        // OFF: the clamped column starts its shadow ray on the wall plane and the wall is missed —
        // the neighbouring room's light leaks fully through (near overbright).
        Assert.True(off > 120, $"fixture must leak through the wall: col0 max {off}");
        // ON: the shadow origin is nudged into room B; the wall occludes → ambient-only (~13).
        Assert.True(on < 30, $"fix must shadow the wall-base column: col0 max {on}");
    }

    [Fact]
    public void Shadow_Default_Bake_Is_Byte_Identical_To_Fix_Off()
    {
        var def = new LightingOptions { CastShadows = true, Quality = false, SmoothIterations = 0 };
        Assert.False(def.CornerLeakFix);

        Surface s = FloorClampedToWall();
        var defPage = new Lightmap { Width = SPage, Height = SPage, Pixels = new byte[SPage * SPage * 3] };
        var field = new AmbientField(new Vec3(0.1f, 0.1f, 0.1f), Array.Empty<Room>());
        Lightmapper.Bake(new List<SurfaceBake> { new(s, false) }, new[] { defPage },
            new List<EngineLight> { LightBeyondWall() }, WallAtX5(), field, def);

        Assert.Equal(BakeShadow(cornerLeakFix: false).Pixels, defPage.Pixels);
    }

    // ---- Composition ----------------------------------------------------------

    [Fact]
    public void WithMethod_Maps_CornerLeakFix_And_Composes_With_Bounced()
    {
        var opts = new LightingOptions();
        opts.WithMethod(new LightingMethod { Base = LightingBase.Bounced, Bounces = 2, CornerLeakFix = true });
        Assert.True(opts.CornerLeakFix);
        Assert.Equal(2, opts.LightBounces); // leak-fix composes with the Bounced base
        Assert.False(opts.IsRedClassicMethod);
    }
}
