using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>Which texture axes a per-face fit stretches to whole tiles (<see cref="UvAlignOps.FitLocal"/>).</summary>
public enum UvFitAxes
{
    /// <summary>Both axes: the face spans exactly the requested tiles in U and V (the texture may stretch).</summary>
    Both,

    /// <summary>U only: V keeps square texels at the density U ends up with.</summary>
    U,

    /// <summary>V only: U keeps square texels at the density V ends up with (a trim strip: one tile tall, aspect kept).</summary>
    V,
}

/// <summary>
/// Edge- and neighbour-aware UV operations that the projection maps (<see cref="UvOps"/>) cannot express:
/// lining a texture up on one polygon edge (panels standing on the floor line, a frieze hanging from the
/// ceiling line, tread plates starting at a stair nosing, ramp textures running up the slope at true length),
/// continuing one face's mapping across a corner onto a neighbour, and fitting each face of a prop to whole
/// tiles in the brush's own frame. All UVs stay in RF's tile units (an integer step is one texture repeat;
/// +V points down the image) and every operation works on world positions (brush rotation and position
/// applied), like the box map.
/// </summary>
public static class UvAlignOps
{
    /// <summary>
    /// A face's UVs as an affine map of world position: <c>U = Gu·p + Cu</c>, <c>V = Gv·p + Cv</c>, with the
    /// gradients lying in the face plane (tiles per metre along each direction).
    /// </summary>
    public readonly record struct UvAffine(Vec3d Gu, double Cu, Vec3d Gv, double Cv)
    {
        public double U(Vec3d p) => Gu.Dot(p) + Cu;

        public double V(Vec3d p) => Gv.Dot(p) + Cv;
    }

    /// <summary>A double-precision vector for the solves (world coordinates reach the thousands; UV offsets must not drift).</summary>
    public readonly record struct Vec3d(double X, double Y, double Z)
    {
        public static Vec3d From(Vec3 v) => new(v.X, v.Y, v.Z);

        public Vec3d Add(Vec3d o) => new(X + o.X, Y + o.Y, Z + o.Z);

        public Vec3d Sub(Vec3d o) => new(X - o.X, Y - o.Y, Z - o.Z);

        public Vec3d Scale(double s) => new(X * s, Y * s, Z * s);

        public double Dot(Vec3d o) => (X * o.X) + (Y * o.Y) + (Z * o.Z);

        public Vec3d Cross(Vec3d o) => new((Y * o.Z) - (Z * o.Y), (Z * o.X) - (X * o.Z), (X * o.Y) - (Y * o.X));

        public double Length => Math.Sqrt(Dot(this));

        public Vec3d Normalized()
        {
            double l = Length;
            return l > 1e-12 ? Scale(1 / l) : this;
        }
    }

    /// <summary>The world-space corners of a face (brush rotation and position applied), in face order.</summary>
    public static List<Vec3d> WorldCorners(Geometry g, Face f, Mat3 rotation, Vec3 position)
    {
        var list = new List<Vec3d>(f.Vertices.Count);
        foreach (FaceVertex fv in f.Vertices)
        {
            list.Add(Vec3d.From(position.Add(rotation.Transform(g.Vertices[fv.Index]))));
        }

        return list;
    }

    /// <summary>Newell's unit normal of a polygon (zero for a degenerate one).</summary>
    public static Vec3d Normal(IReadOnlyList<Vec3d> poly)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            Vec3d a = poly[i];
            Vec3d b = poly[(i + 1) % poly.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3d(x, y, z).Normalized();
    }

    private static Vec3d Centroid(IReadOnlyList<Vec3d> poly)
    {
        var c = new Vec3d(0, 0, 0);
        foreach (Vec3d p in poly)
        {
            c = c.Add(p);
        }

        return poly.Count > 0 ? c.Scale(1.0 / poly.Count) : c;
    }

