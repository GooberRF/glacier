using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;

namespace Ged.Rendering.Scene;

/// <summary>
/// Builds a CPU-side <see cref="RenderScene"/> from a parsed RFL level. Pure
/// logic with no GPU or VFS dependency: static geometry is batched by
/// (texture, lightmap page, pass); movers are pre-transformed to their
/// keyframe-0 pose; point objects become billboards (or mesh instances when a
/// catalog resolves them); links, light ranges and region outlines become lines.
/// </summary>
public static class SceneBuilder
{
    /// <summary>Builds a scene from an RFL file, parsing any not-yet-parsed sections.</summary>
    public static RenderScene Build(RflFile file, SceneBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.ParseAllKnownSections();
        options ??= new SceneBuildOptions();

        var scene = new RenderScene();

        // Lightmap atlas pages (first lightmaps section wins).
        LightmapsSection? lm = FirstContent<LightmapsSection>(file);
        scene.Lightmaps = lm?.Lightmaps ?? (IReadOnlyList<Lightmap>)Array.Empty<Lightmap>();
        int lightmapCount = scene.Lightmaps.Count;

        var batches = new Dictionary<(string Tex, int Lm, RenderPass Pass, float SU, float SV, bool Portal), GeometryBatch>();
        var uidPositions = new Dictionary<int, Vector3>();
        bool anyBounds = false;
        var boundsMin = new Vector3(float.MaxValue);
        var boundsMax = new Vector3(float.MinValue);

        void Grow(Vector3 p)
        {
            boundsMin = Vector3.Min(boundsMin, p);
            boundsMax = Vector3.Max(boundsMax, p);
            anyBounds = true;
        }

        // 1. Static world geometry.
        GeometrySection? staticGeo = options.IncludeStaticGeometry ? FirstContent<GeometrySection>(file) : null;
        if (staticGeo is not null)
        {
            EmitGeometry(scene, staticGeo.Geometry, Matrix4x4.Identity, isMover: false, moverUid: 0,
                lightmapCount, options, batches, Grow, options.VisibleRooms);
        }

        // 2. Movers, at keyframe-0 transform.
        if (options.IncludeMovers)
        {
            MoversSection? movers = FirstContent<MoversSection>(file);
            if (movers is not null)
            {
                foreach (Brush mover in movers.Movers)
                {
                    Matrix4x4 world = ToWorld(mover.Rotation, mover.Position);
                    EmitGeometry(scene, mover.Geometry, world, isMover: true, mover.Uid,
                        lightmapCount, options, batches, Grow, visibleRooms: null);
                }
            }
        }

        scene.Batches.AddRange(batches.Values);

        // 3. Point objects: billboards, meshes, links, ranges, outlines.
        if (options.IncludeObjects)
        {
            EmitObjects(file, options, scene, uidPositions);
            if (options.IncludeLinks)
            {
                EmitLinks(file, uidPositions, scene, options.LinkColor ?? Palette.Rgba(255, 220, 80, 200));
            }

            if (options.ShowBoundingBoxes)
            {
                EmitBoundingBoxes(scene, uidPositions, options.BillboardSize,
                    options.BoundingBoxColor ?? Palette.Rgba(120, 200, 255, 220));
            }

            if (options.ShowPathNodeConnections)
            {
                EmitPathNodeConnections(file, scene, options.PathNodeColor ?? Palette.Rgba(80, 220, 120, 220));
            }
        }

        // Decal projection preview ("Draw Decals", perspective-only, default off): project each
        // decal's texture onto the compiled static geometry it faces. Only runs when the compiled
        // geometry is actually being drawn (so the projection has a surface to sit on), and only
        // here on a scene rebuild — never per frame.
        if (options.DrawDecals && staticGeo is not null)
        {
            DecalsSection? decalsSection = FirstContent<DecalsSection>(file);
            if (decalsSection is { Decals.Count: > 0 })
            {
                DecalProjectionBuilder.Append(scene, staticGeo.Geometry, decalsSection.Decals);
            }
        }

        // Bounds + a framed starting camera.
        if (!anyBounds)
        {
            boundsMin = new Vector3(-10f);
            boundsMax = new Vector3(10f);
        }

        scene.Bounds = new Aabb(new Vec3(boundsMin.X, boundsMin.Y, boundsMin.Z),
                                new Vec3(boundsMax.X, boundsMax.Y, boundsMax.Z));
        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        Vector3 size = boundsMax - boundsMin;
        float radius = MathF.Max(size.Length() * 0.5f, 1f);

        // Prefer the player start as the camera anchor when present.
        PlayerStartSection? start = FirstContent<PlayerStartSection>(file);
        if (start is not null)
        {
            var sp = new Vector3(start.Position.X, start.Position.Y, start.Position.Z);
            scene.SuggestedCameraPosition = sp + (new Vector3(0f, 1.5f, 0f));
            scene.SuggestedCameraTarget = sp + new Vector3(start.Rotation.Forward.X,
                start.Rotation.Forward.Y, start.Rotation.Forward.Z);
        }
        else
        {
            scene.SuggestedCameraPosition = center + new Vector3(radius, radius * 0.6f, radius);
            scene.SuggestedCameraTarget = center;
        }

        return scene;
    }

