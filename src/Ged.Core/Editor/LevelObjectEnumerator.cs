using System;
using System.Collections;
using System.Collections.Generic;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editor;

/// <summary>
/// Projects the parsed sections of an <see cref="RflFile"/> into a flat list of
/// <see cref="LevelObject"/> handles. Objects that carry a shared
/// <see cref="ObjectHeader"/> are wired generically; the flat-field types are
/// wired individually. UIDs that are "hidden" for types without a persistent
/// hidden byte live in the caller-supplied session set.
/// </summary>
internal static class LevelObjectEnumerator
{
    public static List<LevelObject> Enumerate(RflFile file, HashSet<int> sessionHidden)
    {
        var result = new List<LevelObject>();

        foreach (RflSection section in file.Sections)
        {
            switch (section.Content)
            {
                case EntitiesSection s:
                    foreach (Entity e in s.Entities)
                    {
                        result.Add(Flat(LevelObjectKind.Entity, section, e, s.Entities,
                            () => e.Uid, u => e.Uid = u, () => e.ScriptName, v => e.ScriptName = v,
                            () => e.Position, p => e.Position = p, () => e.ClassName,
                            () => e.HiddenInEditor != 0, v => e.HiddenInEditor = B(v)));
                    }

                    break;

                case ItemsSection s:
                    foreach (Item it in s.Items)
                    {
                        result.Add(Header(LevelObjectKind.Item, section, it, it.Header, s.Items));
                    }

                    break;

                case CluttersSection s:
                    foreach (Clutter c in s.Clutters)
                    {
                        result.Add(Header(LevelObjectKind.Clutter, section, c, c.Header, s.Clutters));
                    }

                    break;

                case LightsSection s:
                    foreach (Light l in s.Lights)
                    {
                        result.Add(Flat(LevelObjectKind.Light, section, l, s.Lights,
                            () => l.Uid, u => l.Uid = u, () => l.ScriptName, v => l.ScriptName = v,
                            () => l.Position, p => l.Position = p, () => l.ClassName,
                            () => l.HiddenInEditor != 0, v => l.HiddenInEditor = B(v)));
                    }

                    break;

                case TriggersSection s:
                    foreach (Trigger t in s.Triggers)
                    {
                        result.Add(Flat(LevelObjectKind.Trigger, section, t, s.Triggers,
                            () => t.Uid, u => t.Uid = u, () => t.ScriptName, v => t.ScriptName = v,
                            () => t.Position, p => t.Position = p, () => "Trigger",
                            () => t.HiddenInEditor != 0, v => t.HiddenInEditor = B(v)));
                    }

                    break;

                case EventsSection s:
                    foreach (RflEvent ev in s.Events)
                    {
                        result.Add(Flat(LevelObjectKind.Event, section, ev, s.Events,
                            () => ev.Uid, u => ev.Uid = u, () => ev.ScriptName, v => ev.ScriptName = v,
                            () => ev.Position, p => ev.Position = p, () => ev.ClassName,
                            () => ev.HiddenInEditor != 0, v => ev.HiddenInEditor = B(v)));
                    }

                    break;

                case AmbientSoundsSection s:
                    foreach (AmbientSound a in s.Sounds)
                    {
                        result.Add(Session(LevelObjectKind.AmbientSound, section, a, s.Sounds, sessionHidden,
                            () => a.Uid, u => a.Uid = u, () => string.Empty, _ => { },
                            () => a.Position, p => a.Position = p, () => a.SoundFileName));
                    }

                    break;

                case MpRespawnPointsSection s:
                    foreach (MpRespawnPoint r in s.Points)
                    {
                        result.Add(Flat(LevelObjectKind.MpRespawnPoint, section, r, s.Points,
                            () => r.Uid, u => r.Uid = u, () => r.ScriptName, v => r.ScriptName = v,
                            () => r.Position, p => r.Position = p, () => "MP Respawn",
                            () => r.HiddenInEditor != 0, v => r.HiddenInEditor = B(v)));
                    }

                    break;

                case ParticleEmittersSection s:
                    foreach (ParticleEmitter pe in s.Emitters)
                    {
                        result.Add(Header(LevelObjectKind.ParticleEmitter, section, pe, pe.Header, s.Emitters));
                    }

                    break;

                case BoltEmittersSection s:
                    foreach (BoltEmitter be in s.Emitters)
                    {
                        result.Add(Header(LevelObjectKind.BoltEmitter, section, be, be.Header, s.Emitters));
                    }

                    break;

                case NavPointsSection s:
                    foreach (NavPoint np in s.NavPoints)
                    {
                        result.Add(Session(LevelObjectKind.NavPoint, section, np, s.NavPoints, sessionHidden,
                            () => np.Uid, u => np.Uid = u, () => string.Empty, _ => { },
                            () => np.Position, p => np.Position = p, () => "Nav Point"));
                    }

                    break;

                case TargetsSection s:
                    foreach (ObjectHeader t in s.Targets)
                    {
                        result.Add(Header(LevelObjectKind.Target, section, t, t, s.Targets));
                    }

                    break;

                case CutsceneCamerasSection s:
                    foreach (ObjectHeader c in s.Cameras)
                    {
                        result.Add(Header(LevelObjectKind.CutsceneCamera, section, c, c, s.Cameras));
                    }

                    break;

                case CutscenePathNodesSection s:
                    foreach (ObjectHeader node in s.Nodes)
                    {
                        result.Add(Header(LevelObjectKind.CutscenePathNode, section, node, node, s.Nodes));
                    }

                    break;

                case DecalsSection s:
                    foreach (Decal d in s.Decals)
                    {
                        result.Add(Header(LevelObjectKind.Decal, section, d, d.Header, s.Decals));
                    }

                    break;

                case GeoRegionsSection s:
                    foreach (GeoRegion g in s.Regions)
                    {
                        result.Add(new LevelObject(LevelObjectKind.GeoRegion, section, g, s.Regions,
                            () => g.Uid, u => g.Uid = u, () => string.Empty, _ => { },
                            () => g.Position, p => g.Position = p,
                            () => (g.Flags & GeoRegion.FlagHiddenInEditor) != 0,
                            v => g.Flags = v ? (ushort)(g.Flags | GeoRegion.FlagHiddenInEditor)
                                             : (ushort)(g.Flags & ~GeoRegion.FlagHiddenInEditor),
                            () => "Geo Region"));
                    }

                    break;

                case GasRegionsSection s:
                    foreach (GasRegion g in s.Regions)
                    {
                        result.Add(Header(LevelObjectKind.GasRegion, section, g, g.Header, s.Regions));
                    }

                    break;

                case ClimbingRegionsSection s:
                    foreach (ClimbingRegion c in s.Regions)
                    {
                        result.Add(Header(LevelObjectKind.ClimbRegion, section, c, c.Header, s.Regions));
                    }

                    break;

                case PushRegionsSection s:
                    foreach (PushRegion p in s.Regions)
                    {
                        result.Add(Header(LevelObjectKind.PushRegion, section, p, p.Header, s.Regions));
                    }

                    break;

                case RoomEffectsSection s:
                    foreach (RoomEffect fx in s.Effects)
                    {
                        result.Add(Header(LevelObjectKind.RoomEffect, section, fx, fx.Header, s.Effects));
                    }

                    break;

                case EaxEffectsSection s:
                    // B3: project EAX effect zones as first-class level objects so they are
                    // clickable, selectable, resolvable by UID, and listed in the outliner — the
                    // rendering layer already emits the EAX billboard + pick id, but without this
                    // projection FindByUid returned null and the pick resolved to nothing. Each
                    // carries a full ObjectHeader (uid/class/pos/rot/script/hidden).
                    foreach (EaxEffect eax in s.Effects)
                    {
                        result.Add(Header(LevelObjectKind.Eax, section, eax, eax.Header, s.Effects));
                    }

                    break;

                case MoversSection s:
                    foreach (Brush m in s.Movers)
                    {
                        result.Add(Session(LevelObjectKind.Mover, section, m, s.Movers, sessionHidden,
                            () => m.Uid, u => m.Uid = u, () => string.Empty, _ => { },
                            () => m.Position, p => m.Position = p, () => "Mover"));
                    }

                    break;

                case GroupsSection s:
                    // A moving group's keyframes are objects with their own UIDs: project each so
                    // it is selectable, resolvable by UID (links) and shown in the outliner. The
                    // owning list is the group's keyframe list, so delete/copy round-trips there.
                    foreach (Group grp in s.Groups)
                    {
                        if (grp.IsMoving == 0 || grp.MovingData is not { } data)
                        {
                            continue;
                        }

                        foreach (Keyframe k in data.Keyframes)
                        {
                            result.Add(Flat(LevelObjectKind.Keyframe, section, k, data.Keyframes,
                                () => k.Uid, u => k.Uid = u, () => k.ScriptName, v => k.ScriptName = v,
                                () => k.Position, p => k.Position = p, () => "Keyframe",
                                () => k.HiddenInEditor != 0, v => k.HiddenInEditor = B(v)));
                        }
                    }

                    break;

                case AlpineMeshObjectsSection s:
                    foreach (AlpineMeshObject mo in s.Meshes)
                    {
                        result.Add(Session(LevelObjectKind.MeshObject, section, mo, s.Meshes, sessionHidden,
                            () => mo.Uid, u => mo.Uid = u, () => mo.ScriptName, v => mo.ScriptName = v,
                            () => mo.Position, p => mo.Position = p, () => mo.MeshFilename));
                    }

                    break;

                case AlpineNoteObjectsSection s:
                    foreach (AlpineNoteObject n in s.Notes)
                    {
                        result.Add(Session(LevelObjectKind.NoteObject, section, n, s.Notes, sessionHidden,
                            () => n.Uid, u => n.Uid = u, () => n.ScriptName, v => n.ScriptName = v,
                            () => n.Position, p => n.Position = p, () => "Note"));
                    }

                    break;

                case AlpineCoronaObjectsSection s:
                    foreach (AlpineCoronaObject c in s.Coronas)
                    {
                        result.Add(Session(LevelObjectKind.CoronaObject, section, c, s.Coronas, sessionHidden,
                            () => c.Uid, u => c.Uid = u, () => c.ScriptName, v => c.ScriptName = v,
                            () => c.Position, p => c.Position = p, () => "Corona"));
                    }

                    break;

                case AlpineBagObjectsSection s:
                    foreach (AlpineBagObject b in s.Bags)
                    {
                        result.Add(Session(LevelObjectKind.BagObject, section, b, s.Bags, sessionHidden,
                            () => b.Uid, u => b.Uid = u, () => string.Empty, _ => { },
                            () => b.Position, p => b.Position = p, () => "Bag"));
                    }

                    break;

                case PlayerStartSection s:
                    result.Add(new LevelObject(LevelObjectKind.PlayerStart, section, s, null,
                        () => 0, _ => { }, () => "Player Start", _ => { },
                        () => s.Position, p => s.Position = p,
                        () => false, _ => { }, () => "Player Start"));
                    break;
            }
        }

        return result;
    }

