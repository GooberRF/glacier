using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Spatial;

/// <summary>
/// The movement links the walk graph adds besides walking (DESIGN-QUALITY-SPEC §(e) E1), where the level data
/// determines them:
/// <list type="bullet">
/// <item><b>Lifts</b>: a moving group (not a door, not rotate-in-place) whose keyframes rise or travel at least 1.5 m
/// carries a player standing on its brushes between its stops: consecutive keyframe stops are joined both ways, at
/// the top of the group's brushes shifted by each keyframe's offset from the first.</item>
/// <item><b>Jump pads</b>: a push region with the jump-pad flag (rfl.ksy <c>push_region_flags</c> 0x40) that affects
/// players launches along its forward axis. The landing point follows Alpine Faction's own jump-pad trajectory model
/// (reimplemented from alpinefaction game_patch/misc/waypoints.cpp:7510-7600 and waypoints.h:57-66): launch speed =
/// strength, × 0.6 unless the region is mass-independent (flag 0x01); 0.05 s steps for at most 10 s; the first face
/// with normal.y ≥ 0.5 is the landing; a ceiling hit keeps the horizontal speed and drops the upward speed; a wall
/// hit drops the player straight down. Gravity is the game's default 9.8 m/s² (research/rf_decomp
/// globals/physics.md). Like Alpine, a landing less than 2 m above the pad is no link. Alpine's air-control fan
/// around the apex is not modelled.</item>
/// <item><b>Teleports</b>: a trigger linked to a <c>Teleport_Player</c> or <c>AF_Teleport_Player</c> event moves the
/// player from the trigger to the event (one way).</item>
/// </list>
/// </summary>
internal static class MovementLinks
{
    /// <summary>Gravity for the jump-pad arc (m/s²; the game's default, research/rf_decomp globals/physics.md).</summary>
    private const double Gravity = 9.8;

    /// <summary>Jump-pad trajectory step and limit (s), and the landing floor normal (Alpine waypoints.h:57-66).</summary>
    private const double ArcStep = 0.05, ArcMaxTime = 10.0, LandingNormalY = 0.5;

    /// <summary>The lowest landing above the pad Alpine links a jump pad to (waypoints.h, kJumpPadMinHeightAboveStart).</summary>
    private const double MinHeightAboveStart = 2.0;

    /// <summary>Launch-speed factor of a region that is not mass-independent (Alpine waypoints.cpp, simulate_jump_pad_trajectory).</summary>
    private const double MassDependentFactor = 0.6;

    /// <summary>Push-region flag bits (rfl.ksy push_region_flags).</summary>
    private const ushort JumpPadFlag = 0x40, DoesntAffectPlayerFlag = 0x20, MassIndependentFlag = 0x01;

    public static List<MovementLink> Find(SpatialLevel level, SpaceSampler sampler)
    {
        var links = new List<MovementLink>();
        Lifts(level, links);
        JumpPads(level, sampler, links);
        Teleports(level, links);
        return links;
    }

    private static void Lifts(SpatialLevel level, List<MovementLink> links)
    {
        Dictionary<int, Brush> brushes = new();
        foreach (Brush b in level.Brushes())
        {
            brushes.TryAdd(b.Uid, b);
        }

        foreach (GroupsSection gs in level.Sections<GroupsSection>())
        {
            foreach (Group g in gs.Groups)
            {
                if (g.IsMoving == 0 || g.MovingData is not { } md || md.IsDoor != 0 || md.RotateInPlace != 0 || md.Keyframes.Count < 2)
                {
                    continue;
                }

                List<Brush> members = g.Brushes.Select(u => brushes.GetValueOrDefault(u)).Where(b => b is not null && b.Geometry.Vertices.Count > 0).Select(b => b!).ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue, top = double.MinValue;
                foreach (Brush b in members)
                {
                    foreach (Vec3 v in b.Geometry.Vertices)
                    {
                        Vec3d w = SpatialObjects.V(b.Position).Add(SpatialObjects.V(b.Rotation.Right).Scale(v.X))
                            .Add(SpatialObjects.V(b.Rotation.Up).Scale(v.Y)).Add(SpatialObjects.V(b.Rotation.Forward).Scale(v.Z));
                        x0 = Math.Min(x0, w.X);
                        x1 = Math.Max(x1, w.X);
                        z0 = Math.Min(z0, w.Z);
                        z1 = Math.Max(z1, w.Z);
                        top = Math.Max(top, w.Y);
                    }
                }

                var platform = new Vec3d((x0 + x1) / 2, top + 0.05, (z0 + z1) / 2);
                Vec3d k0 = SpatialObjects.V(md.Keyframes[0].Position);
                for (int i = 0; i + 1 < md.Keyframes.Count; i++)
                {
                    Vec3d a = platform.Add(SpatialObjects.V(md.Keyframes[i].Position).Sub(k0));
                    Vec3d b = platform.Add(SpatialObjects.V(md.Keyframes[i + 1].Position).Sub(k0));
                    Vec3d d = b.Sub(a);
                    if (Math.Abs(d.Y) < 1.5 && Math.Sqrt((d.X * d.X) + (d.Z * d.Z)) < 1.5)
                    {
                        continue;
                    }

                    links.Add(new MovementLink("lift", a, b, OneWay: false, md.Keyframes[0].Uid));
                }
            }
        }
    }

