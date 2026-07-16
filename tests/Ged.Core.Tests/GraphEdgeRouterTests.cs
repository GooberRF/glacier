using System;
using System.Collections.Generic;
using Ged.Core.Editing.Graph;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the framework-free edge router: an unobstructed edge is a single smooth
/// S-curve pinned to the ports; an obstacle sitting on the straight corridor forces a
/// detour whose flattened path stays clear of the inflated rect (including stacked
/// obstacles and backward edges); the flattened polyline is exposed for hit-testing.
/// </summary>
public sealed class GraphEdgeRouterTests
{
    private static readonly GraphRect Src = new(0, 0, 168, 46);

    [Fact]
    public void Unobstructed_Edge_Is_One_Smooth_Segment_Between_Ports()
    {
        var dst = new GraphRect(400, 100, 168, 46);
        GraphEdgePath path = GraphEdgeRouter.Route(Src, dst, Array.Empty<GraphRect>());

        GraphBezierSegment seg = Assert.Single(path.Segments);
        Assert.Equal(new GraphPoint(168, 23), seg.P0);   // right-middle of source
        Assert.Equal(new GraphPoint(400, 123), seg.P1);  // left-middle of target
        // Horizontal S-curve tangents at both ports.
        Assert.Equal(seg.P0.Y, seg.C1.Y, 6);
        Assert.Equal(seg.P1.Y, seg.C2.Y, 6);
        Assert.True(seg.C1.X > seg.P0.X);
        Assert.True(seg.C2.X < seg.P1.X);
        // Flattened polyline spans the same ports.
        Assert.Equal(seg.P0, path.Polyline[0]);
        Assert.Equal(seg.P1, path.Polyline[^1]);
    }

    [Fact]
    public void Obstacle_On_The_Straight_Corridor_Is_Detoured_Around()
    {
        var dst = new GraphRect(600, 0, 168, 46);       // same Y → straight corridor at y=23
        var obstacle = new GraphRect(330, 0, 168, 46);  // parked right on the corridor

        GraphEdgePath path = GraphEdgeRouter.Route(Src, dst, new[] { obstacle });

        Assert.False(path.Intersects(obstacle.Inflate(GraphEdgeRouter.DefaultMargin)),
            "routed path still passes under the obstacle");
        Assert.True(path.Segments.Count >= 2, "expected a detour waypoint");
        Assert.Equal(new GraphPoint(168, 23), path.Start);
        Assert.Equal(new GraphPoint(600, 23), path.End);
    }

    [Fact]
    public void Two_Stacked_Obstacles_Are_Both_Avoided()
    {
        var dst = new GraphRect(800, 0, 168, 46);
        var obstacles = new List<GraphRect>
        {
            new(300, -10, 168, 46),
            new(520, 10, 168, 46),
        };

        GraphEdgePath path = GraphEdgeRouter.Route(Src, dst, obstacles);

        foreach (GraphRect ob in obstacles)
        {
            Assert.False(path.Intersects(ob.Inflate(GraphEdgeRouter.DefaultMargin)),
                $"routed path still passes under obstacle {ob}");
        }

        Assert.Equal(new GraphPoint(168, 23), path.Start);
        Assert.Equal(new GraphPoint(800, 23), path.End);
    }

    [Fact]
    public void Off_Corridor_Obstacle_Keeps_The_Simple_S_Curve()
    {
        var dst = new GraphRect(600, 0, 168, 46);
        var farAway = new GraphRect(300, 400, 168, 46); // nowhere near the corridor

        GraphEdgePath path = GraphEdgeRouter.Route(Src, dst, new[] { farAway });
        Assert.Single(path.Segments);
    }

    [Fact]
    public void Obstacle_Containing_A_Port_Is_Ignored_Instead_Of_Looping()
    {
        var dst = new GraphRect(600, 0, 168, 46);
        var overlapping = new GraphRect(150, 0, 168, 46); // swallows the source port

        GraphEdgePath path = GraphEdgeRouter.Route(Src, dst, new[] { overlapping });
        Assert.Single(path.Segments); // unavoidable → not treated as an obstacle
    }

    [Fact]
    public void Backward_Edge_Still_Starts_And_Ends_At_The_Ports()
    {
        var dst = new GraphRect(-400, 200, 168, 46); // target left of source → loop-back
        GraphEdgePath path = GraphEdgeRouter.Route(Src, dst, Array.Empty<GraphRect>());

        Assert.Equal(new GraphPoint(168, 23), path.Start);
        Assert.Equal(new GraphPoint(-400, 223), path.End);
        // Exit rightward from the source, enter leftward into the target.
        Assert.True(path.Segments[0].C1.X > path.Start.X);
        Assert.True(path.Segments[^1].C2.X < path.End.X);
    }

    [Fact]
    public void Segment_Rect_Intersection_Matches_Expectations()
    {
        var r = new GraphRect(10, 10, 20, 20);
        Assert.True(GraphEdgeRouter.SegmentIntersectsRect(new GraphPoint(0, 20), new GraphPoint(40, 20), r));   // straight through
        Assert.True(GraphEdgeRouter.SegmentIntersectsRect(new GraphPoint(15, 15), new GraphPoint(16, 16), r));  // fully inside
        Assert.False(GraphEdgeRouter.SegmentIntersectsRect(new GraphPoint(0, 40), new GraphPoint(40, 40), r));  // below
        Assert.False(GraphEdgeRouter.SegmentIntersectsRect(new GraphPoint(0, 0), new GraphPoint(5, 40), r));    // left of it
    }
}
