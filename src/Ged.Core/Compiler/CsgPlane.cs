using System;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// A compiler-internal plane in Red Faction's stored convention: the signed
/// distance of a point <c>p</c> is <c>Normal·p + Offset</c>, so a point lies on
/// the plane when that value is zero, and <c>Offset = -(Normal·pointOnPlane)</c>.
/// This matches the plane RF.exe reads from compiled geometry (verified against
/// the corpus), unlike the editor's brush-authoring convention which stores
/// <c>+Normal·point</c>.
/// </summary>
public readonly struct CsgPlane
{
    /// <summary>Symmetric on-plane band (RED's geometry epsilon 0x38d1b717 ≈ 1e-4).</summary>
    public const float OnPlaneEpsilon = 1e-4f;

    public CsgPlane(Vec3 normal, float offset)
    {
        Normal = normal;
        Offset = offset;
    }

    public Vec3 Normal { get; }

    /// <summary>Plane offset such that <c>Normal·p + Offset == 0</c> on the plane.</summary>
    public float Offset { get; }

    /// <summary>Signed distance of <paramref name="p"/>: positive on the normal side.</summary>
    public float Distance(Vec3 p) => Normal.Dot(p) + Offset;

    public CsgPlane Flipped() => new(Normal.Negate(), -Offset);

    /// <summary>Newell-normal plane through a polygon's vertices (offset = -(n·centroid)).</summary>
    public static CsgPlane FromPolygon(System.Collections.Generic.IReadOnlyList<CsgVertex> verts)
    {
        var n = new Vec3(0, 0, 0);
        var c = new Vec3(0, 0, 0);
        for (int i = 0; i < verts.Count; i++)
        {
            Vec3 a = verts[i].Position;
            Vec3 b = verts[(i + 1) % verts.Count].Position;
            n = n.Add(new Vec3(
                (a.Y - b.Y) * (a.Z + b.Z),
                (a.Z - b.Z) * (a.X + b.X),
                (a.X - b.X) * (a.Y + b.Y)));
            c = c.Add(a);
        }

        n = n.Normalized();
        c = c.Scale(1f / verts.Count);
        return new CsgPlane(n, -n.Dot(c));
    }

    /// <summary>Plane from a normal and a point that lies on it.</summary>
    public static CsgPlane FromPointNormal(Vec3 point, Vec3 normal)
    {
        Vec3 n = normal.Normalized();
        return new CsgPlane(n, -n.Dot(point));
    }
}
