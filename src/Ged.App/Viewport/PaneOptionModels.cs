using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.App.Viewport;

/// <summary>
/// Which viewport types a render option is offered on. Options meaningless in an orthographic
/// pane (fog, sky, room culling, portal-face draw) are <see cref="PerspectiveOnly"/> and appear
/// only in a perspective pane's dropdown (item 4).
/// </summary>
public enum ViewScope
{
    /// <summary>Shown in every pane's dropdown (ortho and perspective).</summary>
    AllViews,

    /// <summary>Shown only in perspective panes.</summary>
    PerspectiveOnly,
}

/// <summary>
/// One global render-option bool exposed as a checkbox in each pane's render-mode dropdown.
/// The get/set delegates wrap the shared settings/session state plus its side effects (scene
/// rebuild, persist), so toggling from any pane flips the same global value.
/// </summary>
public sealed class RenderOptionToggle
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

    public RenderOptionToggle(string label, Func<bool> get, Action<bool> set, ViewScope scope = ViewScope.AllViews)
    {
        Label = label;
        _get = get;
        _set = set;
        Scope = scope;
    }

    public string Label { get; }

    /// <summary>The pane view types this toggle appears on.</summary>
    public ViewScope Scope { get; }

    public bool Value => _get();

    internal void Apply(bool value) => _set(value);
}

/// <summary>One selectable option in a <see cref="RenderOptionRadioGroup"/> (a mode choice).</summary>
public sealed class RenderOptionRadioOption
{
    private readonly Action _select;
    private readonly Func<bool> _isChecked;

    public RenderOptionRadioOption(string label, Action select, Func<bool> isChecked)
    {
        Label = label;
        _select = select;
        _isChecked = isChecked;
    }

    public string Label { get; }

    public bool IsChecked => _isChecked();

    internal void Select() => _select();
}

/// <summary>
/// A global three-way (radio) render option exposed in each pane's dropdown — the Room Rendering
/// and Portal Faces choosers relocated from the View menu (item 4). Both are perspective-only.
/// </summary>
public sealed class RenderOptionRadioGroup
{
    public RenderOptionRadioGroup(string label, ViewScope scope, IReadOnlyList<RenderOptionRadioOption> options)
    {
        Label = label;
        Scope = scope;
        Options = options;
    }

    public string Label { get; }

    public ViewScope Scope { get; }

    public IReadOnlyList<RenderOptionRadioOption> Options { get; }
}

/// <summary>
/// The shared (global) set of render options shown in each pane's render-mode dropdown. Since
/// item 4 this includes the toggles and radio groups relocated OUT of the top View menu:
/// Show Links / Show Path Node Connections / Show Gizmo / Show Annotations / Show Event Arrows
/// (all view types), and Show Fog / Draw Sky / Room Rendering / Portal Faces (perspective panes
/// only). All state stays
/// global — toggling from any pane flips the same shared value; the scope only decides WHERE the
/// control is offered.
/// </summary>
public sealed class RenderOptionsModel
{
    private readonly List<RenderOptionToggle> _toggles;
    private readonly List<RenderOptionRadioGroup> _radioGroups;

    public RenderOptionsModel(IEnumerable<RenderOptionToggle> toggles, IEnumerable<RenderOptionRadioGroup>? radioGroups = null)
    {
        _toggles = toggles.ToList();
        _radioGroups = radioGroups?.ToList() ?? new List<RenderOptionRadioGroup>();
    }

    public IReadOnlyList<RenderOptionToggle> Toggles => _toggles;

    public IReadOnlyList<RenderOptionRadioGroup> RadioGroups => _radioGroups;

    /// <summary>The toggles a pane of the given view type shows (perspective-only ones hidden in ortho).</summary>
    public IEnumerable<RenderOptionToggle> VisibleToggles(ViewType viewType) =>
        _toggles.Where(t => t.Scope == ViewScope.AllViews || viewType == ViewType.Perspective);

    /// <summary>The radio groups a pane of the given view type shows (all are perspective-only today).</summary>
    public IEnumerable<RenderOptionRadioGroup> VisibleRadioGroups(ViewType viewType) =>
        _radioGroups.Where(g => g.Scope == ViewScope.AllViews || viewType == ViewType.Perspective);

    /// <summary>Raised whenever any option's value changes (from any pane / command / settings).</summary>
    public event Action? Changed;

