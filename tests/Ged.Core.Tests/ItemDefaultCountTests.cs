using System;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Ged.Core.Tables;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// A newly placed item holds its class's <c>items.tbl</c> Count — what RED writes and what the stock
/// levels carry (an Assault Rifle pickup 42 rounds, a Medical Kit 25) — through the one placement
/// path the editor palette and scripts share; without the table the count stays 1 (scripts say so),
/// and an explicit count from a script always wins.
/// </summary>
public sealed class ItemDefaultCountTests
{
    private static readonly GameCatalogs Tables = new()
    {
        Items = ItemCatalog.Load("#Items\n$Class Name: \"Assault Rifle\"\n$Count: 42\n\n$Class Name: \"Medical Kit\"\n$Count: 25\n\n$Class Name: \"keycard\"\n#End\n"),
    };

    private static int CountOf(EditorDocument doc, int uid) => ((Item)doc.FindByUid(uid)!.Model).Count;

    [Fact]
    public void Editor_Placement_Uses_The_Hosts_Table_Count()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        Assert.Equal(1, CountOf(doc, doc.PlaceObject(LevelObjectKind.Item, Vec3.Zero, "Assault Rifle")!.Uid)); // no table: blueprint 1

        doc.ItemDefaultCount = Tables.ItemCount; // what the editor session installs when an RF install is mounted
        Assert.Equal(42, CountOf(doc, doc.PlaceObject(LevelObjectKind.Item, Vec3.Zero, "Assault Rifle")!.Uid));
        Assert.Equal(25, CountOf(doc, doc.PlaceObject(LevelObjectKind.Item, Vec3.Zero, "medical kit")!.Uid));
        Assert.Equal(1, CountOf(doc, doc.PlaceObject(LevelObjectKind.Item, Vec3.Zero, "keycard")!.Uid)); // class without a count
    }

    [Fact]
    public void Script_Placement_Uses_The_Table_Count_And_An_Explicit_Count_Wins()
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(doc,
            "local a = level.place{kind = 'item', class = 'Assault Rifle', pos = {0, 0.5, 0}}\n" +
            "local b = level.place{kind = 'item', class = 'Medical Kit', pos = {1, 0.5, 0}, props = {count = 5}}\n" +
            "local c = level.place('Item', 2, 0.5, 0, 'Medical Kit')\n" +
            "return a:get('count') .. ',' .. b:get('count') .. ',' .. c:get('count')",
            services: d => new ScriptServices { Document = d, Confirmation = new AllowAllConfirmation(), Catalogs = Tables });
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("42,5,25", r.ReturnValue);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("no items.tbl count", StringComparison.Ordinal));

        // Without tables the count is 1 and the log says why, once.
        (ScriptRunResult bare, ScriptLog bareLog) = ScriptTestFixture.Run(LevelTemplates.CreateNew(KitTestHarness.FixedDate),
            "return level.place{kind = 'item', class = 'Shotgun', pos = {0, 0.5, 0}}:get('count') .. ',' .. level.place{kind = 'item', class = 'Shotgun', pos = {1, 0.5, 0}}:get('count')");
        Assert.True(bare.Success, bare.Error?.ToDisplayString());
        Assert.Equal("1,1", bare.ReturnValue);
        Assert.Single(bareLog.Entries, e => e.Message.Contains("no items.tbl count for 'Shotgun'", StringComparison.Ordinal));
    }

    [Fact]
    public void Stock_Item_Counts_Match_The_Installed_Table()
    {
        if (TestPaths.RfInstall is not { } install)
        {
            return; // no RF install (GED_RF_DIR / research/rf-dirs.txt): skip
        }

        using AssetVfs vfs = GameMount.Mount(install);
        GameCatalogs tables = GameCatalogs.Load(vfs);
        // The counts RED's stock multiplayer levels carry for these pickups.
        foreach ((string cls, int count) in new[]
                 {
                     ("Assault Rifle", 42), ("Shotgun", 8), ("Machine Pistol", 32), ("Medical Kit", 25), ("Suit Repair", 25),
                     ("grenades", 3), ("rocket launcher", 6), ("rail gun", 8), ("flamethrower", 100), ("5.56mm_ammo", 42),
                     ("10gauge_ammo", 8), ("Multi Super Armor", 10), ("flag_red", 1), ("base_blue", 1),
                 })
        {
            Assert.Equal(count, tables.ItemCount(cls));
        }
    }
}
