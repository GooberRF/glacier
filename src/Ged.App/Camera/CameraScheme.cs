using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Input;
using Ged.Rendering;

namespace Ged.App.Camera;

/// <summary>The selectable viewport navigation schemes.</summary>
public enum CameraSchemeKind
{
    RedClassic,
    ModernFps,
    Orbit,
    UnrealEd,
}

/// <summary>Live input state the viewport router maintains and hands to the camera scheme.</summary>
internal sealed class ViewportInputState
{
    public HashSet<string> HeldKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool LeftDown { get; set; }

    public bool RightDown { get; set; }

    public bool MiddleDown { get; set; }

    public GestureModifiers Modifiers { get; set; }

    /// <summary>Camera fly / nudge speed in metres per second.</summary>
    public float Speed { get; set; } = 12f;

    /// <summary>Last pointer position in surface pixels (drives cursor-centred ortho zoom).</summary>
    public float PointerX { get; set; }

    /// <summary>Last pointer position in surface pixels (drives cursor-centred ortho zoom).</summary>
    public float PointerY { get; set; }

    /// <summary>Surface client width in pixels (drives cursor-centred ortho zoom).</summary>
    public float ViewWidth { get; set; } = 1f;

    /// <summary>Surface client height in pixels (drives cursor-centred ortho zoom).</summary>
    public float ViewHeight { get; set; } = 1f;

    /// <summary>True while the pane views an orthographic projection (kept in sync by the surface).</summary>
    public bool Ortho { get; set; }

    public bool Shift => (Modifiers & GestureModifiers.Shift) != 0;

    public bool Ctrl => (Modifiers & GestureModifiers.Ctrl) != 0;

    public bool Alt => (Modifiers & GestureModifiers.Alt) != 0;

    public bool Held(string token) => HeldKeys.Contains(token);

    /// <summary>Clears every held key (native focus loss / viewport switch — item 6b).</summary>
    public void ClearHeld() => HeldKeys.Clear();

    /// <summary>
    /// Drops any held-key token whose physical key is no longer down, per
    /// <paramref name="physicallyDown"/>. This is the defense-in-depth against a lost
    /// KeyUp (e.g. NumpadEnter interleaving) leaving a navigation key stuck; the viewport
    /// runs it every tick with GetAsyncKeyState. A token the predicate cannot resolve is
    /// kept (never dropped on uncertainty).
    /// </summary>
    public void ValidateHeld(Func<string, bool> physicallyDown) =>
        HeldKeys.RemoveWhere(t => !physicallyDown(t));
}

/// <summary>A viewport navigation strategy. One stateful instance lives per pane.</summary>
internal interface ICameraScheme
{
    CameraSchemeKind Kind { get; }

    /// <summary>Per-frame continuous movement from held keys / buttons.</summary>
    void Move(Rendering.Camera cam, float dt, ViewportInputState s);

    /// <summary>Pointer drag: <paramref name="dx"/>/<paramref name="dy"/> are pixel deltas.</summary>
    void Drag(Rendering.Camera cam, float dx, float dy, ViewportInputState s);

    /// <summary>Mouse wheel notches (positive = up).</summary>
    void Wheel(Rendering.Camera cam, int delta, ViewportInputState s);

    /// <summary>True while a drag that should capture the pointer is active.</summary>
    bool IsNavigating(ViewportInputState s);

    /// <summary>
    /// True when the scheme consumes this bare held-key token for continuous camera
    /// navigation — the key router then suppresses the one-shot command dispatch for
    /// it (otherwise e.g. A both zooms and toasts "Zoom In is not available").
    /// </summary>
    bool ConsumesKey(Rendering.Camera cam, string token) =>
        cam.Projection == CameraProjection.Orthographic && OrthoNav.ConsumesKey(token);
}