    public void SetValue(RenderOptionToggle toggle, bool value)
    {
        if (toggle.Value == value)
        {
            return;
        }

        toggle.Apply(value);
        Changed?.Invoke();
    }

    /// <summary>Selects a radio option (idempotent) and notifies every pane.</summary>
    public void SelectRadio(RenderOptionRadioOption option)
    {
        if (option.IsChecked)
        {
            return;
        }

        option.Select();
        Changed?.Invoke();
    }

    /// <summary>Signals a value changed through another path (command hotkey, Settings dialog).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Builds the global render-option set from the app's shared state. Toggles carried over from
    /// earlier: Bounding Boxes, Draw unmerged brushwork, All Ranges, Animate Emitters, Disable
    /// Backface Culling. Relocated from the View menu (item 4): Show Links, Show Path Node
    /// Connections, Show Gizmo, Show Annotations (all views); plus Show Event Arrows (all views);
    /// Show Fog, Draw Sky, Draw Decals, Room Rendering, Portal Faces (perspective only).
    /// </summary>
    public static RenderOptionsModel BuildGlobal(
        AppSettings settings,
        EditorSession session,
        Action rebuildScene,
        Action persist,
        Action applyBackfaceCulling,
        Action<bool> setEmitterAnimation,
        Action ensureMergedBrushStash,
        Func<bool> gizmoVisible,
        Action toggleGizmo,
        Action applyFog,
        Func<RoomVisibility> getRoomMode,
        Action<RoomVisibility> setRoomMode,
        Func<Ged.Rendering.Scene.PortalFaceDrawMode> getPortalFaces,
        Action<Ged.Rendering.Scene.PortalFaceDrawMode> setPortalFaces)
    {
        var toggles = new[]
        {
            new RenderOptionToggle(
                "Show objects as Bounding Boxes",
                () => settings.ShowBoundingBoxes,
                v => { settings.ShowBoundingBoxes = v; session.ShowBoundingBoxes = v; rebuildScene(); persist(); }),
            new RenderOptionToggle(
                "Draw unmerged brushwork",
                () => settings.DrawUnmergedBrushwork,
                v =>
                {
                    settings.DrawUnmergedBrushwork = v;
                    session.DrawUnmergedBrushwork = v;
                    // OFF = show merged geometry, which needs a build stash. If none exists
                    // yet, kick a build so the toggle takes effect without an edit nudge.
                    if (!v)
                    {
                        ensureMergedBrushStash();
                    }

                    rebuildScene();
                    persist();
                }),
            new RenderOptionToggle(
                "Show All Ranges",
                () => settings.ShowAllRanges,
                v => { settings.ShowAllRanges = v; session.ShowAllRanges = v; rebuildScene(); persist(); }),
            new RenderOptionToggle(
                "Animate Emitters",
                () => settings.AnimateEmitters,
                v => { settings.AnimateEmitters = v; session.AnimateEmitters = v; setEmitterAnimation(v); rebuildScene(); persist(); }),
            new RenderOptionToggle(
                "Disable Backface Culling",
                () => settings.DisableBackfaceCulling,
                v => { settings.DisableBackfaceCulling = v; applyBackfaceCulling(); persist(); }),

            // ---- Relocated from the View menu (item 4), all view types ----
            new RenderOptionToggle(
                "Show Links",
                () => settings.ShowLinks,
                v => { settings.ShowLinks = v; session.ShowLinks = v; rebuildScene(); persist(); }),
            new RenderOptionToggle(
                "Show Path Node Connections",
                () => settings.ShowPathNodeConnections,
                v => { settings.ShowPathNodeConnections = v; session.ShowPathNodes = v; rebuildScene(); persist(); }),
            new RenderOptionToggle(
                "Show Gizmo",
                gizmoVisible,
                _ => toggleGizmo()), // routes through the GizmoNone command (state + persist inside)
            new RenderOptionToggle(
                "Show Annotations",
                () => settings.ShowAnnotations,
                v => { settings.ShowAnnotations = v; session.ShowAnnotations = v; rebuildScene(); persist(); }),
            new RenderOptionToggle(
                "Show Event Arrows",
                () => settings.ShowEventArrows,
                v => { settings.ShowEventArrows = v; session.ShowEventArrows = v; rebuildScene(); persist(); }),

            // ---- Relocated from the View menu (item 4), PERSPECTIVE panes only ----
            new RenderOptionToggle(
                "Draw Sky (Like in-game)",
                () => settings.DrawSky,
                v => { settings.DrawSky = v; session.DrawSky = v; rebuildScene(); persist(); },
                ViewScope.PerspectiveOnly),
            new RenderOptionToggle(
                "Draw Decals",
                () => settings.DrawDecals,
                v => { settings.DrawDecals = v; session.DrawDecals = v; rebuildScene(); persist(); },
                ViewScope.PerspectiveOnly),
            new RenderOptionToggle(
                "Show Fog",
                () => settings.ShowFog,
                v => { settings.ShowFog = v; applyFog(); persist(); },
                ViewScope.PerspectiveOnly),
        };

        static RenderOptionRadioOption Option<T>(string label, T value, Func<T> get, Action<T> set) =>
            new(label, () => set(value), () => EqualityComparer<T>.Default.Equals(get(), value));

        var radioGroups = new[]
        {
            new RenderOptionRadioGroup("Room Rendering", ViewScope.PerspectiveOnly, new[]
            {
                Option("Render Everything", RoomVisibility.All, getRoomMode, setRoomMode),
                Option("Render Using Portals", RoomVisibility.Portals, getRoomMode, setRoomMode),
                Option("Render Current Room Only", RoomVisibility.CurrentRoom, getRoomMode, setRoomMode),
            }),
            new RenderOptionRadioGroup("Portal Faces", ViewScope.PerspectiveOnly, new[]
            {
                Option("Don't Draw Portal Faces", Ged.Rendering.Scene.PortalFaceDrawMode.None, getPortalFaces, setPortalFaces),
                Option("Draw See-thru Portal Faces", Ged.Rendering.Scene.PortalFaceDrawMode.SeeThru, getPortalFaces, setPortalFaces),
                Option("Draw Non-see-thru Portal Faces", Ged.Rendering.Scene.PortalFaceDrawMode.Opaque, getPortalFaces, setPortalFaces),
            }),
        };

        return new RenderOptionsModel(toggles, radioGroups);
    }
}

