using System;
using System.Threading.Tasks;
using Ged.Core.Editor;
using Ged.Core.Lighting;

namespace Ged.App;

/// <summary>The outcome of <see cref="EditorSaveFlow.SaveAsync"/>.</summary>
/// <param name="Written">True when the level file was written.</param>
/// <param name="Seal">The seal verdict (always <see cref="LevelSaveSealOutcome.Proceed"/> when no seal was needed).</param>
/// <param name="Notice">The one advisory to show after a successful write.</param>
/// <param name="Error">The write failure message, when the write threw (set by the shell, not the flow).</param>
internal readonly record struct EditorSaveResult(
    bool Written, LevelSaveSealOutcome Seal, LevelSaveNotice Notice, string? Error = null);

/// <summary>
/// The editor's save sequence for a chosen path, without any UI: decide the timestamp stamp, re-seal
/// unsealed preview geometry through the build controller (the seal guard), then write the level and
/// its sidecar through the shared Core <see cref="LevelSavePolicy"/>. File ▸ Save / Save As and
/// command-line saves into the running editor both run it, and the headless host applies the same
/// policy — so every save path writes the same bytes. Write failures propagate to the caller.
/// </summary>
internal static class EditorSaveFlow
{
    /// <param name="doc">The document to save.</param>
    /// <param name="build">The editor's build controller (null in a bare host: nothing to seal).</param>
    /// <param name="path">The target path (already chosen).</param>
    /// <param name="saveAs">True for Save As (always stamps; re-binds the document path).</param>
    /// <param name="levelLighting">The per-level lightmap method override for the sidecar (null = global default).</param>
    /// <param name="status">Optional status-line sink ("Re-sealing preview geometry before save…").</param>
    /// <param name="beforeWrite">Optional hook run after the seal and before the write (the pre-save linter).</param>
    public static async Task<EditorSaveResult> SaveAsync(
        EditorDocument doc,
        GeometryBuildController? build,
        string path,
        bool saveAs,
        LightingMethod? levelLighting,
        Action<string>? status = null,
        Action? beforeWrite = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Captured before the seal build / write, which clear these flags.
        var state = new LevelBuildState(
            build?.GeometryDirty ?? false, build?.GeometryIsPreview ?? false, build?.LightingDirty ?? false);
        bool modified = LevelSavePolicy.StampsTimestamp(saveAs, doc.IsDirty, state);

        LevelSaveNotice notice;
        if (build is not null && LevelSavePolicy.RequiresSeal(state.GeometryIsPreview))
        {
            status?.Invoke("Re-sealing preview geometry before save…");
            bool sealBuildRan = await build.BuildAsync(); // interactive geometry-only re-seal — no lighting bake

            LevelSaveSealOutcome seal = LevelSavePolicy.EvaluateSeal(sealBuildRan, build.GeometryDirty, build.GeometryIsPreview);
            if (seal != LevelSaveSealOutcome.Proceed)
            {
                return new EditorSaveResult(false, seal, LevelSaveNotice.None);
            }

            notice = LevelSaveNotice.GeometryResealed;
        }
        else
        {
            notice = LevelSavePolicy.NoticeForDirtySave(state.GeometryDirty, state.LightingDirty);
        }

        beforeWrite?.Invoke();
        LevelSavePolicy.Write(doc, path, saveAs, modified, levelLighting);
        return new EditorSaveResult(true, LevelSaveSealOutcome.Proceed, notice);
    }
}
