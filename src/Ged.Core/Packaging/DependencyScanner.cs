using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Packaging;

/// <summary>Optional catalogs and inputs that let the scanner resolve game-shipped meshes and the dialogue file.</summary>
public sealed class DependencyScanOptions
{
    public ClutterCatalog? ClutterCatalog { get; set; }

    public EntityCatalog? EntityCatalog { get; set; }

    public ItemCatalog? ItemCatalog { get; set; }

    /// <summary>The companion dialogue text file name (e.g. <c>mylevel.txt</c>), added if present.</summary>
    public string? DialogueTextFile { get; set; }
}

/// <summary>
/// Walks a level and collects every asset it depends on: brush + compiled face
/// textures (incl. liquid), decal / particle / bolt / corona bitmaps, event file
/// references (sounds, bitmaps, meshes, vclips, videos, animations, MVFs via the
/// event schema's FilePicker fields), Alpine mesh objects and their material
/// textures, ambient + mover sounds, the geomod texture, ATX descriptors and
/// their frame files, and (when catalogs are supplied) clutter/entity/item meshes
/// and skins. Pure Ged.Core: <see cref="Gather"/> lists the raw references; <see
/// cref="Scan"/> resolves and classifies them against a mounted VFS (included /
/// base-game-skipped / missing) with mesh-material and ATX-frame expansion.
/// </summary>
public static class DependencyScanner
{
    /// <summary>
    /// Collects the raw (unresolved) dependency references from the level, in a
    /// stable discovery order. Empty references are skipped.
    /// </summary>
    public static IReadOnlyList<DependencyRef> Gather(RflFile rfl, DependencyScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        rfl.ParseAllKnownSections();
        options ??= new DependencyScanOptions();
        var refs = new List<DependencyRef>();

        // Item 4: light projection cookies are EDITOR-ONLY — the game never loads them, so they are
        // excluded from the pack (never packed, never flagged missing-for-pack), exactly like the
        // .gedlayout sidecar. Collect their filenames from the object-metadata chunk and skip any
        // reference that names one.
        HashSet<string> cookies = CookieFileNames(rfl);

        void Add(string? file, DependencyKind kind, string origin, int? uid = null)
        {
            string? trimmed = file?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && !EditorOnlyFiles.IsEditorOnly(trimmed) && !cookies.Contains(trimmed))
            {
                refs.Add(new DependencyRef(trimmed, kind, origin, uid));
            }
        }

