using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Analysis;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;
using Vec3d = Ged.Core.Editing.Organic.Vec3d;

namespace Ged.Core.Tests;

/// <summary>
/// Mapper-style texture alignment (<see cref="UvAutoAlign"/>), its measurement (<see cref="UvQuality"/>) and the seam
/// finder both build on (<see cref="SeamFinder"/>): a room's walls wrap round every corner, stand on the floor line,
/// fit their height and never read mirrored; floors take one world map; slopes map at true length; rock folds across
/// its facets; fitted faces are kept; the measurement tells box mapping (half the walls mirrored) from alignment.
/// </summary>
public sealed class UvAutoAlignTests
{
    private const double Tol = 1e-3;

    /// <summary>A box: one texture everywhere, or (<paramref name="roles"/>) floor.tga / wall.tga / ceiling.tga by role, as kit rooms are.</summary>
    private static Brush Box(float w, float h, float d, Vec3 pos, bool air = false, string texture = "wall.tga", bool roles = false)
    {
        var p = new BrushCreateParams { Shape = BrushShape.Box, Width = w, Height = h, Depth = d, Texture = texture };
        if (roles)
        {
            p.FloorTexture = "floor.tga";
            p.WallTexture = "wall.tga";
            p.CeilingTexture = "ceiling.tga";
        }

        Brush b = BrushFactory.Create(p, 1);
        b.Position = pos;
        b.Rotation = Mat3.Identity;
        if (air)
        {
            b.Flags |= (uint)BrushFlags.Air;
            BrushOrientation.Reverse(b.Geometry); // RED stores air brushes inward
        }

        return b;
    }

    private static readonly UvAutoAlignOptions Options = new() { TextureSize = _ => (256, 256) };