/// <summary>
/// Stock RED orthographic-pane navigation, shared by every camera scheme (the ortho
/// panes behave identically regardless of the perspective scheme in use):
/// Shift+LMB / Shift+RMB slide, Shift+both drag-zoom, A/Z zoom, +/- coarse zoom,
/// cursor-centred wheel zoom, and numpad/arrow view slides (numpad 4/6/8/2 send the
/// arrow virtual-keys without NumLock, and neither has a pitch/heading to drive in an
/// ortho pane, so both slide the view plane). Each entry point returns false for
/// perspective cameras so schemes fall through to their own behavior.
/// </summary>
internal static class OrthoNav
{
    /// <summary>Per-frame held-key navigation: slides and A/Z / +/- zoom.</summary>
    public static bool Move(Rendering.Camera cam, float dt, ViewportInputState s)
    {
        if (cam.Projection != CameraProjection.Orthographic)
        {
            return false;
        }

        float step = s.Speed * dt;
        float right = (B(s.Held("Numpad3")) + B(s.Held("Numpad6")) + B(s.Held("Right"))
            - B(s.Held("Numpad1")) - B(s.Held("Numpad4")) - B(s.Held("Left"))) * step;
        float up = (B(s.Held("NumpadPlus")) + B(s.Held("Numpad8")) + B(s.Held("Up"))
            - B(s.Held("NumpadEnter")) - B(s.Held("Numpad2")) - B(s.Held("Down"))) * step;
        if (right != 0f || up != 0f)
        {
            cam.Pan(right, up);
        }

        // A/Z = fine zoom, main-row +/- = coarse zoom (both scale OrthoZoom).
        float fine = B(s.Held("A")) - B(s.Held("Z"));
        if (fine != 0f)
        {
            cam.ZoomOrtho(fine > 0f ? 1f / (1f + dt) : 1f + dt);
        }

        float coarse = B(s.Held("Plus")) - B(s.Held("Minus"));
        if (coarse != 0f)
        {
            cam.ZoomOrtho(coarse > 0f ? 1f / (1f + (3f * dt)) : 1f + (3f * dt));
        }

        return true;
    }

    /// <summary>
    /// Drag navigation: Shift+LMB or Shift+RMB slides the view plane, Shift+both
    /// drag-zooms. Unhandled drags return false so scheme-specific gestures (e.g.
    /// Orbit's Shift+MMB pan) still apply in ortho panes.
    /// </summary>
    public static bool Drag(Rendering.Camera cam, float dx, float dy, ViewportInputState s)
    {
        if (cam.Projection != CameraProjection.Orthographic)
        {
            return false;
        }

        if (s.Shift && s.LeftDown && s.RightDown)
        {
            cam.ZoomOrtho(1f + (dy * 0.01f));
            return true;
        }

        if (s.Shift && (s.LeftDown || s.RightDown))
        {
            float scale = cam.OrthoZoom * 0.002f;
            cam.Pan(-dx * scale, dy * scale);
            return true;
        }

        return false;
    }

    /// <summary>Cursor-centred wheel zoom (the world point under the cursor stays put).</summary>
    public static bool Wheel(Rendering.Camera cam, int delta, ViewportInputState s)
    {
        if (cam.Projection != CameraProjection.Orthographic)
        {
            return false;
        }

        cam.ZoomOrthoAt(s.PointerX, s.PointerY, s.ViewWidth, s.ViewHeight, delta > 0 ? 0.85f : 1f / 0.85f);
        return true;
    }

    /// <summary>The bare key tokens ortho navigation consumes (suppresses command dispatch).</summary>
    public static bool ConsumesKey(string token) => token is
        "Numpad1" or "Numpad2" or "Numpad3" or "Numpad4" or "Numpad6" or "Numpad8"
        or "NumpadPlus" or "NumpadEnter" or "Left" or "Right" or "Up" or "Down"
        or "A" or "Z" or "Plus" or "Minus";

    private static float B(bool v) => v ? 1f : 0f;
}

internal static class CameraSchemes
{
    public static ICameraScheme Create(CameraSchemeKind kind) => kind switch
    {
        CameraSchemeKind.RedClassic => new RedClassicScheme(),
        CameraSchemeKind.Orbit => new OrbitScheme(),
        CameraSchemeKind.UnrealEd => new UnrealEdScheme(),
        _ => new ModernFpsScheme(),
    };

    public static string DisplayName(CameraSchemeKind kind) => kind switch
    {
        CameraSchemeKind.RedClassic => "RED Classic",
        CameraSchemeKind.ModernFps => "Modern FPS",
        CameraSchemeKind.Orbit => "Orbit",
        CameraSchemeKind.UnrealEd => "UnrealEd",
        _ => kind.ToString(),
    };

    internal static void ApplyWheelSpeed(ViewportInputState s, int delta)
    {
        float factor = delta > 0 ? 1.2f : 1f / 1.2f;
        s.Speed = Math.Clamp(s.Speed * factor, 0.5f, 400f);
    }
}

/// <summary>Modern FPS: hold RMB to look, WASD + E/Q to fly, wheel changes speed.</summary>
internal sealed class ModernFpsScheme : ICameraScheme
{
    public CameraSchemeKind Kind => CameraSchemeKind.ModernFps;

