using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Modules;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Modules;

/// <summary>The module harvester on the in-code fixture level (<see cref="ModuleTestLevel"/>).</summary>
public sealed class ModuleHarvesterTests
{
    private readonly ITestOutputHelper _out;

    public ModuleHarvesterTests(ITestOutputHelper output) => _out = output;

    private List<BuiltModule> Harvest(EditorDocument doc)
    {
        (LevelHarvestResult level, List<BuiltModule> built) = ModuleTestLevel.Harvest(doc);
        _out.WriteLine($"brushes {level.Brushes}, candidates {level.Candidates}, components {level.Components}, rejected: {string.Join(", ", level.Rejected.Select(kv => $"{kv.Key} {kv.Value}"))}");
        foreach (BuiltModule b in built)
        {
            ModuleInfo m = b.Info;
            _out.WriteLine($"{m.Id} {m.Category}/{m.Attach} size {m.Size} occ {m.Occurrences} stretch [{string.Join(",", m.StretchAxes)}] tiles {m.Tiles.Count} prov {m.Provenance[0].Position} yaw {m.Provenance[0].Yaw}");
        }

        return built;
    }

    private static ModuleInfo Single(List<BuiltModule> built, string category) =>
        Assert.Single(built.Where(b => b.Info.Category == category).Select(b => b.Info));

    [Fact]
    public void Finds_The_Expected_Categories_And_Attachments()
    {
        List<BuiltModule> built = Harvest(ModuleTestLevel.Build());
        Assert.Equal("wall", Single(built, "pilaster").Attach);
        Assert.Equal("edge", Single(built, "railing").Attach);
        Assert.Equal("opening", Single(built, "door_frame").Attach);
        Assert.Equal("span", Single(built, "beam").Attach);
        Assert.Equal("floor", Single(built, "crate_stack").Attach);
        Assert.Equal("route", Single(built, "railing").Role);
        Assert.Equal("cover", Single(built, "crate_stack").Role);
        Assert.Equal("theme_detail", Single(built, "pilaster").Role);
    }

    [Fact]
    public void Local_Frames_Anchor_Wall_At_Z0_And_Floor_At_Y0()
    {
        List<BuiltModule> built = Harvest(ModuleTestLevel.Build());
        ModuleInfo pil = Single(built, "pilaster");
        Assert.Equal(0, pil.BoundsMin.Z, 3);          // the wall plane
        Assert.Equal(0.5, pil.BoundsMax.Z, 3);        // extends into the room
        Assert.Equal(0, pil.BoundsMin.Y, 3);          // the floor
        Assert.Equal(4.0, pil.BoundsMax.Y, 3);
        Assert.Equal(-0.4, pil.BoundsMin.X, 3);       // x centred
        Assert.Equal(0.4, pil.BoundsMax.X, 3);
        Assert.Null(pil.MountHeight);

        // The representative sits on the +Z wall facing −Z: local +Z (into the room) is world −Z (yaw 180).
        ModuleProvenance p = pil.Provenance[0];
        Assert.Equal(180, p.Yaw, 3);
        Assert.Equal(6, p.Position.Z, 3);
        Assert.Equal(0, p.Position.Y, 3);

        ModuleInfo rail = Single(built, "railing");
        Assert.Equal(0, rail.BoundsMin.Y, 3);          // y = 0 on the platform floor
        Assert.Equal(0.05, rail.BoundsMin.Z, 2);       // 5 cm back from the edge line
        Assert.Equal(2, rail.Provenance[0].Position.Y, 3);
        Assert.Equal(-1, rail.Provenance[0].Position.Z, 2);

        ModuleInfo door = Single(built, "door_frame");
        Assert.NotNull(door.Opening);
        Assert.Equal(1.6, door.Opening!.Width, 1);
        Assert.Equal(2.4, door.Opening.Height, 1);
        Assert.Equal(0.5, door.Opening.WallThickness, 2);
        Assert.Equal(-0.35, door.BoundsMin.Z, 2);      // z = 0 at the wall mid-plane
        Assert.Equal(0.35, door.BoundsMax.Z, 2);

        ModuleInfo beam = Single(built, "beam");
        Assert.Equal(12, beam.Size.X, 2);              // x along the span
        Assert.Equal(0, beam.BoundsMax.Y, 3);          // y = 0 at the ceiling, extends −Y
    }

