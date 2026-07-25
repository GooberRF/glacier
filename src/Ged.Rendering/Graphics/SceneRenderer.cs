using System.Numerics;
using Ged.Rendering.Picking;
using Ged.Rendering.Rhi;
using Ged.Rendering.Scene;

namespace Ged.Rendering.Graphics;

/// <summary>
/// Draws a <see cref="GpuScene"/> into a render target for a given camera and
/// render mode, and runs the id-buffer pick pass. All targets (swapchain,
/// offscreen readback, pick) and both live and offscreen rendering share this one
/// code path, which issues every GPU operation through the <see cref="IRenderContext"/>.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    /// <summary>The transparent passes, in draw order. A static readonly array so the per-frame
    /// <see cref="Render"/> loop never allocates one (GC pressure on the render hot path shows up as
    /// intermittent camera-orbit hitches).</summary>
    private static readonly RenderPass[] TransparentPasses = { RenderPass.Sky, RenderPass.Liquid, RenderPass.Alpha };

    private readonly GraphicsDevice _gd;
    private readonly IGpuBuffer _frameCb;
    private readonly IGpuBuffer _drawCb;

    public SceneRenderer(GraphicsDevice gd)
    {
        _gd = gd;
        _frameCb = gd.CreateConstantBuffer(160);
        _drawCb = gd.CreateConstantBuffer(96);
    }

    /// <summary>The background clear color (dark editor grey).</summary>
    public Vector4 ClearColor { get; set; } = new(0.10f, 0.11f, 0.13f, 1f);

    /// <summary>Distance-fog settings for the world/mesh passes (off by default).</summary>
    public FogSettings Fog { get; set; } = FogSettings.Off;

    /// <summary>
    /// When false (default) the solid world + single-sided mesh passes back-face cull
    /// (RED parity). When true they render both faces. Transparent passes (sky, liquid,
    /// alpha), wireframe, double-sided (0x20) mesh triangles and picking never cull.
    /// </summary>
    public bool DisableBackfaceCulling { get; set; }

    /// <summary>Animation clock (seconds) driving in-shader UV scroll (liquid surfaces).</summary>
    public float Time { get; set; }

    private IRenderContext Ctx => _gd.Context;

    internal void Render(
        Camera camera,
        RenderMode mode,
        GpuScene scene,
        IRenderTarget target)
    {
        IRenderContext ctx = Ctx;
        ctx.SetRenderTarget(target);
        ctx.ClearColor(target, ClearColor);
        ctx.ClearDepth(target);

        UpdateFrame(camera, mode.GlobalAlpha(), mode.ShaderBranch(), Fog);
        BindConstants();
        BindSampler();

        bool wire = mode.IsWireframe();
        bool cull = !wire && !DisableBackfaceCulling;

        // Opaque world: back-face culled (RED parity) unless wireframe / culling disabled.
        ctx.SetRasterizerState(wire ? _gd.RasterWireframe
            : cull ? _gd.RasterSolidCull
            : _gd.RasterSolid);
        SetProgram(_gd.Programs.World, pick: false);
        SetDepth(write: true);
        SetBlend(mode == RenderMode.SeeThrough);
        foreach (GpuBatch b in scene.Batches)
        {
            // Pick-only batches (invisible-but-selectable brush/portal faces) carry the Opaque
            // pass for the id-buffer, but must never draw in the colour pass.
            if (b.Pass == RenderPass.Opaque && !b.PickOnly)
            {
                DrawBatch(b);
            }
        }

        DrawMeshes(scene, mode, pick: false, cull);

        // Transparent passes (sky, liquid, alpha) with depth writes off — never back-face
        // culled (blended surfaces are commonly authored to be seen from both sides).
        ctx.SetRasterizerState(wire ? _gd.RasterWireframe : _gd.RasterSolid);
        SetProgram(_gd.Programs.World, pick: false);
        SetDepth(write: false);
        foreach (RenderPass pass in TransparentPasses)
        {
            SetBlend(pass != RenderPass.Sky || mode == RenderMode.SeeThrough);
            foreach (GpuBatch b in scene.Batches)
            {
                if (b.Pass == pass)
                {
                    DrawBatch(b);
                }
            }
        }

        // Debug geometry.
        ctx.SetRasterizerState(_gd.RasterSolid);
        DrawLines(scene);
        DrawBillboards(scene, pick: false);
    }

    /// <summary>Renders the id-buffer and reads back the pick under a pixel.</summary>
    internal PickId RenderPick(Camera camera, GpuScene scene, IPickTarget target, int px, int py)
    {
        IRenderContext ctx = Ctx;
        ctx.SetRenderTarget(target);
        ctx.ClearColor(target, Vector4.Zero);
        ctx.ClearDepth(target);

        UpdateFrame(camera, 1f, 0, FogSettings.Off);
        BindConstants();
        BindSampler();

        // Picking must use the SAME cull state as the solid world pass: with back-face
        // culling enabled (default), a click on a back-facing polygon rasterizes nothing
        // for that face, so the pick falls through to whatever is behind it. Disabling
        // culling restores pick-the-backface. The pick pass always rasterizes solid (no
        // wireframe), so the cull decision is just the culling toggle.
        bool cull = !DisableBackfaceCulling;
        ctx.SetRasterizerState(cull ? _gd.RasterSolidCull : _gd.RasterSolid);
        SetDepth(write: true);
        SetBlend(false);

        SetProgram(_gd.Programs.World, pick: true);

        // Draw the pick-only brush faces FIRST. In Group / whole-brush-select modes the editor emits a
        // brush's faces PICK-ONLY (no colour fill) so the whole brush stays selectable, while the compiled
        // static world is ALSO drawn (IncludeStaticGeometry is on outside brush-edit modes) at the exact
        // same depth — the pick-only faces ARE the surviving compiled fragments, so the two are coincident.
        // The pick pass depth-tests with strict Less; whichever draws first wins a coincident-depth pixel.
        // The compiled world carries PickKind.Face (unselectable in every mode), so if IT wins, the brush
        // is unpickable (the Group-mode "can't click a brush" bug: the id-buffer returns Face, no route
        // selects it). Drawing the pick-only faces first makes the whole-brush id win coincident pixels,
        // while a genuinely-nearer compiled wall drawn afterward still overwrites it (correct occlusion —
        // a brush behind a wall stays unpickable).
        foreach (GpuBatch b in scene.Batches)
        {
            if (b.PickOnly)
            {
                DrawBatch(b, bindTextures: false);
            }
        }

        foreach (GpuBatch b in scene.Batches)
        {
            // Opaque world + drawn portal faces (pick-only batches were drawn above). Portal batches
            // carry their own pass but stay pickable, so include them regardless of pass.
            if (!b.PickOnly && (b.Pass == RenderPass.Opaque || b.IsPortal))
            {
                DrawBatch(b, bindTextures: false);
            }
        }

        // DrawMeshes applies the per-mesh double-sided (V3M 0x20) exception itself, so
        // double-sided triangles stay pickable from both sides even when culling is on.
        DrawMeshes(scene, RenderMode.JustTextures, pick: true, cull);
        DrawBillboards(scene, pick: true);

        return PickId.Decode(target.ReadPick(px, py));
    }

    private void DrawMeshes(GpuScene scene, RenderMode mode, bool pick, bool cull)
    {
        if (scene.Meshes.Count == 0)
        {
            return;
        }

        IRenderContext ctx = Ctx;
        SetProgram(_gd.Programs.Mesh, pick);
        ctx.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
        int stride = Scene.MeshVertex.SizeInBytes;
        bool wire = mode.IsWireframe();

        void Draw(GpuMesh m)
        {
            // Cull single-sided meshes with the solid pass; the 0x20 double-sided draw
            // (all VFX effect faces) and wireframe/pick always render both faces.
            ctx.SetRasterizerState(wire ? _gd.RasterWireframe
                : cull && !m.DoubleSided ? _gd.RasterSolidCull
                : _gd.RasterSolid);

            // The Mesh shader reads HasLightmap as a fullbright flag (meshes never sample
            // a lightmap): VFX fullbright/self-illuminated draws bypass scene lighting.
            UpdateDraw(m.World, pick ? Vector4.One : m.Tint, m.PickId, hasLightmap: !pick && m.Fullbright);
            ctx.SetVertexBuffer(m.VertexBuffer, stride);
            ctx.SetIndexBuffer(m.IndexBuffer);
            if (!pick)
            {
                ctx.SetTexture(0, m.Diffuse);
            }

            ctx.DrawIndexed(m.IndexCount);
        }

        // Picking treats every mesh as opaque so effect meshes stay clickable.
        if (pick)
        {
            SetDepth(write: true);
            SetBlend(false);
            foreach (GpuMesh m in scene.Meshes)
            {
                Draw(m);
            }

            return;
        }

        // Opaque meshes (all V3M/V3C, and non-blended VFX): depth-writing solid pass.
        SetDepth(write: true);
        SetBlend(mode == RenderMode.SeeThrough);
        foreach (GpuMesh m in scene.Meshes)
        {
            if (m.Blend == MeshDrawBlend.Opaque)
            {
                Draw(m);
            }
        }

        // Blended VFX effect meshes (alpha / additive): depth-tested, no depth write, so glow/flame
        // draws layer over the scene without occluding it. A manual scan (not LINQ .Any) so the
        // per-frame render path never boxes a List enumerator (render-hot-path GC pressure).
        bool anyBlended = false;
        foreach (GpuMesh m in scene.Meshes)
        {
            if (m.Blend != MeshDrawBlend.Opaque)
            {
                anyBlended = true;
                break;
            }
        }

        if (anyBlended)
        {
            SetDepth(write: false);
            foreach (GpuMesh m in scene.Meshes)
            {
                if (m.Blend == MeshDrawBlend.Opaque)
                {
                    continue;
                }

                ctx.SetBlendState(m.Blend == MeshDrawBlend.Additive ? _gd.BlendAdditive : _gd.BlendAlpha);
                Draw(m);
            }

            SetDepth(write: true);
        }
    }

    private void DrawBatch(GpuBatch b, bool bindTextures = true)
    {
        IRenderContext ctx = Ctx;
        UpdateDraw(Matrix4x4.Identity, b.Tint, 0, b.HasLightmap, b.Scroll);
        ctx.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.SetVertexBuffer(b.VertexBuffer, Scene.WorldVertex.SizeInBytes);
        ctx.SetIndexBuffer(b.IndexBuffer);
        if (bindTextures)
        {
            ctx.SetTexture(0, b.Diffuse);
            ctx.SetTexture(1, b.Lightmap);
        }

        ctx.DrawIndexed(b.IndexCount);
    }

    private void DrawBillboards(GpuScene scene, bool pick)
    {
        // Normal billboards depth-TEST (no write) against the scene so glyphs behind geometry are
        // hidden like the objects they mark.
        DrawBillboardSet(
            scene.BillboardIndexCount, scene.BillboardVertexBuffer, scene.BillboardIndexBuffer,
            scene.ParticleGroups, pick, _gd.DepthNoWrite);

        // On-top atlas glyphs (mover keyframes) draw with the depth test disabled in BOTH the colour
        // and pick passes: drawn last with no depth test they are never occluded AND win the id-buffer
        // at their own pixels, so a keyframe seeded at the mover's rest centre — sitting inside/behind
        // its mover geometry — is still visible and pickable (RED draws editor icons as non-occluded
        // overlays). The on-top TEXTURED groups (transform-drag labels) stay colour-pass only.
        DrawBillboardSet(
            scene.BillboardOnTopIndexCount, scene.BillboardOnTopVertexBuffer, scene.BillboardOnTopIndexBuffer,
            pick ? System.Array.Empty<TexturedBillboardGroup>() : scene.OnTopGroups, pick, _gd.DepthNoTest);
    }

    private void DrawBillboardSet(
        int atlasIndexCount,
        IGpuBuffer? atlasVb,
        IGpuBuffer? atlasIb,
        IReadOnlyList<TexturedBillboardGroup> groups,
        bool pick,
        IDepthStencilState depthState)
    {
        if (atlasIndexCount == 0 && groups.Count == 0)
        {
            return;
        }

        IRenderContext ctx = Ctx;
        SetProgram(_gd.Programs.Billboard, pick);
        ctx.SetDepthStencilState(depthState);
        SetBlend(!pick);
        UpdateDraw(Matrix4x4.Identity, Vector4.One, 0, hasLightmap: false);

        int stride = Scene.BillboardVertex.SizeInBytes;
        ctx.SetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Object-glyph billboards (+ particles with an unresolved bitmap): icon atlas.
        if (atlasIndexCount > 0)
        {
            ctx.SetTexture(0, _gd.Textures.Icons);
            ctx.SetVertexBuffer(atlasVb!, stride);
            ctx.SetIndexBuffer(atlasIb!);
            ctx.DrawIndexed(atlasIndexCount);
        }

        // Particle / label billboards textured with each authored bitmap (or inline label bitmap).
        foreach (TexturedBillboardGroup g in groups)
        {
            if (g.IndexCount == 0)
            {
                continue;
            }

            ctx.SetTexture(0, g.Texture);
            ctx.SetVertexBuffer(g.VertexBuffer, stride);
            ctx.SetIndexBuffer(g.IndexBuffer);
            ctx.DrawIndexed(g.IndexCount);
        }
    }

    /// <summary>
    /// Draws an ad-hoc line list (selection highlight) over whatever is already
    /// in the currently bound target — no clear. Call immediately after
    /// <see cref="Render"/> and before Present.
    /// </summary>
    internal void DrawOverlayLines(Camera camera, IGpuBuffer? vertexBuffer, int vertexCount, bool onTop = false)
    {
        if (vertexCount == 0)
        {
            return;
        }

        IRenderContext ctx = Ctx;
        UpdateFrame(camera, 1f, 0, FogSettings.Off);
        BindConstants();
        ctx.SetRasterizerState(_gd.RasterSolid);
        SetProgram(_gd.Programs.Line, pick: false);
        // onTop = draw with depth test disabled so the gizmo is never occluded by geometry
        // in front of the selection (item 12); otherwise depth-test against the scene.
        ctx.SetDepthStencilState(onTop ? _gd.DepthNoTest : _gd.DepthNoWrite);
        SetBlend(true);
        UpdateDraw(Matrix4x4.Identity, Vector4.One, 0, hasLightmap: false);

        ctx.SetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.SetVertexBuffer(vertexBuffer!, Scene.LineVertex.SizeInBytes);
        ctx.Draw(vertexCount);
    }

    /// <summary>
    /// Draws a small independent overlay scene's billboards over the already-rendered frame — the
    /// transform-drag Δ/∠/% label, carried on its own tiny <see cref="GpuScene"/> so it updates every
    /// drag frame WITHOUT re-emitting/re-uploading the whole level scene. Call after
    /// <see cref="Render"/>, before Present. No-op for an empty overlay scene, so normal rendering
    /// (no drag) is byte-identical.
    /// </summary>
    internal void DrawOverlayBillboards(Camera camera, GpuScene scene)
    {
        if (scene.BillboardIndexCount == 0 && scene.ParticleGroups.Count == 0 &&
            scene.BillboardOnTopIndexCount == 0 && scene.OnTopGroups.Count == 0)
        {
            return;
        }

        UpdateFrame(camera, 1f, 0, FogSettings.Off);
        BindConstants();
        BindSampler();
        Ctx.SetRasterizerState(_gd.RasterSolid);
        DrawBillboards(scene, pick: false);
    }

    private void DrawLines(GpuScene scene)
    {
        if (scene.LineVertexCount == 0)
        {
            return;
        }

        IRenderContext ctx = Ctx;
        SetProgram(_gd.Programs.Line, pick: false);
        SetDepth(write: false);
        SetBlend(true);
        UpdateDraw(Matrix4x4.Identity, Vector4.One, 0, hasLightmap: false);

        ctx.SetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.SetVertexBuffer(scene.LineVertexBuffer!, Scene.LineVertex.SizeInBytes);
        ctx.Draw(scene.LineVertexCount);
    }

    private void SetProgram(IShaderProgram program, bool pick) => Ctx.SetProgram(program, pick);

    private void SetDepth(bool write) =>
        Ctx.SetDepthStencilState(write ? _gd.DepthDefault : _gd.DepthNoWrite);

    private void SetBlend(bool alpha) =>
        Ctx.SetBlendState(alpha ? _gd.BlendAlpha : _gd.BlendOpaque);

    private void BindSampler() => Ctx.SetSampler(0, _gd.Sampler);

    private void BindConstants()
    {
        IRenderContext ctx = Ctx;
        ctx.SetConstantBuffer(0, _frameCb);
        ctx.SetConstantBuffer(1, _drawCb);
    }

    private void UpdateFrame(Camera camera, float globalAlpha, int modeBranch, FogSettings fog)
    {
        var frame = new FrameConstants
        {
            ViewProj = camera.ViewProjectionMatrix,
            CameraRight = new Vector4(camera.Right, 0f),
            CameraUp = new Vector4(camera.Up, 0f),
            CameraPos = new Vector4(camera.Position, 1f),
            Params = new Vector4(globalAlpha, 2.0f, modeBranch, Time),
            FogColor = new Vector4(fog.Color, fog.Enabled ? 1f : 0f),
            FogParams = new Vector4(fog.Start, fog.End, 0f, 0f),
        };
        Ctx.UpdateConstantBuffer(_frameCb, in frame);
    }

    private void UpdateDraw(Matrix4x4 world, Vector4 tint, uint pickId, bool hasLightmap, Vector2 scroll = default)
    {
        var draw = new DrawConstants
        {
            World = world,
            Tint = tint,
            PickId = pickId,
            HasLightmap = hasLightmap ? 1u : 0u,
            Scroll = scroll,
        };
        Ctx.UpdateConstantBuffer(_drawCb, in draw);
    }

    public void Dispose()
    {
        _drawCb.Dispose();
        _frameCb.Dispose();
    }
}
