using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Vec3d = Ged.Core.Editing.Organic.Vec3d;

namespace Ged.Core.Editing;

/// <summary>
/// The direction a texture's features run in its image: a girder, trim strip or wooden post is drawn along one image
/// axis (its grain), a wall panel or hazard stripe has none. A mapper lays the grain along a beam, post or pilaster.
/// </summary>
public enum TextureGrain
{
    /// <summary>No clear direction (a panel, plate or diagonal stripe).</summary>
    None,

    /// <summary>The features run along the image's U axis (a girder drawn lying down, a 256 × 64 trim strip).</summary>
    U,

    /// <summary>The features run along the image's V axis (a post drawn standing up, a 64 × 256 strip).</summary>
    V,
}

/// <summary>
/// Texture grain: from the image's proportions (a strip at least <see cref="StripAspect"/> times longer than wide runs
/// along its long side — stock levels lay 98 % of such textures' long side along their beams and posts), else, for a
/// member-scale texture (at most <see cref="MaxMeasuredSize"/> px: the girder and support squares stock levels lay
/// along their posts are 64–128 px; 256 px squares are wall panels, which stay upright), from its pixels (the image
/// changes much more across its features than along them).
/// </summary>
public static class TextureGrains
{
    /// <summary>A texture at least this many times longer than wide is a strip whose grain is its long side.</summary>
    public const double StripAspect = 2.0;

    /// <summary>A square-ish texture has a grain when its mean luminance change across one image axis is at least this many times the change along the other.</summary>
    public const double MeasuredRatio = 1.6;

    /// <summary>Largest side (px) of a square-ish texture whose pixels may give it a grain.</summary>
    public const int MaxMeasuredSize = 128;

    /// <summary>The grain of a <paramref name="width"/> × <paramref name="height"/> texture whose pixels measured <paramref name="measured"/>.</summary>
    public static TextureGrain Of(int width, int height, TextureGrain measured)
    {
        if (width <= 0 || height <= 0)
        {
            return TextureGrain.None;
        }

        if (width >= StripAspect * height)
        {
            return TextureGrain.U;
        }

        if (height >= StripAspect * width)
        {
            return TextureGrain.V;
        }

        return Math.Max(width, height) <= MaxMeasuredSize ? measured : TextureGrain.None;
    }

    /// <summary>
    /// The grain an image's pixels show: the mean luminance change (alpha-weighted, wrapping like a tiled texture)
    /// between vertical neighbours against that between horizontal neighbours. Horizontal bands (a girder lying down)
    /// change down the image: grain U.
    /// </summary>
    public static TextureGrain Measure(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        (double ex, double ey) = Energies(image);
        if (ex + ey < 1e-6)
        {
            return TextureGrain.None;
        }

        if (ey >= MeasuredRatio * ex)
        {
            return TextureGrain.U;
        }

        return ex >= MeasuredRatio * ey ? TextureGrain.V : TextureGrain.None;
    }

    /// <summary>Mean absolute luminance change between horizontal (x) and vertical (y) neighbours, 0..1.</summary>
    public static (double X, double Y) Energies(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        if (w < 2 || h < 2)
        {
            return (0, 0);
        }

        byte[] px = image.Pixels;
        var luma = new double[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int k = i * 4;
            luma[i] = ((0.299 * px[k]) + (0.587 * px[k + 1]) + (0.114 * px[k + 2])) * px[k + 3] / (255.0 * 255.0);
        }

        double ex = 0, ey = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w, below = ((y + 1) % h) * w;
            for (int x = 0; x < w; x++)
            {
                double v = luma[row + x];
                ex += Math.Abs(luma[row + ((x + 1) % w)] - v);
                ey += Math.Abs(luma[below + x] - v);
            }
        }

        return (ex / (w * h), ey / (w * h));
    }

    /// <summary>The Lua name of a grain: "u", "v", or null for none.</summary>
    public static string? LuaName(TextureGrain g) => g switch
    {
        TextureGrain.U => "u",
        TextureGrain.V => "v",
        _ => null,
    };
}

