using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;
using Ged.Core.Scripting.Kit;

namespace Ged.Core.Scripting;

/// <summary>
/// The design kit's native engine, reached only by the shipped kit modules through <c>ged.import("kit_core")</c>
/// (it is not a documented global): the space probe (column map, floor lattice, walk / foot graphs, sight and
/// ray tests) and the population planners (kit.spawn_points, kit.items) run here in C#, so populating a whole
/// level stays far inside the script instruction budget. The kit functions that use it are the public API.
/// </summary>
public sealed class ScriptKitCore
{
    private readonly ScriptContext _ctx;
    private ScriptKitProbe? _probe;
    private long[] _sigs = Array.Empty<long>();

    internal ScriptKitCore(ScriptContext ctx) => _ctx = ctx;

    internal ScriptContext Context => _ctx;

    /// <summary>The kit's sight measure for the current brushes (see <see cref="KitSight"/>).</summary>
    internal (long Fingerprint, Spatial.SpatialAnalysis? Analysis, string Note)? SightCache { get; set; }

    /// <summary>The compiled faces kit.dress checks decals against (see <see cref="KitDecalFaces"/>).</summary>
    internal (long Fingerprint, Linting.DecalClipCheck.FaceIndex? Index, string Note)? DecalCache { get; set; }

    /// <summary>
    /// The level's shared probe: rebuilt when the brushes changed since it was made (count, ids, time order, bounds,
    /// air/solid), extended when brushes were only added at the end of time order.
    /// </summary>
    public ScriptKitProbe Probe()
    {
        IReadOnlyList<Brush> list = _ctx.Brushes.Brushes;
        long[] sigs = Signatures(list);
        if (_probe is not null && sigs.AsSpan().SequenceEqual(_sigs))
        {
            return _probe;
        }

        if (_probe is not null && sigs.Length >= _sigs.Length && sigs.AsSpan(0, _sigs.Length).SequenceEqual(_sigs))
        {
            for (int i = _sigs.Length; i < list.Count; i++)
            {
                _probe.AddBrushModel(list[i], i);
            }

            _sigs = sigs;
            return _probe;
        }

        var probe = new KitProbe();
        for (int i = 0; i < list.Count; i++)
        {
            if (Record(list[i], i) is { } r)
            {
                probe.Insert(r);
            }
        }

        _probe = new ScriptKitProbe(this, probe);
        _sigs = sigs;
        return _probe;
    }

    /// <summary>Marks the shared probe current again after the kit added the brushes it made (add_brush).</summary>
    public void Touch() => _sigs = Signatures(_ctx.Brushes.Brushes);

    /// <summary>A stamp of the current brushes (equal stamps: no brush changed in a way the probe reads).</summary>
    public double Stamp()
    {
        long h = 17;
        foreach (long v in Signatures(_ctx.Brushes.Brushes))
        {
            h = unchecked((h * 31) + v);
        }

        return h % 9007199254740991L;
    }

    internal KitProbe.Rec? Record(Brush b, int timeIndex) =>
        KitProbe.RecordOf(b, timeIndex, _ctx.Movers.FindGroupForMember(b.Uid) is not null, null);

    internal string? KitTag(int uid) => _ctx.Metadata.Tags(uid).GetValueOrDefault("kit");

    private static long[] Signatures(IReadOnlyList<Brush> list)
    {
        var s = new long[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            Brush b = list[i];
            ScriptAabb bb = ScriptBrushHandle.WorldBounds(b);
            var h = new HashCode();
            h.Add(b.Uid);
            h.Add(b.Flags & (uint)(BrushFlags.Air | BrushFlags.Portal | BrushFlags.Detail));
            h.Add(bb.Min);
            h.Add(bb.Max);
            h.Add(b.Geometry.Faces.Count);
            s[i] = h.ToHashCode();
        }

        return s;
    }

