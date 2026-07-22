using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Model;
using Ged.Rendering.Picking;

namespace Ged.Rendering.Scene;

/// <summary>How brush geometry should be pick-tagged and decorated for a mode.</summary>
public enum BrushPickGranularity
{
    /// <summary>Whole-brush picking (Brush / Object / Group / Texture modes).</summary>
    Brush,

    /// <summary>Per-face picking with selected-face tinting (Face mode).</summary>
    Face,

    /// <summary>Whole-brush picking plus per-vertex dot billboards (Vertex mode).</summary>
    Vertex,
}

/// <summary>
/// Emits the editable <c>brushes</c> section into a <see cref="RenderScene"/>:
/// solid textured faces (so the perspective pane previews brushes like static
/// geometry) plus coloured edge lines (the ortho wireframe, coloured by brush
/// state). Pick ids are assigned per the <see cref="BrushPickGranularity"/> and
/// recorded in a <see cref="BrushPickRegistry"/> for decode. Pure CPU work.
/// </summary>
public static class BrushEmitter
{
    /// <summary>
    /// Appends every brush to the scene and returns the pick registry. Coloured
    /// wireframe edges are always drawn (stock RED-style overlay); solid face fill
    /// is drawn only when <paramref name="solidFill"/> is set — the caller enables
    /// it when there is no compiled static geometry to show (brush-edit / live-CSG
    /// preview) so brush solids never z-fight identical compiled faces.
    /// When <paramref name="survivingFaces"/> is supplied (brush UID → per-local-
    /// face-index survival from the last build), faces whose every fragment was
    /// clipped away by the CSG solve are skipped entirely — neither drawn nor
    /// pick-registered ("Draw unmerged brushwork" OFF). Brushes without an entry
    /// (portal/detail brushes, or no build data yet) always draw in full, and a
    /// stale bitset shorter than the face list never hides the extra faces.
    /// When <paramref name="survivingFragments"/> is also supplied, a covered brush's
    /// faces are drawn as their surviving COMPILED FRAGMENTS (the real visible area)
    /// instead of the full authored polygon (item 5): a partially-clipped face shows
    /// only its surviving piece(s), a fully-clipped face shows nothing, and an
    /// unclipped face shows its single full fragment. Brushes the fragment index does
    /// not cover fall back to the authored-polygon path above.
    /// A brush whose UID is in <paramref name="staleFragmentBrushes"/> (edited since the
    /// stash was built — e.g. mid-move) ignores BOTH the survival map and the fragment
    /// index and draws its full authored polygons, so a move of one brush never reverts
    /// the OTHER brushes' fragment overlay (item 5b — per-brush staleness).
    /// </summary>
    public static BrushPickRegistry Append(
        RenderScene scene,
        IReadOnlyList<Brush> brushes,
        BrushPickGranularity granularity,
        IReadOnlyCollection<int>? selectedBrushes = null,
        bool solidFill = true,
        float vertexDotSize = 0.14f,
        IReadOnlyDictionary<int, bool[]>? survivingFaces = null,
        BrushFragmentIndex? survivingFragments = null,
        IReadOnlySet<int>? staleFragmentBrushes = null,
        PortalFaceDrawMode portalFaces = PortalFaceDrawMode.None,
        uint? portalColor = null,
        bool skyFaceAid = false,
        bool pickWholeBrush = false)
    {
        var registry = new BrushPickRegistry();
        var batches = new Dictionary<(string Tex, RenderPass Pass, bool Portal, bool Sky, bool PickOnly), GeometryBatch>();

        // Portal-brush faces in the edit-mode overlay honor the same View ▸ Portal Faces mode
        // as the compiled path (item 0e): None → NO fill drawn (wireframe edges still drawn so the
        // brush stays visible), See-thru → alpha pass with the portal tint, Non-see-thru → opaque
        // with the portal tint (a flat quad, texture dropped). Under None the portal face is still
        // emitted PICK-ONLY (see below) so authored portal brushes stay selectable in every mode.
        bool drawPortalSolid = portalFaces != PortalFaceDrawMode.None;
        RenderPass portalPass = portalFaces == PortalFaceDrawMode.SeeThru ? RenderPass.Alpha : RenderPass.Opaque;
        Vector4 portalTint = PortalTint(portalFaces, portalColor);

        foreach (Brush b in brushes)
        {
            bool selected = selectedBrushes?.Contains(b.Uid) == true;
            Matrix4x4 world = ToWorld(b.Rotation, b.Position);
            uint color = Palette.BrushStateColor(b.Flags, b.State, selected);
            Geometry g = b.Geometry;
            var edges = new HashSet<(int, int)>();

            // Whether the brush carries the Portal / Air flags. The KEY distinction (owner-reported:
            // an air+portal brush's real-textured faces were "deleted" in Brush mode while Object mode
            // showed them): an AIR portal brush and a SOLID portal brush render their authored faces
            // completely differently, because the COMPILER treats them differently.
            //  • Air|Portal: the compiler adds an air-CARVE clone of the brush to the solid operands
            //    (GeometryCompiler.Run, the `(flags & Air) != 0` branch under portalBrushes), so its
            //    real-textured faces SURVIVE the CSG solve as ordinary cavity-wall faces in the compiled
            //    static world — Object mode draws them as real textures under EVERY View ▸ Portal Faces
            //    setting. So in the authored overlay (Brush-edit / live-CSG preview, where the compiled
            //    world is suppressed) these faces MUST also draw their real textures, or they vanish in
            //    Brush mode while Object mode shows them (the reported asymmetry).
            //  • Solid Portal (no Air): a flat/solid portal brush is a boolean no-op — the compiler emits
            //    only a texture -1 membrane and NO surviving real-textured faces, so Object mode shows
            //    nothing solid. Its authored faces therefore stay portal-classified and obey View ▸ Portal
            //    Faces (None → no fill, See-thru → translucent tint, Opaque → solid tint), matching that.
            // So the per-face portal predicate below folds in the brush-level portal flag ONLY for a
            // SOLID portal brush; an air portal brush's faces are classified by their own Face.IsPortalFace
            // (false for real-textured faces), so they take the normal real-texture path.
            bool brushPortal = ((BrushFlags)b.Flags & BrushFlags.Portal) != 0;
            bool brushAir = ((BrushFlags)b.Flags & BrushFlags.Air) != 0;
            bool solidPortalBrush = brushPortal && !brushAir;

            // A portal brush stays SELECTABLE in every draw mode. A solid portal brush under None emits
            // its faces PICK-ONLY (no colour); an air portal brush's faces are drawn as real solids, which
            // are already pickable. This matches RED, whose picking is flag-AGNOSTIC: box-select (RED.exe
            // BrushList_box_select @ 0x0042adb0), face-pick (0x0042c020) and selection iteration
            // (brush_mode_handle_selection @ 0x0043f430) filter only on the state field (skip hidden=1 /
            // locked=2), never on the portal flag. (The 3-way Portal-Faces draw mode is a Glacier/Alpine
            // view addition; stock RED always draws portal brushes as wireframe.) A portal face on a
            // NON-portal brush keeps the literal view mode — its textured sibling faces already keep the
            // brush pickable, so it needs no pick-only.

            // A brush edited since the stash was built has stale world-space fragments and
            // survival bits — draw its full authored polygons until the next build refreshes
            // the stash (item 5b). Untouched brushes keep their fragment overlay.
            bool stale = staleFragmentBrushes?.Contains(b.Uid) == true;
            bool[]? survived = null;
            if (!stale)
            {
                survivingFaces?.TryGetValue(b.Uid, out survived);
            }

            // Item 5: when the fragment index covers this brush, draw each face as its
            // surviving compiled fragments (world-space) rather than the authored polygon.
            //
            // A PORTAL brush is FORCED down the authored-polygon path, never the merged fragment
            // overlay (Q2). The compiler routes portal brushes to portalBrushes and records NO
            // BrushFaceIdStart / SurvivingBrushFaces for them, so today they are never "covered" —
            // but if a stash ever DID cover a portal brush (e.g. an air+portal brush whose air-carve
            // clone got tracked), the fragment path would skip every face with no surviving fragment,
            // hiding the whole brush in brush-edit modes while it still shows via the compiled path in
            // Object mode. Pinning portal brushes to the authored path guarantees the invariant the
            // View ▸ Portal Faces contract promises in EVERY edit mode: fill governed by the mode,
            // wireframe + pickability always. (Non-portal covered brushes keep the fragment overlay.)
            bool useFragments = !stale && !brushPortal && survivingFragments is not null && survivingFragments.Covers(b.Uid);

            for (int fi = 0; fi < g.Faces.Count; fi++)
            {
                Face f = g.Faces[fi];
                if (f.Vertices.Count < 3)
                {
                    continue;
                }

                if (useFragments)
                {
                    IReadOnlyList<Face>? frags = survivingFragments!.Fragments(b.Uid, fi);
                    if (frags is null || frags.Count == 0)
                    {
                        continue; // fully clipped: nothing drawn, nothing pickable
                    }

                    uint fragPick = granularity == BrushPickGranularity.Face
                        ? new PickId(PickKind.BrushFace, registry.AddFace(b.Uid, fi)).Encode()
                        : new PickId(PickKind.Brush, b.Uid & 0x0FFFFFFF).Encode();
                    // The show_sky flag lives on the AUTHORED face (the user flags it) — the
                    // compiled fragments inherit its visible area, so classify sky per authored face.
                    bool authoredSky = skyFaceAid && ((FaceFlags)f.Flags & FaceFlags.ShowSky) != 0;
                    Geometry fg = survivingFragments.Geometry;
                    foreach (Face frag in frags)
                    {
                        if (frag.Vertices.Count < 3)
                        {
                            continue;
                        }

                        // Classify portal-ness on the COMPILED fragment, not the authored
                        // face: the compiler stamps texture -1 / portal_index_plus_2 onto the
                        // fragment, so a portal face whose authored polygon looks non-portal
                        // still yields portal fragments. Reading f.IsPortalFace here let portal
                        // fragments leak through as opaque textured quads under Portal Faces =
                        // None (item: portal faces render opaque in the fragment overlay).
                        // (Portal brushes never reach the fragment path — useFragments excludes
                        // brushPortal — so a SOLID portal brush's whole-brush fold is applied on the
                        // authored path below, not here.)
                        bool fragPortal = frag.IsPortalFace;
                        if (fragPortal)
                        {
                            if (drawPortalSolid)
                            {
                                EmitFace(scene, batches, fg, frag, Matrix4x4.Identity, color, fragPick, portalPass, isPortal: true, portalTint);
                            }
                            else if (brushPortal)
                            {
                                // Don't Draw: no fill, but keep the authored portal brush selectable.
                                EmitFace(scene, batches, fg, frag, Matrix4x4.Identity, color, fragPick, RenderPass.Opaque, isPortal: false, Vector4.One, pickOnly: true);
                            }
                        }
                        else if (authoredSky)
                        {
                            SkyFaceAid.EnsureTexture(scene);
                            EmitFace(scene, batches, fg, frag, Matrix4x4.Identity, Palette.Rgba(255, 255, 255, 255), fragPick, RenderPass.Alpha, isPortal: false, Vector4.One, isSky: true);
                        }
                        else if (solidFill)
                        {
                            EmitFace(scene, batches, fg, frag, Matrix4x4.Identity, color, fragPick, RenderPass.Opaque, isPortal: false, Vector4.One);
                        }
                        else if (pickWholeBrush)
                        {
                            // Group mode (no solid fill): keep the whole brush selectable (pick-only).
                            EmitFace(scene, batches, fg, frag, Matrix4x4.Identity, color, fragPick, RenderPass.Opaque, isPortal: false, Vector4.One, pickOnly: true);
                        }

                        EmitEdges(scene, edges, fg, frag, Matrix4x4.Identity, color);
                    }

                    continue;
                }

                if (survived is not null && fi < survived.Length && !survived[fi])
                {
                    continue; // fully clipped by the last build: not drawn, not pickable
                }

                uint pickId = granularity == BrushPickGranularity.Face
                    ? new PickId(PickKind.BrushFace, registry.AddFace(b.Uid, fi)).Encode()
                    : new PickId(PickKind.Brush, b.Uid & 0x0FFFFFFF).Encode();

                // A real-textured face is portal-classified only if it is genuinely a portal face, OR the
                // brush is a SOLID portal (a boolean no-op that yields only a membrane). An AIR portal
                // brush's real-textured faces are NOT folded in — they survive the CSG air-carve as real
                // cavity walls (Object mode), so they take the normal real-texture path in every mode.
                bool portal = f.IsPortalFace || solidPortalBrush;
                bool sky = skyFaceAid && !portal && ((FaceFlags)f.Flags & FaceFlags.ShowSky) != 0;
                if (portal)
                {
                    if (drawPortalSolid)
                    {
                        EmitFace(scene, batches, g, f, world, color, pickId, portalPass, isPortal: true, portalTint);
                    }
                    else if (brushPortal)
                    {
                        // Don't Draw: no fill, but keep the authored portal brush selectable in
                        // every mode by emitting the face into the id-buffer pick pass only.
                        EmitFace(scene, batches, g, f, world, color, pickId, RenderPass.Opaque, isPortal: false, Vector4.One, pickOnly: true);
                    }
                }
                else if (sky)
                {
                    SkyFaceAid.EnsureTexture(scene);
                    EmitFace(scene, batches, g, f, world, Palette.Rgba(255, 255, 255, 255), pickId, RenderPass.Alpha, isPortal: false, Vector4.One, isSky: true);
                }
                else if (solidFill)
                {
                    EmitFace(scene, batches, g, f, world, color, pickId, RenderPass.Opaque, isPortal: false, Vector4.One);
                }
                else if (pickWholeBrush)
                {
                    // Group mode (no solid fill): the whole brush must stay selectable as a group
                    // member — emit its faces into the pick pass only (no colour fill).
                    EmitFace(scene, batches, g, f, world, color, pickId, RenderPass.Opaque, isPortal: false, Vector4.One, pickOnly: true);
                }

                EmitEdges(scene, edges, g, f, world, color);
            }

            if (granularity == BrushPickGranularity.Vertex)
            {
                for (int vi = 0; vi < g.Vertices.Count; vi++)
                {
                    Vector3 wp = Transform(world, g.Vertices[vi]);
                    int payload = registry.AddVertex(b.Uid, vi, wp);
                    scene.Billboards.Add(new Billboard(BillboardKind.Vertex, wp, vertexDotSize,
                        Palette.Rgba(255, 255, 255), new PickId(PickKind.BrushVertex, payload)));
                }
            }
        }

        scene.Batches.AddRange(batches.Values);
        return registry;
    }

