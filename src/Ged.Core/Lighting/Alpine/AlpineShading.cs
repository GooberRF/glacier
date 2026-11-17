using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Model;

namespace Ged.Core.Lighting.Alpine;

/// <summary>
/// Which compiled faces belong to a brush flagged "No shadow cast", resolved as Alpine RED resolves them
/// (editor_patch/lightmap.cpp:151-236): CSG carries a source brush face's id onto every fragment, and a fragment whose
/// id is one of a flagged brush's face ids and whose bounds reach that brush's bounding sphere is its.
/// </summary>
public sealed class AlpineNoShadowCastFilter
{
    private readonly Dictionary<int, List<(Vec3 Pos, float Radius, int Uid)>> _owners = new();

    /// <summary>The flagged brush uids.</summary>
    public HashSet<int> Uids { get; } = new();

    public bool IsEmpty => Uids.Count == 0;

    /// <summary>The filter for <paramref name="flagged"/> among <paramref name="brushes"/>.</summary>
    public static AlpineNoShadowCastFilter Build(IReadOnlyCollection<int> flagged, IEnumerable<Brush> brushes)
    {
        var f = new AlpineNoShadowCastFilter();
        foreach (int uid in flagged)
        {
            f.Uids.Add(uid);
        }

        if (f.Uids.Count == 0)
        {
            return f;
        }

        foreach (Brush b in brushes)
        {
            if (!f.Uids.Contains(b.Uid))
            {
                continue;
            }

            // furthest local corner, so the bound survives any brush orientation
            float ex = 0, ey = 0, ez = 0;
            foreach (Vec3 v in b.Geometry.Vertices)
            {
                ex = MathF.Max(ex, MathF.Abs(v.X));
                ey = MathF.Max(ey, MathF.Abs(v.Y));
                ez = MathF.Max(ez, MathF.Abs(v.Z));
            }

            float radius = MathF.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
            foreach (Face face in b.Geometry.Faces)
            {
                if (face.FaceId >= 0)
                {
                    (f._owners.TryGetValue(face.FaceId, out var list) ? list : f._owners[face.FaceId] = new()).Add((b.Position, radius, b.Uid));
                }
            }
        }

        return f;
    }

    /// <summary>The flagged brush a compiled face with <paramref name="faceId"/> and bounds belongs to, or −1.</summary>
    public int Owner(int faceId, Vec3 lo, Vec3 hi)
    {
        if (!_owners.TryGetValue(faceId, out var list))
        {
            return -1;
        }

        foreach ((Vec3 c, float radius, int uid) in list)
        {
            float r = radius + 0.05f;
            if (lo.X <= c.X + r && hi.X >= c.X - r && lo.Y <= c.Y + r && hi.Y >= c.Y - r && lo.Z <= c.Z + r && hi.Z >= c.Z - r)
            {
                return uid;
            }
        }

        return -1;
    }
}

/// <summary>
/// The Alpine fixed pipeline's shading state for one bake: its occluder tree (null: nothing occludes), the level's rules,
/// and the liquid emitters (editor_patch/lightmap.cpp:1100-1257, 1876-1918, 2152-2225).
/// </summary>
public sealed class AlpineShading
{
    public AlpineShading(AlpineOccluders? tree, AlpineLightmapSettings settings, IReadOnlyList<AlpineLiquidEmitter> liquid)
    {
        Tree = tree;
        Settings = settings;
        Liquid = liquid;
    }

    public AlpineOccluders? Tree { get; }

    public AlpineLightmapSettings Settings { get; }

    public IReadOnlyList<AlpineLiquidEmitter> Liquid { get; }

    /// <summary>Lines for the bake log.</summary>
    public List<string> Log { get; } = new();