    /// <summary>Plans kit.spawn_points (see kit_items.lua): positions, headings and the report.</summary>
    public Dictionary<string, object?> SpawnPlan(ScriptArgs args)
    {
        using (_ctx.EnterHostOperation())
        {
            return KitSpawnPlanner.Plan(this, args);
        }
    }

    /// <summary>
    /// kit.dress's decal check: for each decal box {x, y, z, fx, fy, fz, ux, uy, uz, w, h, d} (centre, forward, up, full
    /// extents) the most vertex records of a compiled face it touches (0: none), and the measure used. A decal over
    /// <see cref="Linting.DecalClipCheck.MaxFaceVertexRecords"/> crashes the game and the editor while the level loads.
    /// </summary>
    public Dictionary<string, object?> DecalCheck(ScriptArgs args)
    {
        using (_ctx.EnterHostOperation())
        {
            Linting.DecalClipCheck.FaceIndex? index = KitDecalFaces.For(this, out string note);
            var records = new List<object?>();
            if (args.Named.TryGetValue("decals", out object? dv) && dv is ScriptArgs list)
            {
                foreach (object? o in list.Items)
                {
                    if (o is not ScriptArgs d || index is null)
                    {
                        records.Add(0.0);
                        continue;
                    }

                    float N(string k) => (float)KitArgs.Num(d, k, 0);
                    var fwd = new Vec3(N("fx"), N("fy"), N("fz"));
                    var up = new Vec3(N("ux"), N("uy"), N("uz"));
                    var rot = new Mat3(fwd, up.Cross(fwd), up);
                    (_, int n) = index.WorstTouched(new Vec3(N("x"), N("y"), N("z")), rot, new Vec3(N("w"), N("h"), N("d")));
                    records.Add((double)n);
                }
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["records"] = records.ToArray(),
                ["limit"] = (double)Linting.DecalClipCheck.MaxFaceVertexRecords,
                ["checked"] = index is not null,
                ["measure"] = note,
            };
        }
    }

    /// <summary>Plans kit.items (see kit_items.lua): classes, positions and the report.</summary>
    public Dictionary<string, object?> ItemPlan(ScriptArgs args)
    {
        using (_ctx.EnterHostOperation())
        {
            return KitItemPlanner.Plan(this, args);
        }
    }
}

/// <summary>The kit probe handle (kit_probe.lua's probe).</summary>
public sealed class ScriptKitProbe
{
    private readonly ScriptKitCore _core;

    internal ScriptKitProbe(ScriptKitCore core, KitProbe probe)
    {
        _core = core;
        Model = probe;
    }

    internal KitProbe Model { get; }

    /// <summary>Columns computed so far.</summary>
    public int Columns => Model.ColumnsComputed;

    public bool Air(double x, double y, double z) => Model.Air(x, y, z);

    public bool AirExact(double x, double y, double z) => Model.AirExact(x, y, z);

    public bool Los(double ax, double ay, double az, double bx, double by, double bz) => Model.Los(ax, ay, az, bx, by, bz);

    public double Ray(double x, double y, double z, double dx, double dz, double maxd) => Model.Ray(x, y, z, dx, dz, maxd);

    public double WallDistance(double x, double y, double z, double dx, double dz, double maxd) => Model.WallDistance(x, y, z, dx, dz, maxd);

    public double Protection(double x, double y, double z, double d) => Model.Protection(x, y, z, d);

    /// <summary>{dx, dz, length} of the longest of 16 chest-height rays.</summary>
    public double[] LongestRay(double x, double y, double z, double maxd)
    {
        (double dx, double dz, double d) = Model.LongestRay(x, y, z, maxd);
        return new[] { dx, dz, d };
    }

    public void AddBlock(double x0, double y0, double z0, double x1, double y1, double z1) => Model.AddBlock(x0, y0, z0, x1, y1, z1);

    /// <summary>Adds a brush the kit made (the newest in time order).</summary>
    public void AddBrush(ScriptBrushHandle brush)
    {
        Brush? b = _core.Context.Brushes.FindBrush(brush.Uid);
        if (b is not null)
        {
            AddBrushModel(b, _core.Context.Brushes.TimeIndex(b.Uid));
        }
    }

