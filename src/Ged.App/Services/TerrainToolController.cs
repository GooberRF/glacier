using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering.Scene;

namespace Ged.App.Services;

/// <summary>
/// The viewport side of the Terrain Tools: while the tools are armed on a selected terrain, a left press on the terrain
/// starts a stroke, moves dab along the cursor path, holding still repeats (every 40 ms, as Alpine RED's panel timer does —
/// editor_patch/terrain_paint.cpp:72-74, 1050-1055) and the release records the stroke as one undo step. The cursor ring
/// follows the surface; Ctrl+press with Set Height samples the height under the cursor, Shift+press with a mirror on
/// moves the mirror lines there (terrain_paint.cpp:1582-1628). Rays are world-space (origin, unit direction).
/// </summary>
public sealed class TerrainToolController
{
    private readonly Func<EditorDocument?> _doc;
    private readonly Func<LevelObject?> _target;

    public TerrainToolController(Func<EditorDocument?> doc, Func<LevelObject?> target, TerrainBrushSettings settings)
    {
        _doc = doc;
        _target = target;
        Settings = settings;
    }

    /// <summary>The panel's settings (shared with the Terrain Tools panel).</summary>
    public TerrainBrushSettings Settings { get; }

    /// <summary>True while the tools own left presses on the target terrain.</summary>
    public bool Armed { get; set; }

    /// <summary>The point under the cursor on the target terrain, or null.</summary>
    public Vector3? Hover { get; private set; }

    /// <summary>The stroke in progress, or null.</summary>
    public TerrainStroke? Stroke { get; private set; }

    /// <summary>The terrain the tools act on: the one selected, editable terrain (null otherwise).</summary>
    public LevelObject? Target => _target() is { Model: AlpineTerrain, IsReadOnly: false } o ? o : null;

    /// <summary>A one-off status note (the last stroke's, or a click's).</summary>
    public string? Note { get; private set; }

    /// <summary>Raised when a stroke is recorded (true: heights, holes or diagonals changed — the build is out of date).</summary>
    public event Action<bool>? Committed;

    /// <summary>Raised whenever the terrain being stroked changed (live feedback), or the cursor moved.</summary>
    public event Action? Changed;

    /// <summary>Raised when a click changed a setting (sampled height, mirror lines).</summary>
    public event Action? SettingsChanged;

    private AlpineTerrain? Terrain => Target?.Model as AlpineTerrain;

    private Vector3? Cast(Vector3 origin, Vector3 dir)
    {
        if (Terrain is not { } t)
        {
            return null;
        }

        return TerrainOps.RayHit(t, new Vec3(origin.X, origin.Y, origin.Z), new Vec3(dir.X, dir.Y, dir.Z)) is float d
            ? origin + (dir * d)
            : null;
    }

    /// <summary>Press-time gate: true when the tools are armed and the ray hits the target terrain (the press is then theirs).</summary>
    public bool HitTest(Vector3 origin, Vector3 dir)
    {
        if (!Armed || Target is null)
        {
            return false;
        }

        Hover = Cast(origin, dir);
        return Hover is not null;
    }

    /// <summary>Cursor moved with no button held.</summary>
    public void HoverAt(Vector3 origin, Vector3 dir)
    {
        if (!Armed || Target is null)
        {
            if (Hover is not null)
            {
                Hover = null;
                Changed?.Invoke();
            }

            return;
        }

        Hover = Cast(origin, dir);
        Changed?.Invoke();
    }

    /// <summary>A press on the terrain: Ctrl samples Set Height, Shift places the mirror lines, otherwise a stroke begins.</summary>
    public void Begin(Vector3 origin, Vector3 dir, bool ctrl = false, bool shift = false)
    {
        Note = null;
        if (_doc() is not { } doc || Target is not { } obj || Terrain is not { } t || Cast(origin, dir) is not { } hit)
        {
            return;
        }

        Hover = hit;
        if (ctrl && Settings.Tool == TerrainTool.SetHeight)
        {
            Settings.SetHeight = hit.Y;
            Note = $"Height {hit.Y:0.###}";
            SettingsChanged?.Invoke();
            return;
        }

        if (shift && (Settings.MirrorX || Settings.MirrorZ))
        {
            int Off2(float world, float origin0, int verts) =>
                (int)Math.Round((((world - origin0) / t.CellSize) * 2.0) - AlpineTerrainRules.Cells(verts));
            if (Settings.MirrorX)
            {
                Settings.MirrorXOffset2 = Off2(hit.X, t.Origin.X, t.Nx);
            }

            if (Settings.MirrorZ)
            {
                Settings.MirrorZOffset2 = Off2(hit.Z, t.Origin.Z, t.Nz);
            }

            SettingsChanged?.Invoke();
            Changed?.Invoke();
            return;
        }

        Stroke = new TerrainStroke(doc, obj, Settings);
        if (Stroke.MoveTo(hit.X, hit.Z))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The cursor moved with the button held.</summary>
    public void MoveTo(Vector3 origin, Vector3 dir)
    {
        if (Stroke is not { } s)
        {
            HoverAt(origin, dir);
            return;
        }

        Hover = Cast(origin, dir);
        if (Hover is { } h)
        {
            s.MoveTo(h.X, h.Z);
        }
        else
        {
            s.Lift();
        }

        Changed?.Invoke();
    }

    /// <summary>The repeat tick while the button is held still (sculpt tools keep dabbing).</summary>
    public void Tick()
    {
        if (Stroke is { } s && s.Repeat())
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The release: the stroke becomes one undo step. Returns true when it changed the terrain.</summary>
    public bool End()
    {
        if (Stroke is not { } s)
        {
            return false;
        }

        Stroke = null;
        bool changed = s.Finish();
        Note = s.Note;
        if (changed)
        {
            Committed?.Invoke(s.EditedGeometry);
        }

        Changed?.Invoke();
        return changed;
    }

    /// <summary>Esc: the stroke is abandoned and the terrain restored.</summary>
    public void Cancel()
    {
        if (Stroke is { } s)
        {
            Stroke = null;
            s.Cancel();
            Changed?.Invoke();
        }
    }

    /// <summary>The brush cursor's lines (empty when the tools are not armed on a terrain).</summary>
    public IReadOnlyList<LineSegment> CursorLines()
    {
        if (!Armed || Terrain is not { } t)
        {
            return Array.Empty<LineSegment>();
        }

        (Vector2, Vector2)? segment = Stroke is { Anchor: { } a, End: { } e } && Settings.Tool == TerrainTool.BridgePoints
            ? (new Vector2(a.X, a.Z), new Vector2(e.X, e.Z))
            : null;
        return TerrainBrushCursor.Lines(t, Settings, new TerrainCursorState { Hover = Hover, Stroking = Stroke is not null, Segment = segment });
    }

    /// <summary>A scene holding only the terrain being stroked (drawn over the level while the stroke runs).</summary>
    public RenderScene? LiveScene()
    {
        if (Stroke is not { } s)
        {
            return null;
        }

        var scene = new RenderScene();
        TerrainSurface.Append(scene, s.Terrain, Palette.Rgba(255, 255, 255, 255));
        return scene;
    }
}
