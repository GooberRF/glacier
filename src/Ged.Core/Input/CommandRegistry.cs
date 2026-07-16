using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Input;

/// <summary>
/// The central registry of command definitions. Every menu item, toolbar button,
/// palette entry and hotkey resolves through it. Execution is attached by the App
/// keyed by command id; the registry itself is pure metadata and fully testable.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _byId = new(StringComparer.Ordinal);
    private readonly List<CommandDefinition> _ordered = new();

    public IReadOnlyList<CommandDefinition> Commands => _ordered;

    public void Register(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_byId.ContainsKey(definition.Id))
        {
            throw new InvalidOperationException($"Command '{definition.Id}' is already registered.");
        }

        _byId.Add(definition.Id, definition);
        _ordered.Add(definition);
    }

    public CommandDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public bool Contains(string id) => _byId.ContainsKey(id);

    public IEnumerable<string> Categories => _ordered.Select(c => c.Category).Distinct();

    public IEnumerable<CommandDefinition> InCategory(string category) =>
        _ordered.Where(c => string.Equals(c.Category, category, StringComparison.Ordinal));
}
