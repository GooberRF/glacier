using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Rendering.Scene;

/// <summary>
/// Viewport glyphs for the Alpine Faction 1.5 objects, drawn the way Alpine RED draws them (sizes and colours from
/// its render passes, cited per method) so a mapper sees the same thing in both editors:
/// <list type="bullet">
/// <item>weather region: its box (centred, width / height / depth along right / up / forward) or sphere;</item>
/// <item>vehicle factory: the spawn-facing arrow, and a turret's two yaw-limit stops;</item>
/// <item>projection camera: the facing arrow, plus the view frustum of the Display_Projection event that uses it;</item>
/// <item>rope emitter: the resting rope (catenary to its target, or a dangle);</item>
/// <item>directional light: the travel arrow and its bounding box / sphere / cylinder;</item>
/// <item>waypoint helper: its radius sphere, centre cross and landing arrow;</item>
/// <item>mover paths: curved segments and a loop's closing segment.</item>
/// </list>
/// Orientation conventions follow the RFL model: travel / spawn / view directions are the orientation's
/// <see cref="Mat3.Forward"/> row. Pure line geometry — no GPU dependency — shared by the scene builder (always-on
/// parts and "always show range" volumes) and the App's selection overlay (volumes of selected objects).
/// </summary>
public static class AlpineObjectGizmos
{
    /// <summary>Alpine RED's selected-object colour for these glyphs (red, e.g. dir_light.cpp:655).</summary>
    public static readonly uint SelectedColor = Palette.Rgba(255, 0, 0);

    /// <summary>Weather region volume colour by type (weather_region.cpp:778-790): rain blue, snow pale blue-white.</summary>
    public static uint WeatherColor(AlpineWeatherRegion w) =>
        w.WeatherType == 1 ? Palette.Rgba(200, 230, 255) : Palette.Rgba(64, 160, 255);

    /// <summary>Directional light colour (dir_light.cpp:655-663): green-white when initially on, grey when off.</summary>
    public static uint DirectionalLightColor(AlpineDirectionalLight l) =>
        l.InitiallyOn != 0 ? Palette.Rgba(255, 224, 96) : Palette.Rgba(144, 144, 144);

    /// <summary>Waypoint helper colour by type (waypoint_helper.cpp:41-48).</summary>
    public static uint WaypointHelperColor(AlpineWaypointHelper h) => h.HelperType switch
    {
        0 => Palette.Rgba(0, 220, 255),
        1 => Palette.Rgba(90, 90, 255),
        2 => Palette.Rgba(60, 220, 60),
        _ => Palette.Rgba(160, 160, 160),
    };

    /// <summary>The volume a weather region acts in: its box or its sphere (weather_region.cpp:792-817).</summary>
    public static void AddWeatherVolume(List<LineSegment> lines, AlpineWeatherRegion w, uint? color = null)
    {
        uint c = color ?? WeatherColor(w);
        if (w.Shape == 1)
        {
            // Rotation-invariant: the stored orientation is ignored for a sphere (weather_region.cpp:812-815).
            AddSphere(lines, V(w.Position), w.Radius, c);
        }
        else
        {
            AddBox(lines, w.Position, w.Orientation.Right, w.Orientation.Up, w.Orientation.Forward, new Vec3(w.Width, w.Height, w.Depth), c);
        }
    }

