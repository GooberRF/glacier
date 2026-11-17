using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Editing.Organic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Analysis.Recreation;

/// <summary>The scorer's output: the report (score.json) and the overlay images (file name → PNG bytes).</summary>
public sealed class RecreationScoreResult
{
    internal RecreationScoreResult(double score, JsonObject report, IReadOnlyDictionary<string, byte[]> images)
    {
        Score = score;
        Report = report;
        Images = images;
    }

    /// <summary>The headline score, 0–100.</summary>
    public double Score { get; }

    public JsonObject Report { get; }

    public IReadOnlyDictionary<string, byte[]> Images { get; }
}

/// <summary>
/// The recreation benchmark's objective scorer: how closely a candidate level recreates a reference, either another
/// level (its compiled geometry is the truth) or a digitised mockup map (truth.json: playable mask, route, area
/// markers, elevation profile). A development measuring stick, not editor UI; every metric, weight and blind spot is
/// defined in recreate/SCORING.md and mirrored in the report's <c>definitions</c>.
/// </summary>
public static class RecreationScorer
{
    /// <summary>Open-sky air counted above a floor for the volume metric (m).</summary>
    public const double AirCap = 8.0;

    /// <summary>The largest wall distance counted (m); a missing boundary counts as this.</summary>
    public const double ChamferCap = 10.0;

    /// <summary>Free tolerance before the wall and height components lose points (m): the lattice resolution.</summary>
    public const double FreeTolerance = 0.25;

    /// <summary>Distance beyond the free tolerance at which the wall and height components reach zero (m).</summary>
    public const double FalloffMetres = 4.0;

    /// <summary>The vertical voxel size and plan cell of the volume metric (m).</summary>
    public const double VoxelSize = 1.0;

    /// <summary>Radius around a mockup marker within which candidate floor counts as hitting it (m).</summary>
    public const double MarkerRadius = 1.5;

    /// <summary>Headline weights against a level truth (SCORING.md §3): plan, walls, heights, volume.</summary>
    public const double WPlan = 30, WWalls = 30, WHeights = 20, WVolume = 20;

    /// <summary>Headline weights against a mockup truth (SCORING.md §4): plan, walls, profile, route coverage, markers.</summary>
    public const double MPlan = 30, MWalls = 30, MProfile = 20, MRoute = 10, MMarkers = 10;

    // ---- public entry points ----

    /// <summary>Scores <paramref name="candidate"/>'s compiled geometry against <paramref name="truth"/>'s.</summary>
    public static RecreationScoreResult ScoreAgainstLevel(Geometry candidate, RflFile? candidateRfl, Geometry truth, RflFile? truthRfl, bool allowMirror, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(truth);
        RecreationFloor t = RecreationFloor.Measure(truth, TerrainGround.Of(truthRfl), ct);
        RecreationFloor c = RecreationFloor.Measure(candidate, TerrainGround.Of(candidateRfl), ct);
        return ScoreLevel(t, c, allowMirror, ct, RecreationFloor.StandPoints(truthRfl), RecreationFloor.StandPoints(candidateRfl));
    }

    /// <summary>Scores <paramref name="candidate"/>'s compiled geometry against a digitised mockup (truth.json).</summary>
    public static RecreationScoreResult ScoreAgainstMockup(Geometry candidate, RflFile? candidateRfl, string truthJsonPath, bool allowMirror, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        MockupTruth truth = MockupTruth.Load(truthJsonPath);
        RecreationFloor c = RecreationFloor.Measure(candidate, TerrainGround.Of(candidateRfl), ct);
        IReadOnlyList<Vec3>? cart = candidateRfl is null ? null : CartPath(candidateRfl);
        List<Vec3d> stands = RecreationFloor.StandPoints(candidateRfl);
        if (cart is not null)
        {
            stands.AddRange(cart.Select(k => new Vec3d(k.X, k.Y, k.Z)));
        }

        return ScoreMockup(truth, c, cart, allowMirror, ct, stands);
    }

