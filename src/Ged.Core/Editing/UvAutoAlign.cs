using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Model;
using Vec3d = Ged.Core.Editing.Organic.Vec3d;

namespace Ged.Core.Editing;

/// <summary>Options of <see cref="UvAutoAlign"/>.</summary>
public sealed class UvAutoAlignOptions
{
    /// <summary>Texel density: pixels of each texture per metre (RED maps stock textures at 64).</summary>
    public double PixelsPerMetre { get; init; } = 64;

    /// <summary>
    /// Scale each group of walls (uniformly, both axes) so its height spans whole texture repeats when that needs at
    /// most <see cref="FitTolerance"/> of change: the texture's top then meets the ceiling line as its bottom meets the floor.
    /// </summary>
    public bool FitHeight { get; init; } = true;

    /// <summary>Largest relative density change <see cref="FitHeight"/> may make (0.15 = 15 %).</summary>
    public double FitTolerance { get; init; } = 0.15;

    /// <summary>Largest angle (degrees) between two faces' normals across a shared edge for the texture to continue around it.</summary>
    public double MaxFoldDegrees { get; init; } = 100;

    /// <summary>
    /// Largest angle (degrees) a texture folded around an edge may turn away from upright on the next face before that
    /// face is set upright again (keeping the seam continuous at its midpoint): rock may drift, walls stay upright.
    /// </summary>
    public double MaxTwistDegrees { get; init; } = 30;

    /// <summary>Leave faces fitted to whole tiles (both axes span whole repeats from a tile corner, as brush:fit_faces leaves a crate side) as they are.</summary>
    public bool KeepFitted { get; init; } = true;

    /// <summary>Pixel size of a texture by name (256 × 256 when null).</summary>
    public Func<string, (int W, int H)>? TextureSize { get; init; }

    /// <summary>
    /// The grain a texture's pixels show (<see cref="TextureGrains.Measure"/>), by name; null = judge grain from the
    /// texture's proportions only (strips).
    /// </summary>
    public Func<string, TextureGrain>? MeasuredGrain { get; init; }

    /// <summary>How thin members (beams, posts, pilasters: <see cref="UvMembers"/>) are mapped.</summary>
    public UvMemberMode Members { get; init; } = UvMemberMode.Auto;
}

/// <summary>How <see cref="UvAutoAlign"/> treats thin structural members (<see cref="UvMembers"/>).</summary>
public enum UvMemberMode
{
    /// <summary>
    /// Long faces of members in a grained texture (girders, trim strips, posts) get the grain laid along the member
    /// (members already laid that way are left as they are); everything else is aligned as usual.
    /// </summary>
    Auto,

    /// <summary>Members are aligned like any other faces.</summary>
    Off,

    /// <summary>Only the grained long faces of members are mapped (or kept); every other face is left as it is.</summary>
    Only,
}

/// <summary>What <see cref="UvAutoAlign.Plan"/> did, in counts of faces (groups: connected walls/slopes sharing one texture run).</summary>
public sealed record UvAutoAlignStats(int Faces, int Horizontal, int Upright, int Groups, int Folded, int Reset, int FittedGroups, int Kept,
    int MemberFaces = 0, int MembersKept = 0);

/// <summary>
/// Neighbour-following texture alignment, the way a RED mapper lines textures up by hand: floors and ceilings take
/// one world-anchored planar map (continuous across every flat neighbour); walls, slopes and rock are gathered into
/// groups of faces that share a texture and meet edge to edge (or overlap in one plane), and each group is mapped from
/// its largest wall or slope face outward — the texture folded around every shared edge onto the next face (coplanar
/// neighbours continue, wall corners and rock facets wrap; a face the fold turns more than
/// <see cref="UvAutoAlignOptions.MaxTwistDegrees"/> from upright is set upright again), so seams inside a group only
/// appear where a loop closes or a fold is reset. The top of a crate, pillar or rock that shares its walls' texture
/// joins their group, so the texture runs over the lip. Each group's texture stands upright
/// (V down the face's slope at true length: no box-map stretch on ramps or rock), stands on the group's lowest line
/// (the floor under a wall, the foot of a ramp), and optionally fits its height to whole repeats. Every face keeps
/// one handedness: the texture reads the right way round from the side the face is seen (stock RED levels' rule,
/// 98–99 % of their wall area). Positions are world (brush transforms applied), so groups run across brushes.
/// </summary>
public static class UvAutoAlign
{
    /// <summary>A face is horizontal (floor or ceiling) when |normal.y| is at least this.</summary>
    public const double HorizontalNy = 0.9995;

