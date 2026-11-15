using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing.Organic;

namespace Ged.Core.Scripting.Kit;

/// <summary>
/// kit.items' planner (scripts/lib/kit_items.lua places what it returns): the item economy over the playable floor
/// of the spaces. The count follows the WHOLE level's density (<see cref="Per100"/>): 1.6 items per 100 m² (× density)
/// of the level's walkable floor up to 4,500 m², easing above that the way the exemplars' item counts grow with their
/// floor (never under 1.0 per 100 m²), less every item already in the level, and a call places at most its spaces'
/// share of that — so per-zone calls add up to one level-wide economy instead of each zone getting its own minimum set.
/// Items never bunch: no item goes where <see cref="LocalCap"/> or more already lie within 10 m (the exemplars' 10 m
/// neighbourhood p90 is 4, their worst 6–10), and every placement prefers the emptier neighbourhoods.
/// Shares are the exemplar medians (weapons 31 %, ammo 34 %, health 17 %, armour 14 %, powerups 3 %); powerups in
/// the most contested 8 m cells (route betweenness percentile ≥ 0.6), never pockets; weapons 7–10 m apart and spread
/// over the zones; an item 3.5–5.5 m in front of every spawn that has none within 6 m (counted in the target);
/// health and armour on the flanks; ammo beside the weapons; CTF halves mirrored item for item.
/// </summary>
internal static class KitItemPlanner
{
    private static readonly Dictionary<string, double> Share = new(StringComparer.Ordinal)
    {
        ["weapon"] = 0.31, ["ammo"] = 0.34, ["health"] = 0.17, ["armor"] = 0.14, ["powerup"] = 0.03,
    };

    private sealed class Cell
    {
        public int Id, N;
        public double X, Y, Z, Traffic;
        public bool Pocket, Leaf;
        public readonly List<int> Nbr = new();
        public readonly HashSet<int> NbrSet = new();
    }

    /// <summary>Items per 100 m² up to this much playable floor; larger levels ease (see <see cref="Per100"/>).</summary>
    internal const double DenseArea = 4500;

    /// <summary>
    /// No item goes where this many items already lie within <see cref="LocalRadius"/> (horizontally, within 3.5 m of
    /// height). The stock exemplars have p50 2, p90 4 and at worst 6–10 other items within 10 m of an item.
    /// </summary>
    internal const int LocalCap = 6;

    internal const double LocalRadius = 10;

    /// <summary>
    /// The level-wide item density for <paramref name="levelArea"/> m² of playable floor: 1.6 per 100 m² (× density) up to
    /// <see cref="DenseArea"/>, then × (area / 4500)^-0.35, never under 1.0 × density (the grader's per-segment payload floor is 0.8). The exemplar study's item counts
    /// grow sublinearly with floor area (fit over its 44 exemplars: items ≈ 0.57·area^0.58; over the 20 stock levels
    /// ≈ 0.10·area^0.77), so a payload-sized level (≈ 25,000 m²) gets 1.0 per 100 m² — what exemplars of
    /// 10,000–16,000 m² have (0.7–1.2) — instead of the small-map 1.6.
    /// </summary>
    internal static double Per100(double levelArea, double density) =>
        density * Math.Max(1.0, 1.6 * Math.Pow(Math.Max(levelArea, DenseArea) / DenseArea, -0.35));

    private sealed class Cand
    {
        public required KitFloorNode N;
        public required Cell C;
        public bool Taken;
        public int Near;
        public double DAll = double.MaxValue, DMajor = double.MaxValue;
        public readonly Dictionary<string, double> Dc = new(StringComparer.Ordinal);
    }

    private sealed record Item(double X, double Y, double Z, string Cat, string? Class, int Space);

    private sealed record Weapon(string Class, string Ammo, bool Rare);

    private static double D2(double ax, double ay, double az, double bx, double by, double bz) =>
        ((ax - bx) * (ax - bx)) + ((az - bz) * (az - bz)) + (0.25 * (ay - by) * (ay - by));

