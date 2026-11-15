using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>
/// Harvests recurring detail assemblies from one level, read-only: candidate brushes (small solids and detail,
/// plus air niches carved into solid) → adjacency components (real geometry within 0.1 m) → oversized components
/// split → local frame and contacts (<see cref="ModuleFraming"/>) → stretch slices and tile families
/// (<see cref="ModuleShape"/>) → classification, role and styles → quality gate. De-duplication across levels and
/// the library output are <see cref="ModuleLibraryBuilder"/>'s job.
/// </summary>
public static class ModuleHarvester
{
    private sealed class Member
    {
        public required int Index { get; init; }

        public required Brush Brush { get; init; }

        public required WorldPolyhedron Shape { get; init; }

        public bool Air => (Brush.Flags & (uint)BrushFlags.Air) != 0;
    }

    /// <summary>Harvests the modules of <paramref name="rfl"/> (never modifies it).</summary>
    public static LevelHarvestResult HarvestLevel(RflFile rfl, HarvestLevelContext context, HarvestOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(context);
        HarvestOptions o = options ?? new HarvestOptions();
        rfl.ParseAllKnownSections();
        List<Brush> brushes = rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().FirstOrDefault()?.Brushes ?? new List<Brush>();
        HashSet<int> movers = MoverBrushes.CollectMoverUids(rfl);
        return HarvestBrushes(brushes, movers, context, o, ct);
    }

    /// <summary>Harvests modules from an explicit brush list (time order) with the given mover UIDs excluded.</summary>
    public static LevelHarvestResult HarvestBrushes(IReadOnlyList<Brush> brushes, IReadOnlyCollection<int> moverUids, HarvestLevelContext context, HarvestOptions o, CancellationToken ct = default)
    {
        var result = new LevelHarvestResult { Level = context.Level, Brushes = brushes.Count };
        var shapes = brushes.Select(WorldPolyhedron.FromBrush).ToList();
        var index = new BrushSolidityIndex(brushes, shapes, moverUids);

        // ---- candidates ------------------------------------------------------------------------------------
        var candidates = new List<Member>();
        for (int i = 0; i < brushes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Brush b = brushes[i];
            WorldPolyhedron s = shapes[i];
            if (moverUids.Contains(b.Uid) || (b.Flags & (uint)BrushFlags.Portal) != 0 || b.Geometry.Faces.Count == 0)
            {
                continue;
            }

            D3 size = s.Size;
            double maxExt = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (maxExt < 1e-3 || s.Faces.All(f => f.Length < 3) || !HasVisibleTexture(b))
            {
                continue;
            }

            double vol = size.X * size.Y * size.Z;
            bool air = (b.Flags & (uint)BrushFlags.Air) != 0;
            bool detail = (b.Flags & (uint)BrushFlags.Detail) != 0;
            bool take;
            if (!air)
            {
                take = detail || (vol <= o.MaxSolidVolume && maxExt <= o.MaxSolidExtent);
            }
            else
            {
                double minExt = Math.Min(size.X, Math.Min(size.Y, size.Z));
                take = vol <= o.MaxNicheVolume && minExt <= o.MaxNicheThickness && s.Closed && IsNiche(index, b.Uid, s, o.NicheSolidFraction);
            }

            if (take)
            {
                candidates.Add(new Member { Index = i, Brush = b, Shape = s });
            }
        }

        result.Candidates = candidates.Count;

        // ---- adjacency + components -------------------------------------------------------------------------
        List<List<Member>> components = Components(candidates, o, ct);
        var final = new List<List<Member>>();
        foreach (List<Member> c in components)
        {
            Split(c, o, final);
        }

        result.Components = final.Count;
        Dictionary<string, double> levelStyles = LevelStyleShares(brushes, shapes, moverUids, context);
        foreach (List<Member> comp in final.OrderBy(c => c[0].Index))
        {
            ct.ThrowIfCancellationRequested();
            Analyze(comp, index, context, o, result, levelStyles);
        }

        return result;
    }

