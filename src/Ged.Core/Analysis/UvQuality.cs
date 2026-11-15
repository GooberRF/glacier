using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;

namespace Ged.Core.Analysis;

/// <summary>One textured polygon measured by <see cref="UvQuality"/>: world corners, the visible-side unit normal, per-corner UVs (tiles) and the texture.</summary>
public sealed record UvQualityFace(Vec3d[] Poly, Vec3d Normal, Uv[] Uvs, string Texture, int Source);

/// <summary>A located texturing problem: what, where (world), how bad (pixels or ratio) and over how much (metres or m²).</summary>
public sealed record UvQualityIssue(string Kind, Vec3d At, double Severity, double Extent, string Texture, int Source)
{
    /// <summary>The issue as JSON (world position rounded to centimetres).</summary>
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["pos"] = new JsonArray(Math.Round(At.X, 2), Math.Round(At.Y, 2), Math.Round(At.Z, 2)),
        ["severity"] = Math.Round(Severity, 2),
        ["extent"] = Math.Round(Extent, 2),
        ["texture"] = Texture,
        ["source"] = Source,
    };
}

/// <summary>
/// Texture-UV quality of a set of faces, the way a mapper judges it in the editor: whether the texture runs on
/// unbroken across the seams between faces that share a texture (flat neighbours, the vertical corners of walls,
/// the folds of sloped and rock surfaces), whether it keeps one texel density (pixels per metre of each texture's real
/// size), whether it is stretched or sheared (sloped faces box-mapped from above, skewed fits), whether wall textures
/// stand upright and keep their tile lines on the floor and ceiling lines, and whether faces line a tile edge up with
/// one of their own edges. Measured on world polygons (the compiled geometry of a level, or a set of brush faces); all
/// fractions are length- (seams) or area-weighted. Every metric is null when nothing qualifies for it.
/// </summary>
public static class UvQuality
{
    /// <summary>Pixels of mismatch a seam may show and still read as continuous.</summary>
    public const double SeamTolerancePx = 1.5;

    /// <summary>Pixels a tile line may sit off an edge and still read as lined up on it.</summary>
    public const double LineTolerancePx = 2.0;

    /// <summary>Anisotropy (larger over smaller pixels per metre along U and V) above which a face reads as stretched.</summary>
    public const double StretchRatio = 1.2;

    /// <summary>Shear (degrees off a right angle between the U and V directions) above which a face reads as skewed.</summary>
    public const double ShearDegrees = 6;

    /// <summary>Shortest shared edge (metres) a seam is measured on.</summary>
    public const double MinSeam = 0.2;

    private const int FallbackSize = 256;

    private sealed class Rec
    {
        public required UvQualityFace F;
        public double Area;
        public Vec3d Centroid;
        public int W;
        public int H;
        public bool SizeKnown;
        public bool Ok;
        public Vec3d Gu;
        public Vec3d Gv;
        public Vec3d P0;
        public double U0;
        public double V0;

        public double U(Vec3d p) => U0 + Gu.Dot(p.Sub(P0));

        public double V(Vec3d p) => V0 + Gv.Dot(p.Sub(P0));

        public bool IsWall => Math.Abs(F.Normal.Y) < 0.3;
    }

    /// <summary>The visible textured faces of compiled geometry (portals, sky-room faces, liquid and sky surfaces and invisible helpers left out).</summary>
    public static List<UvQualityFace> FacesOf(Geometry geo)
    {
        ArgumentNullException.ThrowIfNull(geo);
        var list = new List<UvQualityFace>();
        for (int fi = 0; fi < geo.Faces.Count; fi++)
        {
            Face f = geo.Faces[fi];
            if (f.IsPortalFace || f.Vertices.Count < 3 || (f.Flags & (ushort)(FaceFlags.LiquidSurface | FaceFlags.ShowSky)) != 0)
            {
                continue;
            }

            if (f.RoomIndex >= 0 && f.RoomIndex < geo.Rooms.Count && geo.Rooms[f.RoomIndex].IsSkyroom != 0)
            {
                continue;
            }

            if (SpaceSampler.IsInvisibleTexture(geo, f) || SpaceSampler.Polygon(geo, f) is not { } pg)
            {
                continue;
            }

            string tex = f.Texture >= 0 && f.Texture < geo.Textures.Count ? geo.Textures[f.Texture].ToLowerInvariant() : string.Empty;
            list.Add(new UvQualityFace(pg.Poly, pg.Normal, f.Vertices.Select(v => v.TextureCoords).ToArray(), tex, fi));
        }

        return list;
    }

