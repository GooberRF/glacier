using Ged.Core.Compiler;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl;

/// <summary>
/// What a save does with each terrain's build mapping, mirroring Alpine RED's <c>terrain_build_fill_mapping</c> and
/// <c>terrain_serialize_chunk</c> (editor_patch/terrain_build.cpp:885-910, 1126-1170; terrain.cpp:587-597): a mapping is
/// written only while it still describes the terrain (its geometry fingerprint unchanged since the build that made it).
/// A terrain edited since its last build, or a copy, is saved without one, and the game then draws its old compiled
/// rooms as plain geometry. A terrain that was never built is saved without a mapping and has no compiled faces at all.
/// An unedited level keeps every mapping it was loaded with, so it re-saves byte-identically.
/// </summary>
public static class AlpineTerrainSavePolicy
{
    /// <summary>Drops stale mappings (marking the chunk dirty) and returns the save-log lines.</summary>
    public static IReadOnlyList<string> Reconcile(RflFile rfl)
    {
        var notes = new List<string>();
        foreach (RflSection section in rfl.Sections)
        {
            if (section.Content is not AlpineTerrainSection { IsReadOnly: false } terrains)
            {
                continue;
            }

            foreach (AlpineTerrain t in terrains.Terrains)
            {
                string who = TerrainChunkEmitter.Label(t);
                if (t.BuildMapping.Count > 0 && !t.BuildIsCurrent)
                {
                    t.ResetBuiltState();
                    section.Dirty = true;
                    notes.Add($"{who} changed since the last Build Geometry - rebuild before saving. It was saved without its " +
                              "build mapping, so the game draws its old compiled geometry without the terrain blend.");
                }
                else if (t.BuildMapping.Count == 0)
                {
                    notes.Add($"{who} has no compiled geometry - run Build Geometry before saving. Until then the level " +
                              "has no faces or collision for it.");
                }
            }
        }

        return notes;
    }
}
