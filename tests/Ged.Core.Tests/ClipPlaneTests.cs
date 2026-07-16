using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>The two-point Clip tool's plane derivation.</summary>
public sealed class ClipPlaneTests
{
    [Fact]
    public void FromTwoPoints_Normal_Is_Perp_To_Line_And_View()
    {
        // Two points along world X in a Top view (view direction = -Y).
        var a = new Vec3(-1, 5, 0);
        var b = new Vec3(1, 5, 0);
        (Vec3 point, Vec3 normal) = ClipPlanes.FromTwoPoints(a, b, new Vec3(0, -1, 0));

        Assert.True(point.ApproxEquals(a));
        // (b-a)=+X, view=-Y => normal = X × (-Y) = +Z (up to sign), unit length.
        Assert.Equal(1f, normal.Length(), 3);
        Assert.True(normal.ApproxEquals(new Vec3(0, 0, 1)) || normal.ApproxEquals(new Vec3(0, 0, -1)));
        // Normal is perpendicular to the picked edge and the view direction.
        Assert.Equal(0f, normal.Dot(b.Sub(a)), 3);
        Assert.Equal(0f, normal.Dot(new Vec3(0, -1, 0)), 3);
    }

    [Fact]
    public void FromTwoPoints_Degenerate_Falls_Back()
    {
        var a = new Vec3(0, 0, 0);
        (Vec3 _, Vec3 normal) = ClipPlanes.FromTwoPoints(a, a, new Vec3(0, 1, 0));
        Assert.Equal(new Vec3(0, 0, 1), normal);
    }
}