    [Fact]
    public void Stretch_Slices_And_Tile_Families_Are_Detected()
    {
        List<BuiltModule> built = Harvest(ModuleTestLevel.Build());
        ModuleInfo pil = Single(built, "pilaster");
        Assert.NotNull(pil.StretchY);
        Assert.Equal(0.4, pil.StretchY!.A, 3);   // the shaft between base and cap
        Assert.Equal(3.6, pil.StretchY.B, 3);
        Assert.NotNull(pil.StretchX);
        Assert.Equal(0.6, pil.StretchX!.Length, 3);
        Assert.Null(pil.StretchZ);

        ModuleInfo rail = Single(built, "railing");
        Assert.NotNull(rail.StretchX);
        Assert.Equal(6, rail.StretchX!.Length, 3); // the rail stretches, the posts repeat
        ModuleTileFamily posts = Assert.Single(rail.Tiles);
        Assert.Equal("x", posts.Axis);
        Assert.Equal(7, posts.Count);
        Assert.Equal(0.98, posts.Pitch, 2);
        Assert.Single(posts.First);
        Assert.Equal(6, posts.Replicas.Count);

        ModuleInfo door = Single(built, "door_frame");
        Assert.NotNull(door.StretchX);
        Assert.Equal(1.6, door.StretchX!.Length, 3); // between the jambs
        Assert.NotNull(door.StretchY);
        Assert.Equal(2.4, door.StretchY!.Length, 3);

        ModuleInfo beam = Single(built, "beam");
        Assert.NotNull(beam.StretchX);
    }

    [Fact]
    public void Dedupe_Collapses_Identical_And_Near_Identical_Pilasters_And_Rotated_Crates()
    {
        List<BuiltModule> built = Harvest(ModuleTestLevel.Build());
        ModuleInfo pil = Single(built, "pilaster");
        Assert.Equal(ModuleTestLevel.PilasterX.Length + 1, pil.Occurrences); // 4 identical + the 3 cm variant
        Assert.Equal(pil.Occurrences, pil.Provenance.Count);
        Assert.Equal(3, pil.SuggestedPitch!.Value, 3);
        Assert.Equal(2, Single(built, "crate_stack").Occurrences);
        Assert.Equal(3, Single(built, "pilaster").Brushes);
        Assert.All(built, b => Assert.Equal(b.Info.Brushes, b.Brushes.Count));
    }

    [Fact]
    public void Distractors_Are_Rejected()
    {
        (LevelHarvestResult level, List<BuiltModule> built) = ModuleTestLevel.Harvest(ModuleTestLevel.Build());
        Assert.True(level.Rejected.GetValueOrDefault("too_small") >= 1);
        Assert.True(level.Rejected.GetValueOrDefault("free_single") + level.Rejected.GetValueOrDefault("plain_box") >= 1);
        Assert.DoesNotContain(built, b => b.Info.Size.X > 4 && b.Info.Size.Z > 4); // the structural pillar is never a candidate
    }

