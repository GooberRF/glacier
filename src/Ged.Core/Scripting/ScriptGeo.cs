using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// The <c>geo</c> global: geometry tools over brushes. <c>geo.check</c> audits a brush's shape with
/// <see cref="GeometryCheck"/> and changes nothing. The edits (ORGANIC-GEOMETRY-SPEC §2) are bulk: each
/// takes one brush, a list or a <c>brush_query</c>, is all-or-nothing and adds one undo command.
/// <para>
/// <c>jitter</c>, <c>twist</c>, <c>bend</c>, <c>taper</c> and <c>snap</c> default to <c>mode = "hull"</c>:
/// the brush's WORLD vertices are moved in double precision, snapped to the 1/1024 m grid and rebuilt as
/// a convex hull (<see cref="HullBrush.TryRebuild(Brush, IReadOnlyList{Vec3d}, float, double, out HullBrushError?)"/>),
/// so every result is convex with flat faces, unrotated and on the grid. Twist, bend and taper first cut
/// the brush into <c>pieces</c> slabs (<see cref="HullDeform.Slice"/>); new pieces follow their source in
/// time order. <c>mode = "triangulate"</c> is the editor's own deformer followed by the planarity guard
/// (<see cref="FacePlanarizer"/>), which keeps per-corner UVs but may leave a non-convex brush.
/// </para>
/// </summary>
[ScriptDoc("Geometry tools: check, roughen and deform brushes (results stay convex and planar), extrude/bevel faces, weld/snap vertices, and builders for organic brush chains (tube, passage, rock_row, scatter, pit).", Since = 2)]
public sealed partial class ScriptGeo
{
    private const string PieceTag = "geo.piece";

    private readonly ScriptContext _ctx;

    internal ScriptGeo(ScriptContext ctx) => _ctx = ctx;

    [ScriptDoc("Audits a brush: closed (every edge shared by two faces, once each way), wound as RED stores it (solid out, air in), convex and planar (corners within 0.0001 m). Changes nothing. Returns geo_report.",
        Example = "local r = geo.check(level.brushes[1]) if not r.ok then print(r.issues[1]) end", Since = 2)]
    public ScriptGeoReport Check(ScriptBrushHandle brush)
    {
        if (brush is null)
        {
            throw new ScriptApiException("geo.check: a brush is required.", "For example geo.check(level.find_brush(7)).");
        }

        return new ScriptGeoReport(GeometryCheck.Check(brush.Model));
    }

    // ---- jitter ----------------------------------------------------------------------------------

