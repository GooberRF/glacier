using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tests.Compiler;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Pins RED's canonical RFL section order on save, the fix for the "from-scratch
/// levels render fully black in game" defect. RF's two-phase loader binds every
/// world surface to its lightmap at geometry-load time from a registry only
/// populated by an already-parsed lightmaps section, so a lightmaps section
/// written AFTER static_geometry is skipped and the whole level renders black.
/// GED must therefore write lightmaps BEFORE static_geometry, and must repair
/// early Glacier files that have the broken order on disk.
/// </summary>
public sealed class RflSectionOrderTests
{
    private static Vec3 V(float x, float y, float z) => new(x, y, z);

    private static int IndexOf(RflFile rfl, SectionType type) =>
        rfl.Sections.FindIndex(s => s.TypeId == (uint)type);

    /// <summary>A from-scratch level authored the way EditorSession.NewLevel does, plus brushwork.</summary>
    private static RflFile ScratchLevel()
    {
        var brushes = new List<Brush>
        {
            CompilerTestBrushes.AirBox(1, V(-6, 0, 0), 12, 8, 12),                 // room A
            CompilerTestBrushes.AirBox(2, V(6, 0, 0), 12, 6, 6),                   // room B
            CompilerTestBrushes.MakeBox(3, V(0, 0, 0), 0.4f, 6, 6,                 // portal in the doorway
                BrushFlags.Air | BrushFlags.Portal, "wall"),
            CompilerTestBrushes.SolidBox(4, V(-8, 0, 0), 2, 8, 2),                 // pillar in room A
        };

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "scratch.rfl";

        // Same section set + canonical order EditorSession.NewLevel emits (level_properties,
        // player_start, level_info), then a brushes section — before any build runs.
        Add(rfl, SectionType.LevelProperties, LevelPropertiesSection.CreateDefault());
        Add(rfl, SectionType.PlayerStart, new PlayerStartSection { Position = V(0, 1, 0), Rotation = Mat3.Identity });
        Add(rfl, SectionType.LevelInfo, LevelInfoSection.CreateDefault(DateTime.Now));
        Add(rfl, SectionType.Brushes, new BrushesSection { Brushes = brushes });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static void Add(RflFile rfl, SectionType type, IRflSectionContent content) =>
        rfl.Sections.Add(new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true });

    // (a) A from-scratch build writes lightmaps BEFORE static_geometry.
    [Fact]
    public void FromScratch_Build_Writes_Lightmaps_Before_StaticGeometry()
    {
        RflFile rfl = ScratchLevel();
        CompiledLevel built = GeometryBuildService.BuildAndApply(rfl);

        Assert.True(built.Lightmaps.Count > 0, "build produced no lightmap pages");

        int lm = IndexOf(rfl, SectionType.Lightmaps);
        int geo = IndexOf(rfl, SectionType.StaticGeometry);
        Assert.True(lm >= 0 && geo >= 0, "expected both lightmaps and static_geometry sections");
        Assert.True(lm < geo, $"lightmaps (idx {lm}) must precede static_geometry (idx {geo})");

        // Survives serialization + reload in the same order (this is what the game reads).
        RflFile reloaded = RflFile.Load(rfl.Save(updateTimestamp: false));
        Assert.True(IndexOf(reloaded, SectionType.Lightmaps) < IndexOf(reloaded, SectionType.StaticGeometry));
    }

    // (b) A from-scratch full section order matches the canonical table.
    [Fact]
    public void FromScratch_Build_Full_Section_Order_Is_Canonical()
    {
        RflFile rfl = ScratchLevel();
        GeometryBuildService.BuildAndApply(rfl);

        uint[] actual = rfl.Sections.Select(s => s.TypeId).ToArray();
        uint[] expected =
        {
            (uint)SectionType.LevelProperties,
            (uint)SectionType.Lightmaps,
            (uint)SectionType.StaticGeometry,
            (uint)SectionType.PlayerStart,
            (uint)SectionType.LevelInfo,
            (uint)SectionType.Brushes,
            (uint)SectionType.End,
        };

        Assert.Equal(expected, actual);
    }