/// <summary>
/// Thin structural members — beams, girders, posts, pilasters, columns, rails, truss chords — and how a careful Red
/// Faction mapper textures them. Measured on the stock multiplayer levels (dm01–20, ctf01–07: 2,192 long faces of
/// thin axis-aligned members) and on the harvested library modules: the texture's grain runs along the member (98 % of
/// strip-texture area on stock members, 100 % on vertical ones: a girder texture is turned on its side up a post),
/// the member's narrow faces never show the texture repeating across them (96 %), square textures without a grain
/// stay upright, and every face reads the same way round. A member is a solid convex brush at least
/// <see cref="MinAspect"/> times longer than its widest cross-section, which is at most <see cref="MaxWidth"/>.
/// </summary>
public static class UvMembers
{
    /// <summary>Widest cross-section (metres) a member may have; wider pieces are walls and slabs.</summary>
    public const double MaxWidth = 1.25;

    /// <summary>Length over widest cross-section a member needs.</summary>
    public const double MinAspect = 3.0;

    /// <summary>Shortest member (metres).</summary>
    public const double MinLength = 1.0;

    /// <summary>A grain within this many degrees of the member's axis runs along it.</summary>
    public const double GrainToleranceDegrees = 15;

    /// <summary>Most texture repeats a member face may show across its width (a small allowance for rounding).</summary>
    public const double MaxAcrossRepeats = 1.05;

    /// <summary>Least pixels per metre along the member (below it the texture is smeared along the member).</summary>
    public const double MinAlongPixelsPerMetre = 8;

    /// <summary>A member's faces run across this share of the texture or more: fit one repeat across the widest face instead (stock: girder and post textures across 0.5 m faces are fitted, slices of 0.25 m ones are not).</summary>
    public const double FitShare = 0.5;

    /// <summary>One detected member: its world axis (unit), length, widest cross-section and long (side) faces.</summary>
    public sealed record Member(Vec3d Axis, double Length, double Width, IReadOnlyList<MemberFace> Sides);

    /// <summary>A side face of a member: face index in its brush, world polygon, visible-side unit normal and its width across the member.</summary>
    public sealed record MemberFace(int FaceIndex, Vec3d[] Poly, Vec3d Normal, double Width);

    /// <summary>
    /// The member a brush forms, or null when it is not one (air, portal, too wide, too short or too stubby).
    /// Positions are world (brush transform applied); normals point to the side the face is seen from.
    /// </summary>
    public static Member? Detect(Brush b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if ((b.Flags & (uint)(BrushFlags.Air | BrushFlags.Portal)) != 0)
        {
            return null;
        }

        Geometry g = b.Geometry;
        double sign = BrushOrientation.OutwardSign(b) * WindingSign(b);
        var polys = new List<(int Fi, Vec3d[] Poly, Vec3d N)>();
        var all = new List<Vec3d>();
        Vec3d longest = default;
        double longestLen = 0;
        for (int fi = 0; fi < g.Faces.Count; fi++)
        {
            Face f = g.Faces[fi];
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            var poly = new Vec3d[f.Vertices.Count];
            for (int k = 0; k < poly.Length; k++)
            {
                int vi = f.Vertices[k].Index;
                if (vi < 0 || vi >= g.Vertices.Count)
                {
                    return null;
                }

                Vec3 w = b.Position.Add(b.Rotation.Transform(g.Vertices[vi]));
                poly[k] = new Vec3d(w.X, w.Y, w.Z);
            }

            Vec3d nw = Analysis.UvQuality.Newell(poly);
            if (nw.Length < 1e-9)
            {
                continue;
            }

            polys.Add((fi, poly, nw.Normalized().Scale(sign)));
            for (int k = 0; k < poly.Length; k++)
            {
                all.Add(poly[k]);
                Vec3d e = poly[(k + 1) % poly.Length].Sub(poly[k]);
                double l = e.Length;
                if (l > longestLen + 1e-6)
                {
                    longestLen = l;
                    longest = e;
                }
            }
        }

        if (polys.Count < 4 || longestLen < MinLength)
        {
            return null;
        }

        Vec3d axis = Canonical(longest.Scale(1 / longestLen));
        (double lo, double hi) = Span(all, axis);
        double length = hi - lo;
        if (length < MinLength)
        {
            return null;
        }

        var sides = new List<MemberFace>();
        double width = 0;
        foreach ((int fi, Vec3d[] poly, Vec3d n) in polys)
        {
            if (Math.Abs(n.Dot(axis)) > 0.02)
            {
                continue;
            }

            (double a0, double a1) = Span(all, n);
            width = Math.Max(width, a1 - a0);
            Vec3d across = n.Cross(axis).Normalized();
            (double f0, double f1) = Span(poly, across);
            sides.Add(new MemberFace(fi, poly, n, f1 - f0));
        }

        if (sides.Count < 3 || width < 0.01 || width > MaxWidth || length < MinAspect * width)
        {
            return null;
        }

        return new Member(axis, length, width, sides);
    }

