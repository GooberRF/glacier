using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using static Ged.Core.Tests.Scripting.KitTestHarness;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Outdoor lighting in the design kit: kit.light_room in a room open to the sky puts fewer, stronger lights on the
/// room's edges (lamps on the walls, posts beside the doorways), keeps the floor about as well lit as the indoor area
/// rule's grid would, and adds a post inside only where the edges leave floor out of reach — never on the straight way
/// between two doorways; kit.light_path gives every light a wall lamp or a post at the walkway's edge.
/// </summary>
public sealed class KitOutdoorLightingTests
{
    private readonly ITestOutputHelper _out;

    public KitOutdoorLightingTests(ITestOutputHelper output) => _out = output;

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private sealed record Lamp(double X, double Y, double Z, double Range, double Intensity);

    private sealed record Fixture(string Part, double X, double Y, double Z);

    // Lights "x,y,z,range,intensity" and fixture brushes / clutter "part,x,ymin,z" of one kit.name.
    private const string Dump = """
        local function dump(name, lights)
          local out = {}
          for _, l in ipairs(lights) do
            out[#out + 1] = string.format("L %.3f %.3f %.3f %.3f %.3f", l.pos.x, l.pos.y, l.pos.z, l:get("size_range"), l:get("on_intensity"))
          end
          for _, b in ipairs(level.brushes_tagged("kit.name", name).brushes) do
            if b:tags().kit == "light_room" or b:tags().kit == "light_path" then
              local bb = b.bounds
              out[#out + 1] = string.format("F %s %.3f %.3f %.3f", b:tags()["kit.part"], (bb.min.x + bb.max.x) / 2, bb.min.y, (bb.min.z + bb.max.z) / 2)
            end
          end
          return table.concat(out, ";")
        end
        """;

    private static (List<Lamp> Lights, List<Fixture> Fixtures) Parse(string s)
    {
        var lights = new List<Lamp>();
        var fixtures = new List<Fixture>();
        foreach (string e in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = e.Split(' ');
            if (p[0] == "L")
            {
                lights.Add(new Lamp(Num(p[1]), Num(p[2]), Num(p[3]), Num(p[4]), Num(p[5])));
            }
            else
            {
                fixtures.Add(new Fixture(p[1], Num(p[2]), Num(p[3]), Num(p[4])));
            }
        }

        return (lights, fixtures);
    }

    /// <summary>The median over a 1 m floor grid of RF's linear falloff times the point wrap, summed (no shadows).</summary>
    private static (double Median, double Unlit) FloorLight(IEnumerable<Lamp> lights, double x0, double z0, double x1, double z1)
    {
        var e = new List<double>();
        int unlit = 0;
        for (double x = x0 + 0.5; x < x1; x += 1)
        {
            for (double z = z0 + 0.5; z < z1; z += 1)
            {
                double sum = 0;
                foreach (Lamp l in lights)
                {
                    double dx = l.X - x, dy = l.Y, dz = l.Z - z;
                    double d = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                    if (d < l.Range)
                    {
                        sum += l.Intensity * (1 - (d / l.Range)) * ((2 * Math.Max(0, dy / d)) + 1) / 3;
                    }
                }

                e.Add(sum);
                if (sum <= 0)
                {
                    unlit++;
                }
            }
        }

        e.Sort();
        return (e[e.Count / 2], unlit / (double)e.Count);
    }

    private static double SegmentDistance(double x, double z, double ax, double az, double bx, double bz)
    {
        double vx = bx - ax, vz = bz - az;
        double f = Math.Clamp((((x - ax) * vx) + ((z - az) * vz)) / ((vx * vx) + (vz * vz)), 0, 1);
        return Math.Sqrt(Math.Pow(ax + (vx * f) - x, 2) + Math.Pow(az + (vz * f) - z, 2));
    }

    [Fact]
    public void A_Yard_Open_To_The_Sky_Gets_Few_Strong_Lights_On_Its_Edges()
    {
        // A 30 x 26 m yard with doorways in its east and west walls.
        KitRun run = Run(Dump + "\n" + """
            local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {30, 12, 26} }
            local east = kit.room{ name = "east", pos = {34, 0, 0}, size = {16, 6, 16} }
            local west = kit.room{ name = "west", pos = {-34, 0, 0}, size = {16, 6, 16} }
            kit.doorway{ a = yard, b = east, width = 4 }
            kit.doorway{ a = west, b = yard, width = 4 }
            kit.sky_room{ open = { yard } }
            return dump("yard", kit.light_room{ room = yard })
            """);
        (List<Lamp> lights, List<Fixture> fixtures) = Parse(run.Result.ReturnValue!);
        _out.WriteLine(string.Join("\n", lights) + "\n" + string.Join("\n", fixtures));

        // About 0.5 per 100 m² (the stock maps' outdoor lights that carry the floor: 0.32-1.07), not the indoor 2.
        Assert.InRange(lights.Count, 3, 6);
        // On the edges: every light within 1.6 m of a wall (a wall lamp 0.42 m off it, a post's head 3.1 m off it at
        // most: posts stand 1 m off the wall), every post beside a doorway, none on the way between the doorways.
        foreach (Lamp l in lights)
        {
            double edge = Math.Min(Math.Min(l.X + 15, 15 - l.X), Math.Min(l.Z + 13, 13 - l.Z));
            Assert.True(edge <= 3.2, $"light at ({l.X:0.0}, {l.Z:0.0}) stands {edge:0.0} m from the yard's edge");
        }

        List<Fixture> posts = fixtures.Where(f => f.Part == "post").ToList();
        Assert.Equal(2, posts.Count);                                     // one beside each doorway
        foreach (Fixture p in posts)
        {
            Assert.InRange(Math.Min(15 - Math.Abs(p.X), 15), 0.9, 1.1);   // 1 m off the doorway's wall
            Assert.InRange(Math.Abs(p.Z), 4.0, 5.6);                       // 2-3.5 m along it from the opening (z -2..2)
            Assert.True(SegmentDistance(p.X, p.Z, -15, 0, 15, 0) >= 3, $"post at ({p.X:0.0}, {p.Z:0.0}) is on the way between the doorways");
        }

        Assert.Contains(fixtures, f => f.Part == "fixture" && f.Y > 7);    // wall lamps up on the walls

        // The floor stays about as well lit as under the indoor area rule's 16-light grid (light 7.6 m up, 14 m range).
        var grid = new List<Lamp>();
        for (int iz = 0; iz < 4; iz++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                grid.Add(new Lamp(-15 + ((ix + 0.5) * 7.5), 7.6, -13 + ((iz + 0.5) * 6.5), 0.9 * Math.Sqrt((7.5 * 7.5) + (6.5 * 6.5) + 144), 1));
            }
        }