    private static Vec3d W(Brush b, int vi)
    {
        Vec3 v = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[vi]));
        return new Vec3d(v.X, v.Y, v.Z);
    }

    /// <summary>The visible (winding) normal of a face in the world.</summary>
    private static Vec3d Normal(Brush b, Face f) => UvQuality.Newell(f.Vertices.Select(v => W(b, v.Index)).ToArray()).Normalized();

    private static (Vec3d Gu, Vec3d Gv) Grad(Brush b, Face f)
    {
        Vec3d[] poly = f.Vertices.Select(v => W(b, v.Index)).ToArray();
        return UvQuality.Gradients(poly, f.Vertices.Select(v => v.TextureCoords).ToArray(), Normal(b, f))!.Value;
    }

    private static JsonObject Measure(params Brush[] brushes) => UvQuality.Measure(UvQuality.FacesOf(brushes, Array.Empty<string>()), _ => (256, 256));

    [Fact]
    public void A_Room_Wraps_Round_Every_Corner_Stands_On_Its_Floor_Fits_Its_Height_And_Reads_The_Right_Way_Round()
    {
        // A 12 x 4.5 x 10 m room whose floor is at y = 1.5 (off the 4 m tile grid).
        Brush room = Box(12, 4.5f, 10, new Vec3(0, 3.75f, 0), air: true, roles: true);
        UvAutoAlignStats st = UvAutoAlign.Apply(new[] { room }, Options);
        Assert.Equal(6, st.Faces);
        Assert.Equal(2, st.Horizontal);
        Assert.Equal(1, st.Groups);
        Assert.Equal(1, st.FittedGroups); // 4.5 m = 1.125 tiles of 4 m: fitted to one tile

        foreach (Face f in room.Geometry.Faces)
        {
            Vec3d n = Normal(room, f);
            (Vec3d gu, Vec3d gv) = Grad(room, f);
            Assert.True(gu.Cross(gv).Dot(n) > 0, "the texture reads mirrored from the room");
            if (Math.Abs(n.Y) < 0.5)
            {
                Assert.Equal(-1, gv.Normalized().Y, 3);              // upright: V straight down
                Assert.Equal(1 / 4.5, gv.Length, 4);                 // one tile from floor to ceiling
                foreach (FaceVertex fv in f.Vertices)
                {
                    double y = W(room, fv.Index).Y;
                    double frac = fv.TextureCoords.V - Math.Floor(fv.TextureCoords.V + 1e-6);
                    Assert.True(frac < Tol || frac > 1 - Tol, $"a tile line at y = {y}: V {fv.TextureCoords.V}"); // floor and ceiling lines
                }
            }
        }

        JsonObject m = Measure(room);
        Assert.Equal(1.0, m["walls"]!["handedness_major"]!.GetValue<double>(), 3);
        Assert.True(m["seams"]!["wall_corner"]!["continuous"]!.GetValue<double>() >= 0.74, "3 of the 4 corners wrap (the loop closes once)");
    }

    [Fact]
    public void The_Box_Map_Mirrors_Half_The_Walls_And_The_Measurement_Says_So()
    {
        Brush room = Box(12, 4, 10, new Vec3(0, 2, 0), air: true, roles: true);
        foreach (Face f in room.Geometry.Faces)
        {
            UvOps.BoxMap(room.Geometry, f, room.Rotation, room.Position, 64, 256, 256);
        }

        Assert.Equal(0.5, Measure(room)["walls"]!["handedness_major"]!.GetValue<double>(), 1);
        var issues = new List<UvQualityIssue>();
        UvQuality.Measure(UvQuality.FacesOf(new[] { room }, Array.Empty<string>()), _ => (256, 256), issues);
        Assert.Equal(2, issues.Count(i => i.Kind == "mirrored"));

        UvAutoAlign.Apply(new[] { room }, Options);
        Assert.Equal(1.0, Measure(room)["walls"]!["handedness_major"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Floors_Take_One_World_Map_So_Neighbours_Continue_And_A_Ramp_Maps_At_True_Length()
    {
        Brush a = Box(4, 1, 4, new Vec3(-2, 0.5f, 0), roles: true);
        Brush b = Box(4, 1, 4, new Vec3(2, 0.5f, 0), roles: true);
        UvAutoAlign.Apply(new[] { a, b }, Options);
        Face ta = a.Geometry.Faces.First(f => Normal(a, f).Y > 0.99), tb = b.Geometry.Faces.First(f => Normal(b, f).Y > 0.99);
        (Vec3d ga, _) = Grad(a, ta);
        (Vec3d gb, _) = Grad(b, tb);
        Assert.Equal(-0.25, ga.X, 4); // stock RED floors: U = -x
        Assert.Equal(ga.X, gb.X, 6);
        Assert.Equal(1.0, Measure(a, b)["seams"]!["coplanar"]!["continuous"]!.GetValue<double>(), 3);

        // A 30-degree ramp: the box map would stretch the slope by 1/cos(30); aligned, texels stay square.
        Brush ramp = BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Wedge, Width = 4, Height = 2.3094f, Depth = 4, Texture = "ramp.tga" }, 2);
        ramp.Position = new Vec3(0, 2, 10);
        ramp.Rotation = Mat3.Identity;
        UvAutoAlign.Apply(new[] { ramp }, Options);
        Face slope = ramp.Geometry.Faces.First(f => Normal(ramp, f).Y is > 0.2 and < 0.98);
        (Vec3d gu, Vec3d gv) = Grad(ramp, slope);
        Assert.Equal(gu.Length, gv.Length, 4);
        Assert.Equal(0.25, gv.Length, 4);
        Assert.True(gv.Y < 0, "V runs down the slope");
        Assert.Equal(0, Measure(ramp)["slope_stretched"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Rock_Facets_Fold_So_Every_Tree_Seam_Is_Continuous()
    {
        // An irregular rock: the hull of jittered points.
        var rnd = new Random(5);
        var pts = new List<Vec3d>();
        for (int i = 0; i < 24; i++)
        {
            double t = rnd.NextDouble() * Math.PI * 2, p = (rnd.NextDouble() * Math.PI) - (Math.PI / 2);
            double r = 2 + rnd.NextDouble();
            pts.Add(new Vec3d(r * Math.Cos(p) * Math.Cos(t), 1.5 * r * Math.Sin(p), r * Math.Cos(p) * Math.Sin(t)));
        }

        var hp = new Editing.Organic.HullBrushParams { Floor = "rock.tga", Wall = "rock.tga", Ceiling = "rock.tga" };
        Assert.True(Editing.Organic.HullBrush.TryBuild(pts, hp, 3, out Brush? built, out _, out _).Success);
        Brush rock = built!;
        foreach (Face f in rock.Geometry.Faces)
        {
            UvOps.BoxMap(rock.Geometry, f, rock.Rotation, rock.Position, 64, 256, 256);
        }

        double before = Measure(rock)["seams"]!["fold"]!["continuous"]!.GetValue<double>();
        UvAutoAlignStats st = UvAutoAlign.Apply(new[] { rock }, new UvAutoAlignOptions { TextureSize = _ => (256, 256), MaxTwistDegrees = 180 });
        Assert.Equal(1, st.Groups);
        Assert.Equal(0, st.Reset);
        JsonObject after = Measure(rock);
        double cont = after["seams"]!["fold"]!["continuous"]!.GetValue<double>();
        Assert.True(cont > before + 0.2, $"fold continuity {before:0.00} -> {cont:0.00}");
        Assert.True(cont >= 0.5, $"fold continuity {cont:0.00}");             // every spanning-tree edge is exact
        Assert.Equal(0, after["stretched"]!.GetValue<double>(), 3);            // folds keep true length
    }

    [Fact]
    public void Fitted_Faces_Are_Kept_And_Portal_Sky_And_Invisible_Faces_Are_Skipped()
    {
        Brush crate = Box(1, 1, 1, new Vec3(0, 0.5f, 0), texture: "crate.tga");
        foreach (Face f in crate.Geometry.Faces)
        {
            UvAlignOps.FitLocal(crate.Geometry, f, UvFitAxes.Both, 1, 1, 256, 256);
        }

        Uv[][] before = crate.Geometry.Faces.Select(f => f.Vertices.Select(v => v.TextureCoords).ToArray()).ToArray();
        UvAutoAlignStats st = UvAutoAlign.Apply(new[] { crate }, Options);
        Assert.Equal(6, st.Kept);
        Assert.Equal(before, crate.Geometry.Faces.Select(f => f.Vertices.Select(v => v.TextureCoords).ToArray()).ToArray());

        Assert.Equal(6, UvAutoAlign.Apply(new[] { crate }, new UvAutoAlignOptions { TextureSize = _ => (256, 256), KeepFitted = false }).Faces);

        Brush helper = Box(2, 2, 2, new Vec3(5, 1, 0), texture: "sld_invisible01.tga");
        Brush sky = Box(2, 2, 2, new Vec3(9, 1, 0));
        foreach (Face f in sky.Geometry.Faces)
        {
            f.Flags |= (ushort)FaceFlags.ShowSky;
        }

        Assert.Equal(0, UvAutoAlign.Apply(new[] { helper, sky }, Options).Faces);
    }

    [Fact]
    public void Seam_Finder_Pairs_Collinear_Overlaps_Across_T_Junctions_Only()
    {
        // A long edge (0..4) against two short ones (0..2, 2..4) on the same line, plus a parallel edge 1 cm off it.
        var polys = new List<Vec3d[]>
        {
            new[] { new Vec3d(0, 0, 0), new Vec3d(4, 0, 0), new Vec3d(4, 0, -1) },
            new[] { new Vec3d(2, 0, 0), new Vec3d(0, 0, 0), new Vec3d(1, 0, 1) },
            new[] { new Vec3d(4, 0, 0), new Vec3d(2, 0, 0), new Vec3d(3, 0, 1) },
            new[] { new Vec3d(0, 0, 0.01), new Vec3d(4, 0, 0.01), new Vec3d(2, 1, 0.01) },
        };
        var pairs = SeamFinder.Pairs(polys, 0.2);
        Assert.Equal(2, pairs.Count(p => p.A == 0 && (p.B == 1 || p.B == 2)));
        Assert.DoesNotContain(pairs, p => p.B == 3 || p.A == 3);
        Assert.All(pairs.Where(p => p.A == 0), p => Assert.Equal(2, p.P1.Sub(p.P0).Length, 6));
    }
}