    /// <summary>A wall for a flat face to fold onto: |normal.y| below this (steeper than about 72 degrees).</summary>
    private const double WallNy = 0.3;

    /// <summary>A face that a fit may scale: vertical within about 1 degree.</summary>
    private const double VerticalNy = 0.02;

    private sealed class FaceRec
    {
        public required int Brush;
        public required int FaceIndex;
        public required Vec3d[] Poly;
        public required Vec3d N;
        public required string Texture;
        public required double Su;
        public required double Sv;
        public double Area;
        public bool Horizontal;
        public bool Mapped;
        public Vec3d Gu;
        public Vec3d Gv;
        public double Cu;
        public double Cv;
    }

    /// <summary>
    /// Computes the new UVs of every mappable face of <paramref name="brushes"/> (portal, sky and invisible faces are
    /// skipped; with <see cref="UvAutoAlignOptions.KeepFitted"/>, fitted faces too). The result maps (brush index in
    /// the list, face index) to the face's new per-corner UVs; nothing is modified.
    /// </summary>
    public static (Dictionary<(int Brush, int Face), Uv[]> Uvs, UvAutoAlignStats Stats) Plan(IReadOnlyList<Brush> brushes, UvAutoAlignOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        UvAutoAlignOptions o = options ?? new UvAutoAlignOptions();
        double ppm = o.PixelsPerMetre > 0 && double.IsFinite(o.PixelsPerMetre) ? o.PixelsPerMetre : 64;
        var faces = new List<FaceRec>();
        int kept = 0;
        (Dictionary<(int, int), Uv[]> memberUvs, HashSet<(int, int)> memberFaces, int membersKept) = o.Members == UvMemberMode.Off
            ? (new Dictionary<(int, int), Uv[]>(), new HashSet<(int, int)>(), 0)
            : PlanMembers(brushes, o, ppm);
        for (int bi = 0; bi < brushes.Count; bi++)
        {
            Brush b = brushes[bi];
            if ((b.Flags & (uint)BrushFlags.Portal) != 0 || o.Members == UvMemberMode.Only)
            {
                continue;
            }

            Geometry g = b.Geometry;
            double outward = BrushOrientation.OutwardSign(b);
            bool air = (b.Flags & (uint)BrushFlags.Air) != 0;
            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (!Mappable(g, f) || memberFaces.Contains((bi, fi)))
                {
                    continue;
                }

                string tex = g.Textures[f.Texture];

                if (o.KeepFitted && IsFitted(f))
                {
                    kept++;
                    continue;
                }

                var poly = new Vec3d[f.Vertices.Count];
                bool ok = true;
                for (int k = 0; k < poly.Length; k++)
                {
                    int vi = f.Vertices[k].Index;
                    if (vi < 0 || vi >= g.Vertices.Count)
                    {
                        ok = false;
                        break;
                    }

                    Vec3 w = b.Position.Add(b.Rotation.Transform(g.Vertices[vi]));
                    poly[k] = new Vec3d(w.X, w.Y, w.Z);
                }

                Vec3d newell = ok ? UvQuality.Newell(poly) : default;
                if (!ok || newell.Length < 1e-6)
                {
                    continue;
                }

                // The side the face is seen from: outward on a solid brush, inward (into the carved room) on an air brush.
                Vec3d n = newell.Normalized().Scale(outward * (air ? -1 : 1) * WindingSign(b));
                (int w0, int h0) = o.TextureSize?.Invoke(tex) ?? (UvOps.DefaultTextureSize, UvOps.DefaultTextureSize);
                int tw = Math.Max(1, w0), th = Math.Max(1, h0);
                faces.Add(new FaceRec
                {
                    Brush = bi, FaceIndex = fi, Poly = poly, N = n, Texture = tex.ToLowerInvariant(),
                    Su = ppm / tw, Sv = ppm / th, Area = newell.Length / 2, Horizontal = Math.Abs(n.Y) >= HorizontalNy,
                });
            }
        }

        int horizontal = 0, upright = 0, groups = 0, folded = 0, reset = 0, fittedGroups = 0;

        // Which faces meet edge to edge with the same texture, close enough in angle for the texture to fold across.
        var adj = new List<(int Other, double Len, Vec3d P0, Vec3d P1)>[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            adj[i] = new List<(int, double, Vec3d, Vec3d)>();
        }

