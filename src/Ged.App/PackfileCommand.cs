using System;
using System.IO;
using System.Threading.Tasks;
using Ged.Core.Editor;
using Ged.Core.Packaging;

namespace Ged.App;

/// <summary>The outcome of <see cref="PackfileCommand.RunAsync"/>.</summary>
/// <param name="Result">The written packfile, or null when nothing was written.</param>
/// <param name="Refusal">Why nothing was written (null for a write, or a cancelled review dialog).</param>
/// <param name="SavedFirst">True when the level was saved before packing.</param>
/// <param name="Reviewed">True when the review dialog was shown.</param>
internal readonly record struct PackfileCommandOutcome(PackfileBuildResult? Result, string? Refusal, bool SavedFirst, bool Reviewed);

/// <summary>
/// File ▸ Create Level Packfile (and … As…), without UI: the level is packed as it is now, from the saved file.
/// <list type="number">
/// <item>A never-saved level is refused — the packfile is built from the saved level file and goes next to it.</item>
/// <item>Unsaved changes (or unsealed preview geometry) are saved first through the editor's normal save; a save that
/// does not complete cancels the packfile.</item>
/// <item>The dependency scan runs on the saved document, and the packfile carries the saved file's bytes, so the packed
/// level and the loose level are identical.</item>
/// <item>Create Level Packfile writes <see cref="PackfileBuildPlan.LevelFolderOutputPath"/> directly (replacing an
/// existing packfile in place); it opens the review dialog only when a dependency is missing. … As… always opens it.</item>
/// </list>
/// </summary>
internal static class PackfileCommand
{
    /// <summary>The refusal for a level that was never saved.</summary>
    public const string UnsavedRefusal = "Save the level first: the packfile is built from the saved level file and goes next to it.";

    /// <param name="doc">The open level.</param>
    /// <param name="geometryIsPreview">True when the applied geometry is an unsealed preview (the save re-seals it).</param>
    /// <param name="review">True for Create Level Packfile As… (always review).</param>
    /// <param name="save">The editor's save of the level to its own path; true when the file was written.</param>
    /// <param name="scan">The dependency scan of the (saved) document; null when it cannot run.</param>
    /// <param name="plan">Builds the plan from a scan (its output path is the level's own packfile).</param>
    /// <param name="showReview">Shows the review dialog; returns its result (null when cancelled).</param>
    public static async Task<PackfileCommandOutcome> RunAsync(
        EditorDocument doc,
        bool geometryIsPreview,
        bool review,
        Func<string, Task<bool>> save,
        Func<Task<DependencyScanResult?>> scan,
        Func<DependencyScanResult, PackfileBuildPlan?> plan,
        Func<PackfileBuildPlan, byte[], Task<PackfileBuildResult?>> showReview)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Path is not string path)
        {
            return new PackfileCommandOutcome(null, UnsavedRefusal, false, false);
        }

        bool savedFirst = false;
        if (doc.IsDirty || geometryIsPreview)
        {
            if (!await save(path))
            {
                return new PackfileCommandOutcome(null, "Packfile cancelled: the level was not saved.", false, false);
            }

            savedFirst = true;
        }

        byte[] levelBytes = await File.ReadAllBytesAsync(path);
        if (await scan() is not { } result || plan(result) is not { } p)
        {
            return new PackfileCommandOutcome(null, "Could not scan the level's dependencies.", savedFirst, false);
        }

        if (review || !p.CanBuild)
        {
            return new PackfileCommandOutcome(await showReview(p, levelBytes), null, savedFirst, true);
        }

        PackfileBuildResult written = await Task.Run(() => p.Build(levelBytes));
        return new PackfileCommandOutcome(written, null, savedFirst, false);
    }
}