    public static Dictionary<string, object?> Plan(ScriptKitCore core, ScriptArgs a)
    {
        List<KitSpace> spaces = KitArgs.Spaces(a);
        KitProbe probe = core.Probe().Model;
        string gametype = a.String("kit.items", "gametype") ?? "dm";
        double density = KitArgs.Num(a, "density", 1);
        double itemHeight = KitArgs.Num(a, "item_height", 0.5);
        bool powerups = a.Named.TryGetValue("powerups", out object? pv) ? pv is true : true;
        List<Weapon> weapons = Weapons(a);
        List<string> health = Strings(a, "health"), armor = Strings(a, "armor"), powerupClasses = Strings(a, "powerup_classes");
        List<Vec3d> path = KitArgs.Points(a, "path");
        Vec3d? hill = KitArgs.Points(a, "hill") is { Count: > 0 } h ? h[0] : null;

        var playable = new KitPlayable(probe, spaces, 0.75, n => core.ZoneFilter(probe, n));
        List<KitFloorNode> nodes = playable.All;
        double area = nodes.Count * KitProbe.Pitch * KitProbe.Pitch;
        if (nodes.Count == 0 || area <= 0)
        {
            return Fail("no walkable floor in the spaces.", null);
        }

        // The whole level's walkable floor (every air space), for the level-wide target.
        List<KitSpace> levelSpaces = a.Named.TryGetValue("level_spaces", out object? lv) && lv is ScriptArgs la
            ? KitArgs.Spaces(new ScriptArgs(new Dictionary<string, object?> { ["spaces"] = la }))
            : spaces;
        double levelArea = ReferenceEquals(levelSpaces, spaces) ? area : Math.Max(area, new KitPlayable(probe, levelSpaces, 0.75, n => core.ZoneFilter(probe, n)).All.Count * KitProbe.Pitch * KitProbe.Pitch);

        // Areas as the grader measures them (playable floor of the compiled level) when the level can be measured:
        // the kit's lattice also counts rough ledges and slopes the grader's walk graph leaves out.
        string areaMeasure = "the kit's floor lattice";
        if (KitSight.For(core, out _) is { } sight)
        {
            double a1 = sight.PlayableAreaWhere(p => spaces.Any(s => p.X >= s.X0 && p.X <= s.X1 && p.Z >= s.Z0 && p.Z <= s.Z1 && p.Y >= s.Y0 - 0.5 && p.Y <= s.Y1));
            if (a1 > 0)
            {
                area = a1;
                levelArea = ReferenceEquals(levelSpaces, spaces) ? a1 : Math.Max(a1, sight.PlayableAreaWhere(p => levelSpaces.Any(s => p.X >= s.X0 && p.X <= s.X1 && p.Z >= s.Z0 && p.Z <= s.Z1 && p.Y >= s.Y0 - 0.5 && p.Y <= s.Y1)));
                areaMeasure = "the grader's playable floor";
            }
        }

        double bx0 = spaces.Min(s => s.X0) - 3, bz0 = spaces.Min(s => s.Z0) - 3, bx1 = spaces.Max(s => s.X1) + 3, bz1 = spaces.Max(s => s.Z1) + 3;
        bool InBox(double x, double z) => x >= bx0 && x <= bx1 && z >= bz0 && z <= bz1;

        List<Cell> cells = CellGraph(nodes, out Dictionary<KitFloorNode, Cell> cellOf);

        // Spawns in the box and the items already in the level.
        var spawns = new List<(double X, double Y, double Z, double Fx, double Fz, int Team)>();
        if (a.Named.TryGetValue("spawns", out object? sv) && sv is ScriptArgs sl)
        {
            foreach (object? o in sl.Items)
            {
                if (o is ScriptArgs s && InBox(KitArgs.Num(s, "x", 0), KitArgs.Num(s, "z", 0)))
                {
                    spawns.Add((KitArgs.Num(s, "x", 0), KitArgs.Num(s, "y", 0), KitArgs.Num(s, "z", 0), KitArgs.Num(s, "fx", 0), KitArgs.Num(s, "fz", 1), (int)KitArgs.Num(s, "team", 0)));
                }
            }
        }

        var placed = new List<Item>();
        // Existing weapons and powerups (the level's own, placed by hand or an earlier call) count for the spacing.
        var existingMajors = new HashSet<Item>();
        int existingHere = 0, existingLevel = 0;
        var flags = new List<(double X, double Z, bool Red)>();
        if (a.Named.TryGetValue("existing", out object? ev) && ev is ScriptArgs el)
        {
            foreach (object? o in el.Items)
            {
                if (o is not ScriptArgs e)
                {
                    continue;
                }

                string cls = e.String("kit.items", "class") ?? string.Empty;
                double x = KitArgs.Num(e, "x", 0), y = KitArgs.Num(e, "y", 0) - itemHeight, z = KitArgs.Num(e, "z", 0);
                var existingItem = new Item(x, y, z, "existing", cls, -1);
                placed.Add(existingItem);
                string lc = cls.ToLowerInvariant();
                if (weapons.Any(w => string.Equals(w.Class, cls, StringComparison.OrdinalIgnoreCase)) || lc.Contains("amp", StringComparison.Ordinal)
                    || lc.Contains("invuln", StringComparison.Ordinal) || lc.Contains("super ", StringComparison.Ordinal))
                {
                    existingMajors.Add(existingItem);
                }

                if (lc == "flag_red" || lc == "flag_blue")
                {
                    flags.Add((x, z, lc == "flag_red"));
                }

                if (!lc.Contains("flag", StringComparison.Ordinal) && !lc.Contains("base_", StringComparison.Ordinal) && !lc.Contains("banner", StringComparison.Ordinal))
                {
                    existingLevel++;
                    if (InBox(x, z))
                    {
                        existingHere++;
                    }
                }
            }
        }

        // Doorway keep-out (1.5 m).
        var nearDoor = new HashSet<long>();
        foreach (var ps in playable.PerSpace)
        {
            foreach (KitFloorNode dn in ps.Doors)
            {
                long ci = (long)Math.Floor(dn.X / KitProbe.Pitch), ck = (long)Math.Floor(dn.Z / KitProbe.Pitch);
                for (long i = ci - 3; i <= ci + 3; i++)
                {
                    for (long k = ck - 3; k <= ck + 3; k++)
                    {
                        double x = (i + 0.5) * KitProbe.Pitch, z = (k + 0.5) * KitProbe.Pitch;
                        if (((x - dn.X) * (x - dn.X)) + ((z - dn.Z) * (z - dn.Z)) < 2.25)
                        {
                            nearDoor.Add(KitProbe.Key(i, k));
                        }
                    }
                }
            }
        }

        // Candidates: clear floor on a 1 m sub-lattice.
        var cand = new List<Cand>();
        foreach (KitFloorNode n in nodes)
        {
            long i = (long)Math.Floor(n.X / KitProbe.Pitch), k = (long)Math.Floor(n.Z / KitProbe.Pitch);
            if (!n.Clear || ((i % 2) + 2) % 2 != 0 || ((k % 2) + 2) % 2 != 0 || nearDoor.Contains(KitProbe.Key(i, k)))
            {
                continue;
            }

            if (spawns.Any(s => ((s.X - n.X) * (s.X - n.X)) + ((s.Z - n.Z) * (s.Z - n.Z)) < 2.25 && Math.Abs(s.Y - n.Y) < 2))
            {
                continue;
            }

            cand.Add(new Cand { N = n, C = cellOf[n] });
        }

        if (cand.Count == 0)
        {
            return Fail("no clear floor for items (every spot is a doorway, spawn or wall edge).", null);
        }

        // CTF: the mirror between the red and blue halves.
        (double Mx, double Mz, double Ax, double Az, bool Plane)? mirror = null;
        if (gametype == "ctf")
        {
            double rx = 0, rz = 0, rn = 0, bx = 0, bz = 0, bn = 0;
            foreach (var s in spawns)
            {
                if (s.Team == 1)
                {
                    rx += s.X;
                    rz += s.Z;
                    rn++;
                }
                else if (s.Team == 2)
                {
                    bx += s.X;
                    bz += s.Z;
                    bn++;
                }
            }

            foreach (var f in flags)
            {
                if (f.Red)
                {
                    rx += f.X * 4;
                    rz += f.Z * 4;
                    rn += 4;
                }
                else
                {
                    bx += f.X * 4;
                    bz += f.Z * 4;
                    bn += 4;
                }
            }

            if (rn > 0 && bn > 0)
            {
                rx /= rn;
                rz /= rn;
                bx /= bn;
                bz /= bn;
                double ax = bx - rx, az = bz - rz, len = Math.Sqrt((ax * ax) + (az * az));
                if (len > 1)
                {
                    ax /= len;
                    az /= len;
                    mirror = ((rx + bx) / 2, (rz + bz) / 2, ax, az, Math.Abs(ax) > 0.97 || Math.Abs(az) > 0.97);
                }
            }
        }

        bool RedHalf(double x, double z) => mirror is not { } m || (((x - m.Mx) * m.Ax) + ((z - m.Mz) * m.Az)) < -2;
        (double, double) Mirrored(double x, double z)
        {
            var m = mirror!.Value;
            if (m.Plane)
            {
                double s = ((x - m.Mx) * m.Ax) + ((z - m.Mz) * m.Az);
                return (x - (2 * s * m.Ax), z - (2 * s * m.Az));
            }

            return ((2 * m.Mx) - x, (2 * m.Mz) - z);
        }

        var candAt = new Dictionary<(long, long), List<Cand>>();
        foreach (Cand c in cand)
        {
            var key = ((long)Math.Floor(c.N.X / 2), (long)Math.Floor(c.N.Z / 2));
            if (!candAt.TryGetValue(key, out List<Cand>? l))
            {
                candAt[key] = l = new List<Cand>();
            }

            l.Add(c);
        }

        Cand? NearestCand(double x, double y, double z, double r)
        {
            Cand? best = null;
            double bd = r * r;
            for (long p = (long)Math.Floor((x - r) / 2); p <= (long)Math.Floor((x + r) / 2); p++)
            {
                for (long q = (long)Math.Floor((z - r) / 2); q <= (long)Math.Floor((z + r) / 2); q++)
                {
                    if (!candAt.TryGetValue((p, q), out List<Cand>? l))
                    {
                        continue;
                    }

                    foreach (Cand c in l)
                    {
                        double d = ((c.N.X - x) * (c.N.X - x)) + ((c.N.Z - z) * (c.N.Z - z));
                        if (d < bd && Math.Abs(c.N.Y - y) < 2.5 && !c.Taken)
                        {
                            best = c;
                            bd = d;
                        }
                    }
                }
            }

            return best;
        }

        (double Lat, double Along, double Total) PathInfo(KitFloorNode n)
        {
            double best = double.MaxValue, along = 0, total = 0;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vec3d p = path[i], q = path[i + 1];
                double dx = q.X - p.X, dz = q.Z - p.Z, len2 = (dx * dx) + (dz * dz);
                double f = len2 > 0 ? Math.Max(0, Math.Min(1, (((n.X - p.X) * dx) + ((n.Z - p.Z) * dz)) / len2)) : 0;
                double px = p.X + (dx * f), pz = p.Z + (dz * f);
                double d = Math.Sqrt(((n.X - px) * (n.X - px)) + ((n.Z - pz) * (n.Z - pz)));
                if (d < best)
                {
                    best = d;
                    along = total + (f * Math.Sqrt(len2));
                }

                total += Math.Sqrt(len2);
            }

            return (best, along, total);
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["weapon"] = 0, ["ammo"] = 0, ["health"] = 0, ["armor"] = 0, ["powerup"] = 0 };
        var outList = new List<(Item It, Cand C)>();

