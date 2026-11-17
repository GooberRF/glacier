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

    /// <summary>
    /// The most leak edges reported as individual (located) findings; any beyond this are
    /// summarized in one extra finding.
    /// </summary>
    public int MaxLeakFindings { get; set; } = 50;

    /// <summary>
    /// The most brushes with visible placeholder-textured (or untextured) faces reported individually; any beyond
    /// this are summarized in one extra finding.
    /// </summary>
    public int MaxPlaceholderFindings { get; set; } = 50;

    /// <summary>Maximum texture dimension before an oversize warning.</summary>
    public int MaxTextureDimension { get; set; } = 1024;

    /// <summary>Report events/triggers/nav points that are disconnected from the graph.</summary>
    public bool CheckOrphans { get; set; } = true;

    /// <summary>
    /// Compare the level's metrics with the shipped corpus norms and report out-of-range ones as Info
    /// (<see cref="LintCategory.OutOfNorm"/>; CORPUS-NORMS-SPEC §5.5). Off by default (the implicit save-time lint);
    /// explicit lint runs — the Lint panel, <c>glacier-cli lint</c>, MCP, Lua <c>lint.run</c> — turn it on.
    /// </summary>
    public bool CheckNorms { get; set; }

    /// <summary>
    /// True when the compiled geometry is an unsealed preview build: the out-of-norm check is skipped with a note
    /// (preview geometry is not what the level will ship with).
    /// </summary>
    public bool GeometryIsPreview { get; set; }

    /// <summary>The most out-of-norm findings reported (most extreme first).</summary>
    public int MaxNormFindings { get; set; } = 6;

    /// <summary>The space sampler's floor-sample cap for the out-of-norm check (the pitch grows until it fits).</summary>
    public int NormFloorSamples { get; set; } = 5_000;

    /// <summary>The level's file name (gametype prefix for the norms bucket), or null.</summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Run the craft audit (<see cref="CraftAudit"/>: sunk and random props, stairs and openings that lead nowhere, perches,
    /// flat-sided rock, basic areas) as warnings. Off by default (the implicit save-time lint); explicit lint runs turn it
    /// on. Needs sealed compiled geometry (skipped with a note on a preview build).
    /// </summary>
    public bool CheckCraft { get; set; }

    /// <summary>The most craft-audit findings reported per check.</summary>
    public int MaxCraftFindings { get; set; } = 20;
}

