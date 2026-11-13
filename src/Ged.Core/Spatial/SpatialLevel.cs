using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

/// <summary>How fresh the compiled geometry a spatial result measured is (DESIGN-QUALITY-SPEC §(e) guardrails).</summary>
/// <param name="State">
/// The level's own compiled geometry: <c>built</c> (a sealed build matching the brushes), <c>preview</c> (an unsealed
/// preview build), <c>stale</c> (brushes changed since the last build), <c>none</c> (never built) or <c>unknown</c>.
/// </param>
/// <param name="Measured">What was measured: <c>stored</c> (the level's compiled geometry as it is), or <c>preview</c> /
/// <c>sealed</c> (a fresh build made for this query and not applied).</param>
/// <param name="Note">A sentence for the caller when the result may not match the brushes, else null.</param>
public sealed record GeometryFreshness(string State, string Measured, string? Note = null)
{
    /// <summary>The stored geometry of a file read from disk (nothing to compare it with).</summary>
    public static GeometryFreshness StoredFile { get; } = new("built", "stored");

    /// <summary>A short JSON-friendly object.</summary>
    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["state"] = State,
        ["measured"] = Measured,
        ["note"] = Note,
    };
}

/// <summary>What a spatial analysis reads from a level: its compiled geometry, brushes and objects.</summary>
public sealed class SpatialLevel
{
    private SpatialLevel(RflFile rfl, IReadOnlyList<LevelObject> objects, Geometry geometry, string? fileName, GeometryFreshness freshness)
    {
        Rfl = rfl;
        Objects = objects;
        Geometry = geometry;
        FileName = fileName;
        Freshness = freshness;
    }

    /// <summary>The level file content (brushes, respawn points, groups).</summary>
    public RflFile Rfl { get; }

    /// <summary>The level's objects.</summary>
    public IReadOnlyList<LevelObject> Objects { get; }

    /// <summary>The compiled geometry measured (the level's own or a fresh build's).</summary>
    public Geometry Geometry { get; }

    /// <summary>The level's file name (gametype prefix), or null.</summary>
    public string? FileName { get; }

    /// <summary>Which build the measurement reflects.</summary>
    public GeometryFreshness Freshness { get; }

    /// <summary>
    /// Pixel width and height of a texture by (lower-case) file name, when known (a mounted install), for texel-density
    /// metrics; null = unknown (those metrics are then null).
    /// </summary>
    public Func<string, (int Width, int Height)?>? TextureSize { get; init; }

    /// <summary>This level with texture pixel sizes from <paramref name="sizes"/> (e.g. <see cref="Assets.TextureSizeLookup.Get"/>).</summary>
    public SpatialLevel WithTextureSize(Func<string, (int Width, int Height)?> sizes) =>
        new(Rfl, Objects, Geometry, FileName, Freshness) { TextureSize = sizes };

    /// <summary>
    /// The level of <paramref name="document"/>, measuring <paramref name="geometry"/> (default: its stored compiled
    /// geometry). Throws <see cref="SpatialException"/> when there is no compiled geometry.
    /// </summary>
    public static SpatialLevel FromDocument(EditorDocument document, Geometry? geometry = null, GeometryFreshness? freshness = null, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Geometry geo = geometry ?? StoredGeometry(document.Rfl)
            ?? throw new SpatialException("no_geometry", "The level has no compiled geometry to measure.", "Build it first (ops.build() in Lua, build in the CLI or MCP), or ask for a fresh build (build = \"preview\" or \"sealed\").");
        return new SpatialLevel(document.Rfl, document.Objects, geo, fileName ?? (document.Path is { Length: > 0 } p ? System.IO.Path.GetFileName(p) : null), freshness ?? GeometryFreshness.StoredFile);
    }

    /// <summary>The level stored in <paramref name="bytes"/> (a .rfl file), measured on its stored compiled geometry.</summary>
    public static SpatialLevel FromBytes(byte[] bytes, string? fileName)
    {
        RflFile rfl = RflFile.Load(bytes);
        rfl.ParseAllKnownSections();
        var doc = new EditorDocument(rfl, null);
        return FromDocument(doc, null, GeometryFreshness.StoredFile, fileName);
    }

    /// <summary>The level's stored compiled geometry, or null when it has none (no faces and no rooms).</summary>
    public static Geometry? StoredGeometry(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        foreach (RflSection section in rfl.Sections)
        {
            if (section.TypeId == (uint)SectionType.StaticGeometry)
            {
                if (section.Content is null)
                {
                    rfl.ParseAllKnownSections();
                }

                Geometry? g = (section.Content as GeometrySection)?.Geometry;
                return g is null || (g.Faces.Count == 0 && g.Rooms.Count == 0) ? null : g;
            }
        }

        return null;
    }

    /// <summary>
    /// A cheap fingerprint of every brush's shape, placement and flags (the same fields a build reads): two equal
    /// fingerprints mean no brush changed in a way a build would see.
    /// </summary>
    public static long BrushFingerprint(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        var h = new HashCode();
        foreach (Brush b in rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().SelectMany(s => s.Brushes))
        {
            h.Add(b.Uid);
            h.Add(b.Flags);
            h.Add(b.Life);
            h.Add(b.Position);
            h.Add(b.Rotation);
            h.Add(b.Geometry.Vertices.Count);
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                h.Add(v);
            }

            h.Add(b.Geometry.Faces.Count);
            foreach (Face f in b.Geometry.Faces)
            {
                h.Add(f.Texture);
                h.Add(f.Flags);
                h.Add(f.Vertices.Count);
            }
        }