    /// <summary>
    /// The world faces of brushes as a viewer sees them: the winding's normal is the visible side (solid brushes wind
    /// outward, air brushes inward, toward the room they carve). Portal and invisible faces are left out.
    /// Each face's source is its brush UID.
    /// </summary>
    public static List<UvQualityFace> FacesOf(IEnumerable<Brush> brushes, IReadOnlyList<string> textures)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(textures);
        var list = new List<UvQualityFace>();
        foreach (Brush b in brushes)
        {
            foreach (Face f in b.Geometry.Faces)
            {
                if (f.Vertices.Count < 3 || f.IsPortalFace)
                {
                    continue;
                }

                string tex = f.Texture >= 0 && f.Texture < b.Geometry.Textures.Count ? b.Geometry.Textures[f.Texture].ToLowerInvariant()
                    : f.Texture >= 0 && f.Texture < textures.Count ? textures[f.Texture].ToLowerInvariant() : string.Empty;
                if (tex.Contains("invisible", StringComparison.Ordinal))
                {
                    continue;
                }

                var poly = new Vec3d[f.Vertices.Count];
                bool ok = true;
                for (int k = 0; k < poly.Length && ok; k++)
                {
                    int vi = f.Vertices[k].Index;
                    if (vi < 0 || vi >= b.Geometry.Vertices.Count)
                    {
                        ok = false;
                        break;
                    }

                    Vec3 w = b.Position.Add(b.Rotation.Transform(b.Geometry.Vertices[vi]));
                    poly[k] = new Vec3d(w.X, w.Y, w.Z);
                }

                Vec3d nw = Newell(poly);
                if (!ok || nw.Length < 1e-6)
                {
                    continue;
                }

                list.Add(new UvQualityFace(poly, nw.Normalized(), f.Vertices.Select(v => v.TextureCoords).ToArray(), tex, b.Uid));
            }
        }

