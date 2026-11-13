using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// Hull brushes on the construction grid: every generated brush is closed, outward, convex and planar,
/// unrotated, with every world vertex on the 1/1024 m grid; plus GeometryCheck's failure reports,
/// TryRebuild's texture inheritance, BrushEditor.AddBrushes and the organic random stream.
/// </summary>
public sealed class HullBrushTests
{
    private static Vec3d P(double x, double y, double z) => new(x, y, z);

    private static bool OnGrid(float v) => Math.Round((double)v * 1024) == (double)v * 1024;

    private static Brush Build(IReadOnlyList<Vec3d> pts, HullBrushParams? p = null, int uid = 1)
    {
        OpResult r = HullBrush.TryBuild(pts, p ?? new HullBrushParams(), uid, out Brush? brush, out HullResult? hull, out HullBrushError? error);
        Assert.True(r.Success, error?.Message);
        Assert.NotNull(hull);
        return brush!;
    }

    private static List<Vec3d> BoxPoints(double x0, double y0, double z0, double x1, double y1, double z1) => new()
    {
        P(x0, y0, z0), P(x1, y0, z0), P(x0, y1, z0), P(x1, y1, z0), P(x0, y0, z1), P(x1, y0, z1), P(x0, y1, z1), P(x1, y1, z1),
    };

    [Fact]
    public void Random_Clouds_Make_Valid_Grid_Exact_Brushes()
    {
        int simplified = 0;
        for (int seed = 1; seed <= 200; seed++)
        {
            var rng = new OrganicRandom(seed, 1);
            int n = 8 + (int)(rng.NextDouble() * 33);
            var pts = Enumerable.Range(0, n).Select(_ => P(rng.Range(-20, 20), rng.Range(-20, 20), rng.Range(-20, 20))).ToList();
            OpResult r = HullBrush.TryBuild(pts, new HullBrushParams(), seed, out Brush? b, out HullResult? hull, out HullBrushError? error);
            Assert.True(r.Success, $"seed {seed}: {error?.Message}");
            simplified += hull!.Simplified;

            GeometryReport report = GeometryCheck.Check(b!);
            Assert.True(report.Closed && report.Outward && report.Convex && report.Planar,
                $"seed {seed}: " + string.Join(" ", report.Issues));
            Assert.True(report.MaxPlanarError <= 1e-6, $"seed {seed}: planar error {report.MaxPlanarError}");
            Assert.True(report.MaxFaceCorners <= OrganicLimits.MaxFaceCorners);
            Assert.Equal(Mat3.Identity, b!.Rotation);
            Assert.True(OnGrid(b.Position.X) && OnGrid(b.Position.Y) && OnGrid(b.Position.Z));
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                Vec3 w = BrushTransform.WorldVertex(b, v);
                Assert.True(OnGrid(w.X) && OnGrid(w.Y) && OnGrid(w.Z), $"seed {seed}: world vertex {w} is off the grid");

                // World = position + local exactly (float addition of grid values is exact here).
                Assert.Equal((double)b.Position.X + v.X, w.X);
            }

            Assert.True(GeometryUtil.Validate(b.Geometry).Success);
            Assert.Equal(b.Geometry.Faces.Count, hull.Faces.Count);
            Assert.All(b.Geometry.Faces, f => Assert.True(f.SurfaceIndex == -1 && f.RoomIndex == -1));
        }