        (double now, double unlit) = FloorLight(lights, -15, -13, 15, 13);
        (double before, _) = FloorLight(grid, -15, -13, 15, 13);
        _out.WriteLine($"{lights.Count} lights: floor light median {now:0.000} (grid of 16: {before:0.000}), unlit {unlit:P0}");
        Assert.True(now >= 0.85 * before, $"floor light {now:0.000} against the grid's {before:0.000}");
        Assert.Equal(0, unlit);
        Assert.All(lights, l => Assert.InRange(l.Intensity, 1.0, 2.0));
        Assert.All(lights, l => Assert.InRange(l.Range, 15, 40));
    }

    [Fact]
    public void A_Wide_Plaza_Gets_Inner_Posts_Off_The_Way_Between_Its_Doorways()
    {
        KitRun run = Run(Dump + "\n" + """
            local plaza = kit.room{ name = "plaza", pos = {0, 0, 0}, size = {64, 12, 52} }
            local east = kit.room{ name = "east", pos = {40, 0, 0}, size = {14, 6, 14} }
            local west = kit.room{ name = "west", pos = {-40, 0, 0}, size = {14, 6, 14} }
            kit.doorway{ a = plaza, b = east, width = 4 }
            kit.doorway{ a = west, b = plaza, width = 4 }
            kit.sky_room{ open = { plaza } }
            return dump("plaza", kit.light_room{ room = plaza })
            """);
        (List<Lamp> lights, List<Fixture> fixtures) = Parse(run.Result.ReturnValue!);
        List<Fixture> inner = fixtures.Where(f => f.Part == "post" && Math.Min(32 - Math.Abs(f.X), 26 - Math.Abs(f.Z)) > 2).ToList();
        _out.WriteLine($"{lights.Count} lights, inner posts at {string.Join(", ", inner.Select(f => $"({f.X:0.0}, {f.Z:0.0})"))}");
        Assert.NotEmpty(inner);                                            // the middle is more than 18 m from the edges
        Assert.All(inner, f => Assert.True(SegmentDistance(f.X, f.Z, -32, 0, 32, 0) >= 2.9, $"inner post at ({f.X:0.0}, {f.Z:0.0}) stands on the way between the doorways"));
        Assert.InRange(lights.Count * 100.0 / (64 * 52), 0.4, 0.9);
        (double _, double unlit) = FloorLight(lights, -32, -26, 32, 26);
        Assert.Equal(0, unlit);
    }

    [Fact]
    public void Path_Lights_Get_A_Wall_Lamp_Or_A_Post_Within_The_Height_Cap()
    {
        KitRun run = Run(Dump + "\n" + """
            local c = kit.canyon{ name = "gorge", path = { {0, 0, -30}, {0, 0, 30} }, width = 20, height = 20, seed = 1 }
            local lp = kit.light_path{ along = c, spacing = 10 }
            local bare = kit.light_path{ along = c, spacing = 10, fixture = false, name = "bare" }
            return #lp .. "|" .. #lp.fixtures .. "|" .. #bare.fixtures .. "|" .. dump("gorge", lp)
            """);
        string[] parts = run.Result.ReturnValue!.Split('|');
        int n = int.Parse(parts[0], CultureInfo.InvariantCulture);
        (List<Lamp> lights, List<Fixture> fixtures) = Parse(parts[3]);
        _out.WriteLine(string.Join("\n", lights) + "\n" + string.Join("\n", fixtures));
        Assert.True(n > 0);
        Assert.Equal(n, int.Parse(parts[1], CultureInfo.InvariantCulture));          // every light has its lamp (no tables: a detail lamp)
        Assert.Equal("0", parts[2]);                                                  // fixture = false: bare lights
        Assert.All(lights, l => Assert.InRange(l.Y, 2.5, 4.6));                       // at most 4.5 m above the floor
        Assert.All(fixtures, f => Assert.InRange(f.Y, 2.5, 4.9));                     // the lamps hang with their lights
        Assert.Contains(lights, l => l.X > 4);                                        // on the walls, both sides
        Assert.Contains(lights, l => l.X < -4);
    }
}