        return list;
    }

    /// <summary>
    /// Measures <paramref name="faces"/>. <paramref name="textureSize"/> gives each texture's pixel size (null or an
    /// unknown name: 256 × 256 is assumed for the pixel tolerances and density is left unmeasured for that face).
    /// <paramref name="issues"/> (when not null) receives the worst problems, at most <paramref name="maxIssues"/>,
    /// worst first (by severity × extent).
    /// </summary>
    public static JsonObject Measure(IReadOnlyList<UvQualityFace> faces, Func<string, (int W, int H)?>? textureSize,
        List<UvQualityIssue>? issues = null, int maxIssues = 40)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var recs = new List<Rec>(faces.Count);
        foreach (UvQualityFace f in faces)
        {
            Rec r = Prepare(f, textureSize);
            recs.Add(r);
        }

        var found = new List<UvQualityIssue>();

        // ---- per face: density, stretch, upright walls -------------------------------------------------------------
        var ppm = new List<(double V, double A)>();
        double total = 0, known = 0, stretchA = 0, slopeA = 0, slopeStretchA = 0, nonAxisA = 0, nonAxisStretchA = 0;
        var mirrorCand = new List<(Rec R, bool Positive)>();
        double wallA = 0, uprightA = 0, vUpA = 0, vSideA = 0, edgeAlignedA = 0, handPos = 0, handNeg = 0;
        foreach (Rec r in recs)
        {
            total += r.Area;
            if (!r.Ok)
            {
                continue;
            }

            known += r.Area;
            double pu = r.Gu.Length * r.W, pv = r.Gv.Length * r.H;
            if (r.SizeKnown)
            {
                ppm.Add((Math.Sqrt(pu * pv), r.Area));
            }

            double aniso = Math.Max(pu, pv) / Math.Max(1e-9, Math.Min(pu, pv));
            double cos = Math.Abs(r.Gu.Dot(r.Gv)) / Math.Max(1e-12, r.Gu.Length * r.Gv.Length);
            double shear = 90 - (Math.Acos(Math.Min(1, cos)) * 180 / Math.PI);
            bool stretched = aniso > StretchRatio || shear > ShearDegrees;
            if (stretched)
            {
                stretchA += r.Area;
            }

            double ny = Math.Abs(r.F.Normal.Y);
            bool slope = ny > 0.05 && ny < 0.95;
            if (slope)
            {
                slopeA += r.Area;
                if (stretched)
                {
                    slopeStretchA += r.Area;
                }
            }

            bool nonAxis = !IsAxis(r.F.Normal);
            if (nonAxis)
            {
                nonAxisA += r.Area;
                if (stretched)
                {
                    nonAxisStretchA += r.Area;
                }
            }

            if (stretched && r.Area >= 0.5)
            {
                found.Add(new UvQualityIssue(slope ? "stretched_slope" : "stretched", r.Centroid, Math.Max(aniso, 1 + (shear / 45)), r.Area, r.F.Texture, r.F.Source));
            }

            if (r.IsWall)
            {
                wallA += r.Area;
                Vec3d gvDir = r.Gv.Normalized();
                if (gvDir.Y < -0.999)
                {
                    uprightA += r.Area;
                }
                else if (gvDir.Y > 0.999)
                {
                    vUpA += r.Area;
                }
                else if (Math.Abs(gvDir.Y) < 0.017)
                {
                    vSideA += r.Area;
                }

                double handed = r.Gu.Cross(r.Gv).Dot(r.F.Normal);
                if (handed > 0)
                {
                    handPos += r.Area;
                }
                else
                {
                    handNeg += r.Area;
                }

                if (r.Area >= 1)
                {
                    mirrorCand.Add((r, handed > 0));
                }
            }

            if (EdgeOnTileLine(r))
            {
                edgeAlignedA += r.Area;
            }
        }

        // ---- shared edges ------------------------------------------------------------------------------------------
        var seam = new Dictionary<string, double[]>
        {
            ["coplanar"] = new double[3], ["wall_corner"] = new double[3], ["fold"] = new double[3],
        };
        var floorLine = new double[4];
        var ceilLine = new double[4];
        foreach ((Rec a, Rec b, Vec3d p0, Vec3d p1) in SharedEdges(recs))
        {
            double len = p1.Sub(p0).Length;
            double dot = a.F.Normal.Dot(b.F.Normal);
            bool coplanar = dot > 0.999;
            if (a.F.Texture == b.F.Texture)
            {
                if (dot < -0.3 || !a.Ok || !b.Ok)
                {
                    continue; // back to back
                }

                string kind = coplanar ? "coplanar" : a.IsWall && b.IsWall ? "wall_corner" : "fold";
                double[] acc = seam[kind];
                acc[0] += len;
                (double full, double along) = SeamError(a, b, p0, p1);
                if (full <= SeamTolerancePx)
                {
                    acc[1] += len;
                }

                if (along <= SeamTolerancePx)
                {
                    acc[2] += len;
                }
                else if (len >= 0.5)
                {
                    found.Add(new UvQualityIssue("seam_" + kind, p0.Add(p1).Scale(0.5), along, len, a.F.Texture, a.F.Source));
                }

                continue;
            }

            // A wall standing on a floor / hanging from a ceiling along a horizontal edge: where is its tile line?
            if (Math.Abs(p1.Y - p0.Y) > 1e-3)
            {
                continue;
            }

            foreach ((Rec w, Rec o) in new[] { (a, b), (b, a) })
            {
                if (!w.IsWall || !w.Ok || w.Gv.Normalized().Y > -0.999)
                {
                    continue;
                }

                bool floor = o.F.Normal.Y > 0.7 && w.Centroid.Y > p0.Y;
                bool ceil = o.F.Normal.Y < -0.7 && w.Centroid.Y < p0.Y;
                if (!floor && !ceil)
                {
                    continue;
                }

                double[] acc = floor ? floorLine : ceilLine;
                double v = w.V(p0.Add(p1).Scale(0.5));
                double tol = LineTolerancePx / w.H;
                acc[0] += len;
                if (OnLine(v, 1, tol))
                {
                    acc[1] += len;
                }

                if (OnLine(v, 0.5, tol))
                {
                    acc[2] += len;
                }

                if (OnLine(v, 0.25, tol))
                {
                    acc[3] += len;
                }
            }
        }

        double? Frac(double num, double den) => den > 1e-9 ? R(num / den) : null;
        JsonObject SeamJson(double[] acc) => new()
        {
            ["length"] = R(acc[0]),
            ["continuous"] = Frac(acc[1], acc[0]),
            ["aligned"] = Frac(acc[2], acc[0]),
        };
        JsonObject LineJson(double[] acc) => new()
        {
            ["length"] = R(acc[0]),
            ["tile"] = Frac(acc[1], acc[0]),
            ["half"] = Frac(acc[2], acc[0]),
            ["quarter"] = Frac(acc[3], acc[0]),
        };

        JsonObject? density = null;
        if (ppm.Count > 0)
        {
            double med = WeightedQuantile(ppm, 0.5);
            double tw = ppm.Sum(p => p.A);
            density = new JsonObject
            {
                ["coverage"] = Frac(tw, known),
                ["ppm_p10"] = R(WeightedQuantile(ppm, 0.1)),
                ["ppm_p50"] = R(med),
                ["ppm_p90"] = R(WeightedQuantile(ppm, 0.9)),
                ["within25_of_median"] = Frac(ppm.Where(p => Math.Abs(Math.Log2(p.V / med)) <= Math.Log2(1.25)).Sum(p => p.A), tw),
                ["within25_of_64"] = Frac(ppm.Where(p => Math.Abs(Math.Log2(p.V / 64)) <= Math.Log2(1.25)).Sum(p => p.A), tw),
            };
            foreach (Rec r in recs.Where(r => r.Ok && r.SizeKnown && r.Area >= 1))
            {
                double v = Math.Sqrt(r.Gu.Length * r.W * r.Gv.Length * r.H);
                double off = Math.Abs(Math.Log2(v / med));
                if (off > 1)
                {
                    found.Add(new UvQualityIssue("density", r.Centroid, Math.Pow(2, off), r.Area, r.F.Texture, r.F.Source));
                }
            }
        }

        // Walls whose texture reads the other way round from the level's own majority (stock levels: 98 % one way).
        bool majorityPositive = handPos >= handNeg;
        foreach ((Rec r, bool positive) in mirrorCand)
        {
            if (positive != majorityPositive)
            {
                found.Add(new UvQualityIssue("mirrored", r.Centroid, 2, r.Area, r.F.Texture, r.F.Source));
            }
        }

        if (issues is not null)
        {
            issues.AddRange(found.OrderByDescending(i => i.Severity * Math.Sqrt(Math.Max(1e-3, i.Extent))).Take(maxIssues));
        }

        double hand = Math.Max(handPos, handNeg);
        return new JsonObject
        {
            ["faces"] = recs.Count,
            ["area"] = R(total),
            ["uv_known"] = Frac(known, total),
            ["seams"] = new JsonObject
            {
                ["coplanar"] = SeamJson(seam["coplanar"]),
                ["wall_corner"] = SeamJson(seam["wall_corner"]),
                ["fold"] = SeamJson(seam["fold"]),
            },
            ["density"] = density,
            ["stretched"] = Frac(stretchA, known),
            ["slope_area"] = R(slopeA),
            ["slope_stretched"] = Frac(slopeStretchA, slopeA),
            ["nonaxis_area"] = R(nonAxisA),
            ["nonaxis_stretched"] = Frac(nonAxisStretchA, nonAxisA),
            ["walls"] = new JsonObject
            {
                ["area"] = R(wallA),
                ["upright"] = Frac(uprightA, wallA),
                ["upside_down"] = Frac(vUpA, wallA),
                ["sideways"] = Frac(vSideA, wallA),
                ["handedness_major"] = Frac(hand, handPos + handNeg),
                ["handedness_positive"] = Frac(handPos, handPos + handNeg),
                ["floor_line"] = LineJson(floorLine),
                ["ceiling_line"] = LineJson(ceilLine),
            },
            ["edge_aligned"] = Frac(edgeAlignedA, known),
        };
    }

    private static Rec Prepare(UvQualityFace f, Func<string, (int W, int H)?>? textureSize)
    {
        Vec3d c = new(0, 0, 0);
        foreach (Vec3d p in f.Poly)
        {
            c = c.Add(p);
        }

        (int W, int H)? size = textureSize?.Invoke(f.Texture);
        var r = new Rec
        {
            F = f,
            Area = Newell(f.Poly).Length / 2,
            Centroid = c.Scale(1.0 / f.Poly.Length),
            W = size?.W > 0 ? size.Value.W : FallbackSize,
            H = size?.H > 0 ? size.Value.H : FallbackSize,
            SizeKnown = size is { W: > 0, H: > 0 },
        };

        if (f.Uvs.Length != f.Poly.Length || Gradients(f.Poly, f.Uvs, f.Normal) is not { } g)
        {
            return r;
        }

        r.Gu = g.Gu;
        r.Gv = g.Gv;
        r.P0 = f.Poly[0];
        r.U0 = f.Uvs[0].U;
        r.V0 = f.Uvs[0].V;
        r.Ok = g.Gu.Length > 1e-6 && g.Gv.Length > 1e-6 && g.Gu.Cross(g.Gv).Length > 1e-9;
        return r;
    }

    /// <summary>The world gradients of U and V (tiles per metre, in the face plane), least squares over every corner.</summary>
    public static (Vec3d Gu, Vec3d Gv)? Gradients(Vec3d[] poly, Uv[] uvs, Vec3d n)
    {
        Vec3d helper = Math.Abs(n.Y) < 0.9 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0);
        Vec3d e1 = helper.Cross(n).Normalized();
        Vec3d e2 = n.Cross(e1).Normalized();
        Vec3d o = poly[0];
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxu = 0, syu = 0, sxv = 0, syv = 0, su = 0, sv = 0;
        int m = poly.Length;
        for (int i = 0; i < m; i++)
        {
            Vec3d d = poly[i].Sub(o);
            double x = d.Dot(e1), y = d.Dot(e2);
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sx += x;
            sy += y;
            sxu += x * uvs[i].U;
            syu += y * uvs[i].U;
            sxv += x * uvs[i].V;
            syv += y * uvs[i].V;
            su += uvs[i].U;
            sv += uvs[i].V;
        }

        // Centre the normal equations (the constant term drops out).
        double cxx = sxx - (sx * sx / m), cxy = sxy - (sx * sy / m), cyy = syy - (sy * sy / m);
        double cxu = sxu - (sx * su / m), cyu = syu - (sy * su / m), cxv = sxv - (sx * sv / m), cyv = syv - (sy * sv / m);
        double det = (cxx * cyy) - (cxy * cxy);
        if (Math.Abs(det) < 1e-10 * Math.Max(1, cxx * cyy))
        {
            return null;
        }

        double au = ((cxu * cyy) - (cyu * cxy)) / det, bu = ((cyu * cxx) - (cxu * cxy)) / det;
        double av = ((cxv * cyy) - (cyv * cxy)) / det, bv = ((cyv * cxx) - (cxv * cxy)) / det;
        return (e1.Scale(au).Add(e2.Scale(bu)), e1.Scale(av).Add(e2.Scale(bv)));
    }

    /// <summary>
    /// How far two faces' textures disagree along their common segment [p0, p1], in pixels: the full mismatch (the
    /// worst of the two ends, offsets taken modulo whole tiles) and the mismatch along the seam (texture features that
    /// cross the seam do not meet; a jump straight across a corner, such as a box map's U seam, is not counted). A
    /// different texture scale along the seam counts in both.
    /// </summary>
    private static (double Full, double Along) SeamError(Rec a, Rec b, Vec3d p0, Vec3d p1)
    {
        double w = a.W, h = a.H;
        double du0 = a.U(p0) - b.U(p0), dv0 = a.V(p0) - b.V(p0);
        double du1 = a.U(p1) - b.U(p1), dv1 = a.V(p1) - b.V(p1);
        double drift = Math.Sqrt(Sq((du1 - du0) * w) + Sq((dv1 - dv0) * h));
        double ou = (du0 - Math.Round(du0)) * w, ov = (dv0 - Math.Round(dv0)) * h;
        double full = drift + Math.Sqrt((ou * ou) + (ov * ov));

        // The seam's direction in pixel space on face a.
        double su = (a.U(p1) - a.U(p0)) * w, sv = (a.V(p1) - a.V(p0)) * h;
        double sl = Math.Sqrt((su * su) + (sv * sv));
        double along = sl > 1e-9 ? Math.Abs(((ou * su) + (ov * sv)) / sl) : Math.Sqrt((ou * ou) + (ov * ov));

        // An axis-aligned seam (U or V constant along it) repeats every whole tile along that axis: wrap the offset.
        if (sl > 1e-9 && Math.Abs(su) < 1e-3 * sl)
        {
            along = Math.Abs(ov);
        }
        else if (sl > 1e-9 && Math.Abs(sv) < 1e-3 * sl)
        {
            along = Math.Abs(ou);
        }

        return (full, drift + along);
    }

    private static double Sq(double x) => x * x;

    private static bool OnLine(double v, double step, double tol)
    {
        double q = v / step;
        return Math.Abs(q - Math.Round(q)) * step <= tol;
    }

    /// <summary>True when some edge of at least 0.5 m keeps U or V constant on a whole-tile line (a tile edge lined up on it).</summary>
    private static bool EdgeOnTileLine(Rec r)
    {
        Vec3d[] p = r.F.Poly;
        for (int k = 0; k < p.Length; k++)
        {
            Vec3d a = p[k], b = p[(k + 1) % p.Length];
            Vec3d d = b.Sub(a);
            double len = d.Length;
            if (len < 0.5)
            {
                continue;
            }

            double dU = r.Gu.Dot(d) * r.W, dV = r.Gv.Dot(d) * r.H;
            if (Math.Abs(dU) < 0.5 && OnLine(r.U(a), 1, LineTolerancePx / r.W))
            {
                return true;
            }

            if (Math.Abs(dV) < 0.5 && OnLine(r.V(a), 1, LineTolerancePx / r.H))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAxis(Vec3d n) => Math.Max(Math.Abs(n.X), Math.Max(Math.Abs(n.Y), Math.Abs(n.Z))) > 0.999;

    /// <summary>
    /// Every pair of faces meeting along a common segment of at least <see cref="MinSeam"/> (<see cref="SeamFinder"/>),
    /// the two faces lying on opposite sides of it (coplanar overlapping faces, duplicates and decals, are not a seam).
    /// </summary>
    private static IEnumerable<(Rec A, Rec B, Vec3d P0, Vec3d P1)> SharedEdges(List<Rec> recs)
    {
        foreach ((int fa, int fb, Vec3d p0, Vec3d p1) in SeamFinder.Pairs(recs.Select(r => r.F.Poly).ToList(), MinSeam))
        {
            Rec ra = recs[fa], rb = recs[fb];
            Vec3d u = p1.Sub(p0).Normalized();
            Vec3d side = ra.F.Normal.Cross(u);
            Vec3d mid = p0.Add(p1).Scale(0.5);
            if (ra.F.Normal.Dot(rb.F.Normal) > 0.999 && Math.Sign(ra.Centroid.Sub(mid).Dot(side)) == Math.Sign(rb.Centroid.Sub(mid).Dot(side)))
            {
                continue;
            }

            yield return (ra, rb, p0, p1);
        }
    }

    /// <summary>Newell's (unnormalised) polygon normal: its length is twice the area.</summary>
    public static Vec3d Newell(Vec3d[] poly)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            Vec3d a = poly[i], b = poly[(i + 1) % poly.Length];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3d(x, y, z);
    }

    private static double WeightedQuantile(List<(double V, double A)> values, double q)
    {
        List<(double V, double A)> sorted = values.OrderBy(p => p.V).ToList();
        double tw = sorted.Sum(p => p.A), acc = 0;
        foreach ((double v, double a) in sorted)
        {
            acc += a;
            if (acc >= q * tw)
            {
                return v;
            }
        }

        return sorted[^1].V;
    }

    private static double R(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    /// <summary>A compact one-line summary of a report (for logs and the command line).</summary>
    public static string Summary(JsonObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string F(JsonNode? n) => n is null ? "-" : n.GetValue<double>().ToString("0.00", CultureInfo.InvariantCulture);
        JsonNode? s = report["seams"];
        return $"coplanar {F(s?["coplanar"]?["aligned"])}/{F(s?["coplanar"]?["continuous"])}, corners {F(s?["wall_corner"]?["aligned"])}/{F(s?["wall_corner"]?["continuous"])}, " +
            $"folds {F(s?["fold"]?["aligned"])}/{F(s?["fold"]?["continuous"])}, stretched {F(report["stretched"])}, slope stretched {F(report["slope_stretched"])}";
    }
}
