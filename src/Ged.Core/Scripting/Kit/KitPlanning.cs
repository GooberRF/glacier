using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.Editing.Organic;
using Ged.Core.Model;
using Ged.Core.Spatial;

namespace Ged.Core.Scripting.Kit;

/// <summary>A space a population call works on (a kit record's box, a named space or an area).</summary>
internal sealed record KitSpace(string Name, string Kind, double X0, double Y0, double Z0, double X1, double Y1, double Z1);

/// <summary>A playable floor node of a population call (the Lua probe's playable() entries).</summary>
internal sealed class KitFloorNode
{
    public double X, Y, Z, Top;
    public long I, K;
    public int Space;
    public bool Clear;
}

/// <summary>The playable floor of a set of spaces, per space (nodes, doors, walk area).</summary>
internal sealed class KitPlayable
{
    public readonly List<KitFloorNode> All = new();
    public readonly List<(List<KitFloorNode> Nodes, List<KitFloorNode> Doors, double Area, KitSpace S)> PerSpace = new();

    /// <summary>
    /// Lattice nodes of each space reachable on foot (walk area seeds), standing, flagged clear when at least
    /// <paramref name="margin"/> from a floor edge; a floor shared by overlapping spaces counts once. <paramref name="zoneOf"/>
    /// gives the node test of a space by name: a kit.build_layout zone stands on the floor inside its own air brushes
    /// only, since its box may hold a turned or traced neighbour's floor (see <see cref="ScriptKitCore.Zone"/>).
    /// </summary>
    public KitPlayable(KitProbe probe, IReadOnlyList<KitSpace> spaces, double margin, Func<string, Func<double, double, double, bool>?>? zoneOf = null)
    {
        var claimed = new HashSet<(long, long)>();
        for (int si = 0; si < spaces.Count; si++)
        {
            KitSpace s = spaces[si];
            KitLattice l = probe.Lattice(s.X0, s.Z0, s.X1, s.Z1, s.Y0 - 0.1, Math.Max(s.Y0, s.Y1 - 1.0), KitProbe.Headroom, 1, zoneOf?.Invoke(s.Name));
            var doors = l.DoorNodes(s.X0, s.Z0, s.X1, s.Z1).Select(id => new KitFloorNode { X = l.X[id], Y = l.Y[id], Z = l.Z[id], Top = l.Top[id], I = l.I[id], K = l.K[id], Space = si }).ToList();
            double area = l.WalkArea(out bool[] reach);
            bool[] near = l.NearEdge(margin);
            var list = new List<KitFloorNode>();
            for (int id = 1; id <= l.Count; id++)
            {
                long hy = (long)Math.Floor((l.Y[id] * 4) + 0.5);
                var key = (KitProbe.Key(l.I[id], l.K[id]), ((hy % 64) + 64) % 64);
                if (reach[id] && claimed.Add(key))
                {
                    var n = new KitFloorNode { X = l.X[id], Y = l.Y[id], Z = l.Z[id], Top = l.Top[id], I = l.I[id], K = l.K[id], Space = si, Clear = !near[id] };
                    list.Add(n);
                    All.Add(n);
                }
            }

            PerSpace.Add((list, doors, area, s));
        }
    }
}

