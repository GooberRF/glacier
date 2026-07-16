using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Locates the room a world point belongs to by casting a vertical ray at the
/// compiled world faces: the closest non-vertical face below the point (else
/// above) carries the room. This is portal-aware because world faces were
/// chopped at portal membranes — the floor fragment under a probe just off a
/// doorway sheet always belongs to that side's room. Falls back to the nearest
/// face centroid when the column is empty (e.g. beside a wall). Used for portal
/// side assignment, room-effect containment and liquid-surface clipping.
/// </summary>
public sealed class RoomLocator
{
    private const float Cell = 4f;
    private readonly List<CsgFace> _faces;
    private readonly int[] _faceRoom;
    private readonly Dictionary<(int, int), List<int>> _columns = new();

    public RoomLocator(List<CsgFace> faces, int[] faceRoom)
    {
        // Snapshot the face list + room array: the caller compacts (removes dropped
        // portal faces from) both AFTER building the locator, which would otherwise
        // leave our cached column bucket indices dangling / mis-aimed. The dropped
        // faces are portals — already excluded from the buckets below — so the
        // snapshot is geometrically equivalent for point-in-room queries.
        _faces = new List<CsgFace>(faces);
        _faceRoom = (int[])faceRoom.Clone();

        for (int i = 0; i < _faces.Count && i < _faceRoom.Length; i++)
        {
            if (_faceRoom[i] < 0 || _faces[i].IsPortal || MathF.Abs(_faces[i].Plane.Normal.Y) < 0.05f)
            {
                continue; // near-vertical faces can't answer a vertical ray
            }

            var mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            _faces[i].GrowAabb(ref mn, ref mx);
            (int x0, int z0) = C(mn.X, mn.Z);
            (int x1, int z1) = C(mx.X, mx.Z);
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (!_columns.TryGetValue((x, z), out List<int>? bucket))
                    {
                        _columns[(x, z)] = bucket = new List<int>();
                    }

                    bucket.Add(i);
                }
            }
        }
    }

    /// <summary>Room at the point (nearest surface below, else above, else nearby), or -1.</summary>
    public int Locate(Vec3 p) => Locate(p, float.MaxValue);

    /// <summary>
    /// Room at the point, but reject a surface farther than <paramref name="maxDist"/> along the vertical
    /// ray. Detail-room attach uses the bound so a probe just off a detail whose column has no nearby room
    /// floor (only a distant outer/sky shell far below/above) resolves to nothing instead of that far room —
    /// the ray must land on the surface the detail actually rests against.
    /// </summary>
    public int Locate(Vec3 p, float maxDist)
    {
        if (!_columns.TryGetValue(C(p.X, p.Z), out List<int>? bucket))
        {
            return -1;
        }

        int below = -1, above = -1;
        float belowY = float.MinValue, aboveY = float.MaxValue;

        foreach (int i in bucket)
        {
            CsgFace f = _faces[i];
            if (!ContainsXz(f, p.X, p.Z))
            {
                continue;
            }

            // Y of the face's plane at (x, z): n.X*x + n.Y*y + n.Z*z + d = 0.
            Vec3 n = f.Plane.Normal;
            float y = (-f.Plane.Offset - (n.X * p.X) - (n.Z * p.Z)) / n.Y;
            if (y <= p.Y + 0.05f && y > belowY)
            {
                belowY = y;
                below = _faceRoom[i];
            }
            else if (y > p.Y + 0.05f && y < aboveY)
            {
                aboveY = y;
                above = _faceRoom[i];
            }
        }

        if (below >= 0 && p.Y - belowY <= maxDist)
        {
            return below;
        }

        if (above >= 0 && aboveY - p.Y <= maxDist)
        {
            return above;
        }

        return -1;
    }

    /// <summary>2D point-in-polygon on the XZ projection.</summary>
    private static bool ContainsXz(CsgFace f, float px, float pz)
    {
        bool inside = false;
        List<CsgVertex> v = f.Vertices;
        int count = v.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float xi = v[i].Position.X, zi = v[i].Position.Z;
            float xj = v[j].Position.X, zj = v[j].Position.Z;
            if (((zi > pz) != (zj > pz)) && (px < ((xj - xi) * (pz - zi) / (zj - zi)) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static (int, int) C(float x, float z) =>
        ((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell));
}