    internal void AddBrushModel(Brush b, int timeIndex)
    {
        if (_core.Record(b, timeIndex) is { } r)
        {
            Model.AddRecord(r);
        }
    }

    /// <summary>Marks the shared probe current again (after add_brush).</summary>
    public void Touch() => _core.Touch();

    /// <summary>The walkable area (m²) of the floor lattice over a box, measured in the engine only (no node tables).</summary>
    public double WalkArea(double x0, double z0, double x1, double z1, double ylo, double yhi, double headroom)
    {
        using (_core.Context.EnterHostOperation())
        {
            return Model.Lattice(x0, z0, x1, z1, ylo, yhi, headroom, 1).WalkArea(out _);
        }
    }

    /// <summary>The floor lattice over a box (stride 1 = full lattice with neighbour links).</summary>
    public ScriptKitLattice Lattice(double x0, double z0, double x1, double z1, double ylo, double yhi, double headroom, int stride = 1)
    {
        using (_core.Context.EnterHostOperation())
        {
            return new ScriptKitLattice(_core.Context, Model.Lattice(x0, z0, x1, z1, ylo, yhi, headroom, stride));
        }
    }

    /// <summary>
    /// Existing dressing in a box (the study's classes: beams, slabs, small solids and detail brushes whose centre is
    /// in it, light fixtures left out) — brushes only; the caller adds clutter and decals.
    /// </summary>
    public int DressingIn(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        int n = 0;
        foreach (KitProbe.Rec r in Model.Records)
        {
            if (r.Air)
            {
                continue;
            }

            double cx = (r.X0 + r.X1) / 2, cz = (r.Z0 + r.Z1) / 2, cy = (r.Y0 + r.Y1) / 2;
            if (cx < x0 || cx > x1 || cz < z0 || cz > z1 || cy < y0 - 0.5 || cy > y1 + 0.5)
            {
                continue;
            }

            r.Kit ??= _core.KitTag(r.Uid) ?? string.Empty;
            if (r.Kit == "light_room")
            {
                continue;
            }

            double[] e = { r.X1 - r.X0, r.Y1 - r.Y0, r.Z1 - r.Z0 };
            Array.Sort(e);
            bool beam = e[0] <= 0.75 && e[1] <= 0.75 && e[2] >= 2;
            bool slab = e[0] <= 0.5 && e[1] > 0.75 && e[2] <= 16;
            bool small = e[2] <= 4 && e[0] * e[1] * e[2] <= 16;
            if ((beam || slab || small || r.Detail) && e[2] <= 24)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Centres {x, z, x, z, …} of the detail solids whose centre lies in a box (±0.5 m in height).</summary>
    public double[] DetailCentres(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var o = new List<double>();
        foreach (KitProbe.Rec r in Model.Records)
        {
            if (r.Air || !r.Detail)
            {
                continue;
            }

            double cx = (r.X0 + r.X1) / 2, cz = (r.Z0 + r.Z1) / 2, cy = (r.Y0 + r.Y1) / 2;
            if (cx >= x0 && cx <= x1 && cz >= z0 && cz <= z1 && cy >= y0 - 0.5 && cy <= y1 + 0.5)
            {
                o.Add(cx);
                o.Add(cz);
            }
        }

        return o.ToArray();
    }
}

/// <summary>A floor lattice handle (kit_probe.lua builds its node tables from <see cref="Arrays"/>).</summary>
public sealed class ScriptKitLattice
{
    internal ScriptKitLattice(ScriptContext ctx, KitLattice lattice)
    {
        Ctx = ctx;
        Model = lattice;
    }

    internal ScriptContext Ctx { get; }

    internal KitLattice Model { get; }

    public int Count => Model.Count;

    /// <summary>The node fields as parallel lists (index = node id): i, k, x, z, y, top.</summary>
    public Dictionary<string, object?> Arrays() => new(StringComparer.Ordinal)
    {
        ["i"] = Model.I.Skip(1).Select(v => (double)v).ToArray(),
        ["k"] = Model.K.Skip(1).Select(v => (double)v).ToArray(),
        ["x"] = Model.X.Skip(1).ToArray(),
        ["z"] = Model.Z.Skip(1).ToArray(),
        ["y"] = Model.Y.Skip(1).ToArray(),
        ["top"] = Model.Top.Skip(1).ToArray(),
    };

    private static bool[] Ids(bool[] set) => set.AsSpan(1).ToArray();

    /// <summary>{area, reach (per node id)}: the walkable area and the reach set.</summary>
    public Dictionary<string, object?> WalkArea()
    {
        double a = Model.WalkArea(out bool[] reach);
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["area"] = a, ["reach"] = Ids(reach) };
    }

    public bool[] Wide() => Ids(Model.Wide());

    public bool[] NearEdge(double r) => Ids(Model.NearEdge(r));

    public bool[] Flat(double tol) => Ids(Model.Flat(tol));

    public int[] StepNodes(double tol) => Model.StepNodes(tol).ToArray();

    public int[] DoorNodes(double x0, double z0, double x1, double z1) => Model.DoorNodes(x0, z0, x1, z1).ToArray();

    /// <summary>Foot components over the given node set (per node id: component, 0 = not in the set).</summary>
    public int[] Components(ScriptArgs inside)
    {
        var set = new bool[Model.Count + 1];
        for (int i = 0; i < inside.Items.Count && i < Model.Count; i++)
        {
            set[i + 1] = inside.Items[i] is true;
        }

        return Model.Components(set).AsSpan(1).ToArray();
    }

    /// <summary>Components of the live wide set (the dressing route groups).</summary>
    public int[] WideComponents() => Model.Components(Model.LiveWide).AsSpan(1).ToArray();

    /// <summary>True when a node is in the live wide set.</summary>
    public bool IsWide(int id) => id > 0 && id <= Model.Count && Model.LiveWide[id];

    /// <summary>Removes the listed node ids from the live wide set.</summary>
    public void Unwide(ScriptArgs ids) => Model.Unwide(ids.Items.Select(v => (int)Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The route check of a dressing try: groups = lists of anchor node ids, gone = node ids taken.</summary>
    public bool RoutesOk(ScriptArgs groups, ScriptArgs gone)
    {
        var g = new List<IReadOnlyList<int>>();
        foreach (object? item in groups.Items)
        {
            if (item is ScriptArgs a)
            {
                g.Add(a.Items.Select(v => (int)Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToList());
            }
        }

        var gn = gone.Items.Select(v => (int)Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return Model.RoutesOk(g, gn);
    }

    /// <summary>
    /// A dressing space over this lattice: space = {x0, y0, z0, x1, y1, z1}, keep = caller keep-outs ({x, y, z, r} or
    /// {x0, y0, z0, x1, y1, z1}), objects = {{kind, x, y, z}, …} in and around the space, budget = walk share.
    /// </summary>
    public ScriptKitDressSpace DressSpace(ScriptArgs a)
    {
        var s = a.Named.TryGetValue("space", out object? sv) && sv is ScriptArgs sa
            ? new KitSpace("space", "room", KitArgs.Num(sa, "x0", 0), KitArgs.Num(sa, "y0", 0), KitArgs.Num(sa, "z0", 0), KitArgs.Num(sa, "x1", 0), KitArgs.Num(sa, "y1", 0), KitArgs.Num(sa, "z1", 0))
            : throw new ScriptApiException("dress_space: 'space' is required.");
        var keep = new List<(double, double, double, double, bool, double, double, double, double, double, double)>();
        if (a.Named.TryGetValue("keep", out object? kv) && kv is ScriptArgs kl)
        {
            foreach (object? item in kl.Items)
            {
                if (item is ScriptArgs k)
                {
                    bool box = k.Has("x0");
                    keep.Add((KitArgs.Num(k, "x", 0), KitArgs.Num(k, "y", 0), KitArgs.Num(k, "z", 0), KitArgs.Num(k, "r", 1.5), box,
                        KitArgs.Num(k, "x0", 0), KitArgs.Num(k, "y0", 0), KitArgs.Num(k, "z0", 0), KitArgs.Num(k, "x1", 0), KitArgs.Num(k, "y1", 0), KitArgs.Num(k, "z1", 0)));
                }
            }
        }

        var objects = new List<(string, double, double, double)>();
        if (a.Named.TryGetValue("objects", out object? ov) && ov is ScriptArgs ol)
        {
            foreach (object? item in ol.Items)
            {
                if (item is ScriptArgs o && o.Items.Count >= 4)
                {
                    objects.Add((o.Items[0]?.ToString() ?? string.Empty, KitArgs.Num(o.Items[1]), KitArgs.Num(o.Items[2]), KitArgs.Num(o.Items[3])));
                }
            }
        }

        return new ScriptKitDressSpace(Ctx, new KitDressSpace(Model, s, keep, objects, KitArgs.Num(a, "budget", 0.015)));
    }

    /// <summary>The node of column (i, k) nearest height y within tol (0 = none).</summary>
    public int NodeAt(double i, double k, double y, double tol) => Model.NodeAt((long)i, (long)k, y, tol);
}

/// <summary>A space being dressed (kit_dress.lua's Space; see <see cref="KitDressSpace"/>).</summary>
public sealed class ScriptKitDressSpace
{
    private readonly ScriptContext _ctx;

    internal ScriptKitDressSpace(ScriptContext ctx, KitDressSpace space)
    {
        _ctx = ctx;
        Model = space;
    }

    internal KitDressSpace Model { get; }

    public double AreaBefore => Model.AreaBefore;

    public double Budget => Model.Budget;

    public double Lost => Model.Lost;

    public bool[] Flat() => Model.Flat.AsSpan(1).ToArray();

    public bool[] Keepout() => Model.Keepout.AsSpan(1).ToArray();

    public int[] Doors() => Model.Doors.ToArray();

    /// <summary>
    /// Tries a prop (parts: local boxes {u0, u1, v0, v1, y0, y1}); returns {token (0 = does not fit), world =
    /// {{x0, y0, z0, x1, y1, z1, base}, …}}.
    /// </summary>
    public Dictionary<string, object?> Try(ScriptArgs parts, double ax, double ay, double az, double vx, double vz, bool wall, double pad)
    {
        var list = new List<(double, double, double, double, double, double)>();
        foreach (object? item in parts.Items)
        {
            if (item is ScriptArgs p)
            {
                list.Add((KitArgs.Num(p, "u0", 0), KitArgs.Num(p, "u1", 0), KitArgs.Num(p, "v0", 0), KitArgs.Num(p, "v1", 0), KitArgs.Num(p, "y0", 0), KitArgs.Num(p, "y1", 0)));
            }
        }

        using (_ctx.EnterHostOperation())
        {
            (int token, var world) = Model.Try(list, ax, ay, az, vx, vz, wall, pad);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["token"] = (double)token,
                ["world"] = token == 0 ? null : world.Select(w => (object?)new[] { w.X0, w.Y0, w.Z0, w.X1, w.Y1, w.Z1, w.Base }).ToArray(),
            };
        }
    }

    /// <summary>Wall spots as parallel lists {id, h, dist} (see <see cref="KitDressSpace.WallSpots"/>).</summary>
    public Dictionary<string, object?> WallSpots()
    {
        using (_ctx.EnterHostOperation())
        {
            var w = Model.WallSpots();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = w.Select(x => (double)x.Id).ToArray(), ["h"] = w.Select(x => (double)x.H).ToArray(), ["dist"] = w.Select(x => x.Dist).ToArray(),
            };
        }
    }

    /// <summary>Commits an accepted prop; returns the node ids it newly occupies.</summary>
    public int[] Commit(int token) => Model.Commit(token).ToArray();
}