    public void Move(Rendering.Camera cam, float dt, ViewportInputState s)
    {
        if (OrthoNav.Move(cam, dt, s))
        {
            return;
        }

        if (!s.RightDown)
        {
            return;
        }

        float fwd = B(s.Held("W")) - B(s.Held("S"));
        float right = B(s.Held("D")) - B(s.Held("A"));
        float up = B(s.Held("E")) - B(s.Held("Q"));
        float boost = s.Shift ? 3f : 1f;
        if (fwd != 0f || right != 0f || up != 0f)
        {
            float step = s.Speed * boost * dt;
            cam.MoveLocal(right * step, up * step, fwd * step);
        }
    }

    public void Drag(Rendering.Camera cam, float dx, float dy, ViewportInputState s)
    {
        if (OrthoNav.Drag(cam, dx, dy, s))
        {
            return;
        }

        if (s.RightDown)
        {
            cam.Rotate(dx * 0.005f, -dy * 0.005f);
        }
    }

    public void Wheel(Rendering.Camera cam, int delta, ViewportInputState s)
    {
        if (OrthoNav.Wheel(cam, delta, s))
        {
            return;
        }

        CameraSchemes.ApplyWheelSpeed(s, delta);
    }

    public bool IsNavigating(ViewportInputState s) =>
        s.RightDown || (s.Ortho && s.Shift && s.LeftDown);

    private static float B(bool v) => v ? 1f : 0f;
}

/// <summary>
/// RED Classic: numpad pitch/heading/slide, A/Z dolly, +/- ortho zoom, Shift+LMB
/// slide, Shift+RMB freelook (slide in ortho), Shift+both zoom, plain RMB flies.
/// Wheel = speed (cursor-centred zoom in ortho).
/// </summary>
internal sealed class RedClassicScheme : ICameraScheme
{
    public CameraSchemeKind Kind => CameraSchemeKind.RedClassic;

    public void Move(Rendering.Camera cam, float dt, ViewportInputState s)
    {
        // Ortho panes: numpad/arrow slides + A/Z and +/- OrthoZoom (stock §3).
        if (OrthoNav.Move(cam, dt, s))
        {
            return;
        }

        float rot = 1.2f * dt;
        if (s.Held("Numpad8"))
        {
            cam.Rotate(0f, rot);
        }

        if (s.Held("Numpad2"))
        {
            cam.Rotate(0f, -rot);
        }

        if (s.Held("Numpad4"))
        {
            cam.Rotate(-rot, 0f);
        }

        if (s.Held("Numpad6"))
        {
            cam.Rotate(rot, 0f);
        }

        // Shift boosts continuous movement speed (mirrors Modern FPS's x3 fly boost).
        // Shift stays a free modifier here — the Shift+mouse slide chords live in Drag(),
        // so boosting keyboard/numpad movement never changes chord semantics.
        float boost = s.Shift ? 3f : 1f;
        float step = s.Speed * boost * dt;
        float slide = (B(s.Held("Numpad3")) - B(s.Held("Numpad1"))) * step;
        float lift = (B(s.Held("NumpadPlus")) - B(s.Held("NumpadEnter"))) * step;
        float dolly = (B(s.Held("A")) - B(s.Held("Z"))) * step;
        if (slide != 0f || lift != 0f || dolly != 0f)
        {
            cam.MoveLocal(slide, lift, dolly);
        }

        // RED Classic has no WASD fly — that's a Modern FPS feature. In RED Classic 'A'
        // means zoom-in (the A/Z dolly above) and W/S/D keep their stock command meanings
        // (W = Hide All Objects, etc.), so no camera movement is bound to them here.
    }

    public void Drag(Rendering.Camera cam, float dx, float dy, ViewportInputState s)
    {
        // Ortho panes: Shift+LMB / Shift+RMB slide, Shift+both zoom (stock §3).
        if (OrthoNav.Drag(cam, dx, dy, s))
        {
            return;
        }

        if (s.Shift && s.LeftDown && s.RightDown)
        {
            cam.MoveLocal(0f, 0f, -dy * s.Speed * 0.02f); // perspective drag-zoom (dolly)
            return;
        }

        if (s.Shift && s.LeftDown)
        {
            float scale = s.Speed * 0.01f;
            cam.MoveLocal(-dx * scale, dy * scale, 0f); // perspective view-plane slide
            return;
        }

        if (s.RightDown)
        {
            cam.Rotate(dx * 0.005f, -dy * 0.005f);
        }
    }

    public void Wheel(Rendering.Camera cam, int delta, ViewportInputState s)
    {
        if (OrthoNav.Wheel(cam, delta, s))
        {
            return;
        }

        CameraSchemes.ApplyWheelSpeed(s, delta);
    }