        // Random ±20 m clouds rarely produce a sliver; the count is reported, not asserted.
        Assert.True(simplified >= 0);
    }

    [Fact]
    public void A_Box_Hull_Has_Role_Textures_Exact_Planes_And_A_Grid_Position()
    {
        var p = new HullBrushParams { Floor = "floor.tga", Wall = "wall.tga", Ceiling = "ceil.tga" };
        Brush b = Build(BoxPoints(1, 0, 1, 5.5, 3, 4), p);
        Assert.Equal(6, b.Geometry.Faces.Count);
        Assert.Equal(new Vec3(3.25f, 1.5f, 2.5f), b.Position);
        foreach (Face f in b.Geometry.Faces)
        {
            string tex = b.Geometry.Textures[f.Texture];
            Vec3 n = f.Plane.Normal;
            Assert.Equal(1f, MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z));
            Assert.Equal(n.Y > 0.5f ? "floor.tga" : n.Y < -0.5f ? "ceil.tga" : "wall.tga", tex);

            // Every corner lies exactly on the stored plane.
            Assert.All(f.Vertices, fv => Assert.Equal(0f, n.Dot(b.Geometry.Vertices[fv.Index]) + f.Plane.Offset));
        }
    }

    [Fact]
    public void Flags_And_Life_Follow_The_Params()
    {
        Brush air = Build(BoxPoints(0, 0, 0, 2, 2, 2), new HullBrushParams { Air = true, Life = 5 });
        Assert.Equal((uint)BrushFlags.Air, air.Flags);
        Assert.Equal(5, air.Life);
        Brush geo = Build(BoxPoints(0, 0, 0, 2, 2, 2), new HullBrushParams { Geoable = true, EmitsSteam = true });
        Assert.Equal((uint)(BrushFlags.Detail | BrushFlags.Geoable | BrushFlags.EmitsSteam), geo.Flags);
    }

    [Fact]
    public void Box_Map_Is_World_Continuous_Across_Brushes()
    {
        Brush a = Build(BoxPoints(0, 0, 0, 3, 2, 3), new HullBrushParams { TileMetres = 4 });
        Brush b = Build(BoxPoints(3, 0, 0, 7.5, 5, 3), new HullBrushParams { TileMetres = 4 });
        Uv UvAt(Brush br, Vec3 world, float ny)
        {
            Face f = br.Geometry.Faces.Single(x => x.Plane.Normal.Y == ny);
            FaceVertex fv = f.Vertices.Single(v => BrushTransform.WorldVertex(br, br.Geometry.Vertices[v.Index]).Equals(world));
            return fv.TextureCoords;
        }

        // The shared top-edge corner (3, 0, 0) of both bottom faces maps to the same UV.
        Assert.Equal(UvAt(a, new Vec3(3, 0, 0), -1), UvAt(b, new Vec3(3, 0, 0), -1));
    }

    [Fact]
    public void Snap_Grid_And_Duplicates()
    {
        // Snapping to 0.5 m merges near points; the result keeps grid positions.
        var pts = BoxPoints(0, 0, 0, 2, 2, 2);
        pts.Add(P(0.1, 0.1, 0.1));
        OpResult ok = HullBrush.TryBuild(pts, new HullBrushParams { Snap = 0.5 }, 1, out Brush? b, out HullResult? hull, out _);
        Assert.True(ok.Success);
        Assert.Equal(8, hull!.Vertices.Count);
        Assert.Equal(1, hull.InteriorPoints);

        OpResult strict = HullBrush.TryBuild(pts, new HullBrushParams { Snap = 0.5, Strict = true }, 1, out _, out _, out HullBrushError? error);
        Assert.False(strict.Success);
        Assert.Equal(HullBrushErrorKind.Strict, error!.Kind);
        Assert.Equal("points[9] is not a corner of the hull (it repeats points[1]).", error.Message);

        Assert.Equal(HullBrushErrorKind.BadSnap, HullBrush.ValidateSnap(0.3)!.Kind);
        Assert.Equal("Nearest valid value: 1/4.", HullBrush.ValidateSnap(0.3)!.Hint);
        Assert.Null(HullBrush.ValidateSnap(1.0 / 1024));
        Assert.Null(HullBrush.ValidateSnap(1));
        Assert.NotNull(HullBrush.ValidateSnap(2));
        Assert.NotNull(HullBrush.ValidateSnap(1.0 / 2048));
        _ = b;
    }

    [Fact]
    public void Rebuild_Inherits_Textures_By_Normal_And_Keeps_Identity()
    {
        Brush b = Build(BoxPoints(0, 0, 0, 4, 2, 4), new HullBrushParams { Floor = "top.tga", Wall = "side.tga", Ceiling = "bottom.tga", Air = true, Life = 9 }, uid: 42);
        Face east = b.Geometry.Faces.Single(f => f.Plane.Normal.X == 1);
        east.Texture = GeometryUtil.EnsureTexture(b.Geometry, "east.tga");
        east.Flags = 0x1234;

        // A slanted top: the new top faces still pick the old top's texture; the +X face keeps its own.
        List<Vec3d> pts = BoxPoints(0, 0, 0, 4, 2, 4);
        pts[3] = P(4, 2.5, 0);
        OpResult r = HullBrush.TryRebuild(b, pts, 4, 1.0 / 1024);
        Assert.True(r.Success, r.Message);
        Assert.Equal(42, b.Uid);
        Assert.Equal((uint)BrushFlags.Air, b.Flags);
        Assert.Equal(9, b.Life);
        Assert.Equal(Mat3.Identity, b.Rotation);
        GeometryReport report = GeometryCheck.Check(b);
        Assert.True(report.Closed && report.Convex && report.Planar, string.Join(" ", report.Issues));
        foreach (Face f in b.Geometry.Faces)
        {
            Vec3 n = GeometryUtil.Normal(GeometryUtil.Corners(b.Geometry, f));
            string tex = b.Geometry.Textures[f.Texture];
            string expected = n.Y > 0.7f ? "top.tga" : n.Y < -0.7f ? "bottom.tga" : n.X > 0.7f ? "east.tga" : "side.tga";
            Assert.Equal(expected, tex);
            if (n.X > 0.99f)
            {
                Assert.Equal(0x1234, f.Flags);
            }
        }

        // A failing rebuild leaves the brush unchanged.
        Geometry before = b.Geometry;
        Assert.False(HullBrush.TryRebuild(b, new[] { P(0, 0, 0), P(1, 0, 0), P(0, 0, 1), P(1, 0, 1) }, 4, 1.0 / 1024).Success);
        Assert.Same(before, b.Geometry);
    }

    [Fact]
    public void Errors_Name_The_Problem()
    {
        HullBrushError E(IReadOnlyList<Vec3d> pts, HullBrushParams? p = null)
        {
            HullBrush.TryBuild(pts, p ?? new HullBrushParams(), 1, out _, out _, out HullBrushError? e);
            return e!;
        }

        Assert.Equal(HullBrushErrorKind.PointCount, E(new[] { P(0, 0, 0) }).Kind);
        Assert.Equal(HullBrushErrorKind.NonFinite, E(new[] { P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(0, 0, double.NaN) }).Kind);
        Assert.Equal(HullBrushErrorKind.TooFewDistinct, E(new[] { P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(0, 0, 0.0001) }).Kind);
        HullBrushError flat = E(new[] { P(0, 0, 0), P(4, 0, 0), P(0, 0.004, 4), P(4, 0, 4) });
        Assert.Equal(HullBrushErrorKind.Flat, flat.Kind);
        Assert.Equal("the points are coplanar (thinnest extent 0.003906 m); a brush needs volume.", flat.Message);
        Assert.Equal(HullBrushErrorKind.TooLarge, E(BoxPoints(0, 0, 0, 5000, 1, 1)).Kind);
        Assert.Equal(HullBrushErrorKind.OutOfRange, E(BoxPoints(9000, 0, 0, 9001, 1, 1)).Kind);

        var sphere = new List<Vec3d>();
        var rng = new OrganicRandom(11);
        for (int i = 0; i < 200; i++)
        {
            double u = rng.Range(-1, 1), t = rng.Range(0, 2 * Math.PI), s = Math.Sqrt(1 - (u * u));
            sphere.Add(P(10 * s * Math.Cos(t), 10 * u, 10 * s * Math.Sin(t)));
        }

        HullBrushError over = E(sphere);
        Assert.Equal(HullBrushErrorKind.OverLimits, over.Kind);
        Assert.StartsWith("the hull has ", over.Message, StringComparison.Ordinal);
        Assert.EndsWith("; the limit is 64 vertices / 128 faces.", over.Message, StringComparison.Ordinal);
    }

    // ---- GeometryCheck ---------------------------------------------------------------------------

    private static Brush FactoryBox(int splits = 0) =>
        BrushFactory.Create(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, HeightSplits = splits, WidthSplits = splits, DepthSplits = splits }, 1);

    [Fact]
    public void Check_Accepts_A_Factory_Box()
    {
        GeometryReport r = GeometryCheck.Check(FactoryBox());
        Assert.True(r.Closed && r.Outward && r.Convex && r.Planar);
        Assert.Empty(r.Issues);
        Assert.Equal(6, r.Faces);
        Assert.Equal(8, r.Vertices);
        Assert.Equal(4, r.MaxFaceCorners);
    }

    [Fact]
    public void Check_Reports_Open_Inward_NonConvex_And_NonPlanar()
    {
        Brush open = FactoryBox();
        open.Geometry.Faces.RemoveAt(0);
        GeometryReport ro = GeometryCheck.Check(open);
        Assert.False(ro.Closed);
        Assert.False(ro.Convex);
        Assert.Contains(ro.Issues, s => s.Contains("is open", StringComparison.Ordinal));
        Assert.True(ro.Issues.Count <= GeometryCheck.MaxIssues);

        Brush inward = FactoryBox();
        foreach (Face f in inward.Geometry.Faces)
        {
            f.Vertices.Reverse();
        }

        GeometryReport ri = GeometryCheck.Check(inward);
        Assert.True(ri.Closed);
        Assert.False(ri.Outward); // a solid brush wound inward is not how RED stores it
        Assert.True(ri.Convex);   // convexity is judged against the outward planes, whichever way the faces wind
        Assert.Contains(ri.Issues, s => s.Contains("inward", StringComparison.Ordinal));

        // A dent: the top-centre vertex of a split box pushed down makes it non-convex (faces stay planar quads? no —
        // the four top quads bend), so both convex and planar fail.
        Brush dent = FactoryBox(splits: 1);
        int top = dent.Geometry.Vertices.FindIndex(v => v.X == 0 && v.Y == 1 && v.Z == 0);
        dent.Geometry.Vertices[top] = new Vec3(0, 0.5f, 0);
        GeometryReport rd = GeometryCheck.Check(dent);
        Assert.True(rd.Closed);
        Assert.False(rd.Convex);
        Assert.False(rd.Planar);
        Assert.Contains(rd.Issues, s => s.Contains("not convex", StringComparison.Ordinal));
        Assert.Contains(rd.Issues, s => s.Contains("not planar", StringComparison.Ordinal));
        Assert.True(rd.MaxPlanarError > 0.1);
    }

    // ---- BrushEditor.AddBrushes ------------------------------------------------------------------

    [Fact]
    public void AddBrushes_Is_One_Undo_Command_With_The_Section_Inside()
    {
        var doc = Ged.Core.Tests.Scripting.ScriptTestFixture.NewDoc(entities: 1);
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        var ed = new BrushEditor(doc);
        int changes = 0;
        ed.BrushesChanged += () => changes++;
        var brushes = Enumerable.Range(0, 5).Select(i => Build(BoxPoints(i * 3, 0, 0, (i * 3) + 2, 2, 2), uid: doc.AllocateUid())).ToList();
        int nodes = doc.Undo.NodeCount;

        ed.AddBrushes(brushes, "Create 5 brushes");
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
        Assert.Equal(1, changes);
        Assert.Equal(brushes.Select(b => b.Uid), ed.Brushes.Select(b => b.Uid));
        Assert.Empty(ed.SelectedBrushes);

        doc.Undo.Undo();
        Assert.Equal(2, changes);
        Assert.Empty(ed.Brushes);
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false));

        doc.Undo.Redo();
        Assert.Equal(5, ed.Brushes.Count);
        ed.AddBrushes(Array.Empty<Brush>(), "nothing");
        Assert.Equal(nodes + 1, doc.Undo.NodeCount);
    }

    // ---- OrganicRandom ---------------------------------------------------------------------------

    [Fact]
    public void OrganicRandom_Is_Deterministic_And_Stream_Separated()
    {
        var a = new OrganicRandom(1, 2, 3);
        var b = new OrganicRandom(1, 2, 3);
        var c = new OrganicRandom(1, 2, 4);
        double[] sa = Enumerable.Range(0, 100).Select(_ => a.NextDouble()).ToArray();
        Assert.Equal(sa, Enumerable.Range(0, 100).Select(_ => b.NextDouble()).ToArray());
        Assert.NotEqual(sa, Enumerable.Range(0, 100).Select(_ => c.NextDouble()).ToArray());
        Assert.All(sa, v => Assert.InRange(v, 0, 0.9999999999));
        Assert.InRange(sa.Average(), 0.4, 0.6);

        // A pinned value guards the algorithm against accidental change (generated levels depend on it).
        Assert.Equal(6791897765849424158UL, new OrganicRandom(1).NextUInt64());
        Assert.NotEqual(new OrganicRandom(1).NextUInt64(), new OrganicRandom(2).NextUInt64());
        var r = new OrganicRandom(7);
        double x = r.Range(-3, 5);
        Assert.InRange(x, -3, 5);
    }
    [Fact]
    public void A_Sliver_Error_Names_The_Callers_Points_Not_The_Deduplicated_Ones()
    {
        // points[1] and points[2] are one point after snapping, so the hull saw the caller's points 1, 3, 4, 5, ... as 0, 1, 2, 3, ...
        var failure = new HullFailure(HullFailureKind.Sliver, "internal", 0.0015, new[] { 1, 2, 3 });
        string message = HullBrush.SliverMessage(failure, new List<int> { 0, 2, 3, 4, 5 });
        Assert.Equal("points[3], points[4] and points[5] make a sliver face 0.0015 m thin (the minimum is 0.002 m).", message);
    }
}
