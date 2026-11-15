using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Ged.Rendering;
using Ged.Rendering.Graphics;
using Ged.Rendering.Scene;

namespace Ged.Automation.Rendering;

/// <summary>
/// Draws a level to an RGBA image for <c>glacier-cli render</c>: frames the requested view, renders the
/// compiled geometry with the editor's own offscreen renderer (<see cref="OffscreenRenderer"/> — the
/// same, pixel-parity-gated GPU path the viewports use; nothing here changes it), then paints the
/// overlays on the CPU (<see cref="OverlayPainter"/>) and reports how pixels map to world coordinates.
/// <para>
/// <b>Plan slices</b> are an orthographic top-down view whose depth range is clamped to the storey:
/// the near plane sits at the cut height (floor y + thickness), so ceilings and everything above are
/// clipped away, and the far plane at y − below, so lower storeys do not show through. Walls are
/// vertical, so they are edge-on from above; their positions are drawn as dark cut edges where the
/// compiled faces cross the cut plane, and pixels where nothing was drawn (solid space — RF levels
/// are air carved from solid) are hatched dark. Floors keep the requested shading.
/// </para>
/// Read-only: the document is never modified.
/// </summary>
internal static class LevelRenderer
{
    /// <summary>The framing margin on each side, as a fraction of the framed extent.</summary>
    private const float Margin = 0.05f;

    /// <summary>The plan clear colour — a sentinel for "nothing drawn" (replaced by the solid hatch).</summary>
    private static readonly Vector4 PlanSentinel = new(1f, 0f, 1f, 1f);

