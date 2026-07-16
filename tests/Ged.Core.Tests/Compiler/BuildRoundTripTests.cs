using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// End-to-end build integration: assemble a from-scratch level (two rooms joined
/// by a portal, a pillar, and a liquid room), compile + apply it to a document,
/// save to bytes, reload, and verify the compiled static geometry survives the
/// round trip. Also emits a ready-to-test artifact under tests/artifacts.
/// </summary>
public sealed class BuildRoundTripTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Build_Apply_Save_Reload_Preserves_Compiled_Geometry()
    {
        RflFile rfl = ScratchLevel(out _);

        CompiledLevel built = GeometryBuildService.BuildAndApply(rfl);
        Assert.True(built.Report.Rooms >= 2);
        Assert.True(built.Report.Faces > 10);

        byte[] bytes = rfl.Save(updateTimestamp: false);
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        Geometry g = reloaded.Sections
            .Select(s => s.Content).OfType<GeometrySection>().First().Geometry;
        LightmapsSection lm = reloaded.Sections
            .Select(s => s.Content).OfType<LightmapsSection>().First();

        Assert.True(g.Rooms.Count >= 2);
        Assert.True(g.Faces.Count > 10);
        Assert.True(g.Surfaces.Count > 0);
        Assert.True(lm.Lightmaps.Count > 0);

        // Every face references a valid room and (if bound) a valid surface.
        foreach (Face f in g.Faces)
        {
            Assert.InRange(f.RoomIndex, 0, g.Rooms.Count - 1);
            if ((f.SurfaceIndex & 0xFFFF) != 0xFFFF)
            {
                Assert.InRange(f.SurfaceIndex, 0, g.Surfaces.Count - 1);
            }
        }
    }

    [Fact]
    public void Emits_Testable_Artifact()
    {
        if (TestPaths.RepoRoot is null)
        {
            return;
        }

        // Prefer a real corpus level as the scaffold (guaranteed RF-loadable):
        // recompile a small stock level's geometry with GED's compiler so the
        // artifact is a real, playable level whose geometry came from this compiler.
        string outDir = Path.Combine(TestPaths.RepoRoot, "tests", "artifacts");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "ged_testlevel.rfl");

        RflFile artifact;
        if (Corpus.Available && File.Exists(Path.Combine(Corpus.Directory!, "dm04.rfl")))
        {
            artifact = RflFile.Load(Path.Combine(Corpus.Directory!, "dm04.rfl"));
        }
        else
        {
            artifact = ScratchLevel(out _);
        }

        // Texture-derived face flags (invisible / alpha / holes) need the game's
        // textures; use the local RF install when available so the artifact carries
        // the same flag bits RED would produce. Bake real lighting so the emitted
        // level loads in-game with GED-baked lightmaps.
        var options = new CompileOptions { BakeLighting = true };
        Ged.Core.Assets.AssetVfs? vfs = null;
        if (TestPaths.RfInstall is { } install)
        {
            vfs = Ged.Core.Assets.GameMount.Mount(install);
            var traits = new Ged.Core.Assets.TextureTraitsCache(vfs);
            options.TextureTraits = traits.Get;
        }

        CompiledLevel built;
        try
        {
            built = GeometryBuildService.BuildAndApply(artifact, options);
        }
        finally
        {
            vfs?.Dispose();
        }

        File.WriteAllBytes(outPath, artifact.Save(updateTimestamp: true));

        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 1000);
        Assert.True(built.Report.Rooms > 0 && built.Report.Faces > 0);
    }

    /// <summary>Two rooms + portal + pillar + liquid, as an in-memory level.</summary>
    private static RflFile ScratchLevel(out CompiledLevel _)
    {
        var brushes = new List<Brush>
        {
            AirBox(1, V(-6, 0, 0), 12, 8, 12),          // room A
            AirBox(2, V(6, 0, 0), 12, 6, 6),            // room B (smaller -> doorway rim)
            PortalSlab(3, V(0, 0, 0), 0.4f, 6, 6),      // portal in the doorway
            SolidBox(4, V(-8, 0, 0), 2, 8, 2),          // pillar in room A
        };

        var effects = new List<RoomEffect>
        {
            new()
            {
                EffectType = RoomEffectsSection.EffectLiquidRoom,
                LiquidProperties = new RoomEffectLiquidProperties
                {
                    Depth = 3f,
                    LiquidType = 1,
                    SurfaceTexture = "water.tga",
                    Waveform = 2,
                    Visibility = 8f,
                },
                Header = new ObjectHeader { Uid = 9001, Position = V(-6, -1, 0) },
            },
        };

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "ged_testlevel.rfl";
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = brushes });
        AddSection(rfl, SectionType.RoomEffects, new RoomEffectsSection { Effects = effects });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, System.Array.Empty<byte>()));
        _ = null!;
        return rfl;
    }

    private static void AddSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        var s = new RflSection((uint)type, System.Array.Empty<byte>()) { Content = content, Dirty = true };
        rfl.Sections.Add(s);
    }

    private static Brush AirBox(int uid, Vec3 c, float w, float h, float d) =>
        CompilerTestBrushes.MakeBox(uid, c, w, h, d, BrushFlags.Air, "wall");

    private static Brush SolidBox(int uid, Vec3 c, float w, float h, float d) =>
        CompilerTestBrushes.MakeBox(uid, c, w, h, d, BrushFlags.None, "wall");

    private static Brush PortalSlab(int uid, Vec3 c, float t, float h, float d) =>
        CompilerTestBrushes.MakeBox(uid, c, t, h, d, BrushFlags.Air | BrushFlags.Portal, "wall");
}