    private static byte B(bool value) => value ? (byte)1 : (byte)0;

    /// <summary>Wires an object that carries a shared <see cref="ObjectHeader"/>.</summary>
    private static LevelObject Header(LevelObjectKind kind, RflSection section, object model, ObjectHeader h, IList list) =>
        new(kind, section, model, list,
            () => h.Uid, u => h.Uid = u,
            () => h.ScriptName, v => h.ScriptName = v,
            () => h.Position, p => h.Position = p,
            () => h.HiddenInEditor != 0, v => h.HiddenInEditor = B(v),
            () => h.ClassName);

    /// <summary>Wires a flat-field object whose model owns its own hidden byte.</summary>
    private static LevelObject Flat(
        LevelObjectKind kind, RflSection section, object model, IList list,
        Func<int> getUid, Action<int> setUid, Func<string> getScript, Action<string> setScript,
        Func<Vec3> getPos, Action<Vec3> setPos, Func<string> getClass,
        Func<bool> getHidden, Action<bool> setHidden) =>
        new(kind, section, model, list, getUid, setUid, getScript, setScript, getPos, setPos,
            getHidden, setHidden, getClass);

    /// <summary>Wires an object with no persistent hidden byte; hidden lives in the session set.</summary>
    private static LevelObject Session(
        LevelObjectKind kind, RflSection section, object model, IList list, HashSet<int> sessionHidden,
        Func<int> getUid, Action<int> setUid, Func<string> getScript, Action<string> setScript,
        Func<Vec3> getPos, Action<Vec3> setPos, Func<string> getClass) =>
        new(kind, section, model, list, getUid, setUid, getScript, setScript, getPos, setPos,
            () => sessionHidden.Contains(getUid()),
            v =>
            {
                if (v)
                {
                    sessionHidden.Add(getUid());
                }
                else
                {
                    sessionHidden.Remove(getUid());
                }
            },
            getClass);
}
