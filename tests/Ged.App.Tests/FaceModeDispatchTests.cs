using Ged.App.Services;
using Ged.Core.Input;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// DISPATCH-level regression: drive Shift+S / Shift+D as key gestures through
/// the real <see cref="CommandDispatcher"/> with Face mode active (ActiveScope = Face) and
/// assert the RIGHT command is invoked — not by calling the command directly. This catches
/// scope-shadowing (e.g. the Object-scope Shift+S snap-to-camera) that the earlier
/// "the binding exists" model assertions could not.
/// </summary>
public sealed class FaceModeDispatchTests
{
    private static (CommandDispatcher Dispatcher, System.Collections.Generic.Dictionary<string, int> Calls) Build()
    {
        CommandRegistry registry = CommandCatalog.BuildRegistry();
        Keymap keymap = Keymap.FromPreset(CommandCatalog.RedClassic);
        var dispatcher = new CommandDispatcher(registry, keymap);
        var calls = new System.Collections.Generic.Dictionary<string, int>();

        void Track(string id) => dispatcher.Bind(id, () => calls[id] = calls.GetValueOrDefault(id) + 1);
        Track(CommandIds.SelectGrow);
        Track(CommandIds.SelectSameTexture);
        Track(CommandIds.ObjSnapToCamera); // shares Shift+S in Object scope
        Track(CommandIds.ModeEdge);
        return (dispatcher, calls);
    }

    [Fact]
    public void ShiftS_In_Face_Mode_Grows_The_Selection_Not_Snap_To_Camera()
    {
        (CommandDispatcher dispatcher, var calls) = Build();
        dispatcher.ActiveScope = CommandScope.Face;

        Assert.True(dispatcher.Dispatch(new KeyGesture("S", GestureModifiers.Shift)));

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectGrow));
        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.ObjSnapToCamera)); // Object scope not active
    }

    [Fact]
    public void ShiftD_In_Face_Mode_Selects_Same_Texture()
    {
        (CommandDispatcher dispatcher, var calls) = Build();
        dispatcher.ActiveScope = CommandScope.Face;

        Assert.True(dispatcher.Dispatch(new KeyGesture("D", GestureModifiers.Shift)));

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.SelectSameTexture));
    }

    [Fact]
    public void ShiftS_In_Object_Mode_Snaps_To_Camera_Not_Grow()
    {
        (CommandDispatcher dispatcher, var calls) = Build();
        dispatcher.ActiveScope = CommandScope.Object;

        Assert.True(dispatcher.Dispatch(new KeyGesture("S", GestureModifiers.Shift)));

        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.ObjSnapToCamera));
        Assert.Equal(0, calls.GetValueOrDefault(CommandIds.SelectGrow)); // Face scope not active
    }

    [Fact]
    public void ShiftE_Switches_To_Edge_Mode_In_Any_Brush_Scope()
    {
        (CommandDispatcher dispatcher, var calls) = Build();
        dispatcher.ActiveScope = CommandScope.Face; // Global mode command fires regardless

        Assert.True(dispatcher.Dispatch(new KeyGesture("E", GestureModifiers.Shift)));
        Assert.Equal(1, calls.GetValueOrDefault(CommandIds.ModeEdge));
    }
}
