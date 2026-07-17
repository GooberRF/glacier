using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ged.App.Services;
using Ged.Core.Editor;

namespace Ged.App;

/// <summary>The kind of asset a placeable drag carries.</summary>
internal enum PlaceableKind
{
    Mesh,
    Prefab,
    Class,
}

/// <summary>
/// A minimal press → movement-threshold drag recognizer, extracted so the gesture logic is unit
/// testable without a live pointer. <see cref="Move"/> returns true exactly once, when a move first
/// crosses the threshold, and then disarms so a single press starts at most one drag.
/// </summary>
internal sealed class DragGesture
{
    /// <summary>Manhattan-distance threshold (px) a press must travel before a drag starts.</summary>
    public const double DefaultThreshold = 5.0;

    private readonly double _threshold;
    private Point _start;

    public DragGesture(double threshold = DefaultThreshold) => _threshold = threshold;

    /// <summary>True while a press is held and no drag has started yet.</summary>
    public bool Armed { get; private set; }

    public void Press(Point at)
    {
        _start = at;
        Armed = true;
    }

    public void Release() => Armed = false;

    /// <summary>True once when the pointer first crosses the threshold (disarms); false otherwise.</summary>
    public bool Move(Point at)
    {
        if (!Armed)
        {
            return false;
        }

        if (Math.Abs(at.X - _start.X) + Math.Abs(at.Y - _start.Y) < _threshold)
        {
            return false;
        }

        Armed = false;
        return true;
    }
}

/// <summary>
/// The in-app drag payload for dragging a placeable asset (a mesh, a prefab, or a catalog class)
/// out of the Asset Browser / Palette and dropping it into a viewport pane (item E). The descriptor
/// is a single delimited string carried on a custom clipboard <see cref="Format"/>, so it never
/// collides with the file-drop path (.rfl / .rfg from Explorer). The window-level drop handler
/// parses it and places the asset at the drop point.
/// </summary>
internal static class PlaceableDrag
{
    /// <summary>The custom data-object format key for an in-app placeable drag (not a file drop).</summary>
    public const string Format = "application/x-ged-placeable";

    private const char Sep = (char)0x1f;

    /// <summary>A mesh object of <paramref name="meshName"/> (Asset Browser Meshes tile).</summary>
    public static string Mesh(string meshName) => $"mesh{Sep}{meshName}";

    /// <summary>A tracked prefab instance from <paramref name="path"/> (Asset Browser Prefabs tile).</summary>
    public static string Prefab(string path) => $"prefab{Sep}{path}";

    /// <summary>A catalog class / object kind (Palette): clutter, item, entity, or a kind with no class.</summary>
    public static string Class(LevelObjectKind kind, string? className) => $"class{Sep}{kind}{Sep}{className}";

    /// <summary>Splits a descriptor into its fields (kind tag first).</summary>
    public static string[] Split(string descriptor) => descriptor.Split(Sep);

    /// <summary>A human-readable one-liner for the DragDrop diagnostics log.</summary>
    public static string Describe(string descriptor) => string.Join(" ", Split(descriptor));

    /// <summary>
    /// Parses a descriptor into its kind + arguments (pure; unit tested). Returns false for an
    /// unrecognized / malformed descriptor.
    /// </summary>
    public static bool TryParse(string descriptor, out PlaceableKind kind, out string arg1, out string? arg2)
    {
        kind = default;
        arg1 = string.Empty;
        arg2 = null;
        string[] p = Split(descriptor);
        switch (p.Length > 0 ? p[0] : string.Empty)
        {
            case "mesh" when p.Length >= 2:
                kind = PlaceableKind.Mesh;
                arg1 = p[1];
                return true;
            case "prefab" when p.Length >= 2:
                kind = PlaceableKind.Prefab;
                arg1 = p[1];
                return true;
            case "class" when p.Length >= 3:
                kind = PlaceableKind.Class;
                arg1 = p[1];
                arg2 = string.IsNullOrEmpty(p[2]) ? null : p[2];
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Makes <paramref name="source"/> a drag source. The press/move handlers are TUNNEL-routed so a
    /// child (the tile's thumbnail/label) or the Button's own click handler cannot swallow the press:
    /// a left-press arms the gesture (and fires <paramref name="onPress"/>, e.g. to cancel the hover
    /// preview), and once the pointer crosses the threshold an OLE copy-drag starts with the descriptor
    /// from <paramref name="makeDescriptor"/> (null aborts). A plain click / double-click still reaches
    /// the control (the threshold is never met), and ESC during the drag cancels it — both handled by
    /// the OS drag loop. <paramref name="onLog"/> receives a stage line when a drag initiates.
    /// </summary>
    public static void WireSource(Control source, Func<string?> makeDescriptor, Action? onPress = null, Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(makeDescriptor);
        var gesture = new DragGesture();

        source.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                gesture.Press(e.GetPosition(source));
                onPress?.Invoke();
            },
            RoutingStrategies.Tunnel);

        source.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, _) => gesture.Release(),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        source.AddHandler(
            InputElement.PointerMovedEvent,
            async (_, e) =>
            {
                if (!gesture.Armed)
                {
                    return;
                }

                if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
                {
                    gesture.Release();
                    return;
                }

                if (!gesture.Move(e.GetPosition(source)) || makeDescriptor() is not { } descriptor)
                {
                    return;
                }

                onLog?.Invoke($"drag initiated: {Describe(descriptor)}");
                var data = new DataObject();
                data.Set(Format, descriptor);
                try
                {
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
                }
                catch (Exception ex)
                {
                    CrashHandler.LogNonFatal("placeable-drag", ex); // no drag surface (headless) — ignore
                }
            },
            RoutingStrategies.Tunnel);
    }
}
