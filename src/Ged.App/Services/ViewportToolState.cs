using System;

namespace Ged.App.Services;

/// <summary>The three mutually-exclusive viewport click tools.</summary>
public enum ViewportTool
{
    /// <summary>The default: clicks pick / marquee-select.</summary>
    Select,

    /// <summary>The interactive box-brush draw tool.</summary>
    Draw,

    /// <summary>The measure/ruler tool.</summary>
    Ruler,
}

/// <summary>
/// The exclusive viewport-tool selector: exactly one of Select / Draw Brush / Ruler is
/// active at a time. Activating a tool deactivates the others; re-activating the current
/// Draw/Ruler toggles it back off to Select (never leaving Draw/Ruler "sticky"), and there
/// is no way to end up with nothing active — Select is the floor. There is NO no-tool
/// state: Select is active whenever Draw/Ruler aren't, and it can never be deactivated —
/// requesting Select while it is already active is not a toggle-off, it re-asserts Select.
/// <see cref="Changed"/> fires with the tool so the host can arm/disarm the actual viewport
/// handlers and sync the toolbar button highlights.
/// </summary>
public sealed class ViewportToolState
{
    /// <summary>The currently active tool.</summary>
    public ViewportTool Active { get; private set; } = ViewportTool.Select;

    /// <summary>Raised (with the active tool) on a real transition, or when Select is
    /// re-asserted while already active (so the host can re-sync a toolbar toggle that a
    /// click may have visually unchecked).</summary>
    public event Action<ViewportTool>? Changed;

    /// <summary>
    /// Requests a tool. Re-requesting the active Draw/Ruler returns to Select (toggle off).
    /// Select is the floor and is never deactivated: requesting it while already active is a
    /// no-op transition that still re-asserts Select (fires <see cref="Changed"/>) so the UI
    /// keeps its Select button highlighted. Draw/Ruler fire <see cref="Changed"/> only on a
    /// real transition. Returns the resulting active tool.
    /// </summary>
    public ViewportTool Request(ViewportTool tool)
    {
        ViewportTool next = tool != ViewportTool.Select && Active == tool
            ? ViewportTool.Select // re-selecting an active Draw/Ruler toggles it off
            : tool;

        if (next != Active)
        {
            Active = next;
            Changed?.Invoke(Active);
        }
        else if (next == ViewportTool.Select)
        {
            // Re-assert the floor: no state change, but notify so the toolbar re-checks the
            // Select button that Avalonia's ToggleButton auto-unchecks on the click.
            Changed?.Invoke(Active);
        }

        return Active;
    }

    /// <summary>Returns to the default Select tool (e.g. a Draw/Ruler ESC or a mode switch).</summary>
    public ViewportTool Reset() => Request(ViewportTool.Select);
}