    private static void EmitGeometry(
        RenderScene scene,
        Geometry g,
        Matrix4x4 world,
        bool isMover,
        int moverUid,
        int lightmapCount,
        SceneBuildOptions options,
        Dictionary<(string, int, RenderPass, float, float, bool), GeometryBatch> batches,
        Action<Vector3> grow,
        HashSet<int>? visibleRooms)
    {
        bool identity = world.IsIdentity;

        // Per-face UV scroll velocities (liquid conveyor etc.), keyed by compiled face
        // id. Prefer the modern face_scroll_data; fall back to the legacy table.
        Dictionary<int, (float U, float V)>? scroll = BuildScrollLookup(g);

        for (int faceIndex = 0; faceIndex < g.Faces.Count; faceIndex++)
        {
            Face f = g.Faces[faceIndex];
            var flags = (FaceFlags)f.Flags;

            // Room-graph visibility filter (portal culling / current-room-only).
            if (visibleRooms is not null && !visibleRooms.Contains(f.RoomIndex))
            {
                continue;
            }

            bool isPortal = f.IsPortalFace;
            if (isPortal && options.PortalFaces == PortalFaceDrawMode.None)
            {
                continue;
            }

            if ((flags & FaceFlags.IsInvisible) != 0 && !options.IncludeInvisibleFaces)
            {
                continue;
            }

            if ((flags & FaceFlags.IsDetail) != 0 && !options.IncludeDetailFaces)
            {
                continue;
            }

            if (f.Vertices.Count < 3)
            {
                continue;
            }

            // Show-sky editor aid: a face flagged show_sky binds the baked "SHOW SKY" diffuse
            // (a semitransparent sky-blue texture with the label rasterized INTO it) mapped
            // across the face by its own UVs — the label is part of the surface. Portal wins
            // if a face is somehow both.
            bool skyAid = !isPortal && options.ShowSkyFaceAid && (flags & FaceFlags.ShowSky) != 0;

            // Portal faces go into the alpha pass (see-thru) or the opaque pass (non-see-thru)
            // with the portal-brush tint; sky faces go into the alpha pass (the texture alpha
            // fades them); normal faces classify by flag.
            RenderPass pass = isPortal
                ? (options.PortalFaces == PortalFaceDrawMode.SeeThru ? RenderPass.Alpha : RenderPass.Opaque)
                : skyAid ? RenderPass.Alpha : ClassifyPass(flags);
            int lmIndex = isPortal || skyAid ? -1 : LightmapIndexForFace(g, f, lightmapCount);

            // Portal faces drop their texture (flat tinted quad); sky faces bind the baked
            // "SHOW SKY" diffuse; normal faces keep their wall texture.
            string texName = isPortal ? string.Empty
                : skyAid ? SkyFaceAid.EnsureTexture(scene)
                : (f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty);

            (float su, float sv) = (0f, 0f);
            if (!isPortal && !skyAid && scroll is not null && scroll.TryGetValue(f.FaceId, out (float U, float V) sc))
            {
                (su, sv) = sc;
            }

            // Scroll velocity + the portal flag are part of the batch key so faces that
            // scroll at different rates never merge, and portal batches stay isolated. Sky
            // faces key on the baked sky diffuse in the alpha pass — distinct from portal
            // batches and wall-textured faces.
            var key = (texName, lmIndex, pass, su, sv, isPortal);
            if (!batches.TryGetValue(key, out GeometryBatch? batch))
            {
                batch = new GeometryBatch(texName, lmIndex, pass) { ScrollU = su, ScrollV = sv };
                if (isPortal)
                {
                    batch.IsPortal = true;
                    batch.Tint = PortalTint(options);
                }
                else if (skyAid)
                {
                    batch.IsSky = true;
                }

                batches[key] = batch;
            }

            // Sky faces render their baked diffuse at full strength (white vertex colour) so
            // the sky-blue tint + label read true; the texture's alpha does the fade.
            uint color = skyAid
                ? Palette.Rgba(255, 255, 255, 255)
                : Palette.RoomColor(isMover ? -1 : f.RoomIndex);
            uint pickId = isMover
                ? new PickId(PickKind.Brush, moverUid & 0x0FFFFFFF).Encode()
                : new PickId(PickKind.Face, faceIndex).Encode();

            Vector3 normal = new(f.Plane.Normal.X, f.Plane.Normal.Y, f.Plane.Normal.Z);
            if (!identity)
            {
                normal = Vector3.Normalize(Vector3.TransformNormal(normal, world));
            }

            int baseVertex = batch.Vertices.Count;
            foreach (FaceVertex fv in f.Vertices)
            {
                Vector3 local = VertexAt(g, fv.Index);
                Vector3 pos = identity ? local : Vector3.Transform(local, world);
                grow(pos);

                Uv lmUv = fv.LightmapCoords ?? default;
                batch.Vertices.Add(new WorldVertex
                {
                    Position = pos,
                    Normal = normal,
                    TexCoord = new Vector2(fv.TextureCoords.U, fv.TextureCoords.V),
                    LightmapCoord = new Vector2(lmUv.U, lmUv.V),
                    Color = color,
                    PickId = pickId,
                });
            }

            for (int i = 1; i < f.Vertices.Count - 1; i++)
            {
                batch.Indices.Add((uint)baseVertex);
                batch.Indices.Add((uint)(baseVertex + i));
                batch.Indices.Add((uint)(baseVertex + i + 1));
            }
        }
    }