        void Note(Item p)
        {
            foreach (Cand c in cand)
            {
                double hx = c.N.X - p.X, hz = c.N.Z - p.Z;
                if ((hx * hx) + (hz * hz) <= LocalRadius * LocalRadius && Math.Abs(c.N.Y - p.Y) < 3.5)
                {
                    c.Near++;
                }

                double d = Math.Sqrt(D2(c.N.X, c.N.Y, c.N.Z, p.X, p.Y, p.Z));
                if (d < c.DAll)
                {
                    c.DAll = d;
                }

                if ((p.Cat == "weapon" || p.Cat == "powerup" || existingMajors.Contains(p)) && d < c.DMajor)
                {
                    c.DMajor = d;
                }

                if (d < c.Dc.GetValueOrDefault(p.Cat, double.MaxValue))
                {
                    c.Dc[p.Cat] = d;
                }
            }
        }

        foreach (Item p in placed)
        {
            Note(p);
        }

        void Place(Cand c, string cls, string cat)
        {
            c.Taken = true;
            var it = new Item(c.N.X, c.N.Y, c.N.Z, cat, cls, c.N.Space);
            placed.Add(it);
            counts[cat]++;
            outList.Add((it, c));
            Note(it);
        }

        double MinDist(Cand c, string? cat, bool major = false) =>
            cat is null ? (major ? c.DMajor : c.DAll) : c.Dc.GetValueOrDefault(cat, double.MaxValue);

