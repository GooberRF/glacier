using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Editing.Organic;
using Xunit;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The RB4 reference-reading tools and scorer rules: HSV matching with the per-colour report, hole-free pieces and zone
/// splits sharing exact seams, polygon rasterising (mask editing), and the scorer's reachable-floor rule.
/// </summary>
public sealed class ReferenceMaskToolsTests
{
    // 200 x 200: grey L-shaped floor (x 20-120 by y 20-60, x 20-60 by y 20-180) with a black 10 x 10 pillar, on red rock.
    private static RefImage LImage()
    {
        const int W = 200, H = 200;
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                bool floor = (x >= 20 && x < 120 && y >= 20 && y < 60) || (x >= 20 && x < 60 && y >= 20 && y < 180);
                bool pillar = x >= 30 && x < 40 && y >= 100 && y < 110;
                (byte r, byte g, byte b) = pillar ? ((byte)0, (byte)0, (byte)0) : floor ? ((byte)128, (byte)128, (byte)128) : ((byte)120, (byte)40, (byte)30);
                int o = ((y * W) + x) * 4;
                (px[o], px[o + 1], px[o + 2], px[o + 3]) = (r, g, b, 255);
            }
        }

        return RefImage.FromRgba(W, H, px, "l.png");
    }

    private static RefMaskOptions Opts(params (byte, byte, byte)[] colors) => new() { Colors = colors, Close = 0, Open = 0, FillHoles = 0, MinArea = 10 };

    [Fact]
    public void Hsv_Mode_Separates_Grey_From_Red_And_Reports_Each_Colour()
    {
        RefImage img = LImage();
        RefMask hsv = RefMask.Segment(img, new RefMaskOptions { Colors = new[] { ((byte)130, (byte)125, (byte)125) }, Hsv = true, SatTol = 0.1, ValTol = 0.2, Close = 0, Open = 0, FillHoles = 0, MinArea = 10 });
        int floorPx = (100 * 40) + (40 * 120) - 100;
        Assert.Equal(floorPx, hsv.Count);
        Assert.Equal(floorPx, hsv.Matches[0]);
        Assert.Equal(0, hsv.BorderMatches[0]);

        // The rock colour matches most of the picture and all of its border: the report says so.
        RefMask rock = RefMask.Segment(img, new RefMaskOptions { Colors = new[] { ((byte)120, (byte)40, (byte)30) }, Hsv = true, Close = 0, Open = 0, FillHoles = 0, MinArea = 10 });
        Assert.True((double)rock.Matches[0] / rock.WindowPixels > 0.5);
        Assert.Equal(rock.BorderPixels, rock.BorderMatches[0]);

        // An explicit grey box finds the same floor.
        RefMask box = RefMask.Segment(img, new RefMaskOptions { Hsv = true, Ranges = new[] { new HsvRange(null, null, 0, 0.1, 0.3, 0.7) }, Close = 0, Open = 0, FillHoles = 0, MinArea = 10 });
        Assert.Equal(floorPx, box.Count);
    }

    [Fact]
    public void Cut_Holes_Gives_Hole_Free_Pieces_With_The_Same_Area_And_Shared_Seams()
    {
        RefMask m = RefMask.Segment(LImage(), Opts((128, 128, 128)));
        RefPolygon whole = Assert.Single(m.Polygons(0.5));
        Assert.Single(whole.Holes);

        List<(int Zone, RefPolygon Poly)> pieces = m.ZonePolygons(new int[m.M.Length], 0.5, 64, cutHoles: true);
        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.Empty(p.Poly.Holes));
        Assert.Equal(whole.AreaPx, pieces.Sum(p => p.Poly.AreaPx), 3);

        // The cut row (through the pillar's middle, y = 105) is an edge of the pieces on both sides.
        Assert.Contains(pieces, p => p.Poly.Outer.Count(q => q.Y == 105) >= 2 && p.Poly.Outer.Min(q => q.Y) < 105);
        Assert.Contains(pieces, p => p.Poly.Outer.Count(q => q.Y == 105) >= 2 && p.Poly.Outer.Max(q => q.Y) > 105);
    }

    [Fact]
    public void Zone_Split_Pieces_Meet_On_The_Zone_Line()
    {
        RefMask m = RefMask.Segment(LImage(), Opts((128, 128, 128)));
        var zone = new int[m.M.Length];
        for (int p = 0; p < zone.Length; p++)
        {
            zone[p] = p / m.Width < 80 ? 0 : 1; // north / south of picture row 80
        }

        List<(int Zone, RefPolygon Poly)> pieces = m.ZonePolygons(zone, 0.5, 64, cutHoles: false);
        RefPolygon north = Assert.Single(pieces, p => p.Zone == 0).Poly;
        RefPolygon south = Assert.Single(pieces, p => p.Zone == 1).Poly;
        Assert.Contains((20.0, 80.0), north.Outer);
        Assert.Contains((60.0, 80.0), north.Outer);
        Assert.Contains((20.0, 80.0), south.Outer);
        Assert.Contains((60.0, 80.0), south.Outer);
        Assert.Single(south.Holes); // holes = "keep": the pillar stays a hole
    }

    [Fact]
    public void Shapes_Fill_And_Erase_Edit_A_Mask()
    {
        var m = new bool[100 * 100];
        var square = new RefShape("a", new[] { (10.0, 10.0), (50.0, 10.0), (50.0, 50.0), (10.0, 50.0) }, new[] { (IReadOnlyList<(double X, double Y)>)new[] { (20.0, 20.0), (30.0, 20.0), (30.0, 30.0), (20.0, 30.0) } });
        Assert.Equal((40 * 40) - (10 * 10), ReferenceShapes.Fill(m, 100, 100, square, true));
        Assert.False(m[(25 * 100) + 25]); // the hole stays empty
        int erased = ReferenceShapes.Fill(m, 100, 100, new RefShape(null, new[] { (10.0, 10.0), (50.0, 10.0), (50.0, 20.0), (10.0, 20.0) }, Array.Empty<IReadOnlyList<(double X, double Y)>>()), false);
        Assert.Equal(40 * 10, erased);
        Assert.Equal((40 * 40) - (10 * 10) - (40 * 10), m.Count(b => b));
    }

    [Fact]
    public void Reachable_Floor_Drops_Only_Floor_With_No_Link_To_A_Stand_Point()
    {
        var samples = new List<(int I, int K, FloorPoint P)>();
        for (int i = 0; i < 20; i++)
        {
            for (int k = 0; k < 10; k++)
            {
                samples.Add((i, k, new FloorPoint(0, 4))); // main floor
                if (i >= 5 && i < 10)
                {
                    samples.Add((i, k, new FloorPoint(6, 3))); // a deck 6 m up over it: reached only by a drop, kept
                }
            }
        }

        for (int i = 40; i < 46; i++)
        {
            for (int k = 0; k < 6; k++)
            {
                samples.Add((i, k, new FloorPoint(-60, 3))); // a sealed box far away: dropped
            }
        }

        RecreationFloor f = RecreationFloor.FromSamples(0.5, samples);
        var split = f.Reachable(new[] { new Vec3d(1, 1, 1) });
        Assert.NotNull(split);
        Assert.Equal((20 * 10) + (5 * 10), split.Value.Kept.Count);
        Assert.Equal(36, split.Value.Dropped.Count);
        Assert.Equal(-60, split.Value.Dropped.MinY);
        Assert.Null(f.Reachable(new[] { new Vec3d(500, 0, 500) })); // no stand point on floor: nothing dropped
    }
}
