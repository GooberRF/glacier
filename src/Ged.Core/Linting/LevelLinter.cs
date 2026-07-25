using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Tables;

namespace Ged.Core.Linting;

/// <summary>Inputs for a <see cref="LevelLinter"/> run.</summary>
public sealed class LintOptions
{
    /// <summary>The save target the budget checks resolve against.</summary>
    public SaveTarget Target { get; set; } = SaveTarget.Alpine;

    /// <summary>
    /// When set, missing-asset and texture-size checks run against this mounted
    /// VFS; otherwise those checks are skipped (they need a resolvable library).
    /// </summary>
    public AssetVfs? Vfs { get; set; }

    /// <summary>Catalog / dialogue-file options for the dependency scan.</summary>
    public DependencyScanOptions? ScanOptions { get; set; }

    /// <summary>Run the compiled-geometry hole/leak check (needs a built level).</summary>
    public bool CheckGeometryLeaks { get; set; } = true;

    /// <summary>Maximum texture dimension before an oversize warning.</summary>
    public int MaxTextureDimension { get; set; } = 1024;

    /// <summary>Report events/triggers/nav points that are disconnected from the graph.</summary>
    public bool CheckOrphans { get; set; } = true;
}

/// <summary>
/// The level linter: broken/dangling links, missing assets, duplicate UIDs,
/// target-aware limit budgets, geometry leaks, nav-point issues, triggers without
/// links, and event orphans. Runs on demand and as the pre-save summary (only
/// save-target budget violations block; everything else warns). Pure Ged.Core.
/// </summary>
public static class LevelLinter
{
    public static LintReport Lint(RflFile rfl, LintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        options ??= new LintOptions();
        rfl.ParseAllKnownSections();

        var findings = new List<LintFinding>();
        List<LevelObject> objects = LevelObjectEnumerator.Enumerate(rfl, new HashSet<int>());

        CheckDuplicateUids(rfl, objects, findings);
        CheckPlayerStart(objects, findings);
        CheckLinks(objects, findings);
        CheckTriggersAndOrphans(objects, options, findings);
        CheckNavPoints(rfl, options, findings);
        CheckBudgets(rfl, options, findings);
        CheckGeometryLeaks(rfl, options, findings);
        CheckAssets(rfl, options, findings);

        return new LintReport(findings);
    }

    // ---- Duplicate UIDs -------------------------------------------------------

