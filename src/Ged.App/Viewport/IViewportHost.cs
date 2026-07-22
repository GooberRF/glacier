namespace Ged.App;

/// <summary>Which mouse button an input event refers to.</summary>
internal enum ViewportButton
{
    Left,
    Right,
    Middle,
}

/// <summary>Receives raw input proxied from a viewport's native render surface.</summary>
internal interface IViewportInput
{
    /// <summary><paramref name="extended"/> is the WM_KEYDOWN/UP extended-key flag
    /// (lParam bit 24) — set for NumpadEnter, the arrow cluster, right Ctrl/Alt, etc.
    /// It disambiguates NumpadEnter (extended VK_RETURN) from the main Enter.</summary>
    void OnKey(int virtualKey, bool down, bool extended);

    void OnMouseMove(int x, int y);

    void OnButton(ViewportButton button, bool down, int x, int y);

    void OnWheel(int delta);

    /// <summary>Raised when the pointer enters this surface (drives active-pane focus).</summary>
    void OnPointerActivate();

    /// <summary>Raised when the pointer leaves this surface (clears pointer-over state).</summary>
    void OnPointerLeave();

    /// <summary>Raised when the native surface loses keyboard focus (drops the held-key set).</summary>
    void OnFocusLost();

    /// <summary>Raised when the native surface regains keyboard focus (re-syncs the modifier
    /// bitfield from physical key state so a modifier held across the focus change is picked up).</summary>
    void OnFocusGained();
}

/// <summary>
/// The platform host for a live render pane: creates the native drawing surface a
/// <see cref="Ged.Rendering.Viewport"/> renders into, reports its client size, and
/// answers physical-key queries used to reconcile the held-key set. The
/// <see cref="Win32ViewportHost"/> is the Windows implementation (a child HWND via
/// <see cref="Win32"/>); L3 adds an Avalonia <c>OpenGlControlBase</c> host behind
/// this same interface so viewports composite into the UI on every platform.
/// </summary>
internal interface IViewportHost
{
    /// <summary>Creates a native child render surface under <paramref name="parent"/> and wires its input handler.</summary>
    nint CreateChild(nint parent, int width, int height, IViewportInput input);

    /// <summary>Destroys a surface created by <see cref="CreateChild"/>.</summary>
    void DestroyChild(nint handle);

    /// <summary>The client (drawable) size of a surface in pixels.</summary>
    (int Width, int Height) GetClientSize(nint handle);

    /// <summary>True when the physical key for <paramref name="virtualKey"/> is currently down.</summary>
    bool IsKeyPhysicallyDown(int virtualKey);
}

/// <summary>
/// The process-wide viewport host. Defaults to the Windows (<see cref="Win32ViewportHost"/>)
/// implementation; L3 assigns the OpenGL host on non-Windows (or everywhere, once
/// GL is the sole backend).
/// </summary>
internal static class ViewportHost
{
    public static IViewportHost Current { get; set; } = new Win32ViewportHost();
}
