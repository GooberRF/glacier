using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Input;

/// <summary>A gesture bound to two or more commands whose scopes can conflict.</summary>
public sealed class KeyConflict
{
    public KeyConflict(KeyGesture gesture, IReadOnlyList<string> commandIds)
    {
        Gesture = gesture;
        CommandIds = commandIds;
    }

    public KeyGesture Gesture { get; }

    public IReadOnlyList<string> CommandIds { get; }

    public override string ToString() => $"{Gesture} → {string.Join(", ", CommandIds)}";
}

/// <summary>
/// A layered key map: a preset base plus user overrides. An override with a value
/// rebinds a command; an override with a null value explicitly unbinds it. Effective
/// bindings, conflict detection and gesture→command matching all read the merged
/// view. Serialization persists only the preset name and the overrides.
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<string, KeyGesture> _base;
    private readonly Dictionary<string, KeyGesture?> _overrides = new(StringComparer.Ordinal);

    public Keymap(string presetName, Dictionary<string, KeyGesture> baseBindings)
    {
        PresetName = presetName;
        _base = baseBindings;
    }

    /// <summary>Raised whenever the effective bindings change.</summary>
    public event Action? Changed;

    public string PresetName { get; private set; }

    /// <summary>The user overrides, for persistence (value null = unbound).</summary>
    public IReadOnlyDictionary<string, KeyGesture?> Overrides => _overrides;

    public static Keymap FromPreset(string presetName) =>
        new(presetName, CommandCatalog.BuildPreset(presetName));

    /// <summary>Resolves the effective gesture for a command, or null if unbound.</summary>
    public KeyGesture? Resolve(string commandId)
    {
        if (_overrides.TryGetValue(commandId, out KeyGesture? overridden))
        {
            return overridden;
        }

        return _base.TryGetValue(commandId, out KeyGesture g) ? g : null;
    }

    /// <summary>Rebinds a command (null unbinds it).</summary>
    public void Rebind(string commandId, KeyGesture? gesture)
    {
        _overrides[commandId] = gesture;
        Changed?.Invoke();
    }

    /// <summary>Removes any override, reverting to the preset binding.</summary>
    public void ResetBinding(string commandId)
    {
        if (_overrides.Remove(commandId))
        {
            Changed?.Invoke();
        }
    }

    public bool IsOverridden(string commandId) => _overrides.ContainsKey(commandId);

    /// <summary>Reverts every override.</summary>
    public void ResetAll()
    {
        if (_overrides.Count == 0)
        {
            return;
        }

        _overrides.Clear();
        Changed?.Invoke();
    }

    /// <summary>Switches to another preset, discarding overrides.</summary>
    public void ApplyPreset(string presetName)
    {
        PresetName = presetName;
        _base.Clear();
        foreach (var kv in CommandCatalog.BuildPreset(presetName))
        {
            _base[kv.Key] = kv.Value;
        }

        _overrides.Clear();
        Changed?.Invoke();
    }

    /// <summary>The merged command→gesture map (unbound commands omitted).</summary>
    public IReadOnlyDictionary<string, KeyGesture> Effective()
    {
        var result = new Dictionary<string, KeyGesture>(StringComparer.Ordinal);
        foreach (var kv in _base)
        {
            result[kv.Key] = kv.Value;
        }

        foreach (var kv in _overrides)
        {
            if (kv.Value is KeyGesture g)
            {
                result[kv.Key] = g;
            }
            else
            {
                result.Remove(kv.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the command ids that a gesture triggers in the given active scope:
    /// a binding fires when its command's scope is Global or equals the active
    /// scope. Order is registration order.
    /// </summary>
    public IReadOnlyList<string> Match(KeyGesture gesture, CommandScope activeScope, CommandRegistry registry)
    {
        var matches = new List<string>();
        foreach (var kv in Effective())
        {
            if (!kv.Value.Equals(gesture))
            {
                continue;
            }

            CommandDefinition? def = registry.Find(kv.Key);
            if (def is null)
            {
                continue;
            }

            if (def.Scope == CommandScope.Global || def.Scope == activeScope ||
                def.SecondaryScope == activeScope || activeScope == CommandScope.Global)
            {
                matches.Add(kv.Key);
            }
        }

        // Preserve registry order for determinism.
        return registry.Commands
            .Select(c => c.Id)
            .Where(matches.Contains)
            .ToList();
    }

    /// <summary>Finds every gesture bound to more than one command with conflicting scopes.</summary>
    public IReadOnlyList<KeyConflict> FindConflicts(CommandRegistry registry)
    {
        var byGesture = new Dictionary<KeyGesture, List<string>>();
        foreach (var kv in Effective())
        {
            if (!byGesture.TryGetValue(kv.Value, out List<string>? list))
            {
                list = new List<string>();
                byGesture[kv.Value] = list;
            }

            list.Add(kv.Key);
        }

        var conflicts = new List<KeyConflict>();
        foreach (var (gesture, ids) in byGesture)
        {
            if (ids.Count < 2)
            {
                continue;
            }

            // Report the gesture as conflicting if any pair of its commands share a
            // conflicting scope (Global vs anything, or identical scopes).
            bool conflict = false;
            for (int i = 0; i < ids.Count && !conflict; i++)
            {
                for (int j = i + 1; j < ids.Count && !conflict; j++)
                {
                    CommandDefinition? a = registry.Find(ids[i]);
                    CommandDefinition? b = registry.Find(ids[j]);
                    if (a is not null && b is not null && CommandDefinition.ScopesConflict(a, b))
                    {
                        conflict = true;
                    }
                }
            }

            if (conflict)
            {
                conflicts.Add(new KeyConflict(gesture, ids));
            }
        }

        return conflicts;
    }
}