    /// <summary>
    /// A vehicle factory's facing arrow (2 units along forward, cyan) and, for a turret with a yaw limit, the two stops
    /// either side of the spawn facing about world up (1.5 units, yellow) — vehicle_factory.cpp:775-812.
    /// </summary>
    public static void AddVehicleFactory(List<LineSegment> lines, AlpineVehicleFactory vf, EntityCatalog? entities, uint? color = null)
    {
        Vec3 p = vf.Position;
        Vec3 f = vf.Orientation.Forward;
        AddArrow(lines, p, p.Add(f.Scale(2f)), color ?? Palette.Rgba(0, 255, 255));

        float horiz = MathF.Sqrt((f.X * f.X) + (f.Z * f.Z));
        if (vf.TurretYawLimitDegrees == 0 || vf.TurretYawLimitDegrees > AlpineVehicleFactoryLimits.MaxTurretYawLimitDegrees
            || horiz <= 0.01f || !IsTurretClass(vf.VehicleClass, entities))
        {
            return;
        }

        float limit = vf.TurretYawLimitDegrees * MathF.PI / 180f;
        foreach (float a in new[] { -limit, limit })
        {
            float c = MathF.Cos(a);
            float s = MathF.Sin(a);
            var dir = new Vec3(((f.X * c) + (f.Z * s)) / horiz, 0f, ((f.Z * c) - (f.X * s)) / horiz);
            AddArrow(lines, p, p.Add(dir.Scale(1.5f)), Palette.Rgba(255, 255, 0));
        }
    }

