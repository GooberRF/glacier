using System;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Prefabs;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for prefabs: a save → load → place round-trip through the
/// <c>.gedprefab</c> package (a zip of manifest.json + payload.rfg + thumbnail.png)
/// with links remapped onto the placed clones, and a forward-compat manifest read.
/// </summary>
public sealed class PrefabTests : IDisposable
{
    private readonly string _temp;

    public PrefabTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_prefab_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Save_Load_Place_RoundTrips_With_Links_Remapped()
    {
        // Source selection: a brush + two linked events.
        EditorDocument src = NewAlpineDoc();
        var editor = new BrushEditor(src);
        int brushUid = editor.CreateBrush(new BrushCreateParams(), new Vec3(0, 0, 0), Mat3.Identity);
        LevelObject a = src.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(1, 0, 0))!;
        LevelObject b = src.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(2, 0, 0))!;
        ((RflEvent)a.Model).Links.Add(b.Uid);

        RfgFile rfg = RfgInterop.Export(src, new[] { brushUid }, new[] { a.Uid, b.Uid }, alpine: true);
        var manifest = new PrefabManifest
        {
            Name = "Test Widget",
            Description = "two linked events + a brush",
            Author = "Test Author",
            BrushCount = 1,
            ObjectCount = 2,
        };
        byte[] thumb = { 137, 80, 78, 71, 13, 10, 26, 10 }; // a PNG signature stand-in

        // Save the package to disk.
        string path = Path.Combine(_temp, "widget" + PrefabPackage.Extension);
        PrefabPackage.Save(path, manifest, rfg, thumb);
        Assert.True(File.Exists(path));

        // Header-only read (browser listing) skips the payload.
        (PrefabManifest hm, byte[]? ht) = PrefabPackage.LoadHeader(path);
        Assert.Equal("Test Widget", hm.Name);
        Assert.Equal(thumb, ht);

        // Full load + place into a fresh, pre-seeded document.
        PrefabPackage pkg = PrefabPackage.Load(path);
        Assert.Equal(PrefabManifest.CurrentVersion, pkg.Manifest.FormatVersion);
        Assert.Equal(0x12C, pkg.Payload.Version);

        EditorDocument dst = NewAlpineDoc();
        for (int i = 0; i < 8; i++)
        {
            dst.PlaceEvent(EventSchemaCatalog.Find("Delay")!, new Vec3(0, -50, i));
        }

        int firstFresh = dst.Objects.Max(o => o.Uid) + 1;
        var placed = RfgInterop.Import(dst, pkg.Payload, new Vec3(10, 0, 0));

        Assert.Equal(3, placed.Count);
        Assert.All(placed, uid => Assert.True(uid >= firstFresh));

        var imported = dst.Objects.Where(o => o.Kind == LevelObjectKind.Event && placed.Contains(o.Uid)).ToList();
        LevelObject ca = imported.Single(o => ((RflEvent)o.Model).Links.Count > 0);
        LevelObject cb = imported.Single(o => o.Uid != ca.Uid);

        // The link was remapped onto the destination clone of B.
        Assert.Equal(new[] { cb.Uid }, ((RflEvent)ca.Model).Links);
        Assert.Equal(11f, ca.Position.X, 3); // 1 + 10 offset
    }

    [Fact]
    public void Manifest_Is_Forward_Compatible()
    {
        // A manifest written by a hypothetical newer version (extra field + higher
        // FormatVersion) still deserialises the fields this version understands.
        string json =
            "{\"FormatVersion\":99,\"Name\":\"Future\",\"Author\":\"x\",\"SomeNewField\":42," +
            "\"Created\":\"2030-01-02T03:04:05Z\"}";
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PrefabManifest>(json)!;
        Assert.Equal("Future", manifest.Name);
        Assert.Equal(99, manifest.FormatVersion);
        Assert.Equal(2030, manifest.Created.Year);
    }

    private static EditorDocument NewAlpineDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = SaveTargets.AlpineVersion;
        rfl.Header.LevelName = "src.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }
}
