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
                    // A mover's member brush is its EDITABLE copy in the brushes section; this movers-
                    // section copy is projected only so the whole mover stays selectable (for the mover
                    // inspector) — but with a NULL owning list, so it is not delete/copy-backed. That
                    // closes the tester's data-loss path where a mis-picked mover object was deleted out
                    // of the movers section (leaving the group pointing at a dead UID). Removing a mover
                    // goes through the world brush (Brush mode) or Dissolve, never a raw object delete.
                    foreach (Brush m in s.Movers)
                    {
                        result.Add(new LevelObject(LevelObjectKind.Mover, section, m, null,
                            () => m.Uid, u => m.Uid = u, () => string.Empty, _ => { },
                            () => m.Position, p => m.Position = p,
                            () => sessionHidden.Contains(m.Uid),
                            v => { if (v) { sessionHidden.Add(m.Uid); } else { sessionHidden.Remove(m.Uid); } },
                            () => "Mover"));
                    }

                    break;

                case GroupsSection s:
                    // A moving group's keyframes are objects with their own UIDs: project each so
                    // it is selectable, resolvable by UID (links) and shown in the outliner. The
                    // owning list is the group's keyframe list, so delete/copy round-trips there. The
                    // editor groups section's movers are copies of moving_groups (MovingGroupMirror), so
                    // their keyframes are projected once, from moving_groups.
                    bool editorCopy = Editing.MovingGroupMirror.IsEditorCopy(file, section);
                    foreach (Group grp in s.Groups)
                    {
                        if (grp.IsMoving == 0 || grp.MovingData is not { } data || editorCopy)
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

                // Alpine Faction 1.5 objects (editor_patch/weather_region.cpp, vehicle_factory.cpp, projection_camera.cpp,
                // rope_emitter.cpp, dir_light.cpp, waypoint_helper.cpp): editable like the 1.4 objects above (session
                // hidden state, as Alpine stores no hidden flag in these records). A chunk Glacier keeps raw
                // (IRetainedChunk) lists its objects read-only so its bytes are written back unchanged.
                case AlpineWeatherRegionsSection s:
                    foreach (AlpineWeatherRegion w in s.Records)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.WeatherRegion, section, w, s.Records, sessionHidden,
                            () => w.Uid, u => w.Uid = u, () => w.ScriptName, v => w.ScriptName = v,
                            () => w.Position, p => w.Position = p, () => w.WeatherType == 1 ? "Snow" : "Rain"));
                    }

                    break;

                case AlpineVehicleFactoriesSection s:
                    foreach (AlpineVehicleFactory v in s.Records)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.VehicleFactory, section, v, s.Records, sessionHidden,
                            () => v.Uid, u => v.Uid = u, () => v.ScriptName, n => v.ScriptName = n,
                            () => v.Position, p => v.Position = p, () => v.VehicleClass));
                    }

                    break;

                case AlpineProjectionCamerasSection s:
                    foreach (AlpineProjectionCamera c in s.Records)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.ProjectionCamera, section, c, s.Records, sessionHidden,
                            () => c.Uid, u => c.Uid = u, () => c.ScriptName, v => c.ScriptName = v,
                            () => c.Position, p => c.Position = p, () => "Projection Camera"));
                    }

                    break;

                case AlpineRopeEmittersSection s:
                    foreach (AlpineRopeEmitter rope in s.Records)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.RopeEmitter, section, rope, s.Records, sessionHidden,
                            () => rope.Uid, u => rope.Uid = u, () => rope.ScriptName, v => rope.ScriptName = v,
                            () => rope.Position, p => rope.Position = p, () => rope.IsZipline ? "Zipline" : "Rope"));
                    }

                    break;

                case AlpineDirectionalLightsSection s:
                    foreach (AlpineDirectionalLight l in s.Records)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.DirectionalLight, section, l, s.Records, sessionHidden,
                            () => l.Uid, u => l.Uid = u, () => l.ScriptName, v => l.ScriptName = v,
                            () => l.Position, p => l.Position = p, () => "Directional Light"));
                    }

                    break;

                case AlpineWaypointHelpersSection s:
                    // The record has no script name (alpine_waypoint_helper.h:44-56): the name setter does nothing.
                    foreach (AlpineWaypointHelper h in s.Records)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.WaypointHelper, section, h, s.Records, sessionHidden,
                            () => h.Uid, u => h.Uid = u, () => string.Empty, _ => { },
                            () => h.Position, p => h.Position = p, () => "Waypoint Helper"));
                    }

                    break;

                // Alpine terrains (editor_patch/terrain.cpp): editable like the 1.5 objects; the position is the centre
                // of the footprint at the origin's height (AlpineTerrainEdit.FootprintCentre) and moving one
                // translates the whole heightfield (it is never rotated or scaled, mfc_types.h:997). A chunk Alpine
                // would reject is kept raw and its terrains list read-only.
                case AlpineTerrainSection s:
                    foreach (AlpineTerrain t in s.Terrains)
                    {
                        result.Add(Alpine15(s, LevelObjectKind.Terrain, section, t, s.Terrains, sessionHidden,
                            () => t.Uid, u => t.Uid = u, () => t.ScriptName, v => t.ScriptName = v,
                            () => AlpineTerrainEdit.FootprintCentre(t), p => t.Origin = AlpineTerrainEdit.OriginForCentre(t, p),
                            () => "Terrain"));
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

    /// <summary>
    /// Wires an Alpine 1.5 object: editable (session hidden state) when its chunk parsed exactly, read-only when the
    /// chunk is kept raw (<see cref="IRetainedChunk.IsReadOnly"/>).
    /// </summary>
    private static LevelObject Alpine15(
        IRetainedChunk chunk, LevelObjectKind kind, RflSection section, object model, IList list, HashSet<int> sessionHidden,
        Func<int> getUid, Action<int> setUid, Func<string> getScript, Action<string> setScript,
        Func<Vec3> getPos, Action<Vec3> setPos, Func<string> getClass) =>
        chunk.IsReadOnly
            ? ReadOnly(kind, section, model, sessionHidden, getUid(), getScript, getPos, getClass())
            : Session(kind, section, model, list, sessionHidden, getUid, setUid, getScript, setScript, getPos, setPos, getClass);

    /// <summary>
    /// Wires an object read-only: it lists, selects, hides (session) and inspects, but its UID, name and position
    /// setters do nothing and it has no owning list (no delete / copy), so the chunk it came from is written back
    /// exactly as loaded. Used for terrains and for the objects of an Alpine chunk Glacier keeps raw.
    /// </summary>
    private static LevelObject ReadOnly(
        LevelObjectKind kind, RflSection section, object model, HashSet<int> sessionHidden,
        int uid, Func<string> getScript, Func<Vec3> getPos, string className) =>
        new(kind, section, model, null,
            () => uid, _ => { }, getScript, _ => { }, getPos, _ => { },
            () => sessionHidden.Contains(uid),
            v =>
            {
                if (v)
                {
                    sessionHidden.Add(uid);
                }
                else
                {
                    sessionHidden.Remove(uid);
                }
            },
            () => className,
            isReadOnly: true);

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
