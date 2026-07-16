using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Assets;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Vpp;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for packaging + library: the dependency scanner's exact
/// present/missing/base-skipped partition against a one-of-each-kind fixture
/// level, mesh-material and ATX-frame expansion, the VPP builder (rfl-first +
/// byte-exact), the builder view-model, library health (shadowing + content
/// duplicates), where-used, and texture verification.
/// </summary>
public sealed class PackagingTests : IDisposable
{
    private readonly string _temp;

    public PackagingTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_pack_" + Guid.NewGuid().ToString("N"));
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

    // ─── Scanner: exact partition ────────────────────────────────────────────

    [Fact]
    public void Scanner_Partitions_One_Of_Each_Kind_Exactly()
    {
        RflFile level = OneOfEachLevel();

        // Loose mount = user content (included). Base VPP = game-provided (skipped).
        string loose = Path.Combine(_temp, "loose");
        Directory.CreateDirectory(loose);
        foreach (string f in new[]
                 {
                     "wall.tga", "frame1.tga", "water.tga", "decal01.tga", "smoke.vbm", "bolt.tga",
                     "glow.tga", "widget.v3m", "override.tga", "amb.wav", "boom.wav", "model.v3m",
                     "fs.tga", "crater.tga", "doorstart.wav", "level.txt",
                 })
        {
            File.WriteAllBytes(Path.Combine(loose, f), new byte[] { 1, 2, 3, 4 });
        }

        // "anim" resolves to anim.atx (supercede), whose single frame is frame1.tga.
        File.WriteAllText(Path.Combine(loose, "anim.atx"), "[[frame]]\nfile = \"frame1.tga\"\n");

        string baseVpp = Path.Combine(_temp, "base.vpp");
        new VppBuilder().Add("basewall.tga", new byte[] { 9 }).Write(baseVpp);
        // ghost.tga exists nowhere -> missing.

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(loose),
            VppAssetSource.Open(baseVpp),
        });

        var scan = DependencyScanner.Scan(level, new VfsDependencyResolver(vfs),
            new DependencyScanOptions { DialogueTextFile = "level.txt" });

        var expectedIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wall.tga", "anim.atx", "frame1.tga", "water.tga", "decal01.tga", "smoke.vbm", "bolt.tga",
            "glow.tga", "widget.v3m", "override.tga", "amb.wav", "boom.wav", "model.v3m", "fs.tga",
            "crater.tga", "doorstart.wav", "level.txt",
        };

        Assert.Equal(expectedIncluded, scan.IncludedNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "basewall.tga" }, scan.SkippedNames.OrderBy(x => x));
        Assert.Equal(new[] { "ghost.tga" }, scan.MissingNames.OrderBy(x => x));
        Assert.True(scan.HasMissing);
        Assert.True(scan.TotalIncludedSize > 0);

        // The ATX frame was discovered via expansion, kinded as AtxFrame.
        Assert.Contains(scan.All, d => d.Kind == DependencyKind.AtxFrame && d.FileName == "frame1.tga");
        // Each kind that has a direct reference is represented.
        foreach (DependencyKind kind in new[]
                 {
                     DependencyKind.FaceTexture, DependencyKind.LiquidTexture, DependencyKind.DecalTexture,
                     DependencyKind.ParticleBitmap, DependencyKind.BoltBitmap, DependencyKind.CoronaBitmap,
                     DependencyKind.EventSound, DependencyKind.EventBitmap, DependencyKind.EventMesh,
                     DependencyKind.MeshObject, DependencyKind.MeshObjectTexture, DependencyKind.AmbientSound,
                     DependencyKind.MoverSound, DependencyKind.GeomodTexture, DependencyKind.DialogueText,
                 })
        {
            Assert.Contains(scan.All, d => d.Kind == kind);
        }
    }

    [Fact]
    public void Scanner_Expands_Mesh_Material_Textures()
    {
        // A real fixture mesh references at least one diffuse texture.
        string? meshSrc = TestPaths.FixtureFile("mesh", "wallcomputer1.v3m");
        if (meshSrc is null)
        {
            return; // retail-derived mesh fixture not present
        }

        string loose = Path.Combine(_temp, "meshloose");
        Directory.CreateDirectory(loose);
        File.Copy(meshSrc, Path.Combine(loose, "widget.v3m"));

        V3dFile mesh = V3dReader.Read(File.ReadAllBytes(meshSrc));
        var meshTextures = mesh.Submeshes
            .SelectMany(sm => sm.Materials.Select(m => m.DiffuseMapName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(meshTextures);
        foreach (string t in meshTextures)
        {
            File.WriteAllBytes(Path.Combine(loose, t), new byte[] { 7 });
        }

        var rfl = NewLevel();
        AddSection(rfl, SectionType.AlpineMeshObjects, new AlpineMeshObjectsSection
        {
            Meshes = { new AlpineMeshObject { Uid = 10, MeshFilename = "widget.v3m" } },
        });

        using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(loose) });
        var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));

        Assert.Contains(scan.Included, d => d.FileName == "widget.v3m" && d.Kind == DependencyKind.MeshObject);
        Assert.Contains(scan.All, d => d.Kind == DependencyKind.MeshObjectTexture);
        // The mesh's diffuse texture was pulled in and resolved as included.
        foreach (string t in meshTextures)
        {
            Assert.Contains(scan.Included, d => string.Equals(
                IO.Tex.SupercedeChain.GetBaseName(d.FileName),
                IO.Tex.SupercedeChain.GetBaseName(t), StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Scanner_Gathers_Mesh_And_Entity_Animations_With_BaseSkip()
    {
        // Loose mount = user content (.rfa animations packed in); base VPP = engine-provided (skipped).
        string loose = Path.Combine(_temp, "anim_loose");
        Directory.CreateDirectory(loose);
        File.WriteAllBytes(Path.Combine(loose, "wield.rfa"), new byte[] { 1 }); // mesh state anim (probed w/o ext)
        File.WriteAllBytes(Path.Combine(loose, "idle.rfa"), new byte[] { 1 });  // entity state anim
        string baseVpp = Path.Combine(_temp, "anim_base.vpp");
        new VppBuilder().Add("die.rfa", new byte[] { 9 }).Write(baseVpp); // entity death anim -> base-game skip
        // corpse.rfa exists nowhere -> missing.

        var rfl = NewLevel();
        AddSection(rfl, SectionType.AlpineMeshObjects, new AlpineMeshObjectsSection
        {
            Meshes =
            {
                new AlpineMeshObject
                {
                    Uid = 10,
                    MeshFilename = "widget.v3m",
                    StateAnim = "wield", // extensionless -> probes wield.rfa
                    IsClutter = 1,
                    Clutter = new AlpineMeshClutterInfo { CorpseStateAnim = "corpse.rfa" },
                },
            },
        });
        AddSection(rfl, SectionType.Entities, new EntitiesSection
        {
            Entities =
            {
                new Entity { Uid = 11, ClassName = "Guard", StateAnim = "idle.rfa", DeathAnim = "die.rfa" },
            },
        });

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(loose),
            VppAssetSource.Open(baseVpp),
        });

        // No EntityCatalog supplied: anim strings are still gathered (they are direct file refs).
        var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));

        // All four anim references were gathered and kinded as MeshAnimation.
        var anims = scan.All.Where(d => d.Kind == DependencyKind.MeshAnimation).ToList();
        Assert.Equal(4, anims.Count);

        Assert.Contains(scan.Included, d => d.Kind == DependencyKind.MeshAnimation && d.FileName == "wield.rfa");
        Assert.Contains(scan.Included, d => d.Kind == DependencyKind.MeshAnimation && d.FileName == "idle.rfa");
        Assert.Contains(scan.All, d => d.Kind == DependencyKind.MeshAnimation
                                       && d.FileName == "die.rfa" && d.Status == DependencyStatus.BaseGameSkipped);
        Assert.Contains(scan.MissingNames, n => n.Equals("corpse.rfa", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Builder: rfl-first + byte-exact ─────────────────────────────────────

    [Fact]
    public void Builder_Writes_Rfl_First_And_Byte_Exact_Contents()
    {
        string loose = Path.Combine(_temp, "b_loose");
        Directory.CreateDirectory(loose);
        var sources = new Dictionary<string, byte[]>
        {
            ["wall.tga"] = RandomBytes(1000),
            ["decal01.tga"] = RandomBytes(2048),
            ["amb.wav"] = RandomBytes(777),
        };
        foreach (var kv in sources)
        {
            File.WriteAllBytes(Path.Combine(loose, kv.Key), kv.Value);
        }

        var rfl = NewLevel();
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { BrushWith("wall.tga") } });
        AddSection(rfl, SectionType.Decals, new DecalsSection
        {
            Decals = { new Decal { Header = new ObjectHeader { Uid = 5 }, Texture = "decal01.tga" } },
        });
        AddSection(rfl, SectionType.AmbientSounds, new AmbientSoundsSection
        {
            Sounds = { new AmbientSound { Uid = 6, SoundFileName = "amb.wav" } },
        });

        using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(loose) });
        var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));

        byte[] levelBytes = Encoding.ASCII.GetBytes("RFL-LEVEL-PLACEHOLDER-BYTES");
        string outPath = Path.Combine(_temp, "mylevel.vpp");
        PackfileBuildResult result = PackfileBuilder.Build(levelBytes, "mylevel.rfl", scan.Included, outPath);

        Assert.Equal("mylevel.rfl", result.PackedFiles[0]);
        Assert.Empty(result.SkippedUnreadable);

        using VppArchive archive = VppArchive.Open(outPath);
        Assert.Equal("mylevel.rfl", archive.Entries[0].Name); // rfl-first convention
        Assert.Equal(levelBytes, archive.Read("mylevel.rfl"));
        foreach (var kv in sources)
        {
            Assert.True(archive.Contains(kv.Key), $"{kv.Key} missing from pack");
            Assert.Equal(kv.Value, archive.Read(kv.Key)); // byte-exact vs source
        }
    }

    // ─── Builder view-model ──────────────────────────────────────────────────

    [Fact]
    public void BuildPlan_Defaults_And_Blocking_Behaviour()
    {
        RflFile level = OneOfEachLevel();
        string loose = Path.Combine(_temp, "vm_loose");
        Directory.CreateDirectory(loose);
        File.WriteAllBytes(Path.Combine(loose, "wall.tga"), RandomBytes(64));
        File.WriteAllText(Path.Combine(loose, "anim.atx"), "[[frame]]\nfile = \"frame1.tga\"\n");
        File.WriteAllBytes(Path.Combine(loose, "frame1.tga"), RandomBytes(64));
        string baseVpp = Path.Combine(_temp, "vm_base.vpp");
        new VppBuilder().Add("basewall.tga", new byte[] { 9 }).Write(baseVpp);

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(loose),
            VppAssetSource.Open(baseVpp),
        });
        var scan = DependencyScanner.Scan(level, new VfsDependencyResolver(vfs));

        var plan = new PackfileBuildPlan(scan, "mylevel.rfl",
            PackfileBuildPlan.DefaultOutputPath(@"C:\rf", "mylevel.rfl", multiplayer: true));

        Assert.EndsWith(Path.Combine("user_maps", "multi", "mylevel.vpp"), plan.OutputPath);

        // Included files default checked; base-skipped default unchecked.
        Assert.All(plan.AllItems.Where(i => i.Status == DependencyStatus.Included), i => Assert.True(i.Include));
        Assert.All(plan.AllItems.Where(i => i.Status == DependencyStatus.BaseGameSkipped), i => Assert.False(i.Include));

        // Missing files + blocking on -> cannot build; toggle blocking off -> can.
        Assert.True(plan.HasMissing);
        Assert.True(plan.BlockOnMissing);
        Assert.False(plan.CanBuild);
        plan.BlockOnMissing = false;
        Assert.True(plan.CanBuild);

        Assert.Equal(plan.Selection.Count(), plan.SelectedCount);
        Assert.True(plan.SelectedSize > 0);
        Assert.NotEmpty(plan.Groups);
    }

    // ─── Library health ──────────────────────────────────────────────────────

    [Fact]
    public void LibraryHealth_Detects_Shadowing_And_Content_Duplicates()
    {
        // Loose "top" mount + a base VPP "bottom" mount.
        string top = Path.Combine(_temp, "top");
        Directory.CreateDirectory(top);
        byte[] shared = RandomBytes(256);
        File.WriteAllBytes(Path.Combine(top, "metal01.tga"), shared);        // wins over the VPP copy
        File.WriteAllBytes(Path.Combine(top, "metal_copy.tga"), shared);     // identical content, different name
        File.WriteAllBytes(Path.Combine(top, "unique.tga"), RandomBytes(128));

        string vpp = Path.Combine(_temp, "lib.vpp");
        new VppBuilder()
            .Add("metal01.tga", RandomBytes(999)) // shadowed by the loose copy
            .Add("other.tga", RandomBytes(64))
            .Write(vpp);

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(top),
            VppAssetSource.Open(vpp),
        });

        LibraryHealthReport report = LibraryHealth.Analyze(vfs);

        // metal01.tga present in both mounts, loose wins.
        ShadowedName shadow = Assert.Single(report.Shadowed, s => s.Name == "metal01.tga");
        Assert.Equal(2, shadow.Mounts.Count);
        Assert.False(shadow.Winner.IsPackfile);        // loose directory wins
        Assert.True(shadow.Shadowed.First().IsPackfile); // the VPP copy is shadowed

        // metal01.tga (winner) and metal_copy.tga share content.
        ContentDuplicate dup = Assert.Single(report.Duplicates);
        var names = dup.Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "metal01.tga", "metal_copy.tga" }, names);

        Assert.False(report.IsHealthy);
        Assert.Contains("Library Health Report", report.ToText());
    }

    [Fact]
    public void LibraryHealth_Emits_A_Report_Artifact()
    {
        // A deterministic synthetic library (shadow + content duplicate) always runs.
        string top = Path.Combine(_temp, "art_top");
        Directory.CreateDirectory(top);
        byte[] shared = RandomBytes(300);
        File.WriteAllBytes(Path.Combine(top, "wall_metal01.tga"), shared);
        File.WriteAllBytes(Path.Combine(top, "wall_metal01_copy.tga"), shared);
        File.WriteAllBytes(Path.Combine(top, "unique01.tga"), RandomBytes(120));
        string vpp = Path.Combine(_temp, "art_base.vpp");
        new VppBuilder().Add("wall_metal01.tga", RandomBytes(500)).Add("base_only.tga", RandomBytes(64)).Write(vpp);

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(top),
            VppAssetSource.Open(vpp),
        });

        var sb = new StringBuilder();
        sb.AppendLine("### Synthetic library (deterministic) ###");
        sb.AppendLine();
        sb.Append(LibraryHealth.Analyze(vfs).ToText());

        // If a real RF install is present, append a real texture-scope report too.
        if (TestPaths.HasRfInstall)
        {
            using AssetVfs real = GameMount.Mount(TestPaths.RfInstall!);
            // Cap the scope so the content-hash pass stays fast; still surfaces real findings.
            var texNames = real.GetTextureCategories()
                .First(c => c.Name == "All").Files
                .Take(1000)
                .ToList();
            LibraryHealthReport realReport = LibraryHealth.Analyze(real, texNames);
            sb.AppendLine();
            sb.AppendLine($"### Real install (texture scope, first {texNames.Count} names) ###");
            sb.AppendLine();
            sb.Append(realReport.ToText());
        }

        if (TestPaths.RepoRoot is not null)
        {
            string outDir = Path.Combine(TestPaths.RepoRoot, "tests", "artifacts");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "library_health.txt"), sb.ToString());
        }

        Assert.Contains("Name collisions", sb.ToString());
    }

    // ─── Where used ──────────────────────────────────────────────────────────

    [Fact]
    public void WhereUsed_Finds_Texture_Across_Reference_Kinds()
    {
        var rfl = NewLevel();
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { BrushWith("shared.tga") } });
        AddSection(rfl, SectionType.Decals, new DecalsSection
        {
            Decals = { new Decal { Header = new ObjectHeader { Uid = 42 }, Texture = "shared.tga" } },
        });

        // Matches by supercede base name (a .dds reference to the same base).
        IReadOnlyList<AssetUsage> hits = WhereUsed.Find(rfl, "shared.dds");
        Assert.Contains(hits, u => u.Kind == DependencyKind.FaceTexture);
        Assert.Contains(hits, u => u.Kind == DependencyKind.DecalTexture && u.Uid == 42);
        Assert.True(WhereUsed.IsUsed(rfl, "shared"));
        Assert.False(WhereUsed.IsUsed(rfl, "nonexistent"));
        Assert.Contains("shared", WhereUsed.UsedTextureBaseNames(rfl));
    }

    // ─── Verify all textures ─────────────────────────────────────────────────

    [Fact]
    public void TextureVerifier_Flags_Missing_And_NonPowerOfTwo()
    {
        string dir = Path.Combine(_temp, "verify");
        Directory.CreateDirectory(dir);
        // A real 2x2 (power-of-two) texture and, referenced but absent, a missing one.
        File.Copy(TestPaths.Fixture("tex", "gradient2x2.png"), Path.Combine(dir, "good.png"));

        var rfl = NewLevel();
        AddSection(rfl, SectionType.Brushes, new BrushesSection
        {
            Brushes = { BrushWith("good"), BrushWith("missing_tex.tga") },
        });

        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(dir, extensions: IO.Tex.SupercedeChain.Extensions),
        });

        IReadOnlyList<TextureVerifyResult> results = TextureVerifier.Verify(rfl, vfs);
        Assert.Contains(results, r => r.TextureName.Equals("missing_tex", StringComparison.OrdinalIgnoreCase)
                                      && r.Issue == TextureIssue.Missing);
        // good.png is 2x2 -> power of two, so it must NOT be flagged NPOT.
        Assert.DoesNotContain(results, r => r.TextureName.Equals("good", StringComparison.OrdinalIgnoreCase)
                                            && r.Issue == TextureIssue.NonPowerOfTwo);
    }

    [Fact]
    public void TextureVerifier_Flags_NonPowerOfTwo_Dimensions()
    {
        string dir = Path.Combine(_temp, "npot");
        Directory.CreateDirectory(dir);
        // solid8x8.jpg is 8x8 (POT); build a 3x3 PNG to trip the NPOT check.
        byte[] rgba = new byte[3 * 3 * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(i * 7);
        }

        File.WriteAllBytes(Path.Combine(dir, "odd.png"),
            IO.Tex.PngWriter.Encode(new IO.Tex.TextureImage(3, 3, rgba)));

        var rfl = NewLevel();
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { BrushWith("odd") } });
        using var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(dir, extensions: IO.Tex.SupercedeChain.Extensions),
        });

        IReadOnlyList<TextureVerifyResult> results = TextureVerifier.Verify(rfl, vfs);
        Assert.Contains(results, r => r.Issue == TextureIssue.NonPowerOfTwo);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // ─── Item 4: light cookies are editor-only, excluded from the pack ──────────

    [Fact]
    public void Scanner_Excludes_Light_Cookie_Files_As_Editor_Only()
    {
        var rfl = NewLevel();

        // A decal names "cookie.tga" (normally a packable, and here MISSING) — but the same file is
        // also a light projection cookie, so the scanner must drop it entirely: never packed, and
        // never flagged missing-for-pack. A second decal names a normal texture as the control.
        AddSection(rfl, SectionType.Decals, new DecalsSection
        {
            Decals =
            {
                new Decal { Header = new ObjectHeader { Uid = 5 }, Texture = "cookie.tga" },
                new Decal { Header = new ObjectHeader { Uid = 6 }, Texture = "normal.tga" },
            },
        });

        var meta = new GedObjectMetadataSection();
        meta.Entries.Add(new GedObjectMetadataRecord
        {
            Uid = 9,
            Blocks = { new GedObjectMetadataBlock(GedMetadataType.LightCookie, VstringBytes("cookie.tga")) },
        });
        AddSection(rfl, SectionType.GedObjectMetadata, meta);

        IReadOnlyList<DependencyRef> refs = DependencyScanner.Gather(rfl);

        Assert.DoesNotContain(refs, r => string.Equals(r.FileName, "cookie.tga", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(refs, r => string.Equals(r.FileName, "normal.tga", StringComparison.OrdinalIgnoreCase));

        // And it is never flagged missing-for-pack even when absent from every mount.
        using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(_temp) });
        DependencyScanResult scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
        Assert.DoesNotContain("cookie.tga", scan.MissingNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("normal.tga", scan.MissingNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static byte[] VstringBytes(string s)
    {
        var w = new Ged.Core.IO.RfWriter(s.Length + 2);
        w.WriteVString(s);
        return w.ToArray();
    }

    private static RflFile NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D; // Alpine so Alpine sections serialize
        rfl.Header.LevelName = "mylevel.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static RflFile OneOfEachLevel()
    {
        var rfl = NewLevel();

        var brush = BrushWith("wall.tga");
        brush.Geometry.Textures.Add("basewall.tga"); // resolves from the base VPP -> skipped
        brush.Geometry.Textures.Add("ghost.tga");    // nowhere -> missing
        brush.Geometry.Textures.Add("anim");         // resolves to anim.atx -> frame expansion
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { brush } });

        AddSection(rfl, SectionType.RoomEffects, new RoomEffectsSection
        {
            Effects =
            {
                new RoomEffect
                {
                    EffectType = RoomEffectsSection.EffectLiquidRoom,
                    LiquidProperties = new RoomEffectLiquidProperties { SurfaceTexture = "water.tga" },
                    Header = new ObjectHeader { Uid = 100 },
                },
            },
        });

        AddSection(rfl, SectionType.Decals, new DecalsSection
        {
            Decals = { new Decal { Header = new ObjectHeader { Uid = 101 }, Texture = "decal01.tga" } },
        });

        AddSection(rfl, SectionType.ParticleEmitters, new ParticleEmittersSection
        {
            Emitters = { new ParticleEmitter { Header = new ObjectHeader { Uid = 102 }, Texture = "smoke.vbm" } },
        });

        AddSection(rfl, SectionType.BoltEmitters, new BoltEmittersSection
        {
            Emitters = { new BoltEmitter { Header = new ObjectHeader { Uid = 103 }, Texture = "bolt.tga" } },
        });

        AddSection(rfl, SectionType.AlpineCoronaObjects, new AlpineCoronaObjectsSection
        {
            Coronas = { new AlpineCoronaObject { Uid = 104, CoronaBitmap = "glow.tga" } },
        });

        AddSection(rfl, SectionType.AlpineMeshObjects, new AlpineMeshObjectsSection
        {
            Meshes =
            {
                new AlpineMeshObject
                {
                    Uid = 105,
                    MeshFilename = "widget.v3m",
                    TextureOverrides = { new AlpineMeshTextureOverride { SlotId = 0, Filename = "override.tga" } },
                },
            },
        });

        AddSection(rfl, SectionType.AmbientSounds, new AmbientSoundsSection
        {
            Sounds = { new AmbientSound { Uid = 106, SoundFileName = "amb.wav" } },
        });

        AddSection(rfl, SectionType.Events, new EventsSection
        {
            Events =
            {
                new RflEvent { Uid = 107, ClassName = "Play_Sound", Str1 = "boom.wav" },
                new RflEvent { Uid = 108, ClassName = "Switch_Model", Str1 = "model.v3m" },
                new RflEvent { Uid = 109, ClassName = "Display_Fullscreen_Image", Str1 = "fs.tga" },
            },
        });

        AddSection(rfl, SectionType.LevelProperties, new LevelPropertiesSection { GeomodTexture = "crater.tga" });

        AddSection(rfl, SectionType.MovingGroups, new GroupsSection(SectionType.MovingGroups)
        {
            Groups =
            {
                new Group
                {
                    Name = "door1",
                    IsMoving = 1,
                    MovingData = new MovingGroupData { StartSound = "doorstart.wav" },
                },
            },
        });

        return rfl;
    }

    private static Brush BrushWith(string texture)
    {
        var b = new Brush { Uid = 200 };
        b.Geometry.Textures.Add(texture);
        return b;
    }

    private static void AddSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        // Insert before the trailing End section.
        var s = new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true };
        rfl.Sections.Insert(rfl.Sections.Count - 1, s);
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }
}
