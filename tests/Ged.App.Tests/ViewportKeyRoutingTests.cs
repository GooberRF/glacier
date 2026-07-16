using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// REAL-PATH regressions for viewport-focused key routing. These drive the actual
/// <see cref="ViewportSurface"/> native-input entry point (<c>IViewportInput.OnKey</c>) —
/// the path a key takes when a native viewport pane holds focus — and assert the gesture
/// reaches the shared <see cref="CommandDispatcher"/>. The pre-existing dispatch-only tests
/// could not catch a viewport that swallowed the key before it ever reached the keymap.
///
/// Covers: Space→Build with a viewport focused (was reported dead), and Shift+S / Shift+D
/// firing grow / same-texture in Face mode (Shift is a speed-boost STATE, never a consumed
/// key, so chords always flow to the keymap — even while flying).
/// </summary>
public sealed class ViewportKeyRoutingTests
{
    // Win32 virtual-key codes the native WndProc proxies into OnKey.
    private const int VkShift = 0x10;
    private const int VkSpace = 0x20;
    private const int VkS = 0x53;
    private const int VkD = 0x44;
    private const int VkW = 0x57;

    private static (ViewportSurface Surface, IViewportInput Input, CommandDispatcher Dispatcher, Dictionary<string, int> Calls)
        Build(CommandScope scope, ViewType view = ViewType.Perspective, CameraSchemeKind scheme = CameraSchemeKind.RedClassic)
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap keymap = Keymap.FromPreset(CommandCatalog.RedClassic);
        var dispatcher = new CommandDispatcher(registry, keymap) { ActiveScope = scope };
        var calls = new Dictionary<string, int>();
        void Track(string id) => dispatcher.Bind(id, () => calls[id] = calls.GetValueOrDefault(id) + 1);
        Track(CommandIds.BuildGeometry);
        Track(CommandIds.SelectGrow);
        Track(CommandIds.SelectSameTexture);

        var surface = new ViewportSurface(dispatcher, scheme, view);
        return (surface, surface, dispatcher, calls);
    }

    [AvaloniaFact]
    public void Space_With_Viewport_Focused_Fires_Build_Geometry()
    {
        (_, IViewportInput input, _, var calls) = Build(CommandScope.Object);

        input.OnKey(VkSpace, down: true, extended: false);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.BuildGeometry));
    }

    [AvaloniaFact]
    public void Space_In_An_Ortho_Pane_Also_Fires_Build_Geometry()
    {
        (_, IViewportInput input, _, var calls) = Build(CommandScope.Object, ViewType.Top);

        input.OnKey(VkSpace, down: true, extended: false);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.BuildGeometry));
    }

    [AvaloniaFact]
    public void ShiftS_In_Face_Mode_Grows_Via_The_Viewport()
    {
        (_, IViewportInput input, _, var calls) = Build(CommandScope.Face);

        input.OnKey(VkShift, down: true, extended: false); // Shift held (speed-boost state)
        input.OnKey(VkS, down: true, extended: false);     // chord Shift+S

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectGrow));
    }

    [AvaloniaFact]
    public void ShiftD_In_Face_Mode_Selects_Same_Texture_Via_The_Viewport()
    {
        (_, IViewportInput input, _, var calls) = Build(CommandScope.Face);

        input.OnKey(VkShift, down: true, extended: false);
        input.OnKey(VkD, down: true, extended: false);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectSameTexture));
    }

    [AvaloniaFact]
    public void ShiftS_Chord_Still_Flows_To_The_Keymap_While_Flying()
    {
        // While flying (RMB held) Shift is a speed boost, but the Shift+S CHORD must still
        // reach the keymap — it is not a camera key. The bare movement keys are what the
        // scheme owns during a fly, not the chord.
        (ViewportSurface surface, IViewportInput input, _, var calls) = Build(CommandScope.Face);
        input.OnButton(ViewportButton.Right, down: true, 10, 10); // engage the fly drag

        input.OnKey(VkShift, down: true, extended: false);
        input.OnKey(VkS, down: true, extended: false);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectGrow));
    }

    [AvaloniaFact]
    public void Bare_Command_Key_Is_Suppressed_While_Flying()
    {
        // The counterpart: a BARE key while a nav drag is engaged is owned by the scheme
        // (so WASD-fly etc. never double-fire hotkeys). Space is bare, so during a fly it
        // does not build.
        (_, IViewportInput input, _, var calls) = Build(CommandScope.Object);
        input.OnButton(ViewportButton.Right, down: true, 10, 10);

        input.OnKey(VkSpace, down: true, extended: false);

        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.BuildGeometry));
    }

    [Fact]
    public void ModernFps_Shift_Boost_Multiplies_Fly_Speed()
    {
        // Camera boost still works while actually flying: Shift is read as a movement STATE.
        var scheme = (ICameraScheme)new ModernFpsScheme();
        static (Ged.Rendering.Camera Cam, ViewportInputState In) Setup(bool shift)
        {
            var cam = new Ged.Rendering.Camera { Position = new System.Numerics.Vector3(0, 0, 0) };
            var input = new ViewportInputState { Speed = 10f, RightDown = true };
            input.HeldKeys.Add("W");
            if (shift)
            {
                input.Modifiers = GestureModifiers.Shift;
            }

            return (cam, input);
        }

        (Ged.Rendering.Camera plain, ViewportInputState plainIn) = Setup(shift: false);
        (Ged.Rendering.Camera boosted, ViewportInputState boostIn) = Setup(shift: true);
        scheme.Move(plain, 0.1f, plainIn);
        scheme.Move(boosted, 0.1f, boostIn);

        float plainDist = plain.Position.Length();
        float boostDist = boosted.Position.Length();
        Assert.True(plainDist > 0.0001f, "plain fly should move the camera");
        Assert.True(boostDist > plainDist * 2.5f, $"boost {boostDist} should be ~3x plain {plainDist}");
    }
}
