using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// Builds are identical no matter who starts them. Script builds
/// (<c>ops.build</c> / <c>ops.light</c>) and the editor's builds assemble their
/// <see cref="CompileOptions"/> through the same <see cref="CompileOptionsFactory"/>, so a script
/// build and a GUI-path build with the same <see cref="BuildSettings"/> save byte-identical RFLs —
/// including the Alpine geoable/breakable room-UID tables geomod depends on.
/// </summary>
public sealed class ScriptBuildParityTests
{
    private static readonly DateTime FixedDate = new(2026, 1, 2, 3, 4, 5);

    // ---- Factory mapping -------------------------------------------------------

    [Fact]
    public void Factory_Maps_Settings_And_Document_Context()
    {
        RflFile alpine = LevelTemplates.CreateNewRfl(FixedDate); // v300 → Alpine
        var method = new LightingMethod
        {
            Base = LightingBase.Bounced, Bounces = 2, HighResLightmaps = true, SeamBlend = true, MoverShadows = false,
        };
        var settings = new BuildSettings { UseSharedBsp = false, LightingMethod = method };

        CompileOptions lit = CompileOptionsFactory.Create(alpine, settings,
            new CompileRequest { BakeLighting = true, CastShadows = false });
        Assert.True(lit.Alpine);
        Assert.False(lit.SharedBsp);
        Assert.True(lit.BuildSurfaces);
        Assert.True(lit.FixTJoints);
        Assert.True(lit.BakeLighting);
        Assert.False(lit.HighResLightmaps); // Alpine lighting: the level's highres_lightmaps decides, not the method
        Assert.False(lit.Lighting.CastShadows);
        Assert.False(lit.Lighting.WarnStockLightLimit); // Alpine levels are not held to the stock 64-light cap
        Assert.Equal(2, lit.Lighting.LightBounces);
        Assert.True(lit.Lighting.CrossRoomBlend);
        Assert.False(lit.Lighting.MoverShadows);

        // The Preview-Lighting gate: bake on RED Classic, surface density still follows the method.
        CompileOptions gated = CompileOptionsFactory.Create(alpine, settings,
            new CompileRequest { BakeLighting = true, ApplyLightingMethod = false });
        Assert.Equal(0, gated.Lighting.LightBounces);
        Assert.False(gated.Lighting.CrossRoomBlend);
        Assert.False(gated.HighResLightmaps);

        // Legacy lighting keeps the method option; the level property sets Alpine's high resolution (254-texel cap).
        Ged.Core.Lighting.Alpine.AlpineLightmapSettings.Apply(alpine, Ged.Core.Lighting.Alpine.LightmapPipeline.Legacy, null);
        CompileOptions legacy = CompileOptionsFactory.Create(alpine, settings, new CompileRequest { BakeLighting = true });
        Assert.True(legacy.HighResLightmaps);
        Assert.Equal(255, legacy.HighResFragmentMax);
        Ged.Core.Lighting.Alpine.AlpineLightmapSettings.Apply(alpine, Ged.Core.Lighting.Alpine.LightmapPipeline.Alpine, null);
        Ged.Core.Lighting.Alpine.AlpineLightmapSettings.FindProperties(alpine)!.HighresLightmaps = 1;
        CompileOptions levelHi = CompileOptionsFactory.Create(alpine, settings, new CompileRequest { BakeLighting = true });
        Assert.True(levelHi.HighResLightmaps);
        Assert.Equal(254, levelHi.HighResFragmentMax);
        Ged.Core.Lighting.Alpine.AlpineLightmapSettings.FindProperties(alpine)!.HighresLightmaps = 0;

        // A preview build skips the seal and the surface stage.
        CompileOptions preview = CompileOptionsFactory.Create(alpine, settings, new CompileRequest { Sealed = false });
        Assert.False(preview.BuildSurfaces);
        Assert.False(preview.FixTJoints);

        // A stock (pre-Alpine) level is not an Alpine build and keeps the stock light-limit warning.
        RflFile stock = LevelTemplates.CreateNewRfl(FixedDate);
        stock.Header.Version = 200; // the context derives from the header version
        CompileOptions stockOpts = CompileOptionsFactory.Create(stock, BuildSettings.EditorDefaults, new CompileRequest());
        Assert.False(stockOpts.Alpine);
        Assert.True(stockOpts.SharedBsp);
        Assert.True(stockOpts.Lighting.WarnStockLightLimit);
    }