    /// <summary>
    /// The level's liquid emitters: every liquid room whose room effect emits (mode not Off, positive intensity and
    /// distance, a colour) and has a horizontal liquid surface, one per room (lightmap.cpp:2162-2225). A compiled room
    /// carries its room effect's uid as its id.
    /// </summary>
    public static List<AlpineLiquidEmitter> LiquidEmitters(
        IReadOnlyList<AlpineLiquidEmission> records, IReadOnlyList<Room> rooms,
        IEnumerable<(int Room, IReadOnlyList<Vec3> Polygon, Vec3 Normal)> liquidFaces, List<string>? log = null)
    {
        var outp = new List<AlpineLiquidEmitter>();
        if (records.Count == 0)
        {
            return outp;
        }

        var byUid = new Dictionary<int, AlpineLiquidEmission>();
        foreach (AlpineLiquidEmission e in records)
        {
            byUid[e.RoomEffectUid] = e;
        }

        var faces = liquidFaces.GroupBy(f => f.Room).ToDictionary(g => g.Key, g => g.Select(f => (f.Polygon, f.Normal)).ToList());
        var lit = new HashSet<int>();
        for (int r = 0; r < rooms.Count; r++)
        {
            Room room = rooms[r];
            if (room.IsLiquidRoom == 0 || !byUid.TryGetValue(room.Id, out AlpineLiquidEmission? em))
            {
                continue;
            }

            if (em.Mode == 0 || !(em.Intensity > 0f) || !(em.Distance > 0f) || (em.ColorR | em.ColorG | em.ColorB) == 0)
            {
                continue;
            }

            if (!lit.Add(room.Id))
            {
                log?.Add($"Liquid Room {room.Id} emits light but another room already uses it, so it is left out.");
                continue;
            }

            if (!faces.TryGetValue(r, out var list) || AlpineLiquidSurface.Build(list) is not { } surface)
            {
                log?.Add($"Liquid Room {room.Id} emits light but its room has no liquid surface.");
                continue;
            }

            float intensity = MathF.Min(em.Intensity, 100f);
            outp.Add(new AlpineLiquidEmitter
            {
                Surface = surface,
                Color = new Vec3(em.ColorR / 255f * intensity, em.ColorG / 255f * intensity, em.ColorB / 255f * intensity),
                Reach = MathF.Min(em.Distance, 1000f),
                Below = em.Mode == 2,
                EffectUid = room.Id,
            });
            log?.Add($"Liquid Room {room.Id} emits light from {surface.PolygonCount} liquid face(s) at height {surface.Height:0.###}.");
        }

        foreach (AlpineLiquidEmission e in records)
        {
            if (e.Mode != 0 && !lit.Contains(e.RoomEffectUid))
            {
                bool inLiquidRoom = rooms.Any(r => r.Id == e.RoomEffectUid && r.IsLiquidRoom != 0);
                if (!inLiquidRoom)
                {
                    log?.Add($"Liquid Room {e.RoomEffectUid} emits light but is not in a liquid room; run Build Geometry.");
                }
            }
        }

        return outp;
    }

    /// <summary>The faces of an already-compiled level, as occluder input (a relight).</summary>
    public static IEnumerable<AlpineOccluderFace> FacesOf(Geometry g, AlpineNoShadowCastFilter? nsc)
    {
        foreach (Face f in g.Faces)
        {
            if (f.Vertices.Count < 3)
            {
                continue;
            }

            var poly = new Vec3[f.Vertices.Count];
            var uvs = new Uv[f.Vertices.Count];
            Vec3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue, float.MinValue);
            bool ok = true;
            for (int i = 0; i < poly.Length; i++)
            {
                int vi = f.Vertices[i].Index;
                if ((uint)vi >= (uint)g.Vertices.Count)
                {
                    ok = false;
                    break;
                }

                poly[i] = g.Vertices[vi];
                uvs[i] = f.Vertices[i].TextureCoords;
                lo = Vec3Math.Min(lo, poly[i]);
                hi = Vec3Math.Max(hi, poly[i]);
            }

            if (!ok)
            {
                continue;
            }

            string tex = f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty;
            int surf = f.SurfaceIndex >= 0 && (f.SurfaceIndex & 0xFFFF) != 0xFFFF ? f.SurfaceIndex : -1;
            yield return new AlpineOccluderFace(poly, uvs, f.Flags, tex, surf, f.Plane.Normal, f.IsPortalFace,
                nsc is null || nsc.IsEmpty ? -1 : nsc.Owner(f.FaceId, lo, hi));
        }
    }
}
