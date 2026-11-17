using System;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.IO.Vpp;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Ged.Scripting;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The packfile must never ship a stale level, and a stale packfile must be called out. The owner packed a level,
/// then added a trigger and saved: the game kept loading the packfile's copy (Red Faction resolves a level from a
/// packfile before the loose file — <see cref="StalePackfileCheck"/> cites RF.exe / Alpine), so the trigger was
/// missing in game. These tests pin: the default packfile goes next to the level and is replaced in place; Lua
/// <c>ops.package</c> saves unsaved changes first and packs the saved file's own bytes; and a save after packing
/// notes the now-stale packfile until it is rebuilt.
/// </summary>
public sealed class PackfileFreshnessTests : IDisposable
{
    private static readonly DateTime FixedDate = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ged_packfresh_" + Guid.NewGuid().ToString("N"));

    public PackfileFreshnessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    [Fact]
    public void The_Default_Packfile_Is_The_Level_Name_In_The_Level_Folder()
    {
        string level = Path.Combine(_dir, "maps", "dm_owner.rfl");
        Assert.Equal(Path.Combine(_dir, "maps", "dm_owner.vpp"), PackfileBuildPlan.LevelFolderOutputPath(level));
    }

    [Fact]
    public void Building_Over_An_Existing_Packfile_Replaces_It_In_Place()
    {
        string vpp = Path.Combine(_dir, "g.vpp");
        PackfileBuilder.Build(new byte[] { 1, 2, 3 }, "g.rfl", Array.Empty<PackDependency>(), vpp);
        PackfileBuilder.Build(new byte[] { 4, 5, 6, 7 }, "g.rfl", Array.Empty<PackDependency>(), vpp);

        using VppArchive archive = VppArchive.Open(vpp);
        Assert.Single(archive.Entries);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, archive.Read("g.rfl"));
        Assert.False(File.Exists(vpp + ".tmp"));
    }

    [Fact]
    public void A_Save_Notes_An_Older_Packfile_That_Still_Holds_Another_Copy_Of_The_Level()
    {
        string level = Path.Combine(_dir, "g.rfl");
        string vpp = Path.Combine(_dir, "g.vpp");
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        doc.SaveAs(level);
        Assert.Null(doc.LastStalePackfileNote); // no packfile yet

        // Packed from this very save: same bytes, so not stale even when its timestamp is older.
        PackfileBuilder.Build(File.ReadAllBytes(level), "g.rfl", Array.Empty<PackDependency>(), vpp);
        File.SetLastWriteTimeUtc(vpp, File.GetLastWriteTimeUtc(level).AddMinutes(-1));
        Assert.Empty(StalePackfileCheck.Find(level));

        // The owner's sequence: add a trigger and save — the packfile now holds the old level.
        doc.PlaceObject(Ged.Core.Editor.LevelObjectKind.Trigger, new Vec3(0, 1, 0));
        doc.Save(level);
        File.SetLastWriteTimeUtc(vpp, File.GetLastWriteTimeUtc(level).AddMinutes(-1));
        Assert.Equal(new[] { Path.GetFullPath(vpp) }, StalePackfileCheck.Find(level));
        doc.Save(level);
        Assert.NotNull(doc.LastStalePackfileNote);
        Assert.Contains(doc.LastStalePackfileNote, doc.LastSaveNotes);
        Assert.Contains("g.vpp", doc.LastStalePackfileNote, StringComparison.Ordinal);
        Assert.Contains("g.rfl", doc.LastStalePackfileNote, StringComparison.Ordinal);

        // A packfile newer than the level, or one without the level in it, is not stale.
        File.SetLastWriteTimeUtc(vpp, File.GetLastWriteTimeUtc(level).AddMinutes(1));
        Assert.Empty(StalePackfileCheck.Find(level));
        PackfileBuilder.Build(new byte[] { 9 }, "other.rfl", Array.Empty<PackDependency>(), vpp);
        File.SetLastWriteTimeUtc(vpp, File.GetLastWriteTimeUtc(level).AddMinutes(-1));
        Assert.Empty(StalePackfileCheck.Find(level));
    }

    [Fact]
    public void Lua_Package_Saves_First_And_Packs_The_Saved_File_Next_To_The_Level()
    {
        string level = Path.Combine(_dir, "g.rfl");
        string vpp = Path.Combine(_dir, "g.vpp");
        EditorDocument doc = LevelTemplates.CreateNew(FixedDate);
        doc.SaveAs(level);
        using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(_dir) });

        Run(doc, vfs, "ops.package()");
        Assert.True(File.Exists(vpp));
        AssertPackedIsSavedFile(level, vpp);

        // Unsaved change (the trigger), then package: the level is saved first and the pack carries the trigger.
        LevelObject trigger = doc.PlaceObject(Ged.Core.Editor.LevelObjectKind.Trigger, new Vec3(0, 1, 0))!;
        Assert.True(doc.IsDirty);
        ScriptLog log = Run(doc, vfs, "ops.package()");
        Assert.False(doc.IsDirty);
        AssertPackedIsSavedFile(level, vpp);
        Assert.NotNull(EditorDocument.Open(level).FindByUid(trigger.Uid));
        Assert.Contains(log.Entries, e => e.Message.Contains("before packaging", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("older copy", StringComparison.Ordinal));
        Assert.Empty(StalePackfileCheck.Find(level));
    }

    private static void AssertPackedIsSavedFile(string level, string vpp)
    {
        using VppArchive archive = VppArchive.Open(vpp);
        Assert.Equal(Path.GetFileName(level), archive.Entries[0].Name);
        Assert.Equal(File.ReadAllBytes(level), archive.Read(Path.GetFileName(level)));
    }

    private static ScriptLog Run(EditorDocument doc, AssetVfs vfs, string source)
    {
        var log = new ScriptLog();
        ScriptRunResult r = ScriptTestFixture.Runner().Run(
            new ScriptServices { Document = doc, Confirmation = new AllowAllConfirmation(), Assets = vfs },
            source,
            new ScriptRunOptions { ChunkName = "package", AllowDestructive = true },
            log);
        Assert.True(r.Success, r.Error?.ToDisplayString());
        return log;
    }
}