    /// <summary>
    /// How a face's current mapping reads on a member: whether the grain runs along the axis, how many repeats show
    /// across the face, the pixels per metre along the axis and whether it reads the right way round. Null when
    /// the face has no usable mapping.
    /// </summary>
    public static (bool GrainAlong, double AcrossRepeats, double AlongPx, bool Readable)? Read(Vec3d[] poly, Vec3d normal, Uv[] uvs,
        Vec3d axis, double faceWidth, TextureGrain grain, int texW, int texH)
    {
        if (grain == TextureGrain.None || poly.Length != uvs.Length || Analysis.UvQuality.Gradients(poly, uvs, normal) is not { } gr)
        {
            return null;
        }

        Vec3d gu = gr.Gu, gv = gr.Gv;
        if (gu.Cross(gv).Length < 1e-12)
        {
            return null;
        }

        double pu = gu.Dot(axis) * texW, pv = gv.Dot(axis) * texH;
        double along = Math.Sqrt((pu * pu) + (pv * pv));
        double cos = Math.Cos(GrainToleranceDegrees * Math.PI / 180);
        bool grainAlong = along > 1e-9 && (grain == TextureGrain.U ? Math.Abs(pu) : Math.Abs(pv)) >= cos * along;
        Vec3d across = normal.Cross(axis).Normalized();
        double repeats = faceWidth * Math.Abs((grain == TextureGrain.U ? gv : gu).Dot(across));
        bool readable = gu.Cross(gv).Dot(normal) > 0;
        return (grainAlong, repeats, along, readable);
    }

    /// <summary>True when the face already reads as a mapper lays a member: grain along, no repeats across, not smeared, the right way round.</summary>
    public static bool IsGood(Vec3d[] poly, Vec3d normal, Uv[] uvs, Vec3d axis, double faceWidth, TextureGrain grain, int texW, int texH) =>
        Read(poly, normal, uvs, axis, faceWidth, grain, texW, texH) is { } r
        && r.GrainAlong && r.AcrossRepeats <= MaxAcrossRepeats && r.AlongPx >= MinAlongPixelsPerMetre && r.Readable;

    /// <summary>
    /// The mapper's UVs for the member's side faces that use a grained texture (<paramref name="texOf"/> gives each
    /// face's grain and pixel size, or null to leave the face out): the grain runs along the axis at
    /// <paramref name="pixelsPerMetre"/> (square texels), anchored in world space along the axis so collinear pieces
    /// continue; across, the texture wraps continuously round the member from the widest face's edge, and when that
    /// face would show at least <see cref="FitShare"/> of the texture across it, one repeat is fitted across it
    /// exactly (never more than one). Every face reads the right way round from outside. Keys are face indices.
    /// </summary>
    public static Dictionary<int, Uv[]> Map(Member m, Func<int, (TextureGrain Grain, int W, int H)?> texOf, double pixelsPerMetre)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(texOf);
        var result = new Dictionary<int, Uv[]>();
        if (m.Sides.Count == 0)
        {
            return result;
        }

