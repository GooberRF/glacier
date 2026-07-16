using System.Collections.Generic;

namespace Ged.Core.Editor;

/// <summary>
/// Groups several commands into one undo entry. <see cref="Do"/> runs them in
/// order; <see cref="Undo"/> reverses them in the opposite order. Produced when a
/// transaction commits more than one command.
/// </summary>
public sealed class CompositeCommand : IDocumentCommand
{
    private readonly IReadOnlyList<IDocumentCommand> _commands;

    public CompositeCommand(string description, IReadOnlyList<IDocumentCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Description = description;
        _commands = commands;
    }

    public string Description { get; }

    public string? CoalesceKey => null;

    public void Do()
    {
        for (int i = 0; i < _commands.Count; i++)
        {
            _commands[i].Do();
        }
    }

    public void Undo()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo();
        }
    }
}