        void PlacePair(Cand c, string cls, string cat)
        {
            Place(c, cls, cat);
            if (mirror is not null)
            {
                (double mx, double mz) = Mirrored(c.N.X, c.N.Z);
                if (NearestCand(mx, c.N.Y, mz, 3) is { } m)
                {
                    Place(m, cls, cat);
                }
            }
        }

        // The target: this call's share of the whole level's density, less what is already there.
        double per100 = Per100(levelArea, density);
        bool Crowded(Cand c) => c.Near >= LocalCap;
        int levelTarget = (int)Math.Floor((levelArea / 100 * per100) + 0.5);
        int hereTarget = (int)Math.Floor((area / 100 * per100) + 0.5);
        int nTotal = Math.Max(0, Math.Min(hereTarget - existingHere, levelTarget - existingLevel));
        int pairs = mirror is not null ? 2 : 1;
        Dictionary<string, int> want = Allocate(nTotal / (double)pairs, powerups && area >= 900);
        int budget = nTotal;
        bool Room() => outList.Count < budget;

        // 1. Powerups: the most contested cells, never pockets or leaves, as far apart as possible.
        for (int k = 0; k < want["powerup"] && Room(); k++)
        {
            Cand? best = null;
            double bs = 0;
            foreach (Cand c in cand)
            {
                Cell cl = c.C;
                if (c.Taken || !RedHalf(c.N.X, c.N.Z) || cl.Traffic < 0.6 || cl.Pocket || cl.Leaf || Crowded(c))
                {
                    continue;
                }

                double s = cl.Traffic + (0.05 * Math.Min(MinDist(c, null, major: true), 30)) - (0.02 * Math.Sqrt(((c.N.X - cl.X) * (c.N.X - cl.X)) + ((c.N.Z - cl.Z) * (c.N.Z - cl.Z))));
                if (hill is { } hp)
                {
                    s -= 0.02 * Math.Abs(Math.Sqrt(((c.N.X - hp.X) * (c.N.X - hp.X)) + ((c.N.Z - hp.Z) * (c.N.Z - hp.Z))) - 12);
                }

                if (best is null || s > bs + 1e-9)
                {
                    best = c;
                    bs = s;
                }
            }

            if (best is not null && powerupClasses.Count > 0)
            {
                PlacePair(best, powerupClasses[k % Math.Min(3, powerupClasses.Count)], "powerup");
            }
        }

