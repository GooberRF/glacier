using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Compiler;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Opt-in measurement (GED_ROOM_DIAG=1) of room and portal structure against the Alpine RED reference builds
/// (and stock RED where the level is in the stock corpus). Per level: main rooms / portals; how the reference
/// portal faces relate to the portal brushes (on a brush face plane, inside the brush, or neither); the main
/// rooms without a portal, and for each such Glacier room the reference rooms that hold the same faces; subroom
/// sizes; and, per portal brush, the portal faces on each brush face with their rooms and the room pairs joined.
/// GED_ROOM_DIAG_LEVELS narrows the set (comma list); GED_ROOM_DIAG_NOCOMPILE=1 reads the references only;
/// GED_ROOM_DIAG_NOCHOP=1 / GED_ROOM_DIAG_NOSHELL=1 build without the portal chop / the inside-out room rule.
/// Writes <c>tests/artifacts/room_portal_diag.txt</c> (compiler-parity-notes.md "Air portal brushes and
/// inside-out rooms").
/// </summary>
public sealed class RoomPortalParityDiag
{
    private readonly ITestOutputHelper _out;

    public RoomPortalParityDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("GED_ROOM_DIAG") != "1" || AlpineReference.Find() is not { } refs)
        {
            return;
        }

        HashSet<string>? only = Environment.GetEnvironmentVariable("GED_ROOM_DIAG_LEVELS") is { Length: > 0 } l
            ? new HashSet<string>(l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            : null;
        var sb = new StringBuilder();
        foreach (AlpineReference.Level level in refs.Levels)
        {
            if (only is not null && !only.Contains(level.Name) && !only.Contains(Path.GetFileNameWithoutExtension(level.Name)))
            {
                continue;
            }

            if (!AlpineReference.TryLoad(level, out AlpineReference.Loaded? loaded) || loaded is null)
            {
                continue;
            }

            bool noCompile = Environment.GetEnvironmentVariable("GED_ROOM_DIAG_NOCOMPILE") == "1";
            Geometry? glacier = noCompile ? null : GeometryCompiler.Compile(loaded.Brushes, loaded.Effects, new CompileOptions { BuildSurfaces = false, PortalChop = Environment.GetEnvironmentVariable("GED_ROOM_DIAG_NOCHOP") != "1", InsideOutRooms = Environment.GetEnvironmentVariable("GED_ROOM_DIAG_NOSHELL") != "1" }).Geometry;
            sb.AppendLine($"== {level.Name}: main rooms/portals stock {(loaded.Stock is null ? "-" : Rp(loaded.Stock))} alpine {Rp(loaded.Reference)} glacier {(glacier is null ? "-" : Rp(glacier))}");
            var sets = new List<(string Tag, Geometry G)> { ("alpine", loaded.Reference) };
            if (glacier is not null)
            {
                sets.Add(("glacier", glacier));
            }

            Summary(sb, loaded.Reference, loaded.Brushes);
            if (glacier is not null)
            {
                foreach ((string tag, Geometry g) in new[] { ("alpine", loaded.Reference), ("glacier", glacier) })
                {
                    var perRoom = new int[g.Rooms.Count];
                    foreach (Face f in g.Faces)
                    {
                        if (f.RoomIndex >= 0 && f.RoomIndex < perRoom.Length)
                        {
                            perRoom[f.RoomIndex]++;
                        }
                    }

                    var linked = new HashSet<int>();
                    foreach (Portal pt in g.Portals)
                    {
                        linked.Add(pt.RoomIndex1);
                        linked.Add(pt.RoomIndex2);
                    }

                    var lone = new List<string>();
                    for (int r = 0; r < g.Rooms.Count; r++)
                    {
                        if (g.Rooms[r].IsSubroom == 0 && !linked.Contains(r))
                        {
                            Vec3 d = g.Rooms[r].Aabb.P2.Sub(g.Rooms[r].Aabb.P1);
                            Vec3 c = g.Rooms[r].Aabb.P1;
                            float area = 0f;
                            foreach (Face f in g.Faces)
                            {
                                if (f.RoomIndex == r)
                                {
                                    area += Area(g, f);
                                }
                            }

                            lone.Add($"#{r}:{perRoom[r]}f/{area:0.##}m2@({c.X:0.#},{c.Y:0.#},{c.Z:0.#})+({d.X:0.##},{d.Y:0.##},{d.Z:0.##})");
                        }
                    }

                    sb.AppendLine($"   {tag}: main rooms without a portal {lone.Count}: {string.Join(" ", lone.Take(40))}");
                    if (tag == "glacier")
                    {
                        // Where do the reference faces at each lone Glacier room live?
                        Geometry refG = loaded.Reference;
                        var refPerRoom = new int[refG.Rooms.Count];
                        foreach (Face f in refG.Faces)
                        {
                            if (f.RoomIndex >= 0 && f.RoomIndex < refPerRoom.Length)
                            {
                                refPerRoom[f.RoomIndex]++;
                            }
                        }

                        for (int r = 0; r < g.Rooms.Count; r++)
                        {
                            if (g.Rooms[r].IsSubroom != 0 || linked.Contains(r))
                            {
                                continue;
                            }

                            Aabb bb = g.Rooms[r].Aabb;
                            var hits = new SortedDictionary<int, int>();
                            foreach (Face f in refG.Faces)
                            {
                                bool inside = true;
                                foreach (FaceVertex fv in f.Vertices)
                                {
                                    Vec3 q = refG.Vertices[fv.Index];
                                    inside &= q.X >= bb.P1.X - 0.01f && q.X <= bb.P2.X + 0.01f && q.Y >= bb.P1.Y - 0.01f && q.Y <= bb.P2.Y + 0.01f && q.Z >= bb.P1.Z - 0.01f && q.Z <= bb.P2.Z + 0.01f;
                                }

                                if (inside)
                                {
                                    hits[f.RoomIndex] = hits.GetValueOrDefault(f.RoomIndex) + 1;
                                }
                            }

                            sb.AppendLine($"      glacier lone #{r} ({perRoom[r]}f): reference faces inside its box by room: {string.Join(" ", hits.Select(h => $"r{h.Key}{(refG.Rooms[h.Key].IsSubroom != 0 ? "(sub)" : "(main)")}x{h.Value}/of{refPerRoom[h.Key]}"))}");
                        }
                    }
                    var sub = Enumerable.Range(0, g.Rooms.Count).Where(r => g.Rooms[r].IsSubroom != 0).Select(r => perRoom[r]).OrderBy(x => x).ToList();
                    int detailFaces = g.Faces.Count(f => (f.Flags & (ushort)FaceFlags.IsDetail) != 0);
                    sb.AppendLine($"   {tag}: detail faces {detailFaces}, subrooms {sub.Count}, faces/subroom min {(sub.Count > 0 ? sub[0] : 0)} median {(sub.Count > 0 ? sub[sub.Count / 2] : 0)} max {(sub.Count > 0 ? sub[^1] : 0)}, subrooms with <=2 faces {sub.Count(x => x <= 2)}");
                }
            }
            if (loaded.Stock is not null)
            {
                sets.Insert(0, ("stock", loaded.Stock));
            }

            foreach ((string tag, Geometry g) in sets)
            {
                int pf = g.Faces.Count(f => f.Texture < 0 || f.PortalIndexPlus2 >= 2);
                int tagged = g.Faces.Count(f => f.PortalIndexPlus2 >= 2);
                int untex = g.Faces.Count(f => f.Texture < 0);
                sb.AppendLine($"   {tag}: portal faces {pf} (tagged {tagged}, tex<0 {untex}); rooms sky {g.Rooms.Count(r => r.IsSkyroom != 0)} liquid {g.Rooms.Count(r => r.IsLiquidRoom != 0)} sub {g.Rooms.Count(r => r.IsSubroom != 0)}");
            }

            int n = 0;
            foreach (Brush b in loaded.Brushes)
            {
                if ((b.Flags & (uint)BrushFlags.Portal) == 0)
                {
                    continue;
                }

                n++;
                List<CsgFace> faces = BrushWorld.ToWorldFaces(b, 0, out _);
                Vec3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue), mx = new(float.MinValue, float.MinValue, float.MinValue);
                foreach (CsgFace f in faces)
                {
                    f.GrowAabb(ref mn, ref mx);
                }

                Vec3 size = mx.Sub(mn);
                var line = new StringBuilder();
                line.Append($"  portal uid {b.Uid} flags 0x{b.Flags:X} faces {faces.Count} min ({mn.X:0.##},{mn.Y:0.##},{mn.Z:0.##}) size ({size.X:0.##},{size.Y:0.##},{size.Z:0.##}) areas [{string.Join(",", faces.Select(f => f.Area().ToString("0.#")))}]");
                foreach ((string tag, Geometry g) in sets)
                {
                    line.Append($" | {tag}: {Attribute(g, faces, mn, mx)}");
                }

                sb.AppendLine(line.ToString());
            }

            sb.AppendLine($"   portal brushes {n}");
        }

        string path = Path.Combine(TestPaths.RepoRoot!, "tests", "artifacts", "room_portal_diag.txt");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
    }

    /// <summary>How the reference portal faces relate to the portal brushes: on a brush face plane, inside a brush, or neither.</summary>
    private static void Summary(StringBuilder sb, Geometry g, List<Brush> brushes)
    {
        const float Eps = 0.02f;
        var boxes = new List<(Brush B, List<CsgFace> Faces, Vec3 Mn, Vec3 Mx)>();
        foreach (Brush b in brushes)
        {
            if ((b.Flags & (uint)BrushFlags.Portal) == 0)
            {
                continue;
            }

            List<CsgFace> faces = BrushWorld.ToWorldFaces(b, 0, out _);
            Vec3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue), mx = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (CsgFace f in faces)
            {
                f.GrowAabb(ref mn, ref mx);
            }

            boxes.Add((b, faces, mn, mx));
        }

        int airOn = 0, airOff = 0, flatOn = 0, flatOff = 0, none = 0, total = 0;
        foreach (Face f in g.Faces)
        {
            if (f.Texture >= 0 && f.PortalIndexPlus2 < 2)
            {
                continue;
            }

            total++;
            int best = 0; // 0 none, 1 off, 2 on
            bool air = false;
            foreach ((Brush b, List<CsgFace> faces, Vec3 mn, Vec3 mx) in boxes)
            {
                bool inBox = true;
                foreach (FaceVertex fv in f.Vertices)
                {
                    Vec3 p = g.Vertices[fv.Index];
                    inBox &= p.X >= mn.X - Eps && p.X <= mx.X + Eps && p.Y >= mn.Y - Eps && p.Y <= mx.Y + Eps && p.Z >= mn.Z - Eps && p.Z <= mx.Z + Eps;
                }

                if (!inBox)
                {
                    continue;
                }

                bool on = false;
                foreach (CsgFace bf in faces)
                {
                    bool all = true;
                    foreach (FaceVertex fv in f.Vertices)
                    {
                        all &= MathF.Abs(bf.Plane.Distance(g.Vertices[fv.Index])) <= Eps;
                    }

                    on |= all;
                }

                int score = on ? 2 : 1;
                if (score > best)
                {
                    best = score;
                    air = (b.Flags & (uint)BrushFlags.Air) != 0;
                }
            }

            if (best == 0)
            {
                none++;
            }
            else if (air)
            {
                if (best == 2) { airOn++; } else { airOff++; }
            }
            else
            {
                if (best == 2) { flatOn++; } else { flatOff++; }
            }
        }

        int nAir = boxes.Count(x => (x.B.Flags & (uint)BrushFlags.Air) != 0);
        sb.AppendLine($"   SUMMARY portal brushes air {nAir} solid {boxes.Count - nAir}; ref portal faces {total}: air-brush on-face {airOn} inside {airOff}; solid-brush on-face {flatOn} inside {flatOff}; unattributed {none}");
    }

    private static string Rp(Geometry g) => $"{AlpineReference.MainRooms(g)}/{g.Portals.Count}";

    /// <summary>Portal faces of <paramref name="g"/> lying on each brush face plane (inside the brush box) or elsewhere inside it.</summary>
    private static string Attribute(Geometry g, List<CsgFace> brushFaces, Vec3 mn, Vec3 mx)
    {
        const float Eps = 0.02f;
        int[] onFace = new int[brushFaces.Count];
        float[] areaOn = new float[brushFaces.Count];
        int inside = 0;
        var pairs = new SortedSet<(int, int)>();
        var roomsByFace = new SortedSet<int>[brushFaces.Count];
        foreach (Face f in g.Faces)
        {
            if (f.Texture >= 0 && f.PortalIndexPlus2 < 2)
            {
                continue;
            }

            bool inBox = true;
            foreach (FaceVertex fv in f.Vertices)
            {
                Vec3 p = g.Vertices[fv.Index];
                inBox &= p.X >= mn.X - Eps && p.X <= mx.X + Eps && p.Y >= mn.Y - Eps && p.Y <= mx.Y + Eps && p.Z >= mn.Z - Eps && p.Z <= mx.Z + Eps;
            }

            if (!inBox)
            {
                continue;
            }

            int hit = -1;
            for (int i = 0; i < brushFaces.Count && hit < 0; i++)
            {
                bool all = true;
                foreach (FaceVertex fv in f.Vertices)
                {
                    all &= MathF.Abs(brushFaces[i].Plane.Distance(g.Vertices[fv.Index])) <= Eps;
                }

                if (all)
                {
                    hit = i;
                }
            }

            if (hit >= 0)
            {
                onFace[hit]++;
                areaOn[hit] += Area(g, f);
                (roomsByFace[hit] ??= new SortedSet<int>()).Add(f.RoomIndex);
            }
            else
            {
                inside++;
            }

            if (f.PortalIndexPlus2 >= 2 && f.PortalIndexPlus2 - 2 < g.Portals.Count)
            {
                Portal p = g.Portals[f.PortalIndexPlus2 - 2];
                pairs.Add((Math.Min(p.RoomIndex1, p.RoomIndex2), Math.Max(p.RoomIndex1, p.RoomIndex2)));
            }
        }

        var sb = new StringBuilder();
        sb.Append("onFace[");
        for (int i = 0; i < onFace.Length; i++)
        {
            if (onFace[i] > 0)
            {
                sb.Append($"{i}:{onFace[i]}/{areaOn[i]:0.#}/r{string.Join("+", roomsByFace[i]!)} ");
            }
        }

        sb.Append($"] off {inside} pairs {string.Join(",", pairs.Select(p => $"{p.Item1}-{p.Item2}"))}");
        return sb.ToString();
    }

    private static float Area(Geometry g, Face f)
    {
        Vec3 sum = default;
        Vec3 a = g.Vertices[f.Vertices[0].Index];
        for (int i = 1; i + 1 < f.Vertices.Count; i++)
        {
            sum = sum.Add(g.Vertices[f.Vertices[i].Index].Sub(a).Cross(g.Vertices[f.Vertices[i + 1].Index].Sub(a)));
        }

        return sum.Length() * 0.5f;
    }
}
