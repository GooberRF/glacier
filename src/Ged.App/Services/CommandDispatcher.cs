using System;
using System.Collections.Generic;
using Ged.Core.Input;

namespace Ged.App.Services;

/// <summary>An executable command: the registry metadata plus its App-side action.</summary>
public sealed class AppCommand
{
    public required CommandDefinition Definition { get; init; }

    public required Action Execute { get; init; }

    public Func<bool> CanExecute { get; init; } = () => true;
}

/// <summary>
/// Binds command ids (from the shared <see cref="CommandRegistry"/>) to App-side
/// actions and resolves gestures through the active <see cref="Keymap"/>. Menus,
/// toolbars, the palette and both keyboard paths all execute through here, so a
/// command behaves identically however it is invoked. Unimplemented commands raise
/// a short "not available" toast so the RED Classic preset stays complete but
/// honest.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, AppCommand> _commands = new(StringComparer.Ordinal);

    public CommandDispatcher(CommandRegistry registry, Keymap keymap)
    {
        Registry = registry;
        Keymap = keymap;
    }

    /// <summary>Raised with a short user-facing message (status bar / toast).</summary>
    public event Action<string>? Message;

    public CommandRegistry Registry { get; }

    public Keymap Keymap { get; }

    /// <summary>The scope used to disambiguate mode-shared gestures (Global until an editing mode is active).</summary>
    public CommandScope ActiveScope { get; set; } = CommandScope.Global;

    public void Bind(string id, Action execute, Func<bool>? canExecute = null)
    {
        CommandDefinition def = Registry.Find(id)
            ?? throw new InvalidOperationException($"Unknown command id '{id}'.");
        _commands[id] = new AppCommand
        {
            Definition = def,
            Execute = execute,
            CanExecute = canExecute ?? (() => true),
        };
    }

    public bool Has(string id) => _commands.ContainsKey(id);

    public AppCommand? Find(string id) => _commands.GetValueOrDefault(id);

    /// <summary>The effective gesture bound to a command, or null.</summary>
    public KeyGesture? GestureFor(string id) => Keymap.Resolve(id);

    /// <summary>The gesture as a menu-friendly label ("Ctrl+Shift+P"), or empty.</summary>
    public string GestureLabel(string id) => Keymap.Resolve(id)?.Display ?? string.Empty;

    /// <summary>Runs a command by id. Unbound/unimplemented commands raise a toast instead.</summary>
    public bool Invoke(string id)
    {
        CommandDefinition? def = Registry.Find(id);
        if (def is null)
        {
            return false;
        }

        if (!def.Implemented)
        {
            Message?.Invoke($"“{def.DisplayName}” is not available.");
            return true;
        }

        if (_commands.TryGetValue(id, out AppCommand? cmd))
        {
            if (!cmd.CanExecute())
            {
                return false;
            }

            cmd.Execute();
            return true;
        }

        Message?.Invoke($"“{def.DisplayName}” is not available.");
        return true;
    }

    /// <summary>
    /// Dispatches a gesture: finds the matching command(s) for the active scope and
    /// invokes the first. Returns true when a command handled it.
    /// </summary>
    public bool Dispatch(KeyGesture gesture)
    {
        IReadOnlyList<string> matches = Keymap.Match(gesture, ActiveScope, Registry);
        foreach (string id in matches)
        {
            CommandDefinition? def = Registry.Find(id);
            if (def is null)
            {
                continue;
            }

            // Prefer an implemented, wired command; otherwise let the first match
            // report its coming-later toast.
            if (def.Implemented && _commands.ContainsKey(id))
            {
                return Invoke(id);
            }
        }

        if (matches.Count > 0)
        {
            return Invoke(matches[0]);
        }

        // Nothing is bound to this gesture in the ACTIVE scope. If it IS bound to a
        // mode-scoped command in a different mode, say which mode it needs rather than
        // no-op silently — a silent scoped hotkey is exactly what reads as "broken"
        // (e.g. Shift+S / Shift+D are Face-mode grow / select-same-texture).
        HintWrongScope(gesture);
        return false;
    }

    public void ShowMessage(string message) => Message?.Invoke(message);

    /// <summary>The editing-mode scopes a hotkey can be gated to (Global/Viewport are not "modes").</summary>
    private static readonly CommandScope[] ModeScopes =
    {
        CommandScope.Brush, CommandScope.Face, CommandScope.Edge,
        CommandScope.Vertex, CommandScope.Object, CommandScope.Group,
    };

    /// <summary>
    /// Emits a transient hint when <paramref name="gesture"/> is bound only in mode(s) other
    /// than the active one, so a scope-gated hotkey pressed in the wrong mode explains itself
    /// instead of doing nothing. No-op when the gesture is unbound or bound only in non-mode
    /// scopes (Global commands always match; Viewport/camera keys are consumed upstream).
    /// </summary>
    private void HintWrongScope(KeyGesture gesture)
    {
        if (Message is null)
        {
            return;
        }

        // Passing Global as the active scope makes Match return EVERY command bound to the
        // gesture regardless of scope (Global matches anything), so we can see the modes it
        // would fire in.
        var modes = new List<CommandScope>();
        foreach (string id in Keymap.Match(gesture, CommandScope.Global, Registry))
        {
            if (Registry.Find(id) is not { } def)
            {
                continue;
            }

            foreach (CommandScope s in def.ActiveScopes())
            {
                if (Array.IndexOf(ModeScopes, s) >= 0 && !modes.Contains(s))
                {
                    modes.Add(s);
                }
            }
        }

        if (modes.Count == 0)
        {
            return;
        }

        // Deterministic order (Brush, Face, Edge, Vertex, Object, Group).
        modes.Sort((a, b) => Array.IndexOf(ModeScopes, a).CompareTo(Array.IndexOf(ModeScopes, b)));
        Message.Invoke($"{gesture.Display}: requires {JoinModes(modes)} mode");
    }

    /// <summary>"Face", "Face or Object", "Brush, Face or Edge".</summary>
    private static string JoinModes(IReadOnlyList<CommandScope> modes)
    {
        if (modes.Count == 1)
        {
            return modes[0].ToString();
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < modes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(i == modes.Count - 1 ? " or " : ", ");
            }

            sb.Append(modes[i]);
        }

        return sb.ToString();
    }
}
