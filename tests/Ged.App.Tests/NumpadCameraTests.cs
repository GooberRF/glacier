using System.Collections.Generic;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.Core.Input;
using Xunit;
using RCamera = Ged.Rendering.Camera;
using CameraProjection = Ged.Rendering.CameraProjection;

namespace Ged.App.Tests;

/// <summary>
/// Item 6: RED Classic numpad camera parity (2/8 pitch, 4/6 heading matching stock
/// research/red-stock-inventory.md §3) and the held-key state machine that fixes the
/// NumpadEnter stuck-key desync (down4/downEnter/up4/upEnter → nothing held, plus the
/// GetAsyncKeyState reconciliation drop path).
/// </summary>
public sealed class NumpadCameraTests
{
    private static RCamera Perspective() => new()
    {
        Projection = CameraProjection.Perspective,
        Yaw = 0f,
        Pitch = 0f,
    };

    private static ViewportInputState State(string held)
    {
        var s = new ViewportInputState { Speed = 10f };
        s.HeldKeys.Add(held);
        return s;
    }

    // ---- 6a: pitch / heading directions match stock (2 down / 8 up, 4 left / 6 right) ----

    [Fact]
    public void Numpad8_Pitches_Up_And_Numpad2_Pitches_Down()
    {
        var scheme = new RedClassicScheme();

        RCamera up = Perspective();
        scheme.Move(up, 0.1f, State("Numpad8"));
        Assert.True(up.Pitch > 0f, "Numpad8 must pitch UP (positive pitch = look up)");

        RCamera down = Perspective();
        scheme.Move(down, 0.1f, State("Numpad2"));
        Assert.True(down.Pitch < 0f, "Numpad2 must pitch DOWN (negative pitch = look down)");
    }

    [Fact]
    public void Numpad4_Headings_Left_And_Numpad6_Headings_Right()
    {
        var scheme = new RedClassicScheme();

        RCamera left = Perspective();
        scheme.Move(left, 0.1f, State("Numpad4"));
        Assert.True(left.Yaw < 0f, "Numpad4 must yaw left (decreasing yaw)");

        RCamera right = Perspective();
        scheme.Move(right, 0.1f, State("Numpad6"));
        Assert.True(right.Yaw > 0f, "Numpad6 must yaw right (increasing yaw)");
    }

    // ---- 6b: NumpadEnter is a distinct token from Enter ----

    [Fact]
    public void NumpadEnter_Is_A_Distinct_Token_From_Enter()
    {
        // VK_RETURN (0x0D): extended = NumpadEnter, non-extended = main Enter.
        KeyGesture? numpadEnter = GestureConvert.FromVirtualKey(0x0D, GestureModifiers.None, extended: true);
        KeyGesture? mainEnter = GestureConvert.FromVirtualKey(0x0D, GestureModifiers.None, extended: false);

        Assert.Equal("NumpadEnter", numpadEnter?.Key);
        Assert.Equal("Enter", mainEnter?.Key);
        Assert.NotEqual(numpadEnter?.Key, mainEnter?.Key);
    }

    // ---- 6b: the interleaving sequence never leaves a key stuck ----

    private static string? Token(int vk, bool extended) =>
        GestureConvert.FromVirtualKey(vk, GestureModifiers.None, extended)?.Key;

    private static void Apply(HashSet<string> held, int vk, bool extended, bool down)
    {
        if (Token(vk, extended) is { } t)
        {
            if (down)
            {
                held.Add(t);
            }
            else
            {
                held.Remove(t);
            }
        }
    }

    [Fact]
    public void Down4_DownEnter_Up4_UpEnter_Leaves_Nothing_Held()
    {
        var held = new HashSet<string>();
        Apply(held, 0x64, extended: false, down: true);   // Numpad4 down (rotate)
        Apply(held, 0x0D, extended: true, down: true);    // NumpadEnter down (slide down)
        Apply(held, 0x64, extended: false, down: false);  // Numpad4 up
        Apply(held, 0x0D, extended: true, down: false);   // NumpadEnter up

        Assert.Empty(held);
    }

    [Fact]
    public void Rotation_Stops_After_The_Rotate_Key_Is_Released()
    {
        var scheme = new RedClassicScheme();
        var s = new ViewportInputState { Speed = 10f };
        s.HeldKeys.Add("Numpad6");    // rotating right
        s.HeldKeys.Add("NumpadEnter"); // sliding down

        RCamera cam = Perspective();
        scheme.Move(cam, 0.1f, s);
        float turned = cam.Yaw;
        Assert.True(turned > 0f);

        // Release Numpad6; NumpadEnter still down. The next tick must not keep rotating.
        s.HeldKeys.Remove("Numpad6");
        float before = cam.Yaw;
        scheme.Move(cam, 0.1f, s);
        Assert.Equal(before, cam.Yaw, 5);
    }

    // ---- 6b: the async-validation drop path ----

    [Fact]
    public void ValidateHeld_Drops_Keys_No_Longer_Physically_Down()
    {
        var s = new ViewportInputState();
        s.HeldKeys.Add("Numpad4");
        s.HeldKeys.Add("NumpadEnter");

        // Physical keyboard reports only NumpadEnter still down (Numpad4's KeyUp was lost).
        s.ValidateHeld(t => t == "NumpadEnter");

        Assert.DoesNotContain("Numpad4", s.HeldKeys);
        Assert.Contains("NumpadEnter", s.HeldKeys);
    }

    [Fact]
    public void ClearHeld_Empties_The_Held_Set_On_Focus_Loss()
    {
        var s = new ViewportInputState();
        s.HeldKeys.Add("Numpad4");
        s.HeldKeys.Add("A");
        s.ClearHeld();
        Assert.Empty(s.HeldKeys);
    }

    [Fact]
    public void VirtualKeyForToken_Resolves_Navigation_Tokens()
    {
        Assert.Equal(0x64, GestureConvert.VirtualKeyForToken("Numpad4"));
        Assert.Equal(0x0D, GestureConvert.VirtualKeyForToken("NumpadEnter"));
        Assert.Equal(0x6B, GestureConvert.VirtualKeyForToken("NumpadPlus"));
        Assert.Equal(0x41, GestureConvert.VirtualKeyForToken("A"));
        Assert.Equal(-1, GestureConvert.VirtualKeyForToken("F1")); // no async reconciliation needed
    }
}