    public bool IsNavigating(ViewportInputState s) => s.RightDown || (s.Shift && s.LeftDown);

    /// <summary>
    /// RED Classic drives its perspective camera from bare keys too (numpad
    /// pitch/heading/slide/lift, A/Z dolly), so those are consumed in every projection.
    /// </summary>
    public bool ConsumesKey(Rendering.Camera cam, string token) =>
        cam.Projection == CameraProjection.Orthographic
            ? OrthoNav.ConsumesKey(token)
            : token is "Numpad1" or "Numpad2" or "Numpad3" or "Numpad4" or "Numpad6"
                or "Numpad8" or "NumpadPlus" or "NumpadEnter" or "A" or "Z";

    private static float B(bool v) => v ? 1f : 0f;
}

/// <summary>Orbit (Blender-style): MMB or Alt+LMB orbit a pivot, Shift+MMB pan, wheel dollies.</summary>
internal sealed class OrbitScheme : ICameraScheme
{
    private float _distance = 12f;
    private bool _havePivot;
    private Vector3 _pivot;

    public CameraSchemeKind Kind => CameraSchemeKind.Orbit;

    public void Move(Rendering.Camera cam, float dt, ViewportInputState s) =>
        OrthoNav.Move(cam, dt, s);

    public void Drag(Rendering.Camera cam, float dx, float dy, ViewportInputState s)
    {
        if (OrthoNav.Drag(cam, dx, dy, s))
        {
            return;
        }

        bool orbiting = s.MiddleDown && !s.Shift || (s.Alt && s.LeftDown);
        bool panning = s.MiddleDown && s.Shift;

        EnsurePivot(cam);
        if (orbiting)
        {
            cam.Yaw += dx * 0.01f;
            const float limit = (MathF.PI / 2f) - 0.02f;
            cam.Pitch = Math.Clamp(cam.Pitch - dy * 0.01f, -limit, limit);
            PlaceFromPivot(cam);
        }
        else if (panning)
        {
            float scale = _distance * 0.0015f;
            Vector3 move = (-cam.Right * dx * scale) + (cam.Up * dy * scale);
            _pivot += move;
            cam.Position += move;
        }
    }

    public void Wheel(Rendering.Camera cam, int delta, ViewportInputState s)
    {
        if (OrthoNav.Wheel(cam, delta, s))
        {
            return;
        }

        EnsurePivot(cam);
        _distance = Math.Clamp(_distance * (delta > 0 ? 0.85f : 1f / 0.85f), 0.5f, 20000f);
        PlaceFromPivot(cam);
    }

    public bool IsNavigating(ViewportInputState s) =>
        s.MiddleDown || (s.Alt && s.LeftDown) || (s.Ortho && s.Shift && (s.LeftDown || s.RightDown));

    private void EnsurePivot(Rendering.Camera cam)
    {
        if (_havePivot)
        {
            return;
        }

        _pivot = cam.Position + (cam.Forward * _distance);
        _havePivot = true;
    }

    private void PlaceFromPivot(Rendering.Camera cam) =>
        cam.Position = _pivot - (cam.Forward * _distance);
}

/// <summary>UnrealEd-style: LMB drag = dolly + turn, RMB drag = look, both = pan.</summary>
internal sealed class UnrealEdScheme : ICameraScheme
{
    public CameraSchemeKind Kind => CameraSchemeKind.UnrealEd;

    public void Move(Rendering.Camera cam, float dt, ViewportInputState s) =>
        OrthoNav.Move(cam, dt, s);

    public void Drag(Rendering.Camera cam, float dx, float dy, ViewportInputState s)
    {
        if (OrthoNav.Drag(cam, dx, dy, s))
        {
            return;
        }

        if (s.LeftDown && s.RightDown)
        {
            float scale = s.Speed * 0.01f;
            cam.MoveLocal(-dx * scale, dy * scale, 0f);
        }
        else if (s.LeftDown)
        {
            cam.MoveLocal(0f, 0f, -dy * s.Speed * 0.02f);
            cam.Rotate(dx * 0.005f, 0f);
        }
        else if (s.RightDown)
        {
            cam.Rotate(dx * 0.005f, -dy * 0.005f);
        }
    }

    public void Wheel(Rendering.Camera cam, int delta, ViewportInputState s)
    {
        if (OrthoNav.Wheel(cam, delta, s))
        {
            return;
        }

        CameraSchemes.ApplyWheelSpeed(s, delta);
    }

    public bool IsNavigating(ViewportInputState s) => s.LeftDown || s.RightDown;
}
