using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Pure 2D transforms for the UV Unwrap editor: move / rotate / scale / flip /
/// align of a selected subset of UV points about their shared pivot. Operates on a
/// flat <see cref="Uv"/> array (the editor's working set of face-corner UVs) plus a
/// selection of indices, so it is fully unit-testable independent of the window.
/// </summary>
public static class UnwrapOps
{
    /// <summary>Translates the selected UVs by (du, dv).</summary>
    public static void Move(IList<Uv> uvs, IReadOnlyCollection<int> selected, float du, float dv)
    {
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv(uvs[i].U + du, uvs[i].V + dv);
            }
        }
    }

    /// <summary>Rotates the selected UVs about their centroid by <paramref name="degrees"/> (counter-clockwise).</summary>
    public static void Rotate(IList<Uv> uvs, IReadOnlyCollection<int> selected, float degrees)
    {
        Uv c = Centroid(uvs, selected);
        float r = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(r), sin = MathF.Sin(r);
        foreach (int i in selected)
        {
            if (!InRange(uvs, i))
            {
                continue;
            }

            float du = uvs[i].U - c.U;
            float dv = uvs[i].V - c.V;
            uvs[i] = new Uv(
                c.U + (du * cos) - (dv * sin),
                c.V + (du * sin) + (dv * cos));
        }
    }

    /// <summary>Scales the selected UVs about their centroid (non-uniform when su != sv).</summary>
    public static void Scale(IList<Uv> uvs, IReadOnlyCollection<int> selected, float su, float sv)
    {
        Uv c = Centroid(uvs, selected);
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv(c.U + ((uvs[i].U - c.U) * su), c.V + ((uvs[i].V - c.V) * sv));
            }
        }
    }

    /// <summary>Mirrors the selected UVs in U about their centroid (Flip H).</summary>
    public static void FlipU(IList<Uv> uvs, IReadOnlyCollection<int> selected)
    {
        Uv c = Centroid(uvs, selected);
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv((2f * c.U) - uvs[i].U, uvs[i].V);
            }
        }
    }

    /// <summary>Mirrors the selected UVs in V about their centroid (Flip V).</summary>
    public static void FlipV(IList<Uv> uvs, IReadOnlyCollection<int> selected)
    {
        Uv c = Centroid(uvs, selected);
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv(uvs[i].U, (2f * c.V) - uvs[i].V);
            }
        }
    }

    /// <summary>Aligns the selected UVs to a shared U (their minimum U): Shift+H.</summary>
    public static void AlignU(IList<Uv> uvs, IReadOnlyCollection<int> selected)
    {
        float min = float.MaxValue;
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                min = MathF.Min(min, uvs[i].U);
            }
        }

        if (min == float.MaxValue)
        {
            return;
        }

        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv(min, uvs[i].V);
            }
        }
    }

    /// <summary>Aligns the selected UVs to a shared V (their minimum V): Shift+V.</summary>
    public static void AlignV(IList<Uv> uvs, IReadOnlyCollection<int> selected)
    {
        float min = float.MaxValue;
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                min = MathF.Min(min, uvs[i].V);
            }
        }

        if (min == float.MaxValue)
        {
            return;
        }

        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv(uvs[i].U, min);
            }
        }
    }

    /// <summary>Snaps the selected UVs to the nearest multiple of <paramref name="step"/> (grid snap).</summary>
    public static void SnapToGrid(IList<Uv> uvs, IReadOnlyCollection<int> selected, float step)
    {
        if (step <= 1e-6f)
        {
            return;
        }

        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                uvs[i] = new Uv(MathF.Round(uvs[i].U / step) * step, MathF.Round(uvs[i].V / step) * step);
            }
        }
    }

    /// <summary>
    /// Item 11 — Fit: scales + translates the selected UVs so their bounds fill the base
    /// [0,1] tile. Aspect-preserving by default (uniform scale, centred on the short axis);
    /// <paramref name="preserveAspect"/> false stretches each axis independently. Degenerate
    /// axes (zero extent) are centred at 0.5.
    /// </summary>
    public static void FitToTile(IList<Uv> uvs, IReadOnlyCollection<int> selected, bool preserveAspect = true)
    {
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        int n = 0;
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                minU = MathF.Min(minU, uvs[i].U);
                maxU = MathF.Max(maxU, uvs[i].U);
                minV = MathF.Min(minV, uvs[i].V);
                maxV = MathF.Max(maxV, uvs[i].V);
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        float du = maxU - minU;
        float dv = maxV - minV;
        float su = du > 1e-6f ? 1f / du : 0f;
        float sv = dv > 1e-6f ? 1f / dv : 0f;
        if (preserveAspect)
        {
            float s = MathF.Min(su > 0f ? su : float.MaxValue, sv > 0f ? sv : float.MaxValue);
            if (s == float.MaxValue)
            {
                s = 0f;
            }

            su = sv = s;
        }

        // Centre the fitted layout inside the tile on any axis it does not fill.
        float offU = (1f - (du * su)) * 0.5f;
        float offV = (1f - (dv * sv)) * 0.5f;
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                float u = su > 0f ? ((uvs[i].U - minU) * su) + offU : 0.5f;
                float v = sv > 0f ? ((uvs[i].V - minV) * sv) + offV : 0.5f;
                uvs[i] = new Uv(u, v);
            }
        }
    }

    /// <summary>
    /// Item 10 — Auto Unwrap: planar-projects each face by the dominant axis of its normal
    /// (world scale preserved across faces), then shelf-packs the face islands into the base
    /// [0,1] tile with a gutter. Faces keep their relative world proportions; nothing overlaps.
    /// <paramref name="faceRings"/> lists each face's corner indices (into <paramref name="uvs"/>);
    /// <paramref name="positionOf"/> returns a corner's (brush-local) 3D position and
    /// <paramref name="normalOf"/> each face's normal.
    /// </summary>
    public static void AutoUnwrap(
        IList<Uv> uvs,
        IReadOnlyList<IReadOnlyList<int>> faceRings,
        Func<int, Vec3> positionOf,
        Func<int, Vec3> normalOf,
        float gutter = 0.02f)
    {
        ArgumentNullException.ThrowIfNull(positionOf);
        ArgumentNullException.ThrowIfNull(normalOf);
        if (faceRings.Count == 0)
        {
            return;
        }

        // 1. Planar-project every face island to origin-based 2D coords (world units).
        var islands = new List<(IReadOnlyList<int> Ring, float[] U, float[] V, float W, float H)>(faceRings.Count);
        for (int f = 0; f < faceRings.Count; f++)
        {
            IReadOnlyList<int> ring = faceRings[f];
            if (ring.Count == 0)
            {
                continue;
            }

            Vec3 nrm = normalOf(f);
            (int uAxis, int vAxis) = ProjectionAxes(nrm);
            var us = new float[ring.Count];
            var vs = new float[ring.Count];
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            for (int i = 0; i < ring.Count; i++)
            {
                Vec3 p = positionOf(ring[i]);
                us[i] = p.Component(uAxis);
                vs[i] = p.Component(vAxis);
                minU = MathF.Min(minU, us[i]);
                maxU = MathF.Max(maxU, us[i]);
                minV = MathF.Min(minV, vs[i]);
                maxV = MathF.Max(maxV, vs[i]);
            }

            for (int i = 0; i < ring.Count; i++)
            {
                us[i] -= minU;
                vs[i] -= minV;
            }

            islands.Add((ring, us, vs, MathF.Max(maxU - minU, 1e-4f), MathF.Max(maxV - minV, 1e-4f)));
        }

        if (islands.Count == 0)
        {
            return;
        }

        // 2. Find the largest uniform scale whose shelf packing (with gutter) fits the tile.
        float lo = 0f;
        float hi = 1f / MathF.Max(islands.Max(i => MathF.Max(i.W, i.H)), 1e-4f); // one island alone fills the tile
        float scale = hi;
        for (int iter = 0; iter < 24; iter++)
        {
            float mid = (lo + hi) * 0.5f;
            if (TryShelfPack(islands, mid, gutter, place: null))
            {
                lo = mid;
                scale = mid;
            }
            else
            {
                hi = mid;
            }
        }

        // 3. Final placement pass writes the UVs.
        TryShelfPack(islands, scale, gutter, place: (island, ox, oy) =>
        {
            (IReadOnlyList<int> ring, float[] us, float[] vs, float _, float _) = island;
            for (int i = 0; i < ring.Count; i++)
            {
                uvs[ring[i]] = new Uv(ox + (us[i] * scale), oy + (vs[i] * scale));
            }
        });
    }

    private static bool TryShelfPack(
        List<(IReadOnlyList<int> Ring, float[] U, float[] V, float W, float H)> islands,
        float scale,
        float gutter,
        Action<(IReadOnlyList<int> Ring, float[] U, float[] V, float W, float H), float, float>? place)
    {
        float x = gutter, y = gutter, shelf = 0f;
        foreach ((IReadOnlyList<int>, float[], float[], float, float) island in islands)
        {
            float w = island.Item4 * scale;
            float h = island.Item5 * scale;
            if (x + w + gutter > 1f)
            {
                x = gutter;
                y += shelf + gutter;
                shelf = 0f;
            }

            if (y + h + gutter > 1f || w + (2f * gutter) > 1f)
            {
                return false;
            }

            place?.Invoke(island, x, y);
            x += w + gutter;
            shelf = MathF.Max(shelf, h);
        }

        return true;
    }

    /// <summary>The two projection axes for a normal (drop its dominant component), stable ordering.</summary>
    public static (int UAxis, int VAxis) ProjectionAxes(Vec3 normal)
    {
        float ax = MathF.Abs(normal.X), ay = MathF.Abs(normal.Y), az = MathF.Abs(normal.Z);
        int drop = ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        return drop switch
        {
            0 => (1, 2), // X-dominant: U=Y, V=Z
            1 => (0, 2), // Y-dominant: U=X, V=Z
            _ => (0, 1), // Z-dominant: U=X, V=Y
        };
    }

    /// <summary>The centroid of the selected UVs (or the origin when the selection is empty).</summary>
    public static Uv Centroid(IList<Uv> uvs, IReadOnlyCollection<int> selected)
    {
        float u = 0f, v = 0f;
        int n = 0;
        foreach (int i in selected)
        {
            if (InRange(uvs, i))
            {
                u += uvs[i].U;
                v += uvs[i].V;
                n++;
            }
        }

        return n == 0 ? default : new Uv(u / n, v / n);
    }

    private static bool InRange(IList<Uv> uvs, int i) => i >= 0 && i < uvs.Count;
}