    /// <summary>Adds a face's boundary edges (deduped by shared vertex-index pair) as coloured lines.</summary>
    private static void EmitEdges(RenderScene scene, HashSet<(int, int)> edges,
        Geometry g, Face f, Matrix4x4 world, uint color)
    {
        for (int i = 0; i < f.Vertices.Count; i++)
        {
            int a = f.Vertices[i].Index;
            int c = f.Vertices[(i + 1) % f.Vertices.Count].Index;
            var key = a < c ? (a, c) : (c, a);
            if (edges.Add(key))
            {
                scene.Lines.Add(new LineSegment(
                    Transform(world, g.Vertices[a]),
                    Transform(world, g.Vertices[c]),
                    color));
            }
        }
    }

    private static void EmitFace(RenderScene scene, Dictionary<(string, RenderPass, bool, bool, bool), GeometryBatch> batches,
        Geometry g, Face f, Matrix4x4 world, uint color, uint pickId,
        RenderPass pass, bool isPortal, Vector4 tint, bool isSky = false, bool pickOnly = false)
    {
        // Portal faces render as a flat tinted quad (texture dropped); sky faces bind the
        // baked "SHOW SKY" diffuse (mapped by the face UVs); normal faces keep their texture.
        // Pick-only faces are never colour-drawn, so their texture is irrelevant (dropped).
        string tex = isPortal || pickOnly ? string.Empty
            : isSky ? SkyFaceAid.TextureKey
            : (f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty);
        var key = (tex, pass, isPortal, isSky, pickOnly);
        if (!batches.TryGetValue(key, out GeometryBatch? batch))
        {
            batch = new GeometryBatch(tex, -1, pass) { IsPortal = isPortal, IsSky = isSky, PickOnly = pickOnly, Tint = tint };
            batches[key] = batch;
        }

        Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(
            new Vector3(f.Plane.Normal.X, f.Plane.Normal.Y, f.Plane.Normal.Z), world));