    // The canonical-rank comparator itself: lightmaps < static_geometry, End sorts last,
    // and phase-1 sections all precede the geometry.
    [Fact]
    public void CanonicalRank_Places_Phase1_Sections_Before_StaticGeometry()
    {
        int geo = RflSectionOrder.Rank((uint)SectionType.StaticGeometry);
        Assert.True(RflSectionOrder.Rank((uint)SectionType.Lightmaps) < geo);
        Assert.True(RflSectionOrder.Rank((uint)SectionType.LevelProperties) < geo);
        Assert.True(RflSectionOrder.Rank((uint)SectionType.AlpineLevelProperties) < geo);
        Assert.True(RflSectionOrder.Rank((uint)SectionType.TgaFiles) < geo);
        Assert.True(RflSectionOrder.Rank((uint)SectionType.AlpineCoronaObjects) < geo);

        // Phase-2 sections follow the geometry.
        Assert.True(RflSectionOrder.Rank((uint)SectionType.Lights) > geo);
        Assert.True(RflSectionOrder.Rank((uint)SectionType.Movers) > geo);

        // End always sorts last so a new section lands before it.
        Assert.Equal(int.MaxValue, RflSectionOrder.Rank((uint)SectionType.End));
    }

    // A lightmaps section created when static_geometry already exists is inserted before it,
    // never appended after (the core insertion invariant).
    [Fact]
    public void InsertSection_Places_New_Lightmaps_Before_Existing_Geometry()
    {
        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelProperties, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.Lights, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        rfl.InsertSection(new RflSection((uint)SectionType.Lightmaps, Array.Empty<byte>()));

        Assert.True(IndexOf(rfl, SectionType.Lightmaps) < IndexOf(rfl, SectionType.StaticGeometry));
        // Existing sections were not reordered.
        Assert.True(IndexOf(rfl, SectionType.StaticGeometry) < IndexOf(rfl, SectionType.Lights));
    }

    // (c) A synthetic file with lightmaps-after-geometry is repaired on the editor load path.
    [Fact]
    public void BrokenOnDisk_Order_Is_Repaired_On_Load()
    {
        // Build a valid from-scratch level (lightmaps first), then deliberately move the
        // lightmaps section to AFTER static_geometry to synthesize the on-disk defect that
        // early Glacier builds wrote.
        RflFile rfl = ScratchLevel();
        GeometryBuildService.BuildAndApply(rfl);

        int lm = IndexOf(rfl, SectionType.Lightmaps);
        RflSection lightmaps = rfl.Sections[lm];
        rfl.Sections.RemoveAt(lm);
        int geo = IndexOf(rfl, SectionType.StaticGeometry);
        rfl.Sections.Insert(geo + 1, lightmaps); // now AFTER geometry — broken

        byte[] brokenBytes = rfl.Save(updateTimestamp: false);

        // Confirm the synthesized file really is broken.
        RflFile raw = RflFile.Load(brokenBytes);
        Assert.True(IndexOf(raw, SectionType.Lightmaps) > IndexOf(raw, SectionType.StaticGeometry),
            "test setup failed to synthesize the broken order");

        // The editor load path repairs it.
        EditorDocument doc = EditorDocument.OpenBytes(brokenBytes);
        Assert.True(IndexOf(doc.Rfl, SectionType.Lightmaps) < IndexOf(doc.Rfl, SectionType.StaticGeometry),
            "load-time repair did not move lightmaps before static_geometry");

        // The repair persists through a resave.
        RflFile resaved = RflFile.Load(doc.Rfl.Save(updateTimestamp: false));
        Assert.True(IndexOf(resaved, SectionType.Lightmaps) < IndexOf(resaved, SectionType.StaticGeometry));
    }

    [Fact]
    public void RepairLightmapOrder_Is_A_NoOp_When_Already_Correct()
    {
        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)SectionType.LevelProperties, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.Lightmaps, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        Assert.False(rfl.RepairLightmapOrder());
    }

    [Fact]
    public void RepairLightmapOrder_Is_A_NoOp_Without_Lightmaps()
    {
        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)SectionType.StaticGeometry, Array.Empty<byte>()));
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        Assert.False(rfl.RepairLightmapOrder());
    }

    // (d, corpus sweep) NO corpus level (RED- or Glacier-authored) has lightmaps after
    // static_geometry, so the repair predicate never fires on the corpus and the
    // byte-identity round-trip gate (NoOpRoundTripTests) is unaffected.
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Corpus_Never_Has_Lightmaps_After_StaticGeometry(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        RflFile rfl = RflFile.Load(Path.Combine(Corpus.Directory!, fileName));
        int lm = IndexOf(rfl, SectionType.Lightmaps);
        int geo = IndexOf(rfl, SectionType.StaticGeometry);
        bool broken = lm >= 0 && geo >= 0 && lm > geo;
        Assert.False(broken, $"{fileName}: lightmaps (idx {lm}) after static_geometry (idx {geo}) — repair predicate would fire on a corpus file");

        // The repair must be a no-op on every corpus file (byte-identity preserved).
        Assert.False(rfl.RepairLightmapOrder(), $"{fileName}: RepairLightmapOrder mutated a corpus file");
    }
}