        // Round the member, by decreasing angle about the axis: the direction "across" (normal × axis) points that way,
        // so each face's across coordinate continues from the previous face's.
        Vec3d e1 = Perpendicular(m.Axis), e2 = m.Axis.Cross(e1);
        var info = m.Sides.Select(s => (Side: s, Tex: texOf(s.FaceIndex), Angle: Math.Atan2(s.Normal.Dot(e2), s.Normal.Dot(e1)))).ToList();
        var mapped = info.Where(x => x.Tex is { Grain: not TextureGrain.None }).ToList();
        if (mapped.Count == 0)
        {
            return result;
        }

        // Pixels per metre per texture: one fitted repeat across the widest face when it would show at least FitShare.
        var density = new Dictionary<(int W, int H, TextureGrain G), double>();
        foreach (var grp in mapped.GroupBy(x => (x.Tex!.Value.W, x.Tex!.Value.H, x.Tex!.Value.Grain)))
        {
            double wmax = grp.Max(x => x.Side.Width);
            double dimAcross = grp.Key.Grain == TextureGrain.U ? grp.Key.H : grp.Key.W;
            double share = wmax * pixelsPerMetre / dimAcross;
            density[grp.Key] = share >= FitShare && wmax > 1e-6 ? dimAcross / wmax : pixelsPerMetre;
        }

        var order = info.OrderByDescending(x => x.Angle).ToList();
        int start = order.IndexOf(order.Where(x => x.Tex is { Grain: not TextureGrain.None }).OrderByDescending(x => x.Side.Width).ThenBy(x => x.Side.FaceIndex).First());
        double at = 0;
        for (int k = 0; k < order.Count; k++)
        {
            var x = order[(start + k) % order.Count];
            MemberFace s = x.Side;
            Vec3d across = s.Normal.Cross(m.Axis).Normalized();
            (double a0, _) = Span(s.Poly, across);
            if (x.Tex is { Grain: not TextureGrain.None } t)
            {
                double d = density[(t.W, t.H, t.Grain)];
                var uvs = new Uv[s.Poly.Length];
                for (int i = 0; i < uvs.Length; i++)
                {
                    Vec3d p = s.Poly[i];
                    double along = p.Dot(m.Axis) * d, round = (at + p.Dot(across) - a0) * d;

                    // Grain U: U along the axis, V round the member. Grain V: V against the axis (down a post: upright),
                    // U round the member. Both read the right way round from outside ((U × V) · normal > 0).
                    uvs[i] = t.Grain == TextureGrain.U ? new Uv((float)(along / t.W), (float)(round / t.H)) : new Uv((float)(round / t.W), (float)(-along / t.H));
                }

                // Whole-tile shift: smaller numbers, the same texture.
                float su = MathF.Floor(uvs[0].U), sv = MathF.Floor(uvs[0].V);
                for (int i = 0; i < uvs.Length; i++)
                {
                    uvs[i] = new Uv(uvs[i].U - su, uvs[i].V - sv);
                }

                result[s.FaceIndex] = uvs;
            }

            at += s.Width;
        }

