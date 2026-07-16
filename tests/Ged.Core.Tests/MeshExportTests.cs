using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ged.Core.Editing;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Mesh.Export;
using Ged.Core.IO.Mesh.Import;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for level/selection export: brush "To Mesh" (→ V3M reparse, counts +
/// texture names, reset-origin), glTF (structurally valid + re-imports), OBJ
/// (re-parses to the same counts), and VRML (text sanity).
/// </summary>
public sealed class MeshExportTests : IDisposable
{
    private readonly string _temp;

    public MeshExportTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    // ─── Brush → V3M "To Mesh" ───────────────────────────────────────────────

    [Fact]
    public void ToMesh_RoundTrips_Counts_And_Texture_Names()
    {
        var brushes = TwoTexturedBoxes();
        V3dFile mesh = BrushMeshExport.ToV3d("selection.v3m", brushes, resetOrigin: false, out Vec3 origin);
        Assert.Equal(Vec3.Zero, origin);

        byte[] bytes = V3dWriter.Write(mesh);
        V3dFile reparsed = V3dReader.Read(bytes);

        V3dSubmesh sm = Assert.Single(reparsed.Submeshes);
        V3dLod lod = Assert.Single(sm.Lods);
        // One batch per texture (2), 12 triangles each (a box = 6 quads = 12 tris).
        Assert.Equal(2, lod.Batches.Count);
        Assert.Equal(24, lod.Batches.Sum(b => b.NumTriangles));

        var texNames = lod.Batches.Select(b => sm.ResolveBatchTexture(lod, b)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("wall1.tga", texNames);
        Assert.Contains("floor1.tga", texNames);
    }

    [Fact]
    public void ToMesh_ResetOrigin_Recentres_And_Returns_Offset()
    {
        // A box centred at (10, 4, -6): reset-origin should return that centre and
        // recentre the geometry so its bounds straddle the origin.
        Brush box = BoxBrush("wall1.tga", new Vec3(10f, 4f, -6f));
        V3dFile mesh = BrushMeshExport.ToV3d("m.v3m", new[] { box }, resetOrigin: true, out Vec3 origin);

        Assert.True(MathF.Abs(origin.X - 10f) < 0.01f);
        Assert.True(MathF.Abs(origin.Y - 4f) < 0.01f);
        Assert.True(MathF.Abs(origin.Z - (-6f)) < 0.01f);

        Aabb bb = mesh.Submeshes[0].BoundingBox;
        Assert.True(MathF.Abs(bb.P1.X + bb.P2.X) < 0.01f); // symmetric about 0 in X
    }

    // ─── glTF ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gltf_Export_Is_Structurally_Valid_And_Reimports()
    {
        var brushes = TwoTexturedBoxes();
        ImportedModel model = GeometryExtract.FromBrushes(brushes);
        GltfOutput gltf = GltfExporter.Export(model, "level.bin");

        // JSON parses and its accessor/bufferView/buffer references are internally consistent.
        using JsonDocument doc = JsonDocument.Parse(gltf.Json);
        JsonElement root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        int bufferLen = root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32();
        Assert.Equal(gltf.Bin.Length, bufferLen);
        JsonElement views = root.GetProperty("bufferViews");
        foreach (JsonElement v in views.EnumerateArray())
        {
            int off = v.GetProperty("byteOffset").GetInt32();
            int len = v.GetProperty("byteLength").GetInt32();
            Assert.True(off + len <= gltf.Bin.Length);
        }

        Assert.Equal(2, root.GetProperty("materials").GetArrayLength()); // one per texture

        // Write both files and re-import through the pipeline (Assimp) to prove it parses.
        string gltfPath = Path.Combine(_temp, "level.gltf");
        File.WriteAllText(gltfPath, gltf.Json);
        File.WriteAllBytes(Path.Combine(_temp, "level.bin"), gltf.Bin);

        if (AssimpImporter.IsAvailable)
        {
            ImportedModel back = MeshImporter.Load(gltfPath);
            Assert.NotEmpty(back.Groups);
            Assert.Equal(24, back.TotalTriangles); // 2 boxes * 12 tris survive the round-trip
        }
    }

    // ─── OBJ ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Obj_Export_Reparses_To_Same_Counts()
    {
        var brushes = TwoTexturedBoxes();
        ImportedModel model = GeometryExtract.FromBrushes(brushes);
        ObjOutput obj = ObjExporter.Export(model, "level.mtl");

        Assert.Contains("mtllib level.mtl", obj.Obj);
        Assert.Contains("map_Kd wall1.tga", obj.Mtl);

        // Re-parse via the native OBJ importer: 24 triangles across the material groups.
        ImportedModel back = ObjImporter.Import(obj.Obj, obj.Mtl);
        Assert.Equal(24, back.TotalTriangles);
        Assert.Equal(2, back.Groups.Count);
    }

    // ─── VRML ────────────────────────────────────────────────────────────────

    [Fact]
    public void Vrml_Export_Is_Well_Formed()
    {
        var brushes = TwoTexturedBoxes();
        ImportedModel model = GeometryExtract.FromBrushes(brushes);
        string wrl = VrmlExporter.Export(model);

        Assert.StartsWith("#VRML V2.0 utf8", wrl);
        Assert.Contains("coordIndex", wrl);
        Assert.Contains("ImageTexture { url \"wall1.tga\" }", wrl);
        // One Shape / IndexedFaceSet per texture group (2).
        Assert.Equal(2, Occurrences(wrl, "Shape {"));
        Assert.Equal(2, Occurrences(wrl, "IndexedFaceSet {"));
        // Braces balance.
        Assert.Equal(wrl.Count(c => c == '{'), wrl.Count(c => c == '}'));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<Brush> TwoTexturedBoxes() => new[]
    {
        BoxBrush("wall1.tga", new Vec3(0f, 0f, 0f)),
        BoxBrush("floor1.tga", new Vec3(5f, 0f, 0f)),
    };

    private static Brush BoxBrush(string texture, Vec3 position)
    {
        Geometry g = BrushFactory.Box(2f, 2f, 2f, 0, 0, 0, texture);
        return new Brush { Uid = 1, Position = position, Rotation = Mat3.Identity, Geometry = g };
    }

    private static int Occurrences(string s, string sub)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += sub.Length;
        }

        return count;
    }
}