    private static void JumpPads(SpatialLevel level, SpaceSampler sampler, List<MovementLink> links)
    {
        foreach (LevelObject o in level.Objects)
        {
            if (o.Kind != LevelObjectKind.PushRegion || o.Model is not PushRegion pr
                || (pr.Flags & JumpPadFlag) == 0 || (pr.Flags & DoesntAffectPlayerFlag) != 0 || !(pr.Strength > 0))
            {
                continue;
            }

            Vec3 f = pr.Header.Rotation.Forward;
            var dir = new Vec3d(f.X, f.Y, f.Z);
            if (dir.Length < 1e-6)
            {
                continue;
            }

            dir = dir.Normalized();
            Vec3d start = SpatialObjects.V(pr.Header.Position);
            double speed = pr.Strength * ((pr.Flags & MassIndependentFlag) != 0 ? 1.0 : MassDependentFactor);
            // Alpine links a jump pad only to destinations at least 2 m above it (waypoints.cpp:7720,
            // kJumpPadMinHeightAboveStart): a pad is for reaching higher ground, never a fall.
            if (Arc(sampler, start, dir.Scale(speed)) is { } landing && landing.Y >= start.Y + MinHeightAboveStart)
            {
                links.Add(new MovementLink("jump_pad", start, landing, OneWay: true, pr.Header.Uid));
            }
        }
    }

    /// <summary>Traces the launch arc; the point where it first meets a floor (normal.y ≥ 0.5), or null.</summary>
    private static Vec3d? Arc(SpaceSampler sampler, Vec3d start, Vec3d v0)
    {
        Vec3d p = start;
        Vec3d v = v0;
        for (double t = 0; t < ArcMaxTime; t += ArcStep)
        {
            Vec3d next = new(p.X + (v.X * ArcStep), p.Y + (v.Y * ArcStep) - (0.5 * Gravity * ArcStep * ArcStep), p.Z + (v.Z * ArcStep));
            v = new Vec3d(v.X, v.Y - (Gravity * ArcStep), v.Z);
            Vec3d seg = next.Sub(p);
            double len = seg.Length;
            if (len > 1e-9 && sampler.Cast(p, seg.Scale(1 / len), len, SpaceSampler.HitFilter.Any, out int face) is double hitT && face >= 0)
            {
                Vec3d hit = p.Add(seg.Scale(hitT / len));
                double ny = sampler.Faces[face].N.Y;
                if (ny >= LandingNormalY)
                {
                    return hit;
                }

                Vec3d back = hit.Sub(seg.Scale(0.05 / len));
                if (ny < -LandingNormalY)
                {
                    // Ceiling: the upward speed is lost, the horizontal speed kept.
                    p = back;
                    v = new Vec3d(v.X, Math.Min(0, v.Y), v.Z);
                    continue;
                }

                // Wall: the player drops to the floor below.
                double? down = sampler.Cast(back, new Vec3d(0, -1, 0), SpaceSampler.SkyCap, SpaceSampler.HitFilter.UpFacing);
                return down is double dd ? back.Add(new Vec3d(0, -dd, 0)) : null;
            }

            p = next;
        }

        return null;
    }

    private static void Teleports(SpatialLevel level, List<MovementLink> links)
    {
        Dictionary<int, LevelObject> byUid = new();
        foreach (LevelObject o in level.Objects)
        {
            byUid.TryAdd(o.Uid, o);
        }

        foreach (LevelObject o in level.Objects)
        {
            if (o.Kind != LevelObjectKind.Trigger || o.Model is not Trigger t)
            {
                continue;
            }

            foreach (int uid in t.Links)
            {
                if (byUid.GetValueOrDefault(uid) is { Kind: LevelObjectKind.Event } ev
                    && (ev.ClassName is "Teleport_Player" or "AF_Teleport_Player"))
                {
                    links.Add(new MovementLink("teleport", SpatialObjects.V(t.Position), SpatialObjects.V(ev.Position), OneWay: true, t.Uid));
                }
            }
        }
    }
}
