using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Linting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The File ▸ New template lives in Core (<see cref="LevelTemplates"/>) so headless hosts create the
/// same document the editor does: initialized level_properties, a Player Start, and level_info in
/// RED's canonical order. It must save/reload, lint without a missing-start error, and build sealed
/// with zero holes.
/// </summary>
public sealed class LevelTemplateTests
{
    private static readonly DateTime When = new(2026, 7, 8, 9, 5, 3);

    [Fact]
    public void Template_Has_The_New_Level_Sections_In_Canonical_Order()
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(When);

        Assert.Equal(RflFile.AlpineSaveVersion, rfl.Header.Version); // v306: a new level starts at the version every save writes
        Assert.Equal(LevelTemplates.DefaultLevelName, rfl.Header.LevelName);
        Assert.Equal(
            new[] { SectionType.AlpineLevelProperties, SectionType.LevelProperties, SectionType.PlayerStart, SectionType.LevelInfo, SectionType.End },
            rfl.Sections.Select(s => (SectionType)s.TypeId).ToArray());

        PlayerStartSection start = rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>().Single();
        Assert.Equal(new Vec3(0f, 1f, 0f), start.Position);
        Assert.Equal(Mat3.Identity, start.Rotation);
        Assert.Equal("Wednesday, July 8, 2026 09:05:03",
            rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().Single().Date);
    }

    /// <summary>
    /// A new level carries exactly the alpine_level_properties chunk Alpine RED saves for File ▸ New: version 6 in the
    /// full layout (level.h:1244-1331), every field at Alpine's defaults (LoadDefaults, level.h:1133-1180; payload rules
    /// common/include/common/payload.h:25-63) except Legacy Cyclic Timers and Legacy Movers, which are off
    /// (LoadNewLevelDefaults, level.h:1123-1128). Without it the game would treat the level as a legacy one.
    /// </summary>
    [Fact]
    public void Template_Carries_Alpine_Reds_New_Level_Properties()
    {
        RflFile rfl = LevelTemplates.CreateNewRfl(When);
        RflSection host = rfl.Sections.Single(s => s.TypeId == (uint)SectionType.AlpineLevelProperties);
        byte[] body = host.GetBodyBytes(rfl.Context);
        Assert.Equal(Convert.FromHexString(string.Concat(
            "06000000",
            // v1-v4: legacy cyclic timers 0, legacy movers 0, headlamp 1, override 0, ambient 2.0, RF2 geomod 0, three empty tables
            "00", "00", "01", "00", "00000040", "00", "00000000", "00000000", "00000000",
            // v5: sun off, yaw 0, pitch 90, colour 255x4, intensity 1, spread 0, casts shadows 1, affects meshes 1, mesh mode 0,
            // drives shadow-map direction 1, legacy lighting 0, high-res 0, liquid occludes 1, invisible 0, alpha 0,
            // no-shadow-cast list empty, meshes occlude 0, density 0, D3D11-only 0, compression 0
            "00", "00000000", "0000B442", "FFFFFFFF", "0000803F", "00000000", "01", "01", "00", "01", "00", "00", "01", "00", "00",
            "00000000", "00", "00", "00", "00",
            // v6: flight ceiling off / 0, minimap off, bitmap "", min/max 0, cut height 0, require D3D11 0, far clip 0, alpha-tested 0
            "00", "00000000", "00", "0000", new string('0', 48), "00000000", "00", "00000000", "00",
            // payload rules: push 1.5, rollback 0.5, accel 2, bonus 180, heal 10, grace 5, attacker {0,0,0}, defender {1,0.5,1}
            "0000C03F", "0000003F", "00000040", "00003443", "00002041", "0000A040",
            "00000000", "00000000", "00000000", "0000803F", "0000003F", "0000803F")), body);
        Assert.Equal(153, body.Length); // the current minimum v6 size (ALPINE-COMPAT-SPEC §1.3)

        // Read back through the game's reading rules: nothing short, nothing left over.
        var props = (AlpineLevelPropertiesSection)AlpineLevelPropertiesSection.Parse(new RfReader(body), rfl.Context);
        Assert.False(props.IsShortLayout);
        Assert.Empty(props.UnparsedTail);
    }

    [Fact]
    public void Template_Is_Deterministic_For_A_Fixed_Date()
    {
        Assert.Equal(LevelTemplates.CreateNew(When).SaveToBytes(), LevelTemplates.CreateNew(When).SaveToBytes());
    }

    [Fact]
    public void Template_Saves_Reloads_Lints_And_Builds_Sealed_With_Zero_Holes()
    {
        EditorDocument doc = LevelTemplates.CreateNew(When);
        Assert.Null(doc.Path);

        byte[] bytes = doc.SaveToBytes();
        EditorDocument reloaded = EditorDocument.OpenBytes(bytes);
        reloaded.Rfl.ParseAllKnownSections();
        Assert.True(reloaded.Rfl.Header.PlayerStartOffset > 0);
        Assert.True(reloaded.Rfl.Header.LevelInfoOffset > 0);
        Assert.Single(reloaded.Rfl.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>());

        LintReport lint = LevelLinter.Lint(reloaded.Rfl);
        Assert.DoesNotContain(lint.Findings, f => f.Category == LintCategory.MissingPlayerStart);

        // The template has no geometry: a sealed build of it is trivially watertight.
        CompileOptions options = CompileOptionsFactory.CreateForDocument(
            reloaded, vfs: null, BuildSettings.EditorDefaults, new CompileRequest { Sealed = true });
        CompiledLevel result = GeometryBuildService.BuildAndApply(reloaded.Rfl, options);
        List<Vec3> holes = HoleDetector.Detect(result.Geometry);
        Assert.Empty(holes);

        // …and the built level still round-trips.
        EditorDocument again = EditorDocument.OpenBytes(reloaded.SaveToBytes());
        again.Rfl.ParseAllKnownSections();
        Assert.Single(again.Rfl.Sections.Select(s => s.Content).OfType<PlayerStartSection>());
    }
}
