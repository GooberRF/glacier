using System.Collections.Generic;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>Pure (GPU-free) checks of the overlay geometry math.</summary>
public sealed class OverlayBuilderTests
{
    [Fact]
    public void Path_Interpolates_Through_Its_Endpoints()
    {
        var pts = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 0, 10) };
        Assert.Equal(new Vec3(0, 0, 0), OverlayBuilder.SamplePath(pts, 0f));
        Assert.Equal(new Vec3(10, 0, 10), OverlayBuilder.SamplePath(pts, 1f));

        // Midpoint of the whole path lands on the middle control point.
        Vec3 mid = OverlayBuilder.SamplePath(pts, 0.5f);
        Assert.Equal(10f, mid.X, 3);
    }

    [Fact]
    public void Path_And_Disc_Produce_Line_Sets()
    {
        var pts = new List<Vec3> { new(0, 0, 0), new(5, 5, 0) };
        Assert.NotEmpty(OverlayBuilder.Path(pts));
        Assert.NotEmpty(OverlayBuilder.Disc(new Vec3(0, 0, 0), 2f));
        Assert.Empty(OverlayBuilder.Disc(new Vec3(0, 0, 0), 0f)); // zero radius = no disc
        Assert.Equal(12, OverlayBuilder.Box(Vec3.Zero, Mat3.Identity, new Vec3(2, 2, 2)).Count);
    }

    [Fact]
    public void CameraCone_Emits_Apex_Rays_And_A_Far_Rectangle()
    {
        var lines = OverlayBuilder.CameraCone(new Vec3(0, 0, 0), Mat3.Identity, 45f, 4f);
        Assert.Equal(8, lines.Count); // 4 apex rays + 4 far-rect edges
    }

    [Fact]
    public void EventFacingArrow_Points_Along_Forward_With_A_Head()
    {
        var pos = new Vec3(5, 1, -2);

        // Identity forward is +Z: shaft runs from pos to pos + (0,0,length), plus a 2-line head.
        var lines = OverlayBuilder.EventFacingArrow(pos, Mat3.Identity, 3f);
        Assert.Equal(3, lines.Count); // 1 shaft + 2 arrowhead wings
        Assert.Equal(new System.Numerics.Vector3(5, 1, -2), lines[0].A);
        Assert.Equal(new System.Numerics.Vector3(5, 1, 1), lines[0].B); // -2 + 3 = +1 on Z

        // A rotated event faces its own forward vector (only Forward is consulted).
        var facingX = new Mat3(new Vec3(1, 0, 0), default, default);
        var xed = OverlayBuilder.EventFacingArrow(pos, facingX, 2f);
        Assert.Equal(new System.Numerics.Vector3(7, 1, -2), xed[0].B); // 5 + 2 on X
    }

    [Fact]
    public void EventFacingArrow_Empty_For_Zero_Length_Or_Degenerate_Forward()
    {
        Assert.Empty(OverlayBuilder.EventFacingArrow(Vec3.Zero, Mat3.Identity, 0f));
        var noForward = new Mat3(Vec3.Zero, default, default);
        Assert.Empty(OverlayBuilder.EventFacingArrow(Vec3.Zero, noForward, 3f));
    }
}
