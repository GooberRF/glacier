using System;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Analysis.Recreation;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Spatial;
using Ged.Core.Tests.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Analysis;

/// <summary>
/// The heights reading behind the plan / top heights overlay (SpatialAnalysis.Heights): on a traced level with a flat
/// room, a rectangular ramp, a flared ramp, a flight of stairs, a 3 m ledge and a 0.3 m step it finds exactly two ramps
/// (their heights and climb), one flight of stairs and one drop, and never marks the walkable step. Also: the colour map
/// survives the overlay's blending, and a flared kit.trace ramp of any outline is a sealed flight between its floors with
/// a ceiling parallel to its floor and no stair or opening findings.
/// </summary>
public sealed class HeightsReadingTests
{
    /// <summary>
    /// The synthetic level (metres): a hall at 0 with a 0.3 m dais and a 3.3 m ledge beside it (a 3 m drop), a pit at
    /// -3 reached by a rectangular ramp and by a flared one (4 m wide at its foot, 8 m at its top), and stairs from the
    /// hall up to a loft at 3.3. Shared with the render tests.
    /// </summary>
    public const string Level = """
        local kit = ged.import("kit")
        kit.trace{ name = "hz", via = "prism", regions = {
          { name = "low", poly = {{0, 0}, {20, 0}, {20, 20}, {0, 20}}, floor = 0, ceiling = 8 },
          { name = "dais", poly = {{20, 0}, {30, 0}, {30, 20}, {20, 20}}, floor = 0.3, ceiling = 8 },
          { name = "ledge", poly = {{30, 0}, {40, 0}, {40, 20}, {30, 20}}, floor = 3.3, ceiling = 8 },
          { name = "rramp", kind = "ramp", poly = {{2, -10}, {8, -10}, {8, 0}, {2, 0}}, floor = {-3, 0}, climb = {0, 1} },
          { name = "flare", kind = "ramp", poly = {{12, -10}, {16, -10}, {18, 0}, {10, 0}}, floor = {-3, 0}, climb = {0, 1} },
          { name = "pit", poly = {{0, -22}, {20, -22}, {20, -10}, {0, -10}}, floor = -3, ceiling = 3 },
          { name = "st", kind = "stairs", poly = {{2, 20}, {6, 20}, {6, 30}, {2, 30}}, floor = {0, 3.3}, climb = {0, 1} },
          { name = "loft", poly = {{0, 30}, {14, 30}, {14, 38}, {0, 38}}, floor = 3.3, ceiling = 7 },
        } }
        level.player_start{ pos = {10, 1, 10}, yaw = 0 }
        """;

    private readonly ITestOutputHelper _out;