    [Fact]
    public void Editor_Defaults_Match_A_Fresh_Install()
    {
        BuildSettings d = BuildSettings.EditorDefaults;
        Assert.True(d.UseSharedBsp);
        Assert.NotNull(d.LightingMethod);
        Assert.Equal(LightingBase.RedClassic, d.LightingMethod!.Base);
        Assert.True(d.LightingMethod.MoverShadows); // the editor's default modifier
        Assert.False(d.LightingMethod.HighResLightmaps);

        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        var ctx = new ScriptContext(new ScriptServices { Document = doc }, new ScriptRunOptions(), new ScriptLog());
        BuildSettings used = new CoreScriptOperations(ctx).Settings; // no BuildSettings supplied
        Assert.True(used.UseSharedBsp);
        Assert.Equal(d.LightingMethod.DisplayName(), used.LightingMethod!.DisplayName());
        Assert.Equal(d.LightingMethod.HighResLightmaps, used.LightingMethod.HighResLightmaps);
    }

    // ---- Script path vs GUI path (fast, in-test Alpine level) -----------------

    /// <summary>A small Alpine level: an air room with a geoable detail block on the floor and a light.</summary>
    private static byte[] AlpineRoomLevel()
    {
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        var ed = new BrushEditor(doc);
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 12, Height = 8, Depth = 12, Air = true },
            new Vec3(0, 4, 0), Mat3.Identity);
        // "Is Geoable" is editor state that the save reconciles into the alpine_level_properties table
        // (room UID 0 until a build computes it).
        ed.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 2, Height = 2, Depth = 2, Detail = true, Geoable = true },
            new Vec3(2, 1, 2), Mat3.Identity);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 6, 0));
        return doc.SaveToBytes();
    }

    private static EditorDocument Load(byte[] bytes) => EditorDocument.OpenBytes(bytes, "parity.rfl");

    /// <summary>The script path: a real Lua run of <paramref name="lua"/> through the MoonSharp host.</summary>
    private static EditorDocument BuildViaScript(byte[] level, string lua, BuildSettings? settings)
    {
        EditorDocument doc = Load(level);
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), BuildSettings = settings },
            lua,
            new ScriptRunOptions { ChunkName = "parity", AllowDestructive = true },
            log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return doc;
    }

    /// <summary>The GUI path: the editor controller's recipe — factory options, compile, apply.</summary>
    private static EditorDocument BuildViaFactory(byte[] level, BuildSettings settings, bool bake = false, bool shadows = true)
    {
        EditorDocument doc = Load(level);
        CompileOptions options = CompileOptionsFactory.CreateForDocument(
            doc, vfs: null, settings, new CompileRequest { Sealed = true, BakeLighting = bake, CastShadows = shadows });
        GeometryBuildService.BuildAndApply(doc.Rfl, options);
        doc.MarkDirty();
        return doc;
    }

    private static AlpineLevelPropertiesSection Alpine(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        return doc.Rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
    }

    [Fact]
    public void Script_Build_Matches_Gui_Path_Bytes_And_Recomputes_Alpine_Room_Tables()
    {
        byte[] level = AlpineRoomLevel();
        Assert.Equal(0, Assert.Single(Alpine(Load(level)).GeoableEntries).RoomUid);

        EditorDocument viaScript = BuildViaScript(level, "ops.build()", settings: null); // default = editor defaults
        EditorDocument viaGui = BuildViaFactory(level, BuildSettings.EditorDefaults);

        Assert.Equal(viaGui.SaveToBytes(), viaScript.SaveToBytes());

        // The geoable entry now points at the compiled detail room the brush isolated into.
        AlpineGeoableEntry entry = Assert.Single(Alpine(viaScript).GeoableEntries);
        Geometry g = viaScript.Rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        Assert.NotEqual(0, entry.RoomUid);
        Assert.Contains(g.Rooms, r => r.Id == entry.RoomUid && r.IsSubroom != 0);
    }

    [Fact]
    public void Script_Build_Honors_The_Incremental_Build_Method_Setting()
    {
        byte[] level = AlpineRoomLevel();
        var incremental = new BuildSettings { UseSharedBsp = false };

        EditorDocument viaScript = BuildViaScript(level, "ops.build()", incremental);
        EditorDocument viaGui = BuildViaFactory(level, incremental);

        Assert.Equal(viaGui.SaveToBytes(), viaScript.SaveToBytes());
    }

    [Fact]
    public void Script_Light_Matches_Gui_Path_Bytes()
    {
        byte[] level = AlpineRoomLevel();

        EditorDocument viaScript = BuildViaScript(level, "ops.light(true)", settings: null);
        EditorDocument viaGui = BuildViaFactory(level, BuildSettings.EditorDefaults, bake: true, shadows: true);

        Assert.Equal(viaGui.SaveToBytes(), viaScript.SaveToBytes());
    }

    // ---- Corpus (deep tier) ---------------------------------------------------

    public static IEnumerable<object?[]> CorpusLevels() => Corpus.RflFileNames();

    [Theory]
    [Trait("Category", "DeepGate")] // two sealed compiles per corpus level
    [MemberData(nameof(CorpusLevels))]
    public void Corpus_Script_Build_Matches_Gui_Path_Bytes(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus absent
        }

        byte[] level = File.ReadAllBytes(Path.Combine(Corpus.Directory!, fileName));

        // Poison any Alpine geoable/breakable room UIDs so the test proves the script path recomputes them.
        level = PoisonAlpineRoomUids(level, out bool hasAlpineTables);

        EditorDocument viaScript = BuildViaScript(level, "ops.build()", settings: null);
        EditorDocument viaGui = BuildViaFactory(level, BuildSettings.EditorDefaults);
        Assert.True(viaGui.SaveToBytes().AsSpan().SequenceEqual(viaScript.SaveToBytes()),
            $"{fileName}: script build differs from the GUI-path build");

        if (hasAlpineTables)
        {
            AlpineLevelPropertiesSection alp = Alpine(viaScript);
            Assert.DoesNotContain(alp.GeoableEntries, e => e.RoomUid == PoisonUid);
            Assert.DoesNotContain(alp.BreakableEntries, e => e.RoomUid == PoisonUid);
        }
    }

    private const int PoisonUid = -7777;

    private static byte[] PoisonAlpineRoomUids(byte[] level, out bool hasAlpineTables)
    {
        RflFile rfl = RflFile.Load(level);
        rfl.ParseAllKnownSections();
        RflSection? host = rfl.Sections.FirstOrDefault(s => s.Content is AlpineLevelPropertiesSection);
        if (host?.Content is not AlpineLevelPropertiesSection alp
            || !rfl.Context.IsAlpine
            || (alp.GeoableEntries.Count == 0 && alp.BreakableEntries.Count == 0))
        {
            hasAlpineTables = false;
            return level;
        }

        hasAlpineTables = true;
        alp.GeoableEntries.ForEach(e => e.RoomUid = PoisonUid);
        alp.BreakableEntries.ForEach(e => e.RoomUid = PoisonUid);
        host.Dirty = true;
        return rfl.Save(updateTimestamp: false);
    }
}