    public static (byte[] Rgba, RenderResult Result) Render(
        GraphicsDevice gd,
        string deviceName,
        EditorDocument doc,
        AssetVfs? vfs,
        RenderSpec spec,
        IReadOnlyList<Vec3>? holes,
        RenderGeometryDto geometryState,
        string outPath)
    {
        ArgumentNullException.ThrowIfNull(gd);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(spec);

        Geometry? geo = LevelOperations.CompiledGeometry(doc);
        IReadOnlyList<Brush>? movers = doc.Rfl.Sections.Select(s => s.Content).OfType<MoversSection>().FirstOrDefault()?.Movers;
        var index = new LevelGeometryIndex(geo, movers);
        int w = spec.Width, h = spec.Height;
        bool plan = spec.View == RenderView.Plan;
        bool ortho = spec.View is not (RenderView.Iso or RenderView.Camera);
        var notes = new List<string>();

        // ---- framing --------------------------------------------------------------------------
        (Vector3 boxMin, Vector3 boxMax, string framedOn, Vector3? anchor, string anchorName, float? roomFloor) =
            Frame(doc, geo, index, spec);

        // ---- plan slice heights -----------------------------------------------------------------
        float planY = 0f, cut = 0f, bottom = 0f;
        string ySource = string.Empty;
        if (plan)
        {
            (planY, ySource) = PlanHeight(spec, index, anchor, anchorName, roomFloor, boxMin);
            cut = planY + spec.Thickness;
            bottom = planY - spec.Below;

            // A whole-level plan frames the storey: the geometry reaching into the slice.
            if (framedOn == "level" && index.BoundsInBand(bottom, cut) is (Vector3 bmin, Vector3 bmax))
            {
                (boxMin, boxMax) = (bmin, bmax);
                framedOn = "level (geometry in the slice)";
            }

            // A region plan shows the part of the region the slice has geometry in: a region drawn round several storeys
            // (or the whole level) does not leave the storey a corner of an empty frame.
            if (framedOn == "region" && index.BoundsInBand(bottom, cut) is (Vector3 gmin, Vector3 gmax))
            {
                var lo = new Vector3(MathF.Max(boxMin.X, gmin.X), boxMin.Y, MathF.Max(boxMin.Z, gmin.Z));
                var hi = new Vector3(MathF.Min(boxMax.X, gmax.X), boxMax.Y, MathF.Min(boxMax.Z, gmax.Z));
                if (hi.X - lo.X >= 1f && hi.Z - lo.Z >= 1f && (lo != boxMin || hi != boxMax))
                {
                    (boxMin, boxMax) = (lo, hi);
                    framedOn = "region (geometry in the slice)";
                }
            }
        }

        // An orthographic view (plan, top, ...) without a fixed image size takes the framed box's proportions — the
        // region, the room, or the storey's or level's geometry — so a long, narrow level or region fills the frame
        // instead of showing as a strip in an empty square.
        if (ortho && !spec.SizeGiven)
        {
            (w, h) = FitToRegion(OrthoOf(spec.View), boxMin, boxMax, Math.Max(spec.Width, spec.Height));
        }

        // ---- camera -------------------------------------------------------------------------------
        var cam = new Camera { AspectRatio = (float)w / h };
        float? fovDeg = null;
        float? floorUnderCamera = null;
        Vector3? look = null;
        if (ortho)
        {
            cam.Projection = CameraProjection.Orthographic;
            cam.Ortho = OrthoOf(spec.View);
            Vector3 r = cam.Right, u = cam.Up, f = cam.Forward;
            (float rMin, float rMax) = Extent(boxMin, boxMax, r);
            (float uMin, float uMax) = Extent(boxMin, boxMax, u);
            float extR = MathF.Max(rMax - rMin, 0.5f), extU = MathF.Max(uMax - uMin, 0.5f);
            float half = MathF.Max(extU * 0.5f, extR * 0.5f / cam.AspectRatio) * (1f + (2f * Margin));
            cam.OrthoZoom = MathF.Max(half, 0.5f);
            Vector3 boxCenter = (boxMin + boxMax) * 0.5f;
            Vector3 center = (r * ((rMin + rMax) * 0.5f)) + (u * ((uMin + uMax) * 0.5f)) + (f * Vector3.Dot(f, boxCenter));
            if (plan)
            {
                // Eye = Position + OrthoEyeDistance up: the near plane at exactly the cut height.
                center.Y = cut;
                cam.OrthoDepthRange = (Camera.OrthoEyeDistance, Camera.OrthoEyeDistance + spec.Thickness + spec.Below);
            }

            cam.Position = center;

            // Make room for the legend when the level would fill the frame under it: shrink the level
            // into the image beside (or below) the legend panel, whichever keeps it larger.
            if ((spec.Overlays & RenderOverlays.Legend) != 0)
            {
                PlanSlice? slice = plan ? new PlanSlice(planY, cut, bottom, index, Array.Empty<bool>()) : null;
                var estimator = new OverlayPainter(new RgbaCanvas(w, h, new byte[w * h * 4]), cam, spec, ortho, slice, doc, index, holes);
                string axes = $"{AxisName(r).Name} right, {AxisName(u).Name} up, 1px={Num3(cam.WorldPerPixel(Vector3.Zero, h))}m";
                var title = new List<string>
                {
                    plan ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PLAN y={planY:0.0#} ({bottom:0.0#}..{cut:0.0#})") : $"{spec.View.ToString().ToUpperInvariant()} (orthographic)",
                    axes,
                    "seethrough, geometry preview",
                };
                (int lw, int lh, int pad) = estimator.EstimateLegend(title, movers is { Count: > 0 });
                float wpp = cam.WorldPerPixel(Vector3.Zero, h);
                float boxW = extR * (1f + (2f * Margin)) / wpp, boxH = extU * (1f + (2f * Margin)) / wpp;

                // The legend fits beside the level, or over a corner of the frame the level leaves empty (an L- or
                // S-shaped level); only otherwise is the level shrunk into the rest of the image.
                bool fits = (w - boxW) / 2f >= lw + (2 * pad) || (h - boxH) / 2f >= lh + (2 * pad)
                    || FreeCorner(cam, w, h, lw, lh, pad, plan ? index.Polygons(bottom, cut) : index.Polygons(), GlyphPoints(doc, spec, plan, bottom, cut));
                int columnW = w - lw - (2 * pad), bandH = h - lh - (2 * pad);
                if (!fits && columnW > w / 3 && bandH > h / 3)
                {
                    float scale = 1f + (2f * Margin);
                    float wppColumn = MathF.Max(extR * scale / columnW, extU * scale / h);
                    float wppBand = MathF.Max(extR * scale / w, extU * scale / bandH);
                    if (wppColumn <= wppBand)
                    {
                        cam.OrthoZoom = wppColumn * h * 0.5f;
                        cam.Position = center - (r * ((lw + (2 * pad)) * 0.5f * wppColumn));
                    }
                    else
                    {
                        cam.OrthoZoom = wppBand * h * 0.5f;
                        cam.Position = center + (u * ((lh + (2 * pad)) * 0.5f * wppBand));
                    }
                }
            }
        }
        else if (spec.View == RenderView.Iso)
        {
            // Frame what is drawn: the level's geometry (its sky room left out), or the geometry inside the region (the
            // rest is cut away), else the framed box's corners — fitted on their projection, not a bounding sphere.
            float isoFov = spec.HFovDegrees is float isoH ? RenderSpec.VerticalFov(isoH, w, h) : spec.FovDegrees ?? 45f;
            float fv = isoFov * (MathF.PI / 180f);
            List<Vector3> points = IsoPoints(index, spec, framedOn, boxMin, boxMax);
            (Vector3 c, float dist, float near, float far) = FitIso(cam, fv, points);
            cam.NearPlane = near;
            cam.FarPlane = far;
            fovDeg = isoFov;
            look = c;
            if (spec.Region is not null)
            {
                notes.Add("The iso view shows the region only: geometry and objects outside it are cut away.");
            }
        }
        else
        {
            Vector3 eye = spec.CameraPosition!.Value, at = spec.CameraLook!.Value;
            if (spec.EyeHeight is float eyeHeight)
            {
                // The camera's Y is the search start: stand on the highest floor at or below it, keep the pitch.
                if (index.FloorBelow(eye) is float floor)
                {
                    float dy = floor + eyeHeight - eye.Y;
                    eye.Y += dy;
                    at.Y += dy;
                    floorUnderCamera = floor;
                    notes.Add($"The camera stands {eyeHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} m above the floor at y={floor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} (eyeHeight).");
                }
                else
                {
                    notes.Add("eyeHeight: no floor lies under the camera position, so the camera stays where it was given.");
                }
            }

            float fov = spec.HFovDegrees is float hfov ? RenderSpec.VerticalFov(hfov, w, h) : spec.FovDegrees ?? RenderSpec.DefaultFov;
            cam.FieldOfView = fov * (MathF.PI / 180f);
            cam.LookAt(eye, at);
            cam.NearPlane = 0.1f;
            float reach = index.HasGeometry
                ? MathF.Max(Vector3.Distance(eye, index.Min), Vector3.Distance(eye, index.Max)) * 1.5f
                : 6000f;
            cam.FarPlane = MathF.Max(6000f, reach);
            fovDeg = fov;
            look = at;
        }

        // ---- GPU render ----------------------------------------------------------------------------
        // The sky room is the level's skybox, drawn through show-sky faces (see SkyComposite); it is not part of the
        // playable level any view frames or shows.
        HashSet<int>? skyRooms = SkyRooms(geo);
        GameCatalogs catalogs = !plan && vfs is not null ? GameCatalogs.Load(vfs) : GameCatalogs.Empty;
        var options = new SceneBuildOptions
        {
            // Plan: the CPU glyphs stand in for objects (camera-facing billboards seen from above would
            // only clutter the floor plan). Other views draw objects with a game mesh as that mesh, and
            // the editor's icons only when asked (spec.Icons).
            IncludeObjects = !plan,
            ObjectIcons = spec.Icons,
            IncludeLinks = false,
            IncludeLightRanges = false,
            IncludeRegionOutlines = false,
            EventFacingArrows = !plan && spec.Icons,
            Clutter = catalogs.Clutter,
            Items = catalogs.Items,
            Entities = catalogs.Entities,
            ExcludedRooms = skyRooms,

            // Seen from outside the level (plan / ortho / iso), show_sky faces would roof the interior
            // over with their wall texture (the sky pass is never back-face culled).
            IncludeSkyFaces = spec.View == RenderView.Camera,
        };
        if (!plan && vfs is not null)
        {
            // Clutter, entities and items draw as their meshes (from the mounted game tables), as in the editor.
            Ged.Core.Tables.GameCatalogs tables = Ged.Core.Tables.GameCatalogs.Load(vfs);
            options.Clutter = tables.Clutter;
            options.Entities = tables.Entities;
            options.Items = tables.Items;
        }
        RenderScene scene = SceneBuilder.Build(doc.Rfl, options);
        if (spec.View == RenderView.Iso && spec.Region is (Vector3 clipMin, Vector3 clipMax))
        {
            ClipToBox(scene, clipMin, clipMax);
        }

        bool hasLightmaps = scene.Lightmaps.Count > 0 && LightmapsCarryLight(scene.Lightmaps);
        (RenderMode mode, string modeUsed, string? modeNote) = ResolveMode(spec.Mode, hasLightmaps, vfs is not null);
        if (scene.Lightmaps.Count > 0 && !hasLightmaps && modeNote is not null)
        {
            modeNote = "The level's lightmaps carry no baked light (built without lighting). " + modeNote;
        }
        byte[] rgba = OffscreenRenderer.Render(gd, scene, vfs, cam, mode, w, h, fog: null, time: 0f,
            disableBackfaceCulling: false, clearColor: plan ? PlanSentinel : null);
        string? sky = spec.View == RenderView.Camera ? SkyComposite(gd, doc, geo, vfs, scene, cam, mode, w, h, rgba, skyRooms) : null;

        var canvas = new RgbaCanvas(w, h, rgba);
        int wallSegments = 0;
        if (plan)
        {
            // Second pass without the bottom clip: what lies below where the slice drew nothing.
            cam.OrthoDepthRange = (Camera.OrthoEyeDistance, Camera.OrthoEyeDistance + 9000f);
            byte[] deep = OffscreenRenderer.Render(gd, scene, vfs, cam, mode, w, h, fog: null, time: 0f,
                disableBackfaceCulling: false, clearColor: PlanSentinel);
            cam.OrthoDepthRange = (Camera.OrthoEyeDistance, Camera.OrthoEyeDistance + spec.Thickness + spec.Below);
            (bool[] solid, int openPixels) = CompositePlan(canvas, deep, cam, index, cut);
            bool openBelow = openPixels > 0;
            var painterPlan = new PlanSlice(planY, cut, bottom, index, solid);
            (var segs, var moverSegs) = index.CutSegments(cut);
            wallSegments = segs.Count + moverSegs.Count;
            var painter = new OverlayPainter(canvas, cam, spec, ortho, painterPlan, doc, index, holes);
            painter.DrawCutEdges(segs, moverSegs, openBelow ? Hex(OpenBelowTint) : null);
            return Finish(painter);
        }

        return Finish(new OverlayPainter(canvas, cam, spec, ortho, null, doc, index, holes));

        (byte[], RenderResult) Finish(OverlayPainter painter)
        {
            painter.DrawOverlays(TitleLines());
            if (!index.HasGeometry)
            {
                notes.Add("The level has no compiled geometry to draw (no brushes, or the build was skipped).");
            }

            if (modeNote is not null)
            {
                notes.Add(modeNote);
            }

            float wpp = ortho ? cam.WorldPerPixel(Vector3.Zero, h) : cam.WorldPerPixel(look ?? cam.Position, h);
            Vector3 right = cam.Right, up = cam.Up;
            double[]? origin = null;
            RenderPixelAxesDto? axes = null;
            string mapping;
            if (ortho)
            {
                float halfH = cam.OrthoZoom, halfW = halfH * cam.AspectRatio;
                Vector3 o = cam.Position - (right * halfW) + (up * halfH);
                if (plan)
                {
                    o.Y = planY;
                }

                origin = VSig(o);
                axes = new RenderPixelAxesDto { X = VSig(right * wpp), Y = VSig(-up * wpp) };
                (string ah, int hi) = AxisName(right);
                (string av, int vi) = AxisName(up);
                mapping = $"world = origin + (px + 0.5) * pixelAxes.x + (py + 0.5) * pixelAxes.y; image right = {ah}, image up = {av}; " +
                          $"{"xyz"[hi]} = {Sig(o.Get(hi)).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(right.Get(hi) >= 0 ? "+" : "-")} (px + 0.5) * {Sig(wpp).ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                          $"{"xyz"[vi]} = {Sig(o.Get(vi)).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(up.Get(vi) >= 0 ? "-" : "+")} (py + 0.5) * {Sig(wpp).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }
            else
            {
                mapping = "perspective: project with viewProjection ([x, y, z, 1] · M, then divide by w); px = (ndcX * 0.5 + 0.5) * width, py = (0.5 - ndcY * 0.5) * height";
            }

            Matrix4x4 m = cam.ViewProjectionMatrix;
            var result = new RenderResult
            {
                Path = outPath,
                Width = w,
                Height = h,
                View = spec.View.ToString().ToLowerInvariant(),
                Projection = ortho ? "orthographic" : "perspective",
                Mode = spec.Mode,
                ModeUsed = modeUsed,
                ModeNote = modeNote,
                Textures = vfs is not null,
                Camera = new RenderCameraDto
                {
                    Position = V(cam.Position),
                    Forward = V(cam.Forward),
                    Right = V(right),
                    Up = V(up),
                    Look = look is Vector3 lk ? V(lk) : null,
                    FovDegrees = fovDeg,
                    HFovDegrees = fovDeg is float vf ? Dto.R(RenderSpec.HorizontalFov(vf, w, h)) : null,
                    Floor = floorUnderCamera is float fl ? Dto.R(fl) : null,
                    OrthoHalfHeight = ortho ? Dto.R(cam.OrthoZoom) : null,
                },
                WorldPerPixel = Sig(wpp),
                Origin = origin,
                PixelAxes = axes,
                Mapping = mapping,
                ViewProjection = new[]
                {
                    Sig(m.M11), Sig(m.M12), Sig(m.M13), Sig(m.M14),
                    Sig(m.M21), Sig(m.M22), Sig(m.M23), Sig(m.M24),
                    Sig(m.M31), Sig(m.M32), Sig(m.M33), Sig(m.M34),
                    Sig(m.M41), Sig(m.M42), Sig(m.M43), Sig(m.M44),
                },
                Plan = plan ? new RenderPlanDto { Y = Dto.R(planY), YSource = ySource, Cut = Dto.R(cut), Bottom = Dto.R(bottom), WallSegments = wallSegments } : null,
                Framing = new RenderBoxDto { Min = V(boxMin), Max = V(boxMax) },
                FramedOn = framedOn,
                Overlays = RenderSpec.Names(painter.Drawn),
                Icons = plan ? null : spec.Icons,
                Sky = sky,
                Legend = painter.Legend,
                Layout = (painter.LegendRect, painter.GridLabels),
                Objects = painter.Objects.Count > 0 ? painter.Objects.Take(RenderResult.ObjectsLimit).ToList() : null,
                ObjectsTruncated = painter.Objects.Count > RenderResult.ObjectsLimit ? true : null,
                Holes = (spec.Overlays & RenderOverlays.Holes) != 0 ? holes?.Count ?? 0 : null,
                Geometry = geometryState,
                Device = deviceName,
                Notes = notes.Count > 0 ? notes : null,
            };
            return (canvas.Pixels, result);
        }

        List<string> TitleLines()
        {
            var lines = new List<string>();
            string Num(float v) => v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
            if (plan)
            {
                lines.Add($"PLAN y={Num(planY)} ({Num(bottom)}..{Num(cut)})");
            }
            else if (ortho)
            {
                lines.Add($"{spec.View.ToString().ToUpperInvariant()} (orthographic)");
            }
            else
            {
                lines.Add($"{(spec.View == RenderView.Iso ? "ISO" : "CAMERA")} (perspective, fov {Num(fovDeg ?? 60f)})");
            }

            if (ortho)
            {
                (string ah, _) = AxisName(cam.Right);
                (string av, _) = AxisName(cam.Up);
                lines.Add($"{ah} right, {av} up, 1px={Num3(cam.WorldPerPixel(Vector3.Zero, h))}m");
            }

            lines.Add($"{modeUsed}, geometry {geometryState.Rendered}");
            return lines;
        }
    }

    /// <summary>The framing box, what it is, the default-height anchor point and a room's floor.</summary>
    private static (Vector3 Min, Vector3 Max, string FramedOn, Vector3? Anchor, string AnchorName, float? RoomFloor) Frame(
        EditorDocument doc, Geometry? geo, LevelGeometryIndex index, RenderSpec spec)
    {
        if (spec.Region is (Vector3 rmin, Vector3 rmax))
        {
            Vector3 c = (rmin + rmax) * 0.5f;
            return (rmin, rmax, "region", new Vector3(c.X, rmax.Y, c.Z), "the region centre", null);
        }

        if (spec.Room is int room)
        {
            if (geo is null || geo.Rooms.Count == 0)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "The level has no compiled rooms to frame (build it first, or omit --room).");
            }

            if (room >= geo.Rooms.Count)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"Room {room} does not exist: the compiled geometry has {geo.Rooms.Count} rooms (0-{geo.Rooms.Count - 1}).");
            }

