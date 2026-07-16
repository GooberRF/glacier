using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Rooms;

/// <summary>
/// The room adjacency graph of a compiled level: nodes are
/// <see cref="Geometry.Rooms"/>, edges are <see cref="Geometry.Portals"/>. Drives
/// the stock "Render Using Portals" view (traverse the graph from the camera room,
/// render only reached rooms — like in-game), "Render Current Room Only", and the
/// live "Room: N" status readout (point-in-room by smallest containing AABB).
/// </summary>
public sealed class RoomGraph
{
    private readonly Geometry _geo;
    private readonly List<(int Neighbor, int PortalIndex)>[] _adj;

    private RoomGraph(Geometry geo, List<(int, int)>[] adj)
    {
        _geo = geo;
        _adj = adj;
    }

    /// <summary>Number of rooms (graph nodes).</summary>
    public int RoomCount => _geo.Rooms.Count;

    /// <summary>The portal edges as (roomA, roomB, portalIndex) tuples.</summary>
    public IReadOnlyList<(int A, int B, int PortalIndex)> Edges { get; private set; } = Array.Empty<(int, int, int)>();

    /// <summary>Builds the graph from a compiled geometry's rooms + portals.</summary>
    public static RoomGraph Build(Geometry geo)
    {
        ArgumentNullException.ThrowIfNull(geo);
        int n = geo.Rooms.Count;
        var adj = new List<(int, int)>[n];
        for (int i = 0; i < n; i++)
        {
            adj[i] = new List<(int, int)>();
        }

        var edges = new List<(int, int, int)>();
        for (int p = 0; p < geo.Portals.Count; p++)
        {
            Portal portal = geo.Portals[p];
            int a = portal.RoomIndex1;
            int b = portal.RoomIndex2;
            if (a < 0 || a >= n || b < 0 || b >= n || a == b)
            {
                continue;
            }

            adj[a].Add((b, p));
            adj[b].Add((a, p));
            edges.Add((a, b, p));
        }

        return new RoomGraph(geo, adj) { Edges = edges };
    }

    /// <summary>
    /// The set of rooms reachable from <paramref name="startRoom"/> by traversing
    /// portals (breadth-first). <paramref name="isPortalBlocked"/> (portal index →
    /// bool) skips a portal — a closed door / non-see-thru portal face blocks the
    /// view past it. Returns just the start room when it is out of range.
    /// </summary>
    public HashSet<int> Reachable(int startRoom, Func<int, bool>? isPortalBlocked = null)
    {
        var visited = new HashSet<int>();
        if (startRoom < 0 || startRoom >= RoomCount)
        {
            return visited;
        }

        var queue = new Queue<int>();
        queue.Enqueue(startRoom);
        visited.Add(startRoom);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            foreach ((int neighbor, int portalIndex) in _adj[room])
            {
                if (isPortalBlocked is not null && isPortalBlocked(portalIndex))
                {
                    continue;
                }

                if (visited.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        return visited;
    }

    /// <summary>Neighbours of a room across its portals (neighbour room, portal index).</summary>
    public IReadOnlyList<(int Neighbor, int PortalIndex)> Neighbors(int room) =>
        room >= 0 && room < RoomCount ? _adj[room] : Array.Empty<(int, int)>();

    /// <summary>
    /// The room a world point is in: the smallest-volume room whose AABB contains
    /// it, preferring a main (non-sub) room, matching the lighting bake's
    /// smallest-bbox-wins containment. Returns −1 when no room contains the point.
    /// </summary>
    public int RoomAt(Vec3 p)
    {
        int best = -1;
        float bestVol = float.MaxValue;
        bool bestIsMain = false;

        for (int i = 0; i < _geo.Rooms.Count; i++)
        {
            Room room = _geo.Rooms[i];
            if (!Contains(room.Aabb, p))
            {
                continue;
            }

            bool isMain = room.IsSubroom == 0;
            float vol = Volume(room.Aabb);

            // Prefer a main room; among the same class, prefer the smallest volume.
            bool better = (isMain && !bestIsMain) || (isMain == bestIsMain && vol < bestVol);
            if (best < 0 || better)
            {
                best = i;
                bestVol = vol;
                bestIsMain = isMain;
            }
        }

        return best;
    }

    private static bool Contains(Aabb box, Vec3 p) =>
        p.X >= box.P1.X && p.X <= box.P2.X &&
        p.Y >= box.P1.Y && p.Y <= box.P2.Y &&
        p.Z >= box.P1.Z && p.Z <= box.P2.Z;

    private static float Volume(Aabb box)
    {
        float dx = MathF.Max(box.P2.X - box.P1.X, 0f);
        float dy = MathF.Max(box.P2.Y - box.P1.Y, 0f);
        float dz = MathF.Max(box.P2.Z - box.P1.Z, 0f);
        return dx * dy * dz;
    }
}
