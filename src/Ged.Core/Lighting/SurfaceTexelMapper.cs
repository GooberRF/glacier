using System;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// Maps a surface's lightmap texel (fragment-local col/row) back to its world
/// position — the inverse of <c>SurfaceBuilder</c>'s uv_scale / uv_add forward
/// transform (docs/research/red-lighting-model.md §(e)). Kept axes come straight
/// from the atlas UV; the dropped axis is reconstructed from the surface plane.
/// Precomputes the per-surface constants so the per-texel loop is a few FMAs.
/// </summary>
public readonly struct SurfaceTexelMapper
{
    private readonly int _uAxis;
    private readonly int _vAxis;
    private readonly int _dropAxis;
    private readonly float _baseU;   // world-U at col 0
    private readonly float _stepU;   // world-U per col
    private readonly float _baseV;   // world-V at row 0
    private readonly float _stepV;   // world-V per row
    private readonly float _nu;      // plane normal component on uAxis
    private readonly float _nv;      // plane normal component on vAxis
    private readonly float _nd;      // plane normal component on dropped axis
    private readonly float _offset;  // plane offset (N·P + offset = 0)

    public SurfaceTexelMapper(Surface s, int pageWidth, int pageHeight)
    {
        _uAxis = s.UCoefficient;
        _vAxis = s.VCoefficient;
        _dropAxis = s.DroppedCoefficient;

        // atlasU(col) = (X + col + 0.5)/pageW ; worldU = (atlasU − addU)/scaleU
        float invScaleU = s.UvScale.U != 0f ? 1f / s.UvScale.U : 0f;
        float invScaleV = s.UvScale.V != 0f ? 1f / s.UvScale.V : 0f;
        float du = 1f / pageWidth;
        float dv = 1f / pageHeight;
        float a0u = (s.X + 0.5f) * du;
        float a0v = (s.Y + 0.5f) * dv;
        _baseU = (a0u - s.UvAdd.U) * invScaleU;
        _stepU = du * invScaleU;
        _baseV = (a0v - s.UvAdd.V) * invScaleV;
        _stepV = dv * invScaleV;

        Vec3 n = s.Plane.Normal;
        _nu = n.Component(_uAxis);
        _nv = n.Component(_vAxis);
        _nd = n.Component(_dropAxis);
        _offset = s.Plane.Offset;
    }

    /// <summary>World position of the texel centre at fragment column/row (X+col, Y+row).</summary>
    public Vec3 World(int col, int row)
    {
        float worldU = _baseU + (_stepU * col);
        float worldV = _baseV + (_stepV * row);

        // Reconstruct the dropped axis from N·P + offset = 0.
        float dropped = MathF.Abs(_nd) > 1e-9f
            ? -(_offset + (_nu * worldU) + (_nv * worldV)) / _nd
            : 0f;

        var p = default(Vec3);
        p = p.WithComponent(_uAxis, worldU);
        p = p.WithComponent(_vAxis, worldV);
        p = p.WithComponent(_dropAxis, dropped);
        return p;
    }

    /// <summary>
    /// Inverse of <see cref="World"/>: the fragment column/row a world point maps to,
    /// clamped into <c>[0,w)×[0,h)</c>. Used by the bounce gather to fetch the direct-lit
    /// colour at a ray hit on a surface (feature 1).
    /// </summary>
    public void TexelAt(Vec3 world, int w, int h, out int col, out int row)
    {
        float worldU = world.Component(_uAxis);
        float worldV = world.Component(_vAxis);
        int c = _stepU != 0f ? (int)MathF.Round((worldU - _baseU) / _stepU) : 0;
        int r = _stepV != 0f ? (int)MathF.Round((worldV - _baseV) / _stepV) : 0;
        col = Math.Clamp(c, 0, Math.Max(0, w - 1));
        row = Math.Clamp(r, 0, Math.Max(0, h - 1));
    }
}
