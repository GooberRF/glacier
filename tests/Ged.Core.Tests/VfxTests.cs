using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Mesh.Vfx;
using Ged.Core.IO.Vpp;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests;

/// <summary>
/// VFX (VSFX) effect-file reader coverage. The corpus sweep enumerates every .vfx in
/// the mounted RF VPPs and asserts a full, byte-exact parse (0 trailing bytes on every
/// section) plus the header allocation-count invariants; targeted tests pin the parsed
/// structure of well-known effects and the V3D adapter's rendered geometry. Skips
/// gracefully when no real RF install is available (like the other VFS-backed tests).
///
/// GED never writes .vfx, so there is no byte round-trip gate for this format — parse
/// and adapt only (documented in docs/internal/FEATURES.md).
/// </summary>
public sealed class VfxTests
{
    private readonly ITestOutputHelper _out;

    public VfxTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Every_Vfx_In_The_Install_Parses_Exactly_With_Invariants()
    {
        if (TestPaths.RfInstall is null)
        {
            return; // real install unavailable — skip gracefully
        }

        var failures = new List<string>();
        var versions = new SortedSet<int>();
        int parsed = 0;

        foreach (string vpp in Directory.EnumerateFiles(TestPaths.RfInstall, "*.vpp"))
        {
            using VppArchive archive = VppArchive.Open(vpp);
            foreach (VppEntry entry in archive.Entries.Where(e => e.Name.EndsWith(".vfx", System.StringComparison.OrdinalIgnoreCase)))
            {
                VfxFile file;
                try
                {
                    file = VfxReader.Read(archive.Read(entry));
                }
                catch (System.Exception ex)
                {
                    failures.Add($"{entry.Name}: threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                parsed++;
                versions.Add(file.Version);
                CheckInvariants(entry.Name, file, failures);
            }
        }

        _out.WriteLine($"Parsed {parsed} .vfx file(s); versions: {string.Join(", ", versions.Select(v => "0x" + v.ToString("X")))}");
        Assert.True(parsed > 0, "no .vfx files found in the install VPPs");
        Assert.True(failures.Count == 0,
            $"{failures.Count} VFX invariant failure(s):\n{string.Join('\n', failures.Take(40))}");
    }

    private static void CheckInvariants(string name, VfxFile f, List<string> failures)
    {
        // Every fully-parsed section body must be consumed exactly.
        for (int i = 0; i < f.SectionTrailingBytes.Count; i++)
        {
            if (f.SectionTrailingBytes[i] != 0)
            {
                failures.Add($"{name}: section {i} left {f.SectionTrailingBytes[i]} trailing bytes");
            }
        }

        // Header allocation counts vs the sections actually present.
        // "meshes" allocation covers mesh + chain sections (they share the array in the engine).
        if (f.Meshes.Count + f.ChainCount != f.HdrNumMeshes)
        {
            failures.Add($"{name}: mesh+chain count {f.Meshes.Count + f.ChainCount} != header {f.HdrNumMeshes}");
        }

        Compare(name, "lights", f.Lights.Count, f.HdrNumLights, failures);
        Compare(name, "dummies", f.Dummies.Count, f.HdrNumDummies, failures);
        Compare(name, "particle systems", f.ParticleSystems.Count, f.HdrNumParticleSystems, failures);
        Compare(name, "spacewarps", f.Spacewarps.Count, f.HdrNumSpacewarps, failures);
        Compare(name, "cameras", f.Cameras.Count, f.HdrNumCameras, failures);

        if (f.Version >= 0x40000)
        {
            Compare(name, "materials", f.Materials.Count, f.HdrNumMaterials, failures);
        }

        int faceSum = f.Meshes.Sum(m => m.Faces.Count);
        if (faceSum != f.HdrNumFaces)
        {
            failures.Add($"{name}: face sum {faceSum} != header num_faces {f.HdrNumFaces}");
        }
    }

    private static void Compare(string name, string what, int actual, int header, List<string> failures)
    {
        if (actual != header)
        {
            failures.Add($"{name}: {what} count {actual} != header {header}");
        }
    }

    [Fact]
    public void Grabber_ThrusterFx_Parses_As_A_Keyframed_Fullbright_Effect()
    {
        VfxFile? f = LoadFromInstall("grabber_thrusterfx.vfx");
        if (f is null)
        {
            return;
        }

        Assert.Equal(0x3000F, f.Version);
        Assert.Equal(4, f.Meshes.Count);
        Assert.Empty(f.Materials); // legacy: materials embedded in the mesh

        VfxMesh m0 = f.Meshes[0];
        Assert.True(m0.IsKeyframed);
        Assert.True(m0.Flags.Fullbright);
        Assert.NotNull(m0.RestFrame);
        Assert.Equal(m0.NumVertices, m0.RestFrame!.Positions.Length);
        Assert.NotEmpty(m0.EmbeddedMaterials);
        Assert.NotNull(m0.Keyframes);

        // The adapter produces renderable geometry.
        V3dFile v3d = VfxToV3d.Convert(f);
        Assert.Equal(4, v3d.Submeshes.Count);
        Assert.All(v3d.Submeshes, sm => Assert.NotEmpty(sm.Lods[0].Batches));
    }

    [Fact]
    public void Jeep_Vfx_Is_A_Textured_Multi_Material_Mesh()
    {
        VfxFile? f = LoadFromInstall("jeep.vfx");
        if (f is null)
        {
            return;
        }

        Assert.Equal(0x40006, f.Version);
        Assert.Single(f.Meshes);
        Assert.Equal(3, f.Materials.Count);

        // Jeep's cockpit uses a mix: one additive pane (JeepCockpit1B), the rest opaque.
        Assert.Contains(f.Materials, mat => mat.Additive);
        Assert.Contains(f.Materials, mat => !mat.Additive);
        Assert.All(f.Materials, mat => Assert.False(mat.DiffuseTextureName.Length == 0));

        V3dFile v3d = VfxToV3d.Convert(f);
        V3dSubmesh sm = Assert.Single(v3d.Submeshes);
        Assert.NotEmpty(sm.Lods[0].Batches);
        Assert.All(sm.Lods[0].Batches, b => Assert.NotEmpty(b.Positions));
        // RED marks the whole mesh additive when any face material is additive (per-mesh
        // flag derivation), so every batch shares that blend. self_illum is 0 -> lit.
        Assert.All(sm.Lods[0].Batches, b => Assert.Equal(V3dBatchBlend.Additive, b.Blend));
        Assert.All(sm.Lods[0].Batches, b => Assert.False(b.Unlit));
        Assert.Contains(sm.Materials, mm => mm.DiffuseMapName.EndsWith(".tga", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WaterSplash_Retains_Particle_Systems_Alongside_Meshes()
    {
        VfxFile? f = LoadFromInstall("water_splash_huge.vfx");
        if (f is null)
        {
            return;
        }

        Assert.Equal(0x30012, f.Version);
        Assert.Equal(4, f.Meshes.Count);
        Assert.Equal(4, f.ParticleSystems.Count); // retained (not simulated), counts hold
        Assert.All(f.SectionTrailingBytes, t => Assert.Equal(0, t));
    }

    [Fact]
    public void MeshLoader_Dispatches_Vfx_To_The_V3d_Shape()
    {
        string? path = FindEntry("grabber_thrusterfx.vfx");
        if (path is null)
        {
            return;
        }

        byte[] bytes = ReadEntry("grabber_thrusterfx.vfx")!;
        Assert.True(VfxReader.IsVfx(bytes));

        V3dFile v3d = MeshLoader.Read(bytes);
        Assert.NotEmpty(v3d.Submeshes);

        // Textures flow to the dependency scanner via the same MeshLoader seam.
        IReadOnlyList<string> textures = MeshLoader.ReferencedTextures(bytes);
        Assert.NotEmpty(textures);
    }

    // ---- helpers ----

    private static VfxFile? LoadFromInstall(string name)
    {
        byte[]? bytes = ReadEntry(name);
        return bytes is null ? null : VfxReader.Read(bytes);
    }

    private static byte[]? ReadEntry(string name)
    {
        if (TestPaths.RfInstall is null)
        {
            return null;
        }

        foreach (string vpp in Directory.EnumerateFiles(TestPaths.RfInstall, "*.vpp"))
        {
            using VppArchive archive = VppArchive.Open(vpp);
            if (archive.Find(name) is { } entry)
            {
                return archive.Read(entry);
            }
        }

        return null;
    }

    private static string? FindEntry(string name) => ReadEntry(name) is null ? null : name;
}
