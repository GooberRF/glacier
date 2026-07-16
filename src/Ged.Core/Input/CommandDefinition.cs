using System.Collections.Generic;

namespace Ged.Core.Input;

/// <summary>
/// The context in which a gesture is active. RED reuses the same key across
/// editing modes, so two mode-scoped bindings on one gesture do not conflict, but
/// a global binding conflicts with anything sharing its gesture.
/// </summary>
public enum CommandScope
{
    Global,
    Brush,
    Face,
    Edge,
    Vertex,
    Object,
    Group,
    Viewport,
}

/// <summary>
/// Metadata for one editor command. Execution lives in the App (it needs UI), but
/// the id, label, category and scope live here so the registry, keymap, palette
/// and conflict detection are all testable without a window. Commands without a
/// live implementation are still registered with <see cref="Implemented"/> false
/// so the presets stay complete.
/// </summary>
public sealed class CommandDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Category { get; init; }

    public CommandScope Scope { get; init; } = CommandScope.Global;

    /// <summary>
    /// An additional scope the command is also active in (null = none). Lets a single
    /// command fire in two modes without duplicating it.
    /// </summary>
    public CommandScope? SecondaryScope { get; init; }

    /// <summary>False when the command is registered for preset completeness but has no implementation.</summary>
    public bool Implemented { get; init; } = true;

    /// <summary>
    /// True for continuous camera-movement commands driven by the held-key scheme poller
    /// (<see cref="Ged.Core.Input"/> consumers) rather than the command dispatcher. These
    /// are listed in Settings ▸ Input and the hotkey reference for visibility, but excluded
    /// from the command palette: one-shot invocation cannot reproduce a held-key movement,
    /// so offering them there would only ever raise a "not available" toast.
    /// </summary>
    public bool HeldKey { get; init; }

    /// <summary>Every scope this command is active in (its primary plus any secondary scope).</summary>
    public IEnumerable<CommandScope> ActiveScopes()
    {
        yield return Scope;
        if (SecondaryScope is CommandScope s && s != Scope)
        {
            yield return s;
        }
    }

    /// <summary>Two scopes conflict when they can be active simultaneously.</summary>
    public static bool ScopesConflict(CommandScope a, CommandScope b) =>
        a == b || a == CommandScope.Global || b == CommandScope.Global;

    /// <summary>
    /// Two commands conflict when they can fire together: either is Global (always
    /// active), or their active scope sets (primary + secondary) overlap.
    /// </summary>
    public static bool ScopesConflict(CommandDefinition a, CommandDefinition b)
    {
        if (a.Scope == CommandScope.Global || b.Scope == CommandScope.Global)
        {
            return true;
        }

        foreach (CommandScope sa in a.ActiveScopes())
        {
            foreach (CommandScope sb in b.ActiveScopes())
            {
                if (sa == sb)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
