using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Vpp;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Strict regression guard for the V3M/V3C data-block unpacking bugs, covering
/// every LOD of every mesh (not just LOD0). Two historically distinct defects:
/// (1) batch arrays sized from the padded allocation fields (positions_size/12, …)
/// instead of the authoritative num_vertices / num_triangles, which drifted the
/// 0x10 inter-array alignment and collapsed meshes toward the origin; and (2) the
/// LOD1+ multi-batch morph-map (orig_map) quirk, where a mis-sized
/// per-batch morph map drifted the reader and corrupted the prop points / later
/// batches of higher LODs.
///
/// The decisive invariant that catches both is <b>exact data-block consumption</b>:
/// a correct unpack of a LOD lands precisely on the block end
/// (<see cref="V3dLod.DataBlockTrailingBytes"/> == 0) after every batch's arrays and
/// the prop points. Verified 0 across all 939 LODs of the 529 meshes in
/// meshes.vpp + tables.vpp + the Envirosuit_Guard.v3c character fixture, whose
/// morph-mapped 4-batch LOD1/LOD2 were the exact repro. Every batch's geometry must
/// also match its declared counts with in-range triangle indices.
/// </summary>
public sealed class MeshBatchIntegrityTests
{
    [Fact]
    public void Every_Mesh_In_MeshesVpp_Has_Consistent_Batch_Geometry()
    {
        string? path = TestPaths.RfVpp("meshes.vpp");
        if (path is null)
        {
            return; // real install unavailable — skip gracefully
        }

        using VppArchive vpp = VppArchive.Open(path);
        var meshEntries = vpp.Entries
            .Where(e => e.Name.EndsWith(".v3m", System.StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".v3c", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(meshEntries);

        var failures = new List<string>();
        int checkedMeshes = 0;
        foreach (VppEntry entry in meshEntries)
        {
            try
            {
                V3dFile file = V3dReader.Read(vpp.Read(entry));
                CheckMesh(entry.Name, file, failures);
                checkedMeshes++;
            }
            catch (V3dFormatException ex)
            {
                failures.Add($"{entry.Name}: parse threw {ex.Message}");
            }
        }

        Assert.True(checkedMeshes > 0);
        Assert.True(failures.Count == 0,
            $"{failures.Count} mesh batch(es) failed integrity across {checkedMeshes} meshes:\n" +
            string.Join('\n', failures.Take(40)));
    }

    [Fact]
    public void Envirosuit_Guard_V3c_Has_Consistent_Batch_Geometry()
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

        V3dFile file = V3dReader.Read(File.ReadAllBytes(path));

        // Guard against a vacuous pass: this fixture must actually contain the repro
        // shape — LODs beyond LOD0 that are multi-batch AND carry a morph map.
        var reproLods = file.Submeshes
            .SelectMany(sm => sm.Lods.Skip(1))
            .Count(l => l.Batches.Count > 1 && l.HasMorphMap);
        Assert.True(reproLods > 0,
            "fixture no longer exercises the multi-batch LOD1+ morph-map case");

        var failures = new List<string>();
        CheckMesh("Envirosuit_Guard.v3c", file, failures);
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    private static void CheckMesh(string name, V3dFile file, List<string> failures)
    {
        foreach (V3dSubmesh sm in file.Submeshes)
        {
            for (int li = 0; li < sm.Lods.Count; li++)
            {
                V3dLod lod = sm.Lods[li];

                // Exact data-block consumption: the strongest per-LOD invariant. A
                // non-zero residual means a batch array or the LOD1+ morph map drifted
                // the reader (the historical multi-batch-LOD1+ mis-parse). Asserted for
                // EVERY LOD, not just LOD0.
                if (lod.DataBlockTrailingBytes != 0)
                {
                    failures.Add(
                        $"{name}/{sm.Name}/LOD{li}: data block not consumed exactly " +
                        $"(trailing {lod.DataBlockTrailingBytes} bytes; batches={lod.Batches.Count}, " +
                        $"morphMap={lod.HasMorphMap})");
                }

                for (int bi = 0; bi < lod.Batches.Count; bi++)
                {
                    V3dBatch b = lod.Batches[bi];
                    string where = $"{name}/{sm.Name}/LOD{li}/batch{bi}";

                    // The vertex arrays share one allocated length (>= num_vertices);
                    // triangles must index within that allocation, or the mesh was
                    // mis-parsed / mis-aligned. This is exactly what GpuScene relies
                    // on when it uploads the full vertex buffer.
                    int vc = b.Positions.Length;
                    if (b.Normals.Length != vc)
                    {
                        failures.Add($"{where}: Normals.Length={b.Normals.Length} != Positions.Length={vc}");
                    }

                    if (b.TexCoords.Length < b.NumVertices)
                    {
                        failures.Add($"{where}: TexCoords.Length={b.TexCoords.Length} < num_vertices={b.NumVertices}");
                    }

                    if (b.Triangles.Length < b.NumTriangles)
                    {
                        failures.Add($"{where}: Triangles.Length={b.Triangles.Length} < num_triangles={b.NumTriangles}");
                    }

                    for (int t = 0; t < b.NumTriangles && t < b.Triangles.Length; t++)
                    {
                        V3dTriangle tri = b.Triangles[t];
                        if (tri.I0 >= vc || tri.I1 >= vc || tri.I2 >= vc)
                        {
                            failures.Add($"{where}: triangle {t} index ({tri.I0},{tri.I1},{tri.I2}) >= vertex count {vc}");
                            break;
                        }
                    }
                }
            }
        }
    }
}
