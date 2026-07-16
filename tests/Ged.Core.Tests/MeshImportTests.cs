using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for generic mesh import: the native OBJ parser (vertex/face/UV
/// counts on the resulting brush), the import → V3M → reparse round-trip
/// (counts + texture names), the axis-conversion transforms (a known asymmetric
/// fixture lands in the correct RF orientation), and the degenerate-face guard.
/// </summary>
public sealed class MeshImportTests
{
    private static string ObjFixture => TestPaths.Fixture("mesh", "pyramid.obj");

    // ─── OBJ parse → brush ───────────────────────────────────────────────────

    [Fact]
    public void Obj_Imports_To_Brush_With_Expected_Counts()
    {
        ImportedModel model = MeshImporter.Load(ObjFixture);

        // One material group ("stone" -> rck_default01.tga), 5 unique positions, 6 triangles.
        ImportedGroup group = Assert.Single(model.Groups);
        Assert.Equal("rck_default01.tga", group.Texture);
        Assert.Equal(5, group.Positions.Count);
        Assert.Equal(6, group.Indices.Count / 3);
        Assert.True(group.HasTexCoords);

        var options = new MeshImportOptions { Axis = MeshAxisConversion.RfNative, Target = MeshImportTarget.Brushes };
        MeshImportPipeline.ApplyTransform(model, options);
        var brushes = MeshImportPipeline.ToBrushes(model, options).ToList();

        Brush brush = Assert.Single(brushes);
        Assert.Equal(5, brush.Geometry.Vertices.Count);   // 5 pyramid corners
        Assert.Equal(6, brush.Geometry.Faces.Count);      // 6 triangles
        Assert.All(brush.Geometry.Faces, f => Assert.Equal(3, f.Vertices.Count));
        // UVs preserved per corner (the base corners carry the fixture's vt values).
        Assert.Contains(brush.Geometry.Faces.SelectMany(f => f.Vertices),
            v => v.TextureCoords.U != 0f || v.TextureCoords.V != 0f);
        Assert.Single(brush.Geometry.Textures);
    }

    [Fact]
    public void Untextured_Import_Group_Gets_The_Stock_Default_Not_A_Missing_Texture()
    {
        // Item 3 (b): the mesh-import→brushes path bypasses BrushFactory, so it must apply
        // orientation defaults itself — an untextured group used to reference a nonexistent
        // "default.tga" and render untextured.
        var model = new ImportedModel();
        var group = new ImportedGroup { Name = "nomat" }; // Texture left empty
        group.Positions.AddRange(new[]
        {
            new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 0, 2), new Vec3(0, 0, 2),
        });
        group.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        model.Groups.Add(group);

        var options = new MeshImportOptions { Axis = MeshAxisConversion.RfNative, Target = MeshImportTarget.Brushes };
        MeshImportPipeline.ApplyTransform(model, options);
        Brush brush = Assert.Single(MeshImportPipeline.ToBrushes(model, options));

        Assert.NotEmpty(brush.Geometry.Faces);
        Assert.All(brush.Geometry.Faces, f =>
            Assert.Equal(BrushCreateParams.StockWallTexture, brush.Geometry.Textures[f.Texture]));
        Assert.DoesNotContain("default.tga", brush.Geometry.Textures);
        Assert.DoesNotContain(brush.Geometry.Textures, string.IsNullOrEmpty);
    }

    // ─── Import → V3M → reparse round-trip ───────────────────────────────────

    [Fact]
    public void Obj_To_V3m_RoundTrips_Counts_And_Texture()
    {
        ImportedModel model = MeshImporter.Load(ObjFixture);
        var options = new MeshImportOptions { Target = MeshImportTarget.MeshObject };
        MeshImportPipeline.ApplyTransform(model, options);

        V3dFile mesh = MeshImportPipeline.ToV3dFile(model, "pyramid.v3m");
        byte[] bytes = V3dWriter.Write(mesh);
        V3dFile reparsed = V3dReader.Read(bytes);

        V3dSubmesh sm = Assert.Single(reparsed.Submeshes);
        V3dLod lod = Assert.Single(sm.Lods);
        V3dBatch batch = Assert.Single(lod.Batches);

        Assert.Equal(5, batch.NumVertices);
        Assert.Equal(6, batch.NumTriangles);
        Assert.Equal("rck_default01.tga", sm.ResolveBatchTexture(lod, batch));
        // Per-triangle planes were written (flag 0x20) and survive the round-trip.
        Assert.True(lod.HasTrianglePlanes);
        Assert.Equal(6, batch.Planes.Length);
    }

    // ─── Axis conversion ─────────────────────────────────────────────────────

    [Fact]
    public void AxisConversion_Maps_Known_Point_Into_Rf_Space()
    {
        var p = new Vec3(1f, 2f, 3f);

        // RF native: unchanged.
        Assert.Equal(p, MeshAxis.Convert(p, MeshAxisConversion.RfNative));

        // glTF (+Y up, -Z forward) -> RF (+Z forward): negate Z, and winding flips.
        Assert.Equal(new Vec3(1f, 2f, -3f), MeshAxis.Convert(p, MeshAxisConversion.GltfYUp));
        Assert.True(MeshAxis.FlipsWinding(MeshAxisConversion.GltfYUp));

        // Z-up -> RF Y-up: swap Y and Z, and winding flips.
        Assert.Equal(new Vec3(1f, 3f, 2f), MeshAxis.Convert(p, MeshAxisConversion.ZUp));
        Assert.True(MeshAxis.FlipsWinding(MeshAxisConversion.ZUp));

        Assert.False(MeshAxis.FlipsWinding(MeshAxisConversion.RfNative));
        Assert.Equal(MeshAxisConversion.GltfYUp, MeshAxis.DefaultFor(ImportedFormat.Gltf));
    }

    [Fact]
    public void AxisConversion_Applied_To_Fixture_Reorients_Vertices()
    {
        // The pyramid apex is at (0,2,0); a Z-up import must swap it to (0,0,2), and
        // a base corner (1,0,1) must become (1,1,0) — asymmetric, so the swap shows.
        ImportedModel model = MeshImporter.Load(ObjFixture);
        var options = new MeshImportOptions { Axis = MeshAxisConversion.ZUp, Scale = 2f };
        MeshImportPipeline.ApplyTransform(model, options);

        var positions = model.Groups[0].Positions;
        Assert.Contains(positions, v => Near(v, new Vec3(0f, 0f, 4f)));  // apex (0,2,0)*2=(0,4,0) -> swap Y,Z
        Assert.Contains(positions, v => Near(v, new Vec3(2f, -2f, 0f))); // corner (1,0,-1)*2=(2,0,-2) -> swap Y,Z
    }

    // ─── Assimp native path ──────────────────────────────────────────────────

    [Fact]
    public void Assimp_Native_Path_Imports_When_Available()
    {
        // Exercises the real native Assimp binding (Assimp reads OBJ too). Skips
        // gracefully if the native library cannot be loaded in this host.
        if (!AssimpImporter.IsAvailable)
        {
            return;
        }

        ImportedModel model = AssimpImporter.Import(ObjFixture);
        Assert.NotEmpty(model.Groups);
        Assert.Equal(5, model.TotalVertices);   // Assimp joins identical vertices -> 5 pyramid corners
        Assert.Equal(6, model.TotalTriangles);
    }

    // ─── Degenerate rejection ────────────────────────────────────────────────

    [Fact]
    public void Degenerate_Triangles_Are_Rejected()
    {
        // A quad with a duplicated corner collapses one triangle to a line.
        const string obj = "v 0 0 0\nv 1 0 0\nv 1 0 0\nv 0 1 0\nusemtl m\nf 1 2 3\nf 1 3 4\n";
        ImportedModel model = ObjImporter.Import(obj);
        var options = new MeshImportOptions();
        MeshImportPipeline.ApplyTransform(model, options);
        var brushes = MeshImportPipeline.ToBrushes(model, options).ToList();

        Brush brush = Assert.Single(brushes);
        // The (1,2,3) triangle is degenerate (v2==v3 after weld) and dropped; the (1,3,4) survives.
        Assert.Single(brush.Geometry.Faces);
    }

    private static bool Near(Vec3 a, Vec3 b) =>
        System.MathF.Abs(a.X - b.X) < 1e-4f && System.MathF.Abs(a.Y - b.Y) < 1e-4f && System.MathF.Abs(a.Z - b.Z) < 1e-4f;
}