    /// <summary>True for a turret class: entity.tbl <c>$Use: turret</c>, else the stock turret names (vehicle_factory.cpp:42-48).</summary>
    public static bool IsTurretClass(string className, EntityCatalog? entities)
    {
        if (entities?.Find(className) is { } def)
        {
            return string.Equals(def.UseFunction, "turret", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(className, "Stationary Turret_Plain", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "Stationary Turret", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A projection camera's facing arrow (1 unit, red — projection_camera.cpp:174-179) and the frustum of the
    /// Display_Projection event that renders it: the event's FOV (float1, default 50°, 1..179) and render size
    /// (int1 × int2, default 256 × 256, 16..2048) as the game applies them (game_patch/object/event_alpine.cpp:1162-1167).
    /// The FOV is taken as horizontal.
    /// </summary>
    public static void AddProjectionCamera(List<LineSegment> lines, AlpineProjectionCamera cam, RflFile? file, uint? color = null)
    {
        Vec3 p = cam.Position;
        AddArrow(lines, p, p.Add(cam.Orientation.Forward.Normalized()), color ?? Palette.Rgba(255, 0, 0));

        (float fov, float aspect) = ProjectionView(cam.Uid, file);
        AddFrustum(lines, cam.Position, cam.Orientation, fov, aspect, 2f, color ?? Palette.Rgba(255, 192, 32));
    }

    /// <summary>The FOV (degrees) and width / height of the first Display_Projection event linked to camera <paramref name="uid"/>.</summary>
    public static (float FovDegrees, float Aspect) ProjectionView(int uid, RflFile? file)
    {
        RflEvent? ev = file?.Sections.Select(s => s.Content).OfType<EventsSection>()
            .SelectMany(s => s.Events)
            .FirstOrDefault(e => e.ClassName == "Display_Projection" && e.Links.Contains(uid));
        float fov = ev is { Float1: > 0f } ? Math.Clamp(ev.Float1, 1f, 179f) : 50f;
        int w = ev is { Int1: > 0 } ? Math.Clamp(ev.Int1, 16, 2048) : 256;
        int h = ev is { Int2: > 0 } ? Math.Clamp(ev.Int2, 16, 2048) : 256;
        return (fov, w / (float)h);
    }

    /// <summary>
    /// A rope's resting shape (rope_emitter.cpp:1700-1790): the solved polyline in the rope's tint dimmed to 3/5 and
    /// floored at 0x40 so a dark rope still reads.
    /// </summary>
    public static void AddRope(List<LineSegment> lines, AlpineRopeEmitter rope, Func<int, Vec3?> resolve, uint? color = null)
    {
        Vec3? target = rope.TargetUid != -1 && rope.TargetUid != rope.Uid ? resolve(rope.TargetUid) : null;
        IReadOnlyList<Vec3> pts = RopeCurve.Polyline(rope.Position, target, rope.Slack, rope.DangleLength, rope.Segments);
        RfColor tint = rope.Color;
        uint c = color ?? Palette.Rgba(Dim(tint.R), Dim(tint.G), Dim(tint.B));
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            lines.Add(new LineSegment(V(pts[i]), V(pts[i + 1]), c));
        }
    }

    /// <summary>A directional light's travel arrow: 2 units along forward (dir_light.cpp:339-344, 672).</summary>
    public static void AddDirectionalLightArrow(List<LineSegment> lines, AlpineDirectionalLight l, uint? color = null)
    {
        Vec3 f = l.Orientation.Forward.Normalized();
        AddArrow(lines, l.Position, l.Position.Add(f.Scale(2f)), color ?? DirectionalLightColor(l));
    }

    /// <summary>
    /// A directional light's bounding volume (dir_light.cpp:346-377; alpine_dir_light.h:31-36, 226-236): a box centred on
    /// the light, world-aligned after Box Yaw (width X, height Y, depth Z); a sphere of radius extent X; or a cylinder of
    /// radius extent X and length extent Y along the travel direction, centred on the light. Nothing for shape None.
    /// </summary>
    public static void AddDirectionalLightVolume(List<LineSegment> lines, AlpineDirectionalLight l, uint? color = null)
    {
        uint c = color ?? DirectionalLightColor(l);
        switch (l.Shape)
        {
            case 1:
            {
                float yaw = l.BoxYaw * MathF.PI / 180f;
                var right = new Vec3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
                var forward = new Vec3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                AddBox(lines, l.Position, right, new Vec3(0f, 1f, 0f), forward, new Vec3(l.ExtentX, l.ExtentY, l.ExtentZ), c);
                break;
            }

            case 2:
                AddSphere(lines, V(l.Position), l.ExtentX, c);
                break;
            case 3:
                AddCylinder(lines, l.Position, l.Orientation, l.ExtentX, l.ExtentY, c);
                break;
        }
    }

    /// <summary>
    /// A waypoint helper (waypoint_helper.cpp:391-414): its radius sphere, a 0.5-unit centre cross, and for a landing
    /// spot a yellow arrow straight down (up to 2 units).
    /// </summary>
    public static void AddWaypointHelper(List<LineSegment> lines, AlpineWaypointHelper h, uint? color = null)
    {
        uint c = color ?? WaypointHelperColor(h);
        Vector3 p = V(h.Position);
        float radius = float.IsFinite(h.Radius) ? Math.Clamp(h.Radius, AlpineWaypointHelperLimits.MinRadius, AlpineWaypointHelperLimits.MaxRadius) : 4f;
        AddSphere(lines, p, radius, c);
        const float half = 0.25f;
        lines.Add(new LineSegment(p - new Vector3(half, 0, 0), p + new Vector3(half, 0, 0), c));
        lines.Add(new LineSegment(p - new Vector3(0, half, 0), p + new Vector3(0, half, 0), c));
        lines.Add(new LineSegment(p - new Vector3(0, 0, half), p + new Vector3(0, 0, half), c));
        if ((h.Flags & 0x1) != 0)
        {
            float len = MathF.Min(radius, 2f);
            AddArrow(lines, h.Position, h.Position.Add(new Vec3(0f, -len, 0f)), Palette.Rgba(255, 255, 0));
        }
    }

    /// <summary>
    /// The Alpine mover-path segments of every moving group in <paramref name="file"/> that the stock keyframe chain
    /// does not already draw: curved segments as 24-step Bézier polylines and a loop's straight closing segment
    /// (mover_path.cpp:394-418, 441-462). Returns the (from, to) keyframe pairs drawn as curves, so the caller skips
    /// their straight stock link.
    /// </summary>
    public static HashSet<(int From, int To)> AddMoverPaths(List<LineSegment> lines, RflFile file, IEnumerable<Group> groups, uint color)
    {
        var curved = new HashSet<(int, int)>();
        foreach (Group g in groups)
        {
            if (g.IsMoving == 0 || g.MovingData is not { Keyframes.Count: >= 2 })
            {
                continue;
            }

            foreach (MoverPathSegment seg in MoverPathGeometry.Segments(file, g))
            {
                if (seg.Curved)
                {
                    curved.Add((seg.FromUid, seg.ToUid));
                    const int steps = 24;
                    Vector3 prev = V(seg.P0);
                    for (int i = 1; i <= steps; i++)
                    {
                        Vector3 pt = V(seg.Sample(i / (float)steps));
                        lines.Add(new LineSegment(prev, pt, color));
                        prev = pt;
                    }

                    OverlayBuilder.AddArrowHead(lines, V(seg.Sample(0.5f)), V(seg.Sample(0.6f)), color);
                }
                else if (seg.Closing)
                {
                    lines.Add(new LineSegment(V(seg.P0), V(seg.P3), color));
                    OverlayBuilder.AddArrowHead(lines, V(seg.P0), V(seg.P3), color);
                }
            }
        }

        return curved;
    }

    /// <summary>
    /// The curve handles of a selected mover's keyframes (mover_path.cpp:355-384): a line from each keyframe to each
    /// handle its curved segments use, with a small sphere at the handle (yellow-orange).
    /// </summary>
    public static void AddMoverPathHandles(List<LineSegment> lines, RflFile file, Group group)
    {
        uint c = Palette.Rgba(255, 200, 0);
        if (!MoverPathGeometry.HasPaths(file, group) || group.MovingData is not { Keyframes.Count: >= 2 } motion)
        {
            return;
        }

        foreach (MoverPathSegment seg in MoverPathGeometry.Segments(file, group))
        {
            if (!seg.Curved)
            {
                continue;
            }

            foreach ((Vec3 from, Vec3 handle) in new[] { (seg.P0, seg.P1), (seg.P3, seg.P2) })
            {
                lines.Add(new LineSegment(V(from), V(handle), c));
                AddSphere(lines, V(handle), 0.2f, c, 12);
            }
        }

        _ = motion;
    }

    // ---- Primitives ---------------------------------------------------------------

    /// <summary>An arrow from <paramref name="from"/> to <paramref name="to"/> with the shared arrowhead.</summary>
    public static void AddArrow(List<LineSegment> lines, Vec3 from, Vec3 to, uint color)
    {
        Vector3 a = V(from);
        Vector3 b = V(to);
        if ((b - a).LengthSquared() < 1e-8f)
        {
            return;
        }

        lines.Add(new LineSegment(a, b, color));
        AddHead(lines, a, b, color);
    }

    private static void AddHead(List<LineSegment> lines, Vector3 from, Vector3 to, uint color)
    {
        Vector3 dir = to - from;
        float len = dir.Length();
        dir /= len;
        Vector3 up = MathF.Abs(dir.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 side = Vector3.Normalize(Vector3.Cross(dir, up));
        Vector3 side2 = Vector3.Cross(dir, side);
        float wing = MathF.Min(0.25f, len * 0.2f);
        foreach (Vector3 s in new[] { side, -side, side2, -side2 })
        {
            lines.Add(new LineSegment(to, to - (dir * wing) + (s * wing * 0.5f), color));
        }
    }

    /// <summary>A three-ring wire sphere.</summary>
    public static void AddSphere(List<LineSegment> lines, Vector3 center, float radius, uint color, int segments = 24)
    {
        if (!(radius > 0f) || !float.IsFinite(radius))
        {
            return;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 prev = default;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * MathF.Tau;
                float c = MathF.Cos(a) * radius;
                float s = MathF.Sin(a) * radius;
                Vector3 p = axis switch
                {
                    0 => center + new Vector3(c, s, 0f),
                    1 => center + new Vector3(c, 0f, s),
                    _ => center + new Vector3(0f, c, s),
                };
                if (i > 0)
                {
                    lines.Add(new LineSegment(prev, p, color));
                }

                prev = p;
            }
        }
    }

    /// <summary>A box centred on <paramref name="center"/> with full <paramref name="size"/> along the three axes.</summary>
    public static void AddBox(List<LineSegment> lines, Vec3 center, Vec3 right, Vec3 up, Vec3 forward, Vec3 size, uint color)
    {
        Vector3 o = V(center);
        Vector3 rx = V(right) * (size.X * 0.5f);
        Vector3 ry = V(up) * (size.Y * 0.5f);
        Vector3 rz = V(forward) * (size.Z * 0.5f);
        var corner = new Vector3[8];
        int idx = 0;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    corner[idx++] = o + (rx * x) + (ry * y) + (rz * z);
                }
            }
        }

        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 },
        };
        for (int e = 0; e < 12; e++)
        {
            lines.Add(new LineSegment(corner[edges[e, 0]], corner[edges[e, 1]], color));
        }
    }

    /// <summary>A cylinder along <paramref name="rot"/>'s forward, centred on <paramref name="center"/>.</summary>
    public static void AddCylinder(List<LineSegment> lines, Vec3 center, Mat3 rot, float radius, float length, uint color)
    {
        if (!(radius > 0f) || !float.IsFinite(radius) || !float.IsFinite(length))
        {
            return;
        }

        Vector3 axis = V(rot.Forward.Normalized());
        if (axis.LengthSquared() < 0.5f)
        {
            axis = -Vector3.UnitY;
        }

        Vector3 u = MathF.Abs(axis.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 a = Vector3.Normalize(Vector3.Cross(axis, u));
        Vector3 b = Vector3.Cross(axis, a);
        Vector3 c0 = V(center) - (axis * (length * 0.5f));
        Vector3 c1 = V(center) + (axis * (length * 0.5f));
        const int seg = 24;
        Vector3 Ring(Vector3 c, int i)
        {
            float t = i / (float)seg * MathF.Tau;
            return c + (a * (MathF.Cos(t) * radius)) + (b * (MathF.Sin(t) * radius));
        }

        for (int i = 0; i < seg; i++)
        {
            lines.Add(new LineSegment(Ring(c0, i), Ring(c0, i + 1), color));
            lines.Add(new LineSegment(Ring(c1, i), Ring(c1, i + 1), color));
            if (i % 6 == 0)
            {
                lines.Add(new LineSegment(Ring(c0, i), Ring(c1, i), color));
            }
        }
    }

    /// <summary>A view frustum from <paramref name="pos"/> along forward, <paramref name="length"/> deep.</summary>
    public static void AddFrustum(List<LineSegment> lines, Vec3 pos, Mat3 rot, float fovDegrees, float aspect, float length, uint color)
    {
        Vector3 apex = V(pos);
        Vector3 fwd = V(rot.Forward.Normalized());
        Vector3 right = V(rot.Right.Normalized());
        Vector3 up = V(rot.Up.Normalized());
        if (fwd.LengthSquared() < 0.5f)
        {
            return;
        }

        float halfW = MathF.Tan(fovDegrees * MathF.PI / 360f) * length;
        float halfH = aspect > 1e-3f ? halfW / aspect : halfW;
        Vector3 c = apex + (fwd * length);
        Vector3[] corners =
        {
            c + (right * halfW) + (up * halfH),
            c - (right * halfW) + (up * halfH),
            c - (right * halfW) - (up * halfH),
            c + (right * halfW) - (up * halfH),
        };

        for (int i = 0; i < 4; i++)
        {
            lines.Add(new LineSegment(apex, corners[i], color));
            lines.Add(new LineSegment(corners[i], corners[(i + 1) % 4], color));
        }

        // An "up" tick on the top edge so the view's roll reads.
        Vector3 top = c + (up * halfH);
        lines.Add(new LineSegment(top, top + (up * (halfH * 0.3f)), color));
    }

    private static byte Dim(byte v) => (byte)Math.Max(v * 3 / 5, 0x40);

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}