/// <summary>
/// The level linter: broken/dangling links, missing assets, duplicate UIDs,
/// target-aware limit budgets, geometry leaks, nav-point issues, triggers without
/// links, event orphans, and visible faces left with the placeholder texture. Runs on demand and as the pre-save summary (only
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
        CheckPortalBrushes(rfl, findings);
        CheckAirBrushOrientation(rfl, findings);
        CheckPlayerStart(objects, findings);
        SpawnHeightCheck.Run(rfl, objects, findings);
        CheckLinks(rfl, objects, findings);
        CheckAlpinePrerequisites(rfl, objects, findings);
        AlpineObjectChecks.Run(rfl, objects, options, findings);
        CheckTriggersAndOrphans(objects, options, findings);
        CheckNavPoints(rfl, options, findings);
        CheckBudgets(rfl, options, findings);
        CheckGeometryLeaks(rfl, options, findings);
        CheckAssets(rfl, options, findings);
        var notes = new List<string>();
        PlaceholderTextureCheck.Run(rfl, options, findings, notes);
        DecalClipCheck.Run(rfl, findings, notes);
        if (options.CheckNorms)
        {
            CheckNorms(rfl, options, findings, notes);
        }

        if (options.CheckCraft)
        {
            CheckCraft(rfl, options, findings, notes);
        }

        return new LintReport(findings) { Notes = notes };
    }

    // ---- Craft audit ------------------------------------------------------------------------------

    private static void CheckCraft(RflFile rfl, LintOptions options, List<LintFinding> findings, List<string> notes)
    {
        if (options.GeometryIsPreview)
        {
            notes.Add("Geometry is an unsealed preview build: the craft audit was skipped. Build (sealed) to audit the level.");
            return;
        }

        if (Spatial.SpatialLevel.StoredGeometry(rfl) is not { Faces.Count: > 0 } geo)
        {
            notes.Add("The level has no compiled geometry: the craft audit was skipped. Build it to audit the level.");
            return;
        }

        CraftAuditReport report;
        try
        {
            var doc = new EditorDocument(rfl);
            Spatial.SpatialLevel level = Spatial.SpatialLevel.FromDocument(doc, geo, null, options.FileName);
            report = CraftAudit.Run(level, new CraftAuditOptions
            {
                MaxPerCheck = options.MaxCraftFindings,
                ObjectBounds = options.Vfs is { } vfs ? CraftAudit.MeshBoundsFrom(vfs) : null,
                Budget = new Spatial.SpatialBudget { TimeLimit = TimeSpan.FromSeconds(60) },
            });
        }
        catch (Exception ex) when (ex is Spatial.SpatialException or OperationCanceledException)
        {
            notes.Add($"The craft audit did not run: {ex.Message}");
            return;
        }

        notes.AddRange(report.Notes);
        foreach (CraftFinding f in report.Findings)
        {
            findings.Add(new LintFinding(f.Severity, CraftAudit.Category(f.Check), $"{f.Message} Fix: {f.Fix}", f.Uid,
                Location: new Vec3((float)f.Pos.X, (float)f.Pos.Y, (float)f.Pos.Z)));
        }

        foreach ((string check, int count) in report.Counts)
        {
            int shown = report.Findings.Count(f => f.Check == check);
            if (count > shown)
            {
                notes.Add($"Craft audit: {count - shown} more '{check}' finding(s) not listed (glacier-cli audit or level.audit{{}} lists them all).");
            }
        }
    }

    // ---- Out-of-norm metrics ----------------------------------------------------

    /// <summary>
    /// One data-driven loop over the shipped norms (CORPUS-NORMS-SPEC §5.5): every metric that drives defaults, compared
    /// with its reference bucket's p5–p95 range; at most <see cref="LintOptions.MaxNormFindings"/> Info findings, most
    /// extreme first. No per-metric code and no level-specific branches. Needs sealed compiled geometry.
    /// </summary>
    private static void CheckNorms(RflFile rfl, LintOptions options, List<LintFinding> findings, List<string> notes)
    {
        if (!Analysis.CorpusNorms.Available)
        {
            notes.Add("This build carries no corpus norms: the out-of-norm check was skipped.");
            return;
        }

        if (options.GeometryIsPreview)
        {
            notes.Add("Geometry is an unsealed preview build: the out-of-norm check was skipped. Build (sealed) to compare the level with the corpus norms.");
            return;
        }

        if (FirstContent<GeometrySection>(rfl)?.Geometry is not { Faces.Count: > 0 })
        {
            notes.Add("The level has no compiled geometry: the out-of-norm check was skipped. Build it to compare it with the corpus norms.");
            return;
        }

        Analysis.LevelFingerprint fp;
        using var limit = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            fp = Analysis.LevelMetrics.Compute(rfl, new Analysis.MetricOptions { FileName = options.FileName, MaxFloorSamples = options.NormFloorSamples }, limit.Token);
        }
        catch (OperationCanceledException)
        {
            notes.Add("The out-of-norm check did not finish within 30 s and was skipped.");
            return;
        }

        foreach (Analysis.NormComparison c in Analysis.NormComparisons.Compare(fp.Json)
            .Where(c => c.Verdict != "within")
            .Take(Math.Max(0, options.MaxNormFindings)))
        {
            findings.Add(new LintFinding(LintSeverity.Info, LintCategory.OutOfNorm, c.Message()));
        }
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

        var brushUids = new HashSet<int>();
        foreach (RflSection section in rfl.Sections)
        {
            if (section.Content is BrushesSection bs)
            {
                foreach (Brush b in bs.Brushes)
                {
                    Bump(b.Uid);
                    brushUids.Add(b.Uid);
                }
            }
        }

        // RED writes each moving group into the editor groups section (0x3000000) as well as moving_groups
        // (0x3000), keyframes included (every stock mover level, e.g. dm03/dm06). The game reads only the
        // moving_groups copy, so a keyframe's editor-groups twin is the same keyframe, not a duplicate.
        var movingKeyframes = objects
            .Where(o => o.Kind == LevelObjectKind.Keyframe && o.Section.TypeId == (uint)SectionType.MovingGroups)
            .Select(o => o.Uid)
            .ToHashSet();

        var moverTwins = new HashSet<int>();
        foreach (LevelObject o in objects)
        {
            if (o.Kind == LevelObjectKind.Keyframe && o.Section.TypeId == (uint)SectionType.Groups && movingKeyframes.Contains(o.Uid))
            {
                continue;
            }

            // A mover's brush is stored twice by design — in the brushes section and, under the SAME
            // UID, in the movers section (RED writes every stock mover this way, e.g. dm03/dm06). The
            // movers-section copy is one object with its brushes-section twin, not a duplicate — but
            // only ONE copy per brush: a second movers-section copy under that UID is still counted.
            if (o.Kind == LevelObjectKind.Mover && brushUids.Contains(o.Uid) && moverTwins.Add(o.Uid))
            {
                continue;
            }

            Bump(o.Uid);
        }

        foreach ((int uid, int count) in byUid.Where(kv => kv.Value > 1).OrderBy(kv => kv.Key))
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.DuplicateUid,
                $"UID {uid} is used by {count} objects — the last one wins at load.", uid,
                Location: objects.FirstOrDefault(o => o.Uid == uid)?.Position));
        }
    }

    // ---- Portal brushes -----------------------------------------------------------

    /// <summary>
    /// Portal brushes against RED's recovered rule (compiler-parity-notes.md "Air portal brushes and inside-out
    /// rooms", rule 1; RED.exe GeoBuild_Driver 0x004399b0 / 0x00439e5a). RED applies every portal brush after the
    /// ordinary brushes. An AIR portal brush is a boolean that opens solid (mode 4): the wall faces it opens become
    /// two-sided portal faces and the opening a room between them — so one placed in space that is already open
    /// opens nothing and RED builds no portal from it (a warning: Glacier keeps a centre-plane portal for it, Alpine
    /// RED would not). A SOLID portal brush carves nothing (mode 5): its faces that lie in open space become portal
    /// faces — a valid membrane, reported as information only.
    /// </summary>
    private static void CheckPortalBrushes(RflFile rfl, List<LintFinding> findings)
    {
        List<Brush> world = MoverBrushes.StaticWorldBrushes(rfl);
        List<BrushVolume>? structural = null;
        foreach (Brush b in world)
        {
            if ((b.Flags & (uint)BrushFlags.Portal) == 0)
            {
                continue;
            }

            if ((b.Flags & (uint)BrushFlags.Air) == 0)
            {
                findings.Add(new LintFinding(LintSeverity.Info, LintCategory.SolidPortal,
                    $"Brush {b.Uid} is a solid (non-air) portal: it carves nothing, and its faces that lie in open space become portal faces (a membrane). " +
                    "To open a doorway that is its own room between two portals, make it air and fit it through the wall.", b.Uid, Location: b.Position));
                continue;
            }

            // The ordinary (non-portal, non-detail) brushes in time order, as volumes: built once, on the first air portal.
            structural ??= world
                .Where(o => (o.Flags & (uint)(BrushFlags.Portal | BrushFlags.Detail | BrushFlags.Geoable)) == 0)
                .Select(o => (Air: (o.Flags & (uint)BrushFlags.Air) != 0, Faces: BrushWorld.ToOutwardWorldFaces(o)))
                .Where(o => o.Faces.Count >= 4)
                .Select((o, i) => BrushVolume.From(i, o.Air, o.Faces))
                .ToList();
            if (OpensNoSolid(b, structural))
            {
                findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.PortalOpensNothing,
                    $"Brush {b.Uid} is an air portal in space that is already open: RED builds a portal from an air portal brush only where it cuts through solid, so it builds none here. " +
                    "Size it to cut through the wall (it can carve the doorway itself), or make it solid (non-air) so its faces form a membrane.", b.Uid, Location: b.Position));
            }
        }
    }

    /// <summary>
    /// Warns about each air brush stored with its faces wound outward (<see cref="Editing.BrushOrientation"/>): RED
    /// stores air brushes inward and folds their faces into the world as stored, so a RED rebuild would turn this
    /// brush inside-out. Typical of a level written by an older Glacier or another tool; saving in Glacier repairs it.
    /// </summary>
    private static void CheckAirBrushOrientation(RflFile rfl, List<LintFinding> findings)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is not BrushesSection bs)
            {
                continue;
            }

            foreach (Brush b in bs.Brushes)
            {
                if (Editing.BrushOrientation.RequiresInward(b.Flags) && Editing.BrushOrientation.Measure(b) == Editing.BrushWinding.Outward)
                {
                    findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AirBrushInsideOut,
                        $"Air brush {b.Uid} is stored with its faces facing outward: RED stores air brushes facing inward, so rebuilding this level in RED would turn the brush inside-out. " +
                        "Save the level in Glacier to store it the way RED does.", b.Uid, Location: b.Position));
                }
            }
        }
    }

    /// <summary>
    /// True when every sample of the brush (its centre, and each corner pulled a fifth of the way toward it) lies in
    /// open space after the ordinary brushes' time-ordered fold (the world starts solid; air carves, solid fills).
    /// </summary>
    private static bool OpensNoSolid(Brush portal, List<BrushVolume> structural)
    {
        List<CsgFace> faces = BrushWorld.ToWorldFaces(portal, 0, out _);
        var corners = faces.SelectMany(f => f.Vertices).Select(v => v.Position).Distinct().ToList();
        if (corners.Count < 4)
        {
            return false;
        }

        Vec3 centre = corners.Aggregate(default(Vec3), (a, c) => a.Add(c)).Scale(1f / corners.Count);
        var samples = new List<Vec3> { centre };
        samples.AddRange(corners.Select(c => c.Add(centre.Sub(c).Scale(0.2f))));
        Vec3 lo = samples.Aggregate(new Vec3(float.MaxValue, float.MaxValue, float.MaxValue), (a, p) => new Vec3(MathF.Min(a.X, p.X), MathF.Min(a.Y, p.Y), MathF.Min(a.Z, p.Z)));
        Vec3 hi = samples.Aggregate(new Vec3(float.MinValue, float.MinValue, float.MinValue), (a, p) => new Vec3(MathF.Max(a.X, p.X), MathF.Max(a.Y, p.Y), MathF.Max(a.Z, p.Z)));
        var near = new List<BrushVolume>();
        foreach (BrushVolume v in structural)
        {
            if (v.Max.X >= lo.X && v.Min.X <= hi.X && v.Max.Y >= lo.Y && v.Min.Y <= hi.Y && v.Max.Z >= lo.Z && v.Min.Z <= hi.Z)
            {
                near.Add(v);
            }
        }

        foreach (Vec3 p in samples)
        {
            bool open = false;
            foreach (BrushVolume v in near)
            {
                if (v.Contains(p))
                {
                    open = v.IsAir;
                }
            }

            if (!open)
            {
                return false;
            }
        }

        return true;
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

    private static void CheckLinks(RflFile rfl, List<LevelObject> objects, List<LintFinding> findings)
    {
        var kindByUid = objects.GroupBy(o => o.Uid).ToDictionary(g => g.Key, g => g.First().Kind);

        // Alpine 1.5 lets events link to brushes by UID (editor_patch/event.cpp:1674-1678); When_Destroyed tracks
        // destructible detail brushes that way. Brush UIDs are therefore link targets, not missing UIDs.
        var brushDetail = new Dictionary<int, bool>();
        foreach (RflSection section in rfl.Sections)
        {
            if (section.Content is BrushesSection bs)
            {
                foreach (Brush b in bs.Brushes)
                {
                    brushDetail.TryAdd(b.Uid, (b.Flags & (uint)BrushFlags.Detail) != 0);
                }
            }
        }

        foreach (LevelObject o in objects)
        {
            if (LinkModel.LinksOf(o) is not { } links)
            {
                continue;
            }

            foreach (int target in links)
            {
                if (!kindByUid.ContainsKey(target) && brushDetail.TryGetValue(target, out bool detail))
                {
                    CheckBrushLink(o, target, detail, findings);
                    continue;
                }

                if (!kindByUid.TryGetValue(target, out LevelObjectKind targetKind))
                {
                    findings.Add(new LintFinding(LintSeverity.Error, LintCategory.BrokenLink,
                        $"{o.Kind} {o.Uid} links to missing UID {target}.", o.Uid, target, Location: o.Position));
                    continue;
                }

                if (o.Model is RflEvent ev && EventSchemaCatalog.Find(ev.ClassName) is { } schema
                    && !LinkRules.TargetKindAllowed(schema, targetKind))
                {
                    findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.BrokenLink,
                        $"{ev.ClassName} event {o.Uid} links to a {targetKind}, which it does not act on.",
                        o.Uid, target, Location: o.Position));
                }
            }
        }
    }

    /// <summary>
    /// A link to a brush: only an event that acts on detail brushes (When_Destroyed) uses one, and only a detail
    /// brush can be destroyed into the tracked state (game_patch/object/event_alpine.h:3327-3412).
    /// </summary>
    private static void CheckBrushLink(LevelObject origin, int brushUid, bool detail, List<LintFinding> findings)
    {
        bool actsOnBrushes = origin.Model is RflEvent ev
            && EventSchemaCatalog.Find(ev.ClassName) is { } schema
            && schema.LinkTargets.Contains(EventLinkTarget.DetailBrush);
        if (!actsOnBrushes)
        {
            string who = origin.Model is RflEvent e ? $"{e.ClassName} event" : origin.Kind.ToString();
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.BrokenLink,
                $"{who} {origin.Uid} links to brush {brushUid}, which it does not act on.",
                origin.Uid, brushUid, Location: origin.Position));
        }
        else if (!detail)
        {
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.BrokenLink,
                $"{((RflEvent)origin.Model).ClassName} event {origin.Uid} links to brush {brushUid}, which is not a detail brush: only destructible detail brushes can be destroyed.",
                origin.Uid, brushUid, Location: origin.Position));
        }
    }

    // ---- Alpine prerequisites ---------------------------------------------------

    /// <summary>
    /// Alpine 1.5 features that only run when the level meets their prerequisites: the Payload cart. These mirror the
    /// checks Alpine Faction makes when a Payload level starts (game_patch/multi/payload.cpp:1016-1066
    /// <c>payload_level_init_post</c>, :218-245 <c>find_linked_mover</c> / <c>validate_cart</c>, :247-301
    /// <c>build_track</c>, :303-330 <c>find_cart_center</c>), each of which otherwise leaves the level without a cart
    /// or drops a checkpoint with only a log line in game. Glacier always saves RFL 306, so Legacy Movers is the version
    /// prerequisite that can be wrong; a level without an Alpine properties chunk has Legacy Movers on.
    /// </summary>
    private static void CheckAlpinePrerequisites(RflFile rfl, List<LevelObject> objects, List<LintFinding> findings)
    {
        List<LevelObject> handlers = objects.Where(o => IsEventClass(o, "Payload_Handler")).ToList();
        if (handlers.Count == 0)
        {
            return;
        }

        LevelObject payload = handlers[0];
        void Warn(string message, int uid, Vec3 at, int? target = null) =>
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.AlpinePrerequisite, message, uid, target, Location: at));

        AlpineLevelPropertiesSection? props = FirstContent<AlpineLevelPropertiesSection>(rfl);
        if (props is null || props.LegacyMovers != 0)
        {
            Warn($"Payload_Handler event {payload.Uid} needs Legacy Movers off (Level Properties, Alpine): with legacy movers Alpine Faction does not run the payload cart.",
                payload.Uid, payload.Position);
        }

        foreach (LevelObject extra in handlers.Skip(1))
        {
            Warn($"Payload_Handler event {extra.Uid} is ignored: a level has one cart, and Alpine Faction uses the first Payload_Handler ({payload.Uid}).",
                extra.Uid, extra.Position);
        }

        // A link to a mover is stored as its start keyframe (the mover's UID in game).
        var cartByStartKeyframe = new Dictionary<int, Group>();
        foreach (RflSection section in rfl.Sections)
        {
            if (section.Content is GroupsSection { Type: SectionType.MovingGroups } moving)
            {
                foreach (Group g in moving.Groups)
                {
                    if (g.MovingData is { Keyframes.Count: > 0 } data)
                    {
                        cartByStartKeyframe.TryAdd(data.Keyframes[0].Uid, g);
                    }
                }
            }
        }

        Group? cart = ((RflEvent)payload.Model).Links
            .Select(uid => cartByStartKeyframe.GetValueOrDefault(uid))
            .FirstOrDefault(g => g is not null);
        if (cart?.MovingData is not { } motion)
        {
            Warn($"Payload_Handler event {payload.Uid} links to no mover, so the level has no cart: link it to the cart's moving group.",
                payload.Uid, payload.Position);
            return;
        }

        string cartName = cart.Name.Length > 0 ? $"'{cart.Name}'" : $"(start keyframe {motion.Keyframes[0].Uid})";
        if (motion.MovementType != 1)
        {
            Warn($"The payload cart {cartName} must be One Way (its movement type), or the level has no cart.",
                payload.Uid, payload.Position, motion.Keyframes[0].Uid);
        }

        if (motion.RotateInPlace != 0)
        {
            Warn($"The payload cart {cartName} must not Rotate in Place, or the level has no cart.",
                payload.Uid, payload.Position, motion.Keyframes[0].Uid);
        }

        if (motion.Keyframes.Count < 2)
        {
            Warn($"The payload cart {cartName} needs at least 2 keyframes (its track), or the level has no cart.",
                payload.Uid, payload.Position, motion.Keyframes[0].Uid);
        }
        else if (motion.Keyframes.All(k => k.Position == motion.Keyframes[0].Position))
        {
            Warn($"The payload cart {cartName}'s keyframes are all in one place, so it has no track and the level has no cart.",
                payload.Uid, payload.Position, motion.Keyframes[0].Uid);
        }

        if (cart.Brushes.Count == 0 && cart.Objects.Count == 0)
        {
            Warn($"The payload cart {cartName} has no brushes or objects, so the level has no cart.",
                payload.Uid, payload.Position, motion.Keyframes[0].Uid);
        }

        // Checkpoints are the Payload_Checkpoint events the cart's keyframes name (their Triggered Event).
        var events = objects.Where(o => o.Model is RflEvent).GroupBy(o => o.Uid).ToDictionary(g => g.Key, g => g.First());
        var named = new HashSet<int>();
        for (int i = 0; i < motion.Keyframes.Count; i++)
        {
            Keyframe kf = motion.Keyframes[i];
            if (!events.TryGetValue(kf.EventUid, out LevelObject? ev))
            {
                continue;
            }

            if (IsEventClass(ev, "Capture_Point_Handler"))
            {
                Warn($"Cart keyframe {kf.Uid} names Capture_Point_Handler {ev.Uid}; a payload checkpoint is a Payload_Checkpoint event, so it is ignored.",
                    ev.Uid, kf.Position, kf.Uid);
            }
            else if (IsEventClass(ev, "Payload_Checkpoint"))
            {
                if (i == 0)
                {
                    Warn($"Payload_Checkpoint {ev.Uid} is named by the cart's first keyframe, where the cart starts, so it is ignored: name it on a later keyframe.",
                        ev.Uid, ev.Position, kf.Uid);
                }
                else if (!named.Add(ev.Uid))
                {
                    Warn($"Payload_Checkpoint {ev.Uid} is named by more than one cart keyframe; only the first counts.",
                        ev.Uid, ev.Position, kf.Uid);
                }
            }
        }

        if (named.Count == 0)
        {
            Warn($"The payload cart {cartName} has no checkpoints: name a Payload_Checkpoint event in a cart keyframe's Triggered Event (triggered_event_uid).",
                payload.Uid, payload.Position, motion.Keyframes[0].Uid);
        }

        foreach (LevelObject cp in objects.Where(o => IsEventClass(o, "Payload_Checkpoint") && !named.Contains(o.Uid)))
        {
            Warn($"Payload_Checkpoint {cp.Uid} is not on the cart's track: no cart keyframe names it (Triggered Event), so it never activates.",
                cp.Uid, cp.Position);
        }
    }

    private static bool IsEventClass(LevelObject o, string className) =>
        o.Model is RflEvent ev && ev.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase);

    // ---- Triggers without links + event orphans -------------------------------

    private static void CheckTriggersAndOrphans(
        List<LevelObject> objects, LintOptions options, List<LintFinding> findings)
    {
        // In-degree over the whole link graph (who links TO each UID). A mover keyframe's triggered event counts as an
        // incoming link: the keyframe fires it on arrival, and that is the ONLY way a Payload_Checkpoint is wired — Alpine
        // puts a checkpoint on the track when a cart keyframe's event_uid names it (alpinefaction
        // game_patch/multi/payload.cpp:258-298, build_track), so the final checkpoint links nothing and nothing links it.
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
            else if (o.Model is Keyframe { EventUid: > 0 } kf)
            {
                inDegree[kf.EventUid] = inDegree.GetValueOrDefault(kf.EventUid) + 1;
            }
        }

        // A KOTH capture zone is a trigger the Capture_Point_Handler links (handler -> trigger): Alpine takes the first
        // trigger among the handler's links as the hill's zone and tests players against it itself, so the zone trigger
        // needs no links of its own (alpinefaction game_patch/multi/gametype.cpp:719-731,
        // koth_capture_point_handler_trigger, used by the hill discovery at gametype.cpp:2096-2105).
        var byUid = new Dictionary<int, LevelObject>();
        foreach (LevelObject o in objects)
        {
            byUid.TryAdd(o.Uid, o);
        }

        var captureZones = new HashSet<int>();
        foreach (LevelObject o in objects)
        {
            if (o.Model is RflEvent { ClassName: "Capture_Point_Handler" } handler
                && handler.Links.FirstOrDefault(u => byUid.GetValueOrDefault(u)?.Model is Trigger) is int zone and not 0)
            {
                captureZones.Add(zone);
            }
        }

        foreach (LevelObject o in objects)
        {
            switch (o.Model)
            {
                case Trigger t when t.Links.Count == 0 && !captureZones.Contains(o.Uid):
                    findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.TriggerWithoutLinks,
                        $"Trigger {o.Uid} has no links — it will do nothing.", o.Uid, Location: o.Position));
                    break;

                case RflEvent ev when options.CheckOrphans:
                    bool hasOut = ev.Links.Count > 0;
                    bool hasIn = inDegree.GetValueOrDefault(ev.Uid) > 0;
                    if (!hasOut && !hasIn)
                    {
                        findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.EventOrphan,
                            $"{ev.ClassName} event {o.Uid} is disconnected (nothing triggers it and it links to nothing).",
                            o.Uid, Location: o.Position));
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
                    $"Nav point {n.Uid} is isolated (no connections to other nav points).", n.Uid, Location: n.Position));
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
        if (CompiledGeometryState.IsMissing(rfl))
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.MissingGeometry,
                "Level has no compiled geometry — build it before playing (the game crashes loading it)."));
        }

        if (Compiler.SurfaceBudgetState.FromGeometry(FirstContent<GeometrySection>(rfl)?.Geometry) is { Over: true } budget)
        {
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.LightmapSurfaceCap, budget.Message()!, Location: budget.Location));
        }

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
        if (holes.Count == 0)
        {
            return;
        }

        // One finding per leak edge (each carries its world location for jump-to), capped so an
        // unsealed preview build's thousands of t-joint edges cannot flood the report; the overflow
        // is summarized in a final location-less finding.
        int shown = Math.Min(holes.Count, Math.Max(0, options.MaxLeakFindings));
        for (int i = 0; i < shown; i++)
        {
            Vec3 h = holes[i];
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.GeometryLeak,
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Open edge in compiled geometry near ({h.X:0.##}, {h.Y:0.##}, {h.Z:0.##}) — the level may leak."),
                Location: h));
        }

        if (holes.Count > shown)
        {
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.GeometryLeak,
                $"{holes.Count - shown} more open edge(s) ({holes.Count} total) — run Check for Holes to mark them all."));
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