        foreach (IRflSectionContent content in rfl.Sections.Select(s => s.Content).OfType<IRflSectionContent>())
        {
            switch (content)
            {
                case GeometrySection geo:
                    GatherGeometry(geo.Geometry, "static geometry", null, Add);
                    break;

                case BrushesSection brushes:
                    foreach (Brush b in brushes.Brushes)
                    {
                        GatherGeometry(b.Geometry, $"brush {b.Uid}", b.Uid, Add);
                    }

                    break;

                case MoversSection movers:
                    foreach (Brush m in movers.Movers)
                    {
                        GatherGeometry(m.Geometry, $"mover {m.Uid}", m.Uid, Add);
                    }

                    break;

                case RoomEffectsSection re:
                    foreach (RoomEffect e in re.Effects.Where(e => e.LiquidProperties is not null))
                    {
                        Add(e.LiquidProperties!.SurfaceTexture, DependencyKind.LiquidTexture, $"room effect {e.Header.Uid} liquid", e.Header.Uid);
                    }

                    break;

                case DecalsSection decals:
                    foreach (Decal d in decals.Decals)
                    {
                        Add(d.Texture, DependencyKind.DecalTexture, $"decal {d.Header.Uid}", d.Header.Uid);
                    }

                    break;

                case ParticleEmittersSection pe:
                    foreach (ParticleEmitter e in pe.Emitters)
                    {
                        Add(e.Texture, DependencyKind.ParticleBitmap, $"particle emitter {e.Header.Uid}", e.Header.Uid);
                    }

                    break;

                case BoltEmittersSection be:
                    foreach (BoltEmitter e in be.Emitters)
                    {
                        Add(e.Texture, DependencyKind.BoltBitmap, $"bolt emitter {e.Header.Uid}", e.Header.Uid);
                    }

                    break;

                case AlpineCoronaObjectsSection coronas:
                    foreach (AlpineCoronaObject c in coronas.Coronas)
                    {
                        Add(c.CoronaBitmap, DependencyKind.CoronaBitmap, $"corona {c.Uid}", c.Uid);
                        Add(c.VolumetricBitmap, DependencyKind.CoronaBitmap, $"corona {c.Uid} (volumetric)", c.Uid);
                    }

                    break;

                case AlpineMeshObjectsSection meshes:
                    foreach (AlpineMeshObject m in meshes.Meshes)
                    {
                        Add(m.MeshFilename, DependencyKind.MeshObject, $"mesh object {m.Uid}", m.Uid);
                        Add(m.StateAnim, DependencyKind.MeshAnimation, $"mesh object {m.Uid} state anim", m.Uid);
                        foreach (AlpineMeshTextureOverride ov in m.TextureOverrides)
                        {
                            Add(ov.Filename, DependencyKind.MeshObjectTexture, $"mesh object {m.Uid} slot {ov.SlotId}", m.Uid);
                        }

                        if (m.Clutter is { } cl)
                        {
                            Add(cl.DebrisFilename, DependencyKind.MeshObject, $"mesh object {m.Uid} debris", m.Uid);
                            Add(cl.CorpseFilename, DependencyKind.MeshObject, $"mesh object {m.Uid} corpse", m.Uid);
                            Add(cl.CorpseStateAnim, DependencyKind.MeshAnimation, $"mesh object {m.Uid} corpse anim", m.Uid);
                        }
                    }

                    break;

                case AmbientSoundsSection sounds:
                    foreach (AmbientSound s in sounds.Sounds)
                    {
                        Add(s.SoundFileName, DependencyKind.AmbientSound, $"ambient sound {s.Uid}", s.Uid);
                    }

                    break;

                case EventsSection events:
                    foreach (RflEvent ev in events.Events)
                    {
                        GatherEvent(ev, Add);
                    }

                    break;

                case LevelPropertiesSection props:
                    Add(props.GeomodTexture, DependencyKind.GeomodTexture, "level properties (geomod)");
                    break;

                case GroupsSection groups:
                    foreach (Group g in groups.Groups.Where(g => g.MovingData is not null))
                    {
                        MovingGroupData md = g.MovingData!;
                        Add(md.StartSound, DependencyKind.MoverSound, $"moving group '{g.Name}' start");
                        Add(md.LoopingSound, DependencyKind.MoverSound, $"moving group '{g.Name}' loop");
                        Add(md.StopSound, DependencyKind.MoverSound, $"moving group '{g.Name}' stop");
                        Add(md.CloseSound, DependencyKind.MoverSound, $"moving group '{g.Name}' close");
                    }

                    break;

                case CluttersSection clutters when options.ClutterCatalog is { } cc:
                    foreach (Clutter c in clutters.Clutters)
                    {
                        Add(cc.Find(c.Header.ClassName)?.V3dFilename, DependencyKind.ClutterMesh, $"clutter {c.Header.Uid} ({c.Header.ClassName})", c.Header.Uid);
                        Add(c.Skin, DependencyKind.ClutterSkin, $"clutter {c.Header.Uid} skin", c.Header.Uid);
                    }

                    break;

                case EntitiesSection entities:
                    foreach (Entity e in entities.Entities)
                    {
                        // Mesh + skin need the entity.tbl catalog; the death/state anim
                        // strings are direct .rfa references and gathered unconditionally.
                        if (options.EntityCatalog is { } ec)
                        {
                            Add(ec.Find(e.ClassName)?.V3dFilename, DependencyKind.EntityMesh, $"entity {e.Uid} ({e.ClassName})", e.Uid);
                            Add(e.Skin, DependencyKind.EntitySkin, $"entity {e.Uid} skin", e.Uid);
                        }

                        Add(e.StateAnim, DependencyKind.MeshAnimation, $"entity {e.Uid} state anim", e.Uid);
                        Add(e.DeathAnim, DependencyKind.MeshAnimation, $"entity {e.Uid} death anim", e.Uid);
                    }

                    break;

                case ItemsSection items when options.ItemCatalog is { } ic:
                    foreach (Item it in items.Items)
                    {
                        Add(ic.Find(it.Header.ClassName)?.V3dFilename, DependencyKind.ItemMesh, $"item {it.Header.Uid} ({it.Header.ClassName})", it.Header.Uid);
                    }

                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DialogueTextFile))
        {
            Add(options.DialogueTextFile, DependencyKind.DialogueText, "level dialogue text");
        }

