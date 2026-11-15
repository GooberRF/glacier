using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Modules;
using Xunit;

namespace Ged.Core.Tests.Modules;

/// <summary>The module instancer: natural placement, stretch with tile re-instancing, UV density, yaw, mirror, detail, sealed builds.</summary>
public sealed class ModuleInstancerTests
{
    private static readonly Lazy<List<BuiltModule>> Library = new(() => ModuleTestLevel.Harvest(ModuleTestLevel.Build()).Modules);

    private static BuiltModule Module(string category) => Library.Value.Single(m => m.Info.Category == category);

    /// <summary>Asserts two point sets match within 2 mm (each point of one has a partner in the other, and back).</summary>
    private static void SamePoints(IEnumerable<D3> expected, IEnumerable<D3> actual, double tol = 2e-3)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        foreach (D3 p in e)
        {
            Assert.True(a.Any(q => (q - p).Length <= tol), $"expected point {p} has no match");
        }

        foreach (D3 p in a)
        {
            Assert.True(e.Any(q => (q - p).Length <= tol), $"unexpected point {p}");
        }
    }

    [Fact]
    public void Natural_Placement_Reproduces_The_Source_Geometry()
    {
        EditorDocument doc = ModuleTestLevel.Build();
        BuiltModule pil = Module("pilaster");
        ModuleProvenance src = pil.Info.Provenance[0];
        List<Brush> before = ModuleTestLevel.Brushes(doc).ToList();
        ModulePlaceResult r = ModuleInstancer.Place(doc, pil.Info, pil.Brushes, new ModulePlacement
        {
            Position = src.Position.ToVec3(),
            YawDegrees = src.Yaw,
            ForceDetail = true,
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, r.BrushUids.Count);
        List<Brush> after = ModuleTestLevel.Brushes(doc);
        Assert.Equal(before.Count + 3, after.Count);
        Assert.Equal(r.BrushUids, after.Skip(before.Count).Select(b => b.Uid)); // appended at the end, in module order

        List<Brush> placed = after.Skip(before.Count).ToList();
        List<Brush> source = after.Where(b => src.BrushUids.Contains(b.Uid)).ToList();
        for (int i = 0; i < 3; i++)
        {
            SamePoints(WorldPoly(source[i]), WorldPoly(placed[i]));
        }

        // One undo step removes the whole placement.
        doc.Undo.Undo();
        Assert.Equal(before.Count, ModuleTestLevel.Brushes(doc).Count);
    }

    private static IEnumerable<D3> WorldPoly(Brush b) => WorldPolyhedron.FromBrush(b).Vertices;

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(37)]
    public void Any_Yaw_Maps_Local_To_World(double yaw)
    {
        BuiltModule pil = Module("pilaster");
        var at = new D3(3, 1, -2);
        ModuleBuild b = ModuleInstancer.BuildBrushes(pil.Info, pil.Brushes, new ModulePlacement { Position = at.ToVec3(), YawDegrees = yaw });
        Assert.True(b.Ok, b.Error);
        SamePoints(pil.Brushes.SelectMany(WorldPoly).Select(v => YawFrame.ToWorld(v, at, yaw)), b.Brushes.SelectMany(WorldPoly));
        Assert.Equal(pil.Info.Size.Y, b.Size.Y, 3);
    }

    [Fact]
    public void Stretching_A_Railing_Repeats_Posts_And_Keeps_End_Margins()
    {
        BuiltModule rail = Module("railing");
        ModuleTileFamily posts = rail.Info.Tiles.Single();
        double natural = rail.Info.Size.X;
        ModuleBuild b = ModuleInstancer.BuildBrushes(rail.Info, rail.Brushes, new ModulePlacement { Length = natural * 2 });
        Assert.True(b.Ok, b.Error);
        Assert.Equal(natural * 2, b.Size.X, 3);
        int count = b.TileCounts.Single();
        Assert.Equal(13, count);
        Assert.Equal(1 + count, b.Brushes.Count); // the rail + the posts

        // Post centres: first and last keep their margins from the (stretched, re-centred) rail ends.
        var postCentres = b.Brushes.Where(x => BrushTransform.Dimensions(x).Y > 0.5).Select(x => (double)x.Position.X).OrderBy(x => x).ToList();
        Assert.Equal(count, postCentres.Count);
        double railMin = -natural, railMax = natural;
        Assert.Equal(posts.MarginStart, postCentres[0] - railMin, 3);
        Assert.Equal(posts.MarginEnd, railMax - postCentres[^1], 3);
        double spacing = postCentres[1] - postCentres[0];
        for (int i = 2; i < count; i++)
        {
            Assert.Equal(spacing, postCentres[i] - postCentres[i - 1], 3);
        }

        // Shrinking keeps at least two posts.
        ModuleBuild small = ModuleInstancer.BuildBrushes(rail.Info, rail.Brushes, new ModulePlacement { Length = 0.5 });
        Assert.True(small.Ok, small.Error);
        Assert.True(small.TileCounts.Single() >= 2);
    }

    [Fact]
    public void Stretch_Preserves_Uv_Density_And_Refuses_Axes_Without_A_Slice()
    {
        BuiltModule pil = Module("pilaster");
        ModuleBuild b = ModuleInstancer.BuildBrushes(pil.Info, pil.Brushes, new ModulePlacement { Height = 6 });
        Assert.True(b.Ok, b.Error);
        Assert.Equal(6, b.Size.Y, 3);

        // The shaft (module brush 1) has side faces stretched along Y: their UV gradient along Y is unchanged.
        Brush srcShaft = pil.Brushes[1], newShaft = b.Brushes[1];
        int checkedFaces = 0;
        for (int fi = 0; fi < srcShaft.Geometry.Faces.Count; fi++)
        {
            (double du0, double dv0) = UvPerMetreY(srcShaft, fi);
            (double du1, double dv1) = UvPerMetreY(newShaft, fi);
            if (double.IsNaN(du0))
            {
                continue;
            }

            checkedFaces++;
            Assert.Equal(du0, du1, 4);
            Assert.Equal(dv0, dv1, 4);
        }

        Assert.True(checkedFaces >= 4);

        ModulePlaceResult refused = ModuleInstancer.Place(LevelTemplates.CreateNew(new DateTime(2026, 1, 1)), pil.Info, pil.Brushes, new ModulePlacement { Depth = 2 });
        Assert.False(refused.Ok);
        Assert.Contains("cannot stretch on z", refused.Error!);
        Assert.True(ModuleInstancer.BuildBrushes(pil.Info, pil.Brushes, new ModulePlacement { Depth = pil.Info.Size.Z + 0.0005 }).Ok); // < 1 mm is natural
    }

    /// <summary>d(u, v)/dy along a vertical face (NaN for a face with no vertical extent).</summary>
    private static (double, double) UvPerMetreY(Brush b, int fi)
    {
        Face f = b.Geometry.Faces[fi];
        var pts = f.Vertices.Select(fv => (P: D3.From(b.Geometry.Vertices[fv.Index]), Uv: fv.TextureCoords)).ToList();
        var lo = pts.OrderBy(p => p.P.Y).ThenBy(p => p.P.X).ThenBy(p => p.P.Z).First();
        var hi = pts.Where(p => Math.Abs(p.P.X - lo.P.X) < 1e-4 && Math.Abs(p.P.Z - lo.P.Z) < 1e-4 && p.P.Y > lo.P.Y + 0.1).OrderByDescending(p => p.P.Y).FirstOrDefault();
        if (hi.Uv == default && hi.P == default)
        {
            return (double.NaN, double.NaN);
        }

        double dy = hi.P.Y - lo.P.Y;
        return ((hi.Uv.U - lo.Uv.U) / dy, (hi.Uv.V - lo.Uv.V) / dy);
    }

    [Fact]
    public void Mirror_Keeps_Outward_Faces_And_Force_Detail_Marks_Solids()
    {
        foreach (string cat in new[] { "pilaster", "crate_stack", "door_frame" })
        {
            BuiltModule m = Module(cat);
            ModuleBuild b = ModuleInstancer.BuildBrushes(m.Info, m.Brushes, new ModulePlacement { Mirror = true, YawDegrees = 30 });
            Assert.True(b.Ok, b.Error);
            foreach (Brush brush in b.Brushes)
            {
                bool air = (brush.Flags & (uint)BrushFlags.Air) != 0;
                Assert.Equal(air ? BrushWinding.Inward : BrushWinding.Outward, BrushOrientation.MeasureLocal(brush.Geometry));
                if (!air)
                {
                    Assert.NotEqual(0u, brush.Flags & (uint)BrushFlags.Detail);
                }

                foreach (Face f in brush.Geometry.Faces)
                {
                    // Stored planes agree with the corner winding.
                    var corners = GeometryUtil.Corners(brush.Geometry, f);
                    Assert.True(GeometryUtil.Normal(corners).Dot(f.Plane.Normal) > 0.99f);
                }
            }

            // Mirrored geometry is the X-reflection of the plain placement.
            ModuleBuild plain = ModuleInstancer.BuildBrushes(m.Info, m.Brushes, new ModulePlacement());
            ModuleBuild mirrored = ModuleInstancer.BuildBrushes(m.Info, m.Brushes, new ModulePlacement { Mirror = true });
            SamePoints(plain.Brushes.SelectMany(WorldPoly).Select(v => v with { X = -v.X }), mirrored.Brushes.SelectMany(WorldPoly));
        }

        BuiltModule pil = Module("pilaster");
        ModuleBuild keep = ModuleInstancer.BuildBrushes(pil.Info, pil.Brushes, new ModulePlacement { ForceDetail = false, TextureMap = new Dictionary<string, string> { [ModuleTestLevel.Wall] = "mtl_new.tga" } });
        Assert.All(keep.Brushes, b => Assert.Equal(pil.Brushes[0].Flags & (uint)BrushFlags.Detail, b.Flags & (uint)BrushFlags.Detail));
        Assert.All(keep.Brushes, b => Assert.Contains("mtl_new.tga", b.Geometry.Textures));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Level_With_Placed_Modules_Builds_Sealed_With_No_Holes(bool sharedBsp)
    {
        EditorDocument doc = LevelTemplates.CreateNew(new DateTime(2026, 1, 2));
        var ed = new BrushEditor(doc);
        ModuleTestLevel.Box(ed, new D3(0, 3, 0), new D3(16, 6, 12), ModuleTestLevel.Wall, air: true); // x ∈ [−8, 8], z ∈ [−6, 6]
        var tags = new Dictionary<string, string> { ["kit.module"] = "test" };
        void Place(string cat, ModulePlacement p)
        {
            BuiltModule m = Module(cat);
            ModulePlaceResult r = ModuleInstancer.Place(doc, m.Info, m.Brushes, p);
            Assert.True(r.Ok, $"{cat}: {r.Error}");
        }

        Place("pilaster", new ModulePlacement { Position = new Vec3(-4, 0, 6), YawDegrees = 180, Tags = tags });
        Place("pilaster", new ModulePlacement { Position = new Vec3(0, 0, 6), YawDegrees = 180, Height = 5 });
        Place("pilaster", new ModulePlacement { Position = new Vec3(8, 0, 0), YawDegrees = 270, Length = 1.2, Mirror = true });
        Place("crate_stack", new ModulePlacement { Position = new Vec3(-2, 0, -2), YawDegrees = 37 });
        Place("beam", new ModulePlacement { Position = new Vec3(4, 6, 0), YawDegrees = 90, Length = 12 });
        Place("railing", new ModulePlacement { Position = new Vec3(0, 0, -3), Length = 9 });
        Assert.Equal("test", new GedObjectMetadataService(doc).Tags(ModuleTestLevel.Brushes(doc)[1].Uid)["kit.module"]);

        CompileOptions options = CompileOptionsFactory.Create(doc.Rfl, new BuildSettings { UseSharedBsp = sharedBsp }, new CompileRequest { Sealed = true });
        Assert.Equal(sharedBsp, options.SharedBsp);
        CompiledLevel result = GeometryBuildService.Build(doc.Rfl, options);
        Assert.Equal(sharedBsp, result.Report.SharedBspUsed);
        Assert.Empty(HoleDetector.Detect(result.Geometry));
        Assert.True(result.Geometry.Faces.Count > 6);
    }
}
