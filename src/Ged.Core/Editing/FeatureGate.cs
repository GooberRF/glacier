using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Tables;

namespace Ged.Core.Editing;

/// <summary>
/// One Alpine-specific feature usage found during a compatibility check: a
/// human-readable feature label, a detail line, and the affected object/keyframe
/// UIDs (empty for level-wide properties). The UIDs drive the report's jump links.
/// </summary>
public sealed record GateFinding(string Feature, string Detail, IReadOnlyList<int> Uids);

/// <summary>
/// The result of a compatibility check. GED always saves Alpine v305, so this never
/// blocks a save; it is an <em>analysis</em>. Against the Alpine reference the report is
/// always clear. Against the stock (v200) reference, every Alpine-specific feature the
/// level uses is itemized informationally — the report makes no stock-load claim.
/// </summary>
public sealed class FeatureGateReport
{
    public FeatureGateReport(int targetVersion, IReadOnlyList<GateFinding> findings)
    {
        TargetVersion = targetVersion;
        Findings = findings;
    }

    public int TargetVersion { get; }

    public IReadOnlyList<GateFinding> Findings { get; }

    /// <summary>True when Alpine-specific content is present (findings against the stock reference).</summary>
    public bool Blocked => Findings.Count > 0;

    /// <summary>An itemized, multi-line summary of every Alpine-specific feature found.</summary>
    public string Summary()
    {
        if (!Blocked)
        {
            return "No Alpine-specific features detected.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Uses {Findings.Count} Alpine-specific feature(s). Glacier always saves as Alpine v305.");
        sb.AppendLine();
        foreach (GateFinding f in Findings)
        {
            sb.Append("• ").Append(f.Feature);
            if (f.Uids.Count > 0)
            {
                sb.Append("  [uid ").Append(string.Join(", ", f.Uids)).Append(']');
            }

            sb.AppendLine();
            if (!string.IsNullOrEmpty(f.Detail))
            {
                sb.Append("    ").AppendLine(f.Detail);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Compatibility analysis (not a save gate — GED always writes Alpine v305).
/// Enumerates the Alpine-specific features a level uses, measured against the
/// stock (v200) reference: Alpine events (MinVersion ≥ 300), Mesh/Note/Corona/Bag
/// objects, non-default alpine_level_properties, geoable/breakable brushes, mover
/// hold-open, the Dash lightmap-depth flag, and any object counts above the stock
/// caps. The Alpine target always reports clear.
/// </summary>
public static class FeatureGate
{
    public static FeatureGateReport Evaluate(RflFile rfl, SaveTarget target) =>
        Evaluate(rfl, SaveTargets.VersionOf(target));

    public static FeatureGateReport Evaluate(RflFile rfl, int targetVersion)
    {
        System.ArgumentNullException.ThrowIfNull(rfl);
        var findings = new List<GateFinding>();

        // Alpine targets (300+) accept everything.
        if (targetVersion >= SaveTargets.FirstAlpineVersion)
        {
            return new FeatureGateReport(targetVersion, findings);
        }

        rfl.ParseAllKnownSections();

        CheckAlpineEvents(rfl, targetVersion, findings);
        CheckAlpineObjects(rfl, findings);
        CheckAlpineLevelProperties(rfl, findings);
        CheckDashLevelProperties(rfl, findings);
        CheckStockLimits(rfl, findings);

        return new FeatureGateReport(targetVersion, findings);
    }

    private static void CheckAlpineEvents(RflFile rfl, int targetVersion, List<GateFinding> findings)
    {
        if (Content<EventsSection>(rfl) is not { } events)
        {
            return;
        }

        // Group Alpine (or otherwise version-gated) events by class so each finding
        // carries a clean jump list.
        var byClass = new Dictionary<string, List<int>>();
        foreach (var ev in events.Events)
        {
            EventSchema? schema = EventSchemaCatalog.Find(ev.ClassName);
            int minVersion = schema?.MinVersion ?? 0;
            if (minVersion > targetVersion)
            {
                if (!byClass.TryGetValue(ev.ClassName, out List<int>? list))
                {
                    byClass[ev.ClassName] = list = new List<int>();
                }

                list.Add(ev.Uid);
            }
        }

        foreach (var (className, uids) in byClass.OrderBy(k => k.Key))
        {
            findings.Add(new GateFinding(
                $"Alpine event \"{className}\" (requires v300+)",
                $"{uids.Count} instance(s) (Alpine event type).",
                uids));
        }
    }

    private static void CheckAlpineObjects(RflFile rfl, List<GateFinding> findings)
    {
        if (Content<AlpineMeshObjectsSection>(rfl) is { Meshes.Count: > 0 } mesh)
        {
            findings.Add(new GateFinding("Mesh objects (Alpine)",
                "alpine_mesh_objects section (Alpine-specific).", mesh.Meshes.Select(m => m.Uid).ToList()));
        }

        if (Content<AlpineNoteObjectsSection>(rfl) is { Notes.Count: > 0 } note)
        {
            findings.Add(new GateFinding("Note objects (Alpine)",
                "alpine_note_objects section (Alpine-specific).", note.Notes.Select(n => n.Uid).ToList()));
        }

        if (Content<AlpineCoronaObjectsSection>(rfl) is { Coronas.Count: > 0 } corona)
        {
            findings.Add(new GateFinding("Corona objects (Alpine)",
                "alpine_corona_objects section (Alpine-specific).", corona.Coronas.Select(c => c.Uid).ToList()));
        }

        if (Content<AlpineBagObjectsSection>(rfl) is { Bags.Count: > 0 } bag)
        {
            findings.Add(new GateFinding("Bag objects (Alpine)",
                "alpine_bag_objects section (Alpine-specific).", bag.Bags.Select(b => b.Uid).ToList()));
        }
    }

    private static void CheckAlpineLevelProperties(RflFile rfl, List<GateFinding> findings)
    {
        if (Content<AlpineLevelPropertiesSection>(rfl) is not { } alp)
        {
            return;
        }

        void Flag(bool set, string name)
        {
            if (set)
            {
                findings.Add(new GateFinding($"Alpine level property: {name}",
                    "alpine_level_properties (Alpine-specific).", System.Array.Empty<int>()));
            }
        }

        Flag(alp.LegacyCyclicTimers != 0, "legacy cyclic timers");
        Flag(alp.LegacyMovers != 0, "legacy movers");
        Flag(alp.StartsWithHeadlamp != 0, "starts with headlamp");
        Flag(alp.OverrideStaticMeshAmbientLightModifier != 0, "static-mesh ambient override");
        Flag(alp.Rf2StyleGeomod != 0, "RF2-style geomod");

        if (alp.GeoableEntries.Count > 0)
        {
            findings.Add(new GateFinding("Geoable brushes (Alpine)",
                $"{alp.GeoableEntries.Count} brush(es) marked geoable.",
                alp.GeoableEntries.Select(g => g.BrushUid).ToList()));
        }

        if (alp.BreakableEntries.Count > 0)
        {
            findings.Add(new GateFinding("Breakable brushes (Alpine)",
                $"{alp.BreakableEntries.Count} brush(es) marked breakable.",
                alp.BreakableEntries.Select(b => b.BrushUid).ToList()));
        }

        if (alp.HoldOpenKeyframeUids.Count > 0)
        {
            findings.Add(new GateFinding("Mover Hold Open (Alpine)",
                $"{alp.HoldOpenKeyframeUids.Count} keyframe(s) flagged hold-open.",
                alp.HoldOpenKeyframeUids.ToList()));
        }
    }

    private static void CheckDashLevelProperties(RflFile rfl, List<GateFinding> findings)
    {
        if (Content<DashLevelPropertiesSection>(rfl) is { LightmapsFullDepth: > 0 })
        {
            findings.Add(new GateFinding("Dash full-depth lightmaps",
                "dash_level_properties (Alpine-specific).", System.Array.Empty<int>()));
        }
    }

    private static void CheckStockLimits(RflFile rfl, List<GateFinding> findings)
    {
        void Limit(int count, int max, string name)
        {
            if (count > max)
            {
                findings.Add(new GateFinding($"Raised limit: {name}",
                    $"{count} — Alpine raises the stock cap of {max}.", System.Array.Empty<int>()));
            }
        }

        Limit(Content<MpRespawnPointsSection>(rfl)?.Points.Count ?? 0, 32, "MP respawn points");
        Limit(Content<DecalsSection>(rfl)?.Decals.Count ?? 0, 128, "decals");
        Limit(Content<ParticleEmittersSection>(rfl)?.Emitters.Count ?? 0, 128, "particle emitters");
        Limit(Content<AmbientSoundsSection>(rfl)?.Sounds.Count ?? 0, 25, "ambient sounds");
        Limit(Content<ItemsSection>(rfl)?.Items.Count ?? 0, 200, "items");
        Limit(Content<LightsSection>(rfl)?.Lights.Count ?? 0, 1100, "lights");
    }

    private static T? Content<T>(RflFile rfl)
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }
}
