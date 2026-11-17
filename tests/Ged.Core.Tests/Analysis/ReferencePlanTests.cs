using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The reference workflow (ref.* and kit.trace): the picture-to-world calibration, colour masks traced into polygons,
/// the labelled grid, the floor-versus-mask comparison, and kit.trace building a traced plan literally (and refusing
/// overlapping regions, an opening that is not between two regions, and an impossible ramp) with ref.compare closing
/// the loop on the built level.
/// </summary>
public sealed class ReferencePlanTests
{
    private readonly ITestOutputHelper _out;

    public ReferencePlanTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Calibration_Maps_Picture_Down_To_Minus_Z_And_Round_Trips()
    {
        var cal = new RefCalibration(8, 400, 320, 0, 0, 0);
        Assert.Equal((0.0, 0.0), cal.ToWorld(400, 320));
        Assert.Equal((10.0, 0.0), cal.ToWorld(480, 320));
        Assert.Equal((0.0, -10.0), cal.ToWorld(400, 400)); // picture down = world -Z (north up)

        var east = new RefCalibration(8, 400, 320, 5, -3, 90); // picture up = world +X
        (double x, double z) = east.ToWorld(400, 240);
        Assert.Equal(15, x, 6);
        Assert.Equal(-3, z, 6);
        (x, z) = east.ToWorld(480, 320); // picture right = world -Z
        Assert.Equal(5, x, 6);
        Assert.Equal(-13, z, 6);

        var tilted = new RefCalibration(3.7, 12, 34, -20, 41, 33);
        foreach ((double px, double py) in new[] { (0.0, 0.0), (123.4, 56.7), (-40.0, 800.0) })
        {
            (double wx, double wz) = tilted.ToWorld(px, py);
            (double bx, double by) = tilted.ToPx(wx, wz);
            Assert.Equal(px, bx, 6);
            Assert.Equal(py, by, 6);
        }
    }

    [Fact]
    public void Mask_Traces_An_L_With_A_Hole_Into_Exact_Polygons()
    {
        RefImage img = LImage();
        RefMask mask = RefMask.Segment(img, new RefMaskOptions { Colors = new[] { ((byte)128, (byte)128, (byte)128) }, Close = 0, Open = 0, FillHoles = 0, MinArea = 10 });
        List<RefPolygon> polys = mask.Polygons(0.5);
        RefPolygon p = Assert.Single(polys);
        Assert.Equal(6, p.Outer.Count);
        Assert.Equal((100 * 40) + (40 * 120) - 100, p.AreaPx, 3);
        IReadOnlyList<(double X, double Y)> hole = Assert.Single(p.Holes);
        Assert.Equal(4, hole.Count);
        Assert.Contains((20.0, 20.0), p.Outer);
        Assert.Contains((120.0, 60.0), p.Outer);
        Assert.Contains((60.0, 180.0), p.Outer);

        // The grid and the overlay render.
        ReferenceGrid.Result g = ReferenceGrid.Render(img, new[] { 10, 10, 60, 40 }, 4, 0, null);
        Assert.Equal(60 * 4, g.Width - (2 * 34));
        Assert.True(g.Step > 0);
        Assert.NotEmpty(mask.Overlay(img, polys));
    }