    /// <summary>True when the file is a mockup truth (JSON) rather than a level.</summary>
    public static bool IsMockupTruth(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The payload cart's track: the keyframe positions of the moving group the level's first Payload_Handler links
    /// to (a link to a mover is stored as its start keyframe's UID), or null when the level has no cart.
    /// </summary>
    public static IReadOnlyList<Vec3>? CartPath(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        RflEvent? handler = rfl.Sections.Select(s => s.Content).OfType<EventsSection>().SelectMany(e => e.Events)
            .FirstOrDefault(e => e.ClassName.Equals("Payload_Handler", StringComparison.OrdinalIgnoreCase));
        if (handler is null)
        {
            return null;
        }

        foreach (GroupsSection gs in rfl.Sections.Select(s => s.Content).OfType<GroupsSection>().Where(g => g.Type == SectionType.MovingGroups))
        {
            foreach (Group g in gs.Groups)
            {
                if (g.MovingData is { Keyframes.Count: >= 2 } data && handler.Links.Contains(data.Keyframes[0].Uid))
                {
                    return data.Keyframes.Select(k => k.Position).ToList();
                }
            }
        }

        return null;
    }

    // ---- level truth ----

    /// <param name="truthStands">The truth's stand points (<see cref="RecreationFloor.StandPoints"/>): floor not reachable from them is left out of the truth. Null = every floor counts.</param>
    /// <param name="candStands">The candidate's stand points: the same rule applies to the candidate (a level scored against itself stays 100).</param>
    internal static RecreationScoreResult ScoreLevel(RecreationFloor truth, RecreationFloor cand, bool allowMirror, CancellationToken ct,
        IReadOnlyList<Vec3d>? truthStands = null, IReadOnlyList<Vec3d>? candStands = null)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reach = new JsonObject { ["rule"] = "floor kept = connected to a respawn / Player Start / item / nav point by walking, a 1.5 m step or jump, or a drop (either way); both levels (SCORING.md §1)" };
        RecreationFloor? excluded = null;
        if (truthStands is not null && truth.Reachable(truthStands) is { } tr && !tr.Kept.IsEmpty)
        {
            reach["truthSeeds"] = tr.Seeds;
            reach["truthExcludedArea"] = R(tr.Dropped.CellCount * truth.Pitch * truth.Pitch, 1);
            reach["truthExcludedSamples"] = tr.Dropped.Count;
            if (!tr.Dropped.IsEmpty)
            {
                reach["truthExcludedY"] = new JsonArray(R(tr.Dropped.MinY, 2), R(tr.Dropped.MaxY, 2));
                (double x0, double z0, double x1, double z1) = tr.Dropped.Bounds;
                reach["truthExcludedBox"] = new JsonArray(R(x0, 1), R(z0, 1), R(x1, 1), R(z1, 1));
                excluded = tr.Dropped;
            }

            truth = tr.Kept;
        }
        else
        {
            reach["truthSeeds"] = 0;
            reach["note"] = "no truth stand point lands on floor: every truth floor counts";
        }

        if (candStands is not null && cand.Reachable(candStands) is { } cr && !cr.Kept.IsEmpty)
        {
            reach["candidateSeeds"] = cr.Seeds;
            reach["candidateExcludedArea"] = R(cr.Dropped.CellCount * cand.Pitch * cand.Pitch, 1);
            cand = cr.Kept;
        }

        double[] cuts = BandCuts(truth);
        var report = new JsonObject
        {
            ["kind"] = "level",
            ["truth"] = FloorSummary(truth),
            ["candidate"] = FloorSummary(cand),
            ["reachability"] = reach,
        };
        if (truth.IsEmpty || cand.IsEmpty)
        {
            report["score"] = 0.0;
            report["note"] = truth.IsEmpty ? "the truth has no walkable floor" : "the candidate has no walkable floor";
            report["components"] = Components(0, 0, 0, 0, level: true);
            return new RecreationScoreResult(0, report, images);
        }

        // Alignment on the union plan at the truth's lattice.
        var cplan = new RecreationAlign.CandidatePlan(cand);
        PlanRaster tplan = new RecreationAlign.CandidatePlan(truth).Level(0);
        var settings = new RecreationAlign.Settings { AllowMirror = allowMirror };
        RecreationAlign.AlignResult al = RecreationAlign.Align(tplan, cplan, settings, ct);
        PlanPose pose = al.Pose;
        var swMetrics = System.Diagnostics.Stopwatch.StartNew();

        // Evaluation canvas: truth ∪ transformed candidate, 0.5 m pixels.
        Canvas cv = Canvas.Around(tplan, cplan, pose, tplan.Res);
        int[] tCell = cv.Cells(truth, null);
        int[] cCell = cv.Cells(cand, pose);
        double dy = VerticalOffset(truth, cand, tCell, cCell);

        // Plan (per band, pooled) and union.
        bool[] tU = tCell.Select(c => c >= 0).ToArray(), cU = cCell.Select(c => c >= 0).ToArray();
        Overlap union = Overlap.Of(tU, cU, cv.W, cv.H);
        var bands = new JsonArray();
        double wSum = 0, wIou = 0;
        int nb = cuts.Length + 1;
        for (int b = 0; b < nb; b++)
        {
            bool[] tb = BandMask(truth, tCell, cuts, b, 0), cb = BandMask(cand, cCell, cuts, b, dy);
            Overlap o = Overlap.Of(tb, cb, cv.W, cv.H);
            if (o.T == 0 && o.C == 0)
            {
                continue;
            }

            double w = o.Union;
            wSum += w;
            wIou += w * o.IouTol;
            var bandCt = new List<double>();
            var bandTc = new List<double>();
            Chamfer(tb, cb, cv, metresPerPixel: cv.Res, bandTc, bandCt);
            (double bct, double btc, double bsym, _) = ChamferStats(bandCt, bandTc);
            double lo = b == 0 ? double.NegativeInfinity : cuts[b - 1], hi = b == cuts.Length ? double.PositiveInfinity : cuts[b];
            bands.Add(new JsonObject
            {
                ["band"] = b,
                ["fromY"] = double.IsFinite(lo) ? R(lo - truth.MinY) : null,
                ["toY"] = double.IsFinite(hi) ? R(hi - truth.MinY) : null,
                ["truthArea"] = R(o.T * cv.Res * cv.Res),
                ["candidateArea"] = R(o.C * cv.Res * cv.Res),
                ["iou"] = R(o.Iou),
                ["iouTol"] = R(o.IouTol),
                ["wallsSymmetricMean"] = R(bsym),
                ["wallsCandidateToTruth"] = R(bct),
                ["wallsTruthToCandidate"] = R(btc),
            });
            if (nb > 1)
            {
                images[$"overlay_band_{b}.png"] = cv.Overlay(tb, cb, cplan, pose);
            }
        }

        double plan = wSum > 0 ? wIou / wSum : 0;

        // Walls on the union footprint (every height): a storey at the wrong height costs plan, heights and volume
        // points, not wall points as well. The per-band distances are reported above with no weight.
        var chamfer = new List<double>();
        var tc = new List<double>();
        Chamfer(tU, cU, cv, metresPerPixel: cv.Res, tc, chamfer);
        (double ctMean, double tcMean, double sym, double p90) = ChamferStats(chamfer, tc);
        double walls = Falloff(sym);

        // Heights.
        double[] th = Heights(truth, 0), ch = Heights(cand, dy);
        double emd = Emd(th, ch);
        double emdRelMin = Emd(th.Select(y => y - th[0]).ToArray(), ch.Select(y => y - ch[0]).ToArray());
        double heights = Falloff(emd);

        // Volume.
        Overlap vol = Volume(truth, cand, pose, dy, tplan, cplan);
        double volume = vol.IouTol;

        double score = (WPlan * plan) + (WWalls * walls) + (WHeights * heights) + (WVolume * volume);
        report["score"] = R(score, 2);
        report["components"] = Components(plan, walls, heights, volume, level: true);
        report["alignment"] = Alignment(pose, al.Iou, dy);
        report["plan"] = new JsonObject
        {
            ["unionIou"] = R(union.Iou),
            ["unionIouTol"] = R(union.IouTol),
            ["bandIouTol"] = R(plan),
            ["bands"] = bands,
        };
        report["walls"] = new JsonObject
        {
            ["meanCandidateToTruth"] = R(ctMean),
            ["meanTruthToCandidate"] = R(tcMean),
            ["symmetricMean"] = R(sym),
            ["p90"] = R(p90),
            ["on"] = "union footprint (all heights); per-band distances in plan.bands (unweighted)",
        };
        report["heights"] = new JsonObject
        {
            ["emd"] = R(emd),
            ["emdRelativeToMinimum"] = R(emdRelMin),
            ["verticalOffset"] = R(dy),
        };
        report["volume"] = new JsonObject
        {
            ["truthVoxels"] = vol.T,
            ["candidateVoxels"] = vol.C,
            ["iou"] = R(vol.Iou),
            ["iouTol"] = R(vol.IouTol),
        };
        bool[]? ex = excluded is null ? null : cv.Cells(excluded, null).Select(c => c >= 0).ToArray();
        images["overlay_plan.png"] = cv.Overlay(tU, cU, cplan, pose, ex);
        report["timingSeconds"] = Timing(settings, swMetrics);
        return new RecreationScoreResult(score, report, images);
    }