        return h.ToHashCode();
    }

    internal IEnumerable<T> Sections<T>() => Rfl.Sections.Select(s => s.Content).OfType<T>();

    internal IReadOnlyList<Brush> Brushes() => Sections<BrushesSection>().SelectMany(b => b.Brushes).ToList();
}

/// <summary>A spatial query could not run (bad input, no geometry, or over its budget); carries a hint to fix it.</summary>
public sealed class SpatialException : Exception
{
    public SpatialException(string code, string message, string? hint = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
    }

    /// <summary>A stable code: <c>no_geometry</c>, <c>no_floor</c>, <c>budget</c>, <c>timeout</c>, <c>usage</c>.</summary>
    public string Code { get; }

    /// <summary>What to do about it.</summary>
    public string? Hint { get; }
}

/// <summary>The gameplay objects the measurements use, read once.</summary>
internal sealed class SpatialObjects
{
    public readonly List<(Vec3d P, string Kind, int Team, int Uid)> Spawns = new();
    public readonly List<(Vec3d P, string Cls, string Cat, int Uid)> Items = new();
    public readonly List<(Vec3d P, string Kind, string Cls)> Dress = new();
    public readonly List<Vec3d> ClutterPoints = new();

    public SpatialObjects(SpatialLevel level)
    {
        foreach (MpRespawnPoint p in level.Sections<MpRespawnPointsSection>().SelectMany(s => s.Points))
        {
            int team = p.RedTeam != 0 && p.BlueTeam == 0 ? 1 : p.BlueTeam != 0 && p.RedTeam == 0 ? 2 : 0;
            Spawns.Add((V(p.Position), "mp", team, p.Uid));
        }

        foreach (LevelObject o in level.Objects)
        {
            Vec3d p = V(o.Position);
            string cls = o.ClassName ?? string.Empty;
            switch (o.Kind)
            {
                case LevelObjectKind.PlayerStart:
                    Spawns.Add((p, "start", 0, o.Uid));
                    break;
                case LevelObjectKind.Item:
                    Items.Add((p, cls, ItemCategory(cls), o.Uid));
                    break;
                case LevelObjectKind.Clutter:
                    Dress.Add((p, "clutter", cls));
                    ClutterPoints.Add(p);
                    break;
                case LevelObjectKind.Decal:
                    Dress.Add((p, "decal", cls));
                    break;
                case LevelObjectKind.MeshObject:
                    Dress.Add((p, "mesh", cls));
                    ClutterPoints.Add(p);
                    break;
                case LevelObjectKind.ParticleEmitter:
                case LevelObjectKind.BoltEmitter:
                    Dress.Add((p, "emitter", cls));
                    break;
            }
        }

        Lights = level.Objects.Count(o => o.Kind == LevelObjectKind.Light);
    }

    public int Lights { get; }

    public int MpSpawnCount => Spawns.Count(s => s.Kind == "mp");

    internal static Vec3d V(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The item category of a class name (flag, powerup, ammo, health, armor, weapon, other), by name.</summary>
    internal static string ItemCategory(string className)
    {
        string l = className.ToLowerInvariant();
        if (l.Contains("flag", StringComparison.Ordinal))
        {
            return "flag";
        }

        if (l.Contains("amp", StringComparison.Ordinal) || l.Contains("invuln", StringComparison.Ordinal) || l.Contains("super armor", StringComparison.Ordinal)
            || l.Contains("super health", StringComparison.Ordinal) || l.Contains("multi ", StringComparison.Ordinal))
        {
            return "powerup";
        }

        if (l.Contains("ammo", StringComparison.Ordinal) || l.Contains("shells", StringComparison.Ordinal) || l.Contains("rounds", StringComparison.Ordinal))
        {
            return "ammo";
        }

        if (l.Contains("medic", StringComparison.Ordinal) || l.Contains("first aid", StringComparison.Ordinal) || l.Contains("health", StringComparison.Ordinal))
        {
            return "health";
        }

        if (l.Contains("armor", StringComparison.Ordinal) || l.Contains("repair", StringComparison.Ordinal) || l.Contains("envirosuit", StringComparison.Ordinal))
        {
            return "armor";
        }

        string[] weapons = { "rifle", "shotgun", "gun", "launcher", "pistol", "smg", "grenade", "flamethrower", "rail", "charge", "shield", "baton", "sniper", "minigun", "machine" };
        return weapons.Any(w => l.Contains(w, StringComparison.Ordinal)) ? "weapon" : "other";
    }

    /// <summary>A clutter class's theme category (containers, fixtures, machinery, …), by name.</summary>
    internal static string ClutterCategory(string className)
    {
        string l = className.ToLowerInvariant();
        bool Any(params string[] w) => w.Any(x => l.Contains(x, StringComparison.Ordinal));
        if (Any("crate", "box", "barrel", "canister", "tank"))
        {
            return "containers";
        }

        if (Any("light", "lamp"))
        {
            return "fixtures";
        }

        if (Any("pipe", "valve", "vent", "fan", "machine", "generator", "console", "computer", "monitor", "panel", "terminal"))
        {
            return "machinery";
        }

        if (Any("switch", "button"))
        {
            return "switches";
        }

        if (Any("sign", "poster", "picture"))
        {
            return "signage";
        }

        if (Any("chair", "table", "desk", "bed", "locker", "shelf", "cabinet"))
        {
            return "furniture";
        }

        return Any("plant", "tree", "rock") ? "nature" : "other";
    }
}
