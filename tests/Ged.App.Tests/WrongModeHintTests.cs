using System.Collections.Generic;
using Ged.App.Services;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// A scope-gated hotkey pressed in the wrong mode used to no-op silently — which is exactly what
/// made Shift+S / Shift+D feel "broken" (they are Face-mode grow / select-same-texture). The
/// dispatcher now emits a transient status hint naming the mode(s) the gesture needs, for every
/// mode-scoped hotkey, without firing anything or changing the false return.
/// </summary>
public sealed class WrongModeHintTests
{
    private static (CommandDispatcher Dispatcher, List<string> Messages, Dictionary<string, int> Calls) Build(CommandScope scope)
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap keymap = Keymap.FromPreset(CommandCatalog.RedClassic);
        var dispatcher = new CommandDispatcher(registry, keymap) { ActiveScope = scope };
        var messages = new List<string>();
        dispatcher.Message += messages.Add;
        var calls = new Dictionary<string, int>();
        void Track(string id) => dispatcher.Bind(id, () => calls[id] = calls.GetValueOrDefault(id) + 1);
        Track(CommandIds.SelectGrow);
        Track(CommandIds.SelectSameTexture);
        Track(CommandIds.ObjSnapToCamera);
        return (dispatcher, messages, calls);
    }

    [Fact]
    public void ShiftS_In_Brush_Mode_Hints_Its_Modes_And_Fires_Nothing()
    {
        // Shift+S is bound to Grow Selection (Face) and Snap To Camera (Object).
        (CommandDispatcher d, var messages, var calls) = Build(CommandScope.Brush);

        Assert.False(d.Dispatch(new KeyGesture("S", GestureModifiers.Shift)));

        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.SelectGrow));
        Assert.Equal("Shift+S: requires Face or Object mode", Assert.Single(messages));
    }

    [Fact]
    public void ShiftD_In_Object_Mode_Hints_Face_Mode()
    {
        // Shift+D is bound only to Select Same Texture (Face).
        (CommandDispatcher d, var messages, _) = Build(CommandScope.Object);

        Assert.False(d.Dispatch(new KeyGesture("D", GestureModifiers.Shift)));

        Assert.Equal("Shift+D: requires Face mode", Assert.Single(messages));
    }

    [Fact]
    public void ShiftS_In_Face_Mode_Fires_And_Does_Not_Hint()
    {
        (CommandDispatcher d, var messages, var calls) = Build(CommandScope.Face);

        Assert.True(d.Dispatch(new KeyGesture("S", GestureModifiers.Shift)));

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectGrow));
        Assert.Empty(messages); // it fired — no hint
    }

    [Fact]
    public void ShiftS_In_Object_Mode_Fires_Snap_And_Does_Not_Hint()
    {
        // Object mode is a valid scope for Shift+S (Snap To Camera), so it fires with no hint.
        (CommandDispatcher d, var messages, var calls) = Build(CommandScope.Object);

        Assert.True(d.Dispatch(new KeyGesture("S", GestureModifiers.Shift)));

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.ObjSnapToCamera));
        Assert.Empty(messages);
    }

    [Fact]
    public void An_Unbound_Gesture_Does_Not_Hint()
    {
        (CommandDispatcher d, var messages, _) = Build(CommandScope.Face);

        Assert.False(d.Dispatch(new KeyGesture("F19")));

        Assert.Empty(messages);
    }
}
