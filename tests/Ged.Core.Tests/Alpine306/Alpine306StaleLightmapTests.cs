using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Alpine306;

/// <summary>
/// The interim Alpine lightmap rule (ALPINE-COMPAT-SPEC §9; <c>AlpineLightmapSavePolicy</c>): once Glacier has
/// rebuilt the geometry or relit the level, a save drops the Alpine lightmap chunk (0x0AFBAE09) — Alpine RED drops a
/// retained chunk that no longer matches (editor_patch/alpine_lightmaps.cpp:1785-1849) — and makes the props bits
/// consistent: <c>d3d11_only_lightmaps</c> bit0 cleared (the stock section is written), <c>require_d3d11</c> bit0
/// recomputed; the mapper's setting bits are kept. An untouched level re-saves byte for byte.
/// </summary>
public sealed class Alpine306StaleLightmapTests
{
    private const byte Setting = 2;
    private const byte Needed = 1;

    [Fact]
    public void A_No_Op_Save_Keeps_The_Alpine_Lightmaps_Byte_Identically()
    {
        byte[] original = AlpineLitLevel(d3d11Only: false);
        EditorDocument doc = Load(original);
        Assert.Equal(original, SaveAndRead(doc, out IReadOnlyList<string> notes));
        Assert.Empty(notes);
    }

