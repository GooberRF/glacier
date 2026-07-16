using System.Numerics;
using Ged.Core.Assets;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Rhi;
using Ged.Rendering.Scene;

namespace Ged.Rendering;

/// <summary>
/// A single live viewport: a swapchain on a child HWND, a camera, the current
/// render mode, and the uploaded scene. The host (Ged.App) owns one of these per
/// pane, drives <see cref="Render"/> from its render loop, and forwards input to
/// the <see cref="Camera"/>. The GPU device is shared and owned externally.
/// </summary>
public sealed class Viewport : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly SceneRenderer _renderer;
    private readonly ISwapChainTarget _surface;
    private GpuScene _gpuScene;
    private IPickTarget? _pick;
    private IGpuBuffer? _overlayVb;
    private int _overlayVertexCount;
    private IGpuBuffer? _gizmoOverlayVb;
    private int _gizmoOverlayVertexCount;

    public Viewport(GraphicsDevice gd, nint hwnd, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(gd);
        _gd = gd;
        _renderer = new SceneRenderer(gd);
        _surface = gd.CreateSwapChain(hwnd, width, height);
        _gpuScene = new GpuScene(gd, new RenderScene(), null);
        Camera.AspectRatio = (float)Math.Max(1, width) / Math.Max(1, height);
    }

    public Camera Camera { get; } = new();

    public RenderMode Mode { get; set; } = RenderMode.TexturesAndLightmaps;

    /// <summary>Distance-fog settings for the world/mesh passes (off by default).</summary>
    public FogSettings Fog
    {
        get => _renderer.Fog;
        set => _renderer.Fog = value;
    }

    /// <summary>Render both faces of solid geometry (disable RED-parity back-face culling).</summary>
    public bool DisableBackfaceCulling
    {
        get => _renderer.DisableBackfaceCulling;
        set => _renderer.DisableBackfaceCulling = value;
    }

    /// <summary>Animation clock (seconds) driving in-shader UV scroll for liquid surfaces.</summary>
    public float Time
    {
        get => _renderer.Time;
        set => _renderer.Time = value;
    }

    /// <summary>Present with vsync (default). The host can disable it for benchmarking.</summary>
    public bool VSync { get; set; } = true;

    public int Width => _surface.Width;

    public int Height => _surface.Height;

    /// <summary>Replaces the uploaded scene (resolving textures/meshes through the VFS).</summary>
    public void SetScene(RenderScene scene, AssetVfs? vfs)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _gpuScene.Dispose();
        _gpuScene = new GpuScene(_gd, scene, vfs);
        SetSelection(Array.Empty<LineSegment>());
    }

    /// <summary>Sets the selection-highlight line overlay (drawn on top of the scene).</summary>
    public void SetSelection(IReadOnlyList<LineSegment> lines)
    {
        _overlayVb?.Dispose();
        _overlayVb = null;
        _overlayVertexCount = 0;
        if (lines.Count == 0)
        {
            return;
        }

        var verts = new LineVertex[lines.Count * 2];
        for (int i = 0; i < lines.Count; i++)
        {
            verts[(i * 2) + 0] = new LineVertex(lines[i].A, lines[i].Color);
            verts[(i * 2) + 1] = new LineVertex(lines[i].B, lines[i].Color);
        }

        _overlayVb = _gd.CreateVertexBuffer<LineVertex>(verts);
        _overlayVertexCount = verts.Length;
    }

    /// <summary>Sets the manipulator/gizmo line overlay, drawn ON TOP of the scene (depth test
    /// disabled) so its handles are never occluded by geometry in front of the selection (item 12).</summary>
    public void SetGizmoOverlay(IReadOnlyList<LineSegment> lines)
    {
        _gizmoOverlayVb?.Dispose();
        _gizmoOverlayVb = null;
        _gizmoOverlayVertexCount = 0;
        if (lines.Count == 0)
        {
            return;
        }

        var verts = new LineVertex[lines.Count * 2];
        for (int i = 0; i < lines.Count; i++)
        {
            verts[(i * 2) + 0] = new LineVertex(lines[i].A, lines[i].Color);
            verts[(i * 2) + 1] = new LineVertex(lines[i].B, lines[i].Color);
        }

        _gizmoOverlayVb = _gd.CreateVertexBuffer<LineVertex>(verts);
        _gizmoOverlayVertexCount = verts.Length;
    }

    public void Resize(int width, int height)
    {
        _surface.Resize(width, height);
        _pick?.Resize(width, height);
        Camera.AspectRatio = (float)Math.Max(1, _surface.Width) / Math.Max(1, _surface.Height);
    }

    /// <summary>Renders one frame to the swapchain and presents it.</summary>
    public void Render()
    {
        _renderer.Render(Camera, Mode, _gpuScene, _surface);
        _renderer.DrawOverlayLines(Camera, _overlayVb, _overlayVertexCount);
        _renderer.DrawOverlayLines(Camera, _gizmoOverlayVb, _gizmoOverlayVertexCount, onTop: true);
        _surface.Present(VSync);
    }

    /// <summary>Picks the object/face under a pixel via the GPU id-buffer.</summary>
    public PickId Pick(int x, int y)
    {
        _pick ??= _gd.CreatePickTarget(_surface.Width, _surface.Height);
        _pick.Resize(_surface.Width, _surface.Height);
        return _renderer.RenderPick(Camera, _gpuScene, _pick, x, y);
    }

    public void Dispose()
    {
        _overlayVb?.Dispose();
        _gizmoOverlayVb?.Dispose();
        _pick?.Dispose();
        _gpuScene.Dispose();
        _surface.Dispose();
        _renderer.Dispose();
    }
}