    /// <summary>The level's style shares: every static brush face (air faces are the room surfaces), area-weighted.</summary>
    private static Dictionary<string, double> LevelStyleShares(IReadOnlyList<Brush> brushes, List<WorldPolyhedron> shapes, IReadOnlyCollection<int> movers, HarvestLevelContext ctx)
    {
        var areaByTexture = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < brushes.Count; i++)
        {
            Brush b = brushes[i];
            if (movers.Contains(b.Uid) || (b.Flags & (uint)BrushFlags.Portal) != 0)
            {
                continue;
            }

            Geometry g = b.Geometry;
            for (int fi = 0; fi < g.Faces.Count && fi < shapes[i].Faces.Length; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Texture >= 0 && f.Texture < g.Textures.Count && g.Textures[f.Texture].Length > 0)
                {
                    string t = g.Textures[f.Texture];
                    areaByTexture[t] = areaByTexture.GetValueOrDefault(t) + shapes[i].FaceArea(fi);
                }
            }
        }

        var shares = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string tex, double area) in areaByTexture.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (tex.Contains("invisible", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string st = ModuleStyles.StyleOf(tex, ctx.TextureCategory?.Invoke(tex));
            shares[st] = shares.GetValueOrDefault(st) + area;
        }

        return shares;
    }

    private static bool HasVisibleTexture(Brush b)
    {
        Geometry g = b.Geometry;
        foreach (Face f in g.Faces)
        {
            if (f.Texture < 0 || f.Texture >= g.Textures.Count)
            {
                continue;
            }

            string t = g.Textures[f.Texture];
            if (t.Length > 0 && !t.Contains("invisible", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when at least <paramref name="fraction"/> of sample points inside the air brush are solid without it.</summary>
    private static bool IsNiche(BrushSolidityIndex index, int uid, WorldPolyhedron s, double fraction)
    {
        var exclude = new HashSet<int> { uid };
        int inside = 0, solid = 0;
        const int n = 5;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                for (int k = 0; k < n; k++)
                {
                    var p = new D3(
                        s.Min.X + (s.Size.X * (i + 0.5) / n),
                        s.Min.Y + (s.Size.Y * (j + 0.5) / n),
                        s.Min.Z + (s.Size.Z * (k + 0.5) / n));
                    if (!s.Contains(p))
                    {
                        continue;
                    }

                    inside++;
                    if (index.IsSolid(p, exclude, structuralOnly: false))
                    {
                        solid++;
                    }
                }
            }
        }

        return inside > 0 && solid >= fraction * inside;
    }

    // ---- adjacency ----------------------------------------------------------------------------------------------

    private static List<List<Member>> Components(List<Member> members, HarvestOptions o, CancellationToken ct)
    {
        int n = members.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        // Sweep and prune on X.
        int[] order = Enumerable.Range(0, n).OrderBy(i => members[i].Shape.Min.X).ToArray();
        double g = o.AdjacencyGrow;
        for (int a = 0; a < n; a++)
        {
            ct.ThrowIfCancellationRequested();
            Member ma = members[order[a]];
            for (int b = a + 1; b < n; b++)
            {
                Member mb = members[order[b]];
                if (mb.Shape.Min.X - g > ma.Shape.Max.X + g)
                {
                    break;
                }

                if (!BoundsOverlap(ma.Shape, mb.Shape, g))
                {
                    continue;
                }

                int ra = Find(order[a]), rb = Find(order[b]);
                if (ra == rb)
                {
                    continue;
                }

                if (Near(ma.Shape, mb.Shape, o.AdjacencyDistance))
                {
                    parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
            }
        }

        return Enumerable.Range(0, n)
            .GroupBy(Find)
            .Select(grp => grp.Select(i => members[i]).OrderBy(m => m.Index).ToList())
            .OrderBy(c => c[0].Index)
            .ToList();
    }

    private static bool BoundsOverlap(WorldPolyhedron a, WorldPolyhedron b, double grow) =>
        a.Min.X - grow <= b.Max.X + grow && a.Max.X + grow >= b.Min.X - grow
        && a.Min.Y - grow <= b.Max.Y + grow && a.Max.Y + grow >= b.Min.Y - grow
        && a.Min.Z - grow <= b.Max.Z + grow && a.Max.Z + grow >= b.Min.Z - grow;

    /// <summary>
    /// True when the real geometry of two brushes comes within <paramref name="dist"/>: a separating-axis test with a
    /// tolerance for two convex brushes (face normals and edge cross products); otherwise vertex-to-face distances
    /// both ways plus edge-through-face crossings.
    /// </summary>
    internal static bool Near(WorldPolyhedron a, WorldPolyhedron b, double dist)
    {
        if (a.Convex && b.Convex)
        {
            return !Separated(a, b, dist);
        }

        return VertexNearFaces(a, b, dist) || VertexNearFaces(b, a, dist) || EdgesCross(a, b) || EdgesCross(b, a);
    }

    private static bool Separated(WorldPolyhedron a, WorldPolyhedron b, double dist)
    {
        foreach (D3 axis in Axes(a, b))
        {
            (double amin, double amax) = Project(a, axis);
            (double bmin, double bmax) = Project(b, axis);
            if (bmin - amax > dist || amin - bmax > dist)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<D3> Axes(WorldPolyhedron a, WorldPolyhedron b)
    {
        var seen = new List<D3>();
        bool Fresh(D3 d)
        {
            foreach (D3 s in seen)
            {
                if (Math.Abs(s.Dot(d)) > 0.9999)
                {
                    return false;
                }
            }

            seen.Add(d);
            return true;
        }

        foreach (D3 n in a.Normals.Concat(b.Normals))
        {
            if (n.Dot(n) > 0.5 && Fresh(n))
            {
                yield return n;
            }
        }

        List<D3> ea = EdgeDirections(a), eb = EdgeDirections(b);
        foreach (D3 da in ea)
        {
            foreach (D3 db in eb)
            {
                D3 c = da.Cross(db);
                if (c.Length > 1e-6)
                {
                    c = c.Normalized();
                    if (Fresh(c))
                    {
                        yield return c;
                    }
                }
            }
        }
    }

    private static List<D3> EdgeDirections(WorldPolyhedron p)
    {
        var dirs = new List<D3>();
        foreach (int[] f in p.Faces)
        {
            for (int i = 0; i < f.Length && dirs.Count < 16; i++)
            {
                D3 d = p.Vertices[f[(i + 1) % f.Length]] - p.Vertices[f[i]];
                if (d.Length < 1e-6)
                {
                    continue;
                }

                d = d.Normalized();
                if (!dirs.Any(e => Math.Abs(e.Dot(d)) > 0.9999))
                {
                    dirs.Add(d);
                }
            }
        }

        return dirs;
    }

    private static (double, double) Project(WorldPolyhedron p, D3 axis)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (D3 v in p.Vertices)
        {
            double d = v.Dot(axis);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }

        return (min, max);
    }

    private static bool VertexNearFaces(WorldPolyhedron a, WorldPolyhedron b, double dist)
    {
        foreach (D3 v in a.Vertices)
        {
            if (!b.BoundsContain(v, dist))
            {
                continue;
            }

            if (b.Contains(v))
            {
                return true;
            }

            for (int fi = 0; fi < b.Faces.Length; fi++)
            {
                if (PointPolygonDistance(v, b, fi) <= dist)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double PointPolygonDistance(D3 p, WorldPolyhedron poly, int fi)
    {
        int[] f = poly.Faces[fi];
        if (f.Length == 0)
        {
            return double.MaxValue;
        }

        D3 n = WorldPolyhedron.Newell(poly.Vertices, f);
        if (f.Length >= 3 && n.Dot(n) > 0.5)
        {
            double d = n.Dot(p - poly.Vertices[f[0]]);
            D3 q = p - (n * d);
            if (InsidePolygon(q, n, poly.Vertices, f))
            {
                return Math.Abs(d);
            }
        }

        double best = double.MaxValue;
        for (int i = 0; i < f.Length; i++)
        {
            best = Math.Min(best, SegmentDistance(p, poly.Vertices[f[i]], poly.Vertices[f[(i + 1) % f.Length]]));
        }

        return best;
    }

    private static bool InsidePolygon(D3 q, D3 n, D3[] v, int[] f)
    {
        int sign = 0;
        for (int i = 0; i < f.Length; i++)
        {
            D3 a = v[f[i]], b = v[f[(i + 1) % f.Length]];
            double s = (b - a).Cross(q - a).Dot(n);
            if (Math.Abs(s) < 1e-12)
            {
                continue;
            }

            int sg = s > 0 ? 1 : -1;
            if (sign == 0)
            {
                sign = sg;
            }
            else if (sg != sign)
            {
                return false;
            }
        }

        return true;
    }

    private static double SegmentDistance(D3 p, D3 a, D3 b)
    {
        D3 ab = b - a;
        double len2 = ab.Dot(ab);
        double t = len2 < 1e-18 ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return (p - (a + (ab * t))).Length;
    }

    /// <summary>True when an edge of <paramref name="a"/> passes through a face of <paramref name="b"/>.</summary>
    private static bool EdgesCross(WorldPolyhedron a, WorldPolyhedron b)
    {
        foreach (int[] f in a.Faces)
        {
            for (int i = 0; i < f.Length; i++)
            {
                D3 p = a.Vertices[f[i]], q = a.Vertices[f[(i + 1) % f.Length]];
                for (int fi = 0; fi < b.Faces.Length; fi++)
                {
                    int[] g = b.Faces[fi];
                    if (g.Length < 3)
                    {
                        continue;
                    }

                    D3 n = WorldPolyhedron.Newell(b.Vertices, g);
                    double dp = n.Dot(p - b.Vertices[g[0]]), dq = n.Dot(q - b.Vertices[g[0]]);
                    if ((dp > 0 && dq > 0) || (dp < 0 && dq < 0) || Math.Abs(dp - dq) < 1e-12)
                    {
                        continue;
                    }

                    D3 x = p + ((q - p) * (dp / (dp - dq)));
                    if (InsidePolygon(x, n, b.Vertices, g))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // ---- oversize split -------------------------------------------------------------------------------------

    private static void Split(List<Member> comp, HarvestOptions o, List<List<Member>> output)
    {
        var stack = new Stack<List<Member>>();
        stack.Push(comp);
        while (stack.Count > 0)
        {
            List<Member> c = stack.Pop();
            if (WithinLimits(c, o))
            {
                output.Add(c);
                continue;
            }

            // Detach the member with the largest bounds diagonal (ties: earliest), then re-split the rest.
            Member big = c.OrderByDescending(m => m.Shape.Size.Length).ThenBy(m => m.Index).First();
            output.Add(new List<Member> { big });
            List<Member> rest = c.Where(m => m != big).ToList();
            if (rest.Count == 0)
            {
                continue;
            }

            foreach (List<Member> sub in Components(rest, o, CancellationToken.None))
            {
                stack.Push(sub);
            }
        }
    }

    private static bool WithinLimits(List<Member> c, HarvestOptions o)
    {
        if (c.Count == 1)
        {
            return true;
        }

        if (c.Count > o.MaxComponentMembers)
        {
            return false;
        }

        D3 min = c[0].Shape.Min, max = c[0].Shape.Max;
        foreach (Member m in c)
        {
            min = D3.Min(min, m.Shape.Min);
            max = D3.Max(max, m.Shape.Max);
        }

        D3 s = max - min;
        return s.X <= o.MaxComponentHorizontal && s.Z <= o.MaxComponentHorizontal && s.Y <= o.MaxComponentHeight;
    }

    // ---- per-component analysis ---------------------------------------------------------------------------------

    private static void Analyze(List<Member> comp, BrushSolidityIndex index, HarvestLevelContext ctx, HarvestOptions o, LevelHarvestResult result, IReadOnlyDictionary<string, double> levelStyles)
    {
        if (comp.All(m => m.Air))
        {
            // A lone niche with nothing in it: a plain recess is only kept when it has detail around it.
            if (comp.Count == 1)
            {
                result.Reject("plain_box");
                return;
            }
        }

        var uids = new HashSet<int>(comp.Select(m => m.Brush.Uid));
        var framing = new ModuleFraming(index, o, uids);
        FrameMeasurement frame = framing.Measure(comp.Select(m => m.Shape).ToList());

        // Choose the quarter turn that gives the smallest canonical geometry hash (rotation-invariant identity).
        List<LocalMember>? bestLocal = null;
        string? bestHash = null;
        int bestTurn = 0;
        foreach (int k in frame.Turns.Distinct().OrderBy(k => k))
        {
            List<LocalMember> local = ToLocal(comp, frame, k);
            string h = ModuleShape.FoldedGeometryHash(local);
            if (bestHash is null || string.CompareOrdinal(h, bestHash) < 0)
            {
                bestHash = h;
                bestLocal = local;
                bestTurn = k;
            }
        }

        List<LocalMember> members = bestLocal!;
        double yaw = YawFrame.Normalize(frame.Theta + (90.0 * bestTurn));
        D3 originWorld = frame.Pivot + YawFrame.Rotate(frame.Origin, frame.Theta);
        originWorld = new D3(ModuleFraming.Q(originWorld.X, 1.0 / 1024), ModuleFraming.Q(originWorld.Y, 1.0 / 1024), ModuleFraming.Q(originWorld.Z, 1.0 / 1024));

        D3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        foreach (LocalMember m in members)
        {
            min = D3.Min(min, m.Min);
            max = D3.Max(max, m.Max);
        }

        D3 size = max - min;
        double longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        int faceCount = comp.Sum(m => m.Brush.Geometry.Faces.Count);

        // Quality gate (size and complexity first).
        if (longest < o.MinModuleExtent)
        {
            result.Reject("too_small");
            return;
        }

        // A stick or a sliver (two extents under the minimum thickness) is no usable module.
        double[] sorted = { size.X, size.Y, size.Z };
        Array.Sort(sorted);
        if (sorted[1] < o.MinModuleThickness)
        {
            result.Reject("degenerate");
            return;
        }

        if (longest > o.MaxModuleExtent)
        {
            result.Reject("too_large");
            return;
        }

        if (comp.Count > o.MaxModuleBrushes)
        {
            result.Reject("too_many_brushes");
            return;
        }

        if (faceCount > o.MaxModuleFaces)
        {
            result.Reject("too_many_faces");
            return;
        }

        if (frame.Attach == "free" && comp.Count == 1)
        {
            result.Reject("free_single");
            return;
        }

        // Tile families (per axis) and stretch slices.
        var tiles = new List<ModuleTileFamily>();
        var tileMembersByAxis = new Dictionary<int, HashSet<int>>();
        for (int axis = 0; axis < 3; axis++)
        {
            tileMembersByAxis[axis] = new HashSet<int>();
        }

        var slices = new ModuleSlice?[3];
        var familiesByAxis = new Dictionary<int, List<(int Axis, double Pitch, List<List<int>> Units, double FirstCentre)>>();
        for (int axis = 0; axis < 3; axis++)
        {
            var fams = ModuleShape.TileFamilies(members, axis, o.MinTileUnits, o.TileTolerance);
            familiesByAxis[axis] = fams;
            foreach (var f in fams)
            {
                foreach (int idx in f.Units.SelectMany(u => u))
                {
                    tileMembersByAxis[axis].Add(idx);
                }
            }
        }

        bool[] allowed = AllowedAxes(frame.Attach, members);
        for (int axis = 0; axis < 3; axis++)
        {
            if (allowed[axis])
            {
                slices[axis] = ModuleShape.Slice(members, axis, tileMembersByAxis[axis], o.MinSliceLength, o.MinSliceFraction);
            }
        }

        // Member indices in module order (0..n-1) for the index; members[i].Index is the source time index.
        var moduleIndex = new Dictionary<int, int>();
        for (int i = 0; i < members.Count; i++)
        {
            moduleIndex[members[i].Index] = i;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            foreach (var f in familiesByAxis[axis])
            {
                double first = f.FirstCentre;
                double last = first + (f.Pitch * (f.Units.Count - 1));
                ModuleSlice? zone = slices[axis];
                double zoneA = zone?.A ?? min[axis], zoneB = zone?.B ?? max[axis];
                tiles.Add(new ModuleTileFamily
                {
                    Axis = ModuleVocabulary.AxisName(axis),
                    Pitch = Math.Round(f.Pitch * 1024) / 1024,
                    Count = f.Units.Count,
                    First = f.Units[0].Select(i => moduleIndex[i]).OrderBy(i => i).ToList(),
                    Replicas = f.Units.Skip(1).SelectMany(u => u).Select(i => moduleIndex[i]).OrderBy(i => i).ToList(),
                    FirstCenter = Math.Round(first * 1024) / 1024,
                    MarginStart = Math.Round((first - zoneA) * 1024) / 1024,
                    MarginEnd = Math.Round((zoneB - last) * 1024) / 1024,
                });
            }
        }

        // Features for classification.
        var textures = TextureAreas(comp);
        ModuleFeatures features = Features(members, comp, frame, size, min, max, tiles, textures);
        (string category, double classConf) = ModuleClassifier.Classify(features);

        // Plain single box with one texture: only kept as a clear trim / beam / pilaster / panel by attachment.
        if (comp.Count == 1 && IsPlainBox(comp[0].Shape) && textures.Count <= 1
            && category is not ("base_trim" or "top_trim" or "band" or "beam" or "pilaster" or "wall_panel" or "column" or "ledge"))
        {
            result.Reject("plain_box");
            return;
        }

        string role = ModuleClassifier.Role(category, features);

        // Styles: own textures (area-weighted) blended with the level and the manifest hint.
        var own = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string tex, double area) in textures)
        {
            string st = ModuleStyles.StyleOf(tex, ctx.TextureCategory?.Invoke(tex));
            own[st] = own.GetValueOrDefault(st) + area;
        }

        List<string> styles = ModuleStyles.Blend(own, levelStyles, ctx.StyleHint, o.LevelStyleWeight, o.HintStyleWeight, o.MinStyleShare);

        // Textures.
        var texList = textures.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Select(t =>
        {
            bool stock = ctx.IsStockTexture?.Invoke(t) ?? false;
            string? source = stock ? null : ctx.CustomTextureSource?.Invoke(t);
            return new ModuleTexture(t, ModuleStyles.Family(t), stock, source);
        }).ToList();
        double texValid = ctx.TextureResolves is null ? 0.5 : texList.Count == 0 ? 0 : texList.Count(t => ctx.TextureResolves(t.Name)) / (double)texList.Count;

        // Recess depth.
        bool hasAir = members.Any(m => m.Air);
        double? recess = null;
        if (hasAir)
        {
            var airs = members.Where(m => m.Air).ToList();
            recess = frame.Attach is "wall" or "corner" ? Math.Max(0, -airs.Min(m => m.Min.Z)) : airs.Min(m => Math.Min(m.Size.X, Math.Min(m.Size.Y, m.Size.Z)));
        }

        // Local brushes (module order = time order), positioned at their local centroid.
        List<Brush> localBrushes = BuildLocalBrushes(comp, members);

        double contactConf = Math.Clamp(frame.ContactConfidence, 0, 1);
        double memberScore = comp.Count switch
        {
            1 => 0.5,
            <= 20 => 1.0,
            _ => 20.0 / comp.Count,
        };
        double baseScore = (0.20 * contactConf) + (0.15 * memberScore) + (0.15 * texValid) + (0.20 * classConf);

        string texNames = string.Join(",", texList.Select(t => t.Name.ToLowerInvariant()));
        string exact = $"{category}|{frame.Attach}|{bestHash}|{texNames}";
        string near = string.Join("|", category, frame.Attach, styles[0], comp.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Q4(size.X), Q4(size.Y), Q4(size.Z),
            string.Join(";", members.Select(m => $"{Q4(m.Size.X)},{Q4(m.Size.Y)},{Q4(m.Size.Z)}").OrderBy(s => s, StringComparer.Ordinal)),
            PrimaryFamily(textures));

        var info = new ModuleInfo
        {
            Category = category,
            Role = role,
            Attach = frame.Attach,
            Styles = styles,
            Size = new D3(ModuleJson.R(size.X), ModuleJson.R(size.Y), ModuleJson.R(size.Z)),
            BoundsMin = min,
            BoundsMax = max,
            StretchX = slices[0],
            StretchY = slices[1],
            StretchZ = slices[2],
            Tiles = tiles,
            Textures = texList,
            Brushes = comp.Count,
            Faces = faceCount,
            HasAir = hasAir,
            RecessDepth = recess,
            MountHeight = frame.Attach is "ceiling" or "opening" ? null : frame.MountHeight,
            CeilingGap = frame.CeilingGap,
            Opening = frame.Opening,
            Occurrences = 1,
        };

        result.Modules.Add(new HarvestedModule
        {
            Info = info,
            LocalBrushes = localBrushes,
            ExactKey = exact,
            NearKey = near,
            BaseScore = baseScore,
            ContactConfidence = contactConf,
            ClassConfidence = classConf,
            TextureValidity = texValid,
            Provenance = new ModuleProvenance(ctx.Level, ctx.Packfile, originWorld, yaw, comp.Select(m => m.Brush.Uid).ToList()),
        });
    }

    private static string Q4(double v) => Math.Round(v * 4, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string PrimaryFamily(Dictionary<string, double> textures) =>
        textures.Count == 0 ? "none" : ModuleStyles.Family(textures.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key);

    /// <summary>Axes that may stretch for an attachment kind (see the spec table).</summary>
    private static bool[] AllowedAxes(string attach, List<LocalMember> members) => attach switch
    {
        "wall" => new[] { true, true, false },
        "corner" => new[] { false, true, false },
        "span" => new[] { true, false, false },
        "column" => new[] { false, true, false },
        "edge" => new[] { true, false, false },
        "opening" => new[] { true, true, false },
        "ceiling" => new[] { true, false, true },
        _ => new[] { ModuleShape.SingleExtrusion(members, 0), ModuleShape.SingleExtrusion(members, 1), ModuleShape.SingleExtrusion(members, 2) },
    };

    private static List<LocalMember> ToLocal(List<Member> comp, FrameMeasurement frame, int turn)
    {
        var list = new List<LocalMember>(comp.Count);
        foreach (Member m in comp)
        {
            var verts = new D3[m.Shape.Vertices.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                D3 theta = YawFrame.Rotate(m.Shape.Vertices[i] - frame.Pivot, -frame.Theta);
                D3 l = YawFrame.Rotate(theta - frame.Origin, -90.0 * turn);
                verts[i] = new D3(ModuleFraming.Q(l.X, 1.0 / 1024), ModuleFraming.Q(l.Y, 1.0 / 1024), ModuleFraming.Q(l.Z, 1.0 / 1024));
            }

            Geometry g = m.Brush.Geometry;
            string[] tex = g.Faces.Select(f => f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty).ToArray();
            list.Add(LocalMember.Create(m.Index, verts, m.Shape.Faces, tex, m.Air));
        }

        return list;
    }

    /// <summary>The module's brushes in its local frame: vertices relative to each brush's centroid, identity rotation.</summary>
    private static List<Brush> BuildLocalBrushes(List<Member> comp, List<LocalMember> members)
    {
        var list = new List<Brush>(comp.Count);
        for (int i = 0; i < comp.Count; i++)
        {
            Brush src = comp[i].Brush;
            LocalMember lm = members[i];
            D3 c = D3.Zero;
            foreach (D3 v in lm.Vertices)
            {
                c += v;
            }

            c = lm.Vertices.Length == 0 ? c : c * (1.0 / lm.Vertices.Length);
            c = new D3(ModuleFraming.Q(c.X, 1.0 / 1024), ModuleFraming.Q(c.Y, 1.0 / 1024), ModuleFraming.Q(c.Z, 1.0 / 1024));
            Geometry sg = src.Geometry;
            var g = new Geometry
            {
                Unknown1 = 0,
                Modifiability = sg.Modifiability,
                Name = sg.Name,
                ModifiabilityOld = sg.ModifiabilityOld,
                Textures = new List<string>(sg.Textures),
                Vertices = lm.Vertices.Select(v => (v - c).ToVec3()).ToList(),
            };
            var ids = new HashSet<int>();
            foreach (Face f in sg.Faces)
            {
                ids.Add(f.FaceId);
                g.Faces.Add(new Face
                {
                    Plane = f.Plane,
                    Texture = f.Texture,
                    SurfaceIndex = -1,
                    FaceId = f.FaceId,
                    Flags = f.Flags,
                    SmoothingGroups = f.SmoothingGroups,
                    RoomIndex = -1,
                    Vertices = f.Vertices.Select(fv => new FaceVertex { Index = fv.Index, TextureCoords = fv.TextureCoords }).ToList(),
                });
            }

            foreach (FaceScrollData s in sg.FaceScrollData.Concat(sg.LegacyFaceScrollData))
            {
                if (ids.Contains(s.FaceId) && g.FaceScrollData.All(x => x.FaceId != s.FaceId))
                {
                    g.FaceScrollData.Add(new FaceScrollData { FaceId = s.FaceId, UVelocity = s.UVelocity, VVelocity = s.VVelocity });
                }
            }

            GeometryUtil.RecomputeAllPlanes(g);
            list.Add(new Brush
            {
                Uid = i + 1,
                Position = c.ToVec3(),
                Rotation = Mat3.Identity,
                Geometry = g,
                Flags = src.Flags & ~(uint)BrushFlags.Geoable,
                Life = src.Life,
                State = BrushState.Normal,
            });
        }

        return list;
    }

    private static Dictionary<string, double> TextureAreas(List<Member> comp)
    {
        var areas = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (Member m in comp)
        {
            Geometry g = m.Brush.Geometry;
            for (int fi = 0; fi < g.Faces.Count && fi < m.Shape.Faces.Length; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Texture < 0 || f.Texture >= g.Textures.Count)
                {
                    continue;
                }

                string t = g.Textures[f.Texture];
                if (t.Length == 0 || t.Contains("invisible", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                areas[t] = areas.GetValueOrDefault(t) + m.Shape.FaceArea(fi);
            }
        }

        return areas;
    }

    private static bool IsPlainBox(WorldPolyhedron s)
    {
        if (!s.Convex || s.Faces.Length != 6)
        {
            return false;
        }

        double boxVol = s.Size.X * s.Size.Y * s.Size.Z;
        return boxVol > 0 && Math.Abs(s.Volume - boxVol) <= 0.01 * boxVol;
    }

    private static ModuleFeatures Features(List<LocalMember> members, List<Member> comp, FrameMeasurement frame, D3 size, D3 min, D3 max,
        List<ModuleTileFamily> tiles, Dictionary<string, double> textures)
    {
        double total = 0, off = 0, sloped = 0, flatTop = 0;
        double footprint = Math.Max(1e-6, size.X * size.Z);
        double roundVol = 0, solidVol = 0, airVol = 0;
        foreach (LocalMember m in members)
        {
            var poly = WorldPolyhedron.Create(m.Vertices, m.Faces);
            for (int fi = 0; fi < poly.Faces.Length; fi++)
            {
                double a = poly.FaceArea(fi);
                D3 n = poly.Normals[fi];
                total += a;
                double ax = Math.Max(Math.Abs(n.X), Math.Max(Math.Abs(n.Y), Math.Abs(n.Z)));
                if (ax < Math.Cos(5 * Math.PI / 180))
                {
                    off += a;
                }

                if (n.Y is >= 0.2 and <= 0.95)
                {
                    sloped += a * n.Y;
                }

                if (n.Y > 0.95 && poly.Faces[fi].Length > 0 && WorldPolyhedron.Centroid(poly.Vertices, poly.Faces[fi]).Y >= max.Y - 0.1)
                {
                    flatTop += a;
                }
            }

            double vol = poly.Closed ? poly.Volume : 0;
            if (m.Air)
            {
                airVol += vol;
            }
            else
            {
                solidVol += vol;
                if (IsRoundPrism(poly))
                {
                    roundVol += vol;
                }
            }
        }

        var tokens = new Dictionary<string, double>(StringComparer.Ordinal);
        double texTotal = textures.Values.Sum();
        foreach ((string tex, double area) in textures)
        {
            foreach (string tok in ModuleClassifier.TokensOf(tex))
            {
                tokens[tok] = tokens.GetValueOrDefault(tok) + (texTotal > 0 ? area / texTotal : 0);
            }
        }

        int distinct = members.Select(m => $"{Q4(m.Size.X)},{Q4(m.Size.Y)},{Q4(m.Size.Z)}").Distinct().Count();
        return new ModuleFeatures
        {
            Attach = frame.Attach,
            Size = size,
            Members = members.Count,
            SolidMembers = members.Count(m => !m.Air),
            AirMembers = members.Count(m => m.Air),
            Faces = comp.Sum(m => m.Brush.Geometry.Faces.Count),
            FloorTouch = frame.FloorTouch,
            CeilingTouch = frame.CeilingTouch,
            MountHeight = frame.MountHeight,
            CeilingGap = frame.CeilingGap,
            Opening = frame.Opening,
            OpeningInWall = frame.OpeningInWall,
            OffAxis = total > 0 ? off / total : 0,
            SlopedTop = sloped / footprint,
            FlatTop = flatTop / footprint,
            Roundness = solidVol > 0 ? roundVol / solidVol : 0,
            AirRatio = solidVol > 0 ? airVol / solidVol : airVol > 0 ? double.PositiveInfinity : 0,
            StairSteps = StairSteps(members),
            DistinctShapes = distinct,
            Tiles = tiles,
            Tokens = tokens,
        };
    }

    /// <summary>A prism with ≥ 8 side faces around one axis and a roundish (aspect ≥ 0.6) cross-section.</summary>
    private static bool IsRoundPrism(WorldPolyhedron p)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            int sides = 0;
            foreach (D3 n in p.Normals)
            {
                if (Math.Abs(n[axis]) < 0.05 && n.Dot(n) > 0.5)
                {
                    sides++;
                }
            }

            if (sides < 8)
            {
                continue;
            }

            double a = p.Size[(axis + 1) % 3], b = p.Size[(axis + 2) % 3];
            if (Math.Min(a, b) >= 0.6 * Math.Max(a, b) && p.Size[axis] >= 0.5 * Math.Max(a, b))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>≥ 3 solid treads whose tops rise 0.1–0.5 m per step while advancing 0.15–1.2 m along one horizontal axis.</summary>
    private static bool StairSteps(List<LocalMember> members)
    {
        var solids = members.Where(m => !m.Air).ToList();
        if (solids.Count < 3)
        {
            return false;
        }

        foreach (int axis in new[] { 0, 2 })
        {
            foreach (int dir in new[] { 1, -1 })
            {
                var byTop = solids.OrderBy(m => m.Max.Y).ToList();
                int run = 1;
                for (int i = 1; i < byTop.Count; i++)
                {
                    double rise = byTop[i].Max.Y - byTop[i - 1].Max.Y;
                    double adv = (byTop[i].Centre[axis] - byTop[i - 1].Centre[axis]) * dir;
                    if (rise is >= 0.1 and <= 0.5 && adv is >= 0.15 and <= 1.2)
                    {
                        run++;
                        if (run >= 3)
                        {
                            return true;
                        }
                    }
                    else if (rise > 0.01)
                    {
                        run = 1;
                    }
                }
            }
        }

        return false;
    }
}