    public HeightsReadingTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Reading_Finds_Exactly_The_Ramps_Stairs_And_Drop()
    {
        EditorDocument doc = Run(Level + "\nops.build()\nreturn 'ok'");
        HeightsReading h = HeightsReading.Measure(SpatialLevel.FromDocument(doc));
        foreach (HeightsFlight f in h.Ramps.Concat(h.Stairs))
        {
            _out.WriteLine($"{f.Kind} {f.Low:0.00}..{f.High:0.00} climb ({f.DirX:0.00}, {f.DirZ:0.00}) area {f.Area:0}");
        }

        foreach (HeightsDrop d in h.Drops)
        {
            _out.WriteLine($"drop {d.High:0.00} -> {d.Low:0.00} length {d.Length:0.0} at ({d.Points[0].X}, {d.Points[0].Z})");
        }

        Assert.Equal(-3, h.Min, 1);
        Assert.Equal(3.3, h.Max, 1);

        // Two ramps from the pit (-3) up to the hall (0), both climbing +Z.
        Assert.Equal(2, h.Ramps.Count);
        foreach (HeightsFlight r in h.Ramps)
        {
            Assert.InRange(r.Low, -3.25, -2.75);
            Assert.InRange(r.High, -0.25, 0.25);
            Assert.True(Angle(r.DirX, r.DirZ, 0, 1) <= 10, $"climb ({r.DirX}, {r.DirZ})");
        }

        // The flared one is the wider (8 m at its top) and covers its trapezoid (60 m²).
        HeightsFlight flare = h.Ramps.OrderBy(r => r.Foot.X).Last();
        Assert.InRange(flare.Area, 50, 66);
        Assert.InRange(flare.Width, 6.5, 8.5);

        // One flight of stairs from the hall (0) to the loft (3.3).
        HeightsFlight stairs = Assert.Single(h.Stairs);
        Assert.InRange(stairs.Low, -0.25, 0.25);
        Assert.InRange(stairs.High, 3.05, 3.55);
        Assert.True(Angle(stairs.DirX, stairs.DirZ, 0, 1) <= 10, $"stairs climb ({stairs.DirX}, {stairs.DirZ})");

        // One drop: the ledge (3.3) over the dais (0.3) along x = 30; the dais' 0.3 m step is never one.
        HeightsDrop drop = Assert.Single(h.Drops);
        Assert.InRange(drop.High, 3.05, 3.55);
        Assert.InRange(drop.Low, 0.05, 0.55);
        Assert.InRange(drop.Length, 18, 21);
        Assert.All(drop.Points, p => Assert.InRange(p.X, 29.4, 30.6));
        Assert.All(drop.LowSide, n => Assert.True(n.X < -0.9, "the low side is the dais (-X)"));

        // Flat floors: hall 0, dais 0.3, ledge 3.3, pit -3, loft 3.3.
        double[] flats = h.Flats.Where(f => f.Area >= 50).Select(f => Math.Round(f.Y, 1)).OrderBy(y => y).ToArray();
        Assert.Equal(new[] { -3, 0, 0.3, 3.3, 3.3 }, flats);

        // The floor under a point: the flared ramp half way up, the dais, nothing walkable inside the wall between ramps.
        HeightsHit? mid = h.FloorAt(14, -5, 50);
        Assert.NotNull(mid);
        Assert.True(mid.Value.Walkable && mid.Value.Sloped && mid.Value.Kind == HeightsReading.KindRamp);
        Assert.InRange(mid.Value.Y, -1.7, -1.3);
        HeightsHit? dais = h.FloorAt(25, 10, 50);
        Assert.True(dais is { Walkable: true, Kind: HeightsReading.KindFlat } && Math.Abs(dais.Value.Y - 0.3) < 0.01);
        Assert.False(h.FloorAt(9, -5, 50) is { Walkable: true });
    }

    [Fact]
    public void Color_Map_Inverts_Through_Blending_Darkening_And_Lightening()
    {
        for (double t = 0; t <= 1.0001; t += 0.05)
        {
            (byte r, byte g, byte b) = HeightColorMap.Color(t);
            Assert.InRange(HeightColorMap.TOf(r, g, b)!.Value, t - 0.005, t + 0.005);

            // Overlay: 55 % colour over a grey; a contour (darkened); a tread line (lightened).
            foreach (int grey in new[] { 0, 90, 200 })
            {
                byte Mix(byte c) => (byte)Math.Round((c * 0.55) + (grey * 0.45));
                Assert.InRange(HeightColorMap.TOf(Mix(r), Mix(g), Mix(b))!.Value, t - 0.01, t + 0.01);
            }

            Assert.InRange(HeightColorMap.TOf((byte)(r * 0.45), (byte)(g * 0.45), (byte)(b * 0.45))!.Value, t - 0.01, t + 0.01);
            byte Light(byte c) => (byte)(c + ((255 - c) * 0.4));
            Assert.InRange(HeightColorMap.TOf(Light(r), Light(g), Light(b))!.Value, t - 0.01, t + 0.01);
        }

        (byte dr, byte dg, byte db) = HeightColorMap.Drop;
        Assert.Null(HeightColorMap.TOf(dr, dg, db));
        Assert.True(HeightColorMap.IsDrop(dr, dg, db));
        Assert.Null(HeightColorMap.TOf(128, 128, 128));
        Assert.Null(HeightColorMap.TOf(230, 90, 230)); // the plan's mover magenta is no floor
        Assert.False(HeightColorMap.IsDrop(230, 90, 230));
    }

