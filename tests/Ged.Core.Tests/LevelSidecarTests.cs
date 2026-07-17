using System;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Graph;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Lighting;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The unified .gedlayout.json sidecar (features 1 + 4): graph node positions, the
/// per-level lightmap method and the measurement annotations round-trip, and each block's
/// read-modify-write writer preserves the OTHER blocks so writers never clobber one
/// another. Plus the document-level annotation CRUD + undo.
/// </summary>
public sealed class LevelSidecarTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ged_sidecar_{Guid.NewGuid():N}.gedlayout.json");

    [Fact]
    public void Full_Sidecar_Round_Trips_Through_Serialize()
    {
        var s = new LevelSidecar { Version = 1 };
        s.Graph.Set(7, 1.5, -2.25);
        s.Lighting = new LightingMethod { Base = LightingBase.Bounced, Bounces = 2, AmbientOcclusion = true, SoftShadows = true, CornerLeakFix = true, SmoothGutters = true, MoverShadows = false };
        s.Annotations.Add(new Annotation { Id = 3, A = new Vec3(0, 0, 0), B = new Vec3(3, 4, 0), Label = "hall" });

        LevelSidecar back = LevelSidecarStore.Deserialize(LevelSidecarStore.Serialize(s));

        Assert.True(back.Graph.TryGet(7, out double x, out double y));
        Assert.Equal(1.5, x, 6);
        Assert.Equal(-2.25, y, 6);
        Assert.NotNull(back.Lighting);
        Assert.Equal(LightingBase.Bounced, back.Lighting!.Base);
        Assert.Equal(2, back.Lighting.Bounces);
        Assert.True(back.Lighting.AmbientOcclusion);
        Assert.True(back.Lighting.SoftShadows);
        Assert.True(back.Lighting.CornerLeakFix);
        Assert.True(back.Lighting.SmoothGutters);
        Assert.False(back.Lighting.MoverShadows); // explicit OFF round-trips (default is ON)
        Assert.Single(back.Annotations);
        Assert.Equal("hall", back.Annotations[0].Label);
        Assert.Equal(5f, back.Annotations[0].Distance, 4); // 3-4-5
    }

    /// <summary>A sidecar written before the "Movers cast shadows" option (no "moverShadows" key) loads
    /// with the option ON — the app default — never a spurious OFF.</summary>
    [Fact]
    public void Legacy_Sidecar_Without_MoverShadows_Defaults_On()
    {
        const string legacy = "{\"version\":1,\"lighting\":{\"method\":\"RedClassic\",\"cornerLeakFix\":true}}";
        LevelSidecar back = LevelSidecarStore.Deserialize(legacy);
        Assert.NotNull(back.Lighting);
        Assert.True(back.Lighting!.CornerLeakFix);
        Assert.True(back.Lighting.MoverShadows);
    }

    [Fact]
    public void Saving_Graph_Preserves_Lighting_And_Annotations()
    {
        string path = TempPath();
        try
        {
            LevelSidecarStore.SaveLighting(path, new LightingMethod { Base = LightingBase.Bounced, Bounces = 1 });
            LevelSidecarStore.SaveAnnotations(path, new[] { new Annotation { Id = 1, A = Vec3.Zero, B = new Vec3(1, 0, 0) } });

            // A graph-only write must NOT drop the lighting / annotation blocks.
            var g = new GraphLayout();
            g.Set(42, 10, 20);
            LevelSidecarStore.SaveGraph(path, g);

            LevelSidecar back = LevelSidecarStore.Load(path);
            Assert.True(back.Graph.Has(42));
            Assert.NotNull(back.Lighting);
            Assert.Equal(LightingBase.Bounced, back.Lighting!.Base);
            Assert.Single(back.Annotations);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Saving_Lighting_Preserves_Graph_And_Annotations()
    {
        string path = TempPath();
        try
        {
            var g = new GraphLayout();
            g.Set(5, 1, 2);
            LevelSidecarStore.SaveGraph(path, g);
            LevelSidecarStore.SaveAnnotations(path, new[] { new Annotation { Id = 9, A = Vec3.Zero, B = new Vec3(0, 2, 0) } });

            LevelSidecarStore.SaveLighting(path, new LightingMethod { Base = LightingBase.RedClassic, AmbientOcclusion = true });

            LevelSidecar back = LevelSidecarStore.Load(path);
            Assert.True(back.Graph.Has(5));
            Assert.Single(back.Annotations);
            Assert.True(back.Lighting!.AmbientOcclusion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_Sidecar_Loads_Empty()
    {
        LevelSidecar s = LevelSidecarStore.Load(TempPath());
        Assert.Equal(0, s.Graph.Count);
        Assert.Null(s.Lighting);
        Assert.Empty(s.Annotations);
    }

    [Fact]
    public void Legacy_GraphLayoutStore_Still_Round_Trips_And_Shares_The_Path()
    {
        Assert.EndsWith(".gedlayout.json", GraphLayoutStore.SidecarPathFor(Path.Combine("maps", "dm01.rfl")));

        string path = TempPath();
        try
        {
            var g = new GraphLayout();
            g.Set(1, 3, 4);
            GraphLayoutStore.Save(g, path);
            GraphLayout back = GraphLayoutStore.Load(path);
            Assert.True(back.TryGet(1, out double x, out double y));
            Assert.Equal(3, x, 6);
            Assert.Equal(4, y, 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Document annotation CRUD + undo ----

    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    [Fact]
    public void Annotation_Add_Remove_Is_Undoable()
    {
        EditorDocument doc = NewDoc();
        Annotation a = doc.AddAnnotation(new Vec3(0, 0, 0), new Vec3(3, 0, 4));
        Assert.Single(doc.Annotations);
        Assert.Equal(5f, a.Distance, 4);

        doc.Undo.Undo();
        Assert.Empty(doc.Annotations);
        doc.Undo.Redo();
        Assert.Single(doc.Annotations);

        doc.RemoveAnnotation(a.Id);
        Assert.Empty(doc.Annotations);
        doc.Undo.Undo();
        Assert.Single(doc.Annotations);
        Assert.Equal(a.Id, doc.Annotations[0].Id);
    }

    [Fact]
    public void SetAnnotations_Loads_And_Reseeds_Ids()
    {
        EditorDocument doc = NewDoc();
        doc.SetAnnotations(new[]
        {
            new Annotation { Id = 4, A = Vec3.Zero, B = new Vec3(1, 0, 0) },
            new Annotation { Id = 9, A = Vec3.Zero, B = new Vec3(2, 0, 0) },
        });
        Assert.Equal(2, doc.Annotations.Count);

        // The next created annotation gets an id above the loaded max (no collision).
        Annotation next = doc.AddAnnotation(Vec3.Zero, new Vec3(0, 1, 0));
        Assert.True(next.Id > 9);
        Assert.Equal(3, doc.Annotations.Count);
    }
}