    // ---- mockup truth ----

    /// <param name="candStands">
    /// The candidate's stand points (respawns, Player Start, items, nav points, cart keyframes): when given, only floor a
    /// player reaches from them counts (SCORING.md §1, mockup rule: walking, steps and jumps up to 1.5 m, falls of at most
    /// <see cref="RecreationFloor.MaxFall"/>, one way), and floor over <see cref="RecreationFloor.KillDepth"/> below the
    /// lowest stand floor is a kill zone. Null = every floor counts.
    /// </param>
    internal static RecreationScoreResult ScoreMockup(MockupTruth truth, RecreationFloor cand, IReadOnlyList<Vec3>? cart, bool allowMirror, CancellationToken ct,
        IReadOnlyList<Vec3d>? candStands = null)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        PlanRaster tplan = truth.Working();
        var reach = new JsonObject { ["rule"] = FormattableString.Invariant($"candidate floor kept = reached from a respawn / Player Start / item / nav point / cart keyframe by walking, a {RecreationFloor.JumpUp} m step or jump, or a fall of at most {RecreationFloor.MaxFall} m (one way); floor over {RecreationFloor.KillDepth} m below the lowest stand floor is a kill zone (SCORING.md §1)") };
        if (candStands is not null && cand.Reachable(candStands, oneWay: true, RecreationFloor.MaxFall, RecreationFloor.KillDepth) is { } cr && !cr.Kept.IsEmpty)
        {
            reach["candidateSeeds"] = cr.Seeds;
            reach["candidateExcludedArea"] = R(cr.Dropped.CellCount * cand.Pitch * cand.Pitch, 1);
            if (!cr.Dropped.IsEmpty)
            {
                reach["candidateExcludedY"] = new JsonArray(R(cr.Dropped.MinY, 2), R(cr.Dropped.MaxY, 2));
            }

            cand = cr.Kept;
        }
        else
        {
            reach["candidateSeeds"] = 0;
            reach["note"] = "no candidate stand point lands on floor: every candidate floor counts";
        }
        var report = new JsonObject
        {
            ["kind"] = "mockup",
            ["truth"] = new JsonObject { ["name"] = truth.Name, ["playablePixels"] = truth.PlayablePixels, ["routeLengthPixels"] = R(truth.RouteLength, 1) },
            ["candidate"] = FloorSummary(cand),
            ["reachability"] = reach,
        };
        if (cand.IsEmpty)
        {
            report["score"] = 0.0;
            report["note"] = "the candidate has no walkable floor";
            report["components"] = Components(0, 0, 0, 0, level: false);
            return new RecreationScoreResult(0, report, images);
        }

        var cplan = new RecreationAlign.CandidatePlan(cand);
        double s0 = Math.Sqrt(truth.PlayablePixels / Math.Max(1e-6, cplan.Area));
        var settings = new RecreationAlign.Settings
        {
            AllowMirror = allowMirror,
            CoarseYawStep = 4,
            Scales = Enumerable.Range(-4, 9).Select(k => s0 * Math.Pow(2, k / 4.0)).ToArray(),
            ScaleRefine = Enumerable.Range(-2, 5).Select(j => Math.Pow(2, j / 16.0)).ToArray(),
        };
        RecreationAlign.AlignResult al = RecreationAlign.Align(tplan, cplan, settings, ct);
        PlanPose pose = al.Pose;
        var swMetrics = System.Diagnostics.Stopwatch.StartNew();
        double mPerUnit = 1 / pose.Scale;

        Canvas cv = Canvas.Around(tplan, cplan, pose, tplan.Res);
        bool[] tU = cv.Sample(tplan);
        int[] cCell = cv.Cells(cand, pose);
        bool[] cU = cCell.Select(c => c >= 0).ToArray();
        Overlap plan = Overlap.Of(tU, cU, cv.W, cv.H);
        var chamfer = new List<double>();
        var tcd = new List<double>();
        Chamfer(tU, cU, cv, cv.Res * mPerUnit, tcd, chamfer);
        (double ctMean, double tcMean, double sym, double p90) = ChamferStats(chamfer, tcd);
        double walls = Falloff(sym);

        // Route coverage: stations every 0.5 m (candidate metres) along the route.
        double step = 0.5 * pose.Scale;
        int stations = Math.Max(2, (int)Math.Ceiling(truth.RouteLength / step) + 1);
        int onFloor = 0;
        for (int i = 0; i < stations; i++)
        {
            (double a, double b) = truth.RouteAt((double)i / (stations - 1));
            (double ca, double cb) = pose.Inverse(a, b);
            if (cand.Cell(ca, -cb) >= 0)
            {
                onFloor++;
            }
        }

        double coverage = (double)onFloor / stations;

        // Markers.
        var markers = new JsonArray();
        int hits = 0;
        var markerHit = new List<(double A, double B, bool Hit)>();
        foreach ((string label, double a, double b) in truth.Points)
        {
            (double ca, double cb) = pose.Inverse(a, b);
            bool hit = FloorNear(cand, ca, -cb, MarkerRadius);
            hits += hit ? 1 : 0;
            markerHit.Add((a, b, hit));
            markers.Add(new JsonObject { ["label"] = label, ["onFloor"] = hit });
        }

        double markerScore = truth.Points.Count == 0 ? 0 : (double)hits / truth.Points.Count;

        // Route elevation profile: 51 stations, Pearson correlation with the chart's relative heights.
        var cy = new List<double>();
        var ty = new List<double>();
        double? prev = null;
        for (int i = 0; i <= 50; i++)
        {
            double t = i / 50.0;
            (double a, double b) = truth.RouteAt(t);
            (double ca, double cb) = pose.Inverse(a, b);
            ReadOnlySpan<FloorPoint> pts = cand.In(cand.Cell(ca, -cb));
            if (pts.Length == 0)
            {
                continue;
            }

            double y = pts[0].Y;
            if (prev is double p)
            {
                foreach (FloorPoint fp in pts)
                {
                    if (Math.Abs(fp.Y - p) < Math.Abs(y - p))
                    {
                        y = fp.Y;
                    }
                }
            }

            prev = y;
            cy.Add(y);
            ty.Add(truth.ProfileAt(t));
        }

        double r = cy.Count >= 8 ? Pearson(cy, ty) : 0;
        double profile = Math.Max(0, r);
        double rise = cy.Count >= 6 ? cy.TakeLast(3).Average() - cy.Take(3).Average() : 0;

