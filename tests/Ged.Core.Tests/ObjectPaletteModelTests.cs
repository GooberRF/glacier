using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Regression coverage for the object palette's class-bearing rows (entity / clutter /
/// item). The dropdowns are backed by the <c>.tbl</c> catalogs, which are empty until an
/// RF install is mounted; the shell must refresh the palette when that happens. These
/// tests exercise the headless <see cref="ObjectPaletteModel"/> behind the combos, which
/// previously had no refresh contract — so the dropdowns stayed empty after a late mount.
/// </summary>
public class ObjectPaletteModelTests
{
    [Fact]
    public void New_Model_Has_No_Classes_And_Does_Not_Throw()
    {
        var model = new ObjectPaletteModel();

        // No install mounted yet: every class-bearing kind is empty, and asking for a
        // placement class must be safe (null), never throw.
        Assert.False(model.HasAnyClasses);
        Assert.Empty(model.ClassesFor(LevelObjectKind.Entity));
        Assert.Null(model.PlacementClass(LevelObjectKind.Entity));
        Assert.Null(model.PlacementClass(LevelObjectKind.Clutter));
        Assert.Null(model.PlacementClass(LevelObjectKind.Item));

        // A non-class kind never carries a placement class.
        Assert.Null(model.PlacementClass(LevelObjectKind.Light));
    }

    [Fact]
    public void Refresh_Populates_Classes_And_Auto_Selects_First()
    {
        var model = new ObjectPaletteModel();
        var catalog = new Dictionary<LevelObjectKind, IReadOnlyList<string>>
        {
            [LevelObjectKind.Entity] = new[] { "Guard", "Miner", "Sniper" },
            [LevelObjectKind.Clutter] = new[] { "officebookcase" },
            [LevelObjectKind.Item] = new[] { "First_Aid", "Medical_Kit" },
        };

        // This models what the shell does on mount: hand the catalog class names to the
        // palette. Before this call the dropdowns are empty — the exact broken state.
        model.RefreshClasses(k => catalog.TryGetValue(k, out IReadOnlyList<string>? v) ? v : null);

        Assert.True(model.HasAnyClasses);
        Assert.Equal(new[] { "Guard", "Miner", "Sniper" }, model.ClassesFor(LevelObjectKind.Entity));
        Assert.Equal("Guard", model.Selected(LevelObjectKind.Entity));
        Assert.Equal("First_Aid", model.Selected(LevelObjectKind.Item));
        Assert.Equal("Guard", model.PlacementClass(LevelObjectKind.Entity));
    }

    [Fact]
    public void Refresh_Preserves_A_Still_Valid_Selection()
    {
        var model = new ObjectPaletteModel();
        model.RefreshClasses(_ => new[] { "Guard", "Miner", "Sniper" });
        model.Select(LevelObjectKind.Entity, "Sniper");

        // A second refresh (e.g. tables reloaded) must not clobber the user's choice.
        model.RefreshClasses(_ => new[] { "Guard", "Miner", "Sniper", "Elite" });
        Assert.Equal("Sniper", model.Selected(LevelObjectKind.Entity));

        // But a selection that vanished from the catalog falls back to the first entry.
        model.RefreshClasses(_ => new[] { "Guard", "Miner" });
        Assert.Equal("Guard", model.Selected(LevelObjectKind.Entity));
    }

    [Fact]
    public void Selected_Class_Drives_Placement_Into_A_Document()
    {
        // The pending-placement class the palette holds must be the class that ends up on
        // the placed object (the "selecting a class then Place produces that object" flow).
        var model = new ObjectPaletteModel();
        model.RefreshClasses(_ => new[] { "Guard", "Miner", "Sniper" });
        model.Select(LevelObjectKind.Entity, "Sniper");

        EditorDocument doc = NewDocument();
        LevelObject? placed = doc.PlaceObject(
            LevelObjectKind.Entity, new Vec3(1, 2, 3), model.PlacementClass(LevelObjectKind.Entity));

        Assert.NotNull(placed);
        Assert.Equal(LevelObjectKind.Entity, placed!.Kind);
        Assert.Equal("Sniper", ((Entity)placed.Model).ClassName);
    }

    [Fact]
    public void Real_Install_Populates_Entity_Clutter_Item_And_Resolves_Meshes_Without_Throwing()
    {
        if (!TestPaths.HasRfInstall)
        {
            return; // needs a mounted RF install (entity.tbl / clutter.tbl / items.tbl)
        }

        using AssetVfs vfs = GameMount.Mount(TestPaths.RfInstall!);
        var entities = EntityCatalog.Load(vfs.ReadFile("entity.tbl")!);
        var clutter = ClutterCatalog.Load(vfs.ReadFile("clutter.tbl")!);
        var items = ItemCatalog.Load(vfs.ReadFile("items.tbl")!);

        IReadOnlyList<string> NamesFor(LevelObjectKind kind) => kind switch
        {
            LevelObjectKind.Entity => entities.Entities.Select(e => e.Name).OrderBy(n => n).ToList(),
            LevelObjectKind.Clutter => clutter.Clutters.Select(c => c.ClassName).OrderBy(n => n).ToList(),
            LevelObjectKind.Item => items.Items.Select(i => i.ClassName).OrderBy(n => n).ToList(),
            _ => Array.Empty<string>(),
        };

        var model = new ObjectPaletteModel();
        model.RefreshClasses(NamesFor);

        // The regression symptom: these came up empty after a mount. Assert they populate.
        Assert.NotEmpty(model.ClassesFor(LevelObjectKind.Entity));
        Assert.NotEmpty(model.ClassesFor(LevelObjectKind.Clutter));
        Assert.NotEmpty(model.ClassesFor(LevelObjectKind.Item));
        Assert.NotNull(model.Selected(LevelObjectKind.Entity));

        // Selecting each class and resolving its mesh (the thumbnail row's first step) must
        // never throw for any class in the real catalogs — a fault here must stay cosmetic.
        foreach (string name in model.ClassesFor(LevelObjectKind.Entity))
        {
            _ = entities.Find(name)?.V3dFilename; // resolution only; safe, no exception
        }

        // A real selection places a real object of that class.
        string chosen = model.ClassesFor(LevelObjectKind.Entity).First();
        model.Select(LevelObjectKind.Entity, chosen);
        EditorDocument doc = NewDocument();
        LevelObject? placed = doc.PlaceObject(LevelObjectKind.Entity, Vec3.Zero, model.PlacementClass(LevelObjectKind.Entity));
        Assert.NotNull(placed);
        Assert.Equal(chosen, ((Entity)placed!.Model).ClassName);
    }

    /// <summary>An empty in-memory level (mirrors EditorSession.NewLevel) for placement.</summary>
    private static EditorDocument NewDocument()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12C;
        rfl.Header.LevelName = "untitled.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }
}
