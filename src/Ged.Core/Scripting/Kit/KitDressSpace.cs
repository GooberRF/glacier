using System;
using System.Collections.Generic;

namespace Ged.Core.Scripting.Kit;

/// <summary>
/// A space being dressed by kit.dress (scripts/lib/kit_dress.lua decides what to place; this decides where it
/// fits): its floor lattice, keep-outs (doorways 2.5 m, steps / ramps / ledges 1.25 m, spawns 1.6 m, items 1.2 m,
/// triggers / events / nav points 1 m, clutter 0.8 m, caller areas), occupancy, the anchors whose connections
/// dressing must keep (doorways, objects, one per 8 m cell) and the walk budget tall props may take.
/// </summary>
internal sealed class KitDressSpace
{
    private static readonly int[][] N8 = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 }, new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 } };

    private readonly KitLattice _l;
    private readonly List<(int Id, double X, double Y, double Z, int Comp)> _anchors = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private int _nextToken = 1;

    /// <summary>A prop accepted by <see cref="Try"/> and not yet committed.</summary>
    private sealed record Pending(List<(double X0, double Y0, double Z0, double X1, double Y1, double Z1, double Base)> World, List<int> Cover, HashSet<int> Gone, double Lost);

    public KitDressSpace(KitLattice l, KitSpace s, IReadOnlyList<(double X, double Y, double Z, double R, bool Box, double X0, double Y0, double Z0, double X1, double Y1, double Z1)> keep,
        IReadOnlyList<(string Kind, double X, double Y, double Z)> objects, double budget)
    {
        _l = l;
        Flat = l.Flat(0.02);
        Keepout = new bool[l.Count + 1];
        Occupied = new bool[l.Count + 1];
        Doors = l.DoorNodes(s.X0, s.Z0, s.X1, s.Z1);
        foreach (int id in Doors)
        {
            MarkRadius(l.X[id], l.Z[id], l.Y[id], 2.5, 2.0);
        }

        foreach (int id in l.StepNodes(0.02))
        {
            MarkRadius(l.X[id], l.Z[id], l.Y[id], 1.25, 2.0);
        }

        foreach ((string kind, double x, double y, double z) in objects)
        {
            double r = kind switch
            {
                "MpRespawnPoint" or "PlayerStart" => 1.6,
                "Item" => 1.2,
                "Trigger" or "Event" or "NavPoint" or "Entity" => 1.0,
                "Clutter" => 0.8,
                _ => 0,
            };
            if (r > 0)
            {
                MarkRadius(x, z, y - 1.0, r, 2.5);
            }
        }

        foreach (var kb in keep)
        {
            if (!kb.Box)
            {
                MarkRadius(kb.X, kb.Z, kb.Y, kb.R, 3);
                continue;
            }

            (long i0, long i1) = ColRange(kb.X0, kb.X1);
            (long k0, long k1) = ColRange(kb.Z0, kb.Z1);
            for (long i = i0; i <= i1; i++)
            {
                for (long k = k0; k <= k1; k++)
                {
                    if (l.At.TryGetValue(KitProbe.Key(i, k), out List<int>? a))
                    {
                        foreach (int id in a)
                        {
                            if (l.Y[id] >= kb.Y0 - 0.1 && l.Y[id] <= kb.Y1)
                            {
                                Keepout[id] = true;
                            }
                        }
                    }
                }
            }
        }

        // Anchors: doorways, objects (not clutter), and one per 8 m cell so no part of the space can be walled off.
        var pts = new List<(double X, double Y, double Z)>();
        foreach (int id in Doors)
        {
            pts.Add((l.X[id], l.Y[id], l.Z[id]));
        }

        foreach ((string kind, double x, double y, double z) in objects)
        {
            if (kind is "MpRespawnPoint" or "PlayerStart" or "Item" or "Trigger" or "Event" or "NavPoint" or "Entity")
            {
                pts.Add((x, y - 1.0, z));
            }
        }

        int cx = Math.Max(1, (int)Math.Floor(((s.X1 - s.X0) / 8) + 0.5)), cz = Math.Max(1, (int)Math.Floor(((s.Z1 - s.Z0) / 8) + 0.5));
        for (int a = 0; a < cx; a++)
        {
            for (int b = 0; b < cz; b++)
            {
                pts.Add((s.X0 + ((a + 0.5) * (s.X1 - s.X0) / cx), s.Y0, s.Z0 + ((b + 0.5) * (s.Z1 - s.Z0) / cz)));
            }
        }

        bool[] wide = l.LiveWide;
        var seen = new HashSet<int>();
        var chosen = new List<(int, double, double, double)>();
        foreach ((double x, double y, double z) in pts)
        {
            int id = NearestWide(x, y, z, 2.5, wide);
            if (id != 0 && seen.Add(id))
            {
                chosen.Add((id, x, y, z));
            }
        }

        int[] comp = l.Components(wide);
        foreach ((int id, double x, double y, double z) in chosen)
        {
            _anchors.Add((id, x, y, z, comp[id]));
        }

        AreaBefore = l.WalkArea(out _);
        Budget = budget * AreaBefore;
    }

    public bool[] Flat { get; }

    public bool[] Keepout { get; }

    public bool[] Occupied { get; }

    public List<int> Doors { get; }

    public double AreaBefore { get; }

    public double Budget { get; }

    public double Lost { get; private set; }

    private static (long, long) ColRange(double a, double b) =>
        ((long)Math.Ceiling((a / KitProbe.Pitch) - 0.5), (long)Math.Floor((b / KitProbe.Pitch) - 0.5));

    private void MarkRadius(double x, double z, double y, double r, double ytol)
    {
        (long i0, long i1) = ColRange(x - r, x + r);
        (long k0, long k1) = ColRange(z - r, z + r);
        for (long i = i0; i <= i1; i++)
        {
            for (long k = k0; k <= k1; k++)
            {
                double cx = (i + 0.5) * KitProbe.Pitch, cz = (k + 0.5) * KitProbe.Pitch;
                if (((cx - x) * (cx - x)) + ((cz - z) * (cz - z)) > r * r || !_l.At.TryGetValue(KitProbe.Key(i, k), out List<int>? a))
                {
                    continue;
                }

                foreach (int id in a)
                {
                    if (Math.Abs(_l.Y[id] - y) <= ytol)
                    {
                        Keepout[id] = true;
                    }
                }
            }
        }
    }

    private int NearestWide(double x, double y, double z, double r, bool[] wide)
    {
        int best = 0;
        double bd = double.MaxValue;
        (long i0, long i1) = ColRange(x - r, x + r);
        (long k0, long k1) = ColRange(z - r, z + r);
        for (long i = i0; i <= i1; i++)
        {
            for (long k = k0; k <= k1; k++)
            {
                if (!_l.At.TryGetValue(KitProbe.Key(i, k), out List<int>? a))
                {
                    continue;
                }

                foreach (int id in a)
                {
                    if (!wide[id])
                    {
                        continue;
                    }

                    double d = ((_l.X[id] - x) * (_l.X[id] - x)) + ((_l.Z[id] - z) * (_l.Z[id] - z)) + ((_l.Y[id] - y) * (_l.Y[id] - y));
                    if (d < bd)
                    {
                        best = id;
                        bd = d;
                    }
                }
            }
        }

        return best;
    }

    private bool RoutesOk(HashSet<int> gone)
    {
        bool[] wide = _l.LiveWide;
        var groups = new Dictionary<int, List<int>>();
        var order = new List<int>();
        foreach ((int aid, double ax, double ay, double az, int comp) in _anchors)
        {
            int id = aid;
            if (gone.Contains(id) || !wide[id])
            {
                // The anchor's node was taken: the nearest open wide node within 3 m stands in for it.
                id = 0;
                double bd = 9;
                (long i0, long i1) = ColRange(ax - 3, ax + 3);
                (long k0, long k1) = ColRange(az - 3, az + 3);
                for (long i = i0; i <= i1; i++)
                {
                    for (long k = k0; k <= k1; k++)
                    {
                        if (!_l.At.TryGetValue(KitProbe.Key(i, k), out List<int>? l))
                        {
                            continue;
                        }

                        foreach (int j in l)
                        {
                            if (wide[j] && !gone.Contains(j))
                            {
                                double d = ((_l.X[j] - ax) * (_l.X[j] - ax)) + ((_l.Z[j] - az) * (_l.Z[j] - az));
                                if (d < bd && Math.Abs(_l.Y[j] - ay) < 1.0)
                                {
                                    id = j;
                                    bd = d;
                                }
                            }
                        }
                    }
                }

                if (id == 0)
                {
                    return false;
                }
            }

            if (!groups.TryGetValue(comp, out List<int>? g))
            {
                groups[comp] = g = new List<int>();
                order.Add(comp);
            }

            g.Add(id);
        }

        var list = new List<IReadOnlyList<int>>();
        foreach (int c in order)
        {
            if (groups[c].Count > 1)
            {
                list.Add(groups[c]);
            }
        }

        return list.Count == 0 || _l.RoutesOk(list, gone);
    }

    /// <summary>
    /// Tries a prop: parts (local u0, u1, v0, v1, y0, y1 above the floor) at anchor (ax, az) on floor ay, v axis
    /// (vx, vz); wall = true when the v = 0 side is against a wall. Returns a commit token (0 = does not fit) and the
    /// world boxes. Every part must stand over floor at ay with room above it; floor parts need free, flat, padded
    /// floor around them; tall footprints count against the walk budget; no anchor may be cut off.
    /// </summary>
    public (int Token, List<(double X0, double Y0, double Z0, double X1, double Y1, double Z1, double Base)> World) Try(
        IReadOnlyList<(double U0, double U1, double V0, double V1, double Y0, double Y1)> parts, double ax, double ay, double az, double vx, double vz, bool wall, double pad)
    {
        var covered = new HashSet<int>();
        var coverList = new List<int>();
        var colTop = new Dictionary<int, double>();
        var colBot = new Dictionary<int, double>();
        var world = new List<(double, double, double, double, double, double, double)>();
        double ux = -vz, uz = vx;
        foreach (var p in parts)
        {
            double xa = ax + (p.U0 * ux) + (p.V0 * vx), xb = ax + (p.U1 * ux) + (p.V1 * vx);
            double za = az + (p.U0 * uz) + (p.V0 * vz), zb = az + (p.U1 * uz) + (p.V1 * vz);
            double x0 = Math.Min(xa, xb), x1 = Math.Max(xa, xb), z0 = Math.Min(za, zb), z1 = Math.Max(za, zb);
            world.Add((x0, ay + p.Y0, z0, x1, ay + p.Y1, z1, ay));
            (long i0, long i1) = ColRange(x0, x1);
            (long k0, long k1) = ColRange(z0, z1);
            for (long i = i0; i <= i1; i++)
            {
                for (long k = k0; k <= k1; k++)
                {
                    int id = _l.NodeAt(i, k, ay, 0.02);
                    if (id == 0)
                    {
                        return (0, world);
                    }

                    // Parts are local: y0 / y1 are heights above the floor at ay.
                    if (p.Y0 < KitProbe.Headroom + 0.05)
                    {
                        if (covered.Add(id))
                        {
                            coverList.Add(id);
                        }

                        colBot[id] = Math.Min(colBot.GetValueOrDefault(id, 99), p.Y0);
                        colTop[id] = Math.Max(colTop.GetValueOrDefault(id, -99), p.Y1);
                    }

                    if (_l.Top[id] < ay + p.Y1 + 0.05)
                    {
                        return (0, world);
                    }
                }
            }

            // The padded area around floor parts must be free floor too (not on the wall side).
            if (p.Y0 < 1.9)
            {
                double px0 = x0 - pad, px1 = x1 + pad, pz0 = z0 - pad, pz1 = z1 + pad;
                if (wall)
                {
                    if (vx > 0)
                    {
                        px0 = x0;
                    }
                    else if (vx < 0)
                    {
                        px1 = x1;
                    }
                    else if (vz > 0)
                    {
                        pz0 = z0;
                    }
                    else
                    {
                        pz1 = z1;
                    }
                }

                (long pi0, long pi1) = ColRange(px0, px1);
                (long pk0, long pk1) = ColRange(pz0, pz1);
                for (long i = pi0; i <= pi1; i++)
                {
                    for (long k = pk0; k <= pk1; k++)
                    {
                        int id = _l.NodeAt(i, k, ay, 0.02);
                        if (id == 0 || Keepout[id] || Occupied[id] || !Flat[id])
                        {
                            return (0, world);
                        }
                    }
                }
            }
        }

        // Walkable area this prop takes: columns whose stack does not start on the floor, or tops out above a walk
        // step (1.45 m, under the 1.5 m step), lose their standing node.
        double lost = 0;
        foreach (int id in coverList)
        {
            if (colBot[id] > 0.05 || colTop[id] > 1.45)
            {
                lost += KitProbe.Pitch * KitProbe.Pitch;
            }
        }

        if (Lost + lost > Budget + 1e-9)
        {
            return (0, world);
        }

        // Routes: the covered nodes leave the wide set with their neighbours.
        var gone = new HashSet<int>();
        foreach (int id in coverList)
        {
            gone.Add(id);
            foreach (int[] d in N8)
            {
                int j = _l.NodeAt(_l.I[id] + d[0], _l.K[id] + d[1], _l.Y[id], KitProbe.FootStep);
                if (j != 0)
                {
                    gone.Add(j);
                }
            }
        }

        if (coverList.Count > 0 && !RoutesOk(gone))
        {
            return (0, world);
        }

        int token = _nextToken++;
        _pending.Clear();
        _pending[token] = new Pending(world, coverList, gone, lost);
        return (token, world);
    }

    private static readonly int[][] Head = { new[] { 1, 0 }, new[] { 0, 1 }, new[] { -1, 0 }, new[] { 0, -1 } };

    /// <summary>
    /// Wall-adjacent floor spots: (id, heading 1–4, wall distance) for flat nodes with a wall within 1.5 m one
    /// step back along a heading (the column one step back holds floor at this height, two steps back none).
    /// </summary>
    public List<(int Id, int H, double Dist)> WallSpots()
    {
        var o = new List<(int, int, double)>();
        for (int id = 1; id <= _l.Count; id++)
        {
            if (!Flat[id])
            {
                continue;
            }

            for (int h = 0; h < 4; h++)
            {
                int[] d = Head[h];
                int j = _l.NodeAt(_l.I[id] - d[0], _l.K[id] - d[1], _l.Y[id], 0.3);
                int j2 = _l.NodeAt(_l.I[id] - (2 * d[0]), _l.K[id] - (2 * d[1]), _l.Y[id], 0.3);
                if (j != 0 && j2 == 0)
                {
                    double dist = _l.Probe.WallDistance(_l.X[id], _l.Y[id] + 1.0, _l.Z[id], -d[0], -d[1], 1.5);
                    if (dist < 1.5)
                    {
                        o.Add((id, h + 1, dist));
                    }
                }
            }
        }

        return o;
    }

    /// <summary>Commits a prop accepted by <see cref="Try"/>: occupancy (+ 0.45 m), wide set, budget. Returns the node ids newly occupied.</summary>
    public List<int> Commit(int token)
    {
        var newly = new List<int>();
        if (!_pending.Remove(token, out Pending? p))
        {
            return newly;
        }

        _l.Unwide(p.Gone);
        foreach (var w in p.World)
        {
            // Floor-standing parts take their floor (plus a gap); wall-mounted and overhead parts do not.
            if (w.Y0 - w.Base >= 1.9)
            {
                continue;
            }

            (long i0, long i1) = ColRange(w.X0 - 0.45, w.X1 + 0.45);
            (long k0, long k1) = ColRange(w.Z0 - 0.45, w.Z1 + 0.45);
            for (long i = i0; i <= i1; i++)
            {
                for (long k = k0; k <= k1; k++)
                {
                    if (!_l.At.TryGetValue(KitProbe.Key(i, k), out List<int>? a))
                    {
                        continue;
                    }

                    foreach (int id in a)
                    {
                        if (Math.Abs(_l.Y[id] - w.Y0) < 2.5 && !Occupied[id])
                        {
                            Occupied[id] = true;
                            newly.Add(id);
                        }
                    }
                }
            }
        }

        Lost += p.Lost;
        return newly;
    }
}
