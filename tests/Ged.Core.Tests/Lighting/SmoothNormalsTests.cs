using System;
using System.Collections.Generic;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Lighting;

/// <summary>
/// Item 7: RED-parity face smoothing, matching the baker's vertex-normal rule
/// decompiled from RED.exe FUN_004aded0: a vertex normal is the UNWEIGHTED mean of
/// the face's own plane normal plus every vertex-sharing smooth face's plane normal
/// with <c>dot(currentFace.N, otherFace.N) &gt; 0</c> (hemisphere cutoff — a
/// perpendicular floor never bends a wall's normals), normalized. The baker then
/// interpolates these per texel and lights smooth surfaces with raw N·L.
/// </summary>
public sealed class SmoothNormalsTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    private static CsgFace Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 normal, uint groups)
    {
        var f = new CsgFace
        {
            Plane = new CsgPlane(normal, -normal.Dot(a)),
            Texture = "rock",
            SmoothingGroups = groups,
        };
        f.Vertices.Add(new CsgVertex(a, default));
        f.Vertices.Add(new CsgVertex(b, default));
        f.Vertices.Add(new CsgVertex(c, default));
        f.Vertices.Add(new CsgVertex(d, default));
        return f;
    }

    [Fact]
    public void Vertex_Normals_Are_The_Unweighted_Mean_Of_Adjacent_Smooth_Faces()
    {
        // A floor (+Y) meeting a 45° slope along the shared edge x=0: dot = 0.707 > 0,
        // so the shared vertices average the two plane normals.
        Vec3 slopeN = V(1, 1, 0).Normalized();
        CsgFace floor = Quad(V(0, 0, 0), V(0, 0, 4), V(-4, 0, 4), V(-4, 0, 0), V(0, 1, 0), groups: 1);
        CsgFace slope = Quad(V(0, 0, 0), V(0, -2.83f, 2.83f), V(0, -2.83f, 5.83f), V(0, 0, 4), slopeN, groups: 1);

        Dictionary<CsgFaceKey, SmoothFace> map = SmoothNormals.Build(new[] { floor, slope });
        SmoothFace sf = map[new CsgFaceKey(floor)];

        Vec3 expected = V(0, 1, 0).Add(slopeN).Scale(0.5f).Normalized();
        int shared = Array.FindIndex(sf.Positions, p => p.Sub(V(0, 0, 0)).Length() < 1e-4f);
        Assert.True(shared >= 0);
        Assert.True(sf.Normals[shared].Sub(expected).Length() < 1e-3f,
            $"expected ~{expected}, got {sf.Normals[shared]}");

        // A vertex not on the shared edge keeps the flat floor normal.
        int solo = Array.FindIndex(sf.Positions, p => p.Sub(V(-4, 0, 0)).Length() < 1e-4f);
        Assert.True(solo >= 0);
        Assert.True(sf.Normals[solo].Sub(V(0, 1, 0)).Length() < 1e-3f);
    }

    [Fact]
    public void Perpendicular_Faces_Never_Smooth_Together()
    {
        // RED's hemisphere cutoff: only adjacent faces with dot(N, otherN) > 0 count.
        // A wall meeting the floor at exactly 90° (dot = 0) is excluded — the wall
        // base keeps its flat normal instead of tilting toward the floor lights.
        CsgFace floor = Quad(V(0, 0, 0), V(0, 0, 4), V(-4, 0, 4), V(-4, 0, 0), V(0, 1, 0), groups: 1);
        CsgFace wall = Quad(V(0, 0, 0), V(0, -4, 0), V(0, -4, 4), V(0, 0, 4), V(1, 0, 0), groups: 1);

        Dictionary<CsgFaceKey, SmoothFace> map = SmoothNormals.Build(new[] { floor, wall });

        foreach (Vec3 n in map[new CsgFaceKey(floor)].Normals)
        {
            Assert.True(n.Sub(V(0, 1, 0)).Length() < 1e-3f, $"floor normal bent to {n}");
        }

        foreach (Vec3 n in map[new CsgFaceKey(wall)].Normals)
        {
            Assert.True(n.Sub(V(1, 0, 0)).Length() < 1e-3f, $"wall normal bent to {n}");
        }
    }

    [Fact]
    public void Different_Nonzero_Groups_Still_Smooth_Together()
    {
        // RED's adjacency filter is "the other face HAS smoothing data" (nonzero),
        // not a mask overlap — verified in the decompiled vertex-normal builder.
        Vec3 slopeN = V(1, 1, 0).Normalized();
        CsgFace floor = Quad(V(0, 0, 0), V(0, 0, 4), V(-4, 0, 4), V(-4, 0, 0), V(0, 1, 0), groups: 1);
        CsgFace slope = Quad(V(0, 0, 0), V(0, -2.83f, 2.83f), V(0, -2.83f, 5.83f), V(0, 0, 4), slopeN, groups: 2);

        Dictionary<CsgFaceKey, SmoothFace> map = SmoothNormals.Build(new[] { floor, slope });
        SmoothFace sf = map[new CsgFaceKey(floor)];

        int shared = Array.FindIndex(sf.Positions, p => p.Sub(V(0, 0, 0)).Length() < 1e-4f);
        Assert.True(shared >= 0);
        Assert.True(sf.Normals[shared].Sub(V(0, 1, 0)).Length() > 1e-3f,
            "faces in different (nonzero) groups must still smooth");
    }

    [Fact]
    public void Groupless_Faces_Contribute_Nothing_And_Get_No_Entry()
    {
        Vec3 slopeN = V(1, 1, 0).Normalized();
        CsgFace floor = Quad(V(0, 0, 0), V(0, 0, 4), V(-4, 0, 4), V(-4, 0, 0), V(0, 1, 0), groups: 1);
        CsgFace slope = Quad(V(0, 0, 0), V(0, -2.83f, 2.83f), V(0, -2.83f, 5.83f), V(0, 0, 4), slopeN, groups: 0);

        Dictionary<CsgFaceKey, SmoothFace> map = SmoothNormals.Build(new[] { floor, slope });

        Assert.False(map.ContainsKey(new CsgFaceKey(slope))); // no smoothing data → not smoothed
        foreach (Vec3 n in map[new CsgFaceKey(floor)].Normals)
        {
            Assert.True(n.Sub(V(0, 1, 0)).Length() < 1e-3f, "a groupless neighbour must not contribute");
        }
    }

    [Fact]
    public void Coplanar_Neighbours_Weight_The_Mean_By_Multiplicity()
    {
        // Two coplanar floor quads + one slope at a shared corner: the mean is
        // (2·floorN + slopeN)/3 — RED's per-face averaging counts each adjacent face.
        Vec3 slopeN = V(1, 1, 0).Normalized();
        CsgFace floorA = Quad(V(0, 0, 0), V(0, 0, 4), V(-4, 0, 4), V(-4, 0, 0), V(0, 1, 0), groups: 1);
        CsgFace floorB = Quad(V(0, 0, 0), V(-4, 0, 0), V(-4, 0, -4), V(0, 0, -4), V(0, 1, 0), groups: 1);
        CsgFace slope = Quad(V(0, 0, 0), V(0, -2.83f, 2.83f), V(0, -2.83f, 5.83f), V(0, 0, 4), slopeN, groups: 1);

        Dictionary<CsgFaceKey, SmoothFace> map = SmoothNormals.Build(new[] { floorA, floorB, slope });
        SmoothFace sf = map[new CsgFaceKey(floorA)];

        int shared = Array.FindIndex(sf.Positions, p => p.Sub(V(0, 0, 0)).Length() < 1e-4f);
        Assert.True(shared >= 0);
        Vec3 expected = V(0, 1, 0).Scale(2f).Add(slopeN).Scale(1f / 3f).Normalized();
        Assert.True(sf.Normals[shared].Sub(expected).Length() < 1e-3f,
            $"expected ~{expected}, got {sf.Normals[shared]}");
    }

    // ---- Mesh Smooth op consistency with the baker ------------------------------

    [Fact]
    public void MeshSmooth_Puts_Groupless_Faces_Into_Group_1_So_The_Baker_Smooths_Them()
    {
        Geometry g = QuadGeometry(smoothingGroups: 0);
        OpResult r = FaceOps.MeshSmooth(g, new[] { 0 });

        Assert.True(r);
        Assert.True(g.Faces.Count > 1, "the quad should subdivide");
        Assert.All(g.Faces, f => Assert.Equal(1u, f.SmoothingGroups));
    }

    [Fact]
    public void MeshSmooth_Preserves_An_Existing_Smoothing_Group()
    {
        Geometry g = QuadGeometry(smoothingGroups: 0x8);
        OpResult r = FaceOps.MeshSmooth(g, new[] { 0 });

        Assert.True(r);
        Assert.All(g.Faces, f => Assert.Equal(0x8u, f.SmoothingGroups));
    }

    private static Geometry QuadGeometry(uint smoothingGroups)
    {
        var g = new Geometry();
        g.Textures.Add("rock");
        g.Vertices.Add(V(0, 0, 0));
        g.Vertices.Add(V(4, 0, 0));
        g.Vertices.Add(V(4, 0, 4));
        g.Vertices.Add(V(0, 0, 4));
        var f = new Face { Texture = 0, SurfaceIndex = -1, RoomIndex = -1, SmoothingGroups = smoothingGroups };
        for (int i = 0; i < 4; i++)
        {
            f.Vertices.Add(new FaceVertex { Index = i });
        }

        g.Faces.Add(f);
        Ged.Core.Editing.GeometryUtil.RecomputeAllPlanes(g);
        return g;
    }
}