            Aabb a = geo.Rooms[room].Aabb;
            Vector3 lo = Vector3.Min(ToV(a.P1), ToV(a.P2)), hi = Vector3.Max(ToV(a.P1), ToV(a.P2));
            return (lo, hi, $"room {room}", null, string.Empty, lo.Y);
        }

        if (spec.Uid is int uid)
        {
            LevelObject? obj = doc.Objects.FirstOrDefault(o => o.Uid == uid);
            if (obj is not null)
            {
                Vector3 p = ToV(obj.Position);
                return (p - new Vector3(10f, 4f, 10f), p + new Vector3(10f, 4f, 10f), $"uid {uid}", p, $"object {uid}", null);
            }

            Brush? brush = LevelOperations.Brushes(doc).FirstOrDefault(b => b.Uid == uid);
            if (brush is not null && brush.Geometry.Vertices.Count > 0)
            {
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (Vec3 v in brush.Geometry.Vertices)
                {
                    Vector3 wv = ToV(BrushTransform.WorldVertex(brush, v));
                    lo = Vector3.Min(lo, wv);
                    hi = Vector3.Max(hi, wv);
                }

                Vector3 pad = new(2f, 1f, 2f);
                Vector3 c = (lo + hi) * 0.5f;
                return (lo - pad, hi + pad, $"uid {uid}", new Vector3(c.X, hi.Y, c.Z), $"brush {uid}", null);
            }

            throw new AutomationException(AutomationErrorCodes.Usage, $"No object or brush has UID {uid}.");
        }

        (Vector3 min, Vector3 max) = LevelBounds(doc, index);
        (Vector3? anchor, string name) = DefaultAnchor(doc);
        return (min, max, "level", anchor, name, null);
    }

    /// <summary>The level bounds: compiled geometry, else object positions, else a 20 m box.</summary>
    private static (Vector3 Min, Vector3 Max) LevelBounds(EditorDocument doc, LevelGeometryIndex index)
    {
        if (index.HasGeometry)
        {
            return (index.Min, index.Max);
        }

        if (doc.Objects.Count > 0)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (LevelObject o in doc.Objects)
            {
                Vector3 p = ToV(o.Position);
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }

            return (lo - new Vector3(5f), hi + new Vector3(5f));
        }

        return (new Vector3(-10f), new Vector3(10f));
    }

    /// <summary>The player start, else the lowest-UID multiplayer spawn.</summary>
    private static (Vector3? Anchor, string Name) DefaultAnchor(EditorDocument doc)
    {
        LevelObject? start = doc.Objects.FirstOrDefault(o => o.Kind == LevelObjectKind.PlayerStart);
        if (start is not null)
        {
            return (ToV(start.Position), "the player start");
        }

        LevelObject? spawn = doc.Objects.Where(o => o.Kind == LevelObjectKind.MpRespawnPoint).OrderBy(o => o.Uid).FirstOrDefault();
        return spawn is not null ? (ToV(spawn.Position), $"MP spawn {spawn.Uid}") : (null, string.Empty);
    }

    /// <summary>The plan's floor height and where it came from.</summary>
    private static (float Y, string Source) PlanHeight(
        RenderSpec spec, LevelGeometryIndex index, Vector3? anchor, string anchorName, float? roomFloor, Vector3 boxMin)
    {
        if (spec.PlanY is float y)
        {
            return (y, "y argument");
        }

        if (roomFloor is float rf)
        {
            return (rf, "lowest point of the framed room");
        }

        if (anchor is Vector3 a)
        {
            if (index.FloorBelow(a) is float floor)
            {
                return (floor, $"floor under {anchorName}");
            }

            return (a.Y, $"height of {anchorName} (no floor found under it)");
        }

        if (index.LowestFloor() is float low)
        {
            return (low, "lowest floor in the level");
        }

        return (boxMin.Y, "bottom of the level bounds");
    }

    /// <summary>
    /// True when the lightmap pages hold baked light: more than two distinct texel colours. A build
    /// without a lighting bake leaves flat pages (one fill colour, plus unused padding), which would
    /// draw every surface uniformly dark.
    /// </summary>
    private static bool LightmapsCarryLight(IReadOnlyList<Lightmap> pages)
    {
        var seen = new HashSet<int>();
        foreach (Lightmap page in pages)
        {
            byte[] p = page.Pixels;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                if (seen.Add(p[i] | (p[i + 1] << 8) | (p[i + 2] << 16)) && seen.Count > 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Maps the requested mode to a renderer mode, falling back when its data is missing.</summary>
    internal static (RenderMode Mode, string Used, string? Note) ResolveMode(string mode, bool hasLightmaps, bool hasTextures) => mode switch
    {
        "lit" when hasLightmaps => (RenderMode.TexturesAndLightmaps, "lit", hasTextures ? null : "No game textures are mounted: lit surfaces show lightmaps over white."),
        "lit" when hasTextures => (RenderMode.JustTextures, "textured", "The level has no lightmaps (not lit yet): drew textures only."),
        "lit" => (RenderMode.RoomColors, "rooms", "No lightmaps and no game textures are mounted: drew each room in its own colour."),
        "textured" => (RenderMode.JustTextures, "textured", hasTextures ? null : "No game textures are mounted: surfaces are untextured (white)."),
        "lightmaps" => (RenderMode.JustLightmaps, "lightmaps", hasLightmaps ? null : "The level has no lightmaps: surfaces show neutral light."),
        "rooms" => (RenderMode.RoomColors, "rooms", null),
        "wireframe" => (RenderMode.Wireframe, "wireframe", null),
        "seethrough" => (RenderMode.SeeThrough, "seethrough", null),
        _ => throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown mode '{mode}'."),
    };

    /// <summary>The tint that dims floors seen through an opening below the slice.</summary>
    internal static readonly (byte R, byte G, byte B) OpenBelowTint = (22, 34, 64);

    /// <summary>
    /// Fills the plan's "nothing drawn in the slice" pixels: where a vertical probe from the cut plane
    /// starts in air (an opening — e.g. a ledge over a lower hall), the lower floor from the deep pass
    /// is shown dimmed; elsewhere (solid space, or outside the level) the dark solid hatch. Returns the
    /// solid mask and how many pixels were open to below.
    /// </summary>
    private static (bool[] Solid, int Open) CompositePlan(RgbaCanvas canvas, byte[] deep, Camera cam, LevelGeometryIndex index, float cut)
    {
        int w = canvas.Width, h = canvas.Height;
        var solid = new bool[w * h];
        byte[] px = canvas.Pixels;
        int period = Math.Max(6, Math.Min(w, h) / 128);
        float wpp = cam.WorldPerPixel(Vector3.Zero, h);
        Vector3 right = cam.Right * wpp, down = -cam.Up * wpp;
        float halfH = cam.OrthoZoom, halfW = halfH * cam.AspectRatio;
        Vector3 origin = cam.Position - (cam.Right * halfW) + (cam.Up * halfH);
        (byte tr, byte tg, byte tb) = OpenBelowTint;
        int open = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                if (!(px[i] == 255 && px[i + 1] == 0 && px[i + 2] == 255))
                {
                    continue;
                }

                bool deepDrawn = !(deep[i] == 255 && deep[i + 1] == 0 && deep[i + 2] == 255);
                if (deepDrawn)
                {
                    Vector3 world = origin + (right * (x + 0.5f)) + (down * (y + 0.5f));
                    if (index.IsAirBelow(world.X, world.Z, cut))
                    {
                        px[i] = (byte)(((deep[i] * 45) + (tr * 55)) / 100);
                        px[i + 1] = (byte)(((deep[i + 1] * 45) + (tg * 55)) / 100);
                        px[i + 2] = (byte)(((deep[i + 2] * 45) + (tb * 55)) / 100);
                        px[i + 3] = 255;
                        open++;
                        continue;
                    }
                }

                solid[(y * w) + x] = true;
                bool hatch = ((x + y) % period) < Math.Max(1, period / 6);
                px[i] = hatch ? (byte)58 : (byte)36;
                px[i + 1] = hatch ? (byte)60 : (byte)38;
                px[i + 2] = hatch ? (byte)70 : (byte)44;
                px[i + 3] = 255;
            }
        }

        return (solid, open);
    }

    private static string Hex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The orthographic camera view of a render view (plan = top).</summary>
    private static OrthoView OrthoOf(RenderView view) => view switch
    {
        RenderView.Bottom => OrthoView.Bottom,
        RenderView.Front => OrthoView.Front,
        RenderView.Back => OrthoView.Back,
        RenderView.Left => OrthoView.Left,
        RenderView.Right => OrthoView.Right,
        _ => OrthoView.Top,
    };

    /// <summary>The compiled rooms flagged as sky rooms (the level's skybox), or null when there are none.</summary>
    private static HashSet<int>? SkyRooms(Geometry? geo)
    {
        if (geo is null)
        {
            return null;
        }

        var set = new HashSet<int>();
        for (int i = 0; i < geo.Rooms.Count; i++)
        {
            if (geo.Rooms[i].IsSkyroom != 0)
            {
                set.Add(i);
            }
        }

        return set.Count > 0 ? set : null;
    }

    /// <summary>Where the object glyphs (and their labels) of an orthographic view's overlays will sit: object positions, in the plan's band.</summary>
    private static List<Vector3> GlyphPoints(EditorDocument doc, RenderSpec spec, bool plan, float bottom, float cut)
    {
        var points = new List<Vector3>();
        if ((spec.Overlays & (RenderOverlays.Objects | RenderOverlays.Labels)) == 0)
        {
            return points;
        }

        foreach (LevelObject o in doc.Objects)
        {
            Vector3 p = ToV(o.Position);
            if (!plan || (p.Y >= bottom - 1f && p.Y <= cut + 1f))
            {
                points.Add(p);
            }
        }

        return points;
    }

    /// <summary>
    /// True when one corner of the image can hold the legend panel (<paramref name="lw"/> × <paramref name="lh"/> with its
    /// padding and the grid-label strips) over nothing the view draws: no geometry polygon and no object glyph or label
    /// projects under it. Polygons are rasterized on an 8-pixel grid (their outlines too, so an edge-on wall counts).
    /// </summary>
    internal static bool FreeCorner(Camera cam, int w, int h, int lw, int lh, int pad, IEnumerable<Vector3[]> polygons, IEnumerable<Vector3> glyphs)
    {
        const int Cell = 8;
        int gw = (w + Cell - 1) / Cell, gh = (h + Cell - 1) / Cell;
        var occ = new bool[gw * gh];
        Matrix4x4 vp = cam.ViewProjectionMatrix;
        Vector2 Px(Vector3 p)
        {
            Vector4 c = Vector4.Transform(new Vector4(p, 1f), vp);
            float iw = MathF.Abs(c.W) < 1e-6f ? 1f : 1f / c.W;
            return new Vector2(((c.X * iw * 0.5f) + 0.5f) * w, (0.5f - (c.Y * iw * 0.5f)) * h);
        }

        void Mark(float x, float y)
        {
            int cx = (int)MathF.Floor(x / Cell), cy = (int)MathF.Floor(y / Cell);
            if (cx >= 0 && cy >= 0 && cx < gw && cy < gh)
            {
                occ[(cy * gw) + cx] = true;
            }
        }

        foreach (Vector3[] poly in polygons)
        {
            var pts = new Vector2[poly.Length];
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < poly.Length; i++)
            {
                pts[i] = Px(poly[i]);
                x0 = MathF.Min(x0, pts[i].X);
                y0 = MathF.Min(y0, pts[i].Y);
                x1 = MathF.Max(x1, pts[i].X);
                y1 = MathF.Max(y1, pts[i].Y);
            }

            if (x1 < 0 || y1 < 0 || x0 > w || y0 > h)
            {
                continue;
            }

            // The outline (an edge-on wall projects to a line).
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
                int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / (Cell * 0.5f)));
                for (int k = 0; k <= steps; k++)
                {
                    Vector2 q = Vector2.Lerp(a, b, k / (float)steps);
                    Mark(q.X, q.Y);
                }
            }

            // The inside: cell centres within the (convex) polygon.
            int cx0 = Math.Max(0, (int)MathF.Floor(x0 / Cell)), cx1 = Math.Min(gw - 1, (int)MathF.Floor(x1 / Cell));
            int cy0 = Math.Max(0, (int)MathF.Floor(y0 / Cell)), cy1 = Math.Min(gh - 1, (int)MathF.Floor(y1 / Cell));
            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    var c = new Vector2((cx + 0.5f) * Cell, (cy + 0.5f) * Cell);
                    int sign = 0;
                    bool inside = true;
                    for (int i = 0; i < pts.Length && inside; i++)
                    {
                        Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
                        float cross = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
                        int sg = cross > 1e-3f ? 1 : cross < -1e-3f ? -1 : 0;
                        if (sg != 0)
                        {
                            inside = sign == 0 || sg == sign;
                            sign = sg;
                        }
                    }

                    if (inside && sign != 0)
                    {
                        occ[(cy * gw) + cx] = true;
                    }
                }
            }
        }

        // A glyph and its label (to its right).
        foreach (Vector3 g in glyphs)
        {
            Vector2 p = Px(g);
            for (float dx = -Cell; dx <= 14 * Cell; dx += Cell)
            {
                for (float dy = -Cell; dy <= Cell; dy += Cell)
                {
                    Mark(p.X + dx, p.Y + dy);
                }
            }
        }

        // The legend panel with its padding and the grid-label strips along the top and left edges.
        int rw = lw + (3 * pad) + 48, rh = lh + (3 * pad) + 16;
        foreach ((int x, int y) in new[] { (0, 0), (w - rw, 0), (0, h - rh), (w - rw, h - rh) })
        {
            bool free = true;
            for (int cy = Math.Max(0, y / Cell); cy <= Math.Min(gh - 1, (y + rh) / Cell) && free; cy++)
            {
                for (int cx = Math.Max(0, x / Cell); cx <= Math.Min(gw - 1, (x + rw) / Cell); cx++)
                {
                    if (occ[(cy * gw) + cx])
                    {
                        free = false;
                        break;
                    }
                }
            }

            if (free)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The points an iso view frames: the drawn geometry's vertices (sky room left out; inside the region when one is
    /// given), else the framed box's corners.
    /// </summary>
    private static List<Vector3> IsoPoints(LevelGeometryIndex index, RenderSpec spec, string framedOn, Vector3 boxMin, Vector3 boxMax)
    {
        var points = new List<Vector3>();
        if (framedOn is "level" or "region" && index.HasGeometry)
        {
            foreach (Vector3[] poly in index.Polygons())
            {
                foreach (Vector3 v in poly)
                {
                    if (framedOn == "level" || (v.X >= boxMin.X && v.X <= boxMax.X && v.Y >= boxMin.Y && v.Y <= boxMax.Y && v.Z >= boxMin.Z && v.Z <= boxMax.Z))
                    {
                        points.Add(v);
                    }
                }
            }

            // A big level: every n-th vertex frames it as well.
            if (points.Count > 40000)
            {
                int step = (points.Count / 40000) + 1;
                points = points.Where((_, i) => i % step == 0).ToList();
            }
        }

        if (points.Count < 4)
        {
            points.Clear();
            for (int i = 0; i < 8; i++)
            {
                points.Add(new Vector3((i & 1) != 0 ? boxMax.X : boxMin.X, (i & 2) != 0 ? boxMax.Y : boxMin.Y, (i & 4) != 0 ? boxMax.Z : boxMin.Z));
            }
        }

        return points;
    }

    /// <summary>
    /// Places the iso camera (from the south-west, looking north-east and down) so <paramref name="points"/> fill about
    /// 90% of the frame and sit centred in it: the look-at point and distance are refined on the points' projection.
    /// Returns the look-at point, the distance and near / far planes that hold every point.
    /// </summary>
    internal static (Vector3 Look, float Dist, float Near, float Far) FitIso(Camera cam, float fov, IReadOnlyList<Vector3> points)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (Vector3 p in points)
        {
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }

        Vector3 c = (lo + hi) * 0.5f;
        float radius = MathF.Max((hi - lo).Length() * 0.5f, 1f);
        Vector3 dir = Vector3.Normalize(new Vector3(-1f, 1.1f, -1f));
        float fit = MathF.Min(fov, 2f * MathF.Atan(MathF.Tan(fov * 0.5f) * cam.AspectRatio));
        float dist = radius / MathF.Sin(fit * 0.5f);
        cam.FieldOfView = fov;
        cam.NearPlane = 0.1f;
        cam.FarPlane = dist + (radius * 2f);
        cam.LookAt(c + (dir * dist), c);
        for (int iter = 0; iter < 10; iter++)
        {
            Matrix4x4 vp = cam.ViewProjectionMatrix;
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            bool behind = false;
            foreach (Vector3 p in points)
            {
                Vector4 clip = Vector4.Transform(new Vector4(p, 1f), vp);
                if (clip.W <= 1e-3f)
                {
                    behind = true;
                    break;
                }

                float nx = clip.X / clip.W, ny = clip.Y / clip.W;
                x0 = MathF.Min(x0, nx);
                x1 = MathF.Max(x1, nx);
                y0 = MathF.Min(y0, ny);
                y1 = MathF.Max(y1, ny);
            }

            if (behind)
            {
                dist *= 1.5f;
                cam.LookAt(c + (dir * dist), c);
                continue;
            }

            // Centre the projection, then scale the distance to the extent (perspective: close enough to iterate).
            float halfH = dist * MathF.Tan(fov * 0.5f), halfW = halfH * cam.AspectRatio;
            c += (cam.Right * ((x0 + x1) * 0.5f * halfW)) + (cam.Up * ((y0 + y1) * 0.5f * halfH));
            float extent = MathF.Max((x1 - x0) * 0.5f, (y1 - y0) * 0.5f);
            float next = Math.Clamp(dist * extent / 0.9f, dist * 0.3f, dist * 3f);
            bool done = MathF.Abs(extent - 0.9f) < 0.01f && MathF.Abs((x0 + x1) * 0.5f) < 0.01f && MathF.Abs((y0 + y1) * 0.5f) < 0.01f;
            dist = next;
            cam.LookAt(c + (dir * dist), c);
            if (done)
            {
                break;
            }
        }

        Vector3 f = cam.Forward;
        float dMin = float.MaxValue, dMax = float.MinValue;
        foreach (Vector3 p in points)
        {
            float d = Vector3.Dot(p - cam.Position, f);
            dMin = MathF.Min(dMin, d);
            dMax = MathF.Max(dMax, d);
        }

        float near = MathF.Max(0.1f, dMin * 0.9f), far = MathF.Max(near + 1f, dMax * 1.1f);
        return (c, dist, near, far);
    }

    /// <summary>
    /// Cuts the scene down to a box (an iso view of a region): triangles, meshes, icons and lines wholly outside it are
    /// dropped, so nearer parts of the level do not hide the region.
    /// </summary>
    private static void ClipToBox(RenderScene scene, Vector3 min, Vector3 max)
    {
        const float Eps = 0.01f;
        bool Inside(Vector3 p) => p.X >= min.X - Eps && p.X <= max.X + Eps && p.Y >= min.Y - Eps && p.Y <= max.Y + Eps && p.Z >= min.Z - Eps && p.Z <= max.Z + Eps;
        foreach (GeometryBatch b in scene.Batches)
        {
            var kept = new List<uint>(b.Indices.Count);
            for (int i = 0; i + 2 < b.Indices.Count; i += 3)
            {
                Vector3 p0 = b.Vertices[(int)b.Indices[i]].Position, p1 = b.Vertices[(int)b.Indices[i + 1]].Position, p2 = b.Vertices[(int)b.Indices[i + 2]].Position;
                Vector3 lo = Vector3.Min(p0, Vector3.Min(p1, p2)), hi = Vector3.Max(p0, Vector3.Max(p1, p2));
                if (hi.X >= min.X - Eps && lo.X <= max.X + Eps && hi.Y >= min.Y - Eps && lo.Y <= max.Y + Eps && hi.Z >= min.Z - Eps && lo.Z <= max.Z + Eps)
                {
                    kept.Add(b.Indices[i]);
                    kept.Add(b.Indices[i + 1]);
                    kept.Add(b.Indices[i + 2]);
                }
            }

            b.Indices.Clear();
            b.Indices.AddRange(kept);
        }

        scene.Meshes.RemoveAll(m => !Inside(m.World.Translation));
        scene.Billboards.RemoveAll(bb => !Inside(bb.Position));
        scene.Lines.RemoveAll(l => !Inside(l.A) && !Inside(l.B));
    }

    /// <summary>The colour of a show-sky face in the sky mask (a value no lit, textured or mesh pixel of the mask takes).</summary>
    private static readonly (byte R, byte G, byte B) SkyKey = (13, 254, 7);

    /// <summary>The flat sky drawn through show-sky faces when the level has no sky room (top and horizon).</summary>
    internal static readonly (byte R, byte G, byte B) SkyFillTop = (74, 108, 158), SkyFillHorizon = (158, 180, 206);

    /// <summary>
    /// Draws the sky through the show-sky faces of a camera view, as the game does: the faces are found with a second,
    /// flat-shaded pass of the same scene (show-sky faces in a key colour, everything else black; see-through and liquid
    /// surfaces left out, so the sky shows through glass), and those pixels take the sky room drawn from its centre with
    /// the camera's orientation (RF's sky-room render: the room's bounding-box centre is the eye) — or, without a sky
    /// room, a flat sky colour. Returns what was drawn ("sky room N" / "fill"), or null when no show-sky face is in view.
    /// </summary>
    private static string? SkyComposite(GraphicsDevice gd, EditorDocument doc, Geometry? geo, AssetVfs? vfs, RenderScene scene, Camera cam,
        RenderMode mode, int w, int h, byte[] rgba, HashSet<int>? skyRooms)
    {
        if (!scene.Batches.Any(b => b.Pass == RenderPass.Sky && b.Indices.Count > 0))
        {
            return null;
        }

        uint key = Ged.Rendering.Scene.Palette.Rgba(SkyKey.R, SkyKey.G, SkyKey.B);
        uint black = Ged.Rendering.Scene.Palette.Rgba(0, 0, 0);
        var mask = new RenderScene();
        foreach (GeometryBatch b in scene.Batches)
        {
            if (b.PickOnly || b.Indices.Count == 0 || b.Pass is RenderPass.Alpha or RenderPass.Liquid)
            {
                continue;
            }

            uint color = b.Pass == RenderPass.Sky ? key : black;
            var m = new GeometryBatch(string.Empty, -1, RenderPass.Opaque);
            foreach (WorldVertex v in b.Vertices)
            {
                m.Vertices.Add(v with { Color = color });
            }

            m.Indices.AddRange(b.Indices);
            mask.Batches.Add(m);
        }

        mask.Meshes.AddRange(scene.Meshes);
        mask.Billboards.AddRange(scene.Billboards);
        mask.Lines.AddRange(scene.Lines);
        byte[] maskPx = OffscreenRenderer.Render(gd, mask, vfs, cam, RenderMode.RoomColors, w, h, fog: null, time: 0f,
            disableBackfaceCulling: false, clearColor: new Vector4(0f, 0f, 0f, 1f));
        bool any = false;
        for (int i = 0; i < maskPx.Length && !any; i += 4)
        {
            any = maskPx[i] == SkyKey.R && maskPx[i + 1] == SkyKey.G && maskPx[i + 2] == SkyKey.B;
        }

        if (!any)
        {
            return null;
        }

        byte[]? skyPx = null;
        string drawn = "fill";
        int skyRoom = skyRooms is { Count: > 0 } ? skyRooms.Min() : -1;
        if (geo is not null && skyRoom >= 0)
        {
            var skyScene = SceneBuilder.Build(doc.Rfl, new SceneBuildOptions
            {
                VisibleRooms = new HashSet<int> { skyRoom },
                IncludeObjects = false,
                IncludeMovers = false,
                IncludeLinks = false,
                IncludeLightRanges = false,
                IncludeRegionOutlines = false,
            });
            if (skyScene.Batches.Any(b => b.Indices.Count > 0))
            {
                Aabb a = geo.Rooms[skyRoom].Aabb;
                Vector3 lo = Vector3.Min(ToV(a.P1), ToV(a.P2)), hi = Vector3.Max(ToV(a.P1), ToV(a.P2));
                var skyCam = new Camera
                {
                    AspectRatio = cam.AspectRatio,
                    FieldOfView = cam.FieldOfView,
                    NearPlane = 0.05f,
                    FarPlane = MathF.Max(100f, (hi - lo).Length() * 2f),
                };
                Vector3 eye = (lo + hi) * 0.5f;
                skyCam.LookAt(eye, eye + cam.Forward);
                (byte r, byte g, byte bl) = SkyFillHorizon;
                skyPx = OffscreenRenderer.Render(gd, skyScene, vfs, skyCam, mode, w, h, fog: null, time: 0f,
                    disableBackfaceCulling: false, clearColor: new Vector4(r / 255f, g / 255f, bl / 255f, 1f));
                drawn = $"sky room {skyRoom}";
            }
        }

        for (int y = 0; y < h; y++)
        {
            float t = h > 1 ? y / (float)(h - 1) : 0f;
            byte fr = (byte)(SkyFillTop.R + ((SkyFillHorizon.R - SkyFillTop.R) * t));
            byte fg = (byte)(SkyFillTop.G + ((SkyFillHorizon.G - SkyFillTop.G) * t));
            byte fb = (byte)(SkyFillTop.B + ((SkyFillHorizon.B - SkyFillTop.B) * t));
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                if (maskPx[i] != SkyKey.R || maskPx[i + 1] != SkyKey.G || maskPx[i + 2] != SkyKey.B)
                {
                    continue;
                }

                if (skyPx is not null)
                {
                    rgba[i] = skyPx[i];
                    rgba[i + 1] = skyPx[i + 1];
                    rgba[i + 2] = skyPx[i + 2];
                }
                else
                {
                    rgba[i] = fr;
                    rgba[i + 1] = fg;
                    rgba[i + 2] = fb;
                }

                rgba[i + 3] = 255;
            }
        }

        return drawn;
    }


    /// <summary>
    /// The image size for a region seen through <paramref name="view"/>: the longest side <paramref name="longSide"/>,
    /// the other in the region's proportions (at least a third of the longest, so the legend and labels still fit).
    /// </summary>
    internal static (int Width, int Height) FitToRegion(OrthoView view, Vector3 min, Vector3 max, int longSide)
    {
        var probe = new Camera { Projection = CameraProjection.Orthographic, Ortho = view };
        (float rMin, float rMax) = Extent(min, max, probe.Right);
        (float uMin, float uMax) = Extent(min, max, probe.Up);
        float extR = MathF.Max(rMax - rMin, 0.5f), extU = MathF.Max(uMax - uMin, 0.5f);
        float aspect = Math.Clamp(extR / extU, 1f / 3f, 3f);
        int Short(float ratio) => Math.Max(16, (int)MathF.Round(longSide * ratio));
        return aspect >= 1f ? (longSide, Short(1f / aspect)) : (Short(aspect), longSide);
    }

    private static (float Min, float Max) Extent(Vector3 min, Vector3 max, Vector3 axis)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            float d = Vector3.Dot(c, axis);
            lo = MathF.Min(lo, d);
            hi = MathF.Max(hi, d);
        }

        return (lo, hi);
    }

    /// <summary>The signed world-axis name a unit direction is closest to ("+X", "-Z", …) and its index.</summary>
    internal static (string Name, int Index) AxisName(Vector3 d)
    {
        int i = MathF.Abs(d.X) >= MathF.Abs(d.Y) && MathF.Abs(d.X) >= MathF.Abs(d.Z) ? 0 : MathF.Abs(d.Y) >= MathF.Abs(d.Z) ? 1 : 2;
        return ((d.Get(i) >= 0 ? "+" : "-") + "XYZ"[i], i);
    }

    private static float Get(this Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    private static string Num3(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Rounds to 7 significant digits (matrix entries / scale factors need more than 3 decimals).</summary>
    internal static double Sig(double v)
    {
        if (v == 0 || !double.IsFinite(v))
        {
            return 0;
        }

        double r = double.Parse(v.ToString("G7", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        return r == 0 ? 0 : r;
    }

    private static double[] V(Vector3 v) => new[] { Dto.R(v.X), Dto.R(v.Y), Dto.R(v.Z) };

    /// <summary>A vector at 7 significant digits (pixel mapping terms must not lose sub-pixel precision).</summary>
    private static double[] VSig(Vector3 v) => new[] { Sig(v.X), Sig(v.Y), Sig(v.Z) };

    internal static Vector3 ToV(Vec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>The plan slice a painter draws over.</summary>
internal sealed record PlanSlice(float Y, float Cut, float Bottom, LevelGeometryIndex Index, bool[] Solid);
