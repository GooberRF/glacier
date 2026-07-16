using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Inserts water-surface polygons into each liquid room (RED's mode-6 liquid
/// surface generator). The surface starts as a quad spanning the room's AABB at
/// the liquid depth but is then CSG-clipped to the room's open cross-section:
/// fragments are kept only where both sides of the surface lie in open space
/// (so it never pokes through terrain) and where the fragment locates to the
/// owning room (so overlapping room AABBs don't leak liquid into neighbours).
/// Fragments are flagged <see cref="FaceFlags.LiquidSurface"/> and textured with
/// the room's liquid surface texture.
/// </summary>
public static class LiquidSurfaceBuilder
{
    public static void Insert(
        List<CsgFace> faces,
        List<int[]> facePoolIndices,
        VertexWelder welder,
        RoomBuildResult rooms,
        CsgSolver solver)
    {
        for (int r = 0; r < rooms.Rooms.Count; r++)
        {
            Room room = rooms.Rooms[r];
            if (room.IsLiquidRoom == 0 || room.LiquidProperties is not RoomLiquidProperties lp)
            {
                continue;
            }

            Aabb bb = room.Aabb;
            float level = bb.P1.Y + lp.Depth;
            if (level > bb.P2.Y)
            {
                level = bb.P2.Y;
            }

            const float Inset = 0.01f;
            float x0 = bb.P1.X + Inset, x1 = bb.P2.X - Inset;
            float z0 = bb.P1.Z + Inset, z1 = bb.P2.Z - Inset;
            if (x1 <= x0 || z1 <= z0)
            {
                continue;
            }

            var verts = new List<CsgVertex>
            {
                new(new Vec3(x0, level, z0), new Uv(x0, z0)),
                new(new Vec3(x1, level, z0), new Uv(x1, z0)),
                new(new Vec3(x1, level, z1), new Uv(x1, z1)),
                new(new Vec3(x0, level, z1), new Uv(x0, z1)),
            };

            CsgPlane plane = CsgPlane.FromPolygon(verts);
            if (plane.Normal.Y < 0f)
            {
                verts.Reverse(); // face up, into the open air above the liquid
                plane = CsgPlane.FromPolygon(verts);
            }

            var quad = new CsgFace
            {
                Vertices = verts,
                Plane = plane,
                Texture = lp.SurfaceTexture,
                Flags = (ushort)FaceFlags.LiquidSurface,
                RoomIndex = r,
                SourceBrushUid = room.Id,
            };

            // Clip to the open cross-section. ClipToOpen already keeps only fragments bounded by
            // this room's solid geometry, so the surface never pokes through terrain. The only
            // additional filter is to not draw OVER a DIFFERENT liquid room's open volume (which
            // that room draws itself) — a targeted guard on overlapping liquid-room AABBs. The
            // former per-fragment "locate == this room" filter over-dropped legitimate surface
            // (dmabrupt: its vertical-ray locator hit mid-pool structures whose room != this one,
            // so 212 m² of real water collapsed to 66 m² — 16% of RED's; measured corpus-wide the
            // liquid-room guard alone reproduces RED's single-side area to ~100% with no leak).
            var kept = new List<CsgFace>();
            foreach (CsgFace frag in solver.ClipToOpen(quad))
            {
                if (rooms.Locator is { } locator)
                {
                    int loc = locator.Locate(frag.Centroid());
                    if (loc >= 0 && loc != r && loc < rooms.Rooms.Count && rooms.Rooms[loc].IsLiquidRoom != 0)
                    {
                        continue; // belongs to a different liquid room, which draws its own surface
                    }
                }

                kept.Add(frag);
            }

            // The clip routes the surface quad through the world partition, so it comes back as
            // hundreds of coplanar convex slivers (dmabrupt: 693 per side for the same 424 m² RED
            // covers with 59). All slivers share one plane / texture / flag / room, so merge them
            // back into maximal convex faces — RED's compiled liquid surface is a handful of large
            // quads, not a fine grid, and each extra sliver is a wasted face + render primitive that
            // inflates the liquid room's per-room face count far past RED's. HoleDetector excludes
            // LiquidSurface faces, so this cannot open a seam.
            foreach (CsgFace frag in CoplanarMerger.MergeRobust(kept))
            {
                // The robust union keeps every collinear boundary vertex (a merged piece can reach 100+
                // verts). A neighbour's corner only needs to stay for a solid face's watertight seam;
                // the liquid surface is a hole-excluded, self-contained double-sided sub-manifold, so
                // collinear corners are pure bloat (and risk RF's per-face vertex cap). Strip them so
                // each piece is the small convex quad/polygon RED emits (4–15 verts).
                StripCollinear(frag.Vertices);
                if (frag.Vertices.Count < 3)
                {
                    continue;
                }

                AddSurfaceFace(faces, facePoolIndices, welder, frag);

                // RED emits the liquid surface as a self-contained DOUBLE-SIDED sub-manifold: a
                // front face (into the air above) AND a flipped back face at the same plane, so the
                // surface renders from above (looking down at the water) AND from below (swimming,
                // looking up). GED previously emitted only the up-facing side, which back-face-culls
                // from underneath — the reported "water surface not rendering properly". HoleDetector
                // already excludes LiquidSurface faces, so the paired back face adds no open edges.
                var back = frag.With(new List<CsgVertex>(frag.Vertices)); // clones Flags/Texture/Room/Uid
                back.Flip();
                AddSurfaceFace(faces, facePoolIndices, welder, back);
            }
        }
    }

    /// <summary>Removes vertices that are collinear with their neighbours (within 0.5 mm), in place.</summary>
    private static void StripCollinear(List<CsgVertex> v)
    {
        const float Perp = 5e-4f;
        bool changed = true;
        while (changed && v.Count > 3)
        {
            changed = false;
            for (int i = 0; i < v.Count; i++)
            {
                Vec3 prev = v[(i + v.Count - 1) % v.Count].Position;
                Vec3 cur = v[i].Position;
                Vec3 next = v[(i + 1) % v.Count].Position;
                Vec3 e = next.Sub(prev);
                float len2 = e.LengthSquared();
                if (len2 < 1e-12f)
                {
                    continue;
                }

                float t = cur.Sub(prev).Dot(e) / len2;
                Vec3 proj = prev.Add(e.Scale(t));
                if (proj.Sub(cur).LengthSquared() <= Perp * Perp)
                {
                    v.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }
    }

    private static void AddSurfaceFace(List<CsgFace> faces, List<int[]> facePoolIndices, VertexWelder welder, CsgFace frag)
    {
        var idx = new int[frag.Vertices.Count];
        for (int i = 0; i < frag.Vertices.Count; i++)
        {
            idx[i] = welder.Add(frag.Vertices[i].Position);
        }

        faces.Add(frag);
        facePoolIndices.Add(idx);
    }
}
