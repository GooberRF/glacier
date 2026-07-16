using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Bridges the editor document and the .rfg group file: export a selection of
/// brushes + objects into an .rfg (Alpine v300 writes the 0x0AFBAE05 per-brush
/// geoable/breakable metadata), and import an .rfg into the document at a camera
/// offset with every UID freshly remapped and intra-import links repaired. The
/// low-level .rfg read/write lives in <see cref="RfgFile"/>.
/// </summary>
public static class RfgInterop
{
    /// <summary>
    /// Imports every group of <paramref name="rfg"/> into <paramref name="doc"/>,
    /// offset by <paramref name="offset"/>, remapping all UIDs and repairing links
    /// among the imported set (as one undo entry). Returns the placed object UIDs.
    /// </summary>
    public static IReadOnlyList<int> Import(EditorDocument doc, RfgFile rfg, Vec3 offset)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(rfg);
        doc.Rfl.ParseAllKnownSections();

        var newUids = new List<int>();
        var actions = new List<(IList Dest, object Clone)>();
        var dirtyHosts = new HashSet<RflSection>();

        foreach (RfgGroup group in rfg.Groups)
        {
            var remap = new Dictionary<int, int>();

            // Pass 1: allocate a fresh UID for every member.
            foreach (Brush b in group.Brushes.Brushes)
            {
                remap[b.Uid] = doc.AllocateUid();
            }

            foreach (object m in ObjectModels(group))
            {
                if (ObjectUid.TryGet(m, out int uid))
                {
                    remap[uid] = doc.AllocateUid();
                }
            }

            // Pass 2: clone brushes.
            if (group.Brushes.Brushes.Count > 0)
            {
                RflSection host = doc.Rfl.GetOrCreateSection(SectionType.Brushes, () => new BrushesSection());
                var brushSec = (BrushesSection)host.Content!;
                foreach (Brush b in group.Brushes.Brushes)
                {
                    Brush clone = GeometryClone.Deep(b);
                    clone.Uid = remap[b.Uid];
                    clone.Position = clone.Position.Add(offset);
                    clone.State = BrushState.Normal;
                    actions.Add((brushSec.Brushes, clone));
                    newUids.Add(clone.Uid);
                }

                dirtyHosts.Add(host);
            }

            // Pass 2: clone objects, type by type.
            ImportList(doc, group.Lights.Lights, SectionType.Lights, () => new LightsSection(SectionType.Lights), s => ((LightsSection)s).Lights, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Events.Events, SectionType.Events, () => new EventsSection(), s => ((EventsSection)s).Events, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Entities.Entities, SectionType.Entities, () => new EntitiesSection(), s => ((EntitiesSection)s).Entities, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Items.Items, SectionType.Items, () => new ItemsSection(), s => ((ItemsSection)s).Items, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Clutters.Clutters, SectionType.Clutters, () => new CluttersSection(), s => ((CluttersSection)s).Clutters, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Triggers.Triggers, SectionType.Triggers, () => new TriggersSection(), s => ((TriggersSection)s).Triggers, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.MpRespawnPoints.Points, SectionType.MpRespawnPoints, () => new MpRespawnPointsSection(), s => ((MpRespawnPointsSection)s).Points, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.AmbientSounds.Sounds, SectionType.AmbientSounds, () => new AmbientSoundsSection(), s => ((AmbientSoundsSection)s).Sounds, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.ParticleEmitters.Emitters, SectionType.ParticleEmitters, () => new ParticleEmittersSection(), s => ((ParticleEmittersSection)s).Emitters, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.BoltEmitters.Emitters, SectionType.BoltEmitters, () => new BoltEmittersSection(), s => ((BoltEmittersSection)s).Emitters, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Decals.Decals, SectionType.Decals, () => new DecalsSection(), s => ((DecalsSection)s).Decals, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.Targets.Targets, SectionType.Targets, () => new TargetsSection(), s => ((TargetsSection)s).Targets, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.CutsceneCameras.Cameras, SectionType.CutsceneCameras, () => new CutsceneCamerasSection(), s => ((CutsceneCamerasSection)s).Cameras, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.GeoRegions.Regions, SectionType.GeoRegions, () => new GeoRegionsSection(), s => ((GeoRegionsSection)s).Regions, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.GasRegions.Regions, SectionType.GasRegions, () => new GasRegionsSection(), s => ((GasRegionsSection)s).Regions, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.PushRegions.Regions, SectionType.PushRegions, () => new PushRegionsSection(), s => ((PushRegionsSection)s).Regions, remap, offset, actions, newUids, dirtyHosts);
            ImportList(doc, group.ClimbingRegions.Regions, SectionType.ClimbingRegions, () => new ClimbingRegionsSection(), s => ((ClimbingRegionsSection)s).Regions, remap, offset, actions, newUids, dirtyHosts);

            // Nav points carry a parallel (empty) connection list.
            if (group.NavPoints.Count > 0)
            {
                RflSection host = doc.Rfl.GetOrCreateSection(SectionType.NavPoints, () => new NavPointsSection());
                var navSec = (NavPointsSection)host.Content!;
                foreach (NavPoint np in group.NavPoints)
                {
                    var clone = (NavPoint)ModelCloner.Clone(np);
                    if (remap.TryGetValue(np.Uid, out int nu))
                    {
                        clone.Uid = nu;
                        newUids.Add(nu);
                    }

                    clone.Position = clone.Position.Add(offset);
                    RemapLinks(clone, remap);
                    actions.Add((navSec.NavPoints, clone));
                    actions.Add((navSec.Connections, new List<int>()));
                }

                dirtyHosts.Add(host);
            }
        }