    [Fact]
    public void A_Rebuild_Then_Save_Drops_The_Alpine_Lightmaps_And_Fixes_The_Bits()
    {
        EditorDocument doc = Load(AlpineLitLevel(d3d11Only: false, require: Setting | Needed));
        GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc, bake: false));

        RflFile saved = RflFile.Load(SaveAndRead(doc, out IReadOnlyList<string> notes));
        saved.ParseAllKnownSections();
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        Assert.Contains(saved.Sections, s => s.TypeId == (uint)SectionType.Lightmaps);
        Assert.Equal(Setting, Props(saved).D3D11OnlyLightmaps); // the setting is kept, "stock omitted" is not
        Assert.Equal(Setting | Needed, Props(saved).RequireD3D11); // still needed: the mapper's Require setting
        string note = Assert.Single(notes, n => n.StartsWith("Dropped the level's Alpine lightmaps", StringComparison.Ordinal));
        Assert.Contains("rebuilt the geometry", note, StringComparison.Ordinal);
        Assert.Contains("re-bake the level in Alpine RED", note, StringComparison.Ordinal);
        Assert.Contains(notes, n => n.Contains("D3D11-only lightmaps", StringComparison.Ordinal));

        // The rule is idempotent: the next save has nothing left to drop or fix.
        Assert.Empty(saved.UpgradeToAlpine());
    }

    [Fact]
    public void A_Relight_Alone_Behaves_Like_A_Rebuild()
    {
        byte[] original = AlpineLitLevel(d3d11Only: false);
        EditorDocument doc = Load(original);
        byte[] geometryBefore = Section(RflFile.Load(original), SectionType.StaticGeometry);

        BakeStats? stats = GeometryBuildService.Relight(doc.Rfl, Array.Empty<Light>(), new RfColor(64, 64, 64, 255), new LightingOptions());
        Assert.NotNull(stats);

        RflFile saved = RflFile.Load(SaveAndRead(doc, out IReadOnlyList<string> notes));
        saved.ParseAllKnownSections();
        Assert.Equal(geometryBefore, Section(saved, SectionType.StaticGeometry)); // relit, not rebuilt
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        Assert.Equal(Setting, Props(saved).D3D11OnlyLightmaps);
        Assert.Equal(0, Props(saved).RequireD3D11);
        Assert.Contains(notes, n => n.Contains("recalculated the lighting", StringComparison.Ordinal));
    }

    [Fact]
    public void A_D3D11_Only_Level_Gets_Stock_Lightmaps_From_A_Rebuild_And_Stops_Claiming_It_Omits_Them()
    {
        EditorDocument doc = Load(AlpineLitLevel(d3d11Only: true));
        Assert.DoesNotContain(doc.Rfl.Sections, s => s.TypeId == (uint)SectionType.Lightmaps);
        Assert.Equal(Setting | 1, Props(doc.Rfl).D3D11OnlyLightmaps);

        // No stock pages to relight in place: Calculate Lighting runs a full build with lighting instead.
        Assert.Null(GeometryBuildService.Relight(doc.Rfl, Array.Empty<Light>(), null, new LightingOptions()));
        GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc, bake: true));

        RflFile saved = RflFile.Load(SaveAndRead(doc, out IReadOnlyList<string> notes));
        saved.ParseAllKnownSections();
        Assert.DoesNotContain(saved.Sections, s => s.TypeId == (uint)SectionType.AlpineLightmaps);
        List<uint> ids = saved.Sections.Select(s => s.TypeId).ToList();
        int lightmaps = ids.IndexOf((uint)SectionType.Lightmaps);
        Assert.True(lightmaps >= 0 && lightmaps < ids.IndexOf((uint)SectionType.StaticGeometry));
        int pages = saved.Sections.Select(s => s.Content).OfType<LightmapsSection>().Single().Lightmaps.Count;
        Geometry geometry = saved.Sections.Select(s => s.Content).OfType<GeometrySection>().Single().Geometry;
        Assert.NotEmpty(geometry.Surfaces);
        Assert.All(geometry.Surfaces, s => Assert.InRange(s.LightmapIndex, 0, pages - 1));
        Assert.Equal(Setting, Props(saved).D3D11OnlyLightmaps);
        Assert.Equal(0, Props(saved).RequireD3D11); // it was only needed for the omitted stock lightmaps
        Assert.Contains(notes, n => n.Contains("stock lightmaps Glacier wrote", StringComparison.Ordinal));
    }

    [Fact]
    public void Require_D3D11_Stays_Needed_For_A_Mesh_Draw_Scale()
    {
        EditorDocument doc = Load(AlpineLitLevel(d3d11Only: true, meshDrawScale: 2f));
        GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc, bake: false));
        RflFile saved = RflFile.Load(SaveAndRead(doc, out _));
        saved.ParseAllKnownSections();
        Assert.Equal(Needed, Props(saved).RequireD3D11); // level.cpp:1150-1153
        Assert.Equal(Setting, Props(saved).D3D11OnlyLightmaps);
    }

    /// <summary>
    /// A built, lit v306 level carrying an Alpine lightmap chunk and the props bits Alpine RED writes with it; with
    /// <paramref name="d3d11Only"/> it is in the D3D11-only state (no 0x1200, every surface's lightmap index −1).
    /// </summary>
    private static byte[] AlpineLitLevel(bool d3d11Only, byte? require = null, float? meshDrawScale = null)
    {
        KitTestHarness.KitRun run = KitTestHarness.Run("kit.room{ name = 'hall', pos = {0, 0, 0}, size = {8, 4, 8} }");
        EditorDocument doc = run.Doc;
        GeometryBuildService.BuildAndApply(doc.Rfl, Options(doc, bake: true));
        RflFile rfl = doc.Rfl;

        rfl.InsertSection(new RflSection((uint)SectionType.AlpineLightmaps, Alpine306Synthetic.LightmapSection()));
        AlpineLevelPropertiesSection props = Props(rfl);
        props.D3D11OnlyLightmaps = (byte)(Setting | (d3d11Only ? 1 : 0));
        props.RequireD3D11 = require ?? (byte)(d3d11Only ? Needed : 0);
        rfl.Sections.Single(s => s.Content == props).Dirty = true;

        if (meshDrawScale is float scale)
        {
            var meshes = new AlpineMeshObjectsSection();
            meshes.Meshes.Add(new AlpineMeshObject
            {
                Uid = 7001, Position = new Vec3(0, 1, 0), Orientation = Mat3.Identity, ScriptName = "statue",
                MeshFilename = "statue.v3m", DrawScale = scale,
            });
            rfl.InsertSection(new RflSection((uint)SectionType.AlpineMeshObjects, Array.Empty<byte>()) { Content = meshes, Dirty = true });
        }

        if (d3d11Only)
        {
            rfl.Sections.RemoveAll(s => s.TypeId == (uint)SectionType.Lightmaps);
            RflSection geo = rfl.Sections.Single(s => s.TypeId == (uint)SectionType.StaticGeometry);
            foreach (Surface s in ((GeometrySection)geo.Content!).Geometry.Surfaces)
            {
                s.LightmapIndex = -1;
            }

            geo.Dirty = true;
        }

        return rfl.Save(updateTimestamp: false);
    }

    private static CompileOptions Options(EditorDocument doc, bool bake) =>
        CompileOptionsFactory.CreateForDocument(doc, vfs: null, BuildSettings.EditorDefaults,
            new CompileRequest { Sealed = true, BakeLighting = bake, CastShadows = false });

    private static EditorDocument Load(byte[] bytes) => new(RflFile.Load(bytes), "alpine-lit.rfl");

    /// <summary>Saves through the editor's save path (no timestamp) and returns the written bytes.</summary>
    private static byte[] SaveAndRead(EditorDocument doc, out IReadOnlyList<string> notes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ged-stale-lm-{Guid.NewGuid():N}.rfl");
        try
        {
            doc.Save(path, updateTimestamp: false);
            notes = doc.LastSaveNotes;
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AlpineLevelPropertiesSection Props(RflFile rfl)
    {
        rfl.ParseAllKnownSections();
        return rfl.Sections.Select(s => s.Content).OfType<AlpineLevelPropertiesSection>().Single();
    }

    private static byte[] Section(RflFile rfl, SectionType type) =>
        rfl.Sections.Single(s => s.TypeId == (uint)type).GetBodyBytes(rfl.Context);
}
