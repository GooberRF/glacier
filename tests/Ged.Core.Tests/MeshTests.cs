using System.IO;
using Ged.Core.IO.Mesh;
using Xunit;

namespace Ged.Core.Tests;

public class MeshTests
{
    public static IEnumerable<object[]> MeshFixtures()
    {
        yield return new object[] { Path.Combine("mesh", "LightOfficeCan01.v3m") };
        yield return new object[] { Path.Combine("mesh", "wallcomputer1.v3m") };
        yield return new object[] { Path.Combine("mesh", "Disk.v3m") };
    }

    [Theory]
    [MemberData(nameof(MeshFixtures))]
    public void Parses_V3m_Fixture_With_Sane_Geometry(string relPath)
    {
        string? path = TestPaths.FixtureFile(relPath.Split(Path.DirectorySeparatorChar));
        if (path is null)
        {
            return; // retail-derived mesh fixture not present
        }

        var file = V3dReader.Read(File.ReadAllBytes(path));

        Assert.Equal(V3dSignature.V3m, file.Signature);
        Assert.NotEmpty(file.Submeshes);
        AssertGeometrySane(file);
    }

    [Fact]
    public void Parses_Envirosuit_Guard_V3c()
    {
        if (TestPaths.Research is null)
        {
            return;
        }

        string path = Path.Combine(TestPaths.Research, "rf_decomp", "Envirosuit_Guard.v3c");
        if (!File.Exists(path))
        {
            return;
        }

        var file = V3dReader.Read(File.ReadAllBytes(path));

        Assert.Equal(V3dSignature.V3c, file.Signature);
        Assert.NotEmpty(file.Submeshes);
        // A character mesh should carry a skeleton and/or collision spheres.
        Assert.True(file.Bones.Count > 0 || file.ColSpheres.Count > 0);
        AssertGeometrySane(file);

        // Character LODs must expose bone links on at least one batch.
        bool anyBoneLinks = file.Submeshes
            .SelectMany(s => s.Lods)
            .SelectMany(l => l.Batches)
            .Any(b => b.BoneLinks.Length > 0);
        Assert.True(anyBoneLinks);
    }

    [Fact]
    public void Parses_Meshes_From_Real_Vpp()
    {
        string? path = TestPaths.RfVpp("meshes.vpp");
        if (path is null)
        {
            return;
        }

        using var vpp = Ged.Core.IO.Vpp.VppArchive.Open(path);
        int checked_ = 0;
        foreach (var entry in vpp.Entries.Where(e =>
                     e.Name.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase) ||
                     e.Name.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)).Take(5))
        {
            var file = V3dReader.Read(vpp.Read(entry));
            Assert.NotEmpty(file.Submeshes);
            AssertGeometrySane(file);
            checked_++;
        }

        Assert.True(checked_ > 0);
    }

    [Fact]
    public void V3m_Export_Reparse_RoundTrips_Structurally()
    {
        string? path = TestPaths.FixtureFile("mesh", "wallcomputer1.v3m");
        if (path is null)
        {
            return; // retail-derived mesh fixture not present
        }

        byte[] original = File.ReadAllBytes(path);
        var first = V3dReader.Read(original);

        byte[] written = V3dWriter.Write(first);
        var second = V3dReader.Read(written);

        AssertMeshEquivalent(first, second);
    }

    [Fact]
    public void V3c_Export_Reparse_RoundTrips_Structurally()
    {
        if (TestPaths.Research is null)
        {
            return;
        }

        string path = Path.Combine(TestPaths.Research, "rf_decomp", "Envirosuit_Guard.v3c");
        if (!File.Exists(path))
        {
            return;
        }

        var first = V3dReader.Read(File.ReadAllBytes(path));
        var second = V3dReader.Read(V3dWriter.Write(first));
        AssertMeshEquivalent(first, second);
    }

    [Fact]
    public void ResolveBatchTexture_Returns_A_Material_Name()
    {
        string? path = TestPaths.FixtureFile("mesh", "wallcomputer1.v3m");
        if (path is null)
        {
            return; // retail-derived mesh fixture not present
        }

        var file = V3dReader.Read(File.ReadAllBytes(path));
        V3dSubmesh sm = file.Submeshes[0];
        V3dLod lod = sm.Lods[0];
        Assert.NotEmpty(lod.Batches);
        string tex = sm.ResolveBatchTexture(lod, lod.Batches[0]);
        Assert.False(string.IsNullOrWhiteSpace(tex));
    }

    private static void AssertGeometrySane(V3dFile file)
    {
        foreach (V3dSubmesh sm in file.Submeshes)
        {
            Assert.InRange(sm.Lods.Count, 1, 3);
            Assert.Equal(sm.Lods.Count, sm.LodDistances.Count);

            foreach (V3dLod lod in sm.Lods)
            {
                Assert.NotEmpty(lod.Batches);
                foreach (V3dBatch b in lod.Batches)
                {
                    Assert.Equal(b.Positions.Length, b.Normals.Length);
                    Assert.True(b.TexCoords.Length >= b.NumVertices);
                    Assert.True(b.NumTriangles <= b.Triangles.Length);
                    for (int t = 0; t < b.NumTriangles; t++)
                    {
                        V3dTriangle tri = b.Triangles[t];
                        Assert.True(tri.I0 < b.Positions.Length, "index in range");
                        Assert.True(tri.I1 < b.Positions.Length, "index in range");
                        Assert.True(tri.I2 < b.Positions.Length, "index in range");
                    }
                }
            }
        }
    }

    private static void AssertMeshEquivalent(V3dFile a, V3dFile b)
    {
        Assert.Equal(a.Signature, b.Signature);
        Assert.Equal(a.Submeshes.Count, b.Submeshes.Count);
        Assert.Equal(a.ColSpheres.Count, b.ColSpheres.Count);
        Assert.Equal(a.Bones.Count, b.Bones.Count);

        for (int s = 0; s < a.Submeshes.Count; s++)
        {
            V3dSubmesh sa = a.Submeshes[s];
            V3dSubmesh sb = b.Submeshes[s];
            Assert.Equal(sa.Name, sb.Name);
            Assert.Equal(sa.Lods.Count, sb.Lods.Count);
            Assert.Equal(sa.Materials.Count, sb.Materials.Count);
            for (int m = 0; m < sa.Materials.Count; m++)
            {
                Assert.Equal(sa.Materials[m].DiffuseMapName, sb.Materials[m].DiffuseMapName);
            }

            for (int l = 0; l < sa.Lods.Count; l++)
            {
                V3dLod la = sa.Lods[l];
                V3dLod lb = sb.Lods[l];
                Assert.Equal(la.Flags, lb.Flags);
                Assert.Equal(la.Batches.Count, lb.Batches.Count);
                Assert.Equal(la.PropPoints.Count, lb.PropPoints.Count);
                Assert.Equal(la.Textures.Count, lb.Textures.Count);

                for (int i = 0; i < la.Batches.Count; i++)
                {
                    V3dBatch ba = la.Batches[i];
                    V3dBatch bb = lb.Batches[i];
                    Assert.Equal(ba.NumVertices, bb.NumVertices);
                    Assert.Equal(ba.NumTriangles, bb.NumTriangles);
                    Assert.Equal(ba.TextureIndex, bb.TextureIndex);
                    Assert.Equal(ba.Positions, bb.Positions);
                    Assert.Equal(ba.TexCoords, bb.TexCoords);
                    Assert.Equal(ba.Triangles, bb.Triangles);
                }
            }
        }
    }
}