        // 2. Weapons: chains 7–10 m apart over the zones; rare weapons in contested cells only.
        var zonesWith = new HashSet<int>(placed.Where(p => p.Cat == "weapon").Select(p => p.Space));
        var weaponOf = new Dictionary<Cand, Weapon>();
        for (int k = 0; k < want["weapon"] && Room() && weapons.Count > 0; k++)
        {
            Weapon entry = weapons[k % weapons.Count];
            Cand? best = null;
            double bs = 0;

            // Weapons and powerups already standing alone (nearest other one beyond 14 m, the G15 spacing): a new weapon
            // 7-14 m from one of them closes the gap, so the level's spacing stays exemplar-like however few the
            // weapons (small levels, and levels whose author already placed a couple far apart).
            List<Item> majorsNow = placed.Where(p => p.Cat is "weapon" or "powerup" || existingMajors.Contains(p)).ToList();
            List<Item> lone = majorsNow.Where(m => majorsNow.Where(o => !ReferenceEquals(o, m))
                .Select(o => Math.Sqrt(D2(m.X, m.Y, m.Z, o.X, o.Y, o.Z))).DefaultIfEmpty(double.MaxValue).Min() > 14).ToList();
            foreach (Cand c in cand)
            {
                if (c.Taken || !RedHalf(c.N.X, c.N.Z) || Crowded(c))
                {
                    continue;
                }

                Cell cl = c.C;
                if (entry.Rare && (cl.Traffic < 0.5 || cl.Pocket))
                {
                    continue;
                }

                double nnd = MinDist(c, null, major: true);
                if (nnd < 7)
                {
                    continue;
                }

                double s = 0.6 * cl.Traffic;
                if (nnd <= 10)
                {
                    s += 1.0;
                }
                else if (nnd <= 14)
                {
                    s += 0.4;
                }
                else if (nnd < double.MaxValue)
                {
                    s -= 0.1 * (nnd - 14);
                }

                // Spread over the zones, but never at the cost of the spacing: a new zone counts only while the weapon
                // stays within 14 m of its nearest weapon or powerup (G15), so small levels keep exemplar spacing.
                if (!zonesWith.Contains(c.N.Space) && (nnd <= 14 || nnd == double.MaxValue))
                {
                    s += 0.8;
                }

                foreach (Item m in lone)
                {
                    double dm = Math.Sqrt(D2(c.N.X, c.N.Y, c.N.Z, m.X, m.Y, m.Z));
                    if (dm >= 7 && dm <= 14)
                    {
                        s += 1.2;
                        break;
                    }
                }

                if (cl.Pocket)
                {
                    s -= 0.5;
                }

                if (path.Count >= 2 && PathInfo(c.N).Lat <= 8)
                {
                    s += 0.5;
                }

                s -= 0.35 * c.Near;
                if (best is null || s > bs + 1e-9)
                {
                    best = c;
                    bs = s;
                }
            }

            if (best is not null)
            {
                zonesWith.Add(best.N.Space);
                PlacePair(best, entry.Class, "weapon");
                weaponOf[best] = entry;
            }
        }

        // 3. An item 3.5–5.5 m from every spawn without one within 6 m (in front of it when the floor allows): ammo
        //    for the nearest weapon, else health. These count toward the target.
        foreach (var s in spawns)
        {
            if (!Room())
            {
                break;
            }

            bool have = placed.Any(p => ((p.X - s.X) * (p.X - s.X)) + ((p.Z - s.Z) * (p.Z - s.Z)) <= 36 && Math.Abs(p.Y - s.Y) < 2);
            if (have)
            {
                continue;
            }

            Cand? best = null;
            double bs = 0;
            foreach (Cand c in cand)
            {
                if (c.Taken || Crowded(c))
                {
                    continue;
                }

                double dx = c.N.X - s.X, dz = c.N.Z - s.Z, d = Math.Sqrt((dx * dx) + (dz * dz));
                if (d < 3.5 || d > 5.5 || Math.Abs(c.N.Y - s.Y) >= 1.6)
                {
                    continue;
                }

                double front = ((dx * s.Fx) + (dz * s.Fz)) / Math.Max(d, 1e-6);
                double sc = front - (0.1 * Math.Abs(d - 4.4)) + (0.2 * Math.Min(MinDist(c, null), 6) / 6) - (0.1 * c.Near);
                if (best is null || sc > bs + 1e-9)
                {
                    best = c;
                    bs = sc;
                }
            }

            if (best is null || !probe.Los(s.X, s.Y + 1.0, s.Z, best.N.X, best.N.Y + 1.0, best.N.Z))
            {
                continue;
            }

            Item? wbest = null;
            double wd = double.MaxValue;
            foreach (Item p in placed)
            {
                if (p.Cat == "weapon")
                {
                    double d = D2(best.N.X, best.N.Y, best.N.Z, p.X, p.Y, p.Z);
                    if (d < wd)
                    {
                        wbest = p;
                        wd = d;
                    }
                }
            }

            string cls = health.Count > 0 ? health[0] : "Medical Kit", cat = "health";
            if (wbest is not null && weapons.FirstOrDefault(w => w.Class == wbest.Class) is { } wpn)
            {
                cls = wpn.Ammo;
                cat = "ammo";
            }

            Place(best, cls, cat);
        }

