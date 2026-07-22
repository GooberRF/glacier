using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ged.App.Camera;
using Ged.App.Services;
using Ged.App.Viewport;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// REAL-PATH regression for the composited OpenGL viewport pane (the cross-platform surface),
/// the counterpart to <see cref="ViewportKeyRoutingTests"/> which covers the Direct3D 11 native
/// pane. Directly answers the owner question "do Shift+S / Shift+D work in perspective with faces
/// selected?": they are Face-scoped, so with Face mode active they must reach the shared
/// <see cref="CommandDispatcher"/> and fire — on this surface too — and in the wrong mode they
/// must NOT fire (by design). Both the raw router entry point and the Avalonia key path
/// (OnKeyDown → RouteKey → virtual-key translation) are exercised.
/// </summary>
public sealed class GlViewportKeyRoutingTests
{
    private const int VkShift = 0x10;
    private const int VkS = 0x53;
    private const int VkD = 0x44;

    private static (GlViewportSurface Surface, IViewportInput Input, Dictionary<string, int> Calls)
        Build(CommandScope scope)
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap keymap = Keymap.FromPreset(CommandCatalog.RedClassic);
        var dispatcher = new CommandDispatcher(registry, keymap) { ActiveScope = scope };
        var calls = new Dictionary<string, int>();
        void Track(string id) => dispatcher.Bind(id, () => calls[id] = calls.GetValueOrDefault(id) + 1);
        Track(CommandIds.SelectGrow);
        Track(CommandIds.SelectSameTexture);
        Track(CommandIds.ObjSnapToCamera);

        var surface = new GlViewportSurface(dispatcher, CameraSchemeKind.RedClassic, ViewType.Perspective);
        return (surface, surface, calls);
    }

    [AvaloniaFact]
    public void ShiftS_In_Face_Mode_Grows_Via_The_Gl_Surface()
    {
        (_, IViewportInput input, var calls) = Build(CommandScope.Face);

        input.OnKey(VkShift, down: true, extended: false);
        input.OnKey(VkS, down: true, extended: false);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectGrow));
        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.ObjSnapToCamera));
    }

    [AvaloniaFact]
    public void ShiftD_In_Face_Mode_Selects_Same_Texture_Via_The_Gl_Surface()
    {
        (_, IViewportInput input, var calls) = Build(CommandScope.Face);

        input.OnKey(VkShift, down: true, extended: false);
        input.OnKey(VkD, down: true, extended: false);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectSameTexture));
    }

    [AvaloniaFact]
    public void ShiftS_In_Face_Mode_Fires_Through_The_Real_Avalonia_Key_Path()
    {
        // Drives OnKeyDown → SyncModifiers → RouteKey → AvaloniaKeyToVirtualKey → the shared
        // router, not the raw OnKey entry point — the exact path a focused GL pane takes for a
        // physical key press. Real Avalonia carries the live modifier state (KeyModifiers.Shift)
        // on every event while Shift is held, which is what OnKeyDown now re-syncs from.
        (GlViewportSurface surface, _, var calls) = Build(CommandScope.Face);

        RaiseKeyDown(surface, Key.LeftShift, KeyModifiers.Shift);
        RaiseKeyDown(surface, Key.S, KeyModifiers.Shift);

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectGrow));
    }

    [AvaloniaFact]
    public void ShiftD_In_Object_Mode_Does_Not_Grow_Or_Select_Same_Texture()
    {
        // By design: Shift+D is Face-scoped, so in Object mode it fires nothing (the confusing
        // silent no-op the wrong-mode status hint now explains).
        (_, IViewportInput input, var calls) = Build(CommandScope.Object);

        input.OnKey(VkShift, down: true, extended: false);
        input.OnKey(VkD, down: true, extended: false);

        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.SelectSameTexture));
        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.SelectGrow));
    }

    [Fact]
    public void Avalonia_Key_Translation_Matches_The_Native_Virtual_Keys()
    {
        // The GL path relies on this translation producing the same VK codes the D3D11 WndProc
        // proxies, so the shared router treats both surfaces identically.
        Assert.Equal(VkShift, GestureConvert.AvaloniaKeyToVirtualKey(Key.LeftShift));
        Assert.Equal(VkShift, GestureConvert.AvaloniaKeyToVirtualKey(Key.RightShift));
        Assert.Equal(VkS, GestureConvert.AvaloniaKeyToVirtualKey(Key.S));
        Assert.Equal(VkD, GestureConvert.AvaloniaKeyToVirtualKey(Key.D));
    }

    private static void RaiseKeyDown(Control c, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        c.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = c });
}
