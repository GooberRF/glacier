using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Effects;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Picking;

namespace Ged.Rendering.Scene;

/// <summary>Controls for the live emitter/gas previews.</summary>
public sealed class EffectsOptions
{
    /// <summary>Animation clock (seconds).</summary>
    public float Time { get; set; }

    /// <summary>Global "Animate Emitters" toggle; when false, particles/bolts are not simulated.</summary>
    public bool AnimateEmitters { get; set; } = true;

    /// <summary>Per-emitter opt-out by UID (from the inspector's per-emitter toggle).</summary>
    public HashSet<int>? DisabledEmitterUids { get; set; }

    /// <summary>Particle-sim budget/world tunables.</summary>
    public ParticleSimOptions? ParticleOptions { get; set; }

    /// <summary>Draw gas regions as translucent coloured volumes.</summary>
    public bool ShowGasRegions { get; set; } = true;

    private bool Enabled(int uid) => DisabledEmitterUids is null || !DisabledEmitterUids.Contains(uid);

    internal bool IsEmitterEnabled(int uid) => AnimateEmitters && Enabled(uid);
}

/// <summary>
/// Turns a level's particle emitters, bolt emitters and gas regions into live
/// preview geometry (camera-facing particle billboards, jittered bolt polylines,
/// translucent gas volumes) at a given animation time, driving the
/// deterministic <see cref="ParticleSimulator"/> / <see cref="BoltSimulator"/>.
/// Pure: appends to caller-supplied billboard/line lists so it can feed both the
/// offscreen artifact path (into a scene) and the live per-frame dynamic channel.
/// </summary>
public static class EffectsBuilder
{
    /// <summary>Simulates the level's emitters/gas into the two output lists.</summary>
    public static void Build(RflFile file, EffectsOptions options, List<Billboard> billboards, List<LineSegment> lines)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(billboards);
        ArgumentNullException.ThrowIfNull(lines);
        file.ParseAllKnownSections();

        Dictionary<int, Vector3>? uidPos = null;

