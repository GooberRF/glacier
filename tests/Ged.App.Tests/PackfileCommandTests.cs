using System;
using System.IO;
using System.Threading.Tasks;
using Ged.App;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.IO.Vpp;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// File ▸ Create Level Packfile, end to end without UI (<see cref="PackfileCommand"/>, the code the menu commands run):
/// no directory question — it writes <c>&lt;level folder&gt;\&lt;level&gt;.vpp</c>, replacing an older one in place; it
/// packs the level as it is now (unsaved changes are saved first through the editor save, and the packfile carries the
/// saved file's bytes); a never-saved level is refused; Create Level Packfile As… always opens the review dialog.
/// </summary>
public sealed class PackfileCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ged_packcmd_" + Guid.NewGuid().ToString("N"));

    public PackfileCommandTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>A library folder holding every file the level references (so nothing is missing and no review opens).</summary>
    private string Library(EditorDocument doc)
    {
        string lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(lib);
        using var empty = new AssetVfs(Array.Empty<IAssetSource>());
        foreach (PackDependency missing in DependencyScanner.Scan(doc.Rfl, new VfsDependencyResolver(empty)).Missing)
        {
            File.WriteAllBytes(Path.Combine(lib, missing.FileName), new byte[] { 1, 2, 3 });
        }

        return lib;
    }

    private sealed class Harness
    {
        public string? Library;
        public int Saves;
        public int Reviews;
        public bool SaveSucceeds = true;
        public PackfileBuildPlan? ReviewedPlan;
        public byte[]? ReviewedBytes;

        public async Task<PackfileCommandOutcome> Run(EditorDocument doc, bool review, bool preview = false)
        {
            using var vfs = new AssetVfs(Library is null ? Array.Empty<IAssetSource>() : new IAssetSource[] { new DirectoryAssetSource(Library) });
            return await PackfileCommand.RunAsync(
                doc,
                preview,
                review,
                path =>
                {
                    Saves++;
                    if (SaveSucceeds)
                    {
                        doc.Save(path);
                    }

                    return Task.FromResult(SaveSucceeds);
                },
                () => Task.FromResult<DependencyScanResult?>(DependencyScanner.Scan(doc.Rfl, new VfsDependencyResolver(vfs))),
                scan => new PackfileBuildPlan(scan, Path.GetFileName(doc.Path!), PackfileBuildPlan.LevelFolderOutputPath(doc.Path!)),
                (plan, bytes) =>
                {
                    Reviews++;
                    ReviewedPlan = plan;
                    ReviewedBytes = bytes;
                    return Task.FromResult<PackfileBuildResult?>(null);
                });
        }
    }

    [Fact]
    public async Task Create_Packfile_Saves_First_And_Writes_The_Level_Folder_Packfile_In_Place()
    {
        string level = Path.Combine(_dir, "ownerG.rfl");
        string vpp = Path.Combine(_dir, "ownerG.vpp");
        EditorDocument doc = LevelTemplates.CreateNew();
        doc.SaveAs(level);
        File.WriteAllBytes(vpp, new byte[] { 0xDE, 0xAD }); // an old packfile of that name: replaced in place
        var h = new Harness { Library = Library(doc) };

        // Clean level: no save, packed straight to the level folder (no dialog, no path question).
        PackfileCommandOutcome first = await h.Run(doc, review: false);
        Assert.Null(first.Refusal);
        Assert.False(first.SavedFirst);
        Assert.False(first.Reviewed);
        Assert.Equal(Path.GetFullPath(vpp), first.Result!.OutputPath);
        Assert.Equal(0, h.Saves);
        AssertPackedIsSavedFile(level, vpp);

        // The owner's sequence: add a trigger (unsaved), Create Packfile — the level is saved first and packed fresh.
        LevelObject trigger = doc.PlaceObject(LevelObjectKind.Trigger, new Vec3(0, 1, 0))!;
        PackfileCommandOutcome second = await h.Run(doc, review: false);
        Assert.True(second.SavedFirst);
        Assert.Equal(1, h.Saves);
        Assert.False(doc.IsDirty);
        AssertPackedIsSavedFile(level, vpp);
        Assert.NotNull(EditorDocument.OpenBytes(ReadPacked(vpp, "ownerG.rfl")).FindByUid(trigger.Uid));
    }

    [Fact]
    public async Task Unsaved_Preview_Geometry_Is_Saved_Before_Packing()
    {
        string level = Path.Combine(_dir, "p.rfl");
        EditorDocument doc = LevelTemplates.CreateNew();
        doc.SaveAs(level);
        var h = new Harness { Library = Library(doc) };
        PackfileCommandOutcome outcome = await h.Run(doc, review: false, preview: true);
        Assert.True(outcome.SavedFirst);
        Assert.Equal(1, h.Saves);
    }

    [Fact]
    public async Task A_Never_Saved_Level_Is_Refused_And_A_Failed_Save_Writes_Nothing()
    {
        EditorDocument unsaved = LevelTemplates.CreateNew();
        var h = new Harness();
        PackfileCommandOutcome refused = await h.Run(unsaved, review: false);
        Assert.Equal(PackfileCommand.UnsavedRefusal, refused.Refusal);
        Assert.Null(refused.Result);
        Assert.Equal(0, h.Saves);

        string level = Path.Combine(_dir, "f.rfl");
        EditorDocument doc = LevelTemplates.CreateNew();
        doc.SaveAs(level);
        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 2, 0));
        h.SaveSucceeds = false;
        PackfileCommandOutcome cancelled = await h.Run(doc, review: false);
        Assert.NotNull(cancelled.Refusal);
        Assert.Null(cancelled.Result);
        Assert.False(File.Exists(Path.Combine(_dir, "f.vpp")));
    }

    [Fact]
    public async Task Create_Packfile_As_Opens_The_Review_With_The_Level_Folder_Default()
    {
        string level = Path.Combine(_dir, "r.rfl");
        EditorDocument doc = LevelTemplates.CreateNew();
        doc.SaveAs(level);
        var h = new Harness();
        PackfileCommandOutcome outcome = await h.Run(doc, review: true);
        Assert.True(outcome.Reviewed);
        Assert.Equal(1, h.Reviews);
        Assert.Equal(PackfileBuildPlan.LevelFolderOutputPath(level), h.ReviewedPlan!.OutputPath);
        Assert.Equal(File.ReadAllBytes(level), h.ReviewedBytes);
        Assert.False(File.Exists(Path.Combine(_dir, "r.vpp"))); // the dialog builds, not the command

        // Plain Create Level Packfile with a dependency missing (no library mounted) opens the review instead of writing.
        var missing = new Harness();
        PackfileCommandOutcome blocked = await missing.Run(doc, review: false);
        Assert.True(blocked.Reviewed);
        Assert.True(missing.ReviewedPlan!.HasMissing);
        Assert.False(File.Exists(Path.Combine(_dir, "r.vpp")));
    }

    private static byte[] ReadPacked(string vpp, string name)
    {
        using VppArchive archive = VppArchive.Open(vpp);
        return archive.Read(name);
    }

    private static void AssertPackedIsSavedFile(string level, string vpp)
    {
        using VppArchive archive = VppArchive.Open(vpp);
        Assert.Equal(Path.GetFileName(level), archive.Entries[0].Name);
        Assert.Equal(File.ReadAllBytes(level), archive.Read(Path.GetFileName(level)));
    }
}
