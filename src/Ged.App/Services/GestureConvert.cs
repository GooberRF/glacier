using Avalonia.Input;
using CoreGesture = Ged.Core.Input.KeyGesture;
using GestureModifiers = Ged.Core.Input.GestureModifiers;

namespace Ged.App.Services;

/// <summary>
/// Translates platform key events into device-independent
/// <see cref="KeyGesture"/> tokens: Avalonia <see cref="Key"/> + modifiers for the
/// Avalonia input path, and Win32 virtual-key codes for input proxied from the
/// viewport's native swapchain window. Both paths produce identical tokens so a
/// hotkey behaves the same whether the pointer is over a panel or the viewport.
/// </summary>
internal static class GestureConvert
{
    /// <summary>Converts an Avalonia key + modifiers to a gesture, or null if unmapped.</summary>
    public static CoreGesture? FromAvalonia(Key key, KeyModifiers modifiers)
    {
        string? token = AvaloniaKeyToken(key);
        if (token is null)
        {
            return null;
        }

        GestureModifiers mods = GestureModifiers.None;
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            mods |= GestureModifiers.Ctrl;
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            mods |= GestureModifiers.Shift;
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            mods |= GestureModifiers.Alt;
        }

        return new CoreGesture(token, mods);
    }

    /// <summary>Converts a Win32 virtual key + modifier flags to a gesture, or null.
    /// <paramref name="extended"/> (WM_KEY* lParam bit 24) disambiguates NumpadEnter
    /// (extended VK_RETURN → "NumpadEnter") from the main-row Enter.</summary>
    public static CoreGesture? FromVirtualKey(int vk, GestureModifiers mods, bool extended = false)
    {
        string? token = VirtualKeyToken(vk, extended);
        return token is null ? null : new CoreGesture(token, mods);
    }

    /// <summary>
    /// The Win32 virtual-key code that maps to a device-independent token, or -1 when
    /// none does. Used to reconcile the held-key set against GetAsyncKeyState (item 6b);
    /// NumpadEnter and Enter both resolve to VK_RETURN (physical state is indistinguishable,
    /// which is fine for the "is it still down" reconciliation).
    /// </summary>
    public static int VirtualKeyForToken(string token) => token switch
    {
        _ when token.Length == 1 && token[0] is >= 'A' and <= 'Z' => token[0],
        _ when token.Length == 1 && token[0] is >= '0' and <= '9' => token[0],
        _ when token.StartsWith("Numpad", System.StringComparison.Ordinal)
            && token.Length == 7 && token[6] is >= '0' and <= '9' => 0x60 + (token[6] - '0'),
        "NumpadPlus" => 0x6B,
        "NumpadMinus" => 0x6D,
        "NumpadEnter" => 0x0D,
        "Enter" => 0x0D,
        "Space" => 0x20,
        "Tab" => 0x09,
        "Left" => 0x25,
        "Up" => 0x26,
        "Right" => 0x27,
        "Down" => 0x28,
        "Home" => 0x24,
        "End" => 0x23,
        "Delete" => 0x2E,
        "Backspace" => 0x08,
        "Escape" => 0x1B,
        "[" => 0xDB,
        "]" => 0xDD,
        "\\" => 0xDC,
        "Plus" => 0xBB,
        "Minus" => 0xBD,
        "," => 0xBC,
        _ => -1,
    };

    /// <summary>
    /// The Win32 virtual-key code for an Avalonia <see cref="Key"/>, or -1 when none maps.
    /// The composited OpenGL viewport pane translates its Avalonia key events into the same
    /// virtual-key codes the Direct3D 11 native pane's WndProc proxies, so BOTH backends
    /// feed the one shared <see cref="Ged.App.Viewport.ViewportInputRouter"/> identically
    /// (transform keys M/R/N, arrows/PageUp/Down nudges, ESC cancels, held-key camera
    /// navigation, gated hotkey dispatch). NumpadEnter is not distinguished from Enter on
    /// this path (Avalonia collapses both to <see cref="Key.Enter"/>).
    /// </summary>
    public static int AvaloniaKeyToVirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F12 => 0x70 + (key - Key.F1),
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.Enter => 0x0D,
        Key.Tab => 0x09,
        Key.Delete => 0x2E,
        Key.Back => 0x08,
        Key.Add => 0x6B,
        Key.Subtract => 0x6D,
        Key.OemOpenBrackets => 0xDB,
        Key.OemCloseBrackets => 0xDD,
        Key.OemPipe or Key.OemBackslash => 0xDC,
        Key.OemPlus => 0xBB,
        Key.OemMinus => 0xBD,
        Key.OemComma => 0xBC,
        _ => -1,
    };

    private static string? AvaloniaKeyToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Numpad" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F12 => "F" + (key - Key.F1 + 1),
        Key.Add => "NumpadPlus",
        Key.Subtract => "NumpadMinus",
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Left => "Left",
        Key.Up => "Up",
        Key.Right => "Right",
        Key.Down => "Down",
        Key.Home => "Home",
        Key.End => "End",
        Key.Delete => "Delete",
        Key.Back => "Backspace",
        Key.Escape => "Escape",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        Key.OemComma => ",",
        _ => null,
    };

    private static string? VirtualKeyToken(int vk, bool extended = false) => vk switch
    {
        // NumpadEnter is an extended VK_RETURN; the main-row Enter is not. Keep them as
        // distinct tokens so their KeyDown/KeyUp never collide (a shared token could be
        // double-removed / desynced — item 6b) and the "NumpadEnter" binding is reachable.
        0x0D when extended => "NumpadEnter",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),          // A-Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),          // 0-9
        >= 0x60 and <= 0x69 => "Numpad" + (vk - 0x60),         // Numpad0-9
        >= 0x70 and <= 0x7B => "F" + (vk - 0x70 + 1),          // F1-F12
        0x6B => "NumpadPlus",
        0x6D => "NumpadMinus",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x24 => "Home",
        0x23 => "End",
        0x2E => "Delete",
        0x08 => "Backspace",
        0x1B => "Escape",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBB => "Plus",
        0xBD => "Minus",
        0xBC => ",",
        _ => null,
    };
}
