using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Kernel-level tests for the BSP boolean engine: the boundary of a union,
/// subtraction, and intersection of axis-aligned boxes, plus point
/// classification. These pin the arithmetic before the compiler builds on it.
/// </summary>
public sealed class CsgKernelTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Union_Of_Disjoint_Boxes_Keeps_All_Faces()
    {
        var a = CsgTestShapes.Box(V(0, 0, 0), V(2, 2, 2));
        var b = CsgTestShapes.Box(V(5, 0, 0), V(7, 2, 2));

        List<CsgFace> result = BspSolid.Union(a, b);

        // Disjoint: total surface area is the sum of both boxes (2 boxes * 6 * 4 = 48).
        Assert.Equal(48f, CsgTestShapes.TotalArea(result), 2);
    }

    [Fact]
    public void Union_Of_Overlapping_Boxes_Removes_Interior_Walls()
    {
        // Two 2x2x2 boxes sharing an X face region; union is a 4x2x2 box (area 40).
        var a = CsgTestShapes.Box(V(0, 0, 0), V(2, 2, 2));
        var b = CsgTestShapes.Box(V(2, 0, 0), V(4, 2, 2));

        List<CsgFace> result = BspSolid.Union(a, b);

        Assert.Equal(40f, CsgTestShapes.TotalArea(result), 1);
    }

    [Fact]
    public void Subtract_Interior_Box_Adds_Cavity_Walls()
    {
        // Big box minus a fully-interior small box = outer shell + inner cavity.
        var big = CsgTestShapes.Box(V(0, 0, 0), V(10, 10, 10));
        var small = CsgTestShapes.Box(V(4, 4, 4), V(6, 6, 6));

        List<CsgFace> result = BspSolid.Subtract(big, small);

        // Outer 10^3 shell (600) + inner 2^3 cavity (24).
        Assert.Equal(624f, CsgTestShapes.TotalArea(result), 1);
    }

    [Fact]
    public void ClassifyPoint_Inside_And_Outside()
    {
        BspSolid.Node solid = BspSolid.Node.Build(Clone(CsgTestShapes.Box(V(0, 0, 0), V(4, 4, 4))));

        Assert.Equal(-1, solid.ClassifyPoint(V(2, 2, 2))); // centre inside
        Assert.Equal(+1, solid.ClassifyPoint(V(9, 2, 2))); // outside
        Assert.Equal(+1, solid.ClassifyPoint(V(-1, 2, 2)));
    }

    private static List<CsgFace> Clone(List<CsgFace> src)
    {
        var list = new List<CsgFace>();
        foreach (CsgFace f in src)
        {
            list.Add(f.With(new List<CsgVertex>(f.Vertices)));
        }

        return list;
    }
}