        return refs;
    }

    /// <summary>
    /// Gathers, resolves, and classifies every dependency against
    /// <paramref name="resolver"/>. Included meshes are expanded to their material
    /// textures and included ATX descriptors to their frame files (iteratively).
    /// </summary>
    public static DependencyScanResult Scan(
        RflFile rfl, IDependencyResolver resolver, DependencyScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        ArgumentNullException.ThrowIfNull(resolver);

        var byKey = new Dictionary<string, PackDependency>(StringComparer.OrdinalIgnoreCase);
        var order = new List<PackDependency>();
        var queue = new Queue<DependencyRef>(Gather(rfl, options));

        while (queue.Count > 0)
        {
            DependencyRef r = queue.Dequeue();
            DependencyResolution? res = resolver.Resolve(r.Kind, r.FileName);

            string key = res is null ? "missing:" + r.FileName.ToLowerInvariant() : "file:" + res.ResolvedName.ToLowerInvariant();
            if (byKey.TryGetValue(key, out PackDependency? existing))
            {
                AddReferer(existing, r);
                continue;
            }

            var dep = new PackDependency { FileName = res?.ResolvedName ?? r.FileName, Kind = r.Kind };
            AddReferer(dep, r);
            if (res is null)
            {
                dep.Status = DependencyStatus.Missing;
            }
            else
            {
                dep.Status = res.SourceKind == Assets.AssetSourceKind.Packfile
                    ? DependencyStatus.BaseGameSkipped
                    : DependencyStatus.Included;
                dep.SourceDescription = res.SourceDescription;
                dep.LoosePath = res.LoosePath;
                dep.Size = res.Size;
                dep.Read = res.Read;

                if (dep.Status == DependencyStatus.Included)
                {
                    ExpandIncluded(dep, res, queue);
                }
            }

            byKey[key] = dep;
            order.Add(dep);
        }

        return new DependencyScanResult(order);
    }

    private static void ExpandIncluded(PackDependency dep, DependencyResolution res, Queue<DependencyRef> queue)
    {
        try
        {
            if (VfsDependencyResolver.ClassOf(dep.Kind) == VfsDependencyResolver.RefClass.Mesh)
            {
                byte[]? bytes = res.Read();
                if (bytes is null)
                {
                    return;
                }

                // MeshLoader handles both V3M/V3C and VFX effect meshes; a VFX pulls
                // in its mesh/global/particle material bitmaps too.
                foreach (string tex in MeshLoader.ReferencedTextures(bytes))
                {
                    queue.Enqueue(new DependencyRef(tex, DependencyKind.MeshObjectTexture, $"mesh '{dep.FileName}'", ParentFile: dep.FileName));
                }
            }
            else if (res.ResolvedName.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? bytes = res.Read();
                if (bytes is null)
                {
                    return;
                }

                dep.Status = DependencyStatus.Included; // ensure classified as included
                AtxDescriptor atx = AtxDescriptor.Parse(System.Text.Encoding.Latin1.GetString(bytes));
                foreach (AtxFrame frame in atx.Frames)
                {
                    queue.Enqueue(new DependencyRef(frame.File, DependencyKind.AtxFrame, $"ATX '{dep.FileName}'", ParentFile: dep.FileName));
                }
            }
        }
        catch (Exception)
        {
            // Best-effort expansion: a malformed mesh/ATX simply contributes no sub-dependencies.
        }
    }

    /// <summary>The set of light-cookie filenames in the object-metadata chunk (editor-only, never packed).</summary>
    private static HashSet<string> CookieFileNames(RflFile rfl)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is not GedObjectMetadataSection meta)
            {
                continue;
            }

            foreach (GedObjectMetadataRecord entry in meta.Entries)
            {
                foreach (GedObjectMetadataBlock block in entry.Blocks)
                {
                    if (block.MetadataType == (uint)GedMetadataType.LightCookie)
                    {
                        string file = new RfReader(block.Payload).ReadVString().Trim();
                        if (file.Length > 0)
                        {
                            names.Add(file);
                        }
                    }
                }
            }
        }

        return names;
    }

    private static void GatherGeometry(Geometry geo, string origin, int? uid, Action<string?, DependencyKind, string, int?> add)
    {
        foreach (string tex in geo.Textures)
        {
            add(tex, DependencyKind.FaceTexture, origin, uid);
        }

        foreach (Room room in geo.Rooms.Where(r => r.IsLiquidRoom != 0 && r.LiquidProperties is not null))
        {
            add(room.LiquidProperties!.SurfaceTexture, DependencyKind.LiquidTexture, $"{origin} liquid room {room.Id}", uid);
        }
    }

    private static void GatherEvent(RflEvent ev, Action<string?, DependencyKind, string, int?> add)
    {
        EventSchema? schema = EventSchemaCatalog.Find(ev.ClassName);
        if (schema is null)
        {
            return;
        }

        foreach (EventFieldSpec f in schema.Fields.Where(f => f.Editor == EventEditor.FilePicker))
        {
            if (EventFieldAccess.Get(f, ev) is not string value || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            DependencyKind kind = f.FileKind switch
            {
                EventFileKind.Sound => DependencyKind.EventSound,
                EventFileKind.Bitmap => DependencyKind.EventBitmap,
                EventFileKind.Mesh => DependencyKind.EventMesh,
                EventFileKind.Vclip => DependencyKind.EventVclip,
                EventFileKind.Video => DependencyKind.EventVideo,
                EventFileKind.Animation => DependencyKind.EventAnimation,
                EventFileKind.Mvf => DependencyKind.EventMvf,
                _ => DependencyKind.EventBitmap,
            };
            add(value, kind, $"{ev.ClassName} event {ev.Uid}", ev.Uid);
        }
    }

    private static void AddReferer(PackDependency dep, DependencyRef r)
    {
        if (!dep.Origins.Contains(r.Origin))
        {
            dep.Origins.Add(r.Origin);
        }

        if (!dep.Referers.Any(x => string.Equals(x.Description, r.Origin, StringComparison.Ordinal) && x.Uid == r.Uid))
        {
            dep.Referers.Add(new DependencyReferer(r.Origin, r.Uid));
        }

        if (r.ParentFile is { } parent)
        {
            dep.Parents.Add(parent);
        }
    }
}