        foreach (RflSection section in file.Sections)
        {
            switch (section.Content)
            {
                case ParticleEmittersSection pe when options.AnimateEmitters:
                    foreach (ParticleEmitter emitter in pe.Emitters)
                    {
                        if (options.IsEmitterEnabled(emitter.Header.Uid))
                        {
                            AppendParticles(emitter, options, billboards);
                        }
                    }

                    break;

                case BoltEmittersSection be when options.AnimateEmitters:
                    foreach (BoltEmitter bolt in be.Emitters)
                    {
                        if (options.IsEmitterEnabled(bolt.Header.Uid) && BoltSimulator.IsActiveAt(bolt, options.Time))
                        {
                            uidPos ??= CollectPositions(file);
                            AppendBolt(bolt, options.Time, uidPos, lines);
                        }
                    }

                    break;

                case GasRegionsSection gas when options.ShowGasRegions:
                    foreach (GasRegion region in gas.Regions)
                    {
                        AppendGasRegion(region, lines);
                    }

                    break;
            }
        }
    }

    /// <summary>Convenience: append the effects directly into a scene's billboards + lines.</summary>
    public static void Append(RflFile file, EffectsOptions options, RenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Build(file, options, scene.Billboards, scene.Lines);
    }

    private static void AppendParticles(ParticleEmitter emitter, EffectsOptions options, List<Billboard> billboards)
    {
        // Bind the emitter's authored bitmap so the preview shows the real particle
        // art (VBM/ATX resolve to frame 0 in the VFS). The GPU layer resolves the
        // name and falls back to the soft-sprite atlas cell when it cannot.
        string? texture = string.IsNullOrWhiteSpace(emitter.Texture) ? null : emitter.Texture;

        foreach (SimParticle p in ParticleSimulator.Simulate(emitter, options.Time, options.ParticleOptions))
        {
            uint tint = Palette.Rgba(p.Color.R, p.Color.G, p.Color.B, p.Color.A);
            billboards.Add(new Billboard(
                BillboardKind.ParticleEmitter,
                new Vector3(p.Position.X, p.Position.Y, p.Position.Z),
                MathF.Max(p.Radius, 0.02f),
                tint,
                PickId.None,
                Icon: (int)Graphics.EditorIcon.Disc,
                TextureName: texture));
        }
    }

    private static void AppendBolt(BoltEmitter bolt, float time, Dictionary<int, Vector3> uidPos, List<LineSegment> lines)
    {
        Vec3 source = bolt.Header.Position;
        if (!uidPos.TryGetValue(bolt.TargetUid, out Vector3 target3))
        {
            return; // no target placed
        }

        var target = new Vec3(target3.X, target3.Y, target3.Z);
        IReadOnlyList<Vec3> poly = BoltSimulator.Polyline(bolt, source, target, time);
        uint color = Palette.Rgba(bolt.Color.R, bolt.Color.G, bolt.Color.B, bolt.Color.A == 0 ? (byte)255 : bolt.Color.A);
        for (int i = 0; i < poly.Count - 1; i++)
        {
            Vec3 a = poly[i];
            Vec3 b = poly[i + 1];
            lines.Add(new LineSegment(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), color));
        }
    }

    private static void AppendGasRegion(GasRegion region, List<LineSegment> lines)
    {
        var c = region.Header.Position;
        var center = new Vector3(c.X, c.Y, c.Z);
        byte alpha = (byte)Math.Clamp((int)(region.GasDensity * 255f), 40, 220);
        uint color = Palette.Rgba(region.GasColor.R, region.GasColor.G, region.GasColor.B, alpha);

        if (region.Shape == 1 && region.Radius is float r && r > 0.01f)
        {
            AddSphereLines(lines, center, r, color);
        }
        else if (region.Width is float w && region.Height is float h && region.Depth is float d)
        {
            AddVolumeBox(lines, center, region.Header.Rotation, new Vector3(w, h, d), color);
        }
    }

    private static Dictionary<int, Vector3> CollectPositions(RflFile file)
    {
        var map = new Dictionary<int, Vector3>();
        void Put(int uid, Vec3 p) => map[uid] = new Vector3(p.X, p.Y, p.Z);

        foreach (RflSection section in file.Sections)
        {
            switch (section.Content)
            {
                case TargetsSection s: foreach (ObjectHeader t in s.Targets) Put(t.Uid, t.Position); break;
                case EntitiesSection s: foreach (Entity e in s.Entities) Put(e.Uid, e.Position); break;
                case ItemsSection s: foreach (Item it in s.Items) Put(it.Header.Uid, it.Header.Position); break;
                case CluttersSection s: foreach (Clutter cl in s.Clutters) Put(cl.Header.Uid, cl.Header.Position); break;
                case LightsSection s: foreach (Light l in s.Lights) Put(l.Uid, l.Position); break;
                case EventsSection s: foreach (RflEvent ev in s.Events) Put(ev.Uid, ev.Position); break;
                case NavPointsSection s: foreach (NavPoint n in s.NavPoints) Put(n.Uid, n.Position); break;
                case BoltEmittersSection s: foreach (BoltEmitter b in s.Emitters) Put(b.Header.Uid, b.Header.Position); break;
                case ParticleEmittersSection s: foreach (ParticleEmitter pe in s.Emitters) Put(pe.Header.Uid, pe.Header.Position); break;
                case CutsceneCamerasSection s: foreach (ObjectHeader cam in s.Cameras) Put(cam.Uid, cam.Position); break;
                case AlpineMeshObjectsSection s: foreach (AlpineMeshObject mo in s.Meshes) Put(mo.Uid, mo.Position); break;
            }
        }

        return map;
    }

    private static void AddSphereLines(List<LineSegment> lines, Vector3 center, float radius, uint color)
    {
        const int seg = 20;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 prev = default;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * MathF.Tau;
                float cs = MathF.Cos(a) * radius;
                float sn = MathF.Sin(a) * radius;
                Vector3 p = axis switch
                {
                    0 => center + new Vector3(cs, sn, 0f),
                    1 => center + new Vector3(cs, 0f, sn),
                    _ => center + new Vector3(0f, cs, sn),
                };
                if (i > 0)
                {
                    lines.Add(new LineSegment(prev, p, color));
                }

                prev = p;
            }
        }
    }

    private static void AddVolumeBox(List<LineSegment> lines, Vector3 center, Mat3 rot, Vector3 fullSize, uint color)
    {
        Vector3 h = fullSize * 0.5f;
        var m = new Matrix4x4(
            rot.Right.X, rot.Right.Y, rot.Right.Z, 0f,
            rot.Up.X, rot.Up.Y, rot.Up.Z, 0f,
            rot.Forward.X, rot.Forward.Y, rot.Forward.Z, 0f,
            center.X, center.Y, center.Z, 1f);

        Span<Vector3> corners = stackalloc Vector3[8];
        int idx = 0;
        for (int xi = -1; xi <= 1; xi += 2)
        {
            for (int yi = -1; yi <= 1; yi += 2)
            {
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    corners[idx++] = Vector3.Transform(new Vector3(xi * h.X, yi * h.Y, zi * h.Z), m);
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
            lines.Add(new LineSegment(corners[edges[e, 0]], corners[edges[e, 1]], color));
        }
    }
}