        double minDot = Math.Cos(Math.Clamp(o.MaxFoldDegrees, 0, 179) * Math.PI / 180);
        foreach ((int a, int b, Vec3d p0, Vec3d p1) in SeamFinder.Pairs(faces.Select(r => r.Poly).ToList(), UvQuality.MinSeam))
        {
            FaceRec ra = faces[a], rb = faces[b];
            if (ra.Texture != rb.Texture || ra.N.Dot(rb.N) < minDot || !OppositeSides(ra, rb, p0, p1))
            {
                continue;
            }

            double len = p1.Sub(p0).Length;
            adj[a].Add((b, len, p0, p1));
            adj[b].Add((a, len, p0, p1));
        }

        // Coplanar faces that overlap (a corridor's wall running on into a room's, the step boxes of a stair side) show
        // as one surface once built: they continue one map even where their edges never meet.
        foreach (List<int> plane in faces.Select((r, i) => (r, i)).Where(x => !x.r.Horizontal)
                     .GroupBy(x => (x.r.Texture, Q(x.r.N.X, 1e-3), Q(x.r.N.Y, 1e-3), Q(x.r.N.Z, 1e-3), Q(x.r.N.Dot(x.r.Poly[0]), 1e-2)))
                     .Select(g => g.Select(x => x.i).ToList()).Where(l => l.Count > 1))
        {
            for (int i = 0; i < plane.Count; i++)
            {
                for (int j = i + 1; j < plane.Count; j++)
                {
                    FaceRec ra = faces[plane[i]], rb = faces[plane[j]];
                    if (Overlap(ra.Poly, rb.Poly) is not { } mid || adj[plane[i]].Any(e => e.Other == plane[j]))
                    {
                        continue;
                    }

                    Vec3d along = ra.N.Cross(Math.Abs(ra.N.Y) < 0.9 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0)).Normalized();
                    adj[plane[i]].Add((plane[j], 0.05, mid, mid.Add(along)));
                    adj[plane[j]].Add((plane[i], 0.05, mid, mid.Add(along)));
                }
            }
        }

        // Floors and ceilings: one world planar map (stock RED: floors U = -x, V = +z; ceilings U = +x, V = +z), so every
        // flat neighbour continues. A flat face that shares its texture with a wall it meets (the top of a crate, a pillar
        // or a rock) joins that wall's group instead, so the texture runs over the lip; one beside a gentle slope (a
        // canyon floor running into its own sloped floor) keeps the world map its flat neighbours have.
        var grouped = new bool[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            FaceRec r = faces[i];
            grouped[i] = !r.Horizontal || adj[i].Any(e => Math.Abs(faces[e.Other].N.Y) < WallNy);
            if (!grouped[i])
            {
                double s = r.N.Y > 0 ? -1 : 1;
                r.Gu = new Vec3d(s * r.Su, 0, 0);
                r.Gv = new Vec3d(0, 0, r.Sv);
                r.Mapped = true;
                horizontal++;
            }
        }

        List<int> restIdx = Enumerable.Range(0, faces.Count).Where(i => grouped[i]).ToList();
        var restPos = new Dictionary<int, int>();
        for (int k = 0; k < restIdx.Count; k++)
        {
            restPos[restIdx[k]] = k;
        }

        List<FaceRec> rest = restIdx.Select(i => faces[i]).ToList();
        var radj = new List<(int Other, double Len, Vec3d P0, Vec3d P1)>[rest.Count];
        for (int k = 0; k < rest.Count; k++)
        {
            radj[k] = adj[restIdx[k]].Where(e => grouped[e.Other]).Select(e => (restPos[e.Other], e.Len, e.P0, e.P1)).ToList();
        }

        adj = radj;

        double maxTwist = Math.Clamp(o.MaxTwistDegrees, 0, 180) * Math.PI / 180;
        var order = Enumerable.Range(0, rest.Count).OrderByDescending(i => rest[i].Area).ToList();
        var inGroup = new bool[rest.Count];
        foreach (int seed in order)
        {
            if (inGroup[seed])
            {
                continue;
            }

            // The group: everything reachable from the seed.
            var members = new List<int>();
            var stack = new Stack<int>();
            stack.Push(seed);
            inGroup[seed] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                members.Add(c);
                foreach ((int other, _, _, _) in adj[c])
                {
                    if (!inGroup[other])
                    {
                        inGroup[other] = true;
                        stack.Push(other);
                    }
                }
            }

            groups++;
            int seedFace = members.Where(m => !rest[m].Horizontal).DefaultIfEmpty(seed).OrderByDescending(m => rest[m].Area).First();
            FaceRec s0 = rest[seedFace];
            double minY = double.MaxValue, maxY = double.MinValue;
            Vec3d lowest = s0.Poly[0];
            foreach (int m in members)
            {
                foreach (Vec3d p in rest[m].Poly)
                {
                    if (p.Y < minY)
                    {
                        minY = p.Y;
                        lowest = p;
                    }

                    maxY = Math.Max(maxY, p.Y);
                }
            }

            double su = s0.Su, sv = s0.Sv;
            if (o.FitHeight && Math.Abs(s0.N.Y) < VerticalNy && maxY - minY > 0.5)
            {
                double tiles = (maxY - minY) * sv;
                double whole = Math.Max(1, Math.Round(tiles));
                double k = whole / tiles;
                if (Math.Abs(k - 1) > 1e-9 && Math.Abs(k - 1) <= o.FitTolerance)
                {
                    su *= k;
                    sv *= k;
                    fittedGroups++;
                }
            }

            // The seed stands upright on the group's lowest point, U anchored in the world along the face.
            (Vec3d down, Vec3d right) = UprightFrame(s0.N);
            s0.Gu = right.Scale(su);
            s0.Gv = down.Scale(sv);
            s0.Cu = 0;
            s0.Cv = -s0.Gv.Dot(lowest);
            s0.Mapped = true;
            upright++;

            // Grow along the longest shared edges first, so the seams that remain fall on short ones.
            var queue = new PriorityQueue<(int From, int To, Vec3d P0, Vec3d P1), double>();
            void Offer(int from)
            {
                foreach ((int other, double len, Vec3d p0, Vec3d p1) in adj[from])
                {
                    if (!rest[other].Mapped)
                    {
                        queue.Enqueue((from, other, p0, p1), -len);
                    }
                }
            }

            Offer(seedFace);
            while (queue.TryDequeue(out (int From, int To, Vec3d P0, Vec3d P1) e, out _))
            {
                FaceRec child = rest[e.To];
                if (child.Mapped)
                {
                    continue;
                }

                FaceRec parent = rest[e.From];
                if (!Fold(parent, child, e.P0, e.P1, maxTwist))
                {
                    reset++;
                }
                else
                {
                    folded++;
                }

                child.Mapped = true;
                Offer(e.To);
            }
        }

        var uvs = new Dictionary<(int, int), Uv[]>(faces.Count);
        foreach (FaceRec r in faces)
        {
            var list = new Uv[r.Poly.Length];

            // Whole-tile shifts keep the stored numbers small; textures repeat, so seams stay continuous.
            double u0 = Math.Floor(r.Gu.Dot(r.Poly[0]) + r.Cu), v0 = Math.Floor(r.Gv.Dot(r.Poly[0]) + r.Cv);
            for (int k = 0; k < r.Poly.Length; k++)
            {
                list[k] = new Uv((float)(r.Gu.Dot(r.Poly[k]) + r.Cu - u0), (float)(r.Gv.Dot(r.Poly[k]) + r.Cv - v0));
            }

            uvs[(r.Brush, r.FaceIndex)] = list;
        }

        foreach (((int bi, int fi), Uv[] list) in memberUvs)
        {
            uvs[(bi, fi)] = list;
        }

        return (uvs, new UvAutoAlignStats(faces.Count + memberUvs.Count, horizontal, upright, groups, folded, reset, fittedGroups, kept,
            memberUvs.Count, membersKept));
    }

    /// <summary>A face texture alignment may map: a textured, visible face (not a portal, sky, liquid surface or invisible helper).</summary>
    private static bool Mappable(Geometry g, Face f) =>
        f.Vertices.Count >= 3 && !f.IsPortalFace && f.Texture >= 0 && f.Texture < g.Textures.Count
        && (f.Flags & (ushort)(FaceFlags.ShowSky | FaceFlags.LiquidSurface)) == 0
        && !g.Textures[f.Texture].Contains("invisible", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The thin members among <paramref name="brushes"/> (<see cref="UvMembers.Detect"/>): the long faces that use a
    /// grained texture get the member mapping (<see cref="UvMembers.Map"/>) unless every one of them already reads as a
    /// mapper lays a member (<see cref="UvMembers.IsGood"/>), in which case they are kept. Returns the planned UVs, every
    /// face handled here (mapped or kept: the wall pass leaves them alone) and how many members were kept.
    /// </summary>
    private static (Dictionary<(int, int), Uv[]> Uvs, HashSet<(int, int)> Handled, int Kept) PlanMembers(IReadOnlyList<Brush> brushes, UvAutoAlignOptions o, double ppm)
    {
        var uvs = new Dictionary<(int, int), Uv[]>();
        var handled = new HashSet<(int, int)>();
        int keptMembers = 0;
        for (int bi = 0; bi < brushes.Count; bi++)
        {
            Brush b = brushes[bi];
            if (UvMembers.Detect(b) is not { } m)
            {
                continue;
            }

            Geometry g = b.Geometry;
            var tex = new Dictionary<int, (TextureGrain Grain, int W, int H)>();
            foreach (UvMembers.MemberFace s in m.Sides)
            {
                Face f = g.Faces[s.FaceIndex];
                if (!Mappable(g, f))
                {
                    continue;
                }

                string name = g.Textures[f.Texture];
                (int w0, int h0) = o.TextureSize?.Invoke(name) ?? (UvOps.DefaultTextureSize, UvOps.DefaultTextureSize);
                int tw = Math.Max(1, w0), th = Math.Max(1, h0);
                TextureGrain grain = TextureGrains.Of(tw, th, o.MeasuredGrain?.Invoke(name) ?? TextureGrain.None);
                if (grain != TextureGrain.None)
                {
                    tex[s.FaceIndex] = (grain, tw, th);
                }
            }

            if (tex.Count == 0)
            {
                continue;
            }

            bool good = m.Sides.Where(s => tex.ContainsKey(s.FaceIndex)).All(s =>
            {
                (TextureGrain grain, int tw, int th) = tex[s.FaceIndex];
                Uv[] cur = g.Faces[s.FaceIndex].Vertices.Select(v => v.TextureCoords).ToArray();
                return UvMembers.IsGood(s.Poly, s.Normal, cur, m.Axis, s.Width, grain, tw, th);
            });
            foreach (int fi in tex.Keys)
            {
                handled.Add((bi, fi));
            }

            if (good)
            {
                keptMembers++;
                continue;
            }

            foreach ((int fi, Uv[] list) in UvMembers.Map(m, fi => tex.TryGetValue(fi, out var t) ? t : null, ppm))
            {
                uvs[(bi, fi)] = list;
            }
        }

        return (uvs, handled, keptMembers);
    }

    /// <summary>Plans and writes the UVs of <paramref name="brushes"/> in place.</summary>
    public static UvAutoAlignStats Apply(IReadOnlyList<Brush> brushes, UvAutoAlignOptions? options = null)
    {
        (Dictionary<(int Brush, int Face), Uv[]> uvs, UvAutoAlignStats stats) = Plan(brushes, options);
        foreach (((int bi, int fi), Uv[] list) in uvs)
        {
            Write(brushes[bi].Geometry.Faces[fi], list);
        }

        return stats;
    }

    /// <summary>Writes planned UVs onto a face (corner order as planned).</summary>
    public static void Write(Face f, Uv[] uvs)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(uvs);
        for (int k = 0; k < f.Vertices.Count && k < uvs.Length; k++)
        {
            f.Vertices[k].TextureCoords = uvs[k];
        }
    }

    /// <summary>
    /// The upright texture frame of a non-horizontal face seen along <paramref name="n"/>: V runs down the face's
    /// slope (straight down on a wall) and U along its level line, with (U × V)·n &gt; 0 so the texture reads the right
    /// way round from that side (stock RED: a wall facing -X runs U along -Z, one facing +Z along -X).
    /// </summary>
    public static (Vec3d Down, Vec3d Right) UprightFrame(Vec3d n)
    {
        var worldDown = new Vec3d(0, -1, 0);
        Vec3d down = worldDown.Sub(n.Scale(worldDown.Dot(n))).Normalized();
        return (down, down.Cross(n).Normalized());
    }

    /// <summary>
    /// Continues <paramref name="parent"/>'s map onto <paramref name="child"/> across [p0, p1] by folding it about the
    /// edge (exact along the whole edge, true length). When the folded texture turns more than
    /// <paramref name="maxTwist"/> away from upright, the child is set upright instead, matched to the parent at the
    /// edge's midpoint. Returns false in that case.
    /// </summary>
    private static bool Fold(FaceRec parent, FaceRec child, Vec3d p0, Vec3d p1, double maxTwist)
    {
        Vec3d axis = p1.Sub(p0).Normalized();
        double angle = Math.Atan2(axis.Dot(parent.N.Cross(child.N)), parent.N.Dot(child.N));
        Vec3d gu = Rotate(parent.Gu, axis, angle), gv = Rotate(parent.Gv, axis, angle);
        double u0 = parent.Gu.Dot(p0) + parent.Cu, v0 = parent.Gv.Dot(p0) + parent.Cv;
        (Vec3d down, Vec3d right) = UprightFrame(child.N);
        double twist = child.Horizontal ? 0 : Math.Acos(Math.Clamp(gv.Normalized().Dot(down), -1, 1));
        if (twist <= maxTwist + 1e-9)
        {
            child.Gu = gu;
            child.Gv = gv;
            child.Cu = u0 - gu.Dot(p0);
            child.Cv = v0 - gv.Dot(p0);
            return true;
        }

        Vec3d mid = p0.Add(p1).Scale(0.5);
        double um = parent.Gu.Dot(mid) + parent.Cu, vm = parent.Gv.Dot(mid) + parent.Cv;
        child.Gu = right.Scale(parent.Gu.Length);
        child.Gv = down.Scale(parent.Gv.Length);
        child.Cu = um - child.Gu.Dot(mid);
        child.Cv = vm - child.Gv.Dot(mid);
        return false;
    }

    private static long Q(double v, double step) => (long)Math.Round(v / step);

    /// <summary>The centre of the overlap of two coplanar polygons' bounding boxes when it spans at least 5 cm both ways in their plane, else null.</summary>
    private static Vec3d? Overlap(Vec3d[] a, Vec3d[] b)
    {
        double[] lo = new double[3], hi = new double[3];
        int wide = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            double a0 = a.Min(p => p[axis]), a1 = a.Max(p => p[axis]), b0 = b.Min(p => p[axis]), b1 = b.Max(p => p[axis]);
            lo[axis] = Math.Max(a0, b0);
            hi[axis] = Math.Min(a1, b1);
            if (hi[axis] - lo[axis] < -1e-3)
            {
                return null;
            }

            if (hi[axis] - lo[axis] > 0.05)
            {
                wide++;
            }
        }

        return wide >= 2 ? new Vec3d((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2) : null;
    }

    private static bool OppositeSides(FaceRec a, FaceRec b, Vec3d p0, Vec3d p1)
    {
        if (a.N.Dot(b.N) <= 0.999)
        {
            return true;
        }

        Vec3d u = p1.Sub(p0).Normalized();
        Vec3d side = a.N.Cross(u);
        Vec3d mid = p0.Add(p1).Scale(0.5);
        return Math.Sign(Centroid(a.Poly).Sub(mid).Dot(side)) != Math.Sign(Centroid(b.Poly).Sub(mid).Dot(side));
    }

    private static Vec3d Centroid(Vec3d[] poly)
    {
        var c = new Vec3d(0, 0, 0);
        foreach (Vec3d p in poly)
        {
            c = c.Add(p);
        }

        return c.Scale(1.0 / poly.Length);
    }

    /// <summary>Rodrigues' rotation of <paramref name="v"/> about unit <paramref name="k"/> by <paramref name="angle"/> radians.</summary>
    private static Vec3d Rotate(Vec3d v, Vec3d k, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return v.Scale(cos).Add(k.Cross(v).Scale(sin)).Add(k.Scale(k.Dot(v) * (1 - cos)));
    }

    /// <summary>-1 when the brush's rotation mirrors (turns the winding inside out in the world), else +1.</summary>
    private static double WindingSign(Brush b) => UvMembers.WindingSign(b);

    /// <summary>
    /// True when a face's UVs span whole repeats along both axes starting on a tile corner (what fitting a crate side or
    /// a panel to its face leaves): such a face was lined up on purpose.
    /// </summary>
    public static bool IsFitted(Face f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.Vertices.Count < 3)
        {
            return false;
        }

        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (FaceVertex v in f.Vertices)
        {
            minU = Math.Min(minU, v.TextureCoords.U);
            maxU = Math.Max(maxU, v.TextureCoords.U);
            minV = Math.Min(minV, v.TextureCoords.V);
            maxV = Math.Max(maxV, v.TextureCoords.V);
        }

        static bool Whole(double x) => Math.Abs(x - Math.Round(x)) < 1e-3;
        return maxU - minU > 0.5 && maxV - minV > 0.5 && Whole(minU) && Whole(maxU) && Whole(minV) && Whole(maxV);
    }
}