        return result;
    }

    /// <summary>
    /// The member reading of a set of brushes for a texture report: over the side faces of every member that use a
    /// grained texture, the area share whose grain runs along the member (all members, and vertical ones — posts,
    /// pilasters, columns — on their own: stock levels turn every strip texture up their posts), that shows at most one
    /// repeat across, and that is laid as a mapper lays it (grain along, no repeats across, not smeared, the right way round). Problems go
    /// to <paramref name="issues"/> (kind member_grain or member_repeats). Null when no member uses a grained texture.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject? Measure(IEnumerable<Brush> brushes, Func<string, (int W, int H)?> textureSize,
        Func<string, TextureGrain>? measuredGrain, List<Analysis.UvQualityIssue>? issues)
    {
        ArgumentNullException.ThrowIfNull(brushes);
        ArgumentNullException.ThrowIfNull(textureSize);
        int members = 0, faces = 0;
        double area = 0, along = 0, narrow = 0, good = 0, vArea = 0, vAlong = 0;
        foreach (Brush b in brushes)
        {
            if (Detect(b) is not { } m)
            {
                continue;
            }

            bool counted = false;
            foreach (MemberFace s in m.Sides)
            {
                Face f = b.Geometry.Faces[s.FaceIndex];
                if (f.IsPortalFace || f.Texture < 0 || f.Texture >= b.Geometry.Textures.Count)
                {
                    continue;
                }

                string tex = b.Geometry.Textures[f.Texture].ToLowerInvariant();
                if (tex.Contains("invisible", StringComparison.Ordinal) || textureSize(tex) is not { } size)
                {
                    continue;
                }

                TextureGrain grain = TextureGrains.Of(size.W, size.H, measuredGrain?.Invoke(tex) ?? TextureGrain.None);
                Uv[] uvs = f.Vertices.Select(v => v.TextureCoords).ToArray();
                if (Read(s.Poly, s.Normal, uvs, m.Axis, s.Width, grain, size.W, size.H) is not { } r)
                {
                    continue;
                }

                double a = Analysis.UvQuality.Newell(s.Poly).Length / 2;
                Vec3d c = Centroid(s.Poly);
                faces++;
                area += a;
                if (!counted)
                {
                    members++;
                    counted = true;
                }

                bool vertical = Math.Abs(m.Axis.Y) > 0.9;
                if (vertical)
                {
                    vArea += a;
                }

                if (r.GrainAlong)
                {
                    along += a;
                    if (vertical)
                    {
                        vAlong += a;
                    }
                }
                else
                {
                    issues?.Add(new Analysis.UvQualityIssue("member_grain", c, 2, a, tex, b.Uid));
                }

                if (r.AcrossRepeats <= MaxAcrossRepeats)
                {
                    narrow += a;
                }
                else
                {
                    issues?.Add(new Analysis.UvQualityIssue("member_repeats", c, r.AcrossRepeats, a, tex, b.Uid));
                }

                if (r.GrainAlong && r.AcrossRepeats <= MaxAcrossRepeats && r.AlongPx >= MinAlongPixelsPerMetre && r.Readable)
                {
                    good += a;
                }
            }
        }

        if (area <= 1e-9)
        {
            return null;
        }

        static double R(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["members"] = members,
            ["faces"] = faces,
            ["area"] = R(area),
            ["grain_along"] = R(along / area),
            ["no_repeats_across"] = R(narrow / area),
            ["mapped"] = R(good / area),
            ["vertical_area"] = R(vArea),
            ["vertical_grain_along"] = vArea > 1e-9 ? R(vAlong / vArea) : null,
        };
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

    private static (double Lo, double Hi) Span(IEnumerable<Vec3d> pts, Vec3d dir)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (Vec3d p in pts)
        {
            double d = p.Dot(dir);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }

        return (lo, hi);
    }

    /// <summary>The axis turned so its largest component is positive (a post's axis points up).</summary>
    private static Vec3d Canonical(Vec3d a)
    {
        double ax = Math.Abs(a.X), ay = Math.Abs(a.Y), az = Math.Abs(a.Z);
        double dom = ay >= ax - 1e-9 && ay >= az - 1e-9 ? a.Y : ax >= az ? a.X : a.Z;
        return dom < 0 ? a.Scale(-1) : a;
    }

    private static Vec3d Perpendicular(Vec3d a)
    {
        Vec3d helper = Math.Abs(a.Y) < 0.9 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0);
        return helper.Cross(a).Normalized();
    }

    /// <summary>-1 when the brush's rotation mirrors (turns the winding inside out in the world), else +1.</summary>
    internal static double WindingSign(Brush b)
    {
        Mat3 m = b.Rotation;
        Vec3 x = m.Transform(new Vec3(1, 0, 0)), y = m.Transform(new Vec3(0, 1, 0)), z = m.Transform(new Vec3(0, 0, 1));
        double det = (x.X * ((y.Y * z.Z) - (y.Z * z.Y))) - (x.Y * ((y.X * z.Z) - (y.Z * z.X))) + (x.Z * ((y.X * z.Y) - (y.Y * z.X)));
        return det < 0 ? -1 : 1;
    }
}