        if (actions.Count == 0)
        {
            return newUids;
        }

        doc.Undo.Execute(new RelayCommand($"Import {newUids.Count} object(s) from .rfg",
            () =>
            {
                foreach (var (dest, clone) in actions)
                {
                    dest.Add(clone);
                }

                foreach (RflSection h in dirtyHosts)
                {
                    EnsurePresent(doc, h);
                    h.Dirty = true;
                }

                doc.RefreshObjects();
            },
            () =>
            {
                foreach (var (dest, clone) in actions)
                {
                    dest.Remove(clone);
                }

                foreach (RflSection h in dirtyHosts)
                {
                    h.Dirty = true;
                }

                doc.RefreshObjects();
            }));

        return newUids;
    }

    /// <summary>
    /// Exports the given brushes + objects into a single-group .rfg. When
    /// <paramref name="alpine"/> is set the file is written at version 300 with the
    /// 0x0AFBAE05 per-brush geoable/breakable metadata pulled from
    /// <c>alpine_level_properties</c>.
    /// </summary>
    public static RfgFile Export(EditorDocument doc, IEnumerable<int> brushUids, IEnumerable<int> objectUids, bool alpine, string groupName = "group")
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Rfl.ParseAllKnownSections();
        var file = new RfgFile { Version = alpine ? 0x12C : 0xC8 };
        var group = new RfgGroup { Name = groupName };

        var wantBrushes = brushUids.ToHashSet();
        BrushesSection? brushes = FindContent<BrushesSection>(doc);
        var pickedBrushes = brushes?.Brushes.Where(b => wantBrushes.Contains(b.Uid)).ToList() ?? new List<Brush>();
        foreach (Brush b in pickedBrushes)
        {
            group.Brushes.Brushes.Add(GeometryClone.Deep(b));
        }

        var wantObjects = objectUids.ToHashSet();
        foreach (LevelObject o in doc.Objects.Where(o => wantObjects.Contains(o.Uid)))
        {
            object clone = o.CloneModel();
            switch (clone)
            {
                case Light l: group.Lights.Lights.Add(l); break;
                case RflEvent e: group.Events.Events.Add(e); break;
                case Entity en: group.Entities.Entities.Add(en); break;
                case Item it: group.Items.Items.Add(it); break;
                case Clutter c: group.Clutters.Clutters.Add(c); break;
                case Trigger t: group.Triggers.Triggers.Add(t); break;
                case MpRespawnPoint r: group.MpRespawnPoints.Points.Add(r); break;
                case NavPoint np: group.NavPoints.Add(np); break;
                case AmbientSound a: group.AmbientSounds.Sounds.Add(a); break;
                case ParticleEmitter pe: group.ParticleEmitters.Emitters.Add(pe); break;
                case BoltEmitter be: group.BoltEmitters.Emitters.Add(be); break;
                case Decal d: group.Decals.Decals.Add(d); break;
            }
        }

        if (alpine)
        {
            AlpineLevelPropertiesSection? alp = FindContent<AlpineLevelPropertiesSection>(doc);
            if (alp is not null)
            {
                for (int i = 0; i < pickedBrushes.Count; i++)
                {
                    int uid = pickedBrushes[i].Uid;
                    if (alp.GeoableEntries.Any(g => g.BrushUid == uid))
                    {
                        group.AlpineBrushInfos.Add(new AlpineBrushInfo { BrushIndex = (uint)i, Flags = 0x1 });
                    }
                    else if (alp.BreakableEntries.FirstOrDefault(b => b.BrushUid == uid) is { } br)
                    {
                        group.AlpineBrushInfos.Add(new AlpineBrushInfo { BrushIndex = (uint)i, Flags = 0x2, Material = br.Material });
                    }
                }
            }
        }

        file.Groups.Add(group);
        return file;
    }

    // ---- helpers --------------------------------------------------------------

    private static void ImportList<T>(
        EditorDocument doc, IReadOnlyList<T> source, SectionType type, Func<IRflSectionContent> make,
        Func<IRflSectionContent, IList> getList, Dictionary<int, int> remap, Vec3 offset,
        List<(IList Dest, object Clone)> actions, List<int> newUids, HashSet<RflSection> dirtyHosts)
        where T : class
    {
        if (source.Count == 0)
        {
            return;
        }

        RflSection host = doc.Rfl.GetOrCreateSection(type, make);
        IList dest = getList(host.Content!);
        foreach (T item in source)
        {
            object clone = ModelCloner.Clone(item);
            if (ObjectUid.TryGet(clone, out int oldUid) && remap.TryGetValue(oldUid, out int nu))
            {
                ObjectUid.Set(clone, nu);
                newUids.Add(nu);
            }

            OffsetPosition(clone, offset);
            RemapLinks(clone, remap);
            actions.Add((dest, clone));
        }

        dirtyHosts.Add(host);
    }

    private static IEnumerable<object> ObjectModels(RfgGroup g)
    {
        foreach (Light l in g.Lights.Lights)
        {
            yield return l;
        }

        foreach (RflEvent e in g.Events.Events)
        {
            yield return e;
        }

        foreach (Entity en in g.Entities.Entities)
        {
            yield return en;
        }

        foreach (Item it in g.Items.Items)
        {
            yield return it;
        }

        foreach (Clutter c in g.Clutters.Clutters)
        {
            yield return c;
        }

        foreach (Trigger t in g.Triggers.Triggers)
        {
            yield return t;
        }

        foreach (MpRespawnPoint r in g.MpRespawnPoints.Points)
        {
            yield return r;
        }

        foreach (AmbientSound a in g.AmbientSounds.Sounds)
        {
            yield return a;
        }

        foreach (ParticleEmitter pe in g.ParticleEmitters.Emitters)
        {
            yield return pe;
        }

        foreach (BoltEmitter be in g.BoltEmitters.Emitters)
        {
            yield return be;
        }

        foreach (Decal d in g.Decals.Decals)
        {
            yield return d;
        }

        foreach (ObjectHeader t in g.Targets.Targets)
        {
            yield return t;
        }

        foreach (ObjectHeader cc in g.CutsceneCameras.Cameras)
        {
            yield return cc;
        }

        foreach (GeoRegion gr in g.GeoRegions.Regions)
        {
            yield return gr;
        }

        foreach (GasRegion gas in g.GasRegions.Regions)
        {
            yield return gas;
        }

        foreach (PushRegion p in g.PushRegions.Regions)
        {
            yield return p;
        }

        foreach (ClimbingRegion cr in g.ClimbingRegions.Regions)
        {
            yield return cr;
        }

        foreach (NavPoint np in g.NavPoints)
        {
            yield return np;
        }
    }

    private static void OffsetPosition(object model, Vec3 offset)
    {
        if (offset.X == 0 && offset.Y == 0 && offset.Z == 0)
        {
            return;
        }

        PropertyInfo? p = model.GetType().GetProperty("Position");
        if (p is { CanRead: true, CanWrite: true } && p.PropertyType == typeof(Vec3))
        {
            p.SetValue(model, ((Vec3)p.GetValue(model)!).Add(offset));
            return;
        }

        // ObjectHeader-backed models carry position on their Header.
        if (model.GetType().GetProperty("Header")?.GetValue(model) is ObjectHeader h)
        {
            h.Position = h.Position.Add(offset);
        }
    }

    private static void RemapLinks(object model, Dictionary<int, int> remap)
    {
        List<int>? links = model switch
        {
            Trigger t => t.Links,
            RflEvent e => e.Links,
            Clutter c => c.Links,
            NavPoint n => n.Links,
            _ => null,
        };
        if (links is null)
        {
            return;
        }

        for (int i = 0; i < links.Count; i++)
        {
            if (remap.TryGetValue(links[i], out int mapped))
            {
                links[i] = mapped;
            }
        }
    }

    private static void EnsurePresent(EditorDocument doc, RflSection host)
    {
        if (!doc.Rfl.Sections.Contains(host))
        {
            int endIndex = doc.Rfl.Sections.FindIndex(s => s.IsEnd);
            if (endIndex >= 0)
            {
                doc.Rfl.Sections.Insert(endIndex, host);
            }
            else
            {
                doc.Rfl.Sections.Add(host);
            }
        }
    }

    private static T? FindContent<T>(EditorDocument doc)
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }
}
