using System.Linq;
using Ged.Core.Model;
using Ged.Core.Rooms;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the room-graph render modes: portal traversal on a real
/// 3-room / 2-portal fixture (camera in A sees A+B not C when the B–C portal is
/// blocked), current-room-only, and point-in-room location.
/// </summary>
public sealed class RoomGraphTests
{
    /// <summary>Three rooms in a row A(0)-B(1)-C(2), portals A–B and B–C.</summary>
    private static Geometry ThreeRoomChain()
    {
        var g = new Geometry();
        g.Rooms.Add(new Room { Id = 0x7FFFFFFE, Aabb = new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 10)) });
        g.Rooms.Add(new Room { Id = 0x7FFFFFFD, Aabb = new Aabb(new Vec3(10, 0, 0), new Vec3(20, 10, 10)) });
        g.Rooms.Add(new Room { Id = 0x7FFFFFFC, Aabb = new Aabb(new Vec3(20, 0, 0), new Vec3(30, 10, 10)) });

        // Portal 0: A(0) <-> B(1) on the x=10 wall.
        g.Portals.Add(new Portal { RoomIndex1 = 0, RoomIndex2 = 1, Point1 = new Vec3(10, 0, 0), Point2 = new Vec3(10, 10, 10) });
        // Portal 1: B(1) <-> C(2) on the x=20 wall.
        g.Portals.Add(new Portal { RoomIndex1 = 1, RoomIndex2 = 2, Point1 = new Vec3(20, 0, 0), Point2 = new Vec3(20, 10, 10) });
        return g;
    }

    [Fact]
    public void Traversal_Reaches_All_Rooms_When_Nothing_Blocked()
    {
        RoomGraph graph = RoomGraph.Build(ThreeRoomChain());
        var reached = graph.Reachable(0);
        Assert.Equal(new[] { 0, 1, 2 }, reached.OrderBy(r => r));
    }

    [Fact]
    public void Camera_In_A_Sees_A_And_B_But_Not_C_When_BC_Portal_Blocked()
    {
        RoomGraph graph = RoomGraph.Build(ThreeRoomChain());

        // Block portal index 1 (the B–C portal).
        var reached = graph.Reachable(0, portalIndex => portalIndex == 1);

        Assert.Contains(0, reached);
        Assert.Contains(1, reached);
        Assert.DoesNotContain(2, reached);
    }

    [Fact]
    public void Current_Room_Only_Is_The_Start_Room()
    {
        RoomGraph graph = RoomGraph.Build(ThreeRoomChain());
        // A room with every portal blocked yields only itself (current-room-only mode).
        var reached = graph.Reachable(1, _ => true);
        Assert.Equal(new[] { 1 }, reached.OrderBy(r => r));
    }

    [Fact]
    public void RoomAt_Locates_Point_In_Room()
    {
        RoomGraph graph = RoomGraph.Build(ThreeRoomChain());
        Assert.Equal(0, graph.RoomAt(new Vec3(5, 5, 5)));
        Assert.Equal(1, graph.RoomAt(new Vec3(15, 5, 5)));
        Assert.Equal(2, graph.RoomAt(new Vec3(25, 5, 5)));
        Assert.Equal(-1, graph.RoomAt(new Vec3(100, 100, 100)));
    }

    [Fact]
    public void RoomAt_Prefers_Smallest_Containing_Main_Room()
    {
        var g = new Geometry();
        // A big outer room and a small nested subroom that both contain the point.
        g.Rooms.Add(new Room { Id = 1, Aabb = new Aabb(new Vec3(0, 0, 0), new Vec3(100, 100, 100)) });
        g.Rooms.Add(new Room { Id = 2, Aabb = new Aabb(new Vec3(10, 10, 10), new Vec3(20, 20, 20)) });

        RoomGraph graph = RoomGraph.Build(g);
        // Both main rooms contain (15,15,15); the smaller wins.
        Assert.Equal(1, graph.RoomAt(new Vec3(15, 15, 15)));
    }

    [Fact]
    public void Edges_Are_Recorded_For_Each_Portal()
    {
        RoomGraph graph = RoomGraph.Build(ThreeRoomChain());
        Assert.Equal(2, graph.Edges.Count);
        Assert.Contains(graph.Edges, e => e.A == 0 && e.B == 1);
        Assert.Contains(graph.Edges, e => e.A == 1 && e.B == 2);
    }
}
