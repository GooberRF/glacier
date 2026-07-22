using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Ged.App;

/// <summary>
/// Minimal Win32 P/Invoke for hosting a Direct3D swapchain in an Avalonia
/// <c>NativeControlHost</c>: it registers a child-window class whose WndProc
/// forwards mouse/keyboard input to the owning viewport (Avalonia never sees the
/// child window's input, so the viewport handles it directly). Consumed through
/// <see cref="Win32ViewportHost"/>, the Windows <see cref="IViewportHost"/>.
/// </summary>
internal static class Win32
{
    public const uint WsChild = 0x40000000;
    public const uint WsVisible = 0x10000000;
    public const uint WsClipSiblings = 0x04000000;
    public const uint WsClipChildren = 0x02000000;

    private const uint WmDestroy = 0x0002;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmKillFocus = 0x0008;
    private const uint WmSetFocus = 0x0007;

    /// <summary>lParam bit 24: the key is an extended key (NumpadEnter, arrows, right modifiers).</summary>
    private const long ExtendedKeyFlag = 0x01000000;

    private const string ClassName = "GedViewportHostWindow";

    private static readonly ConcurrentDictionary<nint, IViewportInput> Handlers = new();
    private static readonly ConcurrentDictionary<nint, bool> LeaveTracking = new();
    private static readonly WndProcDelegate WndProcInstance = WindowProc;
    private static bool _classRegistered;
    private static readonly object RegisterLock = new();

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>Creates a child render window under <paramref name="parent"/> and wires its input handler.</summary>
    public static nint CreateChild(nint parent, int width, int height, IViewportInput input)
    {
        EnsureClassRegistered();
        nint hwnd = CreateWindowExW(
            0, ClassName, string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, Math.Max(1, width), Math.Max(1, height),
            parent, nint.Zero, GetModuleHandleW(null), nint.Zero);

        if (hwnd != nint.Zero)
        {
            Handlers[hwnd] = input;
        }

        return hwnd;
    }

    public static void DestroyChild(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        Handlers.TryRemove(hwnd, out _);
        DestroyWindow(hwnd);
    }

    public static (int Width, int Height) GetClientSize(nint hwnd)
    {
        if (hwnd != nint.Zero && GetClientRect(hwnd, out Rect r))
        {
            return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        }

        return (1, 1);
    }

    private static void EnsureClassRegistered()
    {
        lock (RegisterLock)
        {
            if (_classRegistered)
            {
                return;
            }

            var wc = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                style = 0x0003, // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcInstance),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(nint.Zero, 32512), // IDC_ARROW
                lpszClassName = ClassName,
            };

            if (RegisterClassExW(ref wc) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx failed: 0x{Marshal.GetLastWin32Error():X8}");
            }

            _classRegistered = true;
        }
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Handlers.TryGetValue(hwnd, out IViewportInput? input))
        {
            switch (msg)
            {
                case WmEraseBkgnd:
                    return 1; // Prevent GDI erase flicker; D3D owns the surface.

                case WmKeyDown:
                case WmSysKeyDown:
                    input.OnKey((int)wParam, true, (lParam.ToInt64() & ExtendedKeyFlag) != 0);
                    return 0;

                case WmKeyUp:
                case WmSysKeyUp:
                    input.OnKey((int)wParam, false, (lParam.ToInt64() & ExtendedKeyFlag) != 0);
                    return 0;

                case WmKillFocus:
                    // Losing keyboard focus (alt-tab, clicking another pane/panel) can
                    // swallow the KeyUp of a held navigation key OR a held modifier — drop
                    // the held set AND the modifier bitfield so nothing stays "stuck down"
                    // (item 6b defense-in-depth; the dead Ctrl+Z/Ctrl+Y fix).
                    input.OnFocusLost();
                    return 0;

                case WmSetFocus:
                    // Regaining focus (re-click, alt-tab back): re-derive the modifiers from
                    // physical key state so a modifier held across the focus change — whose
                    // KeyDown this pane never saw — is picked up before the first gesture.
                    input.OnFocusGained();
                    return 0;

                case WmMouseMove:
                    // Arm WM_MOUSELEAVE once per entry so pointer-over state (TAB
                    // routing, active-pane border) clears when the cursor leaves.
                    if (LeaveTracking.TryAdd(hwnd, true))
                    {
                        var track = new TrackMouseEventData
                        {
                            cbSize = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                            dwFlags = 0x0002, // TME_LEAVE
                            hwndTrack = hwnd,
                        };
                        TrackMouseEvent(ref track);
                    }

                    input.OnPointerActivate();
                    input.OnMouseMove(SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmMouseLeave:
                    LeaveTracking.TryRemove(hwnd, out _);
                    input.OnPointerLeave();
                    return 0;

                case WmLButtonDown:
                    SetFocus(hwnd);
                    SetCapture(hwnd);
                    input.OnButton(ViewportButton.Left, true, SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmLButtonUp:
                    ReleaseCapture();
                    input.OnButton(ViewportButton.Left, false, SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmRButtonDown:
                    SetFocus(hwnd);
                    SetCapture(hwnd);
                    input.OnButton(ViewportButton.Right, true, SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmRButtonUp:
                    ReleaseCapture();
                    input.OnButton(ViewportButton.Right, false, SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmMButtonDown:
                    SetFocus(hwnd);
                    SetCapture(hwnd);
                    input.OnButton(ViewportButton.Middle, true, SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmMButtonUp:
                    ReleaseCapture();
                    input.OnButton(ViewportButton.Middle, false, SignedLoWord(lParam), SignedHiWord(lParam));
                    return 0;

                case WmMouseWheel:
                    input.OnWheel(SignedHiWord(wParam));
                    return 0;

                case WmDestroy:
                    Handlers.TryRemove(hwnd, out _);
                    if (LeaveTracking.TryRemove(hwnd, out _))
                    {
                        input.OnPointerLeave();
                    }

                    break;
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>True when the physical key for <paramref name="virtualKey"/> is currently down
    /// (high bit of GetAsyncKeyState). Used to reconcile the held-key set against reality
    /// so a lost KeyUp can never leave a navigation key stuck (item 6b).</summary>
    public static bool IsKeyPhysicallyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static int SignedLoWord(nint value) => (short)(value.ToInt64() & 0xFFFF);

    private static int SignedHiWord(nint value) => (short)((value.ToInt64() >> 16) & 0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassExW wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, int cursorName);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData eventTrack);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