    [Fact]
    public void Compare_Scores_A_Match_And_Names_The_Missing_Region()
    {
        RefImage img = LImage();
        RefMask mask = RefMask.Segment(img, new RefMaskOptions { Colors = new[] { ((byte)128, (byte)128, (byte)128) }, Close = 0, Open = 0, FillHoles = 0, MinArea = 10 });
        var cal = new RefCalibration(4, 0, 0, 0, 0, 0); // 0.25 m pixels; picture (x, y) -> world (x / 4, -y / 4)

        RecreationFloor whole = Floor(mask, cal, _ => true);
        ReferenceCompare.Result full = ReferenceCompare.Run(whole, cal, img, mask, null, 5, null, null);
        _out.WriteLine(string.Join(", ", full.Table.Where(kv => kv.Value is double).Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.True((double)full.Table["iou_tol"]! > 0.97, $"iou_tol {full.Table["iou_tol"]}");
        Assert.True((double)full.Table["walls_mean_m"]! < 0.3);

        // Without the L's lower arm (picture y > 60) the worst region is that arm, as "missing", at its place.
        RecreationFloor top = Floor(mask, cal, py => py < 60);
        ReferenceCompare.Result part = ReferenceCompare.Run(top, cal, img, mask, null, 5, null, null);
        var worst = (Dictionary<string, object?>)((object?[])part.Table["worst"]!)[0]!;
        Assert.Equal("missing", worst["kind"]);
        double[] c = (double[])worst["center"]!;
        Assert.InRange(c[0], 20 / 4.0, 60 / 4.0);
        Assert.InRange(c[1], -180 / 4.0, -60 / 4.0);
        Assert.True((double)part.Table["iou"]! < 0.6);
    }

    [Fact]
    public void Trace_Builds_A_Plan_Literally_And_Compare_Closes_The_Loop()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ged-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // An L at 4 px per metre: the horizontal arm 25 x 10 m, the vertical arm 10 x 30 m.
            string png = Path.Combine(dir, "plan.png");
            RefImage img = LImage();
            File.WriteAllBytes(png, Ged.Core.IO.Tex.PngWriter.Encode(img.Width, img.Height, img.Rgba));
            string script = $$$"""
                local kit = ged.import("kit")
                ref.open([[{{{png}}}]])
                ref.calibrate{ppm = 4, origin = {0, 0}}
                local m = ref.mask{colors = {{128, 128, 128}}, close = 0, open = 0, fill_holes = 0, min_area = 10}
                assert(#m.polygons == 1 and #m.polygons[1].points == 6, "one 6-corner outline")
                local tr = kit.trace{ name = "L", units = "px", regions = {
                  { name = "arm", poly = {{20, 20}, {120, 20}, {120, 60}, {60, 60}, {60, 180}, {20, 180}}, floor = 0, height = 4 } },
                  solids = { { poly = {{30, 100}, {40, 100}, {40, 110}, {30, 110}}, top = "ceiling" } } }
                assert(#tr.brushes >= 3, "air pieces plus the pillar")
                local c = ref.compare{build = "sealed", out = [[{{{Path.Combine(dir, "cmp.png")}}}]]}
                return string.format("%.4f %.4f %d", c.iou_tol, c.walls_mean_m, #c.worst)
                """;
            EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
            ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), script, Options("trace-test"), new ScriptLog());
            Assert.True(r.Success, r.Error?.ToDisplayString());
            _out.WriteLine(r.ReturnValue);
            string[] v = r.ReturnValue!.Split(' ');
            Assert.True(double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture) > 0.97, r.ReturnValue);
            Assert.True(double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture) < 0.35, r.ReturnValue);
            Assert.True(File.Exists(Path.Combine(dir, "cmp.png")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("regions = { { name = 'a', poly = {{0,0},{10,0},{10,10},{0,10}}, floor = 0 }, { name = 'b', poly = {{5,5},{15,5},{15,15},{5,15}}, floor = 0 } }", "overlap")]
    [InlineData("regions = { { name = 'a', poly = {{0,0},{10,0},{10,10},{0,10}}, floor = 0 }, { name = 'b', poly = {{11,0},{20,0},{20,10},{11,10}}, floor = 0 } }, openings = { { a = 'a', b = 'b', at = {10.5, 30} } }", "not on a wall between them")]
    [InlineData("regions = { { name = 'r', kind = 'ramp', poly = {{0,0},{2,0},{2,4},{0,4}}, floor = {0, 3}, climb = {1, 0} } }", "impossible ramp")]
    [InlineData("regions = { { name = 'a', poly = {{0,0},{10,10},{10,0},{0,10}}, floor = 0 } }", "crosses itself")]
    public void Trace_Refuses_With_An_Explanation(string body, string expected)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(Services(doc), "local kit = ged.import('kit')\nkit.trace{ " + body + " }", Options("trace-refuse"), new ScriptLog());
        Assert.False(r.Success);
        Assert.Contains(expected, r.Error!.ToDisplayString(), StringComparison.Ordinal);
    }

    // A 200 x 200 picture: red rock, a grey L (x 20-120, y 20-60 and x 20-60, y 20-180) with a black 10 px pillar.
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
                px[o] = r;
                px[o + 1] = g;
                px[o + 2] = b;
                px[o + 3] = 255;
            }
        }

        return RefImage.FromRgba(W, H, px, "l.png");
    }

    // Floor samples (0.5 m lattice) at the cell centres the mask covers, kept where keep(picture y) holds.
    private static RecreationFloor Floor(RefMask mask, RefCalibration cal, Func<double, bool> keep)
    {
        var samples = new List<(int I, int K, FloorPoint P)>();
        for (int i = -10; i < 80; i++)
        {
            for (int k = -100; k < 10; k++)
            {
                (double px, double py) = cal.ToPx((i + 0.5) * 0.5, (k + 0.5) * 0.5);
                int x = (int)px, y = (int)py;
                if (x >= 0 && y >= 0 && x < mask.Width && y < mask.Height && mask.M[(y * mask.Width) + x] && keep(py))
                {
                    samples.Add((i, k, new FloorPoint(0, double.PositiveInfinity)));
                }
            }
        }

        return RecreationFloor.FromSamples(0.5, samples);
    }

    private static ScriptServices Services(EditorDocument doc) => new()
    {
        Document = doc,
        Confirmation = new AllowAllConfirmation(),
        FileReadAccess = (p, _) => p,
        FileWriteAccess = (p, _) => p,
    };

    private static ScriptRunOptions Options(string chunk) => new()
    {
        ChunkName = chunk,
        AllowDestructive = true,
        Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(120), InstructionBudget = 2_000_000_000 },
    };
}