/// <summary>Shared reading of the planners' Lua arguments.</summary>
internal static class KitArgs
{
    public static double Num(object? v) => v switch
    {
        double d => d,
        null => double.NaN,
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    public static double Num(ScriptArgs a, string key, double fallback) =>
        a.Named.TryGetValue(key, out object? v) && v is not null ? Num(v) : fallback;

    public static List<KitSpace> Spaces(ScriptArgs a)
    {
        var o = new List<KitSpace>();
        if (a.Named.TryGetValue("spaces", out object? v) && v is ScriptArgs list)
        {
            foreach (object? item in list.Items)
            {
                if (item is ScriptArgs s)
                {
                    o.Add(new KitSpace(s.String("kit", "name") ?? "space", s.String("kit", "kind") ?? "room",
                        Num(s, "x0", 0), Num(s, "y0", 0), Num(s, "z0", 0), Num(s, "x1", 0), Num(s, "y1", 0), Num(s, "z1", 0)));
                }
            }
        }

        return o;
    }

    /// <summary>A list of points {x, y, z} (tables with x/y/z or 1/2/3).</summary>
    public static List<Vec3d> Points(ScriptArgs a, string key)
    {
        var o = new List<Vec3d>();
        if (a.Named.TryGetValue(key, out object? v) && v is ScriptArgs list)
        {
            foreach (object? item in list.Items)
            {
                if (item is ScriptArgs p)
                {
                    o.Add(p.Items.Count >= 3 ? new Vec3d(Num(p.Items[0]), Num(p.Items[1]), Num(p.Items[2])) : new Vec3d(Num(p, "x", 0), Num(p, "y", 0), Num(p, "z", 0)));
                }
            }
        }

        return o;
    }

    public static object?[] List<T>(IEnumerable<T> items, Func<T, object?> f) => items.Select(f).ToArray();
}

/// <summary>
/// The grader's sight measure for the kit: the level's compiled geometry (its stored build when it matches the
/// brushes, else a private sealed build of the current brushes that is never applied, with the same compile
/// options as ops.build) measured by <see cref="SpatialAnalysis"/> with every floor component playable. Cached
/// per brush layout for the run.
/// </summary>
internal static class KitDecalFaces
{
    /// <summary>
    /// The compiled faces kit.dress checks its decals against: the stored build when it is current, else a private sealed
    /// build of the current brushes (t-junction sealing is what lengthens the faces, so never a preview build). Cached
    /// by the structural brushes: dressing adds only detail brushes, which t-junction sealing never splits and which
    /// the kit keeps off world-face edges, so a level's successive dress calls share one build. Null when the level
    /// has no compiled faces.
    /// </summary>
    public static Linting.DecalClipCheck.FaceIndex? For(ScriptKitCore core, out string note)
    {
        ScriptContext ctx = core.Context;
        long fp = StructuralFingerprint(ctx.Document.Rfl);
        if (core.DecalCache is { } c && c.Fingerprint == fp)
        {
            note = c.Note;
            return c.Index;
        }

        Linting.DecalClipCheck.FaceIndex? index = null;
        try
        {
            Geometry? geo;
            string how;
            if (ctx.Spatial.CurrentFreshness().State == "built")
            {
                geo = SpatialLevel.StoredGeometry(ctx.Document.Rfl);
                how = "the stored build";
            }
            else
            {
                geo = GeometryBuildService.Build(ctx.Document.Rfl, KitSight.Options(ctx)).Geometry;
                how = "a private sealed build of the current brushes";
            }

            if (geo is { Faces.Count: > 0 })
            {
                index = new Linting.DecalClipCheck.FaceIndex(geo);
                note = "decals checked against the compiled faces of " + how;
            }
            else
            {
                note = "no compiled geometry: decals not checked";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            note = "the level could not be built (" + ex.Message + "): decals not checked";
        }

        core.DecalCache = (fp, index, note);
        return index;
    }

    /// <summary>A hash of the non-detail brushes (ids, flags, bounds, face counts) in time order.</summary>
    private static long StructuralFingerprint(IO.Rfl.RflFile rfl)
    {
        long h = 23;
        foreach (Brush b in rfl.Sections.Select(s => s.Content).OfType<IO.Rfl.Sections.BrushesSection>().SelectMany(s => s.Brushes))
        {
            if ((b.Flags & (uint)BrushFlags.Detail) != 0)
            {
                continue;
            }

            ScriptAabb bb = ScriptBrushHandle.WorldBounds(b);
            var hc = new HashCode();
            hc.Add(b.Uid);
            hc.Add(b.Flags);
            hc.Add(bb.Min);
            hc.Add(bb.Max);
            hc.Add(b.Geometry.Faces.Count);
            h = unchecked((h * 31) + hc.ToHashCode());
        }

        return h;
    }
}

internal static class KitSight
{
    public static SpatialAnalysis? For(ScriptKitCore core, out string note)
    {
        ScriptContext ctx = core.Context;
        long fp = SpatialLevel.BrushFingerprint(ctx.Document.Rfl);
        if (core.SightCache is { } c && c.Fingerprint == fp)
        {
            note = c.Note;
            return c.Analysis;
        }

        SpatialAnalysis? analysis = null;
        try
        {
            Geometry? geo = null;
            string how;
            if (ctx.Spatial.CurrentFreshness().State == "built")
            {
                geo = SpatialLevel.StoredGeometry(ctx.Document.Rfl);
                how = "the stored build";
            }
            else
            {
                CompiledLevel built = GeometryBuildService.Build(ctx.Document.Rfl, Options(ctx));
                geo = built.Geometry;
                how = "a private sealed build of the current brushes";
            }

            if (geo is null || geo.Faces.Count == 0)
            {
                note = "no compiled geometry: exposure measured on the kit's probe";
            }
            else
            {
                SpatialLevel level = SpatialLevel.FromDocument(ctx.Document, geo, new GeometryFreshness("built", "sealed"));
                // Every floor component counts (the level has no spawns yet: the walk that finds the standable floor starts
                // from each component's main floor), on the grader's floor set and stable sampling, so a planned spawn's
                // exposure is the one the grader's spawn report measures.
                analysis = SpatialAnalysis.Run(level, new SpatialOptions { PlayableAllFloor = true, Focus = false }, ctx.Cancellation);
                note = "the grader's spawn exposure and line of sight (spatial.spawn_report) on " + how;
            }
        }
        catch (Exception ex) when (ex is SpatialException or InvalidOperationException or ArgumentException)
        {
            note = "the level could not be measured (" + ex.Message + "): exposure measured on the kit's probe";
            analysis = null;
        }

        core.SightCache = (fp, analysis, note);
        return analysis;
    }

    internal static CompileOptions Options(ScriptContext ctx)
    {
        ScriptServices sv = ctx.Services;
        Assets.TextureTraitsCache? traits = sv.TextureTraits ?? (sv.Assets is { } vfs ? new Assets.TextureTraitsCache(vfs) : null);
        var request = new CompileRequest { Sealed = true, Cancellation = ctx.Cancellation };
        return CompileOptionsFactory.CreateForDocument(ctx.Document, sv.Assets, sv.BuildSettings ?? BuildSettings.EditorDefaults, request, traits, null);
    }
}
