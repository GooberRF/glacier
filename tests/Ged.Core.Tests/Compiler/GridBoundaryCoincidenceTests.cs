using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Regression: the CSG broad-phase grid (12 m cells) must not drop a brush whose AABB starts exactly on a
/// cell boundary when the querying face lies a few micrometres on the other side of it. Yaw-rotated brushes
/// carry an Up row of length 1.0000002 (float Orthonormalize), so their y=0 floor lands at y≈-3.8e-6; once
/// such a brush interns the y=0 registry plane first, every later floor cap snapped to that plane floors
/// into grid row -1 and never saw the axis-exact neighbour starting at y=0 — the overlap was not clipped,
/// both coplanar floors were emitted (z-fighting) and the mismatched fragment boundaries left open edges.
/// </summary>
public sealed class GridBoundaryCoincidenceTests
{
    private static Brush RotatedAir(int uid, Vec3 pos, float w, float h, float d, Mat3 rot)
    {
        Brush b = CompilerTestBrushes.AirBox(uid, pos, w, h, d);
        b.Rotation = rot;
        return b;
    }

    // The exact stored rotations of a script-built canyon (level.brush{rot={yaw=...}}): note the non-unit Up.
    private static List<Brush> ThreeSegments(bool includeDisjoint)
    {
        var list = new List<Brush>();
        if (includeDisjoint)
        {
            list.Add(RotatedAir(1, new Vec3(9.536023f, 14f, 6f), 13.0f, 28f, 19.2349f,
                new Mat3(new Vec3(0.42178276f, 0f, 0.90669703f), new Vec3(0.90669703f, 0f, -0.42178276f), new Vec3(-0f, 1.0000002f, 0f))));
        }

        list.Add(RotatedAir(2, new Vec3(12.745031f, 14f, 30f), 15.9838f, 28f, 18.0069f,
            new Mat3(new Vec3(-0.0338243f, 0f, 0.9994278f), new Vec3(0.9994278f, 0f, 0.0338243f), new Vec3(0f, 1f, -0f))));
        list.Add(RotatedAir(3, new Vec3(13.825087f, 14f, 42f), 15.1268f, 28f, 18.2713f,
            new Mat3(new Vec3(0.20912452f, 0f, 0.9778891f), new Vec3(0.9778891f, 0f, -0.20912452f), new Vec3(-0f, 1.0000002f, 0f))));
        return list;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Overlapping_Rotated_Air_Floors_Stay_Watertight_With_A_Disjoint_Rotated_Brush(bool sharedBsp)
    {
        foreach (bool withDisjoint in new[] { false, true })
        {
            CompiledLevel level = GeometryCompiler.Compile(
                ThreeSegments(withDisjoint), new List<RoomEffect>(),
                new CompileOptions { BuildSurfaces = false, SharedBsp = sharedBsp });
            int holes = HoleDetector.Detect(level.Geometry).Count;
            Assert.True(holes == 0, $"sharedBsp={sharedBsp} disjoint={withDisjoint}: {holes} open edges");
        }
    }
}
