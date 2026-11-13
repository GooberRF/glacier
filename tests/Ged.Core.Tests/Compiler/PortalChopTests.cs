using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// RED's two room rules recovered for the room/portal parity work (compiler-parity-notes.md "Air portal brushes
/// and inside-out rooms"): an air portal brush is applied after every ordinary brush and leaves a two-sided portal
/// face wherever it removes a boundary face (boolean mode 4), so the doorway it opens is a room of its own; and a
/// free-standing lump of solid is a detail room, not a main room.
/// </summary>
public sealed class PortalChopTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    private static Brush AirPortalBox(int uid, Vec3 center, float w, float h, float d) =>
        CompilerTestBrushes.MakeBox(uid, center, w, h, d, BrushFlags.Air | BrushFlags.Portal, "wall");

    /// <summary>Two 8 m rooms with 2 m of rock between them (x in [-1, 1]).</summary>
    private static List<Brush> TwoRooms() => new()
    {
        CompilerTestBrushes.AirBox(1, V(-5, 0, 0), 8, 8, 8),
        CompilerTestBrushes.AirBox(2, V(5, 0, 0), 8, 8, 8),
    };

    private static CompileOptions Options(bool sharedBsp, bool chop = true) =>
        new() { BuildSurfaces = false, SharedBsp = sharedBsp, PortalChop = chop };

    private static List<Face> PortalFaces(Geometry g) => g.Faces.Where(f => f.PortalIndexPlus2 >= 2).ToList();

    private static float Area(Geometry g, Face f)
    {
        Vec3 sum = default;
        Vec3 a = g.Vertices[f.Vertices[0].Index];
        for (int i = 1; i + 1 < f.Vertices.Count; i++)
        {
            sum = sum.Add(g.Vertices[f.Vertices[i].Index].Sub(a).Cross(g.Vertices[f.Vertices[i + 1].Index].Sub(a)));
        }

        return sum.Length() * 0.5f;
    }

    private static int MainRooms(Geometry g) => g.Rooms.Count(r => r.IsSubroom == 0);

    [Theory]
    [InlineData(true)]  // RED-authentic Shared BSP
    [InlineData(false)] // Incremental
    public void Air_Portal_Box_Through_A_Wall_Is_Its_Own_Room_With_A_Portal_At_Each_End(bool sharedBsp)
    {
        List<Brush> brushes = TwoRooms();
        brushes.Add(AirPortalBox(3, V(0, 0, 0), 2, 3, 3)); // fills the rock gap exactly: ends flush with both walls

        CompiledLevel c = GeometryCompiler.Compile(brushes, null, Options(sharedBsp));
        Geometry g = c.Geometry;
        Assert.Equal(sharedBsp, c.Report.SharedBspUsed);

        Assert.Equal(3, MainRooms(g));
        Assert.Equal(2, g.Portals.Count);

        // Each end of the doorway carries one portal face per side, each the 3 x 3 cross-section, on the wall plane.
        List<Face> portalFaces = PortalFaces(g);
        Assert.Equal(4, portalFaces.Count);
        Assert.All(portalFaces, f => Assert.Equal(9f, Area(g, f), 2));
        Assert.All(portalFaces, f => Assert.All(f.Vertices, v => Assert.Equal(1f, MathF.Abs(g.Vertices[v.Index].X), 3)));
        Assert.Equal(2, portalFaces.Count(f => g.Vertices[f.Vertices[0].Index].X < 0));

        // The doorway room is the one both portals share; each portal links it to one of the big rooms.
        int doorway = new[] { g.Portals[0].RoomIndex1, g.Portals[0].RoomIndex2 }
            .Intersect(new[] { g.Portals[1].RoomIndex1, g.Portals[1].RoomIndex2 }).Single();
        Aabb box = g.Rooms[doorway].Aabb;
        Assert.Equal(-1f, box.P1.X, 2);
        Assert.Equal(1f, box.P2.X, 2);
        Assert.Empty(HoleDetector.Detect(g));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Wall_Face_Inside_The_Portal_Brush_Becomes_The_Portal(bool sharedBsp)
    {
        // The box pokes half a metre into each room, so the walls it opens lie INSIDE it, not on its faces.
        List<Brush> brushes = TwoRooms();
        brushes.Add(AirPortalBox(3, V(0, 0, 0), 3, 3, 3));

        Geometry g = GeometryCompiler.Compile(brushes, null, Options(sharedBsp)).Geometry;

        Assert.Equal(3, MainRooms(g));
        Assert.Equal(2, g.Portals.Count);
        List<Face> portalFaces = PortalFaces(g);
        Assert.Equal(4, portalFaces.Count);
        Assert.All(portalFaces, f => Assert.All(f.Vertices, v => Assert.Equal(1f, MathF.Abs(g.Vertices[v.Index].X), 3)));
        Assert.Empty(HoleDetector.Detect(g));
    }

    [Fact]
    public void Portal_Brush_Is_Applied_After_Every_Brush_Whatever_Its_Place_In_The_List()
    {
        // RED keeps portal brushes in their own list and runs it after the brush list.
        List<Brush> first = TwoRooms();
        first.Insert(0, AirPortalBox(3, V(0, 0, 0), 2, 3, 3));
        List<Brush> last = TwoRooms();
        last.Add(AirPortalBox(3, V(0, 0, 0), 2, 3, 3));

        Geometry a = GeometryCompiler.Compile(first, null, Options(true)).Geometry;
        Geometry b = GeometryCompiler.Compile(last, null, Options(true)).Geometry;

        Assert.Equal(3, MainRooms(a));
        Assert.Equal(2, a.Portals.Count);
        Assert.Equal(b.Faces.Count, a.Faces.Count);
        Assert.Equal(b.Rooms.Count, a.Rooms.Count);
        Assert.Equal(PortalFaces(b).Count, PortalFaces(a).Count);
    }

    [Fact]
    public void A_Duplicate_Portal_Brush_Adds_No_Second_Set_Of_Portal_Faces()
    {
        // dm06 stacks identical portal boxes (uid 696/699, 818/821, ...): RED's output has one pair per end.
        List<Brush> brushes = TwoRooms();
        brushes.Add(AirPortalBox(3, V(0, 0, 0), 2, 3, 3));
        brushes.Add(AirPortalBox(4, V(0, 0, 0), 2, 3, 3));

        Geometry g = GeometryCompiler.Compile(brushes, null, Options(true)).Geometry;

        Assert.Equal(3, MainRooms(g));
        Assert.Equal(2, g.Portals.Count);
        Assert.Equal(4, PortalFaces(g).Count);
    }

    [Fact]
    public void With_The_Chop_Off_The_Brush_Gets_One_Centre_Plane_Membrane()
    {
        List<Brush> brushes = TwoRooms();
        brushes.Add(AirPortalBox(3, V(0, 0, 0), 2, 3, 3));

        Geometry g = GeometryCompiler.Compile(brushes, null, Options(true, chop: false)).Geometry;

        Assert.Equal(2, MainRooms(g));
        Assert.Single(g.Portals);
        Assert.All(PortalFaces(g), f => Assert.All(f.Vertices, v => Assert.Equal(0f, g.Vertices[v.Index].X, 3)));
    }

    [Fact]
    public void A_Detail_Brush_Across_A_Portal_Stays_One_Detail_Room()
    {
        // RED's flood looks at the portal faces around a face only while it floods a non-detail room.
        List<Brush> brushes = TwoRooms();
        brushes.Add(AirPortalBox(3, V(0, 0, 0), 2, 3, 3));
        brushes.Add(CompilerTestBrushes.DetailBox(4, V(-1, 0, 0), 1, 1, 1)); // straddles the x = -1 portal plane

        Geometry g = GeometryCompiler.Compile(brushes, null, Options(true)).Geometry;

        Assert.Equal(3, MainRooms(g));
        Assert.Equal(1, g.Rooms.Count(r => r.IsSubroom != 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Free_Standing_Solid_Is_A_Detail_Room(bool sharedBsp)
    {
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 10, 10),
            CompilerTestBrushes.SolidBox(2, V(0, 0, 0), 2, 2, 2), // floats in the middle, touching nothing
        };

        Geometry g = GeometryCompiler.Compile(brushes, null, Options(sharedBsp)).Geometry;
        Assert.Equal(1, MainRooms(g));
        Assert.Equal(1, g.Rooms.Count(r => r.IsSubroom != 0));
        Assert.Contains(g.SubroomLists, sl => sl.SubroomIndices.Count == 1);

        Geometry off = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false, SharedBsp = sharedBsp, InsideOutRooms = false }).Geometry;
        Assert.Equal(2, MainRooms(off));
    }

    [Fact]
    public void A_Sealed_Air_Pocket_And_A_Solid_On_The_Floor_Stay_Rooms()
    {
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(0, 0, 0), 10, 10, 10),
            CompilerTestBrushes.SolidBox(2, V(0, -4, 0), 2, 2, 2),  // stands on the floor: part of the room
            CompilerTestBrushes.AirBox(3, V(20, 0, 0), 4, 4, 4),     // a sealed pocket: walls seen from inside
        };

        Geometry g = GeometryCompiler.Compile(brushes, null, new CompileOptions { BuildSurfaces = false }).Geometry;
        Assert.Equal(2, MainRooms(g));
        Assert.Equal(0, g.Rooms.Count(r => r.IsSubroom != 0));
    }
}
