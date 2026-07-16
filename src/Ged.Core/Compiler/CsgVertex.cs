using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// A polygon corner during compilation: a world-space position plus its authored
/// texture UV. Both are linearly interpolated when an edge is split by a cut
/// plane, so texture continuity is preserved (no re-projection). Lightmap UVs do
/// not exist yet at CSG time; they are computed in the surface stage.
/// </summary>
public readonly struct CsgVertex
{
    public CsgVertex(Vec3 position, Uv uv)
    {
        Position = position;
        Uv = uv;
    }

    public Vec3 Position { get; }

    public Uv Uv { get; }

    /// <summary>Linear blend of two corners at parameter <paramref name="t"/> (0→a, 1→b).</summary>
    public static CsgVertex Lerp(CsgVertex a, CsgVertex b, float t) => new(
        Vec3Math.Lerp(a.Position, b.Position, t),
        new Uv(a.Uv.U + ((b.Uv.U - a.Uv.U) * t), a.Uv.V + ((b.Uv.V - a.Uv.V) * t)));
}
