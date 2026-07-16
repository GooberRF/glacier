using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Lighting;

public sealed class OccluderBvhTests
{
    private static (Vec3, Vec3, Vec3) Quad2(Vec3 a, Vec3 b, Vec3 c) => (a, b, c);

    [Fact]
    public void Ray_Through_Triangle_Is_Occluded()
    {
        // A 2x2 wall in the z=0 plane centred at origin (two triangles).
        var tris = new List<(Vec3, Vec3, Vec3)>
        {
            (new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(1, 1, 0)),
            (new Vec3(-1, -1, 0), new Vec3(1, 1, 0), new Vec3(-1, 1, 0)),
        };
        OccluderBvh bvh = OccluderBvh.Build(tris);
        Assert.Equal(2, bvh.TriangleCount);
        Assert.False(bvh.IsEmpty);

        // Ray from -z to +z straight through the middle → blocked.
        Assert.True(bvh.Occluded(new Vec3(0, 0, -2), new Vec3(0, 0, 2)));
    }

    [Fact]
    public void Ray_Missing_Triangle_Is_Not_Occluded()
    {
        var tris = new List<(Vec3, Vec3, Vec3)>
        {
            (new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(1, 1, 0)),
        };
        OccluderBvh bvh = OccluderBvh.Build(tris);

        // Ray passing well beside the triangle.
        Assert.False(bvh.Occluded(new Vec3(5, 5, -2), new Vec3(5, 5, 2)));
    }

    [Fact]
    public void Endpoint_On_Surface_Does_Not_Self_Shadow()
    {
        var tris = new List<(Vec3, Vec3, Vec3)>
        {
            (new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(1, 1, 0)),
            (new Vec3(-1, -1, 0), new Vec3(1, 1, 0), new Vec3(-1, 1, 0)),
        };
        OccluderBvh bvh = OccluderBvh.Build(tris);

        // Origin just off the wall, target away from it — must not report the wall itself.
        Assert.False(bvh.Occluded(new Vec3(0, 0, 0.02f), new Vec3(0, 3, 3)));
    }

    [Fact]
    public void Empty_Set_Never_Occludes()
    {
        OccluderBvh bvh = OccluderBvh.Build(new List<(Vec3, Vec3, Vec3)>());
        Assert.True(bvh.IsEmpty);
        Assert.False(bvh.Occluded(new Vec3(0, 0, -1), new Vec3(0, 0, 1)));
    }

    [Fact]
    public void Many_Triangles_Occlusion_Is_Detected()
    {
        // A grid of walls; a ray crossing several must be occluded.
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int i = 0; i < 50; i++)
        {
            float z = i;
            tris.Add((new Vec3(-1, -1, z), new Vec3(1, -1, z), new Vec3(1, 1, z)));
            tris.Add((new Vec3(-1, -1, z), new Vec3(1, 1, z), new Vec3(-1, 1, z)));
        }

        OccluderBvh bvh = OccluderBvh.Build(tris);
        Assert.True(bvh.Occluded(new Vec3(0, 0, -2), new Vec3(0, 0, 60)));
    }

    /// <summary>
    /// Regression: every triangle deep in a MULTI-LEVEL tree must occlude a short ray fired straight
    /// through its own centroid. The tree's internal nodes are appended pre-order, so the right child
    /// is not at <c>left+1</c> once a left subtree spans more than one node — a traversal that assumed
    /// so orphaned every deeper right subtree, so almost all triangles became invisible to shadow rays
    /// (measured on dmabrupt: 0.30% of triangles occluded their own centroid ray; a real level baked
    /// with zero shadow contribution). A scattered 6×6×6 grid forces a deep, balanced tree.
    /// </summary>
    [Fact]
    public void Deep_Tree_Every_Triangle_Occludes_Its_Own_Centroid_Ray()
    {
        var tris = new List<(Vec3, Vec3, Vec3)>();
        for (int gx = 0; gx < 6; gx++)
        {
            for (int gy = 0; gy < 6; gy++)
            {
                for (int gz = 0; gz < 6; gz++)
                {
                    // A small axis-aligned triangle in the z=gz*2 plane at a distinct grid cell.
                    float x = gx * 2, y = gy * 2, z = gz * 2;
                    tris.Add((new Vec3(x, y, z), new Vec3(x + 0.4f, y, z), new Vec3(x, y + 0.4f, z)));
                }
            }
        }

        OccluderBvh bvh = OccluderBvh.Build(tris);
        Assert.Equal(tris.Count, bvh.TriangleCount);

        int occluded = 0;
        foreach ((Vec3 a, Vec3 b, Vec3 c) in tris)
        {
            Vec3 centroid = a.Add(b).Add(c).Scale(1f / 3f); // inside the triangle
            // Short segment (length 0.2) straight through the centroid along the triangle's Z normal.
            if (bvh.Occluded(new Vec3(centroid.X, centroid.Y, centroid.Z + 0.1f),
                             new Vec3(centroid.X, centroid.Y, centroid.Z - 0.1f)))
            {
                occluded++;
            }
        }

        // Every triangle occludes a ray through its own centre — no orphaned right subtrees.
        Assert.Equal(tris.Count, occluded);
    }
}