    /// <summary>Two orthonormal in-plane directions for normal <paramref name="n"/>.</summary>
    private static (Vec3d E1, Vec3d E2) PlaneBasis(Vec3d n)
    {
        Vec3d helper = Math.Abs(n.Y) < 0.9 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0);
        Vec3d e1 = helper.Cross(n).Normalized();
        return (e1, n.Cross(e1).Normalized());
    }

    /// <summary>
    /// The face's current UVs as a world affine map (least squares over its corners, so a slightly non-planar
    /// or non-affine face still gets its best fit). False when the face has fewer than three non-collinear corners.
    /// </summary>
    public static bool TryGetUvMap(Geometry g, Face f, Mat3 rotation, Vec3 position, out UvAffine map)
    {
        map = default;
        List<Vec3d> c = WorldCorners(g, f, rotation, position);
        if (c.Count < 3)
        {
            return false;
        }

        Vec3d n = Normal(c);
        if (n.Length < 0.5)
        {
            return false;
        }

        (Vec3d e1, Vec3d e2) = PlaneBasis(n);
        Vec3d o = Centroid(c);

        // Normal equations of U = a·x + b·y + k over the in-plane coordinates (x, y), centred so k is the mean.
        double sxx = 0, sxy = 0, syy = 0, sxu = 0, syu = 0, sxv = 0, syv = 0, su = 0, sv = 0;
        for (int i = 0; i < c.Count; i++)
        {
            Vec3d d = c[i].Sub(o);
            double x = d.Dot(e1), y = d.Dot(e2);
            Uv uv = f.Vertices[i].TextureCoords;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            sxu += x * uv.U;
            syu += y * uv.U;
            sxv += x * uv.V;
            syv += y * uv.V;
            su += uv.U;
            sv += uv.V;
        }

        double det = (sxx * syy) - (sxy * sxy);
        if (Math.Abs(det) < 1e-12 * Math.Max(1, sxx * syy))
        {
            return false;
        }

        double au = ((sxu * syy) - (syu * sxy)) / det, bu = ((syu * sxx) - (sxu * sxy)) / det;
        double av = ((sxv * syy) - (syv * sxy)) / det, bv = ((syv * sxx) - (sxv * sxy)) / det;
        Vec3d gu = e1.Scale(au).Add(e2.Scale(bu));
        Vec3d gv = e1.Scale(av).Add(e2.Scale(bv));
        double mu = su / c.Count, mv = sv / c.Count;
        map = new UvAffine(gu, mu - gu.Dot(o), gv, mv - gv.Dot(o));
        return true;
    }

    /// <summary>
    /// The edge (index i: corner i to corner i + 1) whose midpoint lies furthest along <paramref name="direction"/>;
    /// ties keep the lowest index. -1 when every edge ties (no edge is extreme that way, e.g. "bottom" on a level floor).
    /// </summary>
    public static int ExtremeEdge(IReadOnlyList<Vec3d> corners, Vec3d direction)
    {
        int n = corners.Count;
        if (n < 2)
        {
            return -1;
        }

        int best = -1;
        double bestD = double.NegativeInfinity, worstD = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            double d = corners[i].Add(corners[(i + 1) % n]).Scale(0.5).Dot(direction);
            if (d > bestD + 1e-5)
            {
                best = i;
                bestD = d;
            }

            worstD = Math.Min(worstD, d);
        }

        return bestD - worstD <= 1e-5 ? -1 : best;
    }

    /// <summary>The handedness the box map gives a face with world normal <paramref name="n"/>: the sign of (∇U × ∇V)·n.</summary>
    private static double BoxHandedness(Vec3d n)
    {
        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(new Vec3((float)n.X, (float)n.Y, (float)n.Z));
        Vec3d gu = Axis(uAxis), gv = Axis(vAxis).Scale(-1);
        return Math.Sign(gu.Cross(gv).Dot(n)) is var s && s != 0 ? s : 1;
    }

    private static Vec3d Axis(int a) => a switch
    {
        0 => new Vec3d(1, 0, 0),
        1 => new Vec3d(0, 1, 0),
        _ => new Vec3d(0, 0, 1),
    };

    /// <summary>
    /// Lines the texture up on edge <paramref name="edge"/> of the face (corner <c>edge</c> to <c>edge + 1</c>):
    /// U runs along the edge, V across it, and a tile boundary lies on the edge — moved
    /// <paramref name="offsetMetres"/> into the face. With <paramref name="hang"/> false the texture stands on
    /// the edge (the image's bottom row is on it, its top points into the face: panels on a floor line, tread
    /// plates from a stair nosing, a ramp texture running up the slope); with true it hangs from it (the image's
    /// top row on the edge: a frieze under a ceiling line, a riser read from its nosing). U is anchored in world
    /// space (continuous along collinear edges) and keeps the box map's handedness, so the texture is not
    /// mirrored relative to box-mapped neighbours. Scale: <paramref name="tilesPerMetreU"/> / <paramref name="tilesPerMetreV"/>
    /// when given, else the face's current scale along each axis (one tile per
    /// <see cref="GeometryUtil.DefaultTileMetres"/> when its UVs are degenerate). Returns false (and leaves the face
    /// unchanged) for a degenerate face or edge.
    /// </summary>
    public static bool AlignToEdge(Geometry g, Face f, Mat3 rotation, Vec3 position, int edge, bool hang, double offsetMetres,
        double? tilesPerMetreU = null, double? tilesPerMetreV = null)
    {
        List<Vec3d> c = WorldCorners(g, f, rotation, position);
        int n = c.Count;
        if (n < 3 || edge < 0 || edge >= n)
        {
            return false;
        }

        Vec3d normal = Normal(c);
        Vec3d a = c[edge], b = c[(edge + 1) % n];
        Vec3d along = b.Sub(a);
        if (normal.Length < 0.5 || along.Length < 1e-6)
        {
            return false;
        }

        along = along.Normalized();
        Vec3d inward = normal.Cross(along).Normalized();
        if (Centroid(c).Sub(a).Dot(inward) < 0)
        {
            inward = inward.Scale(-1);
        }

        double su, sv;
        if (tilesPerMetreU is double tu && tilesPerMetreV is double tv)
        {
            su = tu;
            sv = tv;
        }
        else if (TryGetUvMap(g, f, rotation, position, out UvAffine current) && current.Gu.Length > 1e-9 && current.Gv.Length > 1e-9)
        {
            su = current.Gu.Length;
            sv = current.Gv.Length;
        }
        else
        {
            su = sv = 1.0 / GeometryUtil.DefaultTileMetres;
        }

        // V grows down the image: standing on the edge, "down" points out of the face across the edge.
        Vec3d vDir = hang ? inward : inward.Scale(-1);
        Vec3d uDir = along;
        if (Math.Sign(uDir.Cross(vDir).Dot(normal)) != BoxHandedness(normal))
        {
            uDir = uDir.Scale(-1);
        }

        for (int i = 0; i < n; i++)
        {
            Vec3d p = c[i];
            double depth = p.Sub(a).Dot(inward) - offsetMetres; // metres from the (offset) boundary into the face
            double u = su * p.Dot(uDir);
            double v = hang ? sv * depth : -sv * depth;
            f.Vertices[i].TextureCoords = new Uv((float)u, (float)v);
        }

        return true;
    }

    /// <summary>
    /// Continues the source face's mapping onto the destination face across their common edge, as if the source
    /// texture were folded around the corner: the source map is rotated about the hinge (the shared edge, else the
    /// line where the two planes meet) into the destination plane, so the UVs agree all along the hinge and keep the
    /// source's scale and direction. Parallel faces (coplanar neighbours, opposite walls) take the source map
    /// unchanged. Returns false (destination unchanged) when the source has no usable mapping or a face is degenerate.
    /// </summary>
    public static bool WrapFrom(Geometry gDst, Face dst, Mat3 rDst, Vec3 pDst, Geometry gSrc, Face src, Mat3 rSrc, Vec3 pSrc)
    {
        if (!TryGetUvMap(gSrc, src, rSrc, pSrc, out UvAffine map))
        {
            return false;
        }

        List<Vec3d> cs = WorldCorners(gSrc, src, rSrc, pSrc);
        List<Vec3d> cd = WorldCorners(gDst, dst, rDst, pDst);
        Vec3d ns = Normal(cs), nd = Normal(cd);
        if (cd.Count < 3 || nd.Length < 0.5)
        {
            return false;
        }

        Vec3d gu = map.Gu, gv = map.Gv;
        Vec3d hinge = default;
        Vec3d axis = default;
        bool rotate = false;

        // A shared edge: two destination corners that coincide with source corners.
        var shared = new List<Vec3d>(2);
        foreach (Vec3d p in cd)
        {
            foreach (Vec3d q in cs)
            {
                if (p.Sub(q).Length < 1e-3 && (shared.Count == 0 || shared[0].Sub(p).Length > 1e-3))
                {
                    shared.Add(p);
                    break;
                }
            }

            if (shared.Count == 2)
            {
                break;
            }
        }

        Vec3d cross = ns.Cross(nd);
        if (cross.Length > 1e-6)
        {
            rotate = true;
            if (shared.Count == 2)
            {
                hinge = shared[0];
                axis = shared[1].Sub(shared[0]).Normalized();
            }
            else
            {
                // The planes' line of intersection: n1·x = d1, n2·x = d2.
                double d1 = ns.Dot(cs[0]), d2 = nd.Dot(cd[0]);
                double l2 = cross.Dot(cross);
                hinge = nd.Cross(cross).Scale(d1).Add(cross.Cross(ns).Scale(d2)).Scale(1 / l2);
                axis = cross.Normalized();
            }
        }

        double uh = map.U(hinge), vh = map.V(hinge);
        if (rotate)
        {
            double angle = Math.Atan2(axis.Dot(ns.Cross(nd)), ns.Dot(nd));
            gu = Rotate(gu, axis, angle);
            gv = Rotate(gv, axis, angle);
        }

        for (int i = 0; i < cd.Count; i++)
        {
            Vec3d p = cd[i];
            double u = rotate ? gu.Dot(p.Sub(hinge)) + uh : map.U(p);
            double v = rotate ? gv.Dot(p.Sub(hinge)) + vh : map.V(p);
            dst.Vertices[i].TextureCoords = new Uv((float)u, (float)v);
        }

        return true;
    }

    /// <summary>Rodrigues' rotation of <paramref name="v"/> about unit <paramref name="k"/> by <paramref name="angle"/> radians.</summary>
    private static Vec3d Rotate(Vec3d v, Vec3d k, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return v.Scale(cos).Add(k.Cross(v).Scale(sin)).Add(k.Scale(k.Dot(v) * (1 - cos)));
    }

    /// <summary>
    /// Fits one face to whole tiles in its brush's own frame, so a crate side, a panel or a door shows exactly
    /// <paramref name="tilesU"/> × <paramref name="tilesV"/> repeats whatever the brush's rotation. The axes are the
    /// face plane's true-length directions nearest the box map's (U horizontal and V down the face on walls), with
    /// the tile corner on the face's top-left extent. With <see cref="UvFitAxes.U"/> or <see cref="UvFitAxes.V"/>
    /// only that axis is fitted and the other keeps square texels for a <paramref name="texW"/>×<paramref name="texH"/>
    /// texture (a trim strip one tile tall, its pattern unstretched). Brush-local, so it needs no transform.
    /// </summary>
    public static bool FitLocal(Geometry g, Face f, UvFitAxes axes, double tilesU, double tilesV, int texW, int texH)
    {
        var c = new List<Vec3d>(f.Vertices.Count);
        foreach (FaceVertex fv in f.Vertices)
        {
            c.Add(Vec3d.From(g.Vertices[fv.Index]));
        }

        if (c.Count < 3)
        {
            return false;
        }

        Vec3d n = Normal(c);
        if (n.Length < 0.5)
        {
            return false;
        }

        (int uAxis, int vAxis) = GeometryUtil.DominantProjection(new Vec3((float)n.X, (float)n.Y, (float)n.Z));
        Vec3d u0 = InPlane(Axis(uAxis), n).Normalized();
        Vec3d v0 = InPlane(Axis(vAxis).Scale(-1), n);
        v0 = v0.Sub(u0.Scale(v0.Dot(u0))).Normalized();

        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (Vec3d p in c)
        {
            minU = Math.Min(minU, p.Dot(u0));
            maxU = Math.Max(maxU, p.Dot(u0));
            minV = Math.Min(minV, p.Dot(v0));
            maxV = Math.Max(maxV, p.Dot(v0));
        }

        double du = maxU - minU, dv = maxV - minV;
        double su = du > 1e-6 ? tilesU / du : 0, sv = dv > 1e-6 ? tilesV / dv : 0;
        double w = Math.Max(1, texW), h = Math.Max(1, texH);
        if (axes == UvFitAxes.U)
        {
            sv = su * w / h; // square texels: the same px/m along V as along U
        }
        else if (axes == UvFitAxes.V)
        {
            su = sv * h / w;
        }

        for (int i = 0; i < c.Count; i++)
        {
            f.Vertices[i].TextureCoords = new Uv((float)((c[i].Dot(u0) - minU) * su), (float)((c[i].Dot(v0) - minV) * sv));
        }

        return true;
    }

    private static Vec3d InPlane(Vec3d v, Vec3d n) => v.Sub(n.Scale(n.Dot(v)));
}