    private static void CheckDuplicateUids(RflFile rfl, List<LevelObject> objects, List<LintFinding> findings)
    {
        var byUid = new Dictionary<int, int>();
        void Bump(int uid)
        {
            if (uid == 0)
            {
                return; // player start / unassigned sentinel
            }

            byUid[uid] = byUid.GetValueOrDefault(uid) + 1;
        }

        foreach (LevelObject o in objects)
        {
            Bump(o.Uid);
        }

        foreach (RflSection section in rfl.Sections)
        {
            if (section.Content is BrushesSection bs)
            {
                foreach (Brush b in bs.Brushes)
                {
                    Bump(b.Uid);
                }
            }
        }

        foreach ((int uid, int count) in byUid.Where(kv => kv.Value > 1).OrderBy(kv => kv.Key))
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.DuplicateUid,
                $"UID {uid} is used by {count} objects — the last one wins at load.", uid));
        }
    }

    // ---- Player Start / spawn point -------------------------------------------

    /// <summary>
    /// Flags a level with no spawn point. A single-player start (player_start) and MP respawn
    /// points are the two ways RF places the player; a level with neither spawns the player in
    /// the void, and RF's portal renderer draws nothing from there — a fully black screen
    /// in-game. Reported as an Error (as serious as a broken link — the level is unplayable) but
    /// non-blocking: the file is valid and RED itself permits saving without a start, matching
    /// GED's "always saves" policy, so this surfaces loudly in the pre-save summary's Error band
    /// without hard-blocking the save (only over-budget items block). An MP level that provides
    /// respawn points legitimately has no single-player start, so it passes.
    /// </summary>
    private static void CheckPlayerStart(List<LevelObject> objects, List<LintFinding> findings)
    {
        bool hasPlayerStart = objects.Any(o => o.Kind == LevelObjectKind.PlayerStart);
        bool hasRespawns = objects.Any(o => o.Kind == LevelObjectKind.MpRespawnPoint);
        if (!hasPlayerStart && !hasRespawns)
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.MissingPlayerStart,
                "Level has no Player Start — the player will spawn in the void and the screen will render black in-game."));
        }
    }

    // ---- Links ----------------------------------------------------------------

    private static void CheckLinks(List<LevelObject> objects, List<LintFinding> findings)
    {
        var kindByUid = objects.GroupBy(o => o.Uid).ToDictionary(g => g.Key, g => g.First().Kind);

        foreach (LevelObject o in objects)
        {
            if (LinkModel.LinksOf(o) is not { } links)
            {
                continue;
            }

            foreach (int target in links)
            {
                if (!kindByUid.TryGetValue(target, out LevelObjectKind targetKind))
                {
                    findings.Add(new LintFinding(LintSeverity.Error, LintCategory.BrokenLink,
                        $"{o.Kind} {o.Uid} links to missing UID {target}.", o.Uid, target));
                    continue;
                }

                if (o.Model is RflEvent ev && EventSchemaCatalog.Find(ev.ClassName) is { } schema
                    && !LinkRules.TargetKindAllowed(schema, targetKind))
                {
                    findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.BrokenLink,
                        $"{ev.ClassName} event {o.Uid} links to a {targetKind}, which it does not act on.",
                        o.Uid, target));
                }
            }
        }
    }

    // ---- Triggers without links + event orphans -------------------------------

    private static void CheckTriggersAndOrphans(
        List<LevelObject> objects, LintOptions options, List<LintFinding> findings)
    {
        // In-degree over the whole link graph (who links TO each UID).
        var inDegree = new Dictionary<int, int>();
        foreach (LevelObject o in objects)
        {
            if (LinkModel.LinksOf(o) is { } links)
            {
                foreach (int t in links)
                {
                    inDegree[t] = inDegree.GetValueOrDefault(t) + 1;
                }
            }
        }

        foreach (LevelObject o in objects)
        {
            switch (o.Model)
            {
                case Trigger t when t.Links.Count == 0:
                    findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.TriggerWithoutLinks,
                        $"Trigger {o.Uid} has no links — it will do nothing.", o.Uid));
                    break;

                case RflEvent ev when options.CheckOrphans:
                    bool hasOut = ev.Links.Count > 0;
                    bool hasIn = inDegree.GetValueOrDefault(ev.Uid) > 0;
                    if (!hasOut && !hasIn)
                    {
                        findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.EventOrphan,
                            $"{ev.ClassName} event {o.Uid} is disconnected (nothing triggers it and it links to nothing).",
                            o.Uid));
                    }

                    break;
            }
        }
    }

    // ---- Nav points -----------------------------------------------------------

    private static void CheckNavPoints(RflFile rfl, LintOptions options, List<LintFinding> findings)
    {
        if (!options.CheckOrphans)
        {
            return;
        }

        NavPointsSection? nav = FirstContent<NavPointsSection>(rfl);
        if (nav is null || nav.NavPoints.Count == 0)
        {
            return;
        }

        var navUids = nav.NavPoints.Select(n => n.Uid).ToHashSet();
        var degree = nav.NavPoints.ToDictionary(n => n.Uid, _ => 0);

        foreach (NavPoint n in nav.NavPoints)
        {
            foreach (int t in n.Links)
            {
                if (navUids.Contains(t))
                {
                    degree[n.Uid]++;
                    degree[t]++;
                }
            }
        }

        // Only flag when there is more than one nav point (a lone nav point is legitimate).
        if (nav.NavPoints.Count > 1)
        {
            foreach (NavPoint n in nav.NavPoints.Where(n => degree[n.Uid] == 0))
            {
                findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.NavPoint,
                    $"Nav point {n.Uid} is isolated (no connections to other nav points).", n.Uid));
            }
        }
    }

    // ---- Budgets --------------------------------------------------------------

    private static void CheckBudgets(RflFile rfl, LintOptions options, List<LintFinding> findings)
    {
        foreach (BudgetLine line in LevelBudget.Compute(rfl))
        {
            LintSeverity sev = line.Severity(options.Target);
            if (sev == LintSeverity.Info)
            {
                continue;
            }

            int cap = line.Cap(options.Target);
            bool blocks = line.Over(options.Target);
            string over = line.Over(options.Target)
                ? $"{line.Count}/{cap} exceeds the {SaveTargets.DisplayName(options.Target)} cap"
                : $"{line.Count}/{cap} ({line.Fraction(options.Target) * 100:0}% of budget)";
            findings.Add(new LintFinding(sev, LintCategory.LimitBudget,
                $"{line.Name}: {over}.", BlocksSave: blocks));
        }
    }

    // ---- Geometry leaks -------------------------------------------------------

    private static void CheckGeometryLeaks(RflFile rfl, LintOptions options, List<LintFinding> findings)
    {
        if (!options.CheckGeometryLeaks)
        {
            return;
        }

        GeometrySection? geo = FirstContent<GeometrySection>(rfl);
        if (geo is null || geo.Geometry.Faces.Count == 0)
        {
            return;
        }

        List<Vec3> holes = HoleDetector.Detect(geo.Geometry);
        if (holes.Count > 0)
        {
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.GeometryLeak,
                $"{holes.Count} open edge(s) in compiled geometry — the level may leak. Run Check for Holes to locate."));
        }
    }

    // ---- Missing assets + texture size ---------------------------------------

    private static void CheckAssets(RflFile rfl, LintOptions options, List<LintFinding> findings)
    {
        if (options.Vfs is not { } vfs)
        {
            return;
        }

        var resolver = new VfsDependencyResolver(vfs);
        DependencyScanResult scan = DependencyScanner.Scan(rfl, resolver, options.ScanOptions);
        foreach (PackDependency dep in scan.Missing)
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.MissingAsset,
                $"Missing {KindLabel(dep.Kind)} '{dep.FileName}' ({string.Join(", ", dep.Origins.Take(3))})."));
        }

        var missingTextures = new HashSet<string>(
            scan.Missing.Select(d => d.FileName),
            StringComparer.OrdinalIgnoreCase);

        foreach (TextureVerifyResult tv in TextureVerifier.Verify(rfl, vfs, options.MaxTextureDimension))
        {
            if (tv.Issue == TextureIssue.Missing || missingTextures.Contains(tv.TextureName))
            {
                continue; // already reported by the dependency scan
            }

            int? uid = tv.Usages.Count > 0 ? tv.Usages[0].Uid : null;
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.TextureSize,
                $"Texture '{tv.TextureName}' is {(tv.Issue == TextureIssue.NonPowerOfTwo ? "non-power-of-two" : "oversize")}: {tv.Detail}.",
                uid));
        }
    }

    private static string KindLabel(DependencyKind kind) => kind switch
    {
        DependencyKind.FaceTexture or DependencyKind.LiquidTexture or DependencyKind.DecalTexture
            or DependencyKind.ParticleBitmap or DependencyKind.BoltBitmap or DependencyKind.CoronaBitmap
            or DependencyKind.EventBitmap or DependencyKind.MeshObjectTexture or DependencyKind.GeomodTexture
            or DependencyKind.ClutterSkin or DependencyKind.EntitySkin or DependencyKind.AtxFrame => "texture",
        DependencyKind.MeshObject or DependencyKind.EventMesh or DependencyKind.ClutterMesh
            or DependencyKind.EntityMesh or DependencyKind.ItemMesh => "mesh",
        DependencyKind.EventSound or DependencyKind.AmbientSound or DependencyKind.MoverSound => "sound",
        DependencyKind.MeshAnimation or DependencyKind.EventAnimation => "animation",
        _ => "asset",
    };

    private static T? FirstContent<T>(RflFile rfl)
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