    private static Vector3 VertexAt(Geometry g, int index)
    {
        if (index < 0 || index >= g.Vertices.Count)
        {
            return Vector3.Zero;
        }

        Vec3 v = g.Vertices[index];
        return new Vector3(v.X, v.Y, v.Z);
    }

    private static Dictionary<int, (float U, float V)>? BuildScrollLookup(Geometry g)
    {
        List<FaceScrollData> src = g.FaceScrollData.Count > 0 ? g.FaceScrollData : g.LegacyFaceScrollData;
        if (src.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<int, (float U, float V)>(src.Count);
        foreach (FaceScrollData s in src)
        {
            if (s.UVelocity != 0f || s.VVelocity != 0f)
            {
                map[s.FaceId] = (s.UVelocity, s.VVelocity);
            }
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// The portal-face tint (RGBA 0–1): the portal-brush element colour (default a
    /// teal) with alpha 0.35 for see-thru, 1.0 for non-see-thru.
    /// </summary>
    private static Vector4 PortalTint(SceneBuildOptions options)
    {
        uint rgba = options.PortalFaceColor ?? Palette.Rgba(0x40, 0xE0, 0xD0, 255);
        float r = (rgba & 0xFF) / 255f;
        float gc = ((rgba >> 8) & 0xFF) / 255f;
        float b = ((rgba >> 16) & 0xFF) / 255f;
        float a = options.PortalFaces == PortalFaceDrawMode.SeeThru ? 0.35f : 1.0f;
        return new Vector4(r, gc, b, a);
    }

    private static RenderPass ClassifyPass(FaceFlags flags)
    {
        if ((flags & FaceFlags.ShowSky) != 0)
        {
            return RenderPass.Sky;
        }

        if ((flags & FaceFlags.LiquidSurface) != 0)
        {
            return RenderPass.Liquid;
        }

        if ((flags & FaceFlags.HasAlpha) != 0)
        {
            return RenderPass.Alpha;
        }

        return RenderPass.Opaque;
    }

    private static int LightmapIndexForFace(Geometry g, Face f, int lightmapCount)
    {
        if ((f.SurfaceIndex & 0xFFFF) == 0xFFFF || f.SurfaceIndex < 0 || f.SurfaceIndex >= g.Surfaces.Count)
        {
            return -1;
        }

        int lm = g.Surfaces[f.SurfaceIndex].LightmapIndex;
        return lm >= 0 && lm < lightmapCount ? lm : -1;
    }

    private static void EmitObjects(
        RflFile file,
        SceneBuildOptions options,
        RenderScene scene,
        Dictionary<int, Vector3> uidPositions)
    {
        float size = options.BillboardSize;

        // The shared directional facing arrow (item: arrows for more types). Same fixed-size
        // orange shaft+head as the directional-event arrow, gated by the single "Show Event
        // Arrows" toggle. Emitted for oriented events, MP respawns, the Player Start and
        // directional coronas — every object with a meaningful spawn/projection facing.
        void FacingArrow(Vec3 pos, Mat3 rot)
        {
            if (options.EventFacingArrows)
            {
                scene.Lines.AddRange(OverlayBuilder.EventFacingArrow(pos, rot, size * 5f, options.EventArrowColor));
            }
        }

        // Same shared arrow, but drawn along an explicit world direction for object types whose
        // projection axis is not the orientation's forward row (a corona's cone direction lives
        // in its UP row — see the corona case below).
        void FacingArrowDir(Vec3 pos, Vec3 dir)
        {
            if (options.EventFacingArrows)
            {
                scene.Lines.AddRange(OverlayBuilder.EventFacingArrow(pos, dir, size * 5f, options.EventArrowColor));
            }
        }

        void Add(BillboardKind kind, int uid, Vec3 p) => AddIcon(kind, uid, p, IconForKind(kind));

        // Original RED icons are full-colour: emit object glyphs untinted (white).
        uint White = Palette.Rgba(255, 255, 255, 255);

        void AddIcon(BillboardKind kind, int uid, Vec3 p, EditorIcon icon)
        {
            var pos = new Vector3(p.X, p.Y, p.Z);
            uidPositions[uid] = pos;
            uint tint = options.UseOriginalIcons ? White : Palette.BillboardTint(kind);

            // A non-square ORIGINAL icon (RED's 32×64 MP-respawn, 64×32 keyframe) renders at
            // its true aspect: standard width, height scaled by h/w. The atlas cell is square
            // (the blit squishes), so Size (the HEIGHT half-extent) carries h/w while Aspect
            // (the width multiplier relative to Size) carries its inverse — width stays the
            // standard billboard size. The GPU pick pass rasterizes the same quad, so the
            // hit extent follows automatically. Drawn glyphs (square by design) are unchanged.
            float hOverW = options.UseOriginalIcons
                && options.OriginalIconAspects is { } aspects
                && aspects.TryGetValue(icon, out float a) && a > 0f ? a : 1f;

            scene.Billboards.Add(new Billboard(kind, pos, size * hOverW, tint,
                new PickId(PickKind.Object, uid & 0x0FFFFFFF), (int)icon, Aspect: 1f / hOverW));
        }

        foreach (RflSection section in file.Sections)
        {
            switch (section.Content)
            {
                case LightsSection lights:
                    EditorIcon lightIcon = lights.Type == SectionType.EditorOnlyLights
                        ? EditorIcon.LightEditorOnly : EditorIcon.Light;
                    foreach (Light l in lights.Lights)
                    {
                        AddIcon(BillboardKind.Light, l.Uid, l.Position, lightIcon);
                        if (options.IncludeLightRanges && l.Range > 0.01f && ShowRange(options, l.Uid, l.AlwaysShowRange))
                        {
                            AddSphere(scene, new Vector3(l.Position.X, l.Position.Y, l.Position.Z),
                                l.Range, Palette.Rgba(l.Color.R, l.Color.G, l.Color.B, 160));
                        }
                    }

                    break;

                case EventsSection events:
                    foreach (RflEvent e in events.Events)
                    {
                        Add(BillboardKind.Event, e.Uid, e.Position);

                        // Facing arrow for oriented events (Alpine event.cpp:1249-1263): a
                        // fixed-size glyph along the event's forward vector.
                        if (options.EventFacingArrows && RflEvent.HasFacingArrow(e.ClassName))
                        {
                            scene.Lines.AddRange(OverlayBuilder.EventFacingArrow(
                                e.Position, e.Rotation ?? Mat3.Identity, size * 5f, options.EventArrowColor));
                        }
                    }

                    break;

                case AmbientSoundsSection sounds:
                    foreach (AmbientSound s in sounds.Sounds)
                    {
                        Add(BillboardKind.AmbientSound, s.Uid, s.Position);
                    }

                    break;

                case MpRespawnPointsSection respawns:
                    foreach (MpRespawnPoint rp in respawns.Points)
                    {
                        Add(BillboardKind.Respawn, rp.Uid, rp.Position);
                        FacingArrow(rp.Position, rp.Rotation); // spawn facing
                    }

                    break;

                case ParticleEmittersSection emitters:
                    foreach (ParticleEmitter pe in emitters.Emitters)
                    {
                        Add(BillboardKind.ParticleEmitter, pe.Header.Uid, pe.Header.Position);
                    }

                    break;

                case BoltEmittersSection bolts:
                    foreach (BoltEmitter be in bolts.Emitters)
                    {
                        Add(BillboardKind.BoltEmitter, be.Header.Uid, be.Header.Position);
                    }

                    break;

                case NavPointsSection navs:
                    foreach (NavPoint np in navs.NavPoints)
                    {
                        Add(BillboardKind.NavPoint, np.Uid, np.Position);
                    }

                    break;

                case TargetsSection targets:
                    foreach (ObjectHeader t in targets.Targets)
                    {
                        Add(BillboardKind.Target, t.Uid, t.Position);
                    }

                    break;

                case CutsceneCamerasSection cams:
                    foreach (ObjectHeader c in cams.Cameras)
                    {
                        Add(BillboardKind.CutsceneCamera, c.Uid, c.Position);
                    }

                    break;

                case DecalsSection decals:
                    foreach (Decal d in decals.Decals)
                    {
                        Add(BillboardKind.Decal, d.Header.Uid, d.Header.Position);

                        // A selected decal gets ONE semi-transparent filled face on the side its
                        // projection aims at (the +forward face of the extents box); the wireframe
                        // box (drawn by the selection overlay) covers the other five edges.
                        if (options.SelectedDecalUids?.Contains(d.Header.Uid) == true)
                        {
                            EmitDecalFacingFace(scene, d);
                        }
                    }

                    break;

                case EntitiesSection entities:
                    foreach (Entity e in entities.Entities)
                    {
                        EmitClassObject(BillboardKind.Entity, e.Uid, e.Position, e.Rotation,
                            options.Entities?.Find(e.ClassName)?.V3dFilename, options, scene, Add);
                    }

                    break;

                case CluttersSection clutters:
                    foreach (Clutter c in clutters.Clutters)
                    {
                        EmitClassObject(BillboardKind.Clutter, c.Header.Uid, c.Header.Position, c.Header.Rotation,
                            options.Clutter?.Find(c.Header.ClassName)?.V3dFilename, options, scene, Add);
                    }

                    break;

                case ItemsSection items:
                    foreach (Item it in items.Items)
                    {
                        EmitClassObject(BillboardKind.Item, it.Header.Uid, it.Header.Position, it.Header.Rotation,
                            options.Items?.Find(it.Header.ClassName)?.V3dFilename, options, scene, Add);
                    }

                    break;

                case GeoRegionsSection geo:
                    foreach (GeoRegion r in geo.Regions)
                    {
                        Add(BillboardKind.Region, r.Uid, r.Position);
                        if (options.IncludeRegionOutlines && ShowRange(options, r.Uid, alwaysShow: false))
                        {
                            AddRegionOutline(scene, r.Position, r.Rotation, r.Radius, r.Width, r.Height, r.Depth,
                                options.RegionColor ?? Palette.Rgba(120, 255, 120, 200));
                        }
                    }

                    break;

                case TriggersSection triggers:
                    foreach (Trigger t in triggers.Triggers)
                    {
                        Add(BillboardKind.Trigger, t.Uid, t.Position);
                    }

                    break;

                case GasRegionsSection gasRegions:
                    foreach (GasRegion gr in gasRegions.Regions)
                    {
                        Add(BillboardKind.GasRegion, gr.Header.Uid, gr.Header.Position);
                    }

                    break;

                case ClimbingRegionsSection climbRegions:
                    foreach (ClimbingRegion cr in climbRegions.Regions)
                    {
                        Add(BillboardKind.ClimbRegion, cr.Header.Uid, cr.Header.Position);
                    }

                    break;

                case PushRegionsSection pushRegions:
                    foreach (PushRegion pr in pushRegions.Regions)
                    {
                        Add(BillboardKind.PushRegion, pr.Header.Uid, pr.Header.Position);
                    }

                    break;

                case RoomEffectsSection roomEffects:
                    foreach (RoomEffect fx in roomEffects.Effects)
                    {
                        Add(BillboardKind.RoomEffect, fx.Header.Uid, fx.Header.Position);
                    }

                    break;

                case EaxEffectsSection eaxEffects:
                    foreach (EaxEffect eax in eaxEffects.Effects)
                    {
                        Add(BillboardKind.Eax, eax.Header.Uid, eax.Header.Position);
                    }

                    break;

                case CutscenePathNodesSection pathNodes:
                    foreach (ObjectHeader node in pathNodes.Nodes)
                    {
                        Add(BillboardKind.PathNode, node.Uid, node.Position);
                    }

                    break;

                case PlayerStartSection start:
                    Add(BillboardKind.PlayerStart, 0, start.Position);
                    FacingArrow(start.Position, start.Rotation); // spawn facing
                    break;

                case AlpineMeshObjectsSection meshObjs:
                    foreach (AlpineMeshObject mo in meshObjs.Meshes)
                    {
                        var overrides = mo.TextureOverrides.Count > 0
                            ? mo.TextureOverrides.ToDictionary(o => (int)o.SlotId, o => o.Filename)
                            : null;
                        AddMesh(scene, mo.MeshFilename, mo.Orientation, mo.Position, mo.Uid, overrides, options, Add,
                            BillboardKind.Clutter);
                    }

                    break;

                case AlpineNoteObjectsSection notes:
                    foreach (AlpineNoteObject n in notes.Notes)
                    {
                        Add(BillboardKind.Note, n.Uid, n.Position);
                    }

                    break;

                case AlpineCoronaObjectsSection coronas:
                    foreach (AlpineCoronaObject c in coronas.Coronas)
                    {
                        Add(BillboardKind.Corona, c.Uid, c.Position);

                        // A corona's cone_angle is the full visibility cone; 360° = all-angle
                        // (omnidirectional) per effects.tbl, so only a real directional cone is
                        // arrowed. The cone/aim direction is stored in the orientation's UP row
                        // (verified empirically across the corpus: ceiling coronas carry
                        // Up=(0,-1,0) aiming straight down, wall coronas carry Up = the into-room
                        // normal — while the forward/right rows hold the sprite's arbitrary
                        // in-plane spin, so an arrow along forward reads sideways).
                        if (c.IsDirectional)
                        {
                            FacingArrowDir(c.Position, c.Orientation.Up);
                        }
                    }

                    break;

                case AlpineBagObjectsSection bags:
                    foreach (AlpineBagObject bag in bags.Bags)
                    {
                        Add(BillboardKind.Bag, bag.Uid, bag.Position);
                    }

                    break;

                case GroupsSection groups:
                    // Mover keyframes (in a moving group's data) render as billboards like any
                    // other object: pickable by their own UID, populating uidPositions so the
                    // keyframe/mover link lines can resolve their endpoints.
                    foreach (Group grp in groups.Groups)
                    {
                        if (grp.IsMoving == 0 || grp.MovingData is not { } data)
                        {
                            continue;
                        }

                        foreach (Keyframe k in data.Keyframes)
                        {
                            Add(BillboardKind.Keyframe, k.Uid, k.Position);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The three-way range-visualization gate: a range/region sphere is drawn only
    /// when its object is selected, its stock "Always Show Range" flag is set, or the
    /// global "Show all ranges" toggle is on (<see cref="SceneBuildOptions.ShowAllRanges"/>).
    /// Default (nothing selected, flag clear, toggle off) hides every range.
    /// </summary>
    private static bool ShowRange(SceneBuildOptions options, int uid, bool alwaysShow) =>
        options.ShowAllRanges
        || alwaysShow
        || (options.SelectedUids?.Contains(uid) ?? false);

    /// <summary>Maps a billboard category to its atlas icon (also used by the object-palette row glyphs).</summary>
    public static EditorIcon IconForKind(BillboardKind kind) => kind switch
    {
        BillboardKind.Light => EditorIcon.Light,
        BillboardKind.Event => EditorIcon.Event,
        BillboardKind.AmbientSound => EditorIcon.AmbientSound,
        BillboardKind.Respawn => EditorIcon.Respawn,
        BillboardKind.ParticleEmitter => EditorIcon.ParticleEmitter,
        BillboardKind.BoltEmitter => EditorIcon.BoltEmitter,
        BillboardKind.NavPoint => EditorIcon.NavPoint,
        BillboardKind.PlayerStart => EditorIcon.PlayerStart,
        BillboardKind.Target => EditorIcon.Target,
        BillboardKind.Item => EditorIcon.Item,
        BillboardKind.Clutter => EditorIcon.Clutter,
        BillboardKind.Entity => EditorIcon.Entity,
        BillboardKind.CutsceneCamera => EditorIcon.CutsceneCamera,
        BillboardKind.Region => EditorIcon.GeoRegion,
        BillboardKind.Trigger => EditorIcon.Trigger,
        BillboardKind.GasRegion => EditorIcon.GasRegion,
        BillboardKind.ClimbRegion => EditorIcon.ClimbRegion,
        BillboardKind.PushRegion => EditorIcon.PushRegion,
        BillboardKind.RoomEffect => EditorIcon.RoomEffect,
        BillboardKind.Eax => EditorIcon.Eax,
        BillboardKind.PathNode => EditorIcon.PathNode,
        BillboardKind.Decal => EditorIcon.Decal,
        BillboardKind.Keyframe => EditorIcon.Keyframe,
        BillboardKind.Corona => EditorIcon.Corona,
        BillboardKind.Note => EditorIcon.Note,
        BillboardKind.Bag => EditorIcon.Bag,
        _ => EditorIcon.Generic,
    };

    private static void EmitClassObject(
        BillboardKind kind,
        int uid,
        Vec3 pos,
        Mat3 rot,
        string? meshFilename,
        SceneBuildOptions options,
        RenderScene scene,
        Action<BillboardKind, int, Vec3> addBillboard)
    {
        if (!string.IsNullOrEmpty(meshFilename))
        {
            AddMesh(scene, meshFilename, rot, pos, uid, null, options, addBillboard, kind);
        }
        else
        {
            addBillboard(kind, uid, pos);
        }
    }

    private static void AddMesh(
        RenderScene scene,
        string meshFilename,
        Mat3 rot,
        Vec3 pos,
        int uid,
        IReadOnlyDictionary<int, string>? overrides,
        SceneBuildOptions options,
        Action<BillboardKind, int, Vec3> addBillboard,
        BillboardKind fallbackKind)
    {
        if (string.IsNullOrEmpty(meshFilename))
        {
            addBillboard(fallbackKind, uid, pos);
            return;
        }

        scene.Meshes.Add(new MeshInstance
        {
            MeshFilename = meshFilename,
            World = ToWorld(rot, pos),
            PickId = new PickId(PickKind.Mesh, uid & 0x0FFFFFFF),
            TextureOverrides = overrides,
        });

        // Also drop a small billboard so the object stays pickable/visible even if
        // the mesh fails to resolve in the VFS.
        addBillboard(fallbackKind, uid, pos);
    }

    private static void EmitLinks(RflFile file, Dictionary<int, Vector3> uidPositions, RenderScene scene, uint linkColor)
    {
        // Movers render as geometry (not billboards), so their positions are not in
        // uidPositions; index them from the movers section so keyframe/mover links resolve.
        var moverPositions = new Dictionary<int, Vector3>();
        MoversSection? moversSection = FirstContent<MoversSection>(file);
        if (moversSection is not null)
        {
            foreach (Brush m in moversSection.Movers)
            {
                moverPositions[m.Uid] = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
            }
        }

        bool ResolvePos(int uid, out Vector3 pos) =>
            uidPositions.TryGetValue(uid, out pos) || moverPositions.TryGetValue(uid, out pos);

        // A single directed link line from source to destination, with an arrowhead at the
        // destination end (feature: every link line points at its destination handle).
        void Edge(int fromUid, int toUid)
        {
            if (ResolvePos(fromUid, out Vector3 a) && ResolvePos(toUid, out Vector3 b))
            {
                scene.Lines.Add(new LineSegment(a, b, linkColor));
                OverlayBuilder.AddArrowHead(scene.Lines, a, b, linkColor);
            }
        }

        void Link(int fromUid, IReadOnlyList<int> links)
        {
            foreach (int to in links)
            {
                Edge(fromUid, to);
            }
        }

        foreach (RflSection section in file.Sections)
        {
            switch (section.Content)
            {
                case EventsSection events:
                    foreach (RflEvent e in events.Events)
                    {
                        Link(e.Uid, e.Links);
                    }

                    break;

                case TriggersSection triggers:
                    foreach (Trigger t in triggers.Triggers)
                    {
                        Link(t.Uid, t.Links);
                    }

                    break;

                case CluttersSection clutters:
                    foreach (Clutter c in clutters.Clutters)
                    {
                        Link(c.Header.Uid, c.Links);
                    }

                    break;

                case NavPointsSection navs:
                    foreach (NavPoint np in navs.NavPoints)
                    {
                        Link(np.Uid, np.Links);
                    }

                    break;

                case GroupsSection groups:
                    // Mover keyframe links: each member mover -> the start keyframe, and the
                    // keyframe sequence chain (shared with the Link Graph panel via MovingGroupLinks).
                    foreach ((int from, int to) in Ged.Core.Editing.MovingGroupLinks.Edges(groups.Groups))
                    {
                        Edge(from, to);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Emits the selected decal's facing face: ONE filled, semi-transparent quad on the +forward
    /// side of the extents box (the side the projection aims at, matching the shared forward-axis
    /// facing convention), rendered like a flat portal-face quad (empty texture + tint in the alpha
    /// pass). The remaining five box faces stay wireframe (drawn by the selection overlay). Carries
    /// no pick id and no <c>IsPortal</c> flag, so it never intercepts clicks.
    /// </summary>
    private static void EmitDecalFacingFace(RenderScene scene, Decal d)
    {
        Vec3 ext = d.Extents.LengthSquared() > 1e-4f ? d.Extents : new Vec3(1f, 1f, 0.2f);
        Mat3 rot = d.Header.Rotation;
        var center = new Vector3(d.Header.Position.X, d.Header.Position.Y, d.Header.Position.Z);
        var right = new Vector3(rot.Right.X, rot.Right.Y, rot.Right.Z);
        var up = new Vector3(rot.Up.X, rot.Up.Y, rot.Up.Z);
        var forward = new Vector3(rot.Forward.X, rot.Forward.Y, rot.Forward.Z);
        Vector3 rx = right * (ext.X * 0.5f);
        Vector3 ry = up * (ext.Y * 0.5f);
        Vector3 faceCenter = center + (forward * (ext.Z * 0.5f)); // +forward = projection-facing side
        Vector3 n = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.UnitZ;

        var batch = new GeometryBatch(string.Empty, -1, RenderPass.Alpha)
        {
            // Flat decal-orange quad at 0.4 alpha — the non-see-thru portal-face rendering style,
            // made semi-transparent so the boxed geometry still reads through the highlight.
            Tint = new Vector4(255f / 255f, 180f / 255f, 60f / 255f, 0.4f),
        };
        Span<Vector3> corners = stackalloc Vector3[4]
        {
            faceCenter - rx - ry,
            faceCenter + rx - ry,
            faceCenter + rx + ry,
            faceCenter - rx + ry,
        };
        uint white = Palette.Rgba(255, 255, 255, 255);
        foreach (Vector3 c in corners)
        {
            batch.Vertices.Add(new WorldVertex
            {
                Position = c,
                Normal = n,
                TexCoord = Vector2.Zero,
                LightmapCoord = Vector2.Zero,
                Color = white,
                PickId = 0,
            });
        }

        batch.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
        scene.Batches.Add(batch);
    }

    private static void EmitBoundingBoxes(RenderScene scene, Dictionary<int, Vector3> uidPositions, float size, uint color)
    {
        float half = MathF.Max(size, 0.2f);
        foreach (Vector3 p in uidPositions.Values)
        {
            AddBox(scene, p, null, new Vector3(half * 2f, half * 2f, half * 2f), color);
        }
    }

    private static void EmitPathNodeConnections(RflFile file, RenderScene scene, uint lineColor)
    {
        NavPointsSection? nav = FirstContent<NavPointsSection>(file);
        if (nav is null)
        {
            return;
        }

        var pos = new Dictionary<int, Vector3>();
        var directional = new Dictionary<int, bool>();
        foreach (NavPoint n in nav.NavPoints)
        {
            pos[n.Uid] = new Vector3(n.Position.X, n.Position.Y, n.Position.Z);
            directional[n.Uid] = n.Directional != 0;
        }

        uint arrowColor = Palette.Rgba(160, 255, 160, 255);
        foreach (NavPoint n in nav.NavPoints)
        {
            if (!pos.TryGetValue(n.Uid, out Vector3 a))
            {
                continue;
            }

            foreach (int to in n.Links)
            {
                if (!pos.TryGetValue(to, out Vector3 b))
                {
                    continue;
                }

                scene.Lines.Add(new LineSegment(a, b, lineColor));

                // A directional nav point draws an arrowhead near the target (J-cycle: directional).
                if (directional.GetValueOrDefault(n.Uid))
                {
                    AddArrowHead(scene, a, b, arrowColor);
                }
            }
        }
    }

    private static void AddArrowHead(RenderScene scene, Vector3 from, Vector3 to, uint color) =>
        OverlayBuilder.AddArrowHead(scene.Lines, from, to, color);

    private static void AddRegionOutline(RenderScene scene, Vec3 pos, Mat3? rot, float? radius,
        float? width, float? height, float? depth, uint color)
    {
        var center = new Vector3(pos.X, pos.Y, pos.Z);
        if (radius is float r && r > 0.01f)
        {
            AddSphere(scene, center, r, color);
        }
        else if (width is float w && height is float h && depth is float d)
        {
            AddBox(scene, center, rot, new Vector3(w, h, d), color);
        }
    }

    private static void AddSphere(RenderScene scene, Vector3 center, float radius, uint color)
    {
        const int seg = 24;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 prev = default;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * MathF.Tau;
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
                    scene.Lines.Add(new LineSegment(prev, p, color));
                }

                prev = p;
            }
        }
    }

    private static void AddBox(RenderScene scene, Vector3 center, Mat3? rot, Vector3 fullSize, uint color)
    {
        Vector3 h = fullSize * 0.5f;
        Matrix4x4 m = rot is Mat3 r ? ToWorld(r, new Vec3(center.X, center.Y, center.Z))
                                    : Matrix4x4.CreateTranslation(center);
        Span<Vector3> corners = stackalloc Vector3[8];
        int idx = 0;
        for (int xi = -1; xi <= 1; xi += 2)
        {
            for (int yi = -1; yi <= 1; yi += 2)
            {
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    var local = new Vector3(xi * h.X, yi * h.Y, zi * h.Z);
                    corners[idx++] = rot is null ? center + local : Vector3.Transform(local, m);
                }
            }
        }

        // 12 box edges by corner index (bit pattern xyz).
        int[,] edges =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 },
        };
        for (int e = 0; e < 12; e++)
        {
            scene.Lines.Add(new LineSegment(corners[edges[e, 0]], corners[edges[e, 1]], color));
        }
    }

    /// <summary>
    /// A world-from-local matrix (row-vector convention) matching RF/REDUX:
    /// <c>world = pos + local.X·Right + local.Y·Up + local.Z·Forward</c>, so the
    /// rows are Right, Up, Forward, then the translation. RF's identity rotation
    /// (right=+X, up=+Y, forward=+Z) therefore maps a local point to
    /// <c>pos + local</c>.
    /// </summary>
    private static Matrix4x4 ToWorld(Mat3 r, Vec3 p) => new(
        r.Right.X, r.Right.Y, r.Right.Z, 0f,
        r.Up.X, r.Up.Y, r.Up.Z, 0f,
        r.Forward.X, r.Forward.Y, r.Forward.Z, 0f,
        p.X, p.Y, p.Z, 1f);

    private static T? FirstContent<T>(RflFile file)
        where T : class, IRflSectionContent
    {
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }
}