        // Cart path vs route (reported, not scored).
        JsonObject? cartJson = null;
        List<(double A, double B)>? cartPlan = null;
        if (cart is { Count: >= 2 })
        {
            cartPlan = cart.Select(k => pose.Forward(k.X, -k.Z)).ToList();
            var dists = new List<double>();
            for (int i = 0; i + 1 < cartPlan.Count; i++)
            {
                (double a0, double b0) = cartPlan[i];
                (double a1, double b1) = cartPlan[i + 1];
                double len = Math.Sqrt(((a1 - a0) * (a1 - a0)) + ((b1 - b0) * (b1 - b0)));
                int n = Math.Max(1, (int)Math.Ceiling(len / pose.Scale));
                for (int j = 0; j <= n; j++)
                {
                    double f = (double)j / n;
                    dists.Add(truth.DistanceToRoute(a0 + ((a1 - a0) * f), b0 + ((b1 - b0) * f)) * mPerUnit);
                }
            }

            cartJson = new JsonObject
            {
                ["keyframes"] = cart.Count,
                ["meanDistanceToRoute"] = R(dists.Average()),
                ["maxDistanceToRoute"] = R(dists.Max()),
            };
        }

        double score = (MPlan * plan.IouTol) + (MWalls * walls) + (MProfile * profile) + (MRoute * coverage) + (MMarkers * markerScore);
        report["score"] = R(score, 2);
        report["components"] = new JsonObject
        {
            ["plan"] = Component(plan.IouTol, MPlan),
            ["walls"] = Component(walls, MWalls),
            ["profile"] = Component(profile, MProfile),
            ["routeCoverage"] = Component(coverage, MRoute),
            ["markers"] = Component(markerScore, MMarkers),
        };
        report["alignment"] = Alignment(pose, al.Iou, null);
        report["alignment"]!["metresPerPixel"] = R(mPerUnit);
        report["alignment"]!["scaleRelativeToAreaMatch"] = R(pose.Scale / s0);
        report["plan"] = new JsonObject { ["iou"] = R(plan.Iou), ["iouTol"] = R(plan.IouTol), ["candidateArea"] = R(cplan.Area, 1) };
        report["walls"] = new JsonObject
        {
            ["meanCandidateToTruth"] = R(ctMean),
            ["meanTruthToCandidate"] = R(tcMean),
            ["symmetricMean"] = R(sym),
            ["p90"] = R(p90),
        };
        report["route"] = new JsonObject
        {
            ["coverage"] = R(coverage),
            ["profileStationsOnFloor"] = cy.Count,
            ["profileCorrelation"] = R(r),
            ["riseStartToEnd"] = R(rise),
            ["markers"] = markers,
            ["markersOnFloor"] = hits,
            ["cartPath"] = cartJson,
        };
        images["overlay_plan.png"] = cv.Overlay(tU, cU, cplan, pose);
        images["overlay_mockup.png"] = MockupOverlay(truth, cand, pose, cartPlan, markerHit);
        report["timingSeconds"] = Timing(settings, swMetrics);
        return new RecreationScoreResult(score, report, images);
    }

    // ---- metric helpers ----

    /// <summary>
    /// Height-band cuts from the truth's floor heights: 0.25 m bins; a bin is sparse below 0.5 % of all samples; each
    /// run of at least 4 sparse bins (1 m) between denser bins gives a cut at its middle (at most 7 cuts, longest
    /// runs kept).
    /// </summary>
    internal static double[] BandCuts(RecreationFloor truth)
    {
        if (truth.IsEmpty)
        {
            return Array.Empty<double>();
        }

        const double Bin = 0.25;
        int n = Math.Max(1, (int)Math.Floor((truth.MaxY - truth.MinY) / Bin) + 1);
        var counts = new int[n];
        foreach (FloorPoint p in truth.Points)
        {
            counts[Math.Min(n - 1, (int)Math.Floor((p.Y - truth.MinY) / Bin))]++;
        }

        double sparse = 0.005 * truth.Count;
        var runs = new List<(int Start, int Len)>();
        int i = 0;
        while (i < n)
        {
            if (counts[i] >= sparse)
            {
                i++;
                continue;
            }

            int s = i;
            while (i < n && counts[i] < sparse)
            {
                i++;
            }

            if (s > 0 && i < n && i - s >= 4)
            {
                runs.Add((s, i - s));
            }
        }

        return runs.OrderByDescending(r => r.Len).Take(7)
            .Select(r => truth.MinY + ((r.Start + (r.Len / 2.0)) * Bin)).OrderBy(c => c).ToArray();
    }

    private static int Band(double y, double[] cuts)
    {
        int b = 0;
        while (b < cuts.Length && y >= cuts[b])
        {
            b++;
        }

        return b;
    }

    private static bool[] BandMask(RecreationFloor floor, int[] cells, double[] cuts, int band, double dy)
    {
        var m = new bool[cells.Length];
        for (int p = 0; p < cells.Length; p++)
        {
            foreach (FloorPoint fp in floor.In(cells[p]))
            {
                if (Band(fp.Y + dy, cuts) == band)
                {
                    m[p] = true;
                    break;
                }
            }
        }

        return m;
    }

    /// <summary>Median over overlapping pixels of (truth lowest floor − candidate lowest floor); else the medians' difference.</summary>
    private static double VerticalOffset(RecreationFloor truth, RecreationFloor cand, int[] tCell, int[] cCell)
    {
        var d = new List<double>();
        for (int p = 0; p < tCell.Length; p++)
        {
            if (tCell[p] >= 0 && cCell[p] >= 0)
            {
                d.Add(truth.In(tCell[p])[0].Y - cand.In(cCell[p])[0].Y);
            }
        }

        if (d.Count > 0)
        {
            return SpaceSampler.Median(d);
        }

        return SpaceSampler.Median(Heights(truth, 0)) - SpaceSampler.Median(Heights(cand, 0));
    }

    private static double[] Heights(RecreationFloor f, double dy)
    {
        var h = new double[f.Count];
        int i = 0;
        foreach (FloorPoint p in f.Points)
        {
            h[i++] = p.Y + dy;
        }

        Array.Sort(h);
        return h;
    }

    /// <summary>1-D earth mover's distance between two sorted samples (mean |quantile difference| over 1000 quantiles).</summary>
    internal static double Emd(double[] a, double[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return ChamferCap;
        }

        const int Q = 1000;
        double s = 0;
        for (int q = 0; q < Q; q++)
        {
            double f = (q + 0.5) / Q;
            s += Math.Abs(a[Math.Min(a.Length - 1, (int)(f * a.Length))] - b[Math.Min(b.Length - 1, (int)(f * b.Length))]);
        }

        return s / Q;
    }

    private static double Falloff(double metres) => Math.Clamp(1 - (Math.Max(0, metres - FreeTolerance) / FalloffMetres), 0, 1);

    private static double Pearson(List<double> x, List<double> y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Count; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }

        return sxx < 1e-9 || syy < 1e-9 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    private static bool FloorNear(RecreationFloor f, double x, double z, double radius)
    {
        for (double dx = -radius; dx <= radius + 1e-9; dx += f.Pitch)
        {
            for (double dz = -radius; dz <= radius + 1e-9; dz += f.Pitch)
            {
                if ((dx * dx) + (dz * dz) <= (radius * radius) + 1e-9 && f.Cell(x + dx, z + dz) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Wall boundaries: mask pixels with a 4-neighbour outside the mask (or the raster).</summary>
    internal static bool[] Boundary(bool[] m, int w, int h)
    {
        var b = new bool[m.Length];
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                int p = (v * w) + u;
                b[p] = m[p] && (u == 0 || v == 0 || u == w - 1 || v == h - 1 || !m[p - 1] || !m[p + 1] || !m[p - w] || !m[p + w]);
            }
        }

        return b;
    }

    /// <summary>Distance (pixels) to the nearest seed, 8-neighbour chamfer (1, √2); +∞ without seeds.</summary>
    internal static double[] Distance(bool[] seeds, int w, int h)
    {
        var d = new double[seeds.Length];
        for (int p = 0; p < d.Length; p++)
        {
            d[p] = seeds[p] ? 0 : double.PositiveInfinity;
        }

        const double D = 1.4142135623730951;
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                int p = (v * w) + u;
                if (u > 0) d[p] = Math.Min(d[p], d[p - 1] + 1);
                if (v > 0) d[p] = Math.Min(d[p], d[p - w] + 1);
                if (u > 0 && v > 0) d[p] = Math.Min(d[p], d[p - w - 1] + D);
                if (u < w - 1 && v > 0) d[p] = Math.Min(d[p], d[p - w + 1] + D);
            }
        }

        for (int v = h - 1; v >= 0; v--)
        {
            for (int u = w - 1; u >= 0; u--)
            {
                int p = (v * w) + u;
                if (u < w - 1) d[p] = Math.Min(d[p], d[p + 1] + 1);
                if (v < h - 1) d[p] = Math.Min(d[p], d[p + w] + 1);
                if (u < w - 1 && v < h - 1) d[p] = Math.Min(d[p], d[p + w + 1] + D);
                if (u > 0 && v < h - 1) d[p] = Math.Min(d[p], d[p + w - 1] + D);
            }
        }

        return d;
    }

    /// <summary>Adds the boundary-to-boundary distances (metres, capped) both ways for one pair of masks.</summary>
    private static void Chamfer(bool[] t, bool[] c, Canvas cv, double metresPerPixel, List<double> truthToCand, List<double> candToTruth)
    {
        bool[] tb = Boundary(t, cv.W, cv.H), cb = Boundary(c, cv.W, cv.H);
        double[] dt = Distance(tb, cv.W, cv.H), dc = Distance(cb, cv.W, cv.H);
        for (int p = 0; p < tb.Length; p++)
        {
            if (cb[p])
            {
                candToTruth.Add(Math.Min(ChamferCap, dt[p] * metresPerPixel));
            }

            if (tb[p])
            {
                truthToCand.Add(Math.Min(ChamferCap, dc[p] * metresPerPixel));
            }
        }
    }

    private static (double CtMean, double TcMean, double Sym, double P90) ChamferStats(List<double> candToTruth, List<double> truthToCand)
    {
        double ct = candToTruth.Count > 0 ? candToTruth.Average() : ChamferCap;
        double tc = truthToCand.Count > 0 ? truthToCand.Average() : ChamferCap;
        double[] all = candToTruth.Concat(truthToCand).OrderBy(x => x).ToArray();
        double p90 = all.Length > 0 ? Percentiles.Type7(all, 0.9) : ChamferCap;
        return (ct, tc, (ct + tc) / 2, p90);
    }

    /// <summary>
    /// Air voxels (1 m plan cells × 1 m layers): a voxel is air when its centre lies between a floor sample of its plan
    /// cell and that floor plus its headroom (capped at <see cref="AirCap"/>); the candidate is shifted by the vertical
    /// offset. Each 1 m plan cell reads the 0.5 m floor cell under its centre.
    /// </summary>
    private static Overlap Volume(RecreationFloor truth, RecreationFloor cand, PlanPose pose, double dy, PlanRaster tplan, RecreationAlign.CandidatePlan cplan)
    {
        Canvas cv = Canvas.Around(tplan, cplan, pose, VoxelSize);
        int[] tCell = cv.Cells(truth, null), cCell = cv.Cells(cand, pose);
        double lo = Math.Floor(Math.Min(truth.MinY, cand.MinY + dy));
        double hi = Math.Max(truth.MaxY, cand.MaxY + dy) + AirCap;
        int layers = Math.Min(400, Math.Max(1, (int)Math.Ceiling((hi - lo) / VoxelSize)));
        int plane = cv.W * cv.H;
        var tv = new bool[plane * layers];
        var cvx = new bool[plane * layers];
        void Fill(RecreationFloor f, int[] cells, double shift, bool[] vox)
        {
            for (int p = 0; p < plane; p++)
            {
                foreach (FloorPoint fp in f.In(cells[p]))
                {
                    double y0 = fp.Y + shift, y1 = y0 + Math.Min(fp.Headroom, AirCap);
                    int j0 = Math.Max(0, (int)Math.Ceiling(((y0 - lo) / VoxelSize) - 0.5));
                    int j1 = Math.Min(layers - 1, (int)Math.Floor(((y1 - lo) / VoxelSize) - 0.5));
                    for (int j = j0; j <= j1; j++)
                    {
                        vox[(j * plane) + p] = true;
                    }
                }
            }
        }

        Fill(truth, tCell, 0, tv);
        Fill(cand, cCell, dy, cvx);
        return Overlap.Of3(tv, cvx, cv.W, cv.H, layers);
    }

    // ---- report helpers ----

    private static JsonObject Timing(RecreationAlign.Settings settings, System.Diagnostics.Stopwatch metrics)
    {
        var o = new JsonObject();
        foreach ((string k, double v) in settings.Timings)
        {
            o[k] = R(v, 2);
        }

        o["metrics"] = R(metrics.Elapsed.TotalSeconds, 2);
        return o;
    }

    private static double R(double v, int digits = 4) => double.IsFinite(v) ? Math.Round(v, digits) : -1;

    private static JsonObject Component(double value, double weight) => new()
    {
        ["value"] = R(value),
        ["weight"] = weight,
        ["points"] = R(value * weight, 2),
    };

    private static JsonObject Components(double plan, double walls, double heights, double volumeOrRoute, bool level) => level
        ? new JsonObject
        {
            ["plan"] = Component(plan, WPlan),
            ["walls"] = Component(walls, WWalls),
            ["heights"] = Component(heights, WHeights),
            ["volume"] = Component(volumeOrRoute, WVolume),
        }
        : new JsonObject
        {
            ["plan"] = Component(plan, MPlan),
            ["walls"] = Component(walls, MWalls),
            ["profile"] = Component(heights, MProfile),
            ["routeCoverage"] = Component(volumeOrRoute, MRoute),
            ["markers"] = Component(0, MMarkers),
        };

    private static JsonObject Alignment(PlanPose pose, double iou, double? dy) => new()
    {
        ["yawDegrees"] = R(pose.YawDeg, 2),
        ["mirrored"] = pose.Mirror,
        ["scale"] = R(pose.Scale, 5),
        ["translation"] = new JsonArray(R(pose.Ta, 3), R(pose.Tb, 3)),
        ["verticalOffset"] = dy is double d ? R(d, 3) : null,
        ["searchIou"] = R(iou),
        ["frame"] = "candidate plan (x, -z) -> mirror? -> rotate yaw -> scale -> translate = truth plan (level: x, -z metres; mockup: image pixels)",
    };

    private static JsonObject FloorSummary(RecreationFloor f) => new()
    {
        ["floorSamples"] = f.Count,
        ["planArea"] = R(f.CellCount * f.Pitch * f.Pitch, 1),
        ["minY"] = f.IsEmpty ? 0 : R(f.MinY, 2),
        ["heightRange"] = f.IsEmpty ? 0 : R(f.MaxY - f.MinY, 2),
        ["pitch"] = f.Pitch,
    };

    // ---- overlays ----

    private static byte[] MockupOverlay(MockupTruth truth, RecreationFloor cand, PlanPose pose, List<(double A, double B)>? cart, List<(double A, double B, bool Hit)> markers)
    {
        (int x0, int y0, int w, int h) = truth.MapCrop;
        var img = new Rgba(w, h);
        byte[]? photo = truth.ImageRgba;
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                int ix = x0 + u, iy = y0 + v;
                byte r = 40, g = 40, b = 40;
                if (photo is not null && ix < truth.ImageWidth && iy < truth.ImageHeight)
                {
                    int q = ((iy * truth.ImageWidth) + ix) * 4;
                    r = (byte)(photo[q] / 2);
                    g = (byte)(photo[q + 1] / 2);
                    b = (byte)(photo[q + 2] / 2);
                }

                (double ca, double cb) = pose.Inverse(ix + 0.5, iy + 0.5);
                if (cand.Cell(ca, -cb) >= 0)
                {
                    r = (byte)((r * 0.45) + (0 * 0.55));
                    g = (byte)((g * 0.45) + (210 * 0.55));
                    b = (byte)((b * 0.45) + (255 * 0.55));
                }

                img.Set(u, v, r, g, b);
            }
        }

        for (int i = 0; i + 1 < truth.Route.Count; i++)
        {
            img.Line(truth.Route[i].A - x0, truth.Route[i].B - y0, truth.Route[i + 1].A - x0, truth.Route[i + 1].B - y0, 255, 220, 0, 2);
        }

        if (cart is not null)
        {
            for (int i = 0; i + 1 < cart.Count; i++)
            {
                img.Line(cart[i].A - x0, cart[i].B - y0, cart[i + 1].A - x0, cart[i + 1].B - y0, 255, 0, 255, 2);
            }
        }

        foreach ((double a, double b, bool hit) in markers)
        {
            img.Square(a - x0, b - y0, 6, hit ? (byte)0 : (byte)255, hit ? (byte)255 : (byte)40, 40);
        }

        return img.Png();
    }

    /// <summary>An RGBA image with the few drawing operations the overlays need.</summary>
    internal sealed class Rgba
    {
        public Rgba(int w, int h)
        {
            W = w;
            H = h;
            Px = new byte[w * h * 4];
        }

        public int W { get; }

        public int H { get; }

        public byte[] Px { get; }

        public void Set(int u, int v, byte r, byte g, byte b)
        {
            if ((uint)u >= (uint)W || (uint)v >= (uint)H)
            {
                return;
            }

            int q = ((v * W) + u) * 4;
            Px[q] = r;
            Px[q + 1] = g;
            Px[q + 2] = b;
            Px[q + 3] = 255;
        }

        public void Line(double ax, double ay, double bx, double by, byte r, byte g, byte b, int thick = 1)
        {
            double len = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
            int n = Math.Max(1, (int)Math.Ceiling(len));
            for (int i = 0; i <= n; i++)
            {
                double f = (double)i / n;
                Square(ax + ((bx - ax) * f), ay + ((by - ay) * f), thick, r, g, b);
            }
        }

        public void Square(double cx, double cy, int size, byte r, byte g, byte b)
        {
            int u0 = (int)Math.Floor(cx - (size / 2.0)), v0 = (int)Math.Floor(cy - (size / 2.0));
            for (int v = v0; v < v0 + size; v++)
            {
                for (int u = u0; u < u0 + size; u++)
                {
                    Set(u, v, r, g, b);
                }
            }
        }

        public byte[] Png() => Ged.Core.IO.Tex.PngWriter.Encode(W, H, Px);
    }

    /// <summary>Plain and tolerant overlap of two masks.</summary>
    internal readonly record struct Overlap(long T, long C, long I, double Iou, double IouTol)
    {
        public long Union => T + C - I;

        /// <summary>
        /// IoU, and the tolerant IoU: precision P = share of candidate pixels within one pixel (8-neighbourhood) of
        /// truth, recall R = share of truth pixels within one pixel of candidate, IoU_tol = P·R / (P + R − P·R) (the
        /// IoU that P and R would give exactly).
        /// </summary>
        public static Overlap Of(bool[] t, bool[] c, int w, int h)
        {
            bool[] dt = Dilate(t, w, h, 1), dc = Dilate(c, w, h, 1);
            return Make(t, c, dt, dc);
        }

        public static Overlap Of3(bool[] t, bool[] c, int w, int h, int layers)
        {
            bool[] dt = Dilate(t, w, h, layers), dc = Dilate(c, w, h, layers);
            return Make(t, c, dt, dc);
        }

        private static Overlap Make(bool[] t, bool[] c, bool[] dt, bool[] dc)
        {
            long nt = 0, nc = 0, ni = 0, cin = 0, tin = 0;
            for (int p = 0; p < t.Length; p++)
            {
                if (t[p])
                {
                    nt++;
                    if (dc[p]) tin++;
                }

                if (c[p])
                {
                    nc++;
                    if (dt[p]) cin++;
                    if (t[p]) ni++;
                }
            }

            double iou = nt + nc - ni > 0 ? (double)ni / (nt + nc - ni) : 0;
            double pr = nc > 0 ? (double)cin / nc : 0, rc = nt > 0 ? (double)tin / nt : 0;
            double tol = pr > 0 && rc > 0 ? pr * rc / (pr + rc - (pr * rc)) : 0;
            return new Overlap(nt, nc, ni, iou, tol);
        }

        /// <summary>3×3 (×3 when <paramref name="layers"/> &gt; 1) dilation of a w×h×layers mask.</summary>
        private static bool[] Dilate(bool[] m, int w, int h, int layers)
        {
            int plane = w * h;
            var o = new bool[m.Length];
            for (int j = 0; j < layers; j++)
            {
                for (int v = 0; v < h; v++)
                {
                    for (int u = 0; u < w; u++)
                    {
                        int p = (j * plane) + (v * w) + u;
                        if (!m[p])
                        {
                            continue;
                        }

                        for (int dj = layers > 1 ? -1 : 0; dj <= (layers > 1 ? 1 : 0); dj++)
                        {
                            int jj = j + dj;
                            if ((uint)jj >= (uint)layers)
                            {
                                continue;
                            }

                            for (int dv = -1; dv <= 1; dv++)
                            {
                                int vv = v + dv;
                                if ((uint)vv >= (uint)h)
                                {
                                    continue;
                                }

                                for (int du = -1; du <= 1; du++)
                                {
                                    int uu = u + du;
                                    if ((uint)uu < (uint)w)
                                    {
                                        o[(jj * plane) + (vv * w) + uu] = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return o;
        }
    }

    /// <summary>The evaluation raster: the truth plan's frame, grown to hold the transformed candidate.</summary>
    internal sealed class Canvas
    {
        private Canvas(int w, int h, double a0, double b0, double res)
        {
            W = w;
            H = h;
            A0 = a0;
            B0 = b0;
            Res = res;
        }

        public int W { get; }

        public int H { get; }

        public double A0 { get; }

        public double B0 { get; }

        public double Res { get; }

        public static Canvas Around(PlanRaster truth, RecreationAlign.CandidatePlan cand, PlanPose pose, double res)
        {
            double minA = truth.A0, minB = truth.B0, maxA = truth.A0 + (truth.W * truth.Res), maxB = truth.B0 + (truth.H * truth.Res);
            foreach ((double a, double b) in cand.Corners)
            {
                (double ta, double tb) = pose.Forward(a, b);
                minA = Math.Min(minA, ta);
                minB = Math.Min(minB, tb);
                maxA = Math.Max(maxA, ta);
                maxB = Math.Max(maxB, tb);
            }

            minA = truth.A0 + (Math.Floor((minA - truth.A0) / res) * res) - res;
            minB = truth.B0 + (Math.Floor((minB - truth.B0) / res) * res) - res;
            int w = Math.Min(8192, (int)Math.Ceiling((maxA - minA) / res) + 2), h = Math.Min(8192, (int)Math.Ceiling((maxB - minB) / res) + 2);
            return new Canvas(w, h, minA, minB, res);
        }

        /// <summary>The floor cell under each pixel centre (truth: <paramref name="pose"/> null; candidate: through the pose).</summary>
        public int[] Cells(RecreationFloor floor, PlanPose? pose)
        {
            var cells = new int[W * H];
            for (int v = 0; v < H; v++)
            {
                double b = B0 + ((v + 0.5) * Res);
                for (int u = 0; u < W; u++)
                {
                    double a = A0 + ((u + 0.5) * Res);
                    (double ca, double cb) = pose is PlanPose p ? p.Inverse(a, b) : (a, b);
                    cells[(v * W) + u] = floor.Cell(ca, -cb);
                }
            }

            return cells;
        }

        /// <summary>A truth raster sampled at the pixel centres (coverage ≥ ½).</summary>
        public bool[] Sample(PlanRaster r)
        {
            var m = new bool[W * H];
            for (int v = 0; v < H; v++)
            {
                for (int u = 0; u < W; u++)
                {
                    m[(v * W) + u] = RecreationAlign.CandidatePlan.At(r, A0 + ((u + 0.5) * Res), B0 + ((v + 0.5) * Res)) >= 0.5f;
                }
            }

            return m;
        }

        /// <summary>
        /// Truth grey, candidate cyan, overlap white, truth floor left out as unreachable purple (where nothing else is);
        /// the candidate's transformed frame outlined, its north drawn yellow.
        /// </summary>
        public byte[] Overlay(bool[] t, bool[] c, RecreationAlign.CandidatePlan cand, PlanPose pose, bool[]? excluded = null)
        {
            int k = Math.Max(1, Math.Min(4, 800 / Math.Max(W, H)));
            var img = new Rgba(W * k, H * k);
            for (int v = 0; v < img.H; v++)
            {
                for (int u = 0; u < img.W; u++)
                {
                    int p = ((v / k) * W) + (u / k);
                    (byte r, byte g, byte b) = (t[p], c[p]) switch
                    {
                        (true, true) => ((byte)255, (byte)255, (byte)255),
                        (true, false) => ((byte)120, (byte)120, (byte)120),
                        (false, true) => ((byte)0, (byte)200, (byte)255),
                        _ when excluded is not null && excluded[p] => ((byte)150, (byte)60, (byte)190),
                        _ => ((byte)18, (byte)18, (byte)22),
                    };
                    img.Set(u, v, r, g, b);
                }
            }

            (double X, double Y) Px((double A, double B) q)
            {
                (double a, double b) = pose.Forward(q.A, q.B);
                return ((a - A0) / Res * k, (b - B0) / Res * k);
            }

            (double A, double B)[] cn = cand.Corners;
            var ring = new[] { cn[0], cn[1], cn[3], cn[2], cn[0] };
            for (int i = 0; i < 4; i++)
            {
                (double x0, double y0) = Px(ring[i]);
                (double x1, double y1) = Px(ring[i + 1]);
                img.Line(x0, y0, x1, y1, 0, 110, 150);
            }

            double ca = (cn[0].A + cn[3].A) / 2, cb = (cn[0].B + cn[3].B) / 2, len = Math.Abs(cn[3].B - cn[0].B) / 4;
            (double nx0, double ny0) = Px((ca, cb));
            (double nx1, double ny1) = Px((ca, cb - len));
            img.Line(nx0, ny0, nx1, ny1, 255, 220, 0, 2);
            return img.Png();
        }
    }

    // ---- mockup truth file ----

    /// <summary>A digitised mockup map: truth.json and the playable mask it names (all coordinates in image pixels).</summary>
    internal sealed class MockupTruth
    {
        public string Name { get; private init; } = string.Empty;

        public int ImageWidth { get; private init; }

        public int ImageHeight { get; private init; }

        public (int X, int Y, int W, int H) MapCrop { get; private init; }

        public bool[] Mask { get; private init; } = Array.Empty<bool>();

        public long PlayablePixels { get; private init; }

        public List<(double A, double B)> Route { get; private init; } = new();

        public List<(string Label, double A, double B)> Points { get; private init; } = new();

        public List<(double T, double H)> Profile { get; private init; } = new();

        public byte[]? ImageRgba { get; private init; }

        private double[] _cum = Array.Empty<double>();

        public double RouteLength => _cum.Length > 0 ? _cum[^1] : 0;

        public static MockupTruth Load(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            JsonNode root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty truth file.");
            int[] crop = root["mapCrop"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
            (int mw, int mh, byte[] mask) = Decode(Path.Combine(dir, root["playableMask"]!.GetValue<string>()));
            var m = new bool[mw * mh];
            long count = 0;
            for (int p = 0; p < m.Length; p++)
            {
                m[p] = mask[p * 4] >= 128;
                count += m[p] ? 1 : 0;
            }

            byte[]? photo = null;
            if (root["image"]?.GetValue<string>() is string img && File.Exists(Path.Combine(dir, img)))
            {
                (int pw, int ph, byte[] px) = Decode(Path.Combine(dir, img));
                photo = pw == mw && ph == mh ? px : null;
            }

            var route = root["route"]!.AsArray().Select(n => (n![0]!.GetValue<double>(), n[1]!.GetValue<double>())).ToList();
            var points = root["points"]!.AsArray().Select(n => (n!["label"]!.GetValue<string>(), n["at"]![0]!.GetValue<double>(), n["at"]![1]!.GetValue<double>())).ToList();
            var profile = root["profile"]!["curve"]!.AsArray().Select(n => (n![0]!.GetValue<double>(), n[1]!.GetValue<double>())).OrderBy(p => p.Item1).ToList();
            var t = new MockupTruth
            {
                Name = root["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path),
                ImageWidth = mw,
                ImageHeight = mh,
                MapCrop = (crop[0], crop[1], crop[2], crop[3]),
                Mask = m,
                PlayablePixels = count,
                Route = route,
                Points = points,
                Profile = profile,
                ImageRgba = photo,
            };
            t._cum = new double[route.Count];
            for (int i = 1; i < route.Count; i++)
            {
                t._cum[i] = t._cum[i - 1] + Math.Sqrt(Math.Pow(route[i].Item1 - route[i - 1].Item1, 2) + Math.Pow(route[i].Item2 - route[i - 1].Item2, 2));
            }

            return t;
        }

        private static (int W, int H, byte[] Rgba) Decode(string path)
        {
            StbImageSharp.ImageResult r = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            return (r.Width, r.Height, r.Data);
        }

        /// <summary>The playable mask over the map crop at 2 image pixels per raster pixel (coverage).</summary>
        public PlanRaster Working()
        {
            (int x0, int y0, int w, int h) = MapCrop;
            var full = new PlanRaster(w, h, x0, y0, 1);
            for (int v = 0; v < h; v++)
            {
                for (int u = 0; u < w; u++)
                {
                    int ix = x0 + u, iy = y0 + v;
                    full.V[(v * w) + u] = ix < ImageWidth && iy < ImageHeight && Mask[(iy * ImageWidth) + ix] ? 1f : 0f;
                }
            }

            return full.Downsample(2);
        }

        /// <summary>The route point at arc-length fraction <paramref name="t"/>.</summary>
        public (double A, double B) RouteAt(double t)
        {
            double s = Math.Clamp(t, 0, 1) * RouteLength;
            for (int i = 1; i < Route.Count; i++)
            {
                if (_cum[i] >= s || i == Route.Count - 1)
                {
                    double seg = _cum[i] - _cum[i - 1];
                    double f = seg > 0 ? Math.Clamp((s - _cum[i - 1]) / seg, 0, 1) : 0;
                    return (Route[i - 1].A + ((Route[i].A - Route[i - 1].A) * f), Route[i - 1].B + ((Route[i].B - Route[i - 1].B) * f));
                }
            }

            return Route.Count > 0 ? Route[0] : (0, 0);
        }

        /// <summary>The chart's relative height (0–1) at route fraction <paramref name="t"/> (linear between curve points).</summary>
        public double ProfileAt(double t)
        {
            if (Profile.Count == 0)
            {
                return 0;
            }

            if (t <= Profile[0].T)
            {
                return Profile[0].H;
            }

            for (int i = 1; i < Profile.Count; i++)
            {
                if (t <= Profile[i].T)
                {
                    double f = (t - Profile[i - 1].T) / Math.Max(1e-9, Profile[i].T - Profile[i - 1].T);
                    return Profile[i - 1].H + ((Profile[i].H - Profile[i - 1].H) * f);
                }
            }

            return Profile[^1].H;
        }

        /// <summary>Distance (image pixels) from a point to the route polyline.</summary>
        public double DistanceToRoute(double a, double b)
        {
            double best = double.MaxValue;
            for (int i = 1; i < Route.Count; i++)
            {
                (double a0, double b0) = Route[i - 1];
                (double a1, double b1) = Route[i];
                double da = a1 - a0, db = b1 - b0, l2 = (da * da) + (db * db);
                double f = l2 > 0 ? Math.Clamp((((a - a0) * da) + ((b - b0) * db)) / l2, 0, 1) : 0;
                double ea = a - (a0 + (da * f)), eb = b - (b0 + (db * f));
                best = Math.Min(best, Math.Sqrt((ea * ea) + (eb * eb)));
            }

            return best;
        }
    }
}