    [Fact]
    public void Harvest_Is_Deterministic_And_Order_Independent()
    {
        string a = ModuleTestLevel.TempDir(), b = ModuleTestLevel.TempDir();
        try
        {
            List<BuiltModule> first = ModuleTestLevel.Harvest(ModuleTestLevel.Build()).Modules;
            List<BuiltModule> second = ModuleTestLevel.Harvest(ModuleTestLevel.Build()).Modules;
            ModuleLibraryBuilder.Write(a, first, new[] { "stock" });
            ModuleLibraryBuilder.Write(b, second, new[] { "stock" });
            var filesA = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var filesB = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(filesA, filesB);
            Assert.True(filesA.Count > 2);
            foreach (string f in filesA)
            {
                Assert.True(File.ReadAllBytes(Path.Combine(a, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f))), $"{f} differs between two harvests");
            }

            // The library reads back.
            ModuleLibrary lib = ModuleLibrary.Load(a);
            Assert.True(lib.Available);
            Assert.Equal(first.Count, lib.Modules.Count);
            ModuleInfo pil = lib.Query(new ModuleQuery { Category = "pilaster" }).Single();
            Assert.Equal(3, ModuleLibrary.LoadBrushes(pil).Count);

            // Shuffling the detail brushes' order changes nothing about the module identities.
            var ids = first.Select(m => m.Info.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
            foreach (int seed in new[] { 7, 42 })
            {
                var shuffled = ModuleTestLevel.Harvest(ModuleTestLevel.Build(shuffleDetails: seed)).Modules.Select(m => m.Info.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
                Assert.Equal(ids, shuffled);
            }
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public void Solidity_Follows_Time_Order()
    {
        EditorDocument doc = ModuleTestLevel.Build();
        var index = new BrushSolidityIndex(ModuleTestLevel.Brushes(doc));
        Assert.False(index.IsSolid(new D3(0, 1, 0)));           // inside the room air
        Assert.True(index.IsSolid(new D3(0, -1, 0)));           // below the floor: the world starts solid
        Assert.True(index.IsSolid(new D3(5, 1, -3)));           // the platform solid fills the earlier room air
        Assert.False(index.IsSolid(new D3(7.5, 1, 6.25)));      // the doorway carves the wall
        Assert.True(index.IsSolid(new D3(7.5, 3, 6.25)));       // above the doorway: wall
        Assert.True(index.IsSolid(new D3(-8, 2, 5.8)));         // a pilaster shaft (detail solid) ...
        Assert.False(index.IsSolid(new D3(-8, 2, 5.8), structuralOnly: true)); // ... ignored structurally
        int? shaft = index.LastBrushAt(new D3(-8, 2, 5.8));
        Assert.NotNull(shaft);
        Assert.False(index.IsSolid(new D3(-8, 2, 5.8), new HashSet<int> { shaft!.Value })); // excluded → room air
    }

    [Fact]
    public void Locator_Without_Library_Reports_Unavailable_And_Never_Throws()
    {
        string dir = ModuleTestLevel.TempDir();
        try
        {
            ModuleLibrary missing = ModuleLibrary.Locate(Path.Combine(dir, "nope"));
            Assert.False(missing.Available);
            Assert.False(string.IsNullOrWhiteSpace(missing.Reason));
            Assert.Empty(missing.Query(new ModuleQuery { Category = "pilaster" }));
            Assert.Null(missing.Find("pilaster_00000000"));
            Assert.Equal(0, missing.Stats().Modules);

            ModuleLibrary empty = ModuleLibrary.Load(dir);
            Assert.False(empty.Available);
            Assert.Contains("harvest", empty.Reason!, StringComparison.OrdinalIgnoreCase);

            // No override, no environment, a base folder with no library and no checkout above it.
            ModuleLibraryLocation none = ModuleLibraryLocator.Locate(null, _ => null, Path.GetPathRoot(dir));
            Assert.True(none.Directory is null || none.How == "install");
            ModuleLibraryLocation env = ModuleLibraryLocator.Locate(null, n => n == ModuleLibraryLocator.EnvironmentVariable ? dir : null, dir);
            Assert.Equal("environment", env.How);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Manifest_Globs_Take_Each_File_Once_In_Order()
    {
        string dir = ModuleTestLevel.TempDir();
        try
        {
            foreach (string f in new[] { "dm01.rfl", "dm07.rfl", "dm21.rfl", "dmother.rfl", "ctf03.rfl", "a.vpp", "b.vpp", "notes.txt" })
            {
                File.WriteAllText(Path.Combine(dir, f), "x");
            }

            HarvestManifest m = HarvestManifest.Parse("""
                { "sources": [
                  { "include": ["dm0[1-9].rfl", "dm1[0-9].rfl", "dm20.rfl"], "group": "stock" },
                  { "include": "ctf0[1-7].rfl", "group": "stock" },
                  { "include": "b.vpp", "group": "extra", "mount_with": ["a.vpp"], "style_hint": "mine" },
                  { "include": "*.vpp", "group": "extra" }
                ] }
                """);
            List<HarvestSourceFile> files = m.Resolve(dir);
            Assert.Equal(new[] { "dm01.rfl", "dm07.rfl", "ctf03.rfl", "b.vpp", "a.vpp" }, files.Select(f => f.RelativePath));
            Assert.Equal("mine", files[3].Entry.StyleHint);
            Assert.Equal(new[] { "stock", "extra" }, m.Groups);
            Assert.Throws<ModuleHarvestException>(() => ModuleHarvestRunner.GuardOutput(Path.Combine(dir, "out"), new[] { dir }));
            Assert.Throws<ModuleHarvestException>(() => ModuleHarvestRunner.GuardOutput(Path.GetDirectoryName(dir)!, new[] { dir }));
            ModuleHarvestRunner.GuardOutput(Path.Combine(Path.GetDirectoryName(dir)!, "elsewhere"), new[] { dir });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