        int baseVertex = batch.Vertices.Count;
        foreach (FaceVertex fv in f.Vertices)
        {
            Vector3 pos = Transform(world, g.Vertices[fv.Index]);
            batch.Vertices.Add(new WorldVertex
            {
                Position = pos,
                Normal = normal,
                TexCoord = new Vector2(fv.TextureCoords.U, fv.TextureCoords.V),
                LightmapCoord = Vector2.Zero,
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

    /// <summary>
    /// The portal-face overlay tint (RGBA 0–1): the portal-brush element colour (default teal)
    /// at alpha 0.35 for see-thru, 1.0 for non-see-thru — matching <c>SceneBuilder.PortalTint</c>.
    /// </summary>
    private static Vector4 PortalTint(PortalFaceDrawMode mode, uint? portalColor)
    {
        uint rgba = portalColor ?? Palette.Rgba(0x40, 0xE0, 0xD0, 255);
        float r = (rgba & 0xFF) / 255f;
        float gc = ((rgba >> 8) & 0xFF) / 255f;
        float b = ((rgba >> 16) & 0xFF) / 255f;
        float a = mode == PortalFaceDrawMode.SeeThru ? 0.35f : 1.0f;
        return new Vector4(r, gc, b, a);
    }

    private static Vector3 Transform(Matrix4x4 world, Vec3 local) =>
        Vector3.Transform(new Vector3(local.X, local.Y, local.Z), world);

    // world = pos + local.X·Right + local.Y·Up + local.Z·Forward (RF/REDUX convention):
    // the row-vector matrix rows are Right, Up, Forward, then the translation.
    private static Matrix4x4 ToWorld(Mat3 r, Vec3 p) => new(
        r.Right.X, r.Right.Y, r.Right.Z, 0f,
        r.Up.X, r.Up.Y, r.Up.Z, 0f,
        r.Forward.X, r.Forward.Y, r.Forward.Z, 0f,
        p.X, p.Y, p.Z, 1f);
}