    [ScriptDoc("Roughens brushes: each vertex moves up to amount metres per axis (seeded), then the brush is rebuilt as a convex hull on the 1/1024 m grid. Returns brush_query.",
        Example = "geo.jitter{brushes = rocks, amount = {0.3, 0, 0.3}, seed = 4}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoJitter))]
    public ScriptBrushQuery Jitter(ScriptArgs args)
    {
        const string Api = "geo.jitter";
        args = Require(Api, args, "geo.jitter{brushes = b, amount = 0.2}");
        args.RejectUnknown(ScriptOptionSets.GeoJitter);
        List<Brush> targets = Targets(Api, args);
        Vec3d amount = Amount(Api, args);
        long seed = args.Int(Api, "seed", 1);
        bool hull = HullMode(Api, args);
        float tile = Tile(Api, args);
        if (hull)
        {
            RequireConvex(Api, targets);
        }

        var ordinal = targets.Select((b, i) => (b.Uid, i)).ToDictionary(t => t.Uid, t => (long)t.i);
        int nonConvex = 0;
        IReadOnlyList<int> result = Commit(Api, "Jitter brushes", targets, b =>
        {
            var rng = new OrganicRandom(seed, ordinal[b.Uid]);
            if (hull)
            {
                Rebuild(Api, b, HullDeform.Jitter(b, amount, rng), tile, null);
                return Array.Empty<Brush>();
            }

            // The editor's jitter, in world axes (so {x, 0, z} keeps heights on a rotated brush too).
            Geometry g = b.Geometry;
            foreach (int i in UsedVertices(g))
            {
                var d = new Vec3((float)(rng.Range(-1, 1) * amount.X), (float)(rng.Range(-1, 1) * amount.Y), (float)(rng.Range(-1, 1) * amount.Z));
                g.Vertices[i] = g.Vertices[i].Add(b.Rotation.InverseTransform(d));
            }

            nonConvex += Triangulated(Api, b);
            return Array.Empty<Brush>();
        });

        WarnNonConvex(Api, nonConvex);
        _ctx.Changes.Record("deformed", targets.Count);
        return new ScriptBrushQuery(_ctx, result);
    }

    // ---- twist / bend / taper --------------------------------------------------------------------

    [ScriptDoc("Twists brushes about a world axis through their bounds centre: 0° at the low end, degrees at the high end; pieces > 1 slices first. Results stay convex. Returns brush_query.",
        Example = "geo.twist{brushes = column, degrees = 90, pieces = 6}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoTwist))]
    public ScriptBrushQuery Twist(ScriptArgs args) => Deform("geo.twist", DeformKind.Twist, args);

    [ScriptDoc("Bends brushes: their extent along axis wraps into an arc of degrees curling toward toward; cut into pieces that meet on exact shared faces. Returns brush_query.",
        Example = "geo.bend{brushes = beam, axis = \"z\", toward = \"y\", degrees = 60}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoBend))]
    public ScriptBrushQuery Bend(ScriptArgs args) => Deform("geo.bend", DeformKind.Bend, args);

    [ScriptDoc("Tapers brushes along a world axis: the cross-section scales linearly from 1 at the low end to scale at the high end, about the bounds centre. Returns brush_query.",
        Example = "geo.taper{brushes = spire, axis = \"y\", scale = 0.3}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoTaper))]
    public ScriptBrushQuery Taper(ScriptArgs args) => Deform("geo.taper", DeformKind.Taper, args);

    private ScriptBrushQuery Deform(string api, DeformKind kind, ScriptArgs args)
    {
        ScriptOptionSet set = kind switch
        {
            DeformKind.Twist => ScriptOptionSets.GeoTwist,
            DeformKind.Bend => ScriptOptionSets.GeoBend,
            _ => ScriptOptionSets.GeoTaper,
        };
        args = Require(api, args, kind switch
        {
            DeformKind.Twist => "geo.twist{brushes = b, degrees = 45}",
            DeformKind.Bend => "geo.bend{brushes = b, degrees = 45}",
            _ => "geo.taper{brushes = b, scale = 0.5}",
        });
        args.RejectUnknown(set);
        List<Brush> targets = Targets(api, args);
        int axis = Axis(api, args, "axis", kind == DeformKind.Bend ? "z" : "y");
        int toward = -1;
        double degrees = 0, scale = 1;
        if (kind == DeformKind.Bend)
        {
            toward = Axis(api, args, "toward", "y");
            if (toward == axis)
            {
                string[] others = Enumerable.Range(0, 3).Where(i => i != axis).Select(AxisName).ToArray();
                throw new ScriptApiException($"{api}: 'toward' must differ from 'axis' (both are \"{AxisName(axis)}\").",
                    $"Use toward = \"{others[0]}\" or \"{others[1]}\".");
            }

            degrees = RequiredNumber(api, args, "degrees", -180, 180, "degrees");
            if (degrees == 0)
            {
                throw new ScriptApiException($"{api}: 'degrees' must not be 0.", "Give the total arc, e.g. degrees = 45.");
            }
        }
        else if (kind == DeformKind.Twist)
        {
            degrees = RequiredNumber(api, args, "degrees", -360, 360, "degrees");
        }
        else
        {
            scale = RequiredNumber(api, args, "scale", 0.05, 20, string.Empty);
        }

        bool hull = HullMode(api, args);
        float tile = Tile(api, args);
        // A bend folds neighbouring pieces' side faces by its per-piece angle, so that angle must clear the
        // near-flat fold limit (HullAudit.FoldDegrees): at most |degrees| / 0.5 pieces.
        int mostBendPieces = kind == DeformKind.Bend ? Math.Max(1, (int)Math.Floor((Math.Abs(degrees) / HullAudit.FoldDegrees) + 1e-9)) : 32;
        int defaultPieces = kind == DeformKind.Bend ? Math.Min(mostBendPieces, Math.Max(2, (int)Math.Ceiling(Math.Abs(degrees) / 15))) : 1;
        if (!hull && args.Has("pieces"))
        {
            throw new ScriptApiException($"{api}: 'pieces' applies only to mode = \"hull\".", "Drop 'pieces', or use mode = \"hull\".");
        }

        int pieces = args.Int(api, "pieces", defaultPieces);
        if (pieces < 1 || pieces > 32)
        {
            throw new ScriptApiException($"{api}: 'pieces' must be 1 to 32 (got {pieces}).", $"Nearest valid value: {Math.Clamp(pieces, 1, 32)}.");
        }

        if (hull && pieces > mostBendPieces)
        {
            throw new ScriptApiException(
                $"{api}: a {Fmt(Math.Abs(degrees))}° bend in {pieces} pieces turns each piece by only {Fmt(Math.Abs(degrees) / pieces)}°; neighbouring pieces must fold by at least {Fmt(HullAudit.FoldDegrees)}° (the compiler merges flatter folds and leaks).",
                $"Use pieces = {mostBendPieces} or fewer, or bend more degrees.");
        }

        if (hull)
        {
            RequireConvex(api, targets);
            if (pieces > 1)
            {
                foreach (Brush b in targets)
                {
                    if (_ctx.Movers.FindGroupForMember(b.Uid) is { } mover)
                    {
                        throw new ScriptApiException($"{api}: brush#{b.Uid} belongs to mover '{mover.Name}'; cutting it into {pieces} pieces would leave the new pieces out of the mover.",
                            "Use pieces = 1 for mover brushes, or remove the brush from the mover first.");
                    }

                    int tags = _ctx.Metadata.Tags(b.Uid).Count;
                    if (tags + (_ctx.Metadata.Tags(b.Uid).ContainsKey(PieceTag) ? 0 : 1) > ObjectTags.MaxTagsPerUid)
                    {
                        throw new ScriptApiException($"{api}: brush#{b.Uid} already carries {tags} tags; its pieces would need one more ('{PieceTag}', at most {ObjectTags.MaxTagsPerUid}).",
                            "Remove tags you no longer need with untag.");
                    }
                }
            }
        }

        string what = kind switch { DeformKind.Twist => "Twist", DeformKind.Bend => "Bend", _ => "Taper" };
        var pieceTags = new List<(int Uid, Dictionary<string, string> Tags)>();
        var capWarnings = new List<string>();
        int nonConvex = 0;
        IReadOnlyList<int> result = Commit(api, $"{what} brushes", targets, b =>
        {
            if (!hull)
            {
                Geometry g = b.Geometry;
                switch (kind)
                {
                    case DeformKind.Twist:
                        Deformers.Twist(g, axis, (float)degrees);
                        break;
                    case DeformKind.Bend:
                        Deformers.Bend(g, axis, toward, (float)degrees);
                        break;
                    default:
                        Deformers.Taper(g, axis, (float)scale);
                        break;
                }

                nonConvex += Triangulated(api, b);
                return Array.Empty<Brush>();
            }

            List<Vec3d> world = HullDeform.WorldVertices(b);
            (Vec3d min, Vec3d max) = HullDeform.WorldBounds(world);
            double span = max[axis] - min[axis];
            if (span < HullDeform.MinSlab)
            {
                throw new ScriptApiException($"{api}: brush#{b.Uid} is only {Fmt(span)} m long along {AxisName(axis)}; there is nothing to {what.ToLowerInvariant()}.",
                    $"Choose another axis, or {what.ToLowerInvariant()} a longer brush.");
            }

            var centre = new Vec3d((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            DeformMath def = kind switch
            {
                DeformKind.Twist => DeformMath.Twist(axis, min[axis], span, centre, degrees),
                DeformKind.Bend => DeformMath.Bend(axis, toward, min[axis], span, centre, degrees),
                _ => DeformMath.Taper(axis, min[axis], span, centre, scale),
            };

            if (kind == DeformKind.Bend)
            {
                // Every cross-section must stay on the near side of the bend centre, or it folds over itself.
                double radius = Math.Abs(def.BendRadius);
                double sign = Math.Sign(def.BendRadius);
                double reach = world.Max(p => (p[toward] - centre[toward]) * sign);
                if (radius - reach < HullDeform.MinSlab)
                {
                    double thick = max[toward] - min[toward];
                    throw new ScriptApiException(
                        $"{api}: brush#{b.Uid} is {Fmt(thick)} m thick toward {AxisName(toward)}, but a {Fmt(Math.Abs(degrees))}° bend of its {Fmt(span)} m length curls with a radius of only {Fmt(radius)} m.",
                        "Bend fewer degrees, or bend a longer brush.");
                }
            }

            List<List<Vec3d>>? slabs = HullDeform.Slice(b, def, pieces, out List<GridPlane> joints, out GeoEditError? sliceError);
            if (slabs is null)
            {
                throw new ScriptApiException($"{api}: brush#{b.Uid}: {sliceError!.Message}", sliceError.Hint);
            }

            Brush original = GeometryClone.Deep(b);
            Dictionary<string, string> sourceTags = _ctx.Metadata.Tags(b.Uid);
            var created = new List<Brush>();
            for (int k = 0; k < slabs.Count; k++)
            {
                Brush target = k == 0 ? b : GeometryClone.Deep(original);
                string label = slabs.Count > 1 ? $"brush#{b.Uid} piece {k + 1}/{slabs.Count}" : $"brush#{b.Uid}";
                Rebuild(api, target, slabs[k], tile, label);
                if (k > 0)
                {
                    created.Add(target);
                }
            }

            if (slabs.Count > 1)
            {
                CheckSharedCaps(api, b.Uid, joints, new[] { b }.Concat(created).ToList(), capWarnings);

                pieceTags.Add((b.Uid, WithPiece(sourceTags, 1, slabs.Count)));
                for (int k = 0; k < created.Count; k++)
                {
                    pieceTags.Add((-1, WithPiece(sourceTags, k + 2, slabs.Count))); // UID assigned on commit
                }
            }

            return created;
        }, out IReadOnlyList<int> newPieces);

        // Pieces got their UIDs on commit; map the placeholder rows onto them in creation order.
        if (pieceTags.Count > 0)
        {
            var queue = new Queue<int>(newPieces);
            var writes = new List<(int, GedMetadataType, byte[]?)>();
            foreach ((int uid, Dictionary<string, string> tags) in pieceTags)
            {
                int real = uid >= 0 ? uid : queue.Dequeue();
                writes.Add((real, GedMetadataType.Tags, ObjectTags.Encode(tags)));
            }

            _ctx.Metadata.SetBlocks(writes, "Tag pieces");
        }

        foreach (string w in capWarnings)
        {
            _ctx.Log.Warn(w);
        }

        WarnNonConvex(api, nonConvex);
        _ctx.Changes.Record("deformed", targets.Count);
        return new ScriptBrushQuery(_ctx, result);
    }

    // ---- extrude / bevel -------------------------------------------------------------------------

    [ScriptDoc("Extrudes faces out of their brush (air brushes too) by distance metres, walling the sides (new walls box-mapped at tile). A non-convex result is kept and warned about. Returns brush_query.",
        Example = "geo.extrude{faces = b:faces_facing(\"up\"), distance = 2}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoExtrude))]
    public ScriptBrushQuery Extrude(ScriptArgs args)
    {
        const string Api = "geo.extrude";
        args = Require(Api, args, "geo.extrude{faces = f, distance = 1}");
        args.RejectUnknown(ScriptOptionSets.GeoExtrude);
        Dictionary<Brush, List<int>> faces = Faces(Api, args);
        double distance = RequiredNumber(Api, args, "distance", -4096, 4096, "metres");
        if (Math.Abs(distance) < 0.001)
        {
            throw new ScriptApiException($"{Api}: 'distance' must be at least 0.001 m in size (got {Fmt(distance)}).", "Pass a positive distance to extrude outward, a negative one to push the face in.");
        }

        float tile = Tile(Api, args);
        return FaceEdit(Api, "Extrude faces", faces, tile, (g, f) => FaceOps.Extrude(g, f, (float)distance));
    }

    [ScriptDoc("Bevels (insets) faces: each face shrinks toward its centroid by amount (0.01 to 0.99) and a ring of coplanar faces fills the border. Returns brush_query.",
        Example = "geo.bevel{faces = b:faces_facing(\"up\"), amount = 0.25}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoBevel))]
    public ScriptBrushQuery Bevel(ScriptArgs args)
    {
        const string Api = "geo.bevel";
        args = Require(Api, args, "geo.bevel{faces = f, amount = 0.25}");
        args.RejectUnknown(ScriptOptionSets.GeoBevel);
        Dictionary<Brush, List<int>> faces = Faces(Api, args);
        double amount = RequiredNumber(Api, args, "amount", 0.01, 0.99, string.Empty);
        float tile = Tile(Api, args);
        return FaceEdit(Api, "Bevel faces", faces, tile, (g, f) => FaceOps.Bevel(g, f, (float)amount));
    }

    private ScriptBrushQuery FaceEdit(string api, string description, Dictionary<Brush, List<int>> faces, float tile, Func<Geometry, int, OpResult> op)
    {
        int nonConvex = 0;
        List<Brush> targets = faces.Keys.ToList();
        IReadOnlyList<int> result = Commit(api, description, targets, b =>
        {
            Geometry g = b.Geometry;
            int before = g.Faces.Count;
            GeometryUtil.RecomputeAllPlanes(g);
            foreach (int f in faces[b].OrderByDescending(i => i))
            {
                OpResult r = op(g, f);
                if (!r)
                {
                    throw new ScriptApiException($"{api}: brush#{b.Uid} face {f}: {r.Message}");
                }
            }

            var added = Enumerable.Range(before, g.Faces.Count - before).Where(i => ScriptBrushHandle.IsMappable(g, g.Faces[i])).ToList();
            UvOps.BoxMap(g, added, b.Rotation, b.Position, 1f / tile, 1, 1);
            if (!GeometryUtil.Validate(g))
            {
                throw new ScriptApiException($"{api}: brush#{b.Uid}: the result has degenerate faces.", "Use a smaller distance or amount.");
            }

            if (!GeometryCheck.Check(b).Convex)
            {
                nonConvex++;
            }

            return Array.Empty<Brush>();
        });

        WarnNonConvex(api, nonConvex);
        _ctx.Changes.Record("edited", targets.Count);
        return new ScriptBrushQuery(_ctx, result);
    }

    // ---- weld / snap -----------------------------------------------------------------------------

    [ScriptDoc("Welds vertices closer than tolerance metres (default 0.001) and removes faces that collapse; errors if a brush would lose its volume. Returns brush_query.",
        Example = "geo.weld{brushes = level.all_brushes(), tolerance = 0.002}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoWeld))]
    public ScriptBrushQuery Weld(ScriptArgs args)
    {
        const string Api = "geo.weld";
        args = Require(Api, args, "geo.weld{brushes = b}");
        args.RejectUnknown(ScriptOptionSets.GeoWeld);
        List<Brush> targets = Targets(Api, args);
        double tolerance = args.Number(Api, "tolerance", 0.001);
        if (!double.IsFinite(tolerance) || tolerance < 1e-5 || tolerance > 1)
        {
            throw new ScriptApiException($"{Api}: 'tolerance' must be 0.00001 to 1 metre (got {Fmt(tolerance)}).",
                $"Nearest valid value: {Fmt(double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-5, 1) : 0.001)}.");
        }

        IReadOnlyList<int> result = Commit(Api, "Weld vertices", targets, b =>
        {
            Geometry g = b.Geometry;
            bool closed = GeometryCheck.Check(b).Closed;
            GeometryUtil.WeldVertices(g, (float)tolerance);
            GeometryUtil.CleanupFaces(g);
            GeometryUtil.CompactUnusedVertices(g);
            GeometryUtil.RecomputeAllPlanes(g);
            if (!GeometryUtil.Validate(g) || (closed && !GeometryCheck.Check(b).Closed))
            {
                throw new ScriptApiException($"{Api}: welding at {Fmt(tolerance)} m collapses brush#{b.Uid}.", "Use a smaller tolerance.");
            }

            return Array.Empty<Brush>();
        });

        _ctx.Changes.Record("welded", targets.Count);
        return new ScriptBrushQuery(_ctx, result);
    }

    [ScriptDoc("Snaps every vertex to a world grid (default 1/1024 m) and rebuilds the convex hull — the repair for float noise on hand-made or imported brushes. Returns brush_query.",
        Example = "geo.snap{brushes = b, grid = 1/64}", Since = 2)]
    [ScriptOptions(nameof(ScriptOptionSets.GeoSnap))]
    public ScriptBrushQuery Snap(ScriptArgs args)
    {
        const string Api = "geo.snap";
        args = Require(Api, args, "geo.snap{brushes = b}");
        args.RejectUnknown(ScriptOptionSets.GeoSnap);
        List<Brush> targets = Targets(Api, args);
        double grid = args.Number(Api, "grid", OrganicLimits.MinSnap);
        if (!IsPowerOfTwo(grid, -10, 4))
        {
            double nearest = !double.IsFinite(grid) || grid <= 0 ? OrganicLimits.MinSnap : Math.ScaleB(1.0, (int)Math.Clamp(Math.Round(Math.Log2(grid)), -10, 4));
            throw new ScriptApiException($"{Api}: 'grid' must be a power of two from 1/1024 to 16 (got {Fmt(grid)}).", $"Nearest valid value: {HullBrush.FormatSnap(nearest)}.");
        }

        bool hull = HullMode(Api, args);
        float tile = Tile(Api, args);
        if (hull)
        {
            RequireConvex(Api, targets);
        }

        int nonConvex = 0;
        IReadOnlyList<int> result = Commit(Api, "Snap vertices", targets, b =>
        {
            if (hull)
            {
                Rebuild(Api, b, HullDeform.SnapAll(b, grid), tile, null);
            }
            else
            {
                BrushTransform.SnapVerticesToGrid(b, (float)grid);
                nonConvex += Triangulated(Api, b);
            }

            return Array.Empty<Brush>();
        });

        WarnNonConvex(Api, nonConvex);
        _ctx.Changes.Record("snapped", targets.Count);
        return new ScriptBrushQuery(_ctx, result);
    }

    public override string ToString() => "geo";

    // ---- shared plumbing -------------------------------------------------------------------------

    private static ScriptArgs Require(string api, ScriptArgs? args, string example) =>
        args ?? throw new ScriptApiException($"{api}: a table of options is required.", $"For example {example}.");

    /// <summary>
    /// The <c>brushes</c> option: one brush, a list of handles/UIDs or a brush_query → the live brushes in
    /// time order. Locked and portal brushes are errors.
    /// </summary>
    private List<Brush> Targets(string api, ScriptArgs args)
    {
        object? raw = args.Named.TryGetValue("brushes", out object? v) ? v : null;
        List<int> uids = raw switch
        {
            null => throw new ScriptApiException($"{api}: 'brushes' is required.", "Pass a brush, a list of brushes or UIDs, or a brush_query."),
            ScriptBrushHandle h => new List<int> { h.Uid },
            ScriptBrushQuery q => q.Uids.Distinct().ToList(),
            double d => new List<int> { ScriptArgs.ToInt(api, "brushes", d) },
            ScriptArgs list => ScriptMembers.Brushes(_ctx, api, "brushes", list),
            _ => throw new ScriptApiException($"{api}: 'brushes' must be a brush, a list of brushes or UIDs, or a brush_query (got {ScriptArgs.LuaTypeOf(raw)})."),
        };

        if (uids.Count == 0)
        {
            throw new ScriptApiException($"{api}: 'brushes' lists no brushes.", "Pass a brush, a list of brushes or UIDs, or a non-empty brush_query.");
        }

        var brushes = new List<Brush>(uids.Count);
        foreach (int uid in uids)
        {
            Brush b = _ctx.Brushes.FindBrush(uid) ?? throw new ScriptApiException($"{api}: there is no brush with UID {uid}.");
            RequireEditable(api, b);
            brushes.Add(b);
        }

        return brushes.OrderBy(b => _ctx.Brushes.TimeIndex(b.Uid)).ToList();
    }

    private static void RequireEditable(string api, Brush b)
    {
        if (b.State == BrushState.Locked)
        {
            throw new ScriptApiException($"{api}: brush#{b.Uid} is locked.", "Unlock it with b:set_locked(false), or leave it out.");
        }

        if ((b.Flags & (uint)BrushFlags.Portal) != 0)
        {
            throw new ScriptApiException($"{api}: brush#{b.Uid}: a portal brush cannot be deformed.", "Leave portal brushes out, e.g. q:where(function(b) return not b.portal end).");
        }
    }

    /// <summary>The <c>faces</c> option: one face or a list → face indices grouped by brush, brushes in time order.</summary>
    private Dictionary<Brush, List<int>> Faces(string api, ScriptArgs args)
    {
        object? raw = args.Named.TryGetValue("faces", out object? v) ? v : null;
        IReadOnlyList<object?> items = raw switch
        {
            null => throw new ScriptApiException($"{api}: 'faces' is required.", "Pass a face or a list of faces, e.g. faces = b:faces_facing(\"up\")."),
            ScriptFaceHandle f => new object?[] { f },
            ScriptArgs { Named.Count: 0 } list => list.Items,
            _ => throw new ScriptApiException($"{api}: 'faces' must be a face or a list of faces (got {ScriptArgs.LuaTypeOf(raw)})."),
        };

        if (items.Count == 0)
        {
            throw new ScriptApiException($"{api}: 'faces' lists no faces.", "Pass a face or a non-empty list of faces.");
        }

        var byUid = new Dictionary<int, SortedSet<int>>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not ScriptFaceHandle f)
            {
                throw new ScriptApiException($"{api}: faces[{i + 1}] must be a face (got {ScriptArgs.LuaTypeOf(items[i])}).");
            }

            Brush b = _ctx.Brushes.FindBrush(f.BrushUid) ?? throw new ScriptApiException($"{api}: faces[{i + 1}]: brush {f.BrushUid} no longer exists.");
            if (f.Index < 0 || f.Index >= b.Geometry.Faces.Count)
            {
                throw new ScriptApiException($"{api}: faces[{i + 1}]: face {f.Index} is out of range for brush#{b.Uid}.");
            }

            RequireEditable(api, b);
            (byUid.TryGetValue(b.Uid, out SortedSet<int>? s) ? s : byUid[b.Uid] = new SortedSet<int>()).Add(f.Index);
        }

        return byUid.OrderBy(kv => _ctx.Brushes.TimeIndex(kv.Key))
            .ToDictionary(kv => _ctx.Brushes.FindBrush(kv.Key)!, kv => kv.Value.ToList(), ReferenceEqualityComparer.Instance as IEqualityComparer<Brush>);
    }

    /// <summary>Hull mode requires a convex source brush (checked for every target before anything changes).</summary>
    private static void RequireConvex(string api, IEnumerable<Brush> targets)
    {
        foreach (Brush b in targets)
        {
            if (!GeometryCheck.Check(b).Convex)
            {
                throw new ScriptApiException($"{api}: brush#{b.Uid} is not convex.", "Use mode = \"triangulate\", or rebuild it with level.brush_from_points.");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="edit"/> on every target inside ONE <see cref="BrushEditor.EditBrushesWithPieces"/>
    /// (all-or-nothing: any exception thrown by the edit, a cancellation included, restores every brush and is
    /// rethrown). Returns the edited brushes and their new pieces in time order.
    /// </summary>
    private IReadOnlyList<int> Commit(string api, string description, List<Brush> targets, Func<Brush, IReadOnlyList<Brush>> edit) =>
        Commit(api, description, targets, edit, out _);

    private IReadOnlyList<int> Commit(string api, string description, List<Brush> targets, Func<Brush, IReadOnlyList<Brush>> edit, out IReadOnlyList<int> newPieces)
    {
        ScriptApiException? failure = null;
        var uids = targets.Select(b => b.Uid).ToList();
        OpResult r = _ctx.Brushes.EditBrushesWithPieces(uids, description, b =>
        {
            // Stop cancels between brushes; the edit then restores every brush (all-or-nothing).
            _ctx.Cancellation.ThrowIfCancellationRequested();
            try
            {
                return (OpResult.Ok(), edit(b));
            }
            catch (ScriptApiException ex)
            {
                failure = ex;
                return (OpResult.Fail(ex.Message), Array.Empty<Brush>());
            }
        }, out IReadOnlyList<int> result);

        if (failure is not null)
        {
            throw failure;
        }

        if (!r)
        {
            throw new ScriptApiException($"{api}: {r.Message}");
        }

        var edited = new HashSet<int>(uids);
        newPieces = result.Where(u => !edited.Contains(u)).ToList();
        foreach (int uid in uids)
        {
            _ctx.NoteBrushEdited(uid);
        }

        foreach (int uid in newPieces)
        {
            _ctx.NoteCreated(uid);
            _ctx.Changes.Record("placed");
        }

        return result;
    }

    /// <summary>Rebuilds <paramref name="b"/> as the hull of <paramref name="points"/> and asserts the result is closed, convex and planar.</summary>
    private static void Rebuild(string api, Brush b, IReadOnlyList<Vec3d> points, float tile, string? label)
    {
        string who = label ?? $"brush#{b.Uid}";
        int uid = b.Uid;
        HullBrush.TryRebuild(b, points, tile, HullBrush.Grid, out HullBrushError? error);
        if (error is not null)
        {
            string message = error.Kind == HullBrushErrorKind.PointCount
                ? $"{who} has {points.Count} vertices; hull mode rebuilds brushes of {OrganicLimits.MinInputPoints} to {OrganicLimits.MaxInputPoints}."
                : error.Message;
            throw new ScriptApiException($"{api}: {who}: {message}", error.Hint ?? (error.Kind == HullBrushErrorKind.PointCount ? "Use mode = \"triangulate\" for dense brushes." : null));
        }

        GeometryReport report = GeometryCheck.Check(b);
        if (!report.Closed || !report.Convex || !report.Planar || !GeometryUtil.Validate(b.Geometry))
        {
            throw new ScriptApiException($"{api}: internal error: {who} failed its shape check after the rebuild ({string.Join(" ", report.Issues)}).",
                $"Please report this with the script; brush#{uid} is unchanged.");
        }
    }

    /// <summary>The GUI's planarity guard after a triangulate-mode deformer; returns 1 when the brush ended non-convex.</summary>
    private static int Triangulated(string api, Brush b)
    {
        GeometryUtil.RecomputeAllPlanes(b.Geometry);
        FacePlanarizer.Planarize(b.Geometry);
        OpResult valid = GeometryUtil.Validate(b.Geometry);
        if (!valid)
        {
            throw new ScriptApiException($"{api}: brush#{b.Uid}: {valid.Message}", "Use a smaller amount, or mode = \"hull\".");
        }

        return GeometryCheck.Check(b).Convex ? 0 : 1;
    }

    private void WarnNonConvex(string api, int count)
    {
        if (count > 0)
        {
            _ctx.Log.Warn($"{api}: {count} brush{(count == 1 ? " is" : "es are")} no longer convex (kept; the compiler accepts it, but geo hull edits will refuse {(count == 1 ? "it" : "them")}).");
        }
    }

    /// <summary>Twist / taper slabs must meet on identical faces; a sliver simplification can drop a corner on one side only.</summary>
    private static void CheckSharedCaps(string api, int sourceUid, IReadOnlyList<GridPlane> joints, List<Brush> pieces, List<string> warnings)
    {
        for (int k = 0; k + 1 < pieces.Count && k < joints.Count; k++)
        {
            GridPlane joint = joints[k];
            var capA = HullDeform.WorldVertices(pieces[k]).Where(p => joint.Side(p) == 0).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
            var capB = HullDeform.WorldVertices(pieces[k + 1]).Where(p => joint.Side(p) == 0).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToList();
            if (!capA.SequenceEqual(capB))
            {
                warnings.Add($"{api}: brush#{sourceUid} pieces {k + 1} and {k + 2} do not meet on identical faces at joint {k + 1} (a sliver corner was simplified on one side); the joint stays closed.");
            }
        }
    }

    private static Dictionary<string, string> WithPiece(Dictionary<string, string> source, int k, int n) =>
        new(source, StringComparer.Ordinal) { [PieceTag] = $"{k}/{n}" };

    private static IEnumerable<int> UsedVertices(Geometry g) =>
        new SortedSet<int>(g.Faces.SelectMany(f => f.Vertices.Select(v => v.Index)).Where(i => i >= 0 && i < g.Vertices.Count));

    private static bool HullMode(string api, ScriptArgs args)
    {
        string mode = (args.String(api, "mode", "hull") ?? "hull").Trim();
        if (string.Equals(mode, "hull", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mode, "triangulate", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new ScriptApiException($"{api}: unknown mode '{mode}'.", ScriptSuggest.Hint(mode, new[] { "hull", "triangulate" }, "mode"));
    }

    private static float Tile(string api, ScriptArgs args)
    {
        double tile = args.Number(api, "tile", GeometryUtil.DefaultTileMetres);
        if (HullBrush.ValidateTile(tile) is { } bad)
        {
            throw new ScriptApiException($"{api}: {bad.Message}", bad.Hint);
        }

        return (float)tile;
    }

    private static int Axis(string api, ScriptArgs args, string key, string fallback)
    {
        string name = (args.String(api, key, fallback) ?? fallback).Trim();
        return name.ToLowerInvariant() switch
        {
            "x" => 0,
            "y" => 1,
            "z" => 2,
            _ => throw new ScriptApiException($"{api}: unknown {key} '{name}'.", ScriptSuggest.Hint(name, new[] { "x", "y", "z" }, "axis")),
        };
    }

    private static string AxisName(int axis) => axis switch { 0 => "x", 1 => "y", _ => "z" };

    private static Vec3d Amount(string api, ScriptArgs args)
    {
        object? raw = args.Named.TryGetValue("amount", out object? v) ? v : null;
        Vec3d a = raw switch
        {
            null => throw new ScriptApiException($"{api}: 'amount' is required.", "The largest move per axis in metres, e.g. amount = 0.2 or amount = {0.3, 0, 0.3}."),
            double d => new Vec3d(d, d, d),
            _ => ToVec3d(api, raw),
        };

        foreach (double c in new[] { a.X, a.Y, a.Z })
        {
            if (!double.IsFinite(c) || c < 0 || c > 16)
            {
                throw new ScriptApiException($"{api}: 'amount' must be 0 to 16 metres on each axis (got {Fmt(c)}).",
                    $"Nearest valid value: {Fmt(double.IsFinite(c) ? Math.Clamp(c, 0, 16) : 0)}.");
            }
        }

        return a;
    }

    private static Vec3d ToVec3d(string api, object raw) => raw switch
    {
        Vec3 f => new Vec3d(f.X, f.Y, f.Z),
        ScriptArgs { Named.Count: 0, Items.Count: 3 } t when t.Items.All(i => i is double) => new Vec3d((double)t.Items[0]!, (double)t.Items[1]!, (double)t.Items[2]!),
        ScriptArgs { Items.Count: 0, Named.Count: 3 } t when new[] { "x", "y", "z" }.All(k => t.Named.TryGetValue(k, out object? o) && o is double)
            => new Vec3d((double)t.Named["x"]!, (double)t.Named["y"]!, (double)t.Named["z"]!),
        _ => throw new ScriptApiException($"{api}: 'amount' must be a number or a vec, {{x, y, z}} or {{x=, y=, z=}} (got {ScriptArgs.LuaTypeOf(raw)})."),
    };

    private static double RequiredNumber(string api, ScriptArgs args, string key, double min, double max, string unit)
    {
        double v = args.NumberOrNull(api, key)
            ?? throw new ScriptApiException($"{api}: '{key}' is required.", $"A number from {Fmt(min)} to {Fmt(max)}{(unit.Length > 0 ? " " + unit : string.Empty)}.");
        if (!double.IsFinite(v) || v < min || v > max)
        {
            throw new ScriptApiException($"{api}: '{key}' must be {Fmt(min)} to {Fmt(max)}{(unit.Length > 0 ? " " + unit : string.Empty)} (got {Fmt(v)}).",
                $"Nearest valid value: {Fmt(double.IsFinite(v) ? Math.Clamp(v, min, max) : min)}.");
        }

        return v;
    }

    private static bool IsPowerOfTwo(double v, int minExp, int maxExp)
    {
        if (!double.IsFinite(v) || v <= 0)
        {
            return false;
        }

        double k = Math.Log2(v);
        return k >= minExp && k <= maxExp && Math.Round(k) == k && Math.ScaleB(1.0, (int)k) == v;
    }

    private static string Fmt(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>The result of <c>geo.check</c> (Lua type <c>geo_report</c>).</summary>
[ScriptDoc("A brush shape audit from geo.check: ok (closed, wound the way RED stores it, and planar), each test, the face/vertex counts and the problems found.", Since = 2)]
public sealed class ScriptGeoReport
{
    private readonly GeometryReport _r;

    internal ScriptGeoReport(GeometryReport r) => _r = r;

    [ScriptDoc("True when the brush is closed, wound the way RED stores it and planar (convexity is reported separately).", Since = 2)]
    public bool Ok => _r.Closed && _r.Outward && _r.Planar;

    [ScriptDoc("True when every edge is used by exactly two faces, once in each direction.", Since = 2)]
    public bool Closed => _r.Closed;

    [ScriptDoc("True when the brush is closed and its faces wind the way RED stores it: outward (positive volume) for a solid brush, inward for an air brush (a solid portal brush may wind either way).", Since = 2)]
    public bool Outward => _r.Outward;

    [ScriptDoc("True when the brush is closed and no vertex lies more than 0.0001 m outside any face plane.", Since = 2)]
    public bool Convex => _r.Convex;

    [ScriptDoc("True when every face corner lies within 0.0001 m of its face's plane.", Since = 2)]
    public bool Planar => _r.Planar;

    [ScriptDoc("The number of faces.", Since = 2)]
    public int FaceCount => _r.Faces;

    [ScriptDoc("The number of vertices in the brush's vertex pool.", Since = 2)]
    public int VertexCount => _r.Vertices;

    [ScriptDoc("The most corners on any one face.", Since = 2)]
    public int MaxFaceCorners => _r.MaxFaceCorners;

    [ScriptDoc("The largest distance of a face corner from its face's plane, metres.", Since = 2)]
    public double MaxPlanarError => _r.MaxPlanarError;

    [ScriptDoc("What is wrong, as short sentences (string[], at most 8; empty when every test passes).", Since = 2)]
    public string[] Issues => _r.Issues.ToArray();

    public override string ToString() => Ok && _r.Convex ? "geo_report(ok, convex)" : Ok ? "geo_report(ok)" : $"geo_report({_r.Issues.Count} issue(s))";
}
