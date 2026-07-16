using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Lighting;

/// <summary>
/// Resolves the ambient floor (float RGB 0..1) for a texel. A texel gets its
/// containing room's ambient when that room defines one, else the level ambient;
/// when rooms overlap the smallest-bounding-box room wins (RED's rule). Non-quality
/// bakes use the surface's own room ambient uniformly (<see cref="ForRoom"/>).
/// </summary>
public sealed class AmbientField
{
    private readonly Vec3 _level;
    private readonly Vec3?[] _byRoom;             // per-room ambient, null = inherit level
    private readonly Aabb[] _roomBox;             // per-room bbox (all rooms, for own-room containment)
    private readonly (Aabb Box, Vec3 Ambient, float Volume)[] _rooms; // rooms with ambient, smallest first

    public AmbientField(Vec3 levelAmbient, IReadOnlyList<Room> rooms)
    {
        _level = levelAmbient;
        _byRoom = new Vec3?[rooms.Count];
        _roomBox = new Aabb[rooms.Count];
        var withAmbient = new List<(Aabb, Vec3, float)>();
        for (int i = 0; i < rooms.Count; i++)
        {
            Room r = rooms[i];
            _roomBox[i] = r.Aabb;
            if (r.HasAmbientLight != 0 && r.AmbientColor is RfColor c)
            {
                var a = new Vec3(c.R / 255f, c.G / 255f, c.B / 255f);
                _byRoom[i] = a;
                Vec3 d = r.Aabb.P2.Sub(r.Aabb.P1);
                float vol = MathF.Abs(d.X * d.Y * d.Z);
                withAmbient.Add((r.Aabb, a, vol));
            }
        }

        withAmbient.Sort((x, y) => x.Item3.CompareTo(y.Item3));
        _rooms = withAmbient.ToArray();
    }

    /// <summary>Level ambient (float RGB 0..1).</summary>
    public Vec3 Level => _level;

    /// <summary>The surface's room ambient, or the level ambient if that room has none.</summary>
    public Vec3 ForRoom(int roomIndex)
    {
        if (roomIndex >= 0 && roomIndex < _byRoom.Length && _byRoom[roomIndex] is Vec3 a)
        {
            return a;
        }

        return _level;
    }

    /// <summary>
    /// Per-texel ambient: the smallest-bbox room containing <paramref name="p"/> that
    /// defines an ambient, falling back to <paramref name="surfaceRoom"/> then level.
    /// </summary>
    /// <param name="preferOwnRoom">
    /// Corner Leak Fix: when a texel lies inside its own surface's room, use THAT room's ambient
    /// (the authoritative compiler room assignment) instead of the smallest overlapping room's.
    /// This closes the corner ambient leak where a smaller bright room's bbox overlaps a darker
    /// room's floor. Only when the surface's own room bbox does NOT contain the texel (a grouped
    /// surface extending past its room) does the smallest-bbox lookup run. Default false =
    /// byte-parity behaviour.
    /// </param>
    public Vec3 At(Vec3 p, int surfaceRoom, bool preferOwnRoom = false)
    {
        if (preferOwnRoom && surfaceRoom >= 0 && surfaceRoom < _roomBox.Length && Contains(_roomBox[surfaceRoom], p))
        {
            return ForRoom(surfaceRoom);
        }

        for (int i = 0; i < _rooms.Length; i++)
        {
            if (Contains(_rooms[i].Box, p))
            {
                return _rooms[i].Ambient;
            }
        }

        return ForRoom(surfaceRoom);
    }

    private static bool Contains(Aabb b, Vec3 p) =>
        p.X >= b.P1.X && p.X <= b.P2.X &&
        p.Y >= b.P1.Y && p.Y <= b.P2.Y &&
        p.Z >= b.P1.Z && p.Z <= b.P2.Z;
}