    [Fact]
    public void Decoder_Reads_Flats_A_Ramp_And_A_Drop_From_A_Heights_Picture()
    {
        // 10 px per metre, legend -1..4. Top band: a floor at 0, a ramp up +X to 3 over 6 m, a floor at 3. Bottom band:
        // a floor at 3, a violet drop line, a floor at 0.
        const int W = 200, H = 120;
        var px = new byte[W * H * 4];
        void Put(int x, int y, (byte R, byte G, byte B) c)
        {
            int i = ((y * W) + x) * 4;
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.R, c.G, c.B, (byte)255);
        }

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Put(x, y, ((byte)36, (byte)38, (byte)44));
                double? h = null;
                if (y is >= 10 and < 55 && x is >= 10 and < 190)
                {
                    h = x < 70 ? 0 : x < 130 ? 3.0 * (x + 0.5 - 70) / 60 : 3;
                }
                else if (y is >= 65 and < 110 && x is >= 10 and < 190)
                {
                    h = x < 100 ? 3 : x < 104 ? null : 0;
                    if (h is null)
                    {
                        Put(x, y, HeightColorMap.Drop);
                        continue;
                    }
                }

                if (h is double v)
                {
                    Put(x, y, HeightColorMap.ColorOf(v, -1, 4));
                }
            }
        }

        RefImage img = RefImage.FromRgba(W, H, px);
        HeightsDecoded d = HeightsDecoder.Decode(img, new HeightsDecoderOptions { Min = -1, Max = 4, Ppm = 10 });
        foreach (DecodedRegion r in d.Regions)
        {
            _out.WriteLine($"{r.Name} {r.Kind} {r.Low:0.00}..{r.High:0.00} area {r.AreaM2:0.0} climb {r.Climb}");
        }

        DecodedRegion ramp = Assert.Single(d.Regions, r => r.Kind == "ramp");
        Assert.InRange(ramp.Low, -0.25, 0.25);
        Assert.InRange(ramp.High, 2.75, 3.25);
        Assert.True(ramp.Climb is { } c && c.X > 0.98, $"climb {ramp.Climb}");
        Assert.InRange(ramp.Slope, 0.45, 0.55);
        Assert.Equal(4, d.Regions.Count(r => r.Kind == "flat"));
        Assert.DoesNotContain(d.Regions, r => r.Kind == "stairs");
        DecodedDrop drop = Assert.Single(d.Drops);
        Assert.InRange(drop.High, 2.75, 3.25);
        Assert.InRange(drop.Low, -0.25, 0.25);
        Assert.All(drop.Points, p => Assert.InRange(p.X, 98, 106));

        // The ramp and its floors share their seams (kit.trace joins them), and the drop's floors meet across the line.
        DecodedRegion low = d.Regions.Single(r => r.Kind == "flat" && Math.Abs(r.Low) < 0.1 && r.Poly.Max(p => p.Y) <= 60);
        Assert.InRange(low.Poly.Max(p => p.X) - ramp.Poly.Min(p => p.X), -1.5, 1.5);
        DecodedRegion ledge = d.Regions.Single(r => r.Kind == "flat" && Math.Abs(r.Low - 3) < 0.1 && r.Poly.Min(p => p.Y) >= 60);
        DecodedRegion under = d.Regions.Single(r => r.Kind == "flat" && Math.Abs(r.Low) < 0.1 && r.Poly.Min(p => p.Y) >= 60);
        Assert.InRange(under.Poly.Min(p => p.X) - ledge.Poly.Max(p => p.X), -1.5, 1.5);
    }

    [Fact]
    public void Traced_Flared_Ramp_Is_A_Sealed_Flight_With_A_Parallel_Ceiling()
    {
        // A ramp 4 m wide at its foot and 12 m wide at its top, climbing 4 m over 12 m from one room to the next.
        string script = """
            local kit = ged.import("kit")
            local tr = kit.trace{ name = "fl", check = true, material = "concrete", regions = {
              { name = "low", poly = {{0, 0}, {24, 0}, {24, 16}, {0, 16}}, floor = 0, height = 5 },
              { name = "up", poly = {{0, 28}, {24, 28}, {24, 44}, {0, 44}}, floor = 4, height = 5 },
              { name = "flare", kind = "ramp", poly = {{10, 16}, {14, 16}, {18, 28}, {6, 28}}, floor = {0, 4}, climb = {0, 1} },
            } }
            level.player_start{ pos = {4, 1, 4}, yaw = 0 }
            for _, p in ipairs({ {4, 0.1, 4}, {20, 0.1, 12}, {4, 4.1, 40}, {20, 4.1, 32} }) do level.place{ kind = "respawn", pos = p } end
            assert(tr.layout_check.ok, "kit.check_layout passes: " .. table.concat(tr.layout_check.errors, " "))
            local joined = {}
            for _, c in ipairs(tr.connections) do joined[#joined + 1] = c.a .. "-" .. c.b end
            ops.build()
            local a = level.audit{ checks = { "stairs", "openings" } }
            local floor = spatial.raycast{ from = {12, 3, 22}, dir = {0, -1, 0} }
            local ceiling = spatial.raycast{ from = {12, 3, 22}, dir = {0, 1, 0} }
            local foot = spatial.raycast{ from = {12, 1, 17}, dir = {0, 1, 0} }
            return string.format("%d %d %.3f %.3f %.3f %s", tr.holes, a.total, floor.pos.y, ceiling.pos.y, foot.pos.y, table.concat(joined, ","))
            """;
        EditorDocument doc = Run(script, out string ret);
        _out.WriteLine(ret);
        string[] v = ret.Split(' ');
        Assert.Equal("0", v[0]);                // sealed
        Assert.Equal("0", v[1]);                // no stair or opening findings
        Assert.Equal(2.0, Num(v[2]), 2);        // the planar floor half way up (z = 22)
        Assert.Equal(6.0, Num(v[3]), 2);        // its ceiling 4 m above it, parallel
        Assert.Equal(4.333, Num(v[4]), 2);      // ... and near the foot
        Assert.Contains("low-flare", v[5], StringComparison.Ordinal);
        Assert.Contains("up-flare", v[5], StringComparison.Ordinal);

        // The function checks read it as a flight joining the two floors that serves its route.
        SpatialAnalysis sa = SpatialAnalysis.Run(SpatialLevel.FromDocument(doc));
        sa.Report();
        SpatialAnalysis.FlightRead flight = Assert.Single(sa.FlightReads);
        Assert.InRange(flight.Rise, 3.75, 4.25);
        Assert.InRange(flight.Foot.Y, -0.25, 0.25);
        Assert.Null(flight.Fault);

        // The heights reading sees one ramp from 0 to 4 and no drop.
        HeightsReading h = HeightsReading.Measure(SpatialLevel.FromDocument(doc));
        HeightsFlight ramp = Assert.Single(h.Ramps);
        Assert.InRange(ramp.Low, -0.25, 0.25);
        Assert.InRange(ramp.High, 3.75, 4.25);
        Assert.Empty(h.Drops);
    }

    private static double Angle(double ax, double az, double bx, double bz) =>
        Math.Acos(Math.Clamp(((ax * bx) + (az * bz)) / (Math.Sqrt((ax * ax) + (az * az)) * Math.Sqrt((bx * bx) + (bz * bz))), -1, 1)) * 180 / Math.PI;

    private static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private EditorDocument Run(string script) => Run(script, out _);

    private EditorDocument Run(string script, out string returned)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        ScriptRunResult r = ScriptTestFixture.Runner().Run(new ScriptServices
        {
            Document = doc,
            Confirmation = new AllowAllConfirmation(),
            FileReadAccess = (p, _) => p,
            FileWriteAccess = (p, _) => p,
        }, script, new ScriptRunOptions
        {
            ChunkName = "heights",
            AllowDestructive = true,
            Limits = new ScriptExecutionLimits { Timeout = TimeSpan.FromSeconds(300), InstructionBudget = 2_000_000_000 },
        }, new ScriptLog());
        Assert.True(r.Success, r.Error?.ToDisplayString());
        returned = r.ReturnValue ?? string.Empty;
        return doc;
    }
}