        // 4. Health and armour on the flanks (payload: beside the cart route or at the defenders' holds; KOTH: on the
        //    hill's approaches).
        double FlankScore(Cand c, string same)
        {
            Cell cl = c.C;
            double s = 0;
            if (cl.Traffic >= 0.15 && cl.Traffic <= 0.65)
            {
                s += 1;
            }

            if (cl.Pocket)
            {
                s -= 2;
            }

            s += 0.05 * Math.Min(MinDist(c, same), 20);
            s += 0.04 * Math.Min(MinDist(c, null), 25);   // fill the emptiest stretches first
            s -= 0.35 * c.Near;
            if (path.Count >= 2)
            {
                double lat = PathInfo(c.N).Lat;
                if (lat >= 6 && lat <= 15)
                {
                    s += 1.2;
                }

                for (int i = 1; i < path.Count; i++)
                {
                    Vec3d cp = path[i];
                    if (((c.N.X - cp.X) * (c.N.X - cp.X)) + ((c.N.Z - cp.Z) * (c.N.Z - cp.Z)) <= 400 && c.N.Y >= cp.Y + 1.5)
                    {
                        s += 0.8;
                        break;
                    }
                }
            }

            if (hill is { } hp)
            {
                double hd = Math.Sqrt(((c.N.X - hp.X) * (c.N.X - hp.X)) + ((c.N.Z - hp.Z) * (c.N.Z - hp.Z)));
                if (hd >= 8 && hd <= 16)
                {
                    s += 1.0;
                }
            }

            return s;
        }

        void Fill(string cat, List<string> classes, int kWant, double minGap)
        {
            for (int k = 0; k < kWant && Room() && classes.Count > 0; k++)
            {
                Cand? best = null;
                double bs = 0;
                foreach (Cand c in cand)
                {
                    if (c.Taken || !RedHalf(c.N.X, c.N.Z) || MinDist(c, cat) < minGap || MinDist(c, null) < 3 || Crowded(c))
                    {
                        continue;
                    }

                    double s = FlankScore(c, cat);
                    if (best is null || s > bs + 1e-9)
                    {
                        best = c;
                        bs = s;
                    }
                }

                if (best is null)
                {
                    break;
                }

                PlacePair(best, classes[k % classes.Count], cat);
            }
        }

        Fill("health", health, Math.Max(0, want["health"] - (counts["health"] / pairs)), 6);
        Fill("armor", armor, want["armor"], 6);

        // 5. Ammo beside the weapons (2–6 m).
        double ammoLeft = Math.Max(0, want["ammo"] - (counts["ammo"] / (double)pairs));
        List<Item> weaponsPlaced = placed.Where(p => p.Cat == "weapon").ToList();
        for (int k = 0; ammoLeft > 0.5 && k < weaponsPlaced.Count * 2 && Room(); k++)
        {
            Item wpn = weaponsPlaced[k % weaponsPlaced.Count];
            string? ammo = weapons.FirstOrDefault(w => w.Class == wpn.Class)?.Ammo;
            if (ammo is null || !RedHalf(wpn.X, wpn.Z))
            {
                continue;
            }

            Cand? best = null;
            double bs = 0;
            foreach (Cand c in cand)
            {
                if (c.Taken || !RedHalf(c.N.X, c.N.Z) || Crowded(c))
                {
                    continue;
                }

                double d = Math.Sqrt(D2(c.N.X, c.N.Y, c.N.Z, wpn.X, wpn.Y, wpn.Z));
                if (d >= 2 && d <= 6 && MinDist(c, null) >= 2)
                {
                    double s = FlankScore(c, "ammo") - (0.1 * Math.Abs(d - 4));
                    if (best is null || s > bs + 1e-9)
                    {
                        best = c;
                        bs = s;
                    }
                }
            }

            if (best is not null)
            {
                PlacePair(best, ammo, "ammo");
                ammoLeft -= 1;
            }
        }

        // The report.
        List<Item> majors = placed.Where(p => p.Cat is "weapon" or "powerup").ToList();
        var nn = new List<double>();
        for (int i = 0; i < majors.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < majors.Count; j++)
            {
                if (i != j)
                {
                    best = Math.Min(best, Math.Sqrt(D2(majors[i].X, majors[i].Y, majors[i].Z, majors[j].X, majors[j].Y, majors[j].Z)));
                }
            }

