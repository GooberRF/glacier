using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Lighting;

namespace Ged.Core.Editor;

/// <summary>The single advisory a save reports about stale compiled geometry / lighting.</summary>
public enum LevelSaveNotice
{
    /// <summary>Nothing to say (clean, or nothing stale).</summary>
    None,

    /// <summary>Saved as-is with unbuilt geometry changes — rebuild when ready.</summary>
    UnbuiltGeometry,

    /// <summary>Saved as-is with unbaked lighting changes — bake when ready.</summary>
    UnbakedLighting,

    /// <summary>The unsealed preview geometry was re-sealed for the save (lightmaps reset).</summary>
    GeometryResealed,
}

/// <summary>Whether a save may write after its seal build.</summary>
public enum LevelSaveSealOutcome
{
    /// <summary>Geometry is sealed and current — write the file.</summary>
    Proceed,

    /// <summary>The seal build was refused (another build is running) — write nothing.</summary>
    AbortBuildRunning,

    /// <summary>The seal build ran but left the geometry dirty / preview — write nothing.</summary>
    AbortSealIncomplete,
}

/// <summary>
/// The host's knowledge of the document's compiled geometry at save time: whether brushes changed
/// since the last build, whether the applied geometry is an UNSEALED preview, and whether
/// lighting-relevant content changed since the last bake.
/// </summary>
public readonly record struct LevelBuildState(bool GeometryDirty, bool GeometryIsPreview, bool LightingDirty);

/// <summary>
/// The document-level save rules shared by every host that writes a level — the editor's File ▸ Save
/// and the command-line / automation save — so the same document and build state always produce the
/// same bytes. RED-style: a save never recompiles or re-lights, it serializes what was last built.
/// The ONE exception is unsealed live-CSG preview geometry, which the host re-seals with a
/// geometry-only build before writing (<see cref="RequiresSeal"/>). The header timestamp / level_info
/// date update only when something changed (<see cref="StampsTimestamp"/>), so a no-op save of an
/// untouched v306 file stays byte-identical. Every write is Alpine v306 plus the editor-only
/// <c>.gedlayout.json</c> sidecar (<see cref="Write"/>).
/// </summary>
public static class LevelSavePolicy
{
    /// <summary>True when the document holds unsealed preview geometry — the sole state that forces
    /// a pre-save (geometry-only) rebuild.</summary>
    public static bool RequiresSeal(bool geometryIsPreview) => geometryIsPreview;

    /// <summary>The advisory for a plain (no-seal) save; geometry staleness wins over lighting.</summary>
    public static LevelSaveNotice NoticeForDirtySave(bool geometryDirty, bool lightingDirty) =>
        geometryDirty ? LevelSaveNotice.UnbuiltGeometry
        : lightingDirty ? LevelSaveNotice.UnbakedLighting
        : LevelSaveNotice.None;

    /// <summary>Decides whether the save may write after its seal build.</summary>
    /// <param name="sealBuildRan">False only when the seal build was refused (another build in flight).</param>
    /// <param name="geometryDirty">Geometry-dirty AFTER the seal build.</param>
    /// <param name="geometryIsPreview">Preview-quality AFTER the seal build.</param>
    public static LevelSaveSealOutcome EvaluateSeal(bool sealBuildRan, bool geometryDirty, bool geometryIsPreview)
    {
        if (!sealBuildRan)
        {
            return LevelSaveSealOutcome.AbortBuildRunning;
        }

        return geometryDirty || geometryIsPreview ? LevelSaveSealOutcome.AbortSealIncomplete : LevelSaveSealOutcome.Proceed;
    }

    /// <summary>
    /// Whether the save stamps the header timestamp + level_info date: Save As always does; a plain
    /// save only when the document or its compiled state changed. Evaluate BEFORE the seal build.
    /// </summary>
    public static bool StampsTimestamp(bool saveAs, bool documentDirty, LevelBuildState state) =>
        saveAs || documentDirty || state.GeometryDirty || state.GeometryIsPreview || state.LightingDirty;

    /// <summary>The user-facing advisory text for <paramref name="notice"/>, or null for none.</summary>
    public static string? NoticeText(LevelSaveNotice notice) => notice switch
    {
        LevelSaveNotice.UnbuiltGeometry => "Saved with unbuilt geometry changes — rebuild when ready.",
        LevelSaveNotice.UnbakedLighting => "Saved with unbaked lighting changes — bake when ready.",
        LevelSaveNotice.GeometryResealed => "Geometry re-sealed for save — lightmaps were reset; bake lighting when ready.",
        _ => null,
    };

    /// <summary>
    /// Writes the level: Alpine v306 (<see cref="EditorDocument.SaveAs"/> for a Save As, else
    /// <see cref="EditorDocument.Save"/>), then the editor-only sidecar beside it with the document's
    /// annotations and the per-level lightmap method (<paramref name="levelLighting"/>, null = use the
    /// global default). A sidecar failure is non-fatal; an RFL write failure throws.
    /// </summary>
    public static void Write(
        EditorDocument doc, string path, bool saveAs, bool stampTimestamp, LightingMethod? levelLighting)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (saveAs)
        {
            doc.SaveAs(path, updateTimestamp: true);
        }
        else
        {
            doc.Save(path, updateTimestamp: stampTimestamp);
        }

        WriteSidecar(doc, path, levelLighting);
    }

    /// <summary>Writes the annotations + lightmap-method blocks of the level's sidecar (best-effort).</summary>
    public static void WriteSidecar(EditorDocument doc, string rflPath, LightingMethod? levelLighting)
    {
        ArgumentNullException.ThrowIfNull(doc);
        try
        {
            string sp = LevelSidecarStore.SidecarPathFor(rflPath);
            LevelSidecarStore.SaveAnnotations(sp, doc.Annotations ?? (IReadOnlyList<Annotation>)Array.Empty<Annotation>());
            LevelSidecarStore.SaveLighting(sp, levelLighting);
        }
        catch (Exception)
        {
            // Non-fatal: the sidecar is editor-only convenience data.
        }
    }
}
