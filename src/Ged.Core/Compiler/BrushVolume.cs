using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// A point-in-solid classifier for one brush's world volume, used by the CSG
/// survival test. Convex brushes (the common case) use a fast behind-all-planes
/// test; non-convex brushes (complex curved rooms) use ray casting — counting
/// boundary crossings along an irrational direction — which is O(faces) and,
/// unlike a BSP, cannot fragment or explode on coplanar-heavy real geometry.
/// Carries the brush's time index and air/solid role so the compiler can
/// evaluate "open space" as a time-ordered fold.
/// </summary>
public sealed class BrushVolume
{
    private const float Eps = CsgPlane.OnPlaneEpsilon;

    // An irrational, non-axis-aligned ray direction that avoids grazing axis-aligned faces/edges.
    private static readonly Vec3 RayDir = new Vec3(0.5411961f, 0.5810519f, 0.6081734f).Normalized();

    // Escape probes for the filled-volume test (item 5): a point that cannot reach infinity
    // without crossing the shell in ANY of these directions is enclosed — RED's BSP brush-volume
    // conversion fills such cavities (outside-flood semantics), so a hollow crate's interior is
    // SOLID to the CSG and its inner faces are consumed. Ray parity alone calls the cavity
    // "outside", which kept the shell interior faces alive as sealed junk rooms (kothcow's
    // crate rooms; dmabruptdecay's machine pockets).
    private static readonly Vec3[] EscapeDirs =
    {
        new Vec3(0.5411961f, 0.5810519f, 0.6081734f).Normalized(),
        new Vec3(0.9902680f, 0.0871557f, 0.1073689f).Normalized(),   // ~+X (perturbed off-axis)
        new Vec3(-0.9902680f, -0.0871557f, 0.1073689f).Normalized(), // ~-X
        new Vec3(0.0871557f, 0.9902680f, 0.1073689f).Normalized(),   // ~+Y
        new Vec3(0.1073689f, -0.9902680f, -0.0871557f).Normalized(), // ~-Y
        new Vec3(0.0871557f, 0.1073689f, 0.9902680f).Normalized(),   // ~+Z
        new Vec3(-0.1073689f, 0.0871557f, -0.9902680f).Normalized(), // ~-Z
    };

    private readonly CsgPlane[]? _convexPlanes;
    private readonly CsgFace[]? _faces;

    private BrushVolume(int timeIndex, bool isAir, Vec3 min, Vec3 max, CsgPlane[]? convex, CsgFace[]? faces)
    {
        TimeIndex = timeIndex;
        IsAir = isAir;
        Min = min;
        Max = max;
        _convexPlanes = convex;
        _faces = faces;
    }

    public int TimeIndex { get; }

    public bool IsAir { get; }

    /// <summary>True when the brush is a convex solid (its boundary planes fully define its volume).</summary>
    public bool IsConvexVolume => _convexPlanes is not null;

    public Vec3 Min { get; }

    public Vec3 Max { get; }

    /// <summary>Builds a volume from a brush's outward-facing world faces.</summary>
    public static BrushVolume From(int timeIndex, bool isAir, IReadOnlyList<CsgFace> worldFaces)
    {
        Vec3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vec3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (CsgFace f in worldFaces)
        {
            foreach (CsgVertex v in f.Vertices)
            {
                min = Vec3Math.Min(min, v.Position);
                max = Vec3Math.Max(max, v.Position);
            }
        }

        if (IsConvex(worldFaces))
        {
            var planes = new CsgPlane[worldFaces.Count];
            for (int i = 0; i < worldFaces.Count; i++)
            {
                planes[i] = worldFaces[i].Plane;
            }

            return new BrushVolume(timeIndex, isAir, min, max, planes, null);
        }

        var faces = new CsgFace[worldFaces.Count];
        for (int i = 0; i < worldFaces.Count; i++)
        {
            faces[i] = worldFaces[i];
        }

        return new BrushVolume(timeIndex, isAir, min, max, null, faces);
    }

    /// <summary>True when <paramref name="p"/> lies strictly inside the brush volume.</summary>
    public bool Contains(Vec3 p)
    {
        if (p.X < Min.X - Eps || p.X > Max.X + Eps ||
            p.Y < Min.Y - Eps || p.Y > Max.Y + Eps ||
            p.Z < Min.Z - Eps || p.Z > Max.Z + Eps)
        {
            return false;
        }

        if (_convexPlanes is not null)
        {
            foreach (CsgPlane pl in _convexPlanes)
            {
                if (pl.Distance(p) > Eps)
                {
                    return false; // outside a face => outside the convex volume
                }
            }

            return true;
        }

        // Ray cast: odd number of forward boundary crossings ⇒ inside the shell wall.
        int crossings = Crossings(p, RayDir);
        if ((crossings & 1) == 1)
        {
            return true;
        }

        if (crossings == 0)
        {
            return false; // clean escape: genuinely outside
        }

        // Even-but-nonzero: either outside looking through the brush, or inside an ENCLOSED
        // cavity of a hollow shell. RED's BSP volume conversion fills cavities (outside-flood),
        // so match it: the point is inside the FILLED volume iff no probe direction escapes
        // without crossing the shell.
        for (int i = 1; i < EscapeDirs.Length; i++)
        {
            if (Crossings(p, EscapeDirs[i]) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private int Crossings(Vec3 p, Vec3 dir)
    {
        int crossings = 0;
        foreach (CsgFace f in _faces!)
        {
            float denom = f.Plane.Normal.Dot(dir);
            if (MathF.Abs(denom) < 1e-9f)
            {
                continue; // ray parallel to the face
            }

            float t = -f.Plane.Distance(p) / denom;
            if (t <= Eps)
            {
                continue; // behind or at the origin
            }

            Vec3 hit = p.Add(dir.Scale(t));
            if (PointInPolygon(f, hit))
            {
                crossings++;
            }
        }

        return crossings;
    }

    /// <summary>2D point-in-polygon after dropping the face normal's dominant axis.</summary>
    private static bool PointInPolygon(CsgFace f, Vec3 hit)
    {
        Vec3 n = f.Plane.Normal;
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);

        float hu = Comp(hit, drop, true);
        float hv = Comp(hit, drop, false);
        bool inside = false;
        List<CsgVertex> verts = f.Vertices;
        int count = verts.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float ui = Comp(verts[i].Position, drop, true), vi = Comp(verts[i].Position, drop, false);
            float uj = Comp(verts[j].Position, drop, true), vj = Comp(verts[j].Position, drop, false);
            if (((vi > hv) != (vj > hv)) && (hu < ((uj - ui) * (hv - vi) / (vj - vi)) + ui))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float Comp(Vec3 p, int drop, bool first) => drop switch
    {
        0 => first ? p.Y : p.Z,
        1 => first ? p.X : p.Z,
        _ => first ? p.X : p.Y,
    };

    /// <summary>A brush is convex when every vertex lies behind (or on) every face plane.</summary>
    private static bool IsConvex(IReadOnlyList<CsgFace> faces)
    {
        const float ConvexEps = 0.01f;
        foreach (CsgFace fp in faces)
        {
            CsgPlane plane = fp.Plane;
            foreach (CsgFace fv in faces)
            {
                foreach (CsgVertex v in fv.Vertices)
                {
                    if (plane.Distance(v.Position) > ConvexEps)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
