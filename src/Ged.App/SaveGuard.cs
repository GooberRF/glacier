namespace Ged.App;

/// <summary>
/// The save decision authority (owner decision — "RED-style + seal guard"). Stock RED never
/// recompiles or re-lights on save: it serializes exactly what was last built. Glacier now matches,
/// with ONE exception. Before <see cref="MainWindow.SaveAsync"/> writes, this class decides:
/// <list type="bullet">
/// <item><see cref="RequiresSeal"/> — whether the document holds UNSEALED live-CSG preview geometry (a
/// state RED never has — thousands of open t-joint edges) that must be re-sealed with a geometry-only
/// build before it is written, so the level does not sparkle / leak in-game.</item>
/// <item><see cref="EvaluateSeal"/> — after that seal build, whether the save may proceed or must abort
/// writing nothing (the seal build was refused because another user build is in flight, or it did not
/// complete). The seal path is the SOLE rebuild trigger and the SOLE path that can abort a save.</item>
/// <item><see cref="NoticeForDirtySave"/> — for a plain (no-seal) save, the one advisory nudge to emit
/// so the author knows stale compiled geometry / stale lightmaps are theirs to rebuild.</item>
/// </list>
/// Factored out so the decision is unit-testable without a live window.
/// </summary>
internal static class SaveGuard
{
    /// <summary>The verdict for whether a save may proceed AFTER the seal build.</summary>
    internal enum PreSaveOutcome
    {
        /// <summary>Geometry is sealed and current — write the file.</summary>
        Proceed,

        /// <summary>The seal build was refused (another user build is running) — abort, retry later.</summary>
        AbortBuildRunning,

        /// <summary>The seal build ran but left the geometry dirty / preview — abort, it did not complete.</summary>
        AbortSealIncomplete,
    }

    /// <summary>The single advisory notification a save emits about staleness / re-seal.</summary>
    internal enum SaveNotice
    {
        /// <summary>Nothing to say (clean, or the seal already narrated) — no advisory.</summary>
        None,

        /// <summary>Hint: saved as-is with unbuilt geometry changes — rebuild when ready.</summary>
        UnbuiltGeometry,

        /// <summary>Hint: saved as-is with unbaked lighting changes — bake when ready.</summary>
        UnbakedLighting,

        /// <summary>Info: the unsealed preview geometry was re-sealed for the save (lightmaps reset).</summary>
        GeometryResealed,
    }

    /// <summary>
    /// True when the document holds UNSEALED live-CSG preview geometry, the SOLE state that forces a
    /// pre-save rebuild. Everything else saves exactly as it stands (RED-style).
    /// </summary>
    internal static bool RequiresSeal(bool geometryIsPreview) => geometryIsPreview;

    /// <summary>
    /// The advisory for a plain (no-seal) RED-style save: a merely-dirty document is written as-is, and
    /// the author is nudged to rebuild / re-bake. Geometry staleness wins over lighting when both are
    /// dirty — one hint per save.
    /// </summary>
    internal static SaveNotice NoticeForDirtySave(bool geometryDirty, bool lightingDirty) =>
        geometryDirty ? SaveNotice.UnbuiltGeometry
        : lightingDirty ? SaveNotice.UnbakedLighting
        : SaveNotice.None;

    /// <summary>
    /// Decides whether the save may write AFTER the seal build (the only rebuild a save triggers).
    /// </summary>
    /// <param name="sealBuildRan">
    /// False only when the seal build was REFUSED (another user build is in flight). True when it ran to
    /// completion.
    /// </param>
    /// <param name="geometryDirty">The build controller's geometry-dirty flag AFTER the seal build.</param>
    /// <param name="geometryIsPreview">The build controller's preview-quality flag AFTER the seal build.</param>
    internal static PreSaveOutcome EvaluateSeal(bool sealBuildRan, bool geometryDirty, bool geometryIsPreview)
    {
        if (!sealBuildRan)
        {
            return PreSaveOutcome.AbortBuildRunning;
        }

        if (geometryDirty || geometryIsPreview)
        {
            return PreSaveOutcome.AbortSealIncomplete;
        }

        return PreSaveOutcome.Proceed;
    }
}