            if (best < double.MaxValue)
            {
                nn.Add(best);
            }
        }

        nn.Sort();
        var s2i = spawns.Select(s => placed.Count == 0 ? double.MaxValue : placed.Min(p => Math.Sqrt(((p.X - s.X) * (p.X - s.X)) + ((p.Z - s.Z) * (p.Z - s.Z)) + ((p.Y - s.Y) * (p.Y - s.Y))))).OrderBy(x => x).ToList();
        static object? Med(List<double> l) => l.Count > 0 && l[(int)Math.Ceiling(l.Count / 2.0) - 1] < double.MaxValue ? l[(int)Math.Ceiling(l.Count / 2.0) - 1] : null;
        var itemsOut = outList.Select(x => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["class"] = x.It.Class, ["cat"] = x.It.Cat, ["x"] = x.It.X, ["y"] = x.It.Y, ["z"] = x.It.Z,
        }).ToArray();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["items"] = itemsOut,
            ["by_category"] = counts.ToDictionary(kv => kv.Key, kv => (object?)(double)kv.Value, StringComparer.Ordinal),
            ["area"] = area,
            ["level_area"] = levelArea,
            ["area_measure"] = areaMeasure,
            ["existing"] = (double)existingHere,
            ["level_existing"] = (double)existingLevel,
            ["target"] = (double)nTotal,
            ["target_per100"] = per100,
            ["local_max"] = (double)LocalMax(placed.Where(p => p.Cat != "existing" || !IsFlagLike(p.Class)).ToList()),
            ["per100"] = (outList.Count + existingHere) / area * 100,
            ["level_per100"] = (outList.Count + existingLevel) / levelArea * 100,
            ["weapon_nn"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["min"] = nn.Count > 0 ? nn[0] : null, ["median"] = Med(nn), ["max"] = nn.Count > 0 ? nn[^1] : null },
            ["spawn_to_item"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["median"] = Med(s2i), ["max"] = s2i.Count > 0 && s2i[^1] < double.MaxValue ? s2i[^1] : null },
        };
    }

    private static bool IsFlagLike(string? cls) =>
        cls is not null && (cls.Contains("flag", StringComparison.OrdinalIgnoreCase) || cls.Contains("base_", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("banner", StringComparison.OrdinalIgnoreCase));

    /// <summary>The most other items within <see cref="LocalRadius"/> of any one item (the local-density report).</summary>
    private static int LocalMax(List<Item> items)
    {
        int best = 0;
        foreach (Item a in items)
        {
            int n = 0;
            foreach (Item b in items)
            {
                double dx = a.X - b.X, dz = a.Z - b.Z;
                if (!ReferenceEquals(a, b) && (dx * dx) + (dz * dz) <= LocalRadius * LocalRadius && Math.Abs(a.Y - b.Y) < 3.5)
                {
                    n++;
                }
            }

            best = Math.Max(best, n);
        }

        return best;
    }

    /// <summary>
    /// Splits n items over the categories by the exemplar shares (largest remainder), so a small call gets the
    /// most important categories first instead of one of each; at least one of each non-powerup category from 4 up.
    /// </summary>
    private static Dictionary<string, int> Allocate(double n, bool powerups)
    {
        var o = new Dictionary<string, int>(StringComparer.Ordinal) { ["weapon"] = 0, ["ammo"] = 0, ["health"] = 0, ["armor"] = 0, ["powerup"] = 0 };
        int total = (int)Math.Floor(n + 1e-9);
        if (total <= 0)
        {
            return o;
        }

        string[] cats = { "weapon", "ammo", "health", "armor" };
        if (powerups && total >= 8)
        {
            o["powerup"] = Math.Max(1, (int)Math.Floor((total * Share["powerup"]) + 0.5));
            total -= o["powerup"];
        }

        double sum = cats.Sum(c => Share[c]);
        var rem = new List<(string Cat, double R)>();
        int given = 0;
        foreach (string c in cats)
        {
            double exact = total * Share[c] / sum;
            int f = (int)Math.Floor(exact);
            if (total >= 4 && f < 1)
            {
                f = 1;
            }

            o[c] = f;
            given += f;
            rem.Add((c, exact - Math.Floor(exact)));
        }

        foreach ((string c, _) in rem.OrderByDescending(r => r.R).ThenBy(r => Array.IndexOf(cats, r.Cat)))
        {
            if (given >= total)
            {
                break;
            }

            o[c]++;
            given++;
        }

        while (given > total)
        {
            string c = cats.Where(x => o[x] > 1).OrderBy(x => Share[x]).FirstOrDefault() ?? cats[0];
            o[c]--;
            given--;
        }

        return o;
    }

    /// <summary>The 8 m cell graph of the nodes: route betweenness (as a percentile), pockets and leaves.</summary>
    private static List<Cell> CellGraph(List<KitFloorNode> nodes, out Dictionary<KitFloorNode, Cell> cellOf)
    {
        cellOf = new Dictionary<KitFloorNode, Cell>(ReferenceEqualityComparer.Instance);
        var index = new Dictionary<(long, long, long), Cell>();
        var order = new List<Cell>();
        foreach (KitFloorNode n in nodes)
        {
            var key = ((long)Math.Floor(n.X / 8), (long)Math.Floor(n.Z / 8), Math.Min(9, Math.Max(0, (((long)Math.Floor((n.Y + 40) / 6) % 10) + 10) % 10)));
            if (!index.TryGetValue(key, out Cell? c))
            {
                c = new Cell { Id = order.Count };
                index[key] = c;
                order.Add(c);
            }

            c.N++;
            c.X += n.X;
            c.Y += n.Y;
            c.Z += n.Z;
            cellOf[n] = c;
        }

        foreach (Cell c in order)
        {
            c.X /= c.N;
            c.Y /= c.N;
            c.Z /= c.N;
        }

        var at = new Dictionary<(long, long), List<KitFloorNode>>();
        foreach (KitFloorNode n in nodes)
        {
            var k = ((long)Math.Floor(n.X / KitProbe.Pitch), (long)Math.Floor(n.Z / KitProbe.Pitch));
            if (!at.TryGetValue(k, out List<KitFloorNode>? l))
            {
                at[k] = l = new List<KitFloorNode>();
            }

            l.Add(n);
        }

        int[][] n4 = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
        foreach (KitFloorNode n in nodes)
        {
            Cell cn = cellOf[n];
            long i = (long)Math.Floor(n.X / KitProbe.Pitch), k = (long)Math.Floor(n.Z / KitProbe.Pitch);
            foreach (int[] d in n4)
            {
                if (!at.TryGetValue((i + d[0], k + d[1]), out List<KitFloorNode>? l))
                {
                    continue;
                }

                foreach (KitFloorNode m in l)
                {
                    Cell cm = cellOf[m];
                    if (cm != cn && Math.Abs(m.Y - n.Y) <= KitProbe.Step && cn.NbrSet.Add(cm.Id))
                    {
                        cn.Nbr.Add(cm.Id);
                        if (cm.NbrSet.Add(cn.Id))
                        {
                            cm.Nbr.Add(cn.Id);
                        }
                    }
                }
            }
        }

        // Betweenness (Brandes, unweighted), then percentiles.
        int nc = order.Count;
        var cb = new double[nc];
        var sigma = new double[nc];
        var dist = new int[nc];
        var delta = new double[nc];
        var pred = new List<int>[nc];
        for (int i = 0; i < nc; i++)
        {
            pred[i] = new List<int>();
        }

        var stack = new int[nc];
        var queue = new int[nc];
        for (int s = 0; s < nc; s++)
        {
            for (int i = 0; i < nc; i++)
            {
                pred[i].Clear();
                sigma[i] = 0;
                dist[i] = -1;
                delta[i] = 0;
            }

            sigma[s] = 1;
            dist[s] = 0;
            int head = 0, tail = 0, ns = 0;
            queue[tail++] = s;
            while (head < tail)
            {
                int v = queue[head++];
                stack[ns++] = v;
                foreach (int w in order[v].Nbr)
                {
                    if (dist[w] < 0)
                    {
                        dist[w] = dist[v] + 1;
                        queue[tail++] = w;
                    }

                    if (dist[w] == dist[v] + 1)
                    {
                        sigma[w] += sigma[v];
                        pred[w].Add(v);
                    }
                }
            }

            for (int q = ns - 1; q >= 0; q--)
            {
                int w = stack[q];
                foreach (int v in pred[w])
                {
                    delta[v] += sigma[v] / sigma[w] * (1 + delta[w]);
                }

                if (w != s)
                {
                    cb[w] += delta[w];
                }
            }
        }

        double[] sorted = cb.OrderBy(x => x).ToArray();
        for (int i = 0; i < nc; i++)
        {
            int below = 0;
            foreach (double v in sorted)
            {
                if (v < cb[i])
                {
                    below++;
                }
            }

            order[i].Traffic = nc > 1 ? below / (double)(nc - 1) : 1;
        }

        // Pockets: remove one cell; pieces (not the largest remainder) under 10 % of the floor.
        int totalN = order.Sum(c => c.N);
        foreach (Cell cut in order)
        {
            if (cut.Nbr.Count == 1)
            {
                cut.Leaf = true;
                continue;
            }

            if (cut.Nbr.Count < 2)
            {
                continue;
            }

            var seen = new HashSet<int> { cut.Id };
            var comps = new List<(List<int> List, int Area)>();
            foreach (int start in cut.Nbr)
            {
                if (!seen.Add(start))
                {
                    continue;
                }

                var comp = new List<int>();
                int areaN = 0;
                var q = new Queue<int>();
                q.Enqueue(start);
                while (q.Count > 0)
                {
                    int v = q.Dequeue();
                    comp.Add(v);
                    areaN += order[v].N;
                    foreach (int w in order[v].Nbr)
                    {
                        if (seen.Add(w))
                        {
                            q.Enqueue(w);
                        }
                    }
                }

                comps.Add((comp, areaN));
            }

            if (comps.Count >= 2)
            {
                foreach ((List<int> list, int areaN) in comps)
                {
                    if (areaN < 0.1 * totalN)
                    {
                        foreach (int v in list)
                        {
                            order[v].Pocket = true;
                        }
                    }
                }
            }
        }

        return order;
    }

    private static List<Weapon> Weapons(ScriptArgs a)
    {
        var o = new List<Weapon>();
        if (a.Named.TryGetValue("weapons", out object? v) && v is ScriptArgs l)
        {
            foreach (object? item in l.Items)
            {
                if (item is ScriptArgs w && w.Items.Count >= 2)
                {
                    o.Add(new Weapon(w.Items[0]?.ToString() ?? string.Empty, w.Items[1]?.ToString() ?? string.Empty, w.Named.TryGetValue("rare", out object? r) && r is true));
                }
            }
        }

        return o;
    }

    private static List<string> Strings(ScriptArgs a, string key) =>
        a.Named.TryGetValue(key, out object? v) && v is ScriptArgs l ? l.Items.Select(x => x?.ToString() ?? string.Empty).ToList() : new List<string>();

    private static Dictionary<string, object?> Fail(string message, string? hint) =>
        new(StringComparer.Ordinal) { ["error"] = message, ["hint"] = hint };
}