/// <summary>Free-entry parser signature for <see cref="IncrementSetting"/> (validates + clamps).</summary>
public delegate bool IncrementParser(string? text, out float value);

/// <summary>
/// A shared increment setting (grid size or rotation step) with its preset ladder,
/// backing get/set (including side effects) and a change notification consumed by the
/// status-bar popovers and the per-pane toolbar pickers (item 4).
/// </summary>
public sealed class IncrementSetting
{
    private readonly Func<float> _get;
    private readonly Action<float> _apply;
    private readonly IncrementParser _parser;

    public IncrementSetting(
        string label, string unit, IReadOnlyList<float> presets,
        Func<float> get, Action<float> apply, IncrementParser parser,
        IReadOnlyList<float>? hotkeyLadder = null)
    {
        Label = label;
        Unit = unit;
        Presets = presets;
        HotkeyLadder = hotkeyLadder ?? presets;
        _get = get;
        _apply = apply;
        _parser = parser;
    }

    public string Label { get; }

    /// <summary>Display unit suffix (" m" or "°").</summary>
    public string Unit { get; }

    /// <summary>Quick-select presets shown by the pickers/popovers.</summary>
    public IReadOnlyList<float> Presets { get; }

    /// <summary>
    /// The ladder the [ / ] style hotkeys step through — a superset of
    /// <see cref="Presets"/> for the grid (up to 256 m), identical for rotation.
    /// </summary>
    public IReadOnlyList<float> HotkeyLadder { get; }

    public float Value => _get();

    public event Action? Changed;

    public string Format(float value) => $"{value:0.#####}{Unit}";

    public void SetValue(float value)
    {
        if (MathF.Abs(Value - value) < 1e-6f)
        {
            return;
        }

        _apply(value);
        Changed?.Invoke();
    }

    /// <summary>Applies a validated free-entry value; false (and no change) when invalid.</summary>
    public bool TrySetFromText(string? text)
    {
        if (!_parser(text, out float value))
        {
            return false;
        }

        SetValue(value);
        return true;
    }

    /// <summary>Signals a value changed through another path (Settings dialog edits).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Steps to the next ladder value above the current one (hotkey stepping).</summary>
    public void StepUp() => SetValue(Ged.Core.Editing.SnapIncrements.StepUp(HotkeyLadder, Value));

    /// <summary>Steps to the next ladder value below the current one (hotkey stepping).</summary>
    public void StepDown() => SetValue(Ged.Core.Editing.SnapIncrements.StepDown(HotkeyLadder, Value));
}
