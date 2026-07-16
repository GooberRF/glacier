using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Fixture-level tests for the geometry compiler: the canonical single-box,
/// pillar, detail, and coplanar-dedup cases pinned by structure (room/face
/// counts, inward normals, no z-fighting duplicates).
/// </summary>
public sealed class CompilerFixtureTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Single_Air_Box_Is_One_Room_Six_Inward_Faces()
    {
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 8, 6, 10) };

        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;

        Assert.Single(g.Rooms);
        Assert.Equal(6, g.Faces.Count);
        Assert.Empty(g.Portals);

        // Every face normal points into the room (toward its centre).
        Vec3 center = CompilerTestBrushes.RoomCenter(g.Rooms[0]);
        foreach (Face f in g.Faces)
        {
            Vec3 fc = FaceCentroid(g, f);
            Vec3 toCenter = center.Sub(fc);
            Assert.True(f.Plane.Normal.Dot(toCenter) > 0f,
                $"face normal {f.Plane.Normal} should point inward");

            // Stored offset is RF's convention: n·p + offset == 0 on the plane.
            Assert.True(System.MathF.Abs(f.Plane.Normal.Dot(fc) + f.Plane.Offset) < 1e-2f);
            Assert.Equal(0, f.RoomIndex);
        }
    }

    [Fact]
    public void Solid_Pillar_In_Room_Adds_Walls_No_Interior_Faces()
    {
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 20, 10, 20),
            CompilerTestBrushes.SolidBox(2, V(0, 0, 0), 2, 10, 2), // full-height pillar through the room
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;

        // One room (the pillar is an obstacle, not a separate room).
        Assert.Single(g.Rooms);

        // Pillar contributes 4 side walls; top/bottom coincide with ceiling/floor and are
        // absorbed. The 4 room side walls stay whole; floor and ceiling are split around
        // the pillar (a rectangle with a hole => multiple convex fragments).
        Assert.True(g.Faces.Count > 6, $"expected split floor/ceiling + pillar walls, got {g.Faces.Count}");

        // No two faces should be coincident with opposite normals inside the room (z-fighting).
        Assert.False(HasOpposedCoincidentPair(g), "found opposed coincident faces (z-fighting)");

        // Pillar side faces exist: vertical faces near x=±1 / z=±1.
        int pillarWalls = g.Faces.Count(f => IsVerticalNear(g, f, 1f));
        Assert.True(pillarWalls >= 4, $"expected >=4 pillar side walls, got {pillarWalls}");
    }

    [Fact]
    public void Detail_Brush_Is_Its_Own_Subroom_Attached_To_Parent()
    {
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 20, 10, 20),
            CompilerTestBrushes.DetailBox(2, V(0, -3, 0), 2, 2, 2, life: 100),
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;

        // Main room + one detail subroom.
        Assert.Equal(2, g.Rooms.Count);
        Assert.Equal(1, g.Rooms.Count(r => r.IsSubroom != 0));

        Room detail = g.Rooms.First(r => r.IsSubroom != 0);
        Assert.Equal(100f, detail.Life); // life inherited from the brush

        // Detail faces are flagged detail and did not split the main room.
        int detailFaces = g.Faces.Count(f => ((FaceFlags)f.Flags & FaceFlags.IsDetail) != 0);
        Assert.Equal(6, detailFaces);

        // Subroom containment list: the detail attaches to the main room (index 0).
        Assert.Contains(g.SubroomLists, sl => sl.SubroomIndices.Count > 0);
    }

    [Fact]
    public void Two_Air_Boxes_Joined_By_Portal_Are_Two_Rooms_One_Portal()
    {
        // A big room and a small room abut, leaving a doorway hole; a portal brush
        // fills the doorway. The membrane divides the flood fill into two rooms and
        // a single portal record links them, its AABB the doorway rectangle.
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(-5, 0, 0), 10, 10, 10),
            CompilerTestBrushes.AirBox(2, V(5, 0, 0), 10, 4, 4),
            PortalSlab(3, V(0, 0, 0), 0.4f, 4, 4),
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;

        Assert.Equal(2, g.Rooms.Count);
        Assert.Single(g.Portals);

        Portal p = g.Portals[0];
        var pairs = new HashSet<int> { p.RoomIndex1, p.RoomIndex2 };
        Assert.Equal(2, pairs.Count); // links two distinct rooms

        // Portal AABB is the doorway opening (x = 0, y/z spanning ±2).
        Assert.True(System.MathF.Abs(p.Point1.X) < 0.1f && System.MathF.Abs(p.Point2.X) < 0.1f);
        Assert.True(p.Point2.Y - p.Point1.Y > 3.5f && p.Point2.Z - p.Point1.Z > 3.5f);

        // Two portal faces (texture -1) exist, tagged with portal_index_plus_2.
        var portalFaces = g.Faces.Where(f => f.Texture < 0).ToList();
        Assert.Equal(2, portalFaces.Count);
        Assert.All(portalFaces, f => Assert.True(f.PortalIndexPlus2 >= 2));
    }

    [Fact]
    public void Ceiling_Overhang_Past_A_Wall_Is_Dropped_By_The_Accumulator()
    {
        // The archetype the per-brush BSP clip-and-classify fixes (dmabrupt air-ceiling brush #53
        // overhanging solid brush #537): a ceiling slab whose bottom face extends PAST the room's
        // +X wall, over solid space. RED's mutual clip cuts it at the wall plane and drops the
        // beyond-wall piece; GED's old independent splitter left it hanging over solid (a leak).
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 20, 10, 20),           // room x∈[-10,10], y∈[-5,5]
            CompilerTestBrushes.SolidBox(2, V(5, 6, 0), 30, 2, 20),          // ceiling slab x∈[-10,20], y∈[5,7]
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false });
        Geometry g = c.Geometry;

        // Watertight: the overhang was cut at the wall plane, not left as an open ribbon.
        Assert.Empty(HoleDetector.Detect(g));

        // A down-facing ceiling exists over the room (x < 10)…
        Assert.Contains(g.Faces, f => f.Texture >= 0 && f.Plane.Normal.Y < -0.9f &&
            FaceCentroid(g, f).Y is > 4.5f and < 5.5f && FaceCentroid(g, f).X < 10f);

        // …but NO surviving down-facing fragment overhangs past the wall into solid space (x > 10.5).
        Assert.DoesNotContain(g.Faces, f => f.Texture >= 0 && f.Plane.Normal.Y < -0.9f &&
            FaceCentroid(g, f).Y is > 4.5f and < 5.5f && FaceCentroid(g, f).X > 10.5f);
    }

    [Fact]
    public void Coplanar_Solid_On_Air_Wall_Does_Not_Duplicate_The_Wall()
    {
        // A solid box flush against the room's floor: the shared floor plane must not
        // produce a doubled, z-fighting face.
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 20, 10, 20),
            CompilerTestBrushes.SolidBox(2, V(0, -5, 0), 6, 2, 6), // sits on the floor (y=-5)
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;

        Assert.Single(g.Rooms);
        Assert.False(HasOpposedCoincidentPair(g), "flush solid duplicated a wall (z-fighting)");
    }

    [Fact]
    public void Liquid_Room_Gets_Liquid_Props_And_A_Surface_Quad()
    {
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 10, 10) };
        var effect = new Ged.Core.Model.RoomEffect
        {
            EffectType = Ged.Core.IO.Rfl.Sections.RoomEffectsSection.EffectLiquidRoom,
            LiquidProperties = new Ged.Core.Model.RoomEffectLiquidProperties
            {
                Depth = 4f,
                LiquidType = 1,
                SurfaceTexture = "water.tga",
                Waveform = 2,
            },
            Header = new Ged.Core.Model.ObjectHeader { Uid = 5000, Position = V(0, 0, 0) },
        };

        CompiledLevel c = GeometryCompiler.Compile(brushes, new[] { effect });
        Geometry g = c.Geometry;

        Room liquid = Assert.Single(g.Rooms, r => r.IsLiquidRoom != 0);
        Assert.NotNull(liquid.LiquidProperties);
        Assert.Equal("water.tga", liquid.LiquidProperties!.SurfaceTexture);
        Assert.Equal(5000, liquid.Id);

        // RED emits the liquid surface as a DOUBLE-SIDED sub-manifold: an up-facing front (into the
        // air above) and a down-facing back (into the water) at the liquid level, both flagged and in
        // the liquid room. GED matches (flagship 17 — a single side back-face-culled from one view).
        List<Face> surfs = g.Faces.Where(f => ((FaceFlags)f.Flags & FaceFlags.LiquidSurface) != 0).ToList();
        Assert.Contains(surfs, f => f.Plane.Normal.Y > 0.9f);
        Assert.Contains(surfs, f => f.Plane.Normal.Y < -0.9f);
        Assert.All(surfs, f => Assert.Equal(liquid, g.Rooms[f.RoomIndex]));
    }

    [Fact]
    public void Surface_Uv_Transform_Matches_The_Spec_Formulas()
    {
        // An 8×8×8 air box: each 8&#160;m wall at lightmap resolution 0 (ppm = 1) makes
        // a 10×10 fragment; verify the surface + uv transform per §B.6 exactly.
        var brushes = new List<Brush> { CompilerTestBrushes.AirBox(1, V(0, 0, 0), 8, 8, 8) };

        CompiledLevel c = GeometryCompiler.Compile(brushes);
        Geometry g = c.Geometry;

        Assert.Equal(6, g.Surfaces.Count);
        Assert.Single(c.Lightmaps);
        Assert.Equal(128, c.Lightmaps[0].Width);
        Assert.Equal(128 * 128 * 3, c.Lightmaps[0].Pixels.Length);

        Surface s = g.Surfaces[0];
        Assert.Equal(10, s.W); // round(8 * 1) + 2
        Assert.Equal(10, s.H);
        Assert.Equal(1f, s.XPixelsPerMeter);

        // uv_scale = ((w-2)/128) / extent = (8/128)/8 = 0.0078125
        Assert.Equal(0.0078125f, s.UvScale.U, 5);
        Assert.Equal(0.0078125f, s.UvScale.V, 5);

        // Every bound face's lightmap UVs are clamped into [0,1].
        foreach (Face f in g.Faces)
        {
            if (f.SurfaceIndex < 0)
            {
                continue;
            }

            foreach (FaceVertex fv in f.Vertices)
            {
                Assert.NotNull(fv.LightmapCoords);
                Uv lm = fv.LightmapCoords!.Value;
                Assert.InRange(lm.U, 0f, 1f);
                Assert.InRange(lm.V, 0f, 1f);
            }
        }
    }

    // ---- helpers ----

    private static Brush PortalSlab(int uid, Vec3 center, float thickness, float h, float d)
    {
        Brush b = CompilerTestBrushes.MakeBox(uid, center, thickness, h, d,
            Ged.Core.Model.BrushFlags.Air | Ged.Core.Model.BrushFlags.Portal, "wall");
        return b;
    }

    private static Vec3 FaceCentroid(Geometry g, Face f)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (FaceVertex fv in f.Vertices)
        {
            sum = sum.Add(g.Vertices[fv.Index]);
        }

        return sum.Scale(1f / f.Vertices.Count);
    }

    private static bool IsVerticalNear(Geometry g, Face f, float coord)
    {
        if (System.MathF.Abs(f.Plane.Normal.Y) > 0.3f)
        {
            return false; // not a vertical wall
        }

        Vec3 fc = FaceCentroid(g, f);
        return System.MathF.Abs(System.MathF.Abs(fc.X) - coord) < 0.2f ||
               System.MathF.Abs(System.MathF.Abs(fc.Z) - coord) < 0.2f;
    }

    private static bool HasOpposedCoincidentPair(Geometry g)
    {
        for (int i = 0; i < g.Faces.Count; i++)
        {
            for (int j = i + 1; j < g.Faces.Count; j++)
            {
                Face a = g.Faces[i], b = g.Faces[j];
                float dot = a.Plane.Normal.Dot(b.Plane.Normal);
                if (dot < -0.999f && System.MathF.Abs(a.Plane.Offset + b.Plane.Offset) < 1e-2f &&
                    FaceCentroid(g, a).ApproxEquals(FaceCentroid(g, b), 0.1f))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
